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

	private static boolean isTerrainBlock(BlockState s) {
		for (byte m = 1; m <= 10; m++) if (s == surface(m) || s == sub(m)) return true;
		return s.is(Blocks.STONE);
	}
	private record Tile(int tx, int tz, short[] top, byte[] mat) {}
	private static final ConcurrentLinkedQueue<Tile> pending = new ConcurrentLinkedQueue<>();
	private static final Long2IntOpenHashMap applied = new Long2IntOpenHashMap(); // server thread: column -> top<<8|mat
	private static final LongOpenHashSet clientTiles = new LongOpenHashSet();     // render thread: tiles received
	private static int tilesApplied, columnsChanged;

	public static boolean enabled() { return ValCraftClient.active() && ValCraftClient.terrainMode(); }

	static void clearClient() { clientTiles.clear(); pending.clear(); }

	/** Render thread: EV_TERRAIN_TILE payload. */
	static void receive(byte[] p) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int tx = b.getInt(0), tz = b.getInt(4), n = ValCraftLink.TERRAIN_TILE * ValCraftLink.TERRAIN_TILE;
		short[] top = new short[n]; byte[] mat = new byte[n];
		for (int i = 0; i < n; i++) { top[i] = b.getShort(8 + i * 3); mat[i] = b.get(10 + i * 3); }
		clientTiles.add(key(tx, tz));
		pending.add(new Tile(tx, tz, top, mat));
	}

	static boolean haveTileAt(double x, double z) {
		return clientTiles.contains(key(Math.floorDiv((int) Math.floor(x), ValCraftLink.TERRAIN_TILE), Math.floorDiv((int) Math.floor(z), ValCraftLink.TERRAIN_TILE)));
	}

	private static long key(int x, int z) { return ((long) x << 32) ^ (z & 0xFFFFFFFFL); }

	@SubscribeEvent
	public static void onServerTick(ServerTickEvent.Post e) {
		if (pending.isEmpty()) return;
		ServerLevel level = e.getServer().overworld();
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
			BlockState surface = surface(t.mat[i]), sub = sub(t.mat[i]), stone = Blocks.STONE.defaultBlockState();
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
			columnsChanged++;
		}
		tilesApplied++;
	}

	// TerrainMat (Valheim side): 1 grass, 2 cultivated, 3 stone, 4 sand, 5 snow, 6 mud, 7 ash, 8 paved, 9 gravel, 10 dark grass
	private static BlockState surface(byte m) {
		return switch (m) {
			case 2 -> Blocks.FARMLAND.defaultBlockState();
			case 3 -> Blocks.STONE.defaultBlockState();
			case 4 -> Blocks.SAND.defaultBlockState();
			case 5 -> Blocks.SNOW_BLOCK.defaultBlockState();
			case 6 -> Blocks.MUD.defaultBlockState();
			case 7 -> Blocks.BLACKSTONE.defaultBlockState();
			case 8 -> Blocks.COBBLESTONE.defaultBlockState();
			case 9 -> Blocks.GRAVEL.defaultBlockState();
			case 10 -> Blocks.PODZOL.defaultBlockState();
			default -> Blocks.GRASS_BLOCK.defaultBlockState();
		};
	}

	private static BlockState sub(byte m) {
		return switch (m) {
			case 3, 5, 8 -> Blocks.STONE.defaultBlockState();
			case 4 -> Blocks.SANDSTONE.defaultBlockState();
			case 6 -> Blocks.MUD.defaultBlockState();
			case 7 -> Blocks.BASALT.defaultBlockState();
			case 9 -> Blocks.GRAVEL.defaultBlockState();
			default -> Blocks.DIRT.defaultBlockState();
		};
	}

	// ---- Phase B hook: MC block edits -> Valheim (logged there, not yet applied) ----
	@SubscribeEvent
	public static void onBreak(BlockEvent.BreakEvent e) { forward(e.getPos(), 0, e.getState()); }

	@SubscribeEvent
	public static void onPlace(BlockEvent.EntityPlaceEvent e) { forward(e.getPos(), 1, e.getPlacedBlock()); }

	private static void forward(BlockPos p, int kind, BlockState s) {
		if (!ValCraftClient.active()) return;
		long k = key(p.getX(), p.getZ());
		// keep the server-side column cache honest so a later Valheim resend restores/overrides correctly
		applied.remove(k);
		String id = BuiltInRegistries.BLOCK.getKey(s.getBlock()).toString();
		byte[] name = id.getBytes(java.nio.charset.StandardCharsets.UTF_8);
		ByteBuffer b = ByteBuffer.allocate(16 + Math.min(name.length, 200)).order(ByteOrder.LITTLE_ENDIAN);
		b.putInt(p.getX()).putInt(p.getY()).putInt(p.getZ()).putInt(kind).put(name, 0, Math.min(name.length, 200));
		byte[] payload = b.array();
		Minecraft.getInstance().execute(() -> ValCraftClient.sendToValheim(ValCraftLink.EV_BLOCK_CHANGED, payload)); // ring is single-producer: send from render thread
	}
}
