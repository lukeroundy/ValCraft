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
}
