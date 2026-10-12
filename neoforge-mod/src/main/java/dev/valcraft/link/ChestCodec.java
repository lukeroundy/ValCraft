package dev.valcraft.link;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/** 0.10.0: see protocol/ChestCodec.cs. */
public final class ChestCodec {
	private ChestCodec() {}
	public static final int OP_TAKE = 1, OP_DEPOSIT = 2, OP_CLOSE = 3;
	public record Entry(int slot, int count, int dur, String mcId, String name) {}
	public record Header(long serial, int w, int h, String name) {}

	/** Accumulates messages; returns true on the last one. */
	public static final class Reader {
		public Header header; public final List<Entry> entries = new ArrayList<>();
		public boolean add(byte[] p) {
			ByteBuffer b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN);
			int flags = p[0] & 0xFF; long serial = b.getInt(1) & 0xFFFFFFFFL; int o = 5;
			if ((flags & 1) != 0) {
				entries.clear();
				int w = p[o] & 0xFF, h = p[o + 1] & 0xFF, nl = p[o + 2] & 0xFF; o += 3;
				header = new Header(serial, w, h, new String(p, o, nl, StandardCharsets.UTF_8)); o += nl;
			} else if (header == null || header.serial() != serial) return false;
			while (o + 7 <= p.length) {
				int slot = p[o] & 0xFF, count = b.getShort(o + 1) & 0xFFFF, dur = b.getShort(o + 3) & 0xFFFF, il = p[o + 5] & 0xFF; o += 6;
				String id = new String(p, o, il, StandardCharsets.UTF_8); o += il;
				int nl = p[o] & 0xFF; o++;
				String nm = new String(p, o, nl, StandardCharsets.UTF_8); o += nl;
				entries.add(new Entry(slot, count, dur, id, nm));
			}
			return (flags & 2) != 0;
		}
	}

	public static byte[] op(long serial, int op, int slot, int count, int dur, String id) {
		byte[] ib = id == null ? new byte[0] : id.getBytes(StandardCharsets.UTF_8);
		return ByteBuffer.allocate(10 + ib.length).order(ByteOrder.LITTLE_ENDIAN).putInt((int) serial).put((byte) op).put((byte) slot).putShort((short) count).putShort((short) dur).put(ib).array();
	}
}
