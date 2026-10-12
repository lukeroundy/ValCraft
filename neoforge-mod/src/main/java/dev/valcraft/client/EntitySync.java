package dev.valcraft.client;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.valcraft.ValCraft;
import dev.valcraft.link.BlockCodec;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.texture.OverlayTexture;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.decoration.ArmorStand;
import net.minecraft.world.entity.decoration.ItemFrame;
import net.minecraft.world.entity.item.FallingBlockEntity;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.entity.item.PrimedTnt;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.entity.vehicle.AbstractMinecart;
import net.minecraft.world.entity.vehicle.Boat;
import net.minecraft.world.item.ItemDisplayContext;
import net.minecraft.world.level.block.Block;

/**
 * 0.7.3: nearby non-player entities (dropped items, armor stands, item frames, falling blocks, primed TNT, minecarts,
 * boats) for Valheim. Geometry is captured once per "look" (type + rotation bucket + equipment/item/state) with the
 * same capture as block entities and cached by mesh id; every 100 ms a full snapshot of (entity, mesh, position) is
 * sent. Dropped items are captured static; Valheim spins/bobs them. Render thread (called from BlockSync).
 */
final class EntitySync {
	private EntitySync() {}
	static volatile int maxEntities = 64, maxMobs = 24;
	/** 0.12.0: animation capture at 24 fps near the player (LOD: 12 fps to 32 blocks, 4 fps beyond), time + IPC backlog budget. */
	static final long FRAME_MS = 41;
	static final long CAPTURE_BUDGET_NS = 6_000_000L;
	static final int OUTBOX_BACKLOG = 300;
	static final int MAX_LID_BLOCKS = 96, DYNAMIC_MESH = 0x40000000, BLOCK_ENTITY_ID = 0x80000000;
	private static final java.util.HashMap<Integer, Long> lastMobCapture = new java.util.HashMap<>();
	private static final Map<String, Integer> meshOf = new HashMap<>();
	private static int nextMesh = 1;
	private static long nextUpdate;

	static void reset() { meshOf.clear(); nextMesh = 1; lastMobCapture.clear(); liveMesh.clear(); liveLast.clear(); }

