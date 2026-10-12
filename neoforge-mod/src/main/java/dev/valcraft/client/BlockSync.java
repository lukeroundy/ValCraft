package dev.valcraft.client;

import com.mojang.blaze3d.platform.NativeImage;
import dev.valcraft.ValCraft;
import dev.valcraft.link.BlockCodec;
import dev.valcraft.link.ValCraftLink;
import it.unimi.dsi.fastutil.ints.Int2IntOpenHashMap;
import it.unimi.dsi.fastutil.longs.Long2IntOpenHashMap;
import it.unimi.dsi.fastutil.longs.LongArrayList;
import it.unimi.dsi.fastutil.longs.LongLinkedOpenHashSet;
import it.unimi.dsi.fastutil.longs.LongOpenHashSet;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayDeque;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentLinkedQueue;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.ItemBlockRenderTypes;
import net.minecraft.client.renderer.RenderType;
import net.minecraft.client.renderer.block.model.BakedQuad;
import net.minecraft.client.renderer.texture.TextureAtlasSprite;
import net.minecraft.client.resources.model.BakedModel;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.util.RandomSource;
import net.minecraft.world.level.EmptyBlockGetter;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.chunk.LevelChunk;
import net.minecraft.world.level.chunk.LevelChunkSection;
import net.minecraft.world.phys.AABB;
import net.neoforged.api.distmarker.Dist;
import net.neoforged.bus.api.SubscribeEvent;
import net.neoforged.fml.common.EventBusSubscriber;
import net.neoforged.neoforge.client.event.RenderFrameEvent;
import net.neoforged.neoforge.event.level.BlockEvent;
import net.neoforged.neoforge.event.tick.ServerTickEvent;

/**
 * 0.7.0: streams the MC blocks that are NOT mirrored Valheim terrain (player builds, placed blocks) in 16^3 sections
 * around the player to Valheim, which meshes and draws them. MC's world stays the source of truth (persistence).
 * Server thread: scans sections (dirty first, then a round-robin pass for initial sync / anything missed), hashes
 * contents and queues changed sections. Render thread: maps block states to palette entries (face textures in a
 * PNG atlas next to the shm file + shape boxes), and drains everything onto the M->V ring as space allows.
 */
@EventBusSubscriber(modid = ValCraft.MOD_ID, value = Dist.CLIENT)
public final class BlockSync {
	private BlockSync() {}
	static final int VERT_SECTIONS = 4, NONEMPTY_PER_TICK = 12, SCANS_PER_TICK = 96, OUTBOX_MAX = 4000;
	static final int ATLAS_TILE = 16, ATLAS_PER_ROW = 64, ATLAS_TILES = ATLAS_PER_ROW * ATLAS_PER_ROW;

	// config from Valheim (render thread writes, server thread reads)
	static volatile boolean enabled;
	static volatile int radius = 64;
	static volatile boolean resetServer;

	private record SectionOut(int sx, int sy, int sz, short[] local, int[] state, int count) {}
	private static final ConcurrentLinkedQueue<SectionOut> fromServer = new ConcurrentLinkedQueue<>();
	private static final ConcurrentLinkedQueue<Long> dirtyQ = new ConcurrentLinkedQueue<>();

	// ---------------- server thread ----------------
	private static final Long2IntOpenHashMap sentHash = new Long2IntOpenHashMap();
	private static final LongLinkedOpenHashSet dirty = new LongLinkedOpenHashSet();
	private static LongArrayList desired = new LongArrayList();
	private static LongOpenHashSet desiredSet = new LongOpenHashSet();
	private static int cursor;
	private static long lastCenter = Long.MIN_VALUE;
	private static int lastRadius;
	private static long totalSent;

	static long key(int sx, int sy, int sz) { return ((long) (sx & 0x3FFFFF) << 42) | ((long) (sz & 0x3FFFFF) << 20) | (sy & 0xFFFFF); }
	static int kx(long k) { return (int) (k << 0 >> 42); }
	static int kz(long k) { return (int) (k << 22 >> 42); }
	static int ky(long k) { return (int) (k << 44 >> 44); }

