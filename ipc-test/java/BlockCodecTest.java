import dev.valcraft.link.BlockCodec;
import dev.valcraft.link.ValCraftLink;
import java.io.DataOutputStream;
import java.io.FileOutputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.List;

/** Writes a deterministic v5 block stream (type u16, len u32, payload; LE) for the C# mesher test. */
public class BlockCodecTest {
	public static void main(String[] a) throws Exception {
		List<ValCraftLink.Event> ev = new ArrayList<>();
		ev.add(new ValCraftLink.Event(ValCraftLink.EV_BLOCKS_RESET, new byte[0]));
		ev.add(BlockCodec.palette(1, BlockCodec.FLAG_FULL_OPAQUE | BlockCodec.FLAG_COLLIDES, new int[] { 1, 2, 3, 3, 3, 3 }, new byte[] { 0, 0, 0, 16, 16, 16 }));
		ev.add(BlockCodec.palette(2, BlockCodec.FLAG_COLLIDES, new int[] { 4, 4, 4, 4, 4, 4 }, new byte[] { 0, 0, 0, 16, 8, 16 }));
		ev.addAll(BlockCodec.section(0, 4, 0, new short[] { 0, 1, (short) ((5 << 4) | 5), 15 }, new short[] { 1, 1, 2, 1 }, 4));
		ev.addAll(BlockCodec.section(1, 4, 0, new short[] { 0 }, new short[] { 1 }, 1));
		short[] l = new short[4096], p = new short[4096];
		for (int i = 0; i < 4096; i++) { l[i] = (short) i; p[i] = 1; }
		ev.addAll(BlockCodec.section(2, 4, 0, l, p, 4096));
		ev.addAll(BlockCodec.section(9, 4, 9, new short[] { 7 }, new short[] { 1 }, 1));
		ev.addAll(BlockCodec.section(9, 4, 9, new short[0], new short[0], 0)); // removal
		int T = BlockCodec.FLAG_TRANSLUCENT, F = BlockCodec.FLAG_FLUID, X = BlockCodec.FLAG_CROSS;
		ev.add(BlockCodec.palette(3, T | BlockCodec.FLAG_COLLIDES, new int[] { 5, 5, 5, 5, 5, 5 }, new byte[] { 0, 0, 0, 16, 16, 16 })); // glass
		ev.add(BlockCodec.palette(4, F | T, new int[] { 6, 6, 7, 7, 7, 7 }, new byte[] { 0, 0, 0, 16, 14, 16 })); // water source
		ev.add(BlockCodec.palette(5, F | T, new int[] { 6, 6, 7, 7, 7, 7 }, new byte[] { 0, 0, 0, 16, 8, 16 }));  // flowing water
		ev.add(BlockCodec.palette(6, X | BlockCodec.FLAG_CUTOUT, new int[] { 8, 8, 8, 8, 8, 8 }, new byte[] { 0, 0, 0, 16, 16, 16 })); // poppy
		ev.addAll(BlockCodec.section(3, 4, 0, new short[] { 0, 1 }, new short[] { 3, 3 }, 2));
		ev.addAll(BlockCodec.section(4, 4, 0, new short[] { 0, 1 }, new short[] { 4, 5 }, 2));
		ev.addAll(BlockCodec.section(5, 4, 0, new short[] { 0 }, new short[] { 6 }, 1));
		// 0.7.2: torch as 7 model quads (multi-message), light 14; lava cluster; shiny iron block
		ev.add(BlockCodec.palette(7, BlockCodec.FLAG_CUTOUT | BlockCodec.FLAG_QUADS, new int[] { 9, 9, 9, 9, 9, 9 }, new byte[] { 7, 0, 7, 9, 10, 9 }, 14, 0xFFB060));
		List<BlockCodec.Quad> q = new ArrayList<>();
		for (int i = 0; i < 7; i++) {
			float x = 7 / 16f + i * 0.01f;
			q.add(new BlockCodec.Quad(9, 4, 6, new float[] { x, 0, 0.4375f, x, 0, 0.5625f, x, 0.625f, 0.5625f, x, 0.625f, 0.4375f }, new float[] { 0.4375f, 1, 0.5625f, 1, 0.5625f, 0.375f, 0.4375f, 0.375f }));
		}
		ev.addAll(BlockCodec.quads(7, q));
		ev.add(BlockCodec.palette(8, BlockCodec.FLAG_FULL_OPAQUE | BlockCodec.FLAG_FLUID, new int[] { 10, 10, 11, 11, 11, 11 }, new byte[] { 0, 0, 0, 16, 14, 16 }, 15, 0xFF7A30));
		ev.add(BlockCodec.palette(9, BlockCodec.FLAG_FULL_OPAQUE | BlockCodec.FLAG_COLLIDES | BlockCodec.FLAG_SHINY, new int[] { 12, 12, 12, 12, 12, 12 }, new byte[] { 0, 0, 0, 16, 16, 16 }));
		ev.addAll(BlockCodec.section(6, 4, 0, new short[] { 0 }, new short[] { 7 }, 1));
		short[] lv = new short[16], lp = new short[16];
		for (int i = 0; i < 16; i++) { lv[i] = (short) (((i >> 2) << 8) | ((i >> 1 & 1) << 4) | (i & 1)); lp[i] = 8; }
		ev.addAll(BlockCodec.section(7, 4, 0, lv, lp, 16));
		ev.addAll(BlockCodec.section(8, 4, 0, new short[] { 0 }, new short[] { 9 }, 1));
		// 0.7.3: animation, entity mesh (12 quads -> 3 messages) + two-part snapshot
		ev.add(BlockCodec.anim(20, new int[] { 21, 22, 23 }, new int[] { 2, 2, 4 }));
		List<BlockCodec.Quad> eq = new ArrayList<>();
		for (int i = 0; i < 12; i++) eq.add(new BlockCodec.Quad(30, 1, 6, new float[] { -0.25f, i * 0.01f, -0.25f, 0.25f, i * 0.01f, -0.25f, 0.25f, i * 0.01f, 0.25f, -0.25f, i * 0.01f, 0.25f }, new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }));
		ev.addAll(BlockCodec.entityMesh(5, eq));
		List<BlockCodec.Ent> ents = new ArrayList<>();
		for (int i = 0; i < 15; i++) ents.add(new BlockCodec.Ent(100 + i, 5, i, 64.5f, -i, i == 0 ? 1 : 0));
		ev.addAll(BlockCodec.entityUpdate(ents));
		// 0.7.4: dynamic mob mesh re-sent (replace), mob + lid-block pseudo entity in the snapshot
		ev.addAll(BlockCodec.entityMesh(0x40000007, eq));
		ev.addAll(BlockCodec.entityMesh(0x40000007, eq.subList(0, 3)));
		ev.addAll(BlockCodec.entityUpdate(List.of(new BlockCodec.Ent(7, 0x40000007, 1, 64, 1, 2), new BlockCodec.Ent(0x80000000 | 1234, 5, 3, 64, 3, 0))));
		ev.add(BlockCodec.atlas(3, 10, 64, 16, "C:/x/blocks-atlas.png"));
		try (var out = new DataOutputStream(new FileOutputStream(a[0]))) {
			for (var e : ev) {
				if (e.payload().length > 248) throw new IllegalStateException("payload too big: " + e.payload().length);
				out.write(ByteBuffer.allocate(6).order(ByteOrder.LITTLE_ENDIAN).putShort(e.type()).putInt(e.payload().length).array());
				out.write(e.payload());
			}
		}
		System.out.println("[java] wrote " + ev.size() + " block messages");
	}
}
