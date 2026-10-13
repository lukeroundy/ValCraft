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

	/** 0.7.3: explosions lower Valheim terrain: per column, the lowest destroyed terrain-type block goes through the dig path. */
	@SubscribeEvent
	public static void onExplosion(net.neoforged.neoforge.event.level.ExplosionEvent.Detonate e) {
		if (!ValCraftClient.active()) return;
		// 0.10.0: mob explosions (creepers etc.) don't touch the ground at all: no MC terrain blocks destroyed, no Valheim dig
		var src = e.getExplosion().getDirectSourceEntity();
		var causer = e.getExplosion().getIndirectSourceEntity();
		boolean tnt = src instanceof net.minecraft.world.entity.item.PrimedTnt || src instanceof net.minecraft.world.entity.vehicle.MinecartTNT;
		if (!tnt && (src instanceof net.minecraft.world.entity.Mob || causer instanceof net.minecraft.world.entity.Mob)) {
			int before = e.getAffectedBlocks().size();
			e.getAffectedBlocks().removeIf(p -> TerrainSync.isTerrainBlock(e.getLevel().getBlockState(p)));
			if (before != e.getAffectedBlocks().size()) ValCraft.LOG.debug("[terrain] mob explosion: {} ground blocks kept", before - e.getAffectedBlocks().size());
			return;
		}
		java.util.Map<Long, BlockPos> lowest = new java.util.HashMap<>();
		java.util.Map<Long, BlockState> st = new java.util.HashMap<>();
		for (BlockPos p : e.getAffectedBlocks()) {
			BlockState s = e.getLevel().getBlockState(p);
			if (!TerrainSync.isTerrainBlock(s)) continue;
			long k = ((long) p.getX() << 32) ^ (p.getZ() & 0xFFFFFFFFL);
			BlockPos cur = lowest.get(k);
			if (cur == null || p.getY() < cur.getY()) { lowest.put(k, p.immutable()); st.put(k, s); }
		}
		for (var en : lowest.entrySet()) TerrainSync.forward(en.getValue(), 0, st.get(en.getKey()), null, e.getLevel());
		if (!lowest.isEmpty()) ValCraft.LOG.info("[terrain] explosion: {} columns -> Valheim dig", lowest.size());
	}

	@SubscribeEvent
	public static void onServerTick(ServerTickEvent.Post e) {
		if (!enabled || !ValCraftClient.active()) { if (resetServer) scanTick(null, null); return; }
		// 0.13.0: around OUR player (the host), not whoever joined first; tunnel clients scan their own client world
		var mc = Minecraft.getInstance(); var me = mc.player;
		ServerPlayer sp = me == null ? null : e.getServer().getPlayerList().getPlayer(me.getUUID());
		if (sp == null) return;
		scanTick(e.getServer().overworld(), sp.blockPosition());
	}

	/** Render thread, joined through the tunnel: the same scan on the client world (the host's server is not ours). */
	static void clientTick(Minecraft mc) {
		if (!Ops.remote(mc) || mc.level == null || mc.player == null) return;
		if (!enabled || !ValCraftClient.active()) { if (resetServer) scanTick(null, null); return; }
		scanTick(mc.level, mc.player.blockPosition());
	}

	private static void scanTick(net.minecraft.world.level.Level level, BlockPos pp) {
		if (resetServer) { resetServer = false; sentHash.clear(); dirty.clear(); dirtyQ.clear(); lastCenter = Long.MIN_VALUE; cursor = 0; totalSent = 0; }
		if (level == null) return;
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
	private static boolean scan(net.minecraft.world.level.Level level, long k) {
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
				if (TerrainSync.isTerrainBlock(s) || TerrainSync.isLegacyTerrain(s, wy, TerrainSync.terrainTop(wx, wz))) { int top = TerrainSync.terrainTop(wx, wz); if (top == Integer.MIN_VALUE || wy <= top) continue; }
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
		if (p.length >= 12) EntitySync.maxEntities = b.getInt(8);
		if (p.length >= 16) EntitySync.maxMobs = b.getInt(12);
	}

	/** On (re)link / disconnect: forget everything sent; Valheim is told to clear on next send. */
	static void reset() {
		outbox.clear(); fromServer.clear(); paletteOf.clear(); tileOf.clear();
		nextPalette = 1; nextTile = 1; atlasDirty = false;
		if (atlas != null) { atlas.close(); atlas = null; }
		Capture.clearImages(); EntitySync.reset();
		resetServer = true;
		outbox.add(new ValCraftLink.Event(ValCraftLink.EV_BLOCKS_RESET, new byte[0]));
	}
	private static int entErr;
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
		if (enabled && outbox.size() < OUTBOX_MAX) {
			try { EntitySync.tick(mc, outbox); }
			catch (Throwable t) { if (entErr++ < 5) ValCraft.LOG.warn("[entities] sync error: {}", t.toString(), t); }
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
		com.google.gson.JsonObject ov = null; try { ov = BlockProps.override(s); } catch (Throwable t) { }
		if (s.getRenderShape() == net.minecraft.world.level.block.RenderShape.INVISIBLE && !s.getFluidState().isEmpty()) {
			outbox.add(fluidPalette(mc, id, s));
			return id;
		}
		try {
			if (s.isSolidRender(EmptyBlockGetter.INSTANCE, BlockPos.ZERO)) flags |= BlockCodec.FLAG_FULL_OPAQUE;
			if (!s.getCollisionShape(EmptyBlockGetter.INSTANCE, BlockPos.ZERO).isEmpty()) flags |= BlockCodec.FLAG_COLLIDES;
			int rc = BlockProps.renderClass(mc, s, mc.getBlockRenderer().getBlockModel(s), ov); // 0.14.0: model render types (any mod) + alpha fallback
			if (rc == BlockProps.TRANSLUCENT) flags |= BlockCodec.FLAG_TRANSLUCENT;
			else if (rc == BlockProps.CUTOUT) flags |= BlockCodec.FLAG_CUTOUT;
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
			// 0.7.3: always the real model quads (fixes rotated UVs e.g. pistons/logs); boxes = collision (+ fallback)
			if ((flags & BlockCodec.FLAG_CROSS) == 0) {
				quads = s.getRenderShape() == net.minecraft.world.level.block.RenderShape.MODEL ? modelQuads(mc, s, model) : new java.util.ArrayList<>();
				var ber = isLidBlock(s) ? java.util.List.<BlockCodec.Quad>of() : berQuads(mc, s);
				if (ber.isEmpty() && s.getBlock() instanceof net.minecraft.world.level.block.DecoratedPotBlock) { ber = potFallback(mc, s); ValCraft.LOG.info("[blocks] decorated pot: renderer capture gave nothing, using built-in pot mesh ({} quads)", ber.size()); }
				// chests / ender chests / shulker boxes are drawn as live block entities by EntitySync (lid animation): static part = invisible placeholder
				if (isLidBlock(s)) { quads = new java.util.ArrayList<>(); quads.add(new BlockCodec.Quad(0, 1, 6, new float[12], new float[8])); }
				if (!ber.isEmpty()) { quads.addAll(ber); flags |= BlockCodec.FLAG_CUTOUT; flags &= ~BlockCodec.FLAG_FULL_OPAQUE; }
				if (quads.size() > 255) quads = quads.subList(0, 255);
			}
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
		int lightRgb = 0;
		try {
			BakedModel model = mc.getBlockRenderer().getBlockModel(s);
			int rc = (flags & BlockCodec.FLAG_TRANSLUCENT) != 0 ? BlockProps.TRANSLUCENT : (flags & BlockCodec.FLAG_CUTOUT) != 0 ? BlockProps.CUTOUT : BlockProps.SOLID;
			if (BlockProps.shiny(s, rc, name, ov)) flags |= BlockCodec.FLAG_SHINY;
			if (ov != null && ov.has("light")) light = Math.max(0, Math.min(15, ov.get("light").getAsInt()));
			boolean emissive = ov != null && ov.has("emissive") ? ov.get("emissive").getAsBoolean() : light > 0 || BlockProps.emissiveQuads(s, model);
			if (ov != null && ov.has("lightColor")) lightRgb = Integer.parseInt(ov.get("lightColor").getAsString().replace("#", ""), 16);
			else if (light > 0) lightRgb = BlockProps.lightColorFromTexture(s, model);
			if (emissive && light == 0) light = 0x40; // glow only, no block light (BlockMesh: bit 0x40)
			if (!emissive) light = 0;
		} catch (Throwable t) { if (isShiny(name)) flags |= BlockCodec.FLAG_SHINY; lightRgb = lightColor(name); }
		if (quads != null && !quads.isEmpty()) flags |= BlockCodec.FLAG_QUADS;
		var key = net.minecraft.core.registries.BuiltInRegistries.BLOCK.getKey(s.getBlock());
		if (!key.getNamespace().equals("minecraft") && loggedModded.add(key.toString()))
			ValCraft.LOG.info("[blocks] {}: {}{}{}{}", key, (flags & BlockCodec.FLAG_TRANSLUCENT) != 0 ? "translucent" : (flags & BlockCodec.FLAG_CUTOUT) != 0 ? "cutout" : "solid",
				(flags & BlockCodec.FLAG_SHINY) != 0 ? ", shiny" : "", light > 0 ? ", light " + (light & 15) + ((light & 0x40) != 0 ? " (glow)" : "") : "", ov != null ? ", override" : "");
		outbox.add(BlockCodec.palette(id, flags, tiles, boxes, light, lightRgb));
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

	private static final java.util.Map<net.minecraft.world.level.block.Block, Boolean> liveBe = new java.util.concurrent.ConcurrentHashMap<>();
	/** 0.10.0: modded block entities with a renderer (Create shafts, cogs, water wheels, belts, presses...): captured from the real
	 *  in-world block entity per position (they need their level / neighbours / animation state), not from a dummy per state. */
	static boolean isLiveBe(Minecraft mc, BlockState s) {
		if (!s.hasBlockEntity() || !(s.getBlock() instanceof net.minecraft.world.level.block.EntityBlock eb)) return false;
		return liveBe.computeIfAbsent(s.getBlock(), b -> {
			var id = net.minecraft.core.registries.BuiltInRegistries.BLOCK.getKey(b);
			if (id == null || "minecraft".equals(id.getNamespace())) return false;
			try { var be = eb.newBlockEntity(BlockPos.ZERO, s); return be != null && mc.getBlockEntityRenderDispatcher().getRenderer(be) != null; }
			catch (Throwable t) { ValCraft.LOG.info("[blocks] live-BE probe {} failed: {}", id, t.toString()); return false; }
		});
	}

	static boolean isLidBlock(BlockState s) {
		var b = s.getBlock();
		return b instanceof net.minecraft.world.level.block.ChestBlock || b instanceof net.minecraft.world.level.block.EnderChestBlock || b instanceof net.minecraft.world.level.block.ShulkerBoxBlock;
	}

	/** Hand-built decorated pot (body + neck) from the vanilla pot textures, used if the renderer capture yields nothing. */
	private static java.util.List<BlockCodec.Quad> potFallback(Minecraft mc, BlockState s) {
		var out = new java.util.ArrayList<BlockCodec.Quad>();
		try {
			NativeImage side = NativeImage.read(mc.getResourceManager().open(net.minecraft.resources.ResourceLocation.withDefaultNamespace("textures/entity/decorated_pot/decorated_pot_side.png")));
			NativeImage base = NativeImage.read(mc.getResourceManager().open(net.minecraft.resources.ResourceLocation.withDefaultNamespace("textures/entity/decorated_pot/decorated_pot_base.png")));
			int ts = tileForRegion(side, "pot_side", 0, 0, side.getWidth(), side.getHeight(), -1);
			int tb = tileForRegion(base, "pot_base_top", 0, 0, Math.max(1, base.getWidth() / 2), Math.max(1, base.getHeight() / 2), -1);
			addBox(out, 1 / 16f, 0, 1 / 16f, 15 / 16f, 13 / 16f, 15 / 16f, ts, tb);
			addBox(out, 4 / 16f, 13 / 16f, 4 / 16f, 12 / 16f, 1f, 12 / 16f, tb, tb);
			side.close(); base.close();
		} catch (Exception ex) { ValCraft.LOG.warn("[blocks] decorated pot fallback failed: {}", ex.toString()); }
		return out;
	}

	private static void addBox(java.util.List<BlockCodec.Quad> out, float x0, float y0, float z0, float x1, float y1, float z1, int sideTile, int capTile) {
		float[] uv = { 0, 1, 1, 1, 1, 0, 0, 0 };
		out.add(new BlockCodec.Quad(capTile, 0, 6, new float[] { x0, y0, z0, x1, y0, z0, x1, y0, z1, x0, y0, z1 }, uv));
		out.add(new BlockCodec.Quad(capTile, 1, 6, new float[] { x0, y1, z0, x1, y1, z0, x1, y1, z1, x0, y1, z1 }, uv));
		out.add(new BlockCodec.Quad(sideTile, 2, 6, new float[] { x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0 }, uv));
		out.add(new BlockCodec.Quad(sideTile, 3, 6, new float[] { x0, y0, z1, x1, y0, z1, x1, y1, z1, x0, y1, z1 }, uv));
		out.add(new BlockCodec.Quad(sideTile, 4, 6, new float[] { x0, y0, z0, x0, y0, z1, x0, y1, z1, x0, y1, z0 }, uv));
		out.add(new BlockCodec.Quad(sideTile, 5, 6, new float[] { x1, y0, z0, x1, y0, z1, x1, y1, z1, x1, y1, z0 }, uv));
	}

	/** Geometry of a BlockEntityRenderer (chest, bed, sign, banner, skull, shulker box, decorated pot, bell, conduit, ...) for this state. */
	@SuppressWarnings({ "unchecked", "rawtypes" })
	private static java.util.List<BlockCodec.Quad> berQuads(Minecraft mc, BlockState s) {
		if (!s.hasBlockEntity() || !(s.getBlock() instanceof net.minecraft.world.level.block.EntityBlock eb)) return java.util.List.of();
		if (isLiveBe(mc, s)) return java.util.List.of(); // 0.10.0: drawn per position from the real block entity (EntitySync)
		for (int attempt = 0; attempt < 2; attempt++) {
			try {
				var be = eb.newBlockEntity(BlockPos.ZERO, s);
				if (be == null) return java.util.List.of();
				var r = (net.minecraft.client.renderer.blockentity.BlockEntityRenderer) mc.getBlockEntityRenderDispatcher().getRenderer(be);
				if (r == null) return java.util.List.of();
				if (attempt == 0 && mc.level != null) be.setLevel(mc.level); // chests need a level to use their ChestType
				Capture cap = new Capture();
				r.render(be, 0f, new com.mojang.blaze3d.vertex.PoseStack(), cap, 0xF000F0, net.minecraft.client.renderer.texture.OverlayTexture.NO_OVERLAY);
				var q = Capture.toTileQuads(cap.quads());
				if (q.isEmpty()) ValCraft.LOG.info("[blocks] block-entity renderer for {} drew no capturable quads (attempt {}: {} raw quads, level {})", s, attempt + 1, cap.quads().size(), attempt == 0 ? "set" : "none");
				if (!q.isEmpty() || attempt == 1) { if (!q.isEmpty()) ValCraft.LOG.info("[blocks] captured {} block-entity quads for {}", q.size(), s); return q; }
			} catch (Throwable t) { if (attempt == 1) ValCraft.LOG.warn("[blocks] block-entity capture for {} failed: {}", s, t.toString()); }
		}
		return java.util.List.of();
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
		boolean lava = fs.is(net.minecraft.tags.FluidTags.LAVA) || fs.getFluidType().getTemperature() >= 1000 || fs.getFluidType().getLightLevel() >= 10; // 0.14.0: any hot/glowing modded fluid
		int h = Math.max(1, Math.min(16, Math.round(fs.getOwnHeight() * 16)));
		int flags = BlockCodec.FLAG_FLUID | (lava ? 0 : BlockCodec.FLAG_TRANSLUCENT | BlockCodec.FLAG_SHINY);
		int light = 0; try { light = s.getLightEmission(EmptyBlockGetter.INSTANCE, BlockPos.ZERO); } catch (Throwable t) { }
		if (light == 0) light = fs.getFluidType().getLightLevel();
		if (lava && light == 0) light = 15;
		int frgb = lava ? 0xFF7A30 : 0;
		if (light > 0 && !fs.is(net.minecraft.tags.FluidTags.LAVA)) { var sp = atlasFn.apply(ext.getStillTexture()); frgb = avgColor(sp); }
		return BlockCodec.palette(id, flags, new int[] { still, still, flow, flow, flow, flow }, new byte[] { 0, 0, 0, 16, (byte) h, 16 }, light, frgb);
	}

	private static final java.util.Set<String> loggedModded = java.util.concurrent.ConcurrentHashMap.newKeySet();
	private static int avgColor(TextureAtlasSprite sp) {
		long r = 0, g = 0, b = 0; int n = 0; var img = sp.contents().getOriginalImage();
		for (int y = 0; y < sp.contents().height(); y += 2) for (int x = 0; x < sp.contents().width(); x += 2) { int c = img.getPixelRGBA(x, y); r += c & 0xFF; g += (c >> 8) & 0xFF; b += (c >> 16) & 0xFF; n++; }
		return n == 0 ? 0xFFFFFF : (int) (r / n) << 16 | (int) (g / n) << 8 | (int) (b / n);
	}

	private static int tileFor(TextureAtlasSprite sprite, int tint) {
		String key = sprite.contents().name() + "#" + tint;
		Integer t = tileOf.get(key);
		if (t != null) return t;
		if (nextTile >= ATLAS_TILES) { if (!atlasFullWarned) { atlasFullWarned = true; ValCraft.LOG.warn("[blocks] atlas full ({} tiles): new textures fall back to tile 0", ATLAS_TILES); } return 0; }
		int tile = nextTile++;
		tileOf.put(key, tile);
		NativeImage src = sprite.contents().getOriginalImage();
		int w = sprite.contents().width(), hgt = sprite.contents().height();
		blit(src, 0, 0, w, hgt, tile, tint);
		// animated sprite: frames into extra tiles, Valheim copies the current one into `tile`
		try {
			Object anim = animF().get(sprite.contents());
			if (anim != null) {
				java.util.List<?> frames = (java.util.List<?>) framesF().get(anim);
				int rowSize = rowF().getInt(anim);
				if (frames.size() > 1) {
					int n = Math.min(frames.size(), 61);
					int[] ft = new int[n], ticks = new int[n];
					Map<Integer, Integer> byIndex = new HashMap<>();
					for (int i = 0; i < n && nextTile < ATLAS_TILES; i++) {
						Object f = frames.get(i);
						int idx = idxF().getInt(f);
						ticks[i] = timeF().getInt(f);
						Integer t2 = byIndex.get(idx);
						if (t2 == null) { t2 = nextTile++; byIndex.put(idx, t2); blit(src, (idx % rowSize) * w, (idx / rowSize) * hgt, w, hgt, t2, tint); }
						ft[i] = t2;
					}
					outbox.add(BlockCodec.anim(tile, ft, ticks));
				}
			}
		} catch (Throwable ex) { if (!animWarned) { animWarned = true; ValCraft.LOG.warn("[blocks] sprite animation lookup unavailable: {}", ex.toString()); } }
		return tile;
	}

	private static boolean animWarned;
	private static java.lang.reflect.Field fAnim, fFrames, fRow, fIdx, fTime;
	private static java.lang.reflect.Field acc(java.lang.reflect.Field f) { f.setAccessible(true); return f; }
	private static java.lang.reflect.Field animF() throws Exception { return fAnim != null ? fAnim : (fAnim = acc(net.minecraft.client.renderer.texture.SpriteContents.class.getDeclaredField("animatedTexture"))); }
	private static java.lang.reflect.Field framesF() throws Exception { return fFrames != null ? fFrames : (fFrames = acc(Class.forName("net.minecraft.client.renderer.texture.SpriteContents$AnimatedTexture").getDeclaredField("frames"))); }
	private static java.lang.reflect.Field rowF() throws Exception { return fRow != null ? fRow : (fRow = acc(Class.forName("net.minecraft.client.renderer.texture.SpriteContents$AnimatedTexture").getDeclaredField("frameRowSize"))); }
	private static java.lang.reflect.Field idxF() throws Exception { return fIdx != null ? fIdx : (fIdx = acc(Class.forName("net.minecraft.client.renderer.texture.SpriteContents$FrameInfo").getDeclaredField("index"))); }
	private static java.lang.reflect.Field timeF() throws Exception { return fTime != null ? fTime : (fTime = acc(Class.forName("net.minecraft.client.renderer.texture.SpriteContents$FrameInfo").getDeclaredField("time"))); }

	private static void ensureAtlas() {
		if (atlas == null) {
			atlas = new NativeImage(ATLAS_PER_ROW * ATLAS_TILE, ATLAS_PER_ROW * ATLAS_TILE, true);
			for (int y = 0; y < ATLAS_TILE; y++) for (int x = 0; x < ATLAS_TILE; x++) atlas.setPixelRGBA(x, y, ((x ^ y) & 4) != 0 ? 0xFFFF00FF : 0xFF000000); // tile 0 = missing
		}
	}

	/** Copy (nearest-resampled) a source pixel rectangle into a tile, multiplying by tint (0xRRGGBB, -1 = none). */
	private static void blit(NativeImage src, int sx, int sy, int sw, int sh, int tile, int tint) {
		ensureAtlas();
		int ox = (tile % ATLAS_PER_ROW) * ATLAS_TILE, oy = (tile / ATLAS_PER_ROW) * ATLAS_TILE;
		int tr = (tint >> 16) & 0xFF, tg = (tint >> 8) & 0xFF, tb = tint & 0xFF;
		for (int y = 0; y < ATLAS_TILE; y++) for (int x = 0; x < ATLAS_TILE; x++) {
			int px = Math.min(src.getWidth() - 1, sx + x * sw / ATLAS_TILE), py = Math.min(src.getHeight() - 1, sy + y * sh / ATLAS_TILE);
			int p = src.getPixelRGBA(px, py); // ABGR
			if (tint != -1) {
				int r = (p & 0xFF) * tr / 255, g = ((p >> 8) & 0xFF) * tg / 255, b = ((p >> 16) & 0xFF) * tb / 255;
				p = (p & 0xFF000000) | (b << 16) | (g << 8) | r;
			}
			atlas.setPixelRGBA(ox + x, oy + y, p);
		}
		atlasDirty = true;
	}

	/** Tile for an arbitrary image region (captured entity / block-entity textures). */
	private static boolean atlasFullWarned;
	static int tileForRegion(NativeImage img, String key, int x, int y, int w, int h, int tint) {
		String k = key + "#" + tint;
		Integer t = tileOf.get(k);
		if (t != null) return t;
		if (nextTile >= ATLAS_TILES) { if (!atlasFullWarned) { atlasFullWarned = true; ValCraft.LOG.warn("[blocks] atlas full ({} tiles): new textures fall back to tile 0", ATLAS_TILES); } return 0; }
		int tile = nextTile++;
		tileOf.put(k, tile);
		blit(img, x, y, w, h, tile, tint);
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
