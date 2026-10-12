import dev.valcraft.link.ChestCodec;
import java.io.*;
import java.nio.file.*;

/** 0.10.0: decode C# chest contents, write a deposit op for C#. */
public class ChestCodecTest {
    public static void main(String[] a) throws Exception {
        int fail = 0;
        try (var in = new DataInputStream(new FileInputStream(a[0]))) {
            int n = in.readInt(); var r = new ChestCodec.Reader(); boolean done = false;
            for (int i = 0; i < n; i++) { byte[] p = new byte[in.readInt()]; in.readFully(p); done = r.add(p); if (done != (i == n - 1)) { fail++; System.out.println("FAIL last flag " + i); } }
            var h = r.header;
            if (h == null || h.serial() != 4242 || h.w() != 8 || h.h() != 4 || !h.name().equals("Reinforced chest")) { fail++; System.out.println("FAIL header " + h); }
            if (r.entries.size() != 32) { fail++; System.out.println("FAIL count " + r.entries.size()); }
            else for (int i = 0; i < 32; i++) {
                var e = r.entries.get(i);
                if (e.slot() != i || e.count() != i + 1 || e.dur() != (i % 4 == 0 ? 0xFFFF : i * 10) || !e.mcId().equals(i == 7 ? "" : "valcraft:tin_ingot") || !e.name().equals("$item_tin \u00c4" + i)) { fail++; System.out.println("FAIL entry " + e); }
            }
        }
        Files.write(Path.of(a[1]), ChestCodec.op(4242, ChestCodec.OP_DEPOSIT, 31, 64, 0xFFFF, "valcraft:bronze_ingot"));
        System.out.println(fail == 0 ? "[java] chest OK" : "[java] chest " + fail + " failures");
        System.exit(fail == 0 ? 0 : 1);
    }
}
