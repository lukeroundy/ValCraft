// ValCraft shared-memory link (C# side), protocol v2. See PROTOCOL.md. Shared by the Valheim plugin and the IPC test.
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace ValCraft.Protocol
{
    /// <summary>Minecraft camera + player, MC coordinates (blocks = metres; X east, Y up, Z south; yaw 0 = south, +90 = west; pitch + = down).</summary>
    public struct PlayerState
    {
        public long FrameId;
        public double X, Y, Z;              // camera (eye) position, incl. 3rd-person offset
        public float Yaw, Pitch, Roll, FovDeg; // camera angles; FovDeg = MC's effective vertical FOV this frame (sprint, effects)
        public uint Flags;                   // PsOnGround | PsFlying | ...
        public float BobX, BobY, BobRollDeg, BobPitchDeg; // MC view-bob transform (applied to the world in view space)
        public double FeetX, FeetY, FeetZ;   // player entity position (interpolated)
        public float EyeHeight;
    }

    /// <summary>MC (right-handed, +Z south) <-> Valheim/Unity (left-handed, +Z north). Both Y-up, 1 block = 1 m.</summary>
    public static class CoordMap
    {
        /// <summary>MC position -> Unity position (before adding the world anchor): (x, y, -z).</summary>
        public static void ToUnity(double x, double y, double z, out float ux, out float uy, out float uz) { ux = (float)x; uy = (float)y; uz = (float)-z; }
        /// <summary>MC yaw (0 = facing +Z south, +90 = facing -X west) -> Unity yaw (Euler Y, 0 = facing +Z north, +90 = east).</summary>
        public static float YawToUnity(float mcYaw) { float y = (mcYaw + 180f) % 360f; return y < 0 ? y + 360f : y; }
        /// <summary>MC pitch (+ = looking down) -> Unity Euler X (+ = looking down): same value.</summary>
        public static float PitchToUnity(float mcPitch) => mcPitch;
        /// <summary>Unity forward vector for a Unity yaw (deg).</summary>
        public static void Forward(float unityYaw, out float fx, out float fz) { double r = unityYaw * Math.PI / 180; fx = (float)Math.Sin(r); fz = (float)Math.Cos(r); }
    }

    public struct LinkEvent
    {
        public ushort Type;
        public byte[] Payload;
    }

    /// <summary>A frame buffer the reader may copy from; check <see cref="ValCraftLink.FrameStillValid"/> after copying.</summary>
    public unsafe struct FrameView
    {
        public long Seq;
        public int Index, Width, Height, Stride;
        public byte* Data;
        public int Bytes => Stride * Height;
    }

    public sealed unsafe class ValCraftLink : IDisposable
    {
        public const uint Magic = 0x434C4156; // "VALC"
        public const uint Version = 4;
        // header
        public const int OffMagic = 0x0, OffVersion = 0x4, OffSize = 0x8, OffValheimPid = 0x10, OffMcPid = 0x14;
        public const int OffValheimHeartbeat = 0x18, OffMcHeartbeat = 0x20, OffValheimFlags = 0x28, OffMcFlags = 0x2C;
        public const int OffSession = 0x30, OffMcAckedSession = 0x34;
        // per-frame player state (seqlock, MC writes)
        public const int OffPlayerState = 0x100;
        // frame control
        public const int OffFrameReqW = 0x200, OffFrameReqH = 0x204, OffFrameMaxW = 0x208, OffFrameMaxH = 0x20C;
        public const int OffFrameLatestSeq = 0x210, OffFrameLatestIndex = 0x218;
        public const int OffFrameBufHdr = 0x240, FrameBufHdrSize = 0x40; // u64 seq, u32 w, u32 h, u32 stride, u32 format
        public const int FrameMaxW = 1920, FrameMaxH = 1080, FrameFormatRgba8BottomUp = 1;
        // rings
        public const int RingSlots = 256, SlotSize = 256, SlotPayload = SlotSize - 8, RingHeader = 0x80;
        public const int RingBytes = RingHeader + RingSlots * SlotSize;          // 0x10080
        public const int OffRingValheimToMc = 0x1000;
        public const int OffRingMcToValheim = OffRingValheimToMc + RingBytes;   // 0x11080, ends 0x21100
        // frame pixel buffers
        public const long OffFrameData = 0x22000;
        public const long FrameBytes = (long)FrameMaxW * FrameMaxH * 4;          // 0x7E9000
        // v4: collision snapshot region (Valheim -> MC), double-buffered, boxes as 6 x f32 AABBs in MC coordinates.
        public const long OffColl = OffFrameData + 2 * FrameBytes;               // 0xFF4000
        public const int CollMaxBoxes = 32768, CollBoxBytes = 24;
        public const long OffCollData = OffColl + 0x40;
        public const long FileSize = OffCollData + 2L * CollMaxBoxes * CollBoxBytes; // 0x1174040

        // Valheim flags
        public const uint VFlagInGame = 1, VFlagMenuOpen = 2, VFlagLoading = 4, VFlagInputActive = 8, VFlagQuitting = 16;
        // Minecraft flags
        public const uint McFlagWorldLoaded = 1, McFlagHidden = 2, McFlagScreenOpen = 4;
        // PlayerState flags
        public const uint PsOnGround = 1, PsFlying = 2, PsCrouching = 4, PsInWater = 8, PsThirdPerson = 16, PsHasPlayer = 32, PsViewBob = 64;

        // events
        public const ushort EvHello = 1, EvPing = 2, EvPong = 3, EvHelloAck = 4, EvQuit = 5;
        public const ushort EvKey = 10, EvChar = 11, EvMouseMove = 12, EvMouseButton = 13, EvScroll = 14, EvReleaseAll = 15;
        /// <summary>V2M: f32 width, f32 height (m) for the MC player hitbox (0,0 = vanilla). Eye = 0.9 x height.</summary>
        public const ushort EvPlayerDims = 20;
        /// <summary>V2M: UTF-8 Valheim hover text under the crosshair (empty = nothing interactable).</summary>
        public const ushort EvHoverText = 21;
        /// <summary>M2V: MC right-click with no MC entity targeted and Valheim hover present -> Valheim interact.</summary>
        public const ushort EvInteract = 22;
        /// <summary>V2M terrain tile: i32 tileX, i32 tileZ (MC block coords / 8), then 64 columns (x-major, 8x8) of
        /// i16 topY (MC Y of the top solid block; short.MinValue = no data) + u8 material (TerrainMat).</summary>
        public const ushort EvTerrainTile = 23;
        /// <summary>M2V: i32 x, y, z (MC block), i32 kind (0 = broken, 1 = placed), UTF-8 block id. Phase B hook (logged only).</summary>
        public const ushort EvBlockChanged = 24;
        /// <summary>V2M: f32 x, y, z MC feet position to teleport the MC player to (vertical re-anchoring).</summary>
        public const ushort EvTeleport = 25;
        public const int TerrainTile = 8;
        public const uint VFlagTerrainBlocks = 32;

        readonly MemoryMappedFile _mmf;
        readonly MemoryMappedViewAccessor _view;
        readonly byte* _base;
        readonly bool _isHost;
        public string Path { get; }
        public uint Session { get; private set; }
        /// <summary>Host only: Minecraft heartbeat found in the file before it was re-initialised (0 if none).</summary>
        public long PreviousPeerHeartbeatMs { get; private set; }

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
            if (isHost) { if (fs.Length != FileSize) fs.SetLength(FileSize); }
            else if (fs.Length < FileSize) { fs.Dispose(); throw new IOException("ValCraft link file not initialised yet"); }
            _mmf = MemoryMappedFile.CreateFromFile(fs, null, FileSize, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            _view = _mmf.CreateViewAccessor(0, FileSize, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p + _view.PointerOffset;
            if (isHost) InitHeader();
            else Session = ReadU32(OffSession);
        }

        void InitHeader()
        {
            if ((uint)Volatile.Read(ref *(int*)(_base + OffMagic)) == Magic) PreviousPeerHeartbeatMs = ReadI64(OffMcHeartbeat);
            WriteU32(OffMagic, 0);
            Thread.MemoryBarrier();
            for (long i = 4; i < OffFrameData; i++) _base[i] = 0;
            *(int*)(_base + OffColl) = -1; *(int*)(_base + OffColl + 4) = 0; // pixel data need not be cleared
            WriteU32(OffVersion, Version);
            *(long*)(_base + OffSize) = FileSize;
            WriteU32(OffValheimPid, (uint)System.Diagnostics.Process.GetCurrentProcess().Id);
            WriteU32(OffFrameMaxW, FrameMaxW); WriteU32(OffFrameMaxH, FrameMaxH);
            uint session;
            do { session = (uint)(Guid.NewGuid().GetHashCode()); } while (session == 0);
            Session = session;
            WriteU32(OffSession, session);
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
        public uint Flags { get => ReadU32(_isHost ? OffValheimFlags : OffMcFlags); set => WriteU32(_isHost ? OffValheimFlags : OffMcFlags, value); }
        public void SetFlag(uint bit, bool on) { var f = Flags; Flags = on ? f | bit : f & ~bit; }
        public uint PeerFlags => ReadU32(_isHost ? OffMcFlags : OffValheimFlags);
        public uint HeaderSession => ReadU32(OffSession);
        public uint McAckedSession { get => ReadU32(OffMcAckedSession); set => WriteU32(OffMcAckedSession, value); }

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
            *(float*)(b + 60) = s.BobX; *(float*)(b + 64) = s.BobY; *(float*)(b + 68) = s.BobRollDeg; *(float*)(b + 72) = s.BobPitchDeg;
            *(double*)(b + 80) = s.FeetX; *(double*)(b + 88) = s.FeetY; *(double*)(b + 96) = s.FeetZ; *(float*)(b + 104) = s.EyeHeight;
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
                s.BobX = *(float*)(b + 60); s.BobY = *(float*)(b + 64); s.BobRollDeg = *(float*)(b + 68); s.BobPitchDeg = *(float*)(b + 72);
                s.FeetX = *(double*)(b + 80); s.FeetY = *(double*)(b + 88); s.FeetZ = *(double*)(b + 96); s.EyeHeight = *(float*)(b + 104);
                Thread.MemoryBarrier();
                if (ReadU32(OffPlayerState) == s1) return s1 != 0;
            }
            s = default; return false;
        }

        // ---- frames (MC writes, Valheim reads) ----
        /// <summary>Host: ask Minecraft to render at this size (clamped to 1920x1080 keeping aspect).</summary>
        public void RequestFrameSize(int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            double scale = Math.Min(1.0, Math.Min((double)FrameMaxW / w, (double)FrameMaxH / h));
            WriteU32(OffFrameReqW, (uint)Math.Max(1, (int)(w * scale)));
            WriteU32(OffFrameReqH, (uint)Math.Max(1, (int)(h * scale)));
        }
        public int RequestedWidth => (int)ReadU32(OffFrameReqW);
        public int RequestedHeight => (int)ReadU32(OffFrameReqH);
        public long LatestFrameSeq => ReadI64(OffFrameLatestSeq);

        /// <summary>Returns the newest complete frame if its seq is newer than <paramref name="lastSeen"/>.</summary>
        /// <summary>Host: publish a full collision snapshot (MC-coordinate AABBs: minX,minY,minZ,maxX,maxY,maxZ). Returns boxes written.</summary>
        public int PublishCollision(float[] boxes, int count)
        {
            if (count > CollMaxBoxes) count = CollMaxBoxes;
            uint latest = (uint)Volatile.Read(ref *(int*)(_base + OffColl));
            int idx = latest == 0 ? 1 : 0;
            int* ctl = (int*)(_base + OffColl + 16 + idx * 16);
            uint seq = (uint)Volatile.Read(ref ctl[0]);
            seq = (seq & 1) == 0 ? seq + 1 : seq;
            Volatile.Write(ref ctl[0], (int)seq);                 // odd = writing
            float* dst = (float*)(_base + OffCollData + (long)idx * CollMaxBoxes * CollBoxBytes);
            for (int i = 0; i < count * 6; i++) dst[i] = boxes[i];
            ctl[1] = count;
            Volatile.Write(ref ctl[0], (int)(seq + 1));            // even = stable
            Volatile.Write(ref *(int*)(_base + OffColl), idx);
            Volatile.Write(ref *(int*)(_base + OffColl + 4), *(int*)(_base + OffColl + 4) + 1); // snapshot counter
            return count;
        }
        public int CollisionSnapshotCount => Volatile.Read(ref *(int*)(_base + OffColl + 4));

        public bool TryGetLatestFrame(long lastSeen, out FrameView v)
        {
            v = default;
            long latest = ReadI64(OffFrameLatestSeq);
            if (latest == 0 || latest == lastSeen) return false;
            int idx = (int)ReadU32(OffFrameLatestIndex) & 1;
            int h = OffFrameBufHdr + idx * FrameBufHdrSize;
            long seq = ReadI64(h);
            if ((seq & 1) != 0 || seq == 0) return false;          // being written (writer lapped us)
            v.Seq = seq / 2; v.Index = idx;
            v.Width = (int)ReadU32(h + 8); v.Height = (int)ReadU32(h + 12); v.Stride = (int)ReadU32(h + 16);
            if (v.Width <= 0 || v.Height <= 0 || (long)v.Stride * v.Height > FrameBytes) return false;
            v.Data = _base + OffFrameData + idx * FrameBytes;
            return true;
        }

        /// <summary>After copying a frame: true if the writer did not touch that buffer meanwhile.</summary>
        public bool FrameStillValid(in FrameView v)
        {
            Thread.MemoryBarrier();
            return ReadI64(OffFrameBufHdr + v.Index * FrameBufHdrSize) == v.Seq * 2;
        }

        /// <summary>Writer side (MC; C# version used by tests). <paramref name="frameNo"/> must increase from 1.</summary>
        public void PublishFrame(long frameNo, int w, int h, byte* src)
        {
            int idx = 1 - ((int)ReadU32(OffFrameLatestIndex) & 1);
            if (ReadI64(OffFrameLatestSeq) == 0) idx = 0;
            int hdr = OffFrameBufHdr + idx * FrameBufHdrSize;
            WriteI64(hdr, frameNo * 2 - 1);
            Thread.MemoryBarrier();
            long bytes = (long)w * h * 4;
            Buffer.MemoryCopy(src, _base + OffFrameData + idx * FrameBytes, FrameBytes, bytes);
            WriteU32(hdr + 8, (uint)w); WriteU32(hdr + 12, (uint)h); WriteU32(hdr + 16, (uint)(w * 4)); WriteU32(hdr + 20, FrameFormatRgba8BottomUp);
            WriteI64(hdr, frameNo * 2);
            WriteU32(OffFrameLatestIndex, (uint)idx);
            WriteI64(OffFrameLatestSeq, frameNo);
        }

        // ---- SPSC rings ----
        int OutRing => _isHost ? OffRingValheimToMc : OffRingMcToValheim;
        int InRing => _isHost ? OffRingMcToValheim : OffRingValheimToMc;

        public bool TrySend(ushort type, byte[] payload) => TrySend(type, payload, payload?.Length ?? 0);

        public bool TrySend(ushort type, byte[] payload, int len)
        {
            if (len > SlotPayload) throw new ArgumentException("payload too large");
            int r = OutRing;
            long head = *(long*)(_base + r);             // producer owns head
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
            if (head - tail > RingSlots) { WriteI64(r + 0x40, head); ev = default; return false; } // peer reset: resync
            byte* slot = _base + r + RingHeader + (tail % RingSlots) * SlotSize;
            uint len = *(uint*)(slot + 4);
            if (len > SlotPayload) len = SlotPayload;
            var data = new byte[len];
            for (int i = 0; i < len; i++) data[i] = slot[8 + i];
            ev = new LinkEvent { Type = *(ushort*)slot, Payload = data };
            WriteI64(r + 0x40, tail + 1);                 // release
            return true;
        }

        /// <summary>Drop everything queued for us (used when a peer reconnects).</summary>
        public void DrainIncoming() { int r = InRing; WriteI64(r + 0x40, ReadI64(r)); }

        public void Dispose()
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mmf.Dispose();
        }
    }

    /// <summary>Little-endian payload helpers.</summary>
    public static class Payload
    {
        public static byte[] U32(params uint[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, i * 4); return b; }
        public static byte[] I32(params int[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, i * 4); return b; }
        public static byte[] F32(params float[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BitConverter.GetBytes(v[i]).CopyTo(b, i * 4); return b; }
        public static uint GetU32(byte[] b, int i) => b.Length >= (i + 1) * 4 ? BitConverter.ToUInt32(b, i * 4) : 0;
    }
}
