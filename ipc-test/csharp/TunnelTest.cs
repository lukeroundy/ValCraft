// 0.13.0 tunnel tests: in-process loopback transport (with congestion) and a TCP "fake Valheim network" for e2e runs.
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using ValCraft.Protocol;

/// <summary>Stands in for Valheim's routed RPC between two processes: length-prefixed messages over one TCP socket.
/// Host side: listen; client side: connect. Peer ids: host sees the client as 1, the client sees the host as 0.</summary>
sealed class TcpTransport : ITunnelTransport
{
    NetworkStream _ns; readonly object _w = new object();
    public Action<long, byte[]> OnMessage; public volatile bool Up; public long PeerSeen;
    public static TcpTransport Host(int port)
    {
        var t = new TcpTransport(); var l = new TcpListener(IPAddress.Loopback, port); l.Start();
        new Thread(() => { var c = l.AcceptTcpClient(); c.NoDelay = true; t._ns = c.GetStream(); t.Up = true; Console.WriteLine("[net-sim] client Valheim joined"); t.Read(1); }) { IsBackground = true }.Start();
        return t;
    }
    public static TcpTransport Client(int port)
    {
        var t = new TcpTransport();
        new Thread(() => { while (true) { try { var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); c.NoDelay = true; t._ns = c.GetStream(); break; } catch { Thread.Sleep(300); } } t.Up = true; Console.WriteLine("[net-sim] joined host Valheim"); t.Read(0); }) { IsBackground = true }.Start();
        return t;
    }
    void Read(long from)
    {
        var hdr = new byte[4];
        try
        {
            while (true)
            {
                Fill(hdr); int n = BitConverter.ToInt32(hdr, 0); var m = new byte[n]; Fill(m);
                OnMessage?.Invoke(from, m);
            }
        }
        catch (Exception e) { Up = false; Console.WriteLine("[net-sim] link down: " + e.Message); }
    }
    void Fill(byte[] b) { int o = 0; while (o < b.Length) { int r = _ns.Read(b, o, b.Length - o); if (r <= 0) throw new Exception("eof"); o += r; } }
    public bool CanSend(long peer) => Up;
    public void Send(long peer, byte[] msg) { if (!Up) return; lock (_w) { try { _ns.Write(BitConverter.GetBytes(msg.Length), 0, 4); _ns.Write(msg, 0, msg.Length); } catch { Up = false; } } }
}

/// <summary>In-process: queues between two cores, random congestion, delivery on its own thread.</summary>
sealed class LoopTransport : ITunnelTransport
{
    public LoopTransport Other; public TunnelCore Core; public long MyIdAtOther;
    readonly BlockingCollection<byte[]> _q = new BlockingCollection<byte[]>();
    readonly Random _r = new Random(7); public long Msgs, MaxMsg;
    public void Start() => new Thread(() => { foreach (var m in _q.GetConsumingEnumerable()) Other.Core.OnMessage(MyIdAtOther, m); }) { IsBackground = true }.Start();
    public bool CanSend(long peer) { lock (_r) return _q.Count < 64 && _r.NextDouble() > 0.2; }
    public void Send(long peer, byte[] msg) { Msgs++; if (msg.Length > MaxMsg) MaxMsg = msg.Length; _q.Add(msg); }
}

