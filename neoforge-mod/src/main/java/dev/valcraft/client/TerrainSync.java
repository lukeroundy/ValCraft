package dev.valcraft.client;

import dev.valcraft.ValCraft;
import dev.valcraft.link.ValCraftLink;
import it.unimi.dsi.fastutil.longs.Long2IntOpenHashMap;
import it.unimi.dsi.fastutil.longs.LongOpenHashSet;
import net.minecraft.client.Minecraft;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import net.neoforged.api.distmarker.Dist;
import net.neoforged.bus.api.SubscribeEvent;
import net.neoforged.fml.common.EventBusSubscriber;
import net.neoforged.neoforge.event.level.BlockEvent;
import net.neoforged.neoforge.event.tick.ServerTickEvent;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.concurrent.ConcurrentLinkedQueue;

/**
 * Phase A: Valheim terrain as real MC blocks in the (void) ValCraft world. Valheim streams 8x8-column tiles
 * (top Y + material); the integrated server writes them (surface + 3 sub-surface + stone down to DEPTH), only for
 * columns that changed, a bounded number of tiles per server tick. The blocks are never drawn (MC's world render is
 * skipped) — they're for collision, step-up and mining. Phase B hook: block break/place events go back to Valheim.
 */
@EventBusSubscriber(modid = ValCraft.MOD_ID, value = Dist.CLIENT)
public final class TerrainSync {
	private TerrainSync() {}
	static final int DEPTH = 16, TILES_PER_TICK = 24, STALE_BAND = 64;

	private static final Long2IntOpenHashMap colTop = new Long2IntOpenHashMap(); // server thread: column -> mirrored terrain top (kept across MC edits)
	static int terrainTop(int x, int z) { long k = key(x, z); return colTop.containsKey(k) ? colTop.get(k) : Integer.MIN_VALUE; }

	/** Mirrored-terrain block types (0.10.0: stone and farmland are player blocks; deep layers are deepslate, rock is andesite). */
	static boolean isTerrainBlock(BlockState s) {
		if (s.is(Blocks.STONE) || s.is(Blocks.FARMLAND)) return false;
		for (byte m = 1; m <= 10; m++) if (s == surface(m) || s == sub(m)) return true;
		return s.is(Blocks.DEEPSLATE);
	}
	/** Old saves: stone below the mirrored surface is leftover terrain, kept hidden until the column is rewritten. */
	static boolean isLegacyTerrain(BlockState s, int y, int top) { return s.is(Blocks.STONE) && top != Integer.MIN_VALUE && y < top; }
	private record Tile(int tx, int tz, short[] top, byte[] mat, byte[] bio) {}
	private static final ConcurrentLinkedQueue<Tile> pending = new ConcurrentLinkedQueue<>();
	private static final Long2IntOpenHashMap applied = new Long2IntOpenHashMap(); // server thread: column -> top<<8|mat
	private static final LongOpenHashSet clientTiles = new LongOpenHashSet();     // render thread: tiles received
	private static int tilesApplied, columnsChanged;

	public static boolean enabled() { return ValCraftClient.active() && ValCraftClient.terrainMode(); }

	static void clearClient() { clientTiles.clear(); pending.clear(); if (Ops.remote(Minecraft.getInstance())) colTop.clear(); var s = net.minecraft.client.Minecraft.getInstance().getSingleplayerServer(); if (s != null) s.execute(BiomeSync::clear); }