	static void tick(Minecraft mc, java.util.ArrayDeque<dev.valcraft.link.ValCraftLink.Event> outbox) {
		outboxRef = outbox;
		long now = System.currentTimeMillis();
		if (now < nextUpdate || mc.level == null || mc.player == null || maxEntities <= 0) return;
		nextUpdate = now + FRAME_MS;
		double r = BlockSync.radius, r2 = r * r;
		List<Entity> list = new ArrayList<>();
		List<Entity> mobs = new ArrayList<>();
		for (Entity e : mc.level.entitiesForRendering()) {
			if (e == mc.player || e.distanceToSqr(mc.player) > r2) continue;
			if (e instanceof net.minecraft.world.entity.LivingEntity le && !(e instanceof ArmorStand)) { if (!e.isInvisible()) mobs.add(le); continue; }
			if (e.getClass().getName().contains("Contraption")) { mobs.add(e); continue; } // 0.10.0: Create contraptions move/rotate: dynamic mesh
			if (!wanted(e) || e.isInvisible() && !(e instanceof ArmorStand)) continue;
			list.add(e);
		}
		list.sort((a, b) -> Double.compare(a.distanceToSqr(mc.player), b.distanceToSqr(mc.player)));
		if (list.size() > maxEntities) list = list.subList(0, maxEntities);
		List<BlockCodec.Ent> ents = new ArrayList<>();
		int captures = 0;
		for (Entity e : list) {
			String key = key(e);
			Integer mesh = meshOf.get(key);
			if (mesh == null) {
				if (captures++ >= 4) continue; // spread capture cost over frames
				mesh = nextMesh++;
				meshOf.put(key, mesh);
				var q = capture(mc, e);
				outbox.addAll(BlockCodec.entityMesh(mesh, q));
				if (meshOf.size() <= 20 || meshOf.size() % 50 == 0) ValCraft.LOG.info("[entities] mesh {} for {} ({} quads)", mesh, key, q.size());
			}
			var p = e.position();
			var en = new BlockCodec.Ent(e.getId(), mesh, (float) p.x, (float) p.y, (float) p.z, e instanceof ItemEntity ? 1 : 0);
			ents.add(e instanceof net.minecraft.world.entity.projectile.Projectile ? en : ground(mc, e, en)); // projectiles: exact position (stuck arrows)
		}
		// mobs / NPCs / other players: re-captured every frame-ish (nearest 100 ms, farther 300 ms) into a per-entity dynamic mesh
		mobs.sort((a, b) -> Double.compare(a.distanceToSqr(mc.player), b.distanceToSqr(mc.player)));
		// 0.10.1: third person: our own player is a captured in-world mesh (depth-tested in Valheim) instead of an overlay
		boolean self = mc.gameRenderer.getMainCamera().isDetached() && !mc.player.isInvisible();
		if (self) mobs.add(0, mc.player);
		if (mobs.size() > maxMobs) mobs = mobs.subList(0, maxMobs);
		java.util.HashSet<Integer> live = new java.util.HashSet<>();
		int mobCaps = 0; long t0 = System.nanoTime();
		for (var e : mobs) {
			int mesh = DYNAMIC_MESH | (e.getId() & 0x3FFFFFFF);
			live.add(e.getId());
			Long last = lastMobCapture.get(e.getId());
			double md2 = e.distanceToSqr(mc.player);
			long interval = e == mc.player || md2 < 256 ? FRAME_MS : md2 < 1024 ? 2 * FRAME_MS : 250;
			boolean budget = System.nanoTime() - t0 < CAPTURE_BUDGET_NS && outbox.size() < OUTBOX_BACKLOG;
			if ((last == null || now - last >= interval - 5) && mobCaps < 16 && (budget || last == null)) {
				mobCaps++;
				lastMobCapture.put(e.getId(), now);
				Capture.CAPTURING = true;
				List<BlockCodec.Quad> q;
				try { q = capture(mc, e); } finally { Capture.CAPTURING = false; }
				if (!q.isEmpty()) outbox.addAll(BlockCodec.entityMesh(mesh, q));
				if (last == null && lastMobCapture.size() <= 10) ValCraft.LOG.info("[entities] mob {} ({} quads)", e.getType().getDescriptionId(), q.size());
				if (last == null && DUMP != null) dump(e, q);
			}
			if (!lastMobCapture.containsKey(e.getId())) continue;
			var p = e.position();
			ents.add(ground(mc, e, new BlockCodec.Ent(e.getId(), mesh, (float) p.x, (float) p.y, (float) p.z, e == mc.player ? 2 | ENT_LOCAL_PLAYER : 2)));
		}
		lastMobCapture.keySet().retainAll(live);
		// chests / ender chests / shulker boxes: live block entities, mesh per (state, lid openness bucket)
		lidBlocks(mc, ents);
		liveBlocks(mc, ents, now);
		outbox.addAll(BlockCodec.entityUpdate(ents));
	}

