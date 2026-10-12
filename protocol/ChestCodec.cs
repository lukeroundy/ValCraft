using System;
using System.Collections.Generic;
using System.Text;

namespace ValCraft.Protocol
{
    /// <summary>
    /// 0.10.0 Valheim chest <-> MC chest screen.
    /// EvChestContents (V->MC), split over messages: u8 flags (1 first, 2 last), u32 serial, [first: u8 w, u8 h (w=0 = closed), u8 nameLen, name],
    /// then entries until the end: u8 slot (y*w+x), u16 count, u16 dur (0xFFFF none), u8 idLen, mc id ("" = unmapped), u8 nameLen, Valheim name.
    /// EvChestOp (MC->V): u32 serial, u8 op (1 take, 2 deposit, 3 close), u8 slot, u16 count, u16 dur, utf8 mc id (deposit).
    /// </summary>
    public static class ChestCodec
    {
        public struct Entry { public byte Slot; public ushort Count, Dur; public string McId, Name; }
        public const byte OpTake = 1, OpDeposit = 2, OpClose = 3;

        static byte[] Utf(string s, int max) { var b = Encoding.UTF8.GetBytes(s ?? ""); if (b.Length <= max) return b; int n = max; while (n > 0 && (b[n] & 0xC0) == 0x80) n--; Array.Resize(ref b, n); return b; }

        public static List<byte[]> Encode(uint serial, int w, int h, string name, List<Entry> entries)
        {
            var msgs = new List<byte[]>();
            var cur = new List<byte>();
            void Start(bool first)
            {
                cur.Clear(); cur.Add((byte)(first ? 1 : 0)); cur.AddRange(BitConverter.GetBytes(serial));
                if (first) { cur.Add((byte)w); cur.Add((byte)h); var nb = Utf(name, 48); cur.Add((byte)nb.Length); cur.AddRange(nb); }
            }
            Start(true);
            foreach (var e in entries)
            {
                var id = Utf(e.McId, 64); var nm = Utf(e.Name, 40);
                int size = 1 + 2 + 2 + 1 + id.Length + 1 + nm.Length;
                if (cur.Count + size > ValCraftLink.SlotPayload) { msgs.Add(cur.ToArray()); Start(false); }
                cur.Add(e.Slot); cur.AddRange(BitConverter.GetBytes(e.Count)); cur.AddRange(BitConverter.GetBytes(e.Dur));
                cur.Add((byte)id.Length); cur.AddRange(id); cur.Add((byte)nm.Length); cur.AddRange(nm);
            }
            msgs.Add(cur.ToArray());
            msgs[msgs.Count - 1][0] |= 2;
            return msgs;
        }

        public static (uint serial, byte op, byte slot, ushort count, ushort dur, string id) Op(byte[] p)
            => (BitConverter.ToUInt32(p, 0), p[4], p[5], BitConverter.ToUInt16(p, 6), BitConverter.ToUInt16(p, 8), Encoding.UTF8.GetString(p, 10, p.Length - 10));

        public static byte[] MakeOp(uint serial, byte op, byte slot, ushort count, ushort dur, string id)
        {
            var ib = Utf(id, 200); var b = new byte[10 + ib.Length];
            BitConverter.GetBytes(serial).CopyTo(b, 0); b[4] = op; b[5] = slot; BitConverter.GetBytes(count).CopyTo(b, 6); BitConverter.GetBytes(dur).CopyTo(b, 8); ib.CopyTo(b, 10);
            return b;
        }
    }
}
