// Valheim-role IPC test (protocol v2): acked Hello, ping/pong, seqlock PlayerState, double-buffered frames
// with size negotiation, input events, reconnect after host re-init, Quit.
using System;
using System.Diagnostics;
using System.Threading;
using ValCraft.Protocol;

static unsafe class Program
{
    static string path;
    static int Fail(string m) { Console.WriteLine("[cs] FAIL: " + m); return 1; }

    static bool WaitHelloAndAck(ValCraftLink link, Stopwatch sw)
    {
        long until = sw.ElapsedMilliseconds + 30000;
        while (sw.ElapsedMilliseconds < until)
        {
            link.Heartbeat();
            while (link.TryReceive(out var ev))
                if (ev.Type == ValCraftLink.EvHello && Payload.GetU32(ev.Payload, 2) == link.Session)
                {
                    link.TrySend(ValCraftLink.EvHelloAck, Payload.U32(link.Session, Payload.GetU32(ev.Payload, 0)));
                    long t2 = sw.ElapsedMilliseconds + 5000;
                    while (link.McAckedSession != link.Session && sw.ElapsedMilliseconds < t2) Thread.Sleep(1);
                    return link.McAckedSession == link.Session;
                }
            Thread.Sleep(5);
        }
        return false;
    }

    static bool nodsCheck;
    static int BlocksTest(string file)
    {
        var st = new BlockStore(); var es = new EntityStore();
        var b = System.IO.File.ReadAllBytes(file);
        int n = 0;
        for (int o = 0; o < b.Length;) { ushort t = BitConverter.ToUInt16(b, o); int len = BitConverter.ToInt32(b, o + 2); var p = new byte[len]; Array.Copy(b, o + 6, p, 0, len); o += 6 + len; if (!es.Handle(t, p) && !st.Handle(t, p)) return Fail("unhandled type " + t); n++; }
        var m = new BlockStore.MeshData();
        bool ok = true;
        void Expect((int, int, int) k, int faces) { st.Build(k, m); Console.WriteLine($"[cs] section {k}: {m.Faces} faces (expected {faces}), {m.ColTris.Count / 6} collider quads"); if (m.Faces != faces) ok = false;
            var all = new System.Collections.Generic.List<int>(); foreach (var sub in m.Sub) all.AddRange(sub); bool ds = nodsCheck;
            for (int i = 0; i < all.Count && !ds; i += 3) { int a = all[i], c1 = all[i + 1], c2 = all[i + 2];
                float ex = m.Pos[c1 * 3] - m.Pos[a * 3], ey = m.Pos[c1 * 3 + 1] - m.Pos[a * 3 + 1], ez = m.Pos[c1 * 3 + 2] - m.Pos[a * 3 + 2];
                float fx = m.Pos[c2 * 3] - m.Pos[a * 3], fy = m.Pos[c2 * 3 + 1] - m.Pos[a * 3 + 1], fz = m.Pos[c2 * 3 + 2] - m.Pos[a * 3 + 2];
                float d = (ey * fz - ez * fy) * m.Nrm[a * 3] + (ez * fx - ex * fz) * m.Nrm[a * 3 + 1] + (ex * fy - ey * fx) * m.Nrm[a * 3 + 2];
                if (d <= 0) { Console.WriteLine("[cs] back-facing triangle"); ok = false; break; } }
            foreach (var u in m.Uv) if (u < 0 || u > 1) { Console.WriteLine("[cs] uv out of range"); ok = false; break; } }
        Expect((0, 4, 0), 10 + 6 + 5);
        Expect((1, 4, 0), 5); // -x face hidden by x=15 of section 0
        Expect((2, 4, 0), 6 * 256);
        if (st.Sections.ContainsKey((9, 4, 9))) { Console.WriteLine("[cs] removed section still present"); ok = false; }
        if (st.AtlasRevision != 3 || st.AtlasPath != "C:/x/blocks-atlas.png") { Console.WriteLine("[cs] atlas msg wrong"); ok = false; }
        st.Build((0, 4, 0), m);
        // slab top face at y=0.5 (Unity local); cube face at z: MC z=0 -> Unity -0
        bool slabTop = false; for (int i = 0; i < m.Pos.Count; i += 3) if (Math.Abs(m.Pos[i + 1] - 0.5f) < 1e-4 && Math.Abs(m.Pos[i + 2] + 6f) < 1e-4) slabTop = true;
        if (!slabTop) { Console.WriteLine("[cs] slab geometry wrong"); ok = false; }
        // 0.7.1: glass (cull same), water (fluid slopes + same-fluid culling), plant (cross, double-sided, no collision)
        Expect((3, 4, 0), 9);           // two adjacent glass blocks: 12 - 2 shared faces - 1 against the full section 2
        st.Build((3, 4, 0), m); if (m.Sub[2].Count == 0 || m.ColTris.Count == 0) { Console.WriteLine("[cs] glass not translucent/colliding"); ok = false; }
        st.Build((4, 4, 0), m);
        Console.WriteLine($"[cs] water: {m.Faces} faces, {m.ColTris.Count} collider idx, sub2 {m.Sub[2].Count}");
        if (m.ColTris.Count != 0 || m.Faces != 10 || m.Sub[2].Count == 0) ok = false; // 2 water blocks side by side: shared side culled -> 10
        bool sloped = false; for (int i = 1; i < m.Pos.Count; i += 3) if (m.Pos[i] > 0.4f && m.Pos[i] < 0.9f && Math.Abs(m.Pos[i] - 0.875f) > 1e-3 && Math.Abs(m.Pos[i] - 0.5f) > 1e-3) sloped = true;
        if (!sloped) { Console.WriteLine("[cs] no averaged corner height"); ok = false; }
        nodsCheck = true; st.Build((5, 4, 0), m); nodsCheck = false;
        Console.WriteLine($"[cs] plant: {m.Faces} quads, cutout {m.Sub[1].Count / 6}, collider {m.ColTris.Count}");
        if (m.Faces != 4 || m.Sub[1].Count != 24 || m.ColTris.Count != 0) ok = false;
        st.Build((6, 4, 0), m);
        Console.WriteLine($"[cs] torch: {m.Faces} quads (layer1 {m.Sub[1].Count / 6}), collider {m.ColTris.Count}, lights {m.Lights.Count}, palette quads {st.Palette[7].Quads.Count}, light {st.Palette[7].Light} rgb {st.Palette[7].LightRgb:X6}");
        if (st.Palette[7].Quads.Count != 7 || !st.Palette[7].QuadsComplete || m.Sub[1].Count != 42 || m.ColTris.Count != 0 || m.Lights.Count != 1 || m.Lights[0].level != 14 || st.Palette[7].LightRgb != 0xFFB060) ok = false;
        st.Build((7, 4, 0), m);
        Console.WriteLine($"[cs] lava: {m.Faces} faces, layer3 (emissive) {m.Sub[3].Count / 6}, lights {m.Lights.Count} (count {(m.Lights.Count > 0 ? m.Lights[0].count : 0)})");
        if (m.Lights.Count != 1 || m.Lights[0].count != 16 || m.Sub[3].Count == 0 || m.ColTris.Count != 0) ok = false;
        st.Build((8, 4, 0), m);
        Console.WriteLine($"[cs] iron: layer4 (shiny) {m.Sub[4].Count / 6}");
        if (m.Sub[4].Count != 36) ok = false;
        var an = st.Anims.TryGetValue(20, out var aa) ? aa : null;
        Console.WriteLine($"[cs] anim: {(an == null ? "missing" : $"{an.Frames.Length} frames, total {an.Total} ticks, frame@0={an.FrameAt(0)} @3={an.FrameAt(3)} @5={an.FrameAt(5)} @8={an.FrameAt(8)}")}");
        if (an == null || an.Total != 8 || an.FrameAt(0) != 0 || an.FrameAt(3) != 1 || an.FrameAt(5) != 2 || an.FrameAt(8) != 0) ok = false;
        Console.WriteLine($"[cs] entities: mesh quads {(es.Meshes.TryGetValue(5, out var mq) ? mq.Count : -1)}, complete {es.CompleteMeshes.Contains(5)}, snapshot {es.Current.Count} (snapshots {es.Snapshots})");
        if (mq == null || mq.Count != 12 || !es.CompleteMeshes.Contains(5) || Math.Abs(mq[3].Pos[1] - 0.03f) > 2e-3f) ok = false;
        int dyn = es.Meshes.TryGetValue(0x40000007u, out var dq) ? dq.Count : -1;
        Console.WriteLine($"[cs] mob mesh after resend: {dyn} quads; snapshot {es.Current.Count} ({es.Snapshots} snapshots), lid entity {(es.Current.ContainsKey(0x80000000u | 1234) ? "ok" : "missing")}");
        if (dyn != 3 || es.Snapshots != 2 || es.Current.Count != 2 || es.Current[7].Flags != 2 || !es.Current.ContainsKey(0x80000000u | 1234)) ok = false;
        Console.WriteLine($"[cs] blocks test: {n} messages, {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "blocks") return BlocksTest(args[1]);
        if (args.Length > 0 && args[0] == "host") return FakeValheim.Run(args.Length > 1 ? args[1] : ValCraftLink.DefaultPath(), args.Length > 2 ? int.Parse(args[2]) : 300, args.Length > 3 ? args[3] : "cmd.txt");
        path = args.Length > 0 ? args[0] : ValCraftLink.DefaultPath();
        int pings = args.Length > 1 ? int.Parse(args[1]) : 20000;
        // 0. coordinate mapping (MC +Z south, yaw 0 = south, +90 = west -> Unity +Z north, yaw 0 = north, +90 = east)
        {
            bool ok = true;
            void Check(string what, bool c) { if (!c) { Console.WriteLine("[cs] coord FAIL: " + what); ok = false; } }
            CoordMap.ToUnity(1, 2, 3, out var ux, out var uy, out var uz); Check("pos", ux == 1 && uy == 2 && uz == -3);
            foreach (var (mcYaw, mcDirX, mcDirZ) in new[] { (0f, 0f, 1f), (90f, -1f, 0f), (180f, 0f, -1f), (-90f, 1f, 0f), (45f, -0.7071f, 0.7071f) })
            {
                CoordMap.Forward(CoordMap.YawToUnity(mcYaw), out var fx, out var fz);
                // MC look vector (-sin yaw, cos yaw) mapped to Unity is (x, -z)
                Check($"yaw {mcYaw}", Math.Abs(fx - mcDirX) < 1e-3 && Math.Abs(fz - -mcDirZ) < 1e-3);
            }
            Console.WriteLine(ok ? "[cs] coordinate mapping OK (5 yaw cases + position)" : "[cs] coordinate mapping FAILED");
            if (!ok) return 1;
        }
        var sw = Stopwatch.StartNew();
        var link = new ValCraftLink(path, isHost: true);
        // collision snapshots hammered from a background thread for the whole run (reader validates consistency)
        var collStop = false;
        var collThread = new Thread(() =>
        {
            var buf = new float[ValCraftLink.CollMaxBoxes * 6];
            int snap = 0;
            while (!Volatile.Read(ref collStop))
            {
                snap++;
                int count = 1000 + (snap * 977) % 30000;
                for (int i = 0; i < count; i++) for (int k = 0; k < 6; k++) buf[i * 6 + k] = snap + i + (k >= 3 ? 1 : 0);
                link.PublishCollision(buf, count);
                Thread.Sleep(2);
            }
            Console.WriteLine($"[cs] published {snap} collision snapshots");
        }) { IsBackground = true };
        collThread.Start();
        Console.WriteLine($"[cs] created {path} ({ValCraftLink.FileSize} bytes), session {link.Session:x}");

        // 1. acked hello
        if (!WaitHelloAndAck(link, sw)) return Fail("no acked Hello");
        Console.WriteLine("[cs] hello/ack OK");

        // 2. ping/pong + seqlock
        long sent = 0, received = 0, torn = 0, snapshots = 0, lastHb = 0;
        var t0 = sw.ElapsedMilliseconds;
        while (received < pings)
        {
            if (sw.ElapsedMilliseconds - lastHb > 100) { link.Heartbeat(); lastHb = sw.ElapsedMilliseconds; }
            while (sent < pings && sent - received < 200 && link.TrySend(ValCraftLink.EvPing, BitConverter.GetBytes(sent + 1))) sent++;
            while (link.TryReceive(out var ev))
            {
                if (ev.Type != ValCraftLink.EvPong) continue; // stray Hello repeats are fine
                long n = BitConverter.ToInt64(ev.Payload, 0);
                if (n != received + 1) return Fail($"pong {n} expected {received + 1}");
                received++;
            }
            if (link.TryReadPlayerState(out var s))
            {
                snapshots++;
                if (s.X != s.FrameId || s.Y != s.FrameId * 2.0 || s.Z != -s.FrameId || s.Yaw != (float)(s.FrameId % 360)
                    || s.FeetX != s.X || s.FeetY != s.FrameId * 2.0 - 1.62 || s.FeetZ != s.Z || s.EyeHeight != 1.62f
                    || s.BobX != 0.25f || s.BobY != -0.5f || s.BobRollDeg != 3f || s.BobPitchDeg != 5f || s.Flags != ValCraftLink.PsHasPlayer) torn++;
            }
            if (sw.ElapsedMilliseconds - t0 > 60000) return Fail("ping timeout");
        }
        Console.WriteLine($"[cs] {received} ping/pong in {sw.ElapsedMilliseconds - t0} ms; playerState snapshots={snapshots}, torn={torn}");
        if (torn != 0 || snapshots == 0) return Fail("player state");

        // 3. frames: negotiation + tear detection
        var copy = new byte[ValCraftLink.FrameBytes];
        foreach (var (rw, rh, ew, eh, slowReader) in new[] { (1280, 720, 1280, 720, false), (3840, 2160, 1920, 1080, false), (800, 600, 800, 600, true) })
        {
            link.RequestFrameSize(rw, rh);
            long iter = 0, last = 0, good = 0, skipped = 0, corrupt = 0, wrongSize = 0;
            var until = sw.ElapsedMilliseconds + 1500;
            while (sw.ElapsedMilliseconds < until)
            {
                link.Heartbeat();
                while (link.TryReceive(out _)) { }
                if (!link.TryGetLatestFrame(last, out var v)) { Thread.Sleep(1); continue; }
                fixed (byte* dst = copy) Buffer.MemoryCopy(v.Data, dst, copy.Length, v.Bytes);
                if (slowReader && (++iter % 2) == 0) Thread.Sleep(25); // let the writer lap us: tear must be detected, never accepted
                if (!link.FrameStillValid(v)) { skipped++; continue; }
                last = v.Seq;
                if (v.Width != ew || v.Height != eh) { wrongSize++; continue; }
                byte expect = (byte)v.Seq;
                for (int i = 0; i < v.Bytes; i += 4093) if (copy[i] != expect) { corrupt++; break; }
                if (copy[v.Bytes - 1] != expect) corrupt++;
                good++;
            }
            Console.WriteLine($"[cs] frames request {rw}x{rh} -> expect {ew}x{eh}: good={good} skipped(torn-detected)={skipped} corrupt={corrupt} beforeResize={wrongSize}{(slowReader ? " (slow reader)" : "")}");
            if (corrupt != 0 || good < 5 || (slowReader && skipped == 0)) return Fail("frames");
        }

        // 4. input events
        for (int i = 0; i < 1000; i++) while (!link.TrySend(ValCraftLink.EvKey, Payload.I32(65 + i % 26, 0, i % 2, 0))) { link.Heartbeat(); Thread.Sleep(1); }
        link.TrySend(ValCraftLink.EvReleaseAll, null);
        long keysSeen = -1; var tk = sw.ElapsedMilliseconds + 5000;
        while (keysSeen < 0 && sw.ElapsedMilliseconds < tk)
        {
            link.Heartbeat();
            while (link.TryReceive(out var ev)) if (ev.Type == ValCraftLink.EvPong) { long n = BitConverter.ToInt64(ev.Payload, 0); if (n <= 0) keysSeen = -n; }
            Thread.Sleep(1);
        }
        Console.WriteLine($"[cs] input events delivered: {keysSeen}/1000");
        if (keysSeen != 1000) return Fail("input events");

        // 5. reconnect: host restarts (re-initialises the same file, new session)
        uint oldSession = link.Session;
        Volatile.Write(ref collStop, true); collThread.Join();
        link.Dispose();
        Thread.Sleep(300);
        link = new ValCraftLink(path, isHost: true);
        Console.WriteLine($"[cs] host re-initialised: session {oldSession:x} -> {link.Session:x}, previous MC heartbeat age {ValCraftLink.NowMs() - link.PreviousPeerHeartbeatMs} ms");
        if (!WaitHelloAndAck(link, sw)) return Fail("no re-Hello after host restart");
        link.TrySend(ValCraftLink.EvPing, BitConverter.GetBytes(42L));
        bool pong = false; var tp = sw.ElapsedMilliseconds + 5000;
        while (!pong && sw.ElapsedMilliseconds < tp) { link.Heartbeat(); while (link.TryReceive(out var ev)) if (ev.Type == ValCraftLink.EvPong && BitConverter.ToInt64(ev.Payload, 0) == 42) pong = true; Thread.Sleep(1); }
        if (!pong) return Fail("no pong after reconnect");
        Console.WriteLine("[cs] reconnect OK");

        // 6. quit
        link.SetFlag(ValCraftLink.VFlagQuitting, true);
        link.TrySend(ValCraftLink.EvQuit, null);
        link.Dispose();
        Console.WriteLine("[cs] PASS");
        return 0;
    }
}
