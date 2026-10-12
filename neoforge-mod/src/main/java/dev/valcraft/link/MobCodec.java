package dev.valcraft.link;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.List;

/** 0.9.0 (v14) mob list / hurt / water payloads; layout in protocol/MobCodec.cs. */
public final class MobCodec {
	private MobCodec() {}
	public record Mob(int id, int kind, float x, float y, float z, float w, float h, float hp) {}
	public static final int HOSTILE = 1, PASSIVE = 2, ENTRY = 20;

	private static int u8(float v) { return Math.max(0, Math.min(255, Math.round(v))); }

	public static List<byte[]> encodeList(List<Mob> mobs) {
		List<byte[]> out = new ArrayList<>(); int per = (248 - 3) / ENTRY;
		for (int s = 0; s == 0 || s < mobs.size(); s += per) {
			int n = Math.min(per, mobs.size() - s);
			ByteBuffer b = ByteBuffer.allocate(3 + n * ENTRY).order(ByteOrder.LITTLE_ENDIAN);
			b.put((byte) 0).put((byte) 0).put((byte) n);
			for (int i = 0; i < n; i++) {
				Mob m = mobs.get(s + i);
				b.putInt(m.id()).put((byte) m.kind()).putFloat(m.x()).putFloat(m.y()).putFloat(m.z()).put((byte) u8(m.w() * 10)).put((byte) u8(m.h() * 10)).put((byte) u8(m.hp() * 100));
			}
			out.add(b.array());
		}
		out.get(0)[0] = 1; out.get(out.size() - 1)[1] = 1;
		return out;
	}

	public static List<Mob> decodeList(byte[] p) {
		List<Mob> r = new ArrayList<>(); ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
		int n = p[2] & 0xFF;
		for (int i = 0; i < n && 3 + (i + 1) * ENTRY <= p.length; i++) {
			int o = 3 + i * ENTRY;
			r.add(new Mob(b.getInt(o), p[o + 4] & 0xFF, b.getFloat(o + 5), b.getFloat(o + 9), b.getFloat(o + 13), (p[o + 17] & 0xFF) / 10f, (p[o + 18] & 0xFF) / 10f, (p[o + 19] & 0xFF) / 100f));
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
