package dev.valcraft.link;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/** Pure-JDK encoder for the v5 block-render messages (PROTOCOL.md "v5"). Shared with the IPC test. */
public final class BlockCodec {
	private BlockCodec() {}
	public static final int FLAG_FULL_OPAQUE = 1, FLAG_COLLIDES = 2, FLAG_CUTOUT = 4, FLAG_TRANSLUCENT = 8, FLAG_CROSS = 16, FLAG_FLUID = 32, FLAG_SHINY = 64, FLAG_QUADS = 128;
	public static final int QUAD_BYTES = 44, QUADS_PER_MSG = (ValCraftLink.SLOT_PAYLOAD - 6) / QUAD_BYTES; // 5
	public static final int MAX_BOXES = 8;
	public static final int BLOCKS_PER_MSG = (ValCraftLink.SLOT_PAYLOAD - 14) / 4; // 58

	private static ByteBuffer le(int n) { return ByteBuffer.allocate(n).order(ByteOrder.LITTLE_ENDIAN); }

	/** tiles: 6 atlas tile indices (MC Direction order down,up,north,south,west,east); boxes: n*6 values 0..16 (minX,minY,minZ,maxX,maxY,maxZ in 1/16 block). */
	public static ValCraftLink.Event palette(int paletteId, int flags, int[] tiles, byte[] boxes) { return palette(paletteId, flags, tiles, boxes, 0, 0); }

	/** v6: after the boxes, u8 light level (0-15), u8 r, g, b light colour. */
	public static ValCraftLink.Event palette(int paletteId, int flags, int[] tiles, byte[] boxes, int light, int rgb) {
		int n = Math.min(boxes.length / 6, MAX_BOXES);
		ByteBuffer b = le(16 + n * 6 + 4);
		b.putShort((short) paletteId).put((byte) flags).put((byte) n);
		for (int i = 0; i < 6; i++) b.putShort((short) tiles[i]);
		b.put(boxes, 0, n * 6);
		b.put((byte) light).put((byte) (rgb >> 16)).put((byte) (rgb >> 8)).put((byte) rgb);
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

	/** One model quad: 4 vertices pos (block-local, 0..1 nominal) + uv as fractions of the sprite (0..1, v down). */
	public record Quad(int tile, int dir, int cull, float[] pos, float[] uv) {}

	/** EV_BLOCK_QUADS: u16 paletteId, u16 firstIndex, u8 n, u8 last(1 = final message), then n quads of 44 bytes:
	 *  u16 tile, u8 dir (facing, MC Direction ordinal), u8 cull (0-5 = cull face, 6 = never), 4 x (i16 x,y,z in 1/1024 block, u16 u,v in 1/65535 of the sprite). */
	public static List<ValCraftLink.Event> quads(int paletteId, List<Quad> q) {
		List<ValCraftLink.Event> out = new ArrayList<>();
		for (int off = 0; off < q.size() || off == 0; off += QUADS_PER_MSG) {
			int n = Math.min(QUADS_PER_MSG, q.size() - off);
			ByteBuffer b = le(6 + n * QUAD_BYTES);
			b.putShort((short) paletteId).putShort((short) off).put((byte) n).put((byte) (off + n >= q.size() ? 1 : 0));
			for (int i = 0; i < n; i++) {
				Quad x = q.get(off + i);
				b.putShort((short) x.tile()).put((byte) x.dir()).put((byte) x.cull());
				for (int v = 0; v < 4; v++) {
					for (int c = 0; c < 3; c++) b.putShort((short) Math.max(-32768, Math.min(32767, Math.round(x.pos()[v * 3 + c] * 1024))));
					for (int c = 0; c < 2; c++) b.putShort((short) Math.max(0, Math.min(65535, Math.round(x.uv()[v * 2 + c] * 65535))));
				}
			}
			out.add(new ValCraftLink.Event(ValCraftLink.EV_BLOCK_QUADS, b.array()));
			if (q.isEmpty()) break;
		}
		return out;
	}

	public static ValCraftLink.Event atlas(int revision, int tiles, int tilesPerRow, int tileSize, String path) {
		byte[] p = path.getBytes(StandardCharsets.UTF_8);
		int n = Math.min(p.length, ValCraftLink.SLOT_PAYLOAD - 16);
		return new ValCraftLink.Event(ValCraftLink.EV_BLOCK_ATLAS, le(16 + n).putInt(revision).putInt(tiles).putInt(tilesPerRow).putInt(tileSize).put(p, 0, n).array());
	}
}