	@SuppressWarnings({ "unchecked", "rawtypes" })
	private static void lidBlocks(Minecraft mc, List<BlockCodec.Ent> ents) {
		var pp = mc.player.blockPosition();
		int rc = (BlockSync.radius >> 4) + 1, n = 0, caps = 0;
		for (int cx = (pp.getX() >> 4) - rc; cx <= (pp.getX() >> 4) + rc; cx++)
			for (int cz = (pp.getZ() >> 4) - rc; cz <= (pp.getZ() >> 4) + rc; cz++) {
				var chunk = mc.level.getChunkSource().getChunk(cx, cz, false);
				if (chunk == null) continue;
				for (var be : chunk.getBlockEntities().values()) {
					var st = be.getBlockState();
					if (!BlockSync.isLidBlock(st) || n >= MAX_LID_BLOCKS) continue;
					var pos = be.getBlockPos();
					if (pos.distSqr(pp) > (double) BlockSync.radius * BlockSync.radius) continue;
					float open = 0;
					if (be instanceof net.minecraft.world.level.block.entity.LidBlockEntity lid) open = lid.getOpenNess(1f);
					else if (be instanceof net.minecraft.world.level.block.entity.ShulkerBoxBlockEntity sb) open = sb.getProgress(1f);
					String key = "lid|" + Block.getId(st) + "|" + Math.round(open * 8);
					Integer mesh = meshOf.get(key);
					if (mesh == null) {
						if (caps++ >= 6) continue;
						mesh = nextMesh++;
						meshOf.put(key, mesh);
						Capture cap = new Capture();
						try {
							var r = (net.minecraft.client.renderer.blockentity.BlockEntityRenderer) mc.getBlockEntityRenderDispatcher().getRenderer(be);
							if (r != null) r.render(be, 1f, new PoseStack(), cap, 0xF000F0, OverlayTexture.NO_OVERLAY);
						} catch (Throwable t) { ValCraft.LOG.warn("[entities] lid block capture {} failed: {}", st, t.toString()); }
						var q = Capture.toTileQuads(cap.quads());
						outbox().addAll(BlockCodec.entityMesh(mesh, q));
					}
					n++;
					ents.add(new BlockCodec.Ent(BLOCK_ENTITY_ID | (pos.hashCode() & 0x7FFFFFFF), mesh, pos.getX(), pos.getY(), pos.getZ(), 0));
				}
			}
	}

