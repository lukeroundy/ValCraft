// 0.13.0: Minecraft TCP connections tunnelled over a message transport (Valheim routed RPC in game, a loopback in tests).
// Transport-agnostic and Unity-free so ipc-test can run it. Wire message: u8 kind, i32 stream id (LE), payload.
//   Open (client->host), Data (bytes), Close (either side), Ack (u32 bytes written to the local socket: flow-control credit).
// Threading: socket IO on background threads; every transport Send happens inside Pump() (Valheim: main thread).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ValCraft.Protocol
{
    public interface ITunnelTransport
    {
        /// <summary>False while the transport to that peer is congested (Valheim: socket send queue too long).</summary>
        bool CanSend(long peer);
        void Send(long peer, byte[] msg);
    }

    public sealed class TunnelCore : IDisposable
    {
        public const byte KOpen = 1, KData = 2, KClose = 3, KAck = 4;
        public const int Chunk = 32 * 1024;          // bytes per Data message (well under Valheim's RPC limits)
        public const int Window = 1024 * 1024;       // max un-acked bytes in flight per stream and direction
        public const int AckEvery = 64 * 1024;
        public int PumpBudget = 512 * 1024;          // bytes per peer per Pump call

        public readonly bool IsHost;
        readonly ITunnelTransport _t;
        public Action<string> Log = _ => { };
        /// <summary>Host: where each tunnelled connection goes (the host's MC LAN port); null = not ready (connection refused).</summary>
        public Func<IPEndPoint> Target;
        /// <summary>Client: transport peer id of the host.</summary>
        public long ServerPeer;
        public int ListenPort { get; private set; }
        public long BytesUp, BytesDown;
        public int StreamCount => _streams.Count;

        TcpListener _listener; Thread _accept; volatile bool _disposed; int _nextId;
        readonly ConcurrentDictionary<(long, int), Stream> _streams = new ConcurrentDictionary<(long, int), Stream>();
        readonly ConcurrentQueue<(long, byte[])> _control = new ConcurrentQueue<(long, byte[])>();

        sealed class Stream
        {
            public long Peer; public int Id; public TcpClient Tcp; public NetworkStream Ns;
            public readonly ConcurrentQueue<byte[]> Out = new ConcurrentQueue<byte[]>();
            public readonly BlockingCollection<byte[]> In = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
            public long InFlight; public long Written;
            public readonly object Gate = new object();
            public volatile bool Closed, CloseQueued, CloseSent, RemoteClosed;
        }

        public TunnelCore(bool isHost, ITunnelTransport transport) { IsHost = isHost; _t = transport; }

        static byte[] Msg(byte kind, int id, byte[] data, int off, int n)
        {
            var m = new byte[5 + n]; m[0] = kind; BitConverter.GetBytes(id).CopyTo(m, 1);
            if (n > 0) Buffer.BlockCopy(data, off, m, 5, n);
            return m;
        }

        /// <summary>Client: listen on 127.0.0.1 (port 0 = any free port); each accepted connection opens a stream to the host.</summary>
        public int StartListener(int port = 0)
        {
            _listener = new TcpListener(IPAddress.Loopback, port); _listener.Start();
            ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "ValCraft tunnel accept" }; _accept.Start();
            Log($"[net] tunnel listening on 127.0.0.1:{ListenPort}");
            return ListenPort;
        }

        void AcceptLoop()
        {
            while (!_disposed)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); } catch { if (_disposed) return; Thread.Sleep(100); continue; }
                c.NoDelay = true;
                var s = new Stream { Peer = ServerPeer, Id = Interlocked.Increment(ref _nextId), Tcp = c, Ns = c.GetStream() };
                _streams[(s.Peer, s.Id)] = s;
                s.Out.Enqueue(Msg(KOpen, s.Id, null, 0, 0));
                Log($"[net] MC connection {s.Id} -> host {s.Peer}");
                StartIo(s);
            }
        }

        void StartIo(Stream s)
        {
            new Thread(() => ReadLoop(s)) { IsBackground = true, Name = "ValCraft tunnel rd " + s.Id }.Start();
            new Thread(() => WriteLoop(s)) { IsBackground = true, Name = "ValCraft tunnel wr " + s.Id }.Start();
        }

        void ReadLoop(Stream s)
        {
            var buf = new byte[Chunk];
            try
            {
                while (!s.Closed)
                {
                    lock (s.Gate) while (Interlocked.Read(ref s.InFlight) >= Window && !s.Closed) Monitor.Wait(s.Gate, 500);
                    if (s.Closed) break;
                    int n = s.Ns.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    Interlocked.Add(ref s.InFlight, n);
                    s.Out.Enqueue(Msg(KData, s.Id, buf, 0, n));
                }
            }
            catch { }
            LocalClose(s, "local socket closed");
        }

        void WriteLoop(Stream s)
        {
            try
            {
                foreach (var b in s.In.GetConsumingEnumerable())
                {
                    s.Ns.Write(b, 0, b.Length);
                    s.Written += b.Length;
                    if (s.Written >= AckEvery) { _control.Enqueue((s.Peer, Msg(KAck, s.Id, BitConverter.GetBytes((uint)s.Written), 0, 4))); s.Written = 0; }
                }
            }
            catch { LocalClose(s, "write failed"); }
            if (s.RemoteClosed) Dispose(s);
        }

        /// <summary>Our side ended: tell the peer (after any queued data), stop writing.</summary>
        void LocalClose(Stream s, string why)
        {
            lock (s.Gate)
            {
                if (s.CloseQueued) return;
                s.CloseQueued = true;
                if (!s.RemoteClosed) s.Out.Enqueue(Msg(KClose, s.Id, null, 0, 0));
                s.Closed = true; Monitor.PulseAll(s.Gate);
            }
            try { s.In.CompleteAdding(); } catch { }
            Log($"[net] stream {s.Peer}/{s.Id} closing ({why})");
            if (s.RemoteClosed) Dispose(s);
        }

        void Dispose(Stream s)
        {
            s.Closed = true;
            try { s.Tcp?.Close(); } catch { }
            lock (s.Gate) Monitor.PulseAll(s.Gate);
            if (s.RemoteClosed || s.CloseSent || !s.CloseQueued) _streams.TryRemove((s.Peer, s.Id), out _);
        }

        /// <summary>A message from the transport (any thread).</summary>
        public void OnMessage(long peer, byte[] m)
        {
            if (m == null || m.Length < 5 || _disposed) return;
            byte kind = m[0]; int id = BitConverter.ToInt32(m, 1);
            if (!IsHost) peer = ServerPeer;
            _streams.TryGetValue((peer, id), out var s);
            switch (kind)
            {
                case KOpen:
                    if (!IsHost || s != null) return;
                    s = new Stream { Peer = peer, Id = id };
                    _streams[(peer, id)] = s;
                    var st = s;
                    new Thread(() => ConnectTarget(st)) { IsBackground = true, Name = "ValCraft tunnel connect" }.Start();
                    break;
                case KData:
                    if (s == null) { _control.Enqueue((peer, Msg(KClose, id, null, 0, 0))); return; }
                    var d = new byte[m.Length - 5]; Buffer.BlockCopy(m, 5, d, 0, d.Length);
                    Interlocked.Add(ref BytesDown, d.Length);
                    try { if (!s.In.IsAddingCompleted) s.In.Add(d); } catch (InvalidOperationException) { }
                    break;
                case KAck:
                    if (s == null || m.Length < 9) return;
                    Interlocked.Add(ref s.InFlight, -BitConverter.ToUInt32(m, 5));
                    lock (s.Gate) Monitor.PulseAll(s.Gate);
                    break;
                case KClose:
                    if (s == null) return;
                    s.RemoteClosed = true;
                    try { s.In.CompleteAdding(); } catch { }   // writer drains what's left, then closes the socket
                    lock (s.Gate) { s.Closed = true; Monitor.PulseAll(s.Gate); }
                    if (s.Tcp == null || s.CloseQueued) Dispose(s);
                    break;
            }
        }

        void ConnectTarget(Stream s)
        {
            var ep = Target?.Invoke();
            if (ep == null) { Log($"[net] peer {s.Peer} stream {s.Id}: host Minecraft not ready, refused"); LocalClose(s, "no target"); return; }
            try
            {
                var c = new TcpClient { NoDelay = true };
                var ar = c.BeginConnect(ep.Address, ep.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(5000) || !c.Connected) throw new SocketException((int)SocketError.TimedOut);
                c.EndConnect(ar);
                s.Tcp = c; s.Ns = c.GetStream();
                Log($"[net] peer {s.Peer} stream {s.Id} -> Minecraft {ep}");
                if (s.RemoteClosed) { Dispose(s); return; }
                StartIo(s);
            }
            catch (Exception e) { Log($"[net] peer {s.Peer} stream {s.Id}: connect {ep} failed: {e.Message}"); LocalClose(s, "connect failed"); }
        }

        /// <summary>Send queued messages; call often (Valheim: every frame, main thread).</summary>
        public void Pump()
        {
            while (_control.TryDequeue(out var c)) _t.Send(c.Item1, c.Item2);
            var budget = new Dictionary<long, int>();
            foreach (var kv in _streams)
            {
                var s = kv.Value;
                if (!budget.TryGetValue(s.Peer, out int left)) left = PumpBudget;
                while (left > 0 && s.Out.TryPeek(out var m))
                {
                    if (!_t.CanSend(s.Peer)) break;
                    s.Out.TryDequeue(out m);
                    _t.Send(s.Peer, m);
                    if (m[0] == KData) { left -= m.Length; Interlocked.Add(ref BytesUp, m.Length - 5); }
                    if (m[0] == KClose) { s.CloseSent = true; if (s.Tcp == null || s.In.IsCompleted) Dispose(s); }
                }
                budget[s.Peer] = left;
                if (s.Closed && s.CloseSent && s.Out.IsEmpty) _streams.TryRemove(kv.Key, out _);
                if (s.Closed && s.RemoteClosed && s.Out.IsEmpty && s.In.IsCompleted) { try { s.Tcp?.Close(); } catch { } _streams.TryRemove(kv.Key, out _); }
            }
        }

        /// <summary>The transport lost a peer (Valheim player left): drop its connections without messages.</summary>
        public void DropPeer(long peer)
        {
            foreach (var kv in _streams)
                if (kv.Key.Item1 == peer) { var s = kv.Value; s.RemoteClosed = true; s.Closed = true; try { s.In.CompleteAdding(); } catch { } try { s.Tcp?.Close(); } catch { } lock (s.Gate) Monitor.PulseAll(s.Gate); _streams.TryRemove(kv.Key, out _); Log($"[net] peer {peer} gone: stream {s.Id} dropped"); }
        }

        public void Dispose()
        {
            _disposed = true;
            // best effort: tell the other side (the session may already be gone)
            foreach (var kv in _streams) { var s = kv.Value; if (!s.CloseSent && !s.RemoteClosed) try { if (_t.CanSend(s.Peer)) _t.Send(s.Peer, Msg(KClose, s.Id, null, 0, 0)); } catch { } }
            try { _listener?.Stop(); } catch { }
            foreach (var kv in _streams) { var s = kv.Value; s.Closed = true; try { s.In.CompleteAdding(); } catch { } try { s.Tcp?.Close(); } catch { } lock (s.Gate) Monitor.PulseAll(s.Gate); }
            _streams.Clear();
        }
    }
}
