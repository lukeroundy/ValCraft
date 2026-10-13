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
        if (dyn != 3 || es.Snapshots != 2 || es.Current.Count != 2 || es.Current[7].Flags != (2 | 8 | 4) || Math.Abs(es.Current[7].GroundY - 63f) > 1e-4f || !es.Current.ContainsKey(0x80000000u | 1234)) ok = false;
        Console.WriteLine($"[cs] blocks test: {n} messages, {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "blocks") return BlocksTest(args[1]);
        if (args.Length == 2 && args[0] == "items") return ItemsTest(args[1]);
        if (args.Length == 2 && args[0] == "chest") return ChestTest(args[1]);
        if (args.Length == 2 && args[0] == "chestop") return ChestOpTest(args[1]);
        if (args.Length == 3 && args[0] == "mobs") return MobsTest(args[1], args[2]);
        if (args.Length == 1 && args[0] == "tunnel") return TunnelTest.Run();
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

    static int ItemsTest(string path)
    {
        int fail = 0; void Check(bool c, string m) { if (!c) { fail++; Console.WriteLine("FAIL " + m); } }
        var map = new ValCraft.Protocol.ItemMap();
        int n = map.Load(ValCraft.Protocol.ItemMap.DefaultJson);
        Check(n >= 30, "default entries " + n);
        Check(map.McFor("Wood") == "minecraft:oak_log", "Wood->oak_log");
        Check(map.ValheimFor("minecraft:birch_planks") == "Wood", "planks->Wood");
        Check(map.ValheimFor("minecraft:charcoal") == "Coal", "charcoal->Coal");
        Check(map.ValheimFor("minecraft:dirt") == null, "dirt unmapped");
        Check(map.Load("{\"_c\":[\"x\"], \"Resin\": [\"slime_ball\"]}") == 1 && map.ValheimFor("minecraft:slime_ball") == "Resin", "custom/implicit namespace");
        var g = ValCraft.Protocol.ItemMap.Give(5, 7, "minecraft:flint");
        Check(BitConverter.ToUInt32(g, 0) == 5 && BitConverter.ToUInt16(g, 4) == 7 && BitConverter.ToUInt16(g, 6) == 0xFFFF && System.Text.Encoding.UTF8.GetString(g, 8, g.Length - 8) == "minecraft:flint", "give");
        var g2 = ValCraft.Protocol.ItemMap.Give(6, 1, "minecraft:iron_axe", ValCraft.Protocol.ItemMap.Dur(0.25f));
        Check(BitConverter.ToUInt16(g2, 6) == 2500, "give durability");
        // equipment defaults + merge
        map.Load(ValCraft.Protocol.ItemMap.DefaultJson);
        Check(map.McFor("Hammer") == "minecraft:mace" && map.ValheimFor("minecraft:mace") == "Hammer", "hammer<->mace");
        Check(map.McFor("ArrowBronze") == "minecraft:arrow" && map.ValheimFor("minecraft:arrow") == "ArrowWood", "arrows");
        Check(map.McFor("AxeBronze") == "valcraft:bronze_axe" && map.ValheimFor("valcraft:bronze_sword") == "SwordBronze" && map.McFor("Tin") == "valcraft:tin_ingot", "tools");
        // 0.11.0 generated Valheim items: stand-in first, ValCraft item accepted; new defaults where there was no stand-in
        Check(map.McFor("SwordIron") == "minecraft:diamond_sword" && map.ValheimFor("valcraft:iron_sword") == "SwordIron", "stand-in kept + valcraft appended");
        Check(map.McFor("HelmetFlametal") == "valcraft:flametal_helmet" && map.McFor("SwordMistwalker") == "valcraft:mistwalker" && map.McFor("CookedLoxMeat") == "valcraft:cooked_lox_meat", "new defaults");
        Check(map.McFor("FishingRod") == "minecraft:fishing_rod" && n >= 300, "fishing rod / count " + n);
        Check(map.McFor("TrophyBoar") == "valcraft:trophy_boar" && map.McFor("TrophyDeer") == "valcraft:trophy_deer" && map.ValheimFor("valcraft:trophy_skeleton") == "TrophySkeleton", "0.12.0 trophies");
        var app = ValCraft.Protocol.ItemMap.Merge("{\n  \"SwordIron\": [\"minecraft:diamond_sword\"],\n  \"SwordSilver\": [\"minecraft:iron_sword\"]\n}", out _);
        Check(app.Contains("\"SwordIron\": [\"minecraft:diamond_sword\", \"valcraft:iron_sword\"]") && app.Contains("\"SwordSilver\": [\"minecraft:iron_sword\"]"), "append migration");
        Check(ReferenceEquals(ValCraft.Protocol.ItemMap.Merge(app, out var aa), app) && aa.Count == 0, "append migration idempotent");
        string user = "{\n  \"Wood\": [\"minecraft:spruce_log\"],\n  \"Stone\": [\"minecraft:deepslate\"]\n}";
        var merged = ValCraft.Protocol.ItemMap.Merge(user, out var added);
        var mm = new ValCraft.Protocol.ItemMap(); mm.Load(merged);
        Check(mm.McFor("Wood") == "minecraft:spruce_log" && mm.McFor("Stone") == "minecraft:deepslate", "merge keeps user edits");
        Check(mm.McFor("Hammer") == "minecraft:mace" && added.Contains("Hammer") && !added.Contains("Wood"), "merge adds defaults");
        var again = ValCraft.Protocol.ItemMap.Merge(merged, out var added2);
        Check(ReferenceEquals(again, merged) && added2.Count == 0, "merge idempotent");
        var deleted = System.Text.RegularExpressions.Regex.Replace(merged, ",\\s*\"Hammer\":\\s*\\[[^\\]]*\\]", "");
        ValCraft.Protocol.ItemMap.Merge(deleted, out var added3);
        Check(added3.Count == 0, "deleted default not re-added");
        Console.WriteLine($"[cs] merge added {added.Count} defaults");
        var c = ValCraft.Protocol.ItemMap.Consume(9, 2, 1, "minecraft:coal");
        Check(c[6] == 1 && System.Text.Encoding.UTF8.GetString(c, 7, c.Length - 7) == "minecraft:coal", "consume");
        using (var r = new System.IO.BinaryReader(System.IO.File.OpenRead(path)))
        {
            byte[] Next() { int len = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(r.ReadInt32()); return r.ReadBytes(len); }
            var (hc, hid) = ValCraft.Protocol.ItemMap.Held(Next()); Check(hc == 12 && hid == "minecraft:oak_log", "held");
            var (rq, rm, adur, aid) = ValCraft.Protocol.ItemMap.Ack(Next()); Check(rq == 77 && rm == 3 && adur == 5000 && aid == "minecraft:oak_log", "ack");
            var snap = new ValCraft.Protocol.ItemMap.Snapshot();
            Check(!snap.Add(Next()), "snapshot part1 not final");
            Check(snap.Add(Next()) && snap.Result.Count == 3 && snap.Result["minecraft:iron_ingot"] == 300 && snap.Result["minecraft:oak_log"] == 64, "snapshot");
            var (hp, mx, food, dead) = ValCraft.Protocol.HealthCodec.ReadHealth(Next());
            Check(hp == 13.5f && mx == 20f && food == 17 && dead, "health");
            var dm = ValCraft.Protocol.HealthCodec.Damage(2.5f, 1, 0.6f, -0.8f, 30f, 0xDEADBEEF);
            Check(dm.Length == 21 && BitConverter.ToUInt32(dm, 17) == 0xDEADBEEF && BitConverter.ToSingle(dm, 0) == 2.5f && dm[4] == 1 && BitConverter.ToSingle(dm, 9) == -0.8f && BitConverter.ToSingle(dm, 13) == 30f, "damage");
        }
        // 0.8.5 build piece list: C# encode/decode + file for the Java decoder test
        var bl = new System.Collections.Generic.List<ValCraft.Protocol.BuildCodec.Entry>();
        for (int i = 0; i < 40; i++) bl.Add(new ValCraft.Protocol.BuildCodec.Entry { Index = (ushort)(i * 7), Category = (byte)(i % 3), CanBuild = i % 2 == 0, Name = "Piece " + i + (i == 5 ? " Ä長い" : ""), Cost = i == 9 ? new string('x', 300) : "10 Wood (oak_log), 2 Stone (cobblestone)" });
        var bm = ValCraft.Protocol.BuildCodec.Encode(bl);
        var back = new System.Collections.Generic.List<ValCraft.Protocol.BuildCodec.Entry>();
        foreach (var m in bm) { Check(m.Length <= 248, "build msg size"); back.AddRange(ValCraft.Protocol.BuildCodec.Decode(m)); }
        Check(bm[0][0] == 1 && bm[bm.Count - 1][1] == 1 && back.Count == 40 && back[5].Name == "Piece 5 Ä長い" && back[39].Index == 273 && !back[39].CanBuild && back[9].Cost.Length == 120, "build list");
        using (var w = new System.IO.BinaryWriter(System.IO.File.Create("/tmp/valcraft-build.bin")))
        { w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(bm.Count)); foreach (var m in bm) { w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(m.Length)); w.Write(m); } }
        Console.WriteLine($"[cs] build list: {bm.Count} messages");
        Console.WriteLine(fail == 0 ? "items OK" : $"items {fail} failures");
        return fail == 0 ? 0 : 1;
    }

    // 0.10.0 chest codec (C# encode -> Java decode, Java op -> C# decode) + item map migration
    static int ChestTest(string outFile)
    {
        int fail = 0; void Check(bool c, string m) { if (!c) { fail++; Console.WriteLine("FAIL " + m); } }
        var es = new System.Collections.Generic.List<ValCraft.Protocol.ChestCodec.Entry>();
        for (int i = 0; i < 32; i++) es.Add(new ValCraft.Protocol.ChestCodec.Entry { Slot = (byte)i, Count = (ushort)(i + 1), Dur = i % 4 == 0 ? (ushort)0xFFFF : (ushort)(i * 10), McId = i == 7 ? "" : "valcraft:tin_ingot", Name = "$item_tin Ä" + i });
        var ms = ValCraft.Protocol.ChestCodec.Encode(4242, 8, 4, "Reinforced chest", es);
        foreach (var m in ms) Check(m.Length <= 248, "chest msg size");
        Check(ms.Count > 1 && (ms[0][0] & 1) == 1 && (ms[ms.Count - 1][0] & 2) == 2, "chest flags");
        using (var w = new System.IO.BinaryWriter(System.IO.File.Create(outFile)))
        { w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(ms.Count)); foreach (var m in ms) { w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(m.Length)); w.Write(m); } }
        var old = "{\n  \"Tin\": [\"minecraft:gold_ingot\"],\n  \"Bronze\": [\"minecraft:iron_block\"]\n}";
        var merged = ValCraft.Protocol.ItemMap.Merge(old, out var added);
        Check(merged.Contains("\"Tin\": [\"valcraft:tin_ingot\"]"), "migration old default");
        Check(merged.Contains("\"Bronze\": [\"minecraft:iron_block\"]"), "migration keeps user edit");
        var again = ValCraft.Protocol.ItemMap.Merge(merged, out var added2);
        Check(again == merged && added2.Count == 0, "migration idempotent");
        Console.WriteLine($"[cs] chest: {ms.Count} messages; " + (fail == 0 ? "chest OK" : $"chest {fail} failures"));
        return fail == 0 ? 0 : 1;
    }
    static int ChestOpTest(string file)
    {
        var p = System.IO.File.ReadAllBytes(file);
        var (serial, op, slot, count, dur, id) = ValCraft.Protocol.ChestCodec.Op(p);
        bool ok = serial == 4242 && op == ValCraft.Protocol.ChestCodec.OpDeposit && slot == 31 && count == 64 && dur == 0xFFFF && id == "valcraft:bronze_ingot";
        Console.WriteLine("[cs] chest op " + (ok ? "OK" : "FAIL"));
        return ok ? 0 : 1;
    }

    static int MobsTest(string javaFile, string outFile)
    {
        int fail = 0; void Check(bool c, string m) { if (!c) { fail++; Console.WriteLine("FAIL " + m); } }
        int Rd(System.IO.BinaryReader r) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(r.ReadInt32());
        using (var r = new System.IO.BinaryReader(System.IO.File.OpenRead(javaFile)))
        {
            int n = Rd(r); var all = new System.Collections.Generic.List<ValCraft.Protocol.MobCodec.Mob>(); bool first = false, last = false;
            for (int i = 0; i < n; i++) { var m = r.ReadBytes(Rd(r)); if (i == 0) first = m[0] == 1; if (i == n - 1) last = m[1] == 1; all.AddRange(ValCraft.Protocol.MobCodec.DecodeList(m)); }
            Check(first && last && all.Count == 30 && all[4].Id == 1004 && all[4].Kind == 1 && all[5].Kind == 2 && Math.Abs(all[4].X - 6f) < 1e-4 && Math.Abs(all[3].Z + 3f) < 1e-4 && Math.Abs(all[0].H - 1.95f) < 0.06 && Math.Abs(all[0].HpFrac - 0.5f) < 0.01, "mc mob list");
            Check(all[7].Name == "Zombie Bob \u00e9" && all[0].Name == "Zombie" && Math.Abs(all[7].MaxHp - 27f) < 0.5f && System.Text.Encoding.UTF8.GetByteCount(all[9].Name) <= 32 && all[9].Name.StartsWith("A very long"), "mc mob names/maxHp");
            var (vid, dmg, att) = ValCraft.Protocol.MobCodec.VMobHurt(r.ReadBytes(Rd(r)));
            Check(vid == 77 && dmg == 3.5f && att == 1004, "vmob hurt");
        }
        var hurt = ValCraft.Protocol.MobCodec.MobHurt(5, 2f, 0.5f, -0.5f, 9);
        Check(hurt.Length == 20 && BitConverter.ToUInt32(hurt, 16) == 9, "mob hurt");
        var vl = new System.Collections.Generic.List<ValCraft.Protocol.MobCodec.Mob>();
        for (uint i = 1; i <= 3; i++) vl.Add(new ValCraft.Protocol.MobCodec.Mob { Id = i, Kind = 1, X = i, Y = 69.5f + i, Z = -i, W = 1.2f, H = 2f, HpFrac = 0.25f, MaxHp = i == 2 ? 200 : 50, Name = i == 2 ? "Greyling" : i == 1 ? "" : "Neck" });
        var surf = new float[9]; var tar = new bool[9];
        for (int k = 0; k < 9; k++) surf[k] = float.NaN;
        surf[0] = 62.5f; surf[8] = 61f; tar[8] = true; // cells: x0=10,z0=20 step 2 -> (10,20) water, (12,20) none, (14,24) tar
        using (var w = new System.IO.BinaryWriter(System.IO.File.Create(outFile)))
        {
            var msgs = ValCraft.Protocol.MobCodec.EncodeList(vl);
            w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(msgs.Count));
            foreach (var m in msgs) { w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(m.Length)); w.Write(m); }
            var wb = ValCraft.Protocol.MobCodec.Water(10, 20, 2, 3, 3, surf, tar);
            Check(wb.Length <= 248, "water size");
            Check(ValCraft.Protocol.MobCodec.Water(0, 0, 2, 9, 9, new float[81], new bool[81]).Length <= 248, "9x9 water fits");
            w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(wb.Length)); w.Write(wb);
        }
        Check(ValCraft.Protocol.MobCodec.EncodeList(new System.Collections.Generic.List<ValCraft.Protocol.MobCodec.Mob>()).Count == 1, "empty list = 1 msg");
        Console.WriteLine(fail == 0 ? "mobs OK" : $"mobs {fail} failures");
        return fail == 0 ? 0 : 1;
    }
}
