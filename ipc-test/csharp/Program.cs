// Host-side (Valheim role) IPC test: creates the link, heartbeats, pings the Java peer N times,
// checks each Pong, and validates seqlocked PlayerState snapshots written concurrently by Java.
using System;
using System.Diagnostics;
using System.Threading;
using ValCraft.Protocol;

static class Program
{
    static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : ValCraftLink.DefaultPath();
        int pings = args.Length > 1 ? int.Parse(args[1]) : 10000;
        using var link = new ValCraftLink(path, isHost: true);
        Console.WriteLine($"[cs] created {path}");
        var sw = Stopwatch.StartNew();
        // wait for peer heartbeat
        while (!link.PeerAlive()) { link.Heartbeat(); if (sw.ElapsedMilliseconds > 30000) { Console.WriteLine("[cs] FAIL: no peer heartbeat"); return 1; } Thread.Sleep(10); }
        Console.WriteLine($"[cs] peer alive, pid {link.PeerPid}");

        long sent = 0, received = 0, torn = 0, snapshots = 0; bool hello = false;
        long lastHb = 0;
        sw.Restart();
        while (received < pings)
        {
            if (sw.ElapsedMilliseconds - lastHb > 100) { link.Heartbeat(); lastHb = sw.ElapsedMilliseconds; }
            while (sent < pings && sent - received < 200 && link.TrySend(ValCraftLink.EvPing, BitConverter.GetBytes(sent + 1))) sent++;
            while (link.TryReceive(out var ev))
            {
                if (ev.Type == ValCraftLink.EvHello) { hello = true; continue; }
                if (ev.Type != ValCraftLink.EvPong) { Console.WriteLine($"[cs] FAIL: unexpected type {ev.Type}"); return 1; }
                long n = BitConverter.ToInt64(ev.Payload, 0);
                if (n != received + 1) { Console.WriteLine($"[cs] FAIL: pong {n} expected {received + 1}"); return 1; }
                received++;
            }
            if (link.TryReadPlayerState(out var s))
            {
                snapshots++;
                if (s.X != s.FrameId || s.Y != s.FrameId * 2.0 || s.Z != -s.FrameId || s.Yaw != (float)(s.FrameId % 360)) torn++;
            }
            if (sw.ElapsedMilliseconds > 60000) { Console.WriteLine($"[cs] FAIL: timeout, received {received}"); return 1; }
        }
        link.TrySend(100, null); // quit
        Console.WriteLine($"[cs] {received} ping/pong round trips in {sw.ElapsedMilliseconds} ms; hello={hello}; playerState snapshots={snapshots}, torn={torn}");
        bool ok = hello && torn == 0 && snapshots > 0;
        Console.WriteLine(ok ? "[cs] PASS" : "[cs] FAIL");
        return ok ? 0 : 1;
    }
}