	// 0.10.0: modded block entities (Create kinetics etc.) from the real BE per position, re-captured while animating
	private static final java.util.HashMap<Long, int[]> liveMesh = new java.util.HashMap<>(); // pos -> {mesh, lastCaptureMs low bits}
	private static final java.util.HashMap<Long, Long> liveLast = new java.util.HashMap<>();
	private static int liveLogged;
	private static final java.util.Map<Long, int[]> liveQuads = new java.util.HashMap<>();
	@SuppressWarnings({ "unchecked", "rawtypes" })
	private static void liveBlocks(Minecraft mc, List<BlockCodec.Ent> ents, long now) {
		var pp = mc.player.blockPosition();
		int rad = Math.min(BlockSync.radius, 48), rc = (rad >> 4) + 1, caps = 0, n = 0;
		var seen = new java.util.HashSet<Long>();
		float pt = mc.getTimer().getGameTimeDeltaPartialTick(false);
		for (int cx = (pp.getX() >> 4) - rc; cx <= (pp.getX() >> 4) + rc; cx++)
			for (int cz = (pp.getZ() >> 4) - rc; cz <= (pp.getZ() >> 4) + rc; cz++) {
				var chunk = mc.level.getChunkSource().getChunk(cx, cz, false);
				if (chunk == null) continue;
				for (var be : chunk.getBlockEntities().values()) {
					var st = be.getBlockState();
					if (n >= 160 || !BlockSync.isLiveBe(mc, st)) continue;
					var pos = be.getBlockPos();
					double d2 = pos.distSqr(pp);
					if (d2 > (double) rad * rad) continue;
					long pk = pos.asLong(); seen.add(pk); n++;
					int[] m = liveMesh.get(pk);
					if (m == null) { m = new int[] { DYNAMIC_MESH | 0x20000000 | (nextMesh++ & 0x1FFFFFFF) }; liveMesh.put(pk, m); }
					boolean kinetic = isKinetic(be);
					int[] qc = liveQuads.get(pk);
					long interval = kinetic ? (qc != null && qc[0] > 300 ? 1500 : d2 < 256 ? FRAME_MS : d2 < 1024 ? 2 * FRAME_MS : 400) : 2000; // 0.10.1: big models (water wheels, 1000+ quads) ~0.7 Hz: they flooded the IPC ring
					Long last = liveLast.get(pk);
					if ((last == null || now - last >= interval - 5) && caps < 10 && (last == null || outbox().size() < OUTBOX_BACKLOG)) {
						caps++; liveLast.put(pk, now);
						Capture cap = new Capture();
						Capture.CAPTURING = true;
						try {
							var r = (net.minecraft.client.renderer.blockentity.BlockEntityRenderer) mc.getBlockEntityRenderDispatcher().getRenderer(be);
							if (r != null) r.render(be, pt, new PoseStack(), cap, 0xF000F0, OverlayTexture.NO_OVERLAY);
						} catch (Throwable t) { if (liveLogged++ < 20) ValCraft.LOG.warn("[entities] block entity {} capture failed: {}", st, t.toString()); }
						finally { Capture.CAPTURING = false; }
						var q = Capture.toTileQuads(cap.quads());
						if (last == null && liveLogged++ < 20) ValCraft.LOG.info("[entities] live block entity {} at {}: {} quads{}", st.getBlock(), pos, q.size(), kinetic ? " (kinetic, re-captured)" : "");
						outbox().addAll(BlockCodec.entityMesh(m[0], q));
						liveQuads.put(pk, new int[] { q.size() });
					}
					if (liveLast.containsKey(pk)) ents.add(new BlockCodec.Ent(BLOCK_ENTITY_ID | 0x40000000 | ((int) (pk ^ (pk >>> 32)) & 0x3FFFFFFF), m[0], pos.getX(), pos.getY(), pos.getZ(), 0));
				}
			}
		liveMesh.keySet().retainAll(seen); liveLast.keySet().retainAll(seen); liveQuads.keySet().retainAll(seen);
	}
	private static final java.util.Map<Class<?>, Boolean> kineticCls = new java.util.concurrent.ConcurrentHashMap<>();
	private static boolean isKinetic(Object be) {
		return kineticCls.computeIfAbsent(be.getClass(), c -> { for (var k = c; k != null; k = k.getSuperclass()) if (k.getSimpleName().contains("Kinetic") || k.getSimpleName().contains("Animated")) return true; return false; });
	}

	private static java.util.ArrayDeque<dev.valcraft.link.ValCraftLink.Event> outboxRef;
	private static java.util.ArrayDeque<dev.valcraft.link.ValCraftLink.Event> outbox() { return outboxRef; }

	/** 0.7.5: if the entity stands on mirrored terrain (terrain-type block under its feet), tell Valheim the MC ground Y so it can
	 *  re-seat the entity on Valheim's smooth ground. Not for player-placed blocks (rendered as blocks in Valheim). */
	static BlockCodec.Ent ground(Minecraft mc, Entity e, BlockCodec.Ent en) {
		if (!e.onGround() && !(e instanceof ItemEntity && e.getDeltaMovement().lengthSqr() < 1e-3)) {
			// airborne / swimming: still report the terrain below (within 3 blocks) so Valheim keeps the same offset
			var bp = e.blockPosition();
			for (int dy = 1; dy <= 3; dy++) { var s = mc.level.getBlockState(bp.below(dy)); if (!s.isAir()) return TerrainSync.isTerrainBlock(s) ? withGround(en, bp.getY() - dy + 1, 0) : en; }
			return en;
		}
		var below = net.minecraft.core.BlockPos.containing(e.getX(), e.getY() - 0.2, e.getZ());
		var s = mc.level.getBlockState(below);
		if (!TerrainSync.isTerrainBlock(s)) return en;
		return withGround(en, below.getY() + 1, BlockCodec.ENT_ON_TERRAIN);
	}
	private static BlockCodec.Ent withGround(BlockCodec.Ent en, float gy, int extra) { return new BlockCodec.Ent(en.id(), en.meshId(), en.x(), en.y(), en.z(), en.flags() | 8 | extra, gy); }

