import dev.valcraft.link.BuildCodec;
import java.io.*; import java.util.*;
/** Decodes the piece list written by the C# side (0.8.5). */
public class BuildListTest {
    public static void main(String[] a) throws Exception {
        List<BuildCodec.Piece> all = new ArrayList<>(); boolean first = false, last = false;
        try (var in = new DataInputStream(new FileInputStream(a[0]))) {
            int n = in.readInt();
            for (int i = 0; i < n; i++) { byte[] m = new byte[in.readInt()]; in.readFully(m); if (i == 0) first = BuildCodec.first(m); if (i == n - 1) last = BuildCodec.last(m); all.addAll(BuildCodec.decode(m)); }
        }
        boolean ok = first && last && all.size() == 40 && all.get(5).name().equals("Piece 5 Ä長い") && all.get(39).index() == 273 && !all.get(39).canBuild() && all.get(0).cost().startsWith("10 Wood") && all.get(2).category() == 2;
        System.out.println(ok ? "java build list OK" : "java build list FAIL " + all.size());
        System.exit(ok ? 0 : 1);
    }
}
