package dev.valcraft.link;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.List;

/** 0.9.0 (v14) mob list / hurt / water payloads; layout in protocol/MobCodec.cs. */
public final class MobCodec {
	private MobCodec() {}
	public record Mob(int id, int kind, float x, float y, float z, float w, float h, float hp, float maxHp, String name) {}
	public static final int HOSTILE = 1, PASSIVE = 2, FIXED = 23;
	public static final int TILE_CELLS = 9, TILE_STEP = 2, TILE_SIZE = 18;

	private static int u8(float v) { return Math.max(0, Math.min(255, Math.round(v))); }

	/** v15 layout, see protocol/MobCodec.cs. */
	public static List<byte[]> encodeList(List<Mob> mobs) {
		List<byte[]> out = new ArrayList<>();
		ByteBuffer cur = ByteBuffer.allocate(248).order(ByteOrder.LITTLE_ENDIAN); cur.position(3); int n = 0;
		for (Mob m : mobs) {
			byte[] name = (m.name() == null ? "" : m.name()).getBytes(java.nio.charset.StandardCharsets.UTF_8);
			if (name.length > 32) { int k = 32; while (k > 0 && (name[k] & 0xC0) == 0x80) k--; name = java.util.Arrays.copyOf(name, k); }
			if (cur.position() + FIXED + name.length > 248) { out.add(finish(cur, n)); cur = ByteBuffer.allocate(248).order(ByteOrder.LITTLE_ENDIAN); cur.position(3); n = 0; }
			cur.putInt(m.id()).put((byte) m.kind()).putFloat(m.x()).putFloat(m.y()).putFloat(m.z()).put((byte) u8(m.w() * 10)).put((byte) u8(m.h() * 10)).put((byte) u8(m.hp() * 100))
				.putShort((short) Math.max(0, Math.min(65535, Math.round(m.maxHp())))).put((byte) name.length).put(name);
			n++;
		}
		if (n > 0 || out.isEmpty()) out.add(finish(cur, n));
		out.get(0)[0] = 1; out.get(out.size() - 1)[1] = 1;
		return out;
	}
	private static byte[] finish(ByteBuffer b, int n) { byte[] a = java.util.Arrays.copyOf(b.array(), b.position()); a[0] = 0; a[1] = 0; a[2] = (byte) n; return a; }

	public static List<Mob> decodeList(byte[] p) {
		List<Mob> r = new ArrayList<>(); ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int n = p[2] & 0xFF, o = 3;
		for (int i = 0; i < n && o + FIXED <= p.length; i++) {
			int nl = p[o + 22] & 0xFF;
			String name = new String(p, o + 23, Math.min(nl, p.length - o - 23), java.nio.charset.StandardCharsets.UTF_8);
			r.add(new Mob(b.getInt(o), p[o + 4] & 0xFF, b.getFloat(o + 5), b.getFloat(o + 9), b.getFloat(o + 13), (p[o + 17] & 0xFF) / 10f, (p[o + 18] & 0xFF) / 10f, (p[o + 19] & 0xFF) / 100f, b.getShort(o + 20) & 0xFFFF, name));
			o += FIXED + nl;
		}
		return r;
	}

	public static byte[] vmobHurt(int vid, float dmg, int attacker) {
		return ByteBuffer.allocate(12).order(ByteOrder.LITTLE_ENDIAN).putInt(vid).putFloat(dmg).putInt(attacker).array();
	}

	/** Valheim water grid (EV_WATER). */
	public record Water(float x0, float z0, int step, int nx, int nz, float[] surface, boolean[] tar, long time) {
		/** Surface MC Y at (x, z) from the nearest cell, NaN if none / outside. */
		public float surfaceAt(double x, double z) {
			int i = (int) Math.round((x - x0) / step), j = (int) Math.round((z - z0) / step);
			if (i < 0 || j < 0 || i >= nx || j >= nz) return Float.NaN;
			return surface[j * nx + i];
		}
		/** 0.9.3: bilinear surface (waves sampled per 2 m cell would otherwise jump); nearest cell where a neighbour is dry. */
		public float surfaceSmooth(double x, double z) {
			double fx = (x - x0) / step, fz = (z - z0) / step;
			int i = (int) Math.floor(fx), j = (int) Math.floor(fz);
			if (i < 0 || j < 0 || i + 1 >= nx || j + 1 >= nz) return surfaceAt(x, z);
			float a = surface[j * nx + i], b = surface[j * nx + i + 1], c = surface[(j + 1) * nx + i], d = surface[(j + 1) * nx + i + 1];
			if (Float.isNaN(a) || Float.isNaN(b) || Float.isNaN(c) || Float.isNaN(d)) return surfaceAt(x, z);
			double u = fx - i, v = fz - j;
			return (float) ((a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v);
		}
		public boolean tarAt(double x, double z) {
			int i = (int) Math.round((x - x0) / step), j = (int) Math.round((z - z0) / step);
			return i >= 0 && j >= 0 && i < nx && j < nz && tar[j * nx + i];
		}
	}
	public static Water decodeWater(byte[] p, long now) {
		ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		float x0 = b.getFloat(0), z0 = b.getFloat(4); int step = p[8] & 0xFF, nx = p[9] & 0xFF, nz = p[10] & 0xFF, n = nx * nz;
		float[] s = new float[n]; boolean[] t = new boolean[n];
		for (int i = 0; i < n; i++) { short v = b.getShort(11 + i * 2); s[i] = v == Short.MIN_VALUE ? Float.NaN : v / 16f; t[i] = (p[11 + n * 2 + i / 8] & (1 << (i % 8))) != 0; }
		return new Water(x0, z0, Math.max(1, step), nx, nz, s, t, now);
	}
}