	/** Render thread: EV_TERRAIN_TILE payload. */
	static void receive(byte[] p) {
		Tile t = parse(p);
		clientTiles.add(key(t.tx, t.tz));
		var mc = Minecraft.getInstance();
		if (Ops.remote(mc)) {
			// 0.13.0: joined through the tunnel: the host's server writes the blocks; we keep the column tops ourselves
			// (BlockSync runs on this client and must know which blocks are mirrored terrain)
			Ops.run(mc, ValCraftLink.EV_TERRAIN_TILE, p);
			int n = ValCraftLink.TERRAIN_TILE; var lvl = mc.level;
			for (int i = 0; i < n * n; i++) {
				int top = t.top[i]; if (top == Short.MIN_VALUE) continue;
				if (lvl != null) top = Math.max(lvl.getMinBuildHeight() + DEPTH, Math.min(lvl.getMaxBuildHeight() - 1, top));
				int x = t.tx * n + i / n, z = t.tz * n + i % n; long k = key(x, z);
				int prev = colTop.containsKey(k) ? colTop.get(k) : Integer.MIN_VALUE;
				if (prev != top) { colTop.put(k, top); BlockSync.markColumnDirty(x, z, prev, top); }
			}
			return;
		}
		pending.add(t);
	}
	private static Tile parse(byte[] p) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int tx = b.getInt(0), tz = b.getInt(4), n = ValCraftLink.TERRAIN_TILE * ValCraftLink.TERRAIN_TILE;
		short[] top = new short[n]; byte[] mat = new byte[n];
		for (int i = 0; i < n; i++) { top[i] = b.getShort(8 + i * 3); mat[i] = b.get(10 + i * 3); }
		byte[] bio = p.length >= 8 + n * 3 + 4 ? java.util.Arrays.copyOfRange(p, 8 + n * 3, 8 + n * 3 + 4) : null; // 0.9.7
		return new Tile(tx, tz, top, mat, bio);
	}
	/** Server thread: a tunnel client's Valheim terrain tile (all players' tiles of one area are identical: idempotent). */
	public static void tileOn(net.minecraft.server.level.ServerPlayer sp, byte[] p) {
		int n = ValCraftLink.TERRAIN_TILE * ValCraftLink.TERRAIN_TILE;
		if (p.length < 8 + n * 3 || pending.size() > 4000) return;
		pending.add(parse(p));
	}

	static boolean haveTileAt(double x, double z) {
		return clientTiles.contains(key(Math.floorDiv((int) Math.floor(x), ValCraftLink.TERRAIN_TILE), Math.floorDiv((int) Math.floor(z), ValCraftLink.TERRAIN_TILE)));
	}

	private static long key(int x, int z) { return ((long) x << 32) ^ (z & 0xFFFFFFFFL); }

	@SubscribeEvent
	public static void onServerTick(ServerTickEvent.Post e) {
		ServerLevel level = e.getServer().overworld();
		BiomeSync.tick(level);
		dev.valcraft.mobs.DragonSpawner.tick(level);
		if (pending.isEmpty()) return;
		for (int i = 0; i < TILES_PER_TICK; i++) {
			Tile t = pending.poll();
			if (t == null) break;
			apply(level, t);
		}
		if (pending.isEmpty() && tilesApplied > 0) ValCraft.LOG.info("[terrain] {} tiles applied so far, {} columns changed", tilesApplied, columnsChanged);
	}

	private static void apply(ServerLevel level, Tile t) {
		int n = ValCraftLink.TERRAIN_TILE;
		BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
		for (int i = 0; i < n * n; i++) {
			int top = t.top[i];
			if (top == Short.MIN_VALUE) continue;
			top = Math.max(level.getMinBuildHeight() + DEPTH, Math.min(level.getMaxBuildHeight() - 1, top));
			int x = t.tx * n + i / n, z = t.tz * n + i % n;
			long k = key(x, z);
			int packed = (top << 8) | (t.mat[i] & 0xFF);
			if (applied.containsKey(k) && applied.get(k) == packed) continue;
			int oldTop = applied.containsKey(k) ? applied.get(k) >> 8 : Integer.MIN_VALUE;
			BlockState surface = surface(t.mat[i]), sub = sub(t.mat[i]), stone = Blocks.DEEPSLATE.defaultBlockState();
			int flags = Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE;
			for (int y = top; y > top - DEPTH; y--) {
				BlockState s = y == top ? surface : y > top - 4 ? sub : stone;
				pos.set(x, y, z);
				if (level.getBlockState(pos) != s) level.setBlock(pos, s, flags);
			}
			// First time this session: the saved world may hold terrain from an earlier session/anchor above the new
			// top (stale ledges). Clear our terrain block types in a band above; player-built blocks of other types stay.
			int clearTo = oldTop == Integer.MIN_VALUE ? Math.min(level.getMaxBuildHeight() - 1, top + STALE_BAND) : oldTop;
			for (int y = top + 1; y <= clearTo; y++) {
				pos.set(x, y, z);
				BlockState cur = level.getBlockState(pos);
				if (cur.isAir()) continue;
				if (oldTop != Integer.MIN_VALUE || isTerrainBlock(cur)) level.setBlock(pos, Blocks.AIR.defaultBlockState(), flags);
			}
			applied.put(k, packed);
			int prevTop = colTop.containsKey(k) ? colTop.get(k) : Integer.MIN_VALUE;
			if (prevTop != top) { colTop.put(k, top); BlockSync.markColumnDirty(x, z, prevTop, top); }
			columnsChanged++;
		}
		tilesApplied++;
		BiomeSync.onTile(level, t.tx, t.tz, n, t.bio);
	}

	// TerrainMat (Valheim side): 1 grass, 2 cultivated, 3 stone, 4 sand, 5 snow, 6 mud, 7 ash, 8 paved, 9 gravel, 10 dark grass
	private static BlockState surface(byte m) {
		return switch (m) {
			case 2 -> Blocks.DIRT.defaultBlockState();      // 0.10.0: cultivated (hoe it in MC to get farmland)
			case 3 -> Blocks.ANDESITE.defaultBlockState();
			case 4 -> Blocks.SAND.defaultBlockState();
			case 5 -> Blocks.SNOW_BLOCK.defaultBlockState();
			case 6 -> Blocks.MUD.defaultBlockState();
			case 7 -> Blocks.BLACKSTONE.defaultBlockState();
			case 8 -> Blocks.COBBLESTONE.defaultBlockState();
			case 9 -> Blocks.GRAVEL.defaultBlockState();
			case 10 -> Blocks.GRASS_BLOCK.defaultBlockState(); // 0.9.7: was podzol; animals need grass_block (MC terrain isn't drawn)
			default -> Blocks.GRASS_BLOCK.defaultBlockState();
		};
	}

	private static BlockState sub(byte m) {
		return switch (m) {
			case 3, 5, 8 -> Blocks.ANDESITE.defaultBlockState();
			case 4 -> Blocks.SANDSTONE.defaultBlockState();
			case 6 -> Blocks.MUD.defaultBlockState();
			case 7 -> Blocks.BASALT.defaultBlockState();
			case 9 -> Blocks.GRAVEL.defaultBlockState();
			default -> Blocks.DIRT.defaultBlockState();
		};
	}

	// ---- Phase B hook: MC block edits -> Valheim (logged there, not yet applied) ----
	@SubscribeEvent
	public static void onBreak(BlockEvent.BreakEvent e) { forward(e.getPos(), 0, e.getState(), e.getPlayer() instanceof net.minecraft.server.level.ServerPlayer sp ? sp : null, e.getLevel()); }

	@SubscribeEvent
	public static void onPlace(BlockEvent.EntityPlaceEvent e) { forward(e.getPos(), 1, e.getPlacedBlock(), e.getEntity() instanceof net.minecraft.server.level.ServerPlayer sp ? sp : null, e.getLevel()); }

	/** 0.13.0: the edit goes to the Valheim of the player who made it (or the nearest player): Valheim then syncs the
	 * terrain change to everyone itself, so it is applied exactly once. */
	static void forward(BlockPos p, int kind, BlockState s, net.minecraft.server.level.ServerPlayer who, net.minecraft.world.level.LevelAccessor lvl) {
		if (!ValCraftClient.active()) return;
		long k = key(p.getX(), p.getZ());
		// keep the server-side column cache honest so a later Valheim resend restores/overrides correctly
		applied.remove(k);
		String id = BuiltInRegistries.BLOCK.getKey(s.getBlock()).toString();
		byte[] name = id.getBytes(java.nio.charset.StandardCharsets.UTF_8);
		ByteBuffer b = ByteBuffer.allocate(16 + Math.min(name.length, 200)).order(ByteOrder.LITTLE_ENDIAN);
		b.putInt(p.getX()).putInt(p.getY()).putInt(p.getZ()).putInt(kind).put(name, 0, Math.min(name.length, 200));
		byte[] payload = b.array();
		if (who == null && lvl instanceof ServerLevel sl) {
			double best = 64 * 64;
			for (var pl : sl.players()) { double d = pl.distanceToSqr(p.getX() + 0.5, p.getY() + 0.5, p.getZ() + 0.5); if (d < best) { best = d; who = pl; } }
		}
		if (who != null) dev.valcraft.net.ServerOps.toValheim(who, ValCraftLink.EV_BLOCK_CHANGED, payload);
	}
}
