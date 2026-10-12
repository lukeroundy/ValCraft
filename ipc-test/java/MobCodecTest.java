import dev.valcraft.link.MobCodec;
import java.io.*; import java.util.*;
/** 0.9.0: mode "write" = Java-encoded MC mob list + vmob hurt for C#; mode "read" = decode C#-written vmob list + water. */
public class MobCodecTest {
    public static void main(String[] a) throws Exception {
        if (a[0].equals("write")) {
            List<MobCodec.Mob> l = new ArrayList<>();
            for (int i = 0; i < 30; i++) l.add(new MobCodec.Mob(1000 + i, i % 2 == 0 ? 1 : 2, i * 1.5f, 64.25f, -i, 0.6f, 1.95f, 0.5f, 20f + i, i == 7 ? "Zombie Bob \u00e9" : i == 9 ? "A very long custom name that exceeds thirty-two bytes" : "Zombie"));
            try (var o = new DataOutputStream(new FileOutputStream(a[1]))) {
                var msgs = MobCodec.encodeList(l); o.writeInt(msgs.size()); for (byte[] m : msgs) { o.writeInt(m.length); o.write(m); }
                byte[] h = MobCodec.vmobHurt(77, 3.5f, 1004); o.writeInt(h.length); o.write(h);
            }
            System.out.println("java mobs written"); return;
        }
        boolean ok = true;
        try (var in = new DataInputStream(new FileInputStream(a[1]))) {
            int n = in.readInt(); List<MobCodec.Mob> all = new ArrayList<>();
            for (int i = 0; i < n; i++) { byte[] m = new byte[in.readInt()]; in.readFully(m); all.addAll(MobCodec.decodeList(m)); }
            ok &= all.size() == 3 && all.get(2).id() == 3 && Math.abs(all.get(1).y() - 71.5f) < 1e-4 && Math.abs(all.get(0).w() - 1.2f) < 0.01 && Math.abs(all.get(2).hp() - 0.25f) < 0.01
                && Math.abs(all.get(1).maxHp() - 200f) < 0.5f && all.get(1).name().equals("Greyling") && all.get(0).name().isEmpty();
            byte[] w = new byte[in.readInt()]; in.readFully(w);
            var g = MobCodec.decodeWater(w, System.currentTimeMillis());
            ok &= g.nx() == 3 && Math.abs(g.surfaceAt(10, 20) - 62.5f) < 0.07 && Float.isNaN(g.surfaceAt(12, 20)) && g.tarAt(14, 24) && !g.tarAt(10, 20) && Float.isNaN(g.surfaceAt(100, 20));
        }
        System.out.println(ok ? "java mobs/water OK" : "java mobs/water FAIL"); System.exit(ok ? 0 : 1);
    }
}
