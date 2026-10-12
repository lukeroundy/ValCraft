package dev.valcraft.link;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/** Pure-JDK encoder for the v5 block-render messages (PROTOCOL.md "v5"). Shared with the IPC test. */
public final class BlockCodec {
	private BlockCodec() {}
	public static final int FLAG_FULL_OPAQUE = 1, FLAG_COLLIDES = 2, FLAG_CUTOUT = 4;
	public static final int MAX_BOXES = 8;
	public static final int BLOCKS_PER_MSG = (ValCraftLink.SLOT_PAYLOAD - 14) / 4; // 58

	private static ByteBuffer le(int n) { return ByteBuffer.allocate(n).order(ByteOrder.LITTLE_ENDIAN); }

	/** tiles: 6 atlas tile indices (MC Direction order down,up,north,south,west,east); boxes: n*6 values 0..16 (minX,minY,minZ,maxX,maxY,maxZ in 1/16 block). */
	public static ValCraftLink.Event palette(int paletteId, int flags, int[] tiles, byte[] boxes) {
		int n = Math.min(boxes.length / 6, MAX_BOXES);
		ByteBuffer b = le(16 + n * 6);
		b.putShort((short) paletteId).put((byte) flags).put((byte) n);
		for (int i = 0; i < 6; i++) b.putShort((short) tiles[i]);
		b.put(boxes, 0, n * 6);
		return new ValCraftLink.Event(ValCraftLink.EV_BLOCK_PALETTE, b.array());
	}

	/** local index = (y<<8)|(z<<4)|x within the 16^3 section (sx,sy,sz = MC block coords >> 4). pids are palette ids (>= 1). */
	public static List<ValCraftLink.Event> section(int sx, int sy, int sz, short[] local, short[] pid, int count) {
		List<ValCraftLink.Event> out = new ArrayList<>(2 + count / BLOCKS_PER_MSG + 1);
		out.add(new ValCraftLink.Event(ValCraftLink.EV_SECTION_BEGIN, le(16).putInt(sx).putInt(sy).putInt(sz).putInt(count).array()));
		for (int off = 0; off < count; off += BLOCKS_PER_MSG) {
			int n = Math.min(BLOCKS_PER_MSG, count - off);
			ByteBuffer b = le(14 + n * 4);
			b.putInt(sx).putInt(sy).putInt(sz).putShort((short) n);
			for (int i = 0; i < n; i++) b.putShort(local[off + i]).putShort(pid[off + i]);
			out.add(new ValCraftLink.Event(ValCraftLink.EV_SECTION_BLOCKS, b.array()));
		}
		out.add(new ValCraftLink.Event(ValCraftLink.EV_SECTION_END, le(12).putInt(sx).putInt(sy).putInt(sz).array()));
		return out;
	}

	public static ValCraftLink.Event atlas(int revision, int tiles, int tilesPerRow, int tileSize, String path) {
		byte[] p = path.getBytes(StandardCharsets.UTF_8);
		int n = Math.min(p.length, ValCraftLink.SLOT_PAYLOAD - 16);
		return new ValCraftLink.Event(ValCraftLink.EV_BLOCK_ATLAS, le(16 + n).putInt(revision).putInt(tiles).putInt(tilesPerRow).putInt(tileSize).put(p, 0, n).array());
	}
}