	static void markDirty(int x, int y, int z) { dirtyQ.add(key(x >> 4, y >> 4, z >> 4)); }
	/** TerrainSync: a column's mirrored-terrain top changed -> the "is this terrain?" decision changes for that column. */
	static void markColumnDirty(int x, int z, int oldTop, int newTop) {
		if (!enabled) return;
		int lo = Math.min(oldTop == Integer.MIN_VALUE ? newTop : oldTop, newTop) >> 4, hi = Math.max(oldTop == Integer.MIN_VALUE ? newTop : oldTop, newTop) >> 4;
		for (int sy = lo; sy <= hi; sy++) dirtyQ.add(key(x >> 4, sy, z >> 4));
	}

	@SubscribeEvent public static void onBreak(BlockEvent.BreakEvent e) { if (enabled) markDirty(e.getPos().getX(), e.getPos().getY(), e.getPos().getZ()); }
	@SubscribeEvent public static void onPlace(BlockEvent.EntityPlaceEvent e) { if (enabled) markDirty(e.getPos().getX(), e.getPos().getY(), e.getPos().getZ()); }
	@SubscribeEvent public static void onNotify(BlockEvent.NeighborNotifyEvent e) { if (enabled) markDirty(e.getPos().getX(), e.getPos().getY(), e.getPos().getZ()); }

	@SubscribeEvent
	public static void onServerTick(ServerTickEvent.Post e) {
		if (resetServer) { resetServer = false; sentHash.clear(); dirty.clear(); dirtyQ.clear(); lastCenter = Long.MIN_VALUE; cursor = 0; totalSent = 0; }
		if (!enabled || !ValCraftClient.active()) return;
		ServerLevel level = e.getServer().overworld();
		List<ServerPlayer> players = e.getServer().getPlayerList().getPlayers();
		if (players.isEmpty()) return;
		BlockPos pp = players.get(0).blockPosition();
		int r = Math.max(16, Math.min(160, radius));
		long center = key(pp.getX() >> 4, pp.getY() >> 4, pp.getZ() >> 4);
		if (center != lastCenter || r != lastRadius) rebuildDesired(pp.getX() >> 4, pp.getY() >> 4, pp.getZ() >> 4, r);
		Long q;
		while ((q = dirtyQ.poll()) != null) dirty.add(q.longValue());

		int nonEmpty = 0, scans = 0;
		while (!dirty.isEmpty() && nonEmpty < NONEMPTY_PER_TICK * 2) {
			long k = dirty.removeFirstLong();
			if (!desiredSet.contains(k)) continue;
			if (scan(level, k)) nonEmpty++;
		}
		while (scans < SCANS_PER_TICK && nonEmpty < NONEMPTY_PER_TICK && !desired.isEmpty()) {
			if (cursor >= desired.size()) cursor = 0;
			if (scan(level, desired.getLong(cursor++))) nonEmpty++;
			scans++;
		}
	}

	private static void rebuildDesired(int cx, int cy, int cz, int r) {
		lastRadius = r;
		lastCenter = key(cx, cy, cz);
		int rs = (r + 15) / 16;
		LongArrayList list = new LongArrayList();
		for (int dx = -rs; dx <= rs; dx++) for (int dz = -rs; dz <= rs; dz++) {
			if ((dx * dx + dz * dz) * 256 > (r + 12) * (r + 12)) continue;
			for (int dy = -VERT_SECTIONS; dy <= VERT_SECTIONS; dy++) list.add(key(cx + dx, cy + dy, cz + dz));
		}
		list.sort((a, b) -> Integer.compare(d2(a, cx, cy, cz), d2(b, cx, cy, cz)));
		LongOpenHashSet set = new LongOpenHashSet(list);
		// sections that fell out of range: tell Valheim to drop them
		LongArrayList gone = new LongArrayList();
		for (long k : sentHash.keySet()) if (!set.contains(k)) gone.add(k);
		for (long k : gone) { sentHash.remove(k); fromServer.add(new SectionOut(kx(k), ky(k), kz(k), new short[0], new int[0], 0)); }
		desired = list; desiredSet = set; cursor = 0;
	}

	private static int d2(long k, int cx, int cy, int cz) { int a = kx(k) - cx, b = ky(k) - cy, c = kz(k) - cz; return a * a + b * b * 2 + c * c; }

	private static final short[] tmpLocal = new short[4096];
	private static final int[] tmpState = new int[4096];

