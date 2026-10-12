package dev.valcraft.client;

import dev.valcraft.link.ValCraftLink;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.concurrent.ConcurrentHashMap;

/**
 * 0.5.1: Valheim's true (smooth) ground height for movement. Terrain blocks stay in the world for mining/raycasts,
 * but the linked player's movement ignores them (except anything sticking out above this surface, e.g. placed
 * blocks) and is carried by a bilinear heightfield of 0.5 m samples instead, so 1-block ledges from small bumps
 * don't exist for movement. Read by the client and the integrated server thread.
 */
public final class HeightField {
	private HeightField() {}
	static final int C = ValCraftLink.HEIGHT_CELL, R = ValCraftLink.HEIGHT_RES;
	static final float STEP = (float) C / (R - 1);
	private static final ConcurrentHashMap<Long, float[]> cells = new ConcurrentHashMap<>();

	static void clear() { cells.clear(); }
	public static boolean active() { return TerrainSync.enabled() && !cells.isEmpty(); }

	static void receive(byte[] p) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int cx = b.getInt(0), cz = b.getInt(4);
		float base = b.getFloat(8);
		float[] h = new float[R * R];
		for (int i = 0; i < R * R; i++) { short v = b.getShort(12 + i * 2); h[i] = v == Short.MIN_VALUE ? Float.NaN : base + v / 100f; }
		cells.put(key(cx, cz), h);
	}

	private static long key(int x, int z) { return ((long) x << 32) ^ (z & 0xFFFFFFFFL); }

	/** Ground height at MC (x,z), or NaN if unknown. */
	public static double height(double x, double z) {
		int cx = Math.floorDiv((int) Math.floor(x), C), cz = Math.floorDiv((int) Math.floor(z), C);
		float[] h = cells.get(key(cx, cz));
		if (h == null) return Double.NaN;
		double fx = (x - cx * C) / STEP, fz = (z - cz * C) / STEP;
		int i = Math.min(R - 2, Math.max(0, (int) Math.floor(fx))), j = Math.min(R - 2, Math.max(0, (int) Math.floor(fz)));
		double u = fx - i, v = fz - j;
		float a = h[i * R + j], bb = h[(i + 1) * R + j], c = h[i * R + j + 1], d = h[(i + 1) * R + j + 1];
		if (Float.isNaN(a) || Float.isNaN(bb) || Float.isNaN(c) || Float.isNaN(d)) return Double.NaN;
		return (a * (1 - u) + bb * u) * (1 - v) + (c * (1 - u) + d * u) * v;
	}

	/** Highest ground over a box footprint (sampled at 0.25 m), NaN if unknown. */
	public static double maxOver(double x0, double z0, double x1, double z1) {
		double m = Double.NaN;
		for (double x = x0; x <= x1 + 1e-6; x += Math.max(0.05, Math.min(0.25, (x1 - x0))))
			for (double z = z0; z <= z1 + 1e-6; z += Math.max(0.05, Math.min(0.25, (z1 - z0)))) {
				double h = height(Math.min(x, x1), Math.min(z, z1));
				if (!Double.isNaN(h) && (Double.isNaN(m) || h > m)) m = h;
			}
		return m;
	}
}
