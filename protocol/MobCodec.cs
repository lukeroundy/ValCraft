using System;
using System.Collections.Generic;

namespace ValCraft.Protocol
{
    /// <summary>0.9.0 (v14) / 0.9.1 (v15) cross-game mobs + water payloads. All little-endian; positions are MC coordinates.</summary>
    public static class MobCodec
    {
        public struct Mob { public uint Id; public byte Kind; public float X, Y, Z, W, H, HpFrac, MaxHp; public string Name; } // Kind 1 hostile, 2 passive
        public const byte Hostile = 1, Passive = 2;
        const int Fixed = 23;

        /// <summary>Valheim meters per MC block (ValCraft maps 1:1).</summary>
        public const float BlockToMeter = 1f;
        /// <summary>0.15.2: hit capsule for an MC bounding box (W x H blocks, already including baby/pose/scale, as sent by MC):
        /// radius = half the width (min 0.15 m), height = full box height (at least 2 r), centred on the body (centerY = h/2).
        /// The movement collider stays small: radius min(r, 0.4), height min(h, 1.8), also body-centred.</summary>
        public struct Box { public float Radius, Height, CenterY, MoveRadius, MoveHeight, MoveCenterY; public bool FromRender; }
        /// <summary>0.15.3: capsules from the RENDERED model. rMinY/rMaxY/rSizeX/rSizeZ = world AABB of the MC entity mesh
        /// (Renderer.bounds), feetY = world Y of the proxy root (= the mesh origin), rootScale = proxy root lossyScale.y.
        /// Hit capsule: height = model top - feet (at least 2 r), radius = half the smaller horizontal extent (at least half the
        /// MC bbox width, so out-stretched arms don't make it huge). The root capsule (Character.m_collider: GetTopPoint for the
        /// name tag, GetCenterPoint / GetHeight for aim and projectiles) gets the SAME height and centre with radius ≤ 0.5 m.
        /// Values are returned in the root's LOCAL units (world / rootScale). Bad / missing bounds -> MC bbox (Hitbox()).</summary>
        public static Box Fit(bool rendered, float rMinY, float rMaxY, float rSizeX, float rSizeZ, float feetY, float bbW, float bbH, float rootScale = 1f)
        {
            if (!(rootScale > 0.001f) || float.IsInfinity(rootScale)) rootScale = 1f;
            Box b;
            float hh = rMaxY - feetY;
            bool ok = rendered && !float.IsNaN(hh) && hh > 0.2f && hh < 40f && rSizeX > 0.05f && rSizeZ > 0.05f && rSizeX < 60f && rSizeZ < 60f;
            if (!ok) { b = Hitbox(bbW, bbH); MoveFull(ref b); }
            else
            {
                float r = Math.Max(Math.Min(rSizeX, rSizeZ) / 2f, bbW > 0 ? bbW / 2f : 0.15f);
                r = Math.Max(0.15f, r);
                float h = Math.Max(hh, 2 * r);
                b = new Box { Radius = r, Height = h, CenterY = h / 2f, FromRender = true };
                MoveFull(ref b);
            }
            b.Radius /= rootScale; b.Height /= rootScale; b.CenterY /= rootScale; b.MoveRadius /= rootScale; b.MoveHeight /= rootScale; b.MoveCenterY /= rootScale;
            return b;
        }
        static void MoveFull(ref Box b) { b.MoveRadius = Math.Min(b.Radius, 0.5f); b.MoveHeight = Math.Max(b.Height, 2 * b.MoveRadius); b.MoveCenterY = b.MoveHeight / 2f; }

        public static Box Hitbox(float w, float h, float scale = BlockToMeter)
        {
            if (!(w > 0)) w = 0.6f; if (!(h > 0)) h = 1.8f;
            float r = Math.Max(0.15f, w * scale / 2f), hh = Math.Max(2 * r, h * scale);
            float mr = Math.Min(r, 0.4f), mh = Math.Max(2 * mr, Math.Min(hh, 1.8f));
            return new Box { Radius = r, Height = hh, CenterY = hh / 2f, MoveRadius = mr, MoveHeight = mh, MoveCenterY = mh / 2f };
        }

        static byte U8(float v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));

        /// <summary>
        /// EvMobList (MC→V) and EvVMobList (V→MC), v15: u8 first, u8 last, u8 n, n × (u32 id, u8 kind, f32 x, y, z,
        /// u8 width×10, u8 height×10, u8 hp%, u16 maxHp, u8 nameLen, utf8 name ≤ 32 B).
        /// </summary>
        public static List<byte[]> EncodeList(IList<Mob> mobs, int max = 248)
        {
            var msgs = new List<byte[]>(); var cur = new List<byte>(); int n = 0;
            void Flush() { var a = new byte[3 + cur.Count]; a[2] = (byte)n; cur.CopyTo(a, 3); msgs.Add(a); cur.Clear(); n = 0; }
            foreach (var m in mobs)
            {
                var name = System.Text.Encoding.UTF8.GetBytes(m.Name ?? "");
                if (name.Length > 32) { int k = 32; while (k > 0 && (name[k] & 0xC0) == 0x80) k--; Array.Resize(ref name, k); }
                if (3 + cur.Count + Fixed + name.Length > max) Flush();
                cur.AddRange(BitConverter.GetBytes(m.Id)); cur.Add(m.Kind);
                cur.AddRange(BitConverter.GetBytes(m.X)); cur.AddRange(BitConverter.GetBytes(m.Y)); cur.AddRange(BitConverter.GetBytes(m.Z));
                cur.Add(U8(m.W * 10)); cur.Add(U8(m.H * 10)); cur.Add(U8(m.HpFrac * 100));
                cur.AddRange(BitConverter.GetBytes((ushort)Math.Max(0, Math.Min(65535, Math.Round(m.MaxHp)))));
                cur.Add((byte)name.Length); cur.AddRange(name); n++;
            }
            if (n > 0 || msgs.Count == 0) Flush();
            msgs[0][0] = 1; msgs[msgs.Count - 1][1] = 1;
            return msgs;
        }
        public static List<Mob> DecodeList(byte[] p)
        {
            var r = new List<Mob>(); int n = p[2], o = 3;
            for (int i = 0; i < n && o + Fixed <= p.Length; i++)
            {
                var m = new Mob { Id = BitConverter.ToUInt32(p, o), Kind = p[o + 4], X = BitConverter.ToSingle(p, o + 5), Y = BitConverter.ToSingle(p, o + 9), Z = BitConverter.ToSingle(p, o + 13), W = p[o + 17] / 10f, H = p[o + 18] / 10f, HpFrac = p[o + 19] / 100f, MaxHp = BitConverter.ToUInt16(p, o + 20) };
                int nl = p[o + 22]; m.Name = System.Text.Encoding.UTF8.GetString(p, o + 23, Math.Min(nl, p.Length - o - 23)); o += Fixed + nl;
                r.Add(m);
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
        /// <summary>v15: water tiles are 9×9 cells of 2 m aligned to 18 m: tile (tx, tz) has x0 = tx·18 + 1, z0 = tz·18 + 1.</summary>
        public const int TileCells = 9, TileStep = 2, TileSize = 18;
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