	static boolean wanted(Entity e) {
		return e instanceof ItemEntity || e instanceof ArmorStand || e instanceof ItemFrame || e instanceof FallingBlockEntity || e instanceof PrimedTnt
				|| e instanceof AbstractMinecart || e instanceof Boat
				|| e instanceof net.minecraft.world.entity.projectile.Projectile; // 0.12.0: arrows (flying + stuck), tridents, thrown items
	}

	private static int bucket(float deg) { return Math.round(((deg % 360) + 360) % 360 / 5f); }

	static String key(Entity e) {
		String t = BuiltInRegistries.ENTITY_TYPE.getKey(e.getType()).toString();
		if (e instanceof ItemEntity ie) return t + "|" + ie.getItem().getItem() + "|" + ie.getItem().getComponentsPatch().hashCode();
		if (e instanceof FallingBlockEntity fb) return t + "|" + Block.getId(fb.getBlockState());
		if (e instanceof PrimedTnt tnt) return t + "|" + Math.min(10, tnt.getFuse() / 4);
		StringBuilder k = new StringBuilder(t).append('|').append(bucket(e.getYRot())).append('|').append(bucket(e.getXRot()));
		if (e instanceof ArmorStand as) {
			k.append('|').append(bucket(as.yBodyRot)).append(as.isSmall()).append(as.isShowArms()).append(as.isNoBasePlate());
			for (EquipmentSlot sl : EquipmentSlot.values()) k.append(',').append(as.getItemBySlot(sl).getItem());
			k.append(as.getHeadPose()).append(as.getBodyPose()).append(as.getLeftArmPose()).append(as.getRightArmPose()).append(as.getLeftLegPose()).append(as.getRightLegPose());
		}
		if (e instanceof ItemFrame f) k.append('|').append(f.getDirection()).append(f.getItem().getItem()).append(f.getRotation());
		if (e instanceof AbstractMinecart m) k.append('|').append(Block.getId(m.getDisplayBlockState()));
		return k.toString();
	}

	static final int ENT_LOCAL_PLAYER = 16;
	private static final String DUMP = System.getenv("VALCRAFT_CAPDUMP");
	private static final java.util.Set<String> dumped = new java.util.HashSet<>();
	private static void dump(Entity e, List<BlockCodec.Quad> q) {
		String n = BuiltInRegistries.ENTITY_TYPE.getKey(e.getType()).getPath();
		if (!dumped.add(n)) return;
		try (var w = new java.io.PrintWriter(DUMP + "/" + n + ".txt")) {
			for (var x : q) { w.print(x.tile() + " " + x.dir()); for (float f : x.pos()) w.print(" " + f); for (float f : x.uv()) w.print(" " + f); w.println(); }
		} catch (Exception ex) { ValCraft.LOG.warn("dump failed {}", ex.toString()); }
	}

	@SuppressWarnings({ "unchecked", "rawtypes" })
	static List<BlockCodec.Quad> capture(Minecraft mc, Entity e) {
		Capture cap = new Capture();
		try {
			PoseStack ps = new PoseStack();
			if (e instanceof ItemEntity ie) {
				ps.translate(0, 0.25, 0);
				mc.getItemRenderer().renderStatic(ie.getItem(), ItemDisplayContext.GROUND, 0xF000F0, OverlayTexture.NO_OVERLAY, ps, cap, mc.level, 0);
			} else {
				var r = (net.minecraft.client.renderer.entity.EntityRenderer) mc.getEntityRenderDispatcher().getRenderer(e);
				r.render(e, e.getYRot(), 1f, ps, cap, 0xF000F0);
			}
		} catch (Throwable t) { ValCraft.LOG.warn("[entities] capture of {} failed: {}", e, t.toString()); }
		return Capture.toTileQuads(cap.quads());
	}
}
