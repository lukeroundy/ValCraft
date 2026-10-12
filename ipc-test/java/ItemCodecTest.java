import java.io.*; import java.nio.*; import java.nio.charset.StandardCharsets;
/** Writes 0.8.0 item payloads in the exact layout ItemLink.java sends (little-endian), length-prefixed, for the C# side. */
public class ItemCodecTest {
    static ByteBuffer bb(int n){ return ByteBuffer.allocate(n).order(ByteOrder.LITTLE_ENDIAN); }
    static void w(DataOutputStream o, ByteBuffer b) throws IOException { byte[] a = java.util.Arrays.copyOf(b.array(), b.position()); o.writeInt(a.length); o.write(a); }
    public static void main(String[] a) throws Exception {
        try (var o = new DataOutputStream(new FileOutputStream(a[0]))) {
            byte[] id = "minecraft:oak_log".getBytes(StandardCharsets.UTF_8);
            var held = bb(64); held.putShort((short)12).put(id); w(o, held);
            var ack = bb(64); ack.putInt(77).putShort((short)3).put(id); w(o, ack);
            String[] ids = {"minecraft:oak_log", "minecraft:flint", "minecraft:iron_ingot"}; int[] cs = {64, 5, 300};
            for (int part = 0; part < 2; part++) {
                var s = bb(248); s.put((byte)(part == 0 ? 1 : 0)).put((byte)(part == 1 ? 1 : 0));
                int from = part == 0 ? 0 : 2, to = part == 0 ? 2 : 3; s.put((byte)(to - from));
                for (int i = from; i < to; i++) { byte[] b = ids[i].getBytes(StandardCharsets.UTF_8); s.putShort((short)cs[i]).put((byte)b.length).put(b); }
                w(o, s);
            }
        }
        System.out.println("java items ok");
    }
}
