package dev.valcraft.client;

import dev.valcraft.link.ValCraftLink;
import it.unimi.dsi.fastutil.longs.Long2ObjectOpenHashMap;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.shapes.Shapes;
import net.minecraft.world.phys.shapes.VoxelShape;

import java.util.ArrayList;
import java.util.List;

/**
 * Milestone 5: Valheim geometry (building pieces, rocks, trees, terrain samples) as invisible collision boxes for players.
 * Snapshot-swapped (immutable index behind a volatile), so the integrated server thread can read it too.
 */
public final class CollisionWorld {
	private CollisionWorld() {}

	private record Index(Long2ObjectOpenHashMap<List<VoxelShape>> cells, int boxes) {}
	private static final int CELL_SHIFT = 2; // 4-block buckets
	private static volatile Index index;
	private static int lastSnapshot = -1;
	/** Ignore Minecraft's own blocks for players while linked (Valheim is the world). -Dvalcraft.ignoreMcBlocks=false to keep them. */
	public static final boolean IGNORE_MC_BLOCKS = !"false".equals(System.getProperty("valcraft.ignoreMcBlocks"));

	public static boolean ready() { return index != null; }
	public static int boxCount() { Index i = index; return i == null ? 0 : i.boxes; }

	static void clear() { index = null; lastSnapshot = -1; }

	/** Client tick: pick up a new snapshot if Valheim published one. */
	static void poll(ValCraftLink link) {
		int n = link.collisionSnapshotCount();
		if (n == 0 || n == lastSnapshot) return;
		float[] b = link.readCollision();
		if (b == null) return;
		lastSnapshot = n;
		var cells = new Long2ObjectOpenHashMap<List<VoxelShape>>();
		int count = b.length / 6;
		for (int i = 0; i < count; i++) {
			int o = i * 6;
			if (!(b[o + 3] > b[o]) || !(b[o + 4] > b[o + 1]) || !(b[o + 5] > b[o + 2])) continue;
			VoxelShape s = Shapes.create(b[o], b[o + 1], b[o + 2], b[o + 3], b[o + 4], b[o + 5]);
			int x0 = (int) Math.floor(b[o]) >> CELL_SHIFT, x1 = (int) Math.floor(b[o + 3]) >> CELL_SHIFT;
			int z0 = (int) Math.floor(b[o + 2]) >> CELL_SHIFT, z1 = (int) Math.floor(b[o + 5]) >> CELL_SHIFT;
			for (int x = x0; x <= x1; x++)
				for (int z = z0; z <= z1; z++)
					cells.computeIfAbsent(key(x, z), k -> new ArrayList<>(4)).add(s);
		}
		index = new Index(cells, count);
	}

	private static long key(int x, int z) { return ((long) x << 32) ^ (z & 0xFFFFFFFFL); }

	public static boolean appliesTo(Entity e) { return e instanceof Player && ValCraftClient.collisionActive(); }

	/** 0.10.0: Valheim structures are solid for every MC entity (mobs, items, boats...), not just players; MC blocks stay solid for non-players. */
	public static boolean solidFor(Entity e) {
		if (index == null || !ValCraftClient.collisionActive()) return false;
		if (e instanceof Player) return true;
		return !(e instanceof dev.valcraft.mobs.ValheimProxy) && !e.noPhysics;
	}

	/** Allocation-free: any Valheim box intersecting [x0,x1]x[y0,y1]x[z0,z1]? (pathfinding) */
	public static boolean occupied(double x0, double y0, double z0, double x1, double y1, double z1) {
		Index idx = index;
		if (idx == null) return false;
		int cx0 = (int) Math.floor(x0) >> CELL_SHIFT, cx1 = (int) Math.floor(x1) >> CELL_SHIFT;
		int cz0 = (int) Math.floor(z0) >> CELL_SHIFT, cz1 = (int) Math.floor(z1) >> CELL_SHIFT;
		for (int x = cx0; x <= cx1; x++)
			for (int z = cz0; z <= cz1; z++) {
				List<VoxelShape> l = idx.cells.get(key(x, z));
				if (l == null) continue;
				for (int i = 0; i < l.size(); i++) {
					AABB b = l.get(i).bounds();
					if (b.minX < x1 && b.maxX > x0 && b.minY < y1 && b.maxY > y0 && b.minZ < z1 && b.maxZ > z0) return true;
				}
			}
		return false;
	}