	/** Returns true if the section had any non-air content (counts toward the per-tick budget). */
	private static boolean scan(ServerLevel level, long k) {
		int sx = kx(k), sy = ky(k), sz = kz(k);
		LevelChunk chunk = level.getChunkSource().getChunkNow(sx, sz);
		if (chunk == null) return false;
		int idx = chunk.getSectionIndexFromSectionY(sy);
		if (idx < 0 || idx >= chunk.getSectionsCount()) return false;
		LevelChunkSection sec = chunk.getSection(idx);
		int n = 0, h = 1;
		boolean any = sec != null && !sec.hasOnlyAir();
		if (any) {
			for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) {
				BlockState s = sec.getBlockState(x, y, z);
				if (s.isAir()) continue;
				int wx = (sx << 4) + x, wy = (sy << 4) + y, wz = (sz << 4) + z;
				if (TerrainSync.isTerrainBlock(s)) { int top = TerrainSync.terrainTop(wx, wz); if (top == Integer.MIN_VALUE || wy <= top) continue; }
				if (s.getRenderShape() == net.minecraft.world.level.block.RenderShape.INVISIBLE && s.getFluidState().isEmpty()) continue;
				int id = Block.getId(s);
				tmpLocal[n] = (short) ((y << 8) | (z << 4) | x); tmpState[n] = id; n++;
				h = h * 31 + ((y << 8) | (z << 4) | x); h = h * 31 + id;
			}
		}
		if (n == 0) h = 0;
		boolean had = sentHash.containsKey(k);
		if (n == 0 && !had) return any;
		if (had && sentHash.get(k) == h) return any;
		if (n == 0) sentHash.remove(k); else sentHash.put(k, h);
		short[] l = new short[n]; int[] st = new int[n];
		System.arraycopy(tmpLocal, 0, l, 0, n); System.arraycopy(tmpState, 0, st, 0, n);
		fromServer.add(new SectionOut(sx, sy, sz, l, st, n));
		totalSent++;
		if (totalSent <= 3 || totalSent % 200 == 0) ValCraft.LOG.info("[blocks] section ({},{},{}) {} blocks queued ({} sections sent so far)", sx, sy, sz, n, totalSent);
		return true;
	}

	// ---------------- render thread ----------------
	private static final ArrayDeque<ValCraftLink.Event> outbox = new ArrayDeque<>();
	private static final Int2IntOpenHashMap paletteOf = new Int2IntOpenHashMap(); // state id -> palette id
	private static final Map<String, Integer> tileOf = new HashMap<>();
	private static NativeImage atlas;
	private static int nextPalette = 1, nextTile = 1, atlasRevision;
	private static boolean atlasDirty;
	private static long nextAtlasWrite;

	/** Valheim config: EV_BLOCK_SYNC_CONFIG {u32 enabled, u32 radius}. */
	static void receiveConfig(byte[] p) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		boolean en = b.getInt(0) != 0; int r = b.getInt(4);
		if (en != enabled || r != radius) ValCraft.LOG.info("[blocks] Valheim block rendering {} (radius {})", en ? "on" : "off", r);
		if (en && !enabled) reset();
		radius = r; enabled = en;
	}

	/** On (re)link / disconnect: forget everything sent; Valheim is told to clear on next send. */
	static void reset() {
		outbox.clear(); fromServer.clear(); paletteOf.clear(); tileOf.clear();
		nextPalette = 1; nextTile = 1; atlasDirty = false;
		if (atlas != null) { atlas.close(); atlas = null; }
		resetServer = true;
		outbox.add(new ValCraftLink.Event(ValCraftLink.EV_BLOCKS_RESET, new byte[0]));
	}
	static void disconnected() { enabled = false; reset(); outbox.clear(); }

	@SubscribeEvent
	public static void onRenderFrame(RenderFrameEvent.Post e) {
		if (!ValCraftClient.active()) return;
		Minecraft mc = Minecraft.getInstance();
		SectionOut so;
		while (outbox.size() < OUTBOX_MAX && (so = fromServer.poll()) != null) {
			short[] pid = new short[so.count];
			for (int i = 0; i < so.count; i++) pid[i] = (short) paletteFor(mc, so.state[i]);
			outbox.addAll(BlockCodec.section(so.sx, so.sy, so.sz, so.local, pid, so.count));
		}
		long now = System.currentTimeMillis();
		if (atlasDirty && now >= nextAtlasWrite) writeAtlas();
		ValCraftLink.Event ev;
		while ((ev = outbox.peek()) != null && ValCraftClient.sendToValheim(ev.type(), ev.payload())) outbox.poll();
	}

	private static int paletteFor(Minecraft mc, int stateId) {
		if (paletteOf.containsKey(stateId)) return paletteOf.get(stateId);
		int id = nextPalette++;
		paletteOf.put(stateId, id);
		BlockState s = Block.stateById(stateId);
		int flags = 0; int[] tiles = new int[6]; byte[] boxes;
		if (s.getRenderShape() == net.minecraft.world.level.block.RenderShape.INVISIBLE && !s.getFluidState().isEmpty()) {
			outbox.add(fluidPalette(mc, id, s));
			return id;
		}
		try {
			if (s.isSolidRender(EmptyBlockGetter.INSTANCE, BlockPos.ZERO)) flags |= BlockCodec.FLAG_FULL_OPAQUE;
			if (!s.getCollisionShape(EmptyBlockGetter.INSTANCE, BlockPos.ZERO).isEmpty()) flags |= BlockCodec.FLAG_COLLIDES;
			RenderType rt = ItemBlockRenderTypes.getChunkRenderType(s);
			if (rt == RenderType.translucent() || rt == RenderType.tripwire()) flags |= BlockCodec.FLAG_TRANSLUCENT;
			else if (rt != RenderType.solid()) flags |= BlockCodec.FLAG_CUTOUT;
			if (isCross(s)) flags = (flags & ~BlockCodec.FLAG_COLLIDES & ~BlockCodec.FLAG_FULL_OPAQUE) | BlockCodec.FLAG_CROSS | BlockCodec.FLAG_CUTOUT;
		} catch (Throwable t) { flags = BlockCodec.FLAG_FULL_OPAQUE | BlockCodec.FLAG_COLLIDES; }
		try {
			List<AABB> list = s.getShape(EmptyBlockGetter.INSTANCE, BlockPos.ZERO).toAabbs();
			if (list.isEmpty()) list = List.of(new AABB(0.125, 0, 0.125, 0.875, 0.75, 0.875)); // plants etc.: a small box
			int n = Math.min(list.size(), BlockCodec.MAX_BOXES);
			boxes = new byte[n * 6];
			for (int i = 0; i < n; i++) {
				AABB a = list.get(i);
				double[] v = { a.minX, a.minY, a.minZ, a.maxX, a.maxY, a.maxZ };
				for (int j = 0; j < 6; j++) boxes[i * 6 + j] = (byte) Math.max(0, Math.min(16, Math.round(v[j] * 16)));
				for (int j = 0; j < 3; j++) if (boxes[i * 6 + j + 3] <= boxes[i * 6 + j]) { if (boxes[i * 6 + j] >= 16) boxes[i * 6 + j]--; boxes[i * 6 + j + 3] = (byte) (boxes[i * 6 + j] + 1); }
			}
		} catch (Throwable t) { boxes = new byte[] { 0, 0, 0, 16, 16, 16 }; }
		java.util.List<BlockCodec.Quad> quads = null;
		try {
			BakedModel model = mc.getBlockRenderer().getBlockModel(s);
			boolean fullBox = boxes.length == 6 && boxes[0] == 0 && boxes[1] == 0 && boxes[2] == 0 && boxes[3] == 16 && boxes[4] == 16 && boxes[5] == 16;
			if ((flags & BlockCodec.FLAG_CROSS) == 0 && (!fullBox || (flags & BlockCodec.FLAG_COLLIDES) == 0)) quads = modelQuads(mc, s, model);
			RandomSource rand = RandomSource.create(42);
			List<BakedQuad> general = model.getQuads(s, null, rand);
			for (Direction d : Direction.values()) {
				rand.setSeed(42);
				List<BakedQuad> q = model.getQuads(s, d, rand);
				BakedQuad pick = q.isEmpty() ? null : q.get(0);
				if (pick == null) for (BakedQuad g : general) if (g.getDirection() == d) { pick = g; break; }
				if (pick == null && !general.isEmpty()) pick = general.get(0);
				TextureAtlasSprite sprite = pick != null ? pick.getSprite() : model.getParticleIcon();
				int tint = pick != null && pick.isTinted() ? mc.getBlockColors().getColor(s, null, null, pick.getTintIndex()) : -1;
				tiles[d.ordinal()] = tileFor(sprite, tint);
			}
		} catch (Throwable t) { ValCraft.LOG.warn("[blocks] textures for {}: {}", s, t.toString()); }
		int light = 0;
		try { light = s.getLightEmission(EmptyBlockGetter.INSTANCE, BlockPos.ZERO); } catch (Throwable t) { }
		String name = net.minecraft.core.registries.BuiltInRegistries.BLOCK.getKey(s.getBlock()).getPath();
		if (isShiny(name)) flags |= BlockCodec.FLAG_SHINY;
		if (quads != null && !quads.isEmpty()) flags |= BlockCodec.FLAG_QUADS;
		outbox.add(BlockCodec.palette(id, flags, tiles, boxes, light, lightColor(name)));
		if ((flags & BlockCodec.FLAG_QUADS) != 0) outbox.addAll(BlockCodec.quads(id, quads));
		return id;
	}

	static boolean isShiny(String n) {
		return n.endsWith("glass") || n.endsWith("glass_pane") || n.contains("ice") || n.equals("iron_block") || n.equals("gold_block") || n.equals("diamond_block")
				|| n.equals("emerald_block") || n.equals("netherite_block") || n.contains("copper") || n.startsWith("polished_") || n.contains("quartz")
				|| n.contains("amethyst") || n.contains("prismarine") || n.equals("obsidian") || n.contains("glazed_terracotta");
	}

	/** Light colour (0xRRGGBB) by block name. */
	static int lightColor(String n) {
		if (n.contains("soul")) return 0x66E0FF;
		if (n.contains("sea_lantern") || n.contains("end_rod") || n.contains("beacon") || n.contains("froglight") && n.startsWith("pearlescent")) return 0xD8F0FF;
		if (n.contains("lava") || n.contains("magma")) return 0xFF7A30;
		if (n.contains("redstone") || n.contains("crying")) return 0xFF5040;
		if (n.contains("glowstone") || n.contains("shroomlight") || n.contains("lamp")) return 0xFFD890;
		return 0xFFB060; // torches, lanterns, campfires, jack o'lanterns, ...
	}

	/** All model quads (directional + general) with sprite-relative UVs; vertex format BLOCK (8 ints/vertex). */
	private static java.util.List<BlockCodec.Quad> modelQuads(Minecraft mc, BlockState s, BakedModel model) {
		var out = new java.util.ArrayList<BlockCodec.Quad>();
		RandomSource rand = RandomSource.create(42);
		for (int d = 0; d <= 6; d++) {
			Direction side = d < 6 ? Direction.values()[d] : null;
			rand.setSeed(42);
			for (BakedQuad q : model.getQuads(s, side, rand)) {
				int[] v = q.getVertices();
				int stride = v.length / 4;
				TextureAtlasSprite sp = q.getSprite();
				float u0 = sp.getU0(), u1 = sp.getU1(), v0 = sp.getV0(), v1 = sp.getV1();
				float[] pos = new float[12], uv = new float[8];
				for (int i = 0; i < 4; i++) {
					int o = i * stride;
					pos[i * 3] = Float.intBitsToFloat(v[o]); pos[i * 3 + 1] = Float.intBitsToFloat(v[o + 1]); pos[i * 3 + 2] = Float.intBitsToFloat(v[o + 2]);
					uv[i * 2] = (Float.intBitsToFloat(v[o + 4]) - u0) / Math.max(1e-6f, u1 - u0);
					uv[i * 2 + 1] = (Float.intBitsToFloat(v[o + 5]) - v0) / Math.max(1e-6f, v1 - v0);
				}
				int tint = q.isTinted() ? mc.getBlockColors().getColor(s, null, null, q.getTintIndex()) : -1;
				out.add(new BlockCodec.Quad(tileFor(sp, tint), q.getDirection().ordinal(), d, pos, uv));
				if (out.size() >= 255) return out;
			}
		}
		return out;
	}

	/** Blocks MC draws with the "cross" (X-shaped) / crop model. */
	static boolean isCross(BlockState s) {
		var b = s.getBlock();
		return b instanceof net.minecraft.world.level.block.BushBlock || b instanceof net.minecraft.world.level.block.SugarCaneBlock
				|| b instanceof net.minecraft.world.level.block.BambooSaplingBlock || b instanceof net.minecraft.world.level.block.WebBlock
				|| b instanceof net.minecraft.world.level.block.NetherSproutsBlock || b instanceof net.minecraft.world.level.block.HangingRootsBlock;
	}

	/** Water/lava: tiles up/down = still (frame 0), sides = flowing; box height = fluid height; translucent for non-lava. */
	private static ValCraftLink.Event fluidPalette(Minecraft mc, int id, BlockState s) {
		var fs = s.getFluidState();
		var ext = net.neoforged.neoforge.client.extensions.common.IClientFluidTypeExtensions.of(fs);
		var atlasFn = mc.getTextureAtlas(net.minecraft.world.inventory.InventoryMenu.BLOCK_ATLAS);
		int tint = ext.getTintColor() & 0xFFFFFF;
		if (tint == 0xFFFFFF) tint = -1;
		int still = tileFor(atlasFn.apply(ext.getStillTexture()), tint), flow = tileFor(atlasFn.apply(ext.getFlowingTexture()), tint);
		boolean lava = fs.is(net.minecraft.tags.FluidTags.LAVA);
		int h = Math.max(1, Math.min(16, Math.round(fs.getOwnHeight() * 16)));
		int flags = BlockCodec.FLAG_FLUID | (lava ? 0 : BlockCodec.FLAG_TRANSLUCENT | BlockCodec.FLAG_SHINY);
		int light = 0; try { light = s.getLightEmission(EmptyBlockGetter.INSTANCE, BlockPos.ZERO); } catch (Throwable t) { }
		if (lava && light == 0) light = 15;
		return BlockCodec.palette(id, flags, new int[] { still, still, flow, flow, flow, flow }, new byte[] { 0, 0, 0, 16, (byte) h, 16 }, light, lava ? 0xFF7A30 : 0);
	}

	private static int tileFor(TextureAtlasSprite sprite, int tint) {
		String key = sprite.contents().name() + "#" + tint;
		Integer t = tileOf.get(key);
		if (t != null) return t;
		if (nextTile >= ATLAS_TILES) return 0;
		int tile = nextTile++;
		tileOf.put(key, tile);
		if (atlas == null) {
			atlas = new NativeImage(ATLAS_PER_ROW * ATLAS_TILE, ATLAS_PER_ROW * ATLAS_TILE, true);
			for (int y = 0; y < ATLAS_TILE; y++) for (int x = 0; x < ATLAS_TILE; x++) atlas.setPixelRGBA(x, y, ((x ^ y) & 4) != 0 ? 0xFFFF00FF : 0xFF000000); // tile 0 = missing
		}
		NativeImage src = sprite.contents().getOriginalImage();
		int w = sprite.contents().width(), hgt = Math.min(sprite.contents().height(), w);
		int ox = (tile % ATLAS_PER_ROW) * ATLAS_TILE, oy = (tile / ATLAS_PER_ROW) * ATLAS_TILE;
		int tr = (tint >> 16) & 0xFF, tg = (tint >> 8) & 0xFF, tb = tint & 0xFF;
		for (int y = 0; y < ATLAS_TILE; y++) for (int x = 0; x < ATLAS_TILE; x++) {
			int p = src.getPixelRGBA(x * w / ATLAS_TILE, y * hgt / ATLAS_TILE); // ABGR
			if (tint != -1) {
				int r = (p & 0xFF) * tr / 255, g = ((p >> 8) & 0xFF) * tg / 255, b = ((p >> 16) & 0xFF) * tb / 255;
				p = (p & 0xFF000000) | (b << 16) | (g << 8) | r;
			}
			atlas.setPixelRGBA(ox + x, oy + y, p);
		}
		atlasDirty = true;
		return tile;
	}

	static Path atlasPath() { return ValCraftLink.defaultPath().resolveSibling("blocks-atlas.png"); }

	private static void writeAtlas() {
		nextAtlasWrite = System.currentTimeMillis() + 500;
		try {
			Path p = atlasPath(), tmp = p.resolveSibling("blocks-atlas.tmp.png");
			atlas.writeToFile(tmp);
			Files.move(tmp, p, StandardCopyOption.REPLACE_EXISTING);
			atlasDirty = false;
			atlasRevision++;
			outbox.add(BlockCodec.atlas(atlasRevision, nextTile, ATLAS_PER_ROW, ATLAS_TILE, p.toString()));
			ValCraft.LOG.info("[blocks] atlas rev {} written ({} tiles, {} block states) -> {}", atlasRevision, nextTile, nextPalette - 1, p);
		} catch (Exception ex) { ValCraft.LOG.warn("[blocks] atlas write failed (will retry): {}", ex.toString()); }
	}
}
