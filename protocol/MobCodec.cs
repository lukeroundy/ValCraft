using System;
using System.Collections.Generic;

namespace ValCraft.Protocol
{
    /// <summary>0.9.0 (v14) cross-game mobs + water payloads. All little-endian; positions are MC coordinates.</summary>
    public static class MobCodec
    {
        public struct Mob { public uint Id; public byte Kind; public float X, Y, Z, W, H, HpFrac; } // Kind 1 hostile, 2 passive
        public const byte Hostile = 1, Passive = 2;
        const int McEntry = 20, VEntry = 20;

        static byte U8(float v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));

        /// <summary>EvMobList (MC→V) and EvVMobList (V→MC) share a layout: u8 first, u8 last, u8 n, n × (u32 id, u8 kind, f32 x, y, z, u8 width×10, u8 height×10, u8 hp%).</summary>
        public static List<byte[]> EncodeList(IList<Mob> mobs, int max = 248)
        {
            var msgs = new List<byte[]>(); int per = (max - 3) / McEntry;
            for (int s = 0; s == 0 || s < mobs.Count; s += per)
            {
                int n = Math.Min(per, mobs.Count - s); var b = new byte[3 + n * McEntry]; b[2] = (byte)n;
                for (int i = 0; i < n; i++)
                {
                    var m = mobs[s + i]; int o = 3 + i * McEntry;
                    BitConverter.GetBytes(m.Id).CopyTo(b, o); b[o + 4] = m.Kind;
                    BitConverter.GetBytes(m.X).CopyTo(b, o + 5); BitConverter.GetBytes(m.Y).CopyTo(b, o + 9); BitConverter.GetBytes(m.Z).CopyTo(b, o + 13);
                    b[o + 17] = U8(m.W * 10); b[o + 18] = U8(m.H * 10); b[o + 19] = U8(m.HpFrac * 100);
                }
                msgs.Add(b);
            }
            msgs[0][0] = 1; msgs[msgs.Count - 1][1] = 1;
            return msgs;
        }
        public static List<Mob> DecodeList(byte[] p)
        {
            var r = new List<Mob>(); int n = p[2];
            for (int i = 0; i < n && 3 + (i + 1) * McEntry <= p.Length; i++)
            {
                int o = 3 + i * McEntry;
                r.Add(new Mob { Id = BitConverter.ToUInt32(p, o), Kind = p[o + 4], X = BitConverter.ToSingle(p, o + 5), Y = BitConverter.ToSingle(p, o + 9), Z = BitConverter.ToSingle(p, o + 13), W = p[o + 17] / 10f, H = p[o + 18] / 10f, HpFrac = p[o + 19] / 100f });
            }
            return r;
        }

        /// <summary>EvMobHurt V→MC: u32 mcId, f32 damage (MC hp), f32 dirX, f32 dirZ, u32 attackerVid (0 = none).</summary>
        public static byte[] MobHurt(uint mcId, float dmg, float dx, float dz, uint attackerVid)
        {
            var b = new byte[20]; BitConverter.GetBytes(mcId).CopyTo(b, 0); BitConverter.GetBytes(dmg).CopyTo(b, 4); BitConverter.GetBytes(dx).CopyTo(b, 8); BitConverter.GetBytes(dz).CopyTo(b, 12); BitConverter.GetBytes(attackerVid).CopyTo(b, 16); return b;
        }
        /// <summary>EvVMobHurt MC→V: u32 vid, f32 damage (MC hp), u32 mcAttackerId (0 = none).</summary>
        public static (uint vid, float dmg, uint attacker) VMobHurt(byte[] p) => (BitConverter.ToUInt32(p, 0), BitConverter.ToSingle(p, 4), BitConverter.ToUInt32(p, 8));

        public const short NoWater = short.MinValue;
        /// <summary>EvWater V→MC: f32 x0, f32 z0 (MC coords of cell [0,0] centre), u8 step (m), u8 nx, u8 nz, nx·nz × i16 (surface MC Y × 16, NoWater = none; x fastest), then ⌈nx·nz/8⌉ bytes of tar bits.</summary>
        public static byte[] Water(float x0, float z0, int step, int nx, int nz, float[] surfaceMcY, bool[] tar)
        {
            int n = nx * nz; var b = new byte[11 + n * 2 + (n + 7) / 8];
            BitConverter.GetBytes(x0).CopyTo(b, 0); BitConverter.GetBytes(z0).CopyTo(b, 4); b[8] = (byte)step; b[9] = (byte)nx; b[10] = (byte)nz;
            for (int i = 0; i < n; i++)
            {
                short v = float.IsNaN(surfaceMcY[i]) ? NoWater : (short)Math.Max(-32767, Math.Min(32767, Math.Round(surfaceMcY[i] * 16)));
                BitConverter.GetBytes(v).CopyTo(b, 11 + i * 2);
                if (tar != null && tar[i]) b[11 + n * 2 + i / 8] |= (byte)(1 << (i % 8));
            }
            return b;
        }
    }
}
