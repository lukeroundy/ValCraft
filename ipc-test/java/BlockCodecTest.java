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
