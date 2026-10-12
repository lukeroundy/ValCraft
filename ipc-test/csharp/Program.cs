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

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "host") return FakeValheim.Run(args.Length > 1 ? args[1] : ValCraftLink.DefaultPath(), args.Length > 2 ? int.Parse(args[2]) : 300, args.Length > 3 ? args[3] : "cmd.txt");
        path = args.Length > 0 ? args[0] : ValCraftLink.DefaultPath();
        int pings = args.Length > 1 ? int.Parse(args[1]) : 20000;
        var sw = Stopwatch.StartNew();
        var link = new ValCraftLink(path, isHost: true);
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
                if (s.X != s.FrameId || s.Y != s.FrameId * 2.0 || s.Z != -s.FrameId || s.Yaw != (float)(s.FrameId % 360)) torn++;
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