	/** Shapes overlapping the query box (may contain duplicates' identity once each). */
	public static List<VoxelShape> query(AABB q) {
		Index idx = index;
		if (idx == null) return List.of();
		List<VoxelShape> out = new ArrayList<>();
		java.util.Set<VoxelShape> seen = java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<>());
		int x0 = (int) Math.floor(q.minX) >> CELL_SHIFT, x1 = (int) Math.floor(q.maxX) >> CELL_SHIFT;
		int z0 = (int) Math.floor(q.minZ) >> CELL_SHIFT, z1 = (int) Math.floor(q.maxZ) >> CELL_SHIFT;
		for (int x = x0; x <= x1; x++)
			for (int z = z0; z <= z1; z++) {
				List<VoxelShape> l = idx.cells.get(key(x, z));
				if (l == null) continue;
				for (VoxelShape s : l) if (seen.add(s) && s.bounds().intersects(q)) out.add(s);
			}
		return out;
	}

	/** 0.10.1: nearest point where the segment a->b enters a Valheim box (line of sight, projectiles), or null. Any thread. */
	public static net.minecraft.world.phys.Vec3 clip(net.minecraft.world.phys.Vec3 a, net.minecraft.world.phys.Vec3 b) {
		if (index == null || !ValCraftClient.collisionActive()) return null;
		net.minecraft.world.phys.Vec3 best = null; double bd = Double.MAX_VALUE;
		for (VoxelShape s : query(new AABB(a, b).inflate(0.01)))
			for (AABB box : s.toAabbs()) {
				if (box.contains(a)) continue; // starting inside (e.g. shooter in a doorway box): ignore that box
				var h = box.clip(a, b);
				if (h.isPresent()) { double d = h.get().distanceToSqr(a); if (d < bd) { bd = d; best = h.get(); } }
			}
		return best;
	}

	/** Combine an MC clip result with Valheim boxes: the nearer wins (Valheim hit = block hit on air at that point). */
	public static net.minecraft.world.phys.HitResult clip(net.minecraft.world.level.ClipContext ctx, net.minecraft.world.phys.BlockHitResult mc) {
		var a = ctx.getFrom(); var h = clip(a, ctx.getTo());
		if (h == null) return mc;
		if (mc.getType() != net.minecraft.world.phys.HitResult.Type.MISS && mc.getLocation().distanceToSqr(a) <= h.distanceToSqr(a)) return mc;
		var d = h.subtract(a);
		var face = net.minecraft.core.Direction.getNearest(-d.x, -d.y, -d.z);
		return new net.minecraft.world.phys.BlockHitResult(h, face, net.minecraft.core.BlockPos.containing(h), false);
	}

	/** 0.12.0: a Valheim box fills a real part of this block cell (fluids must not flow there). Any thread. */
	public static boolean occupies(net.minecraft.core.BlockPos p) {
		if (index == null || !ValCraftClient.collisionActive()) return false;
		var cell = new AABB(p).deflate(0.2);
		for (VoxelShape s : query(cell)) for (AABB b : s.toAabbs()) if (b.intersects(cell)) return true;
		return false;
	}
	/** 0.12.0: a Valheim box touches this point (stuck arrows keep sticking). Any thread. */
	public static boolean near(net.minecraft.world.phys.Vec3 v, double r) {
		if (index == null || !ValCraftClient.collisionActive()) return false;
		var q = new AABB(v, v).inflate(r);
		for (VoxelShape s : query(q)) for (AABB b : s.toAabbs()) if (b.intersects(q)) return true;
		return false;
	}
}