static class TunnelTest
{
    public static int Run()
    {
        // echo server = "host Minecraft"
        var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start(); int echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        int accepted = 0;
        new Thread(() => { while (true) { var c = echo.AcceptTcpClient(); Interlocked.Increment(ref accepted); new Thread(() => { var ns = c.GetStream(); var b = new byte[50000]; try { int n; while ((n = ns.Read(b, 0, b.Length)) > 0) ns.Write(b, 0, n); } catch { } c.Close(); }) { IsBackground = true }.Start(); } }) { IsBackground = true }.Start();

        var th = new LoopTransport { MyIdAtOther = 0 }; var tc = new LoopTransport { MyIdAtOther = 42 };
        th.Other = tc; tc.Other = th;
        var host = new TunnelCore(true, th) { Target = () => new IPEndPoint(IPAddress.Loopback, echoPort), Log = s => Console.WriteLine("  host " + s) };
        var client = new TunnelCore(false, tc) { ServerPeer = 0, Log = s => Console.WriteLine("  client " + s) };
        th.Core = host; tc.Core = client; th.Start(); tc.Start();
        bool run = true;
        new Thread(() => { while (run) { host.Pump(); client.Pump(); Thread.Sleep(1); } }) { IsBackground = true }.Start();
        int port = client.StartListener();
        bool ok = true;
        // 3 concurrent streams, 8 MB each, echoed back and hashed
        var threads = new Thread[3]; var results = new bool[3];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < threads.Length; i++)
        {
            int k = i;
            threads[i] = new Thread(() =>
            {
                var data = new byte[8 << 20]; new Random(k).NextBytes(data);
                using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); var ns = c.GetStream();
                var back = new byte[data.Length]; int got = 0;
                var rd = new Thread(() => { while (got < back.Length) { int n = ns.Read(back, got, back.Length - got); if (n <= 0) break; got += n; } });
                rd.Start();
                for (int o = 0; o < data.Length; o += 100000) ns.Write(data, o, Math.Min(100000, data.Length - o));
                rd.Join(60000);
                results[k] = got == data.Length && Convert.ToBase64String(SHA256.HashData(back)) == Convert.ToBase64String(SHA256.HashData(data));
            });
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();
        double secs = sw.Elapsed.TotalSeconds;
        for (int i = 0; i < 3; i++) { Console.WriteLine($"[tunnel] stream {i}: 8 MB echoed intact = {results[i]}"); ok &= results[i]; }
        Console.WriteLine($"[tunnel] 48 MB through in {secs:F1} s, messages {th.Msgs + tc.Msgs}, largest {Math.Max(th.MaxMsg, tc.MaxMsg)} B (limit {TunnelCore.Chunk + 5})");
        ok &= Math.Max(th.MaxMsg, tc.MaxMsg) <= TunnelCore.Chunk + 5;
        Thread.Sleep(500);
        Console.WriteLine($"[tunnel] after close: host streams {host.StreamCount}, client streams {client.StreamCount}");
        ok &= host.StreamCount == 0 && client.StreamCount == 0;

        // backpressure: a reader that never reads -> in-flight bounded by the window (sender blocks, no unbounded queue)
        using (var c = new TcpClient())
        {
            c.Connect(IPAddress.Loopback, port); var ns = c.GetStream(); long sent = 0; var big = new byte[1 << 20];
            var wt = new Thread(() => { try { while (true) { ns.Write(big, 0, big.Length); Interlocked.Add(ref sent, big.Length); } } catch { } }) { IsBackground = true };
            wt.Start(); Thread.Sleep(3000);
            long s1 = Interlocked.Read(ref sent);
            // the echo server writes back to our socket which we never read -> everything stalls after a few MB of OS buffers + 2 windows
            Console.WriteLine($"[tunnel] backpressure: writer stalled after {s1 / (1 << 20)} MB (expect a few MB, not unbounded)");
            ok &= s1 < 64L << 20;
        }
        Thread.Sleep(800);
        Console.WriteLine($"[tunnel] stalled stream closed by client: host streams {host.StreamCount}, client streams {client.StreamCount}");

        // host Minecraft not running -> connection refused cleanly
        host.Target = () => null;
        using (var c = new TcpClient())
        {
            c.Connect(IPAddress.Loopback, port); var ns = c.GetStream(); ns.ReadTimeout = 5000; int n = -1;
            try { n = ns.Read(new byte[16], 0, 16); } catch { }
            Console.WriteLine($"[tunnel] host MC not ready: client socket closed = {n == 0}");
            ok &= n == 0;
        }
        // Valheim peer leaves -> streams dropped
        host.Target = () => new IPEndPoint(IPAddress.Loopback, echoPort);
        var c2 = new TcpClient(); c2.Connect(IPAddress.Loopback, port); c2.GetStream().Write(new byte[10]); Thread.Sleep(300);
        host.DropPeer(42); Thread.Sleep(200);
        Console.WriteLine($"[tunnel] peer left: host streams {host.StreamCount}");
        ok &= host.StreamCount == 0;
        c2.Close(); run = false; host.Dispose(); client.Dispose();
        Console.WriteLine(ok ? "[tunnel] PASS" : "[tunnel] FAIL");
        return ok ? 0 : 1;
    }
}
