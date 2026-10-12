// ValCraft shared-memory link (C# side). See PROTOCOL.md. Shared by the Valheim plugin and the IPC test.
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace ValCraft.Protocol
{
    public struct PlayerState
    {
        public long FrameId;
        public double X, Y, Z;
        public float Yaw, Pitch, Roll, FovDeg;
        public uint Flags;
    }

    public struct LinkEvent
    {
        public ushort Type;
        public byte[] Payload;
    }

    public sealed unsafe class ValCraftLink : IDisposable
    {
        public const uint Magic = 0x434C4156; // "VALC"
        public const uint Version = 1;
        public const int OffMagic = 0x0, OffVersion = 0x4, OffSize = 0x8, OffValheimPid = 0x10, OffMcPid = 0x14;
        public const int OffValheimHeartbeat = 0x18, OffMcHeartbeat = 0x20, OffValheimFlags = 0x28, OffMcFlags = 0x2C;
        public const int OffPlayerState = 0x100;
        public const int RingSlots = 256, SlotSize = 256, SlotPayload = SlotSize - 8, RingHeader = 0x80;
        public const int RingBytes = RingHeader + RingSlots * SlotSize; // 0x10080
        public const int OffRingValheimToMc = 0x1000;
        public const int OffRingMcToValheim = OffRingValheimToMc + RingBytes; // 0x11080
        public const long TotalSize = OffRingMcToValheim + RingBytes;          // 0x21100 (file is padded to FileSize)
        public const long FileSize = 0x22100;

        public const ushort EvHello = 1, EvPing = 2, EvPong = 3;

        readonly MemoryMappedFile _mmf;
        readonly MemoryMappedViewAccessor _view;
        readonly byte* _base;
        readonly bool _isHost;
        public string Path { get; }

        public static string DefaultPath()
        {
            var env = Environment.GetEnvironmentVariable("VALCRAFT_SHM");
            if (!string.IsNullOrEmpty(env)) return env;
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return System.IO.Path.Combine(System.IO.Path.Combine(root, "ValCraft"), "valcraft.shm");
        }

        /// <param name="isHost">true = Valheim side: creates and initialises the file.</param>
        public ValCraftLink(string path, bool isHost)
        {
            Path = path;
            _isHost = isHost;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (isHost) fs.SetLength(FileSize);
            else if (fs.Length < FileSize) { fs.Dispose(); throw new IOException("ValCraft link file not initialised yet"); }
            // mapName null: file-backed map, no named kernel object needed (works on Mono/Linux too).
            _mmf = MemoryMappedFile.CreateFromFile(fs, null, FileSize, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, false);
            _view = _mmf.CreateViewAccessor(0, FileSize, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p + _view.PointerOffset;
            if (isHost) InitHeader();
        }

        void InitHeader()
        {
            WriteU32(OffMagic, 0);
            Thread.MemoryBarrier();
            for (long i = 4; i < FileSize; i++) _base[i] = 0;
            WriteU32(OffVersion, Version);
            *(long*)(_base + OffSize) = FileSize;
            WriteU32(OffValheimPid, (uint)System.Diagnostics.Process.GetCurrentProcess().Id);
            Thread.MemoryBarrier();
            Volatile.Write(ref *(int*)(_base + OffMagic), unchecked((int)Magic));
        }

        public bool IsReady => (uint)Volatile.Read(ref *(int*)(_base + OffMagic)) == Magic && ReadU32(OffVersion) == Version;

        uint ReadU32(int off) => (uint)Volatile.Read(ref *(int*)(_base + off));
        void WriteU32(int off, uint v) => Volatile.Write(ref *(int*)(_base + off), unchecked((int)v));
        long ReadI64(int off) => Volatile.Read(ref *(long*)(_base + off));
        void WriteI64(int off, long v) => Volatile.Write(ref *(long*)(_base + off), v);

        public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ---- heartbeats / flags ----
        public void Heartbeat()
        {
            if (_isHost) WriteI64(OffValheimHeartbeat, NowMs());
            else { WriteU32(OffMcPid, (uint)System.Diagnostics.Process.GetCurrentProcess().Id); WriteI64(OffMcHeartbeat, NowMs()); }
        }
        public long PeerHeartbeatMs => ReadI64(_isHost ? OffMcHeartbeat : OffValheimHeartbeat);
        public bool PeerAlive(long timeoutMs = 2000) { var hb = PeerHeartbeatMs; return hb != 0 && NowMs() - hb < timeoutMs; }
        public uint PeerPid => ReadU32(_isHost ? OffMcPid : OffValheimPid);
        public void SetFlags(uint f) => WriteU32(_isHost ? OffValheimFlags : OffMcFlags, f);
        public uint PeerFlags => ReadU32(_isHost ? OffMcFlags : OffValheimFlags);

        // ---- seqlock PlayerState (written by MC; C# writer exists for tests) ----
        public void WritePlayerState(in PlayerState s)
        {
            byte* b = _base + OffPlayerState;
            uint seq = ReadU32(OffPlayerState);
            WriteU32(OffPlayerState, seq + 1);
            Thread.MemoryBarrier();
            *(long*)(b + 8) = s.FrameId;
            *(double*)(b + 16) = s.X; *(double*)(b + 24) = s.Y; *(double*)(b + 32) = s.Z;
            *(float*)(b + 40) = s.Yaw; *(float*)(b + 44) = s.Pitch; *(float*)(b + 48) = s.Roll; *(float*)(b + 52) = s.FovDeg;
            *(uint*)(b + 56) = s.Flags;
            Thread.MemoryBarrier();
            WriteU32(OffPlayerState, seq + 2);
        }

        public bool TryReadPlayerState(out PlayerState s, int maxRetries = 64)
        {
            byte* b = _base + OffPlayerState;
            for (int i = 0; i < maxRetries; i++)
            {
                uint s1 = ReadU32(OffPlayerState);
                if ((s1 & 1) != 0) { Thread.SpinWait(8); continue; }
                s.FrameId = *(long*)(b + 8);
                s.X = *(double*)(b + 16); s.Y = *(double*)(b + 24); s.Z = *(double*)(b + 32);
                s.Yaw = *(float*)(b + 40); s.Pitch = *(float*)(b + 44); s.Roll = *(float*)(b + 48); s.FovDeg = *(float*)(b + 52);
                s.Flags = *(uint*)(b + 56);
                Thread.MemoryBarrier();
                if (ReadU32(OffPlayerState) == s1) return s1 != 0;
            }
            s = default; return false;
        }

        // ---- SPSC rings ----
        int OutRing => _isHost ? OffRingValheimToMc : OffRingMcToValheim;
        int InRing => _isHost ? OffRingMcToValheim : OffRingValheimToMc;

        public bool TrySend(ushort type, byte[] payload)
        {
            int len = payload?.Length ?? 0;
            if (len > SlotPayload) throw new ArgumentException("payload too large");
            int r = OutRing;
            long head = *(long*)(_base + r);           // producer owns head
            long tail = ReadI64(r + 0x40);
            if (head - tail >= RingSlots) return false;   // full
            byte* slot = _base + r + RingHeader + (head % RingSlots) * SlotSize;
            *(ushort*)slot = type; *(ushort*)(slot + 2) = 0; *(uint*)(slot + 4) = (uint)len;
            for (int i = 0; i < len; i++) slot[8 + i] = payload[i];
            WriteI64(r, head + 1);                        // release
            return true;
        }

        public bool TryReceive(out LinkEvent ev)
        {
            int r = InRing;
            long tail = *(long*)(_base + r + 0x40);      // consumer owns tail
            long head = ReadI64(r);                       // acquire
            if (tail == head) { ev = default; return false; }
            byte* slot = _base + r + RingHeader + (tail % RingSlots) * SlotSize;
            uint len = *(uint*)(slot + 4);
            if (len > SlotPayload) len = SlotPayload;
            var data = new byte[len];
            for (int i = 0; i < len; i++) data[i] = slot[8 + i];
            ev = new LinkEvent { Type = *(ushort*)slot, Payload = data };
            WriteI64(r + 0x40, tail + 1);                 // release
            return true;
        }

        public void Dispose()
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mmf.Dispose();
        }
    }
}
