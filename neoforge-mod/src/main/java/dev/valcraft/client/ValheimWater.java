package dev.valcraft.client;

import dev.valcraft.link.MobCodec;
import net.minecraft.client.Minecraft;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.phys.Vec3;

import java.util.concurrent.ConcurrentHashMap;

/**
 * 0.9.0/0.9.1: Valheim water/tar surface (EV_WATER), now as 18 m tiles kept in a map (about ±36 m around the player).
 * Single-player: one static grid read by the water mixins on client and server threads, for every entity (players, mobs, items, boats).
 */
public final class ValheimWater {
	private ValheimWater() {}
	private static final long MAX_AGE_MS = 3000, DRY_AGE_MS = 15000;
	private static final ConcurrentHashMap<Long, MobCodec.Water> tiles = new ConcurrentHashMap<>();

	private static long key(int tx, int tz) { return ((long) tx << 32) ^ (tz & 0xFFFFFFFFL); }
	private static int tile(double v) { return (int) Math.floor(v / MobCodec.TILE_SIZE); }

	static void receive(byte[] p) {
		var w = MobCodec.decodeWater(p, System.currentTimeMillis());
		tiles.put(key(tile(w.x0() + 0.5), tile(w.z0() + 0.5)), w);
	}
	static void clear() { tiles.clear(); }

	private static boolean dry(MobCodec.Water w) { for (float f : w.surface()) if (!Float.isNaN(f)) return false; return true; }

	private static MobCodec.Water at(double x, double z) {
		var w = tiles.get(key(tile(x), tile(z)));
		if (w == null) return null;
		long age = System.currentTimeMillis() - w.time();
		// dry tiles are sent once and then skipped by Valheim, so they stay valid longer
		if (age > (dry(w) ? DRY_AGE_MS : MAX_AGE_MS)) return null;
		return w;
	}

	/** Valheim water surface (MC Y) at this entity's column, NaN if none; tar doesn't count. */
	public static double waterSurface(Entity e) { return waterSurface(e.getX(), e.getZ()); }
	public static double waterSurface(double x, double z) {
		if (tiles.isEmpty()) return Double.NaN;
		var w = at(x, z);
		if (w == null || w.tarAt(x, z)) return Double.NaN;
		return w.surfaceAt(x, z);
	}

	/** Client tick: Valheim tar slows the local player. */
	static void tick(Minecraft mc) {
		var p = mc.player; if (p == null) return;
		var g = at(p.getX(), p.getZ()); if (g == null) return;
		float s = g.surfaceAt(p.getX(), p.getZ());
		if (g.tarAt(p.getX(), p.getZ()) && !Float.isNaN(s) && s > p.getY()) p.makeStuckInBlock(Blocks.COBWEB.defaultBlockState(), new Vec3(0.45, 0.6, 0.45));
	}
}
