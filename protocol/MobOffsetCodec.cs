using System;
using System.Collections.Generic;
namespace ValCraft.Protocol
{
    /// <summary>0.15.7 EvMobOffsets: u16 count, then count x (u32 MC entity id, f32 dy metres). Little endian.</summary>
    public static class MobOffsetCodec
    {
        public static byte[] Encode(IList<KeyValuePair<uint, float>> l)
        {
            var b = new byte[2 + l.Count * 8];
            BitConverter.GetBytes((ushort)l.Count).CopyTo(b, 0);
            for (int i = 0; i < l.Count; i++) { BitConverter.GetBytes(l[i].Key).CopyTo(b, 2 + i * 8); BitConverter.GetBytes(l[i].Value).CopyTo(b, 6 + i * 8); }
            return b;
        }
        public static List<KeyValuePair<uint, float>> Decode(byte[] b)
        {
            var r = new List<KeyValuePair<uint, float>>(); if (b == null || b.Length < 2) return r;
            int n = BitConverter.ToUInt16(b, 0);
            for (int i = 0; i < n && 2 + i * 8 + 8 <= b.Length; i++) r.Add(new KeyValuePair<uint, float>(BitConverter.ToUInt32(b, 2 + i * 8), BitConverter.ToSingle(b, 6 + i * 8)));
            return r;
        }
    }

    /// <summary>0.15.7: MC XP for a Valheim kill: ~1 per 10 max HP, x level (1 = no stars, 2 = one star...), bosses x5 and
    /// at least 200; capped at 5000.</summary>
    public static class ValheimXp
    {
        public static int For(float maxHealth, int level, bool boss)
        {
            double xp = Math.Max(1.0, maxHealth / 10.0) * Math.Max(1, level);
            if (boss) xp = Math.Max(200.0, xp * 5.0);
            return (int)Math.Min(5000.0, Math.Round(xp));
        }
    }
}
