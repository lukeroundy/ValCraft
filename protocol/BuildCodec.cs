using System;
using System.Collections.Generic;
using System.Text;

namespace ValCraft.Protocol
{
    /// <summary>0.8.5 (v13) EvBuildPieces: the Valheim piece list for the MC-side build picker.</summary>
    public static class BuildCodec
    {
        public struct Entry { public ushort Index; public byte Category; public bool CanBuild; public string Name, Cost; }

        static byte[] Utf(string s, int max)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            if (b.Length <= max) return b;
            int n = max; while (n > 0 && (b[n] & 0xC0) == 0x80) n--; // don't cut a UTF-8 sequence
            var r = new byte[n]; Array.Copy(b, r, n); return r;
        }

        /// <summary>Messages: u8 first, u8 last, u8 n, n x (u16 index, u8 category, u8 flags(1 = can build now), u8 nameLen, name, u8 costLen, cost).</summary>
        public static List<byte[]> Encode(IList<Entry> entries, int maxPayload = 248)
        {
            var msgs = new List<byte[]>(); var cur = new List<byte>(); int n = 0;
            void Flush() { if (n == 0 && msgs.Count > 0) return; var a = new byte[3 + cur.Count]; a[2] = (byte)n; cur.CopyTo(a, 3); msgs.Add(a); cur.Clear(); n = 0; }
            foreach (var e in entries)
            {
                var name = Utf(e.Name, 60); var cost = Utf(e.Cost, 120);
                int len = 6 + name.Length + 1 + cost.Length;
                if (3 + cur.Count + len > maxPayload || n == 255) Flush();
                cur.Add((byte)(e.Index & 0xFF)); cur.Add((byte)(e.Index >> 8)); cur.Add(e.Category); cur.Add((byte)(e.CanBuild ? 1 : 0));
                cur.Add((byte)name.Length); cur.AddRange(name); cur.Add((byte)cost.Length); cur.AddRange(cost); n++;
            }
            if (n > 0 || msgs.Count == 0) Flush();
            msgs[0][0] = 1; msgs[msgs.Count - 1][1] = 1;
            return msgs;
        }

        public static List<Entry> Decode(byte[] p)
        {
            var r = new List<Entry>(); int n = p[2], o = 3;
            for (int i = 0; i < n; i++)
            {
                var e = new Entry { Index = (ushort)(p[o] | p[o + 1] << 8), Category = p[o + 2], CanBuild = (p[o + 3] & 1) != 0 };
                int nl = p[o + 4]; e.Name = Encoding.UTF8.GetString(p, o + 5, nl); o += 5 + nl;
                int cl = p[o]; e.Cost = Encoding.UTF8.GetString(p, o + 1, cl); o += 1 + cl;
                r.Add(e);
            }
            return r;
        }
    }
}
