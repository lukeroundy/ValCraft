using System;
using System.Collections.Generic;

namespace ValCraft.Protocol
{
    /// <summary>One MC block state as Valheim draws it (EvBlockPalette).</summary>
    public sealed class BlockPaletteEntry
    {
        public const byte FullOpaque = 1, Collides = 2, Cutout = 4;
        public byte Flags;
        public ushort[] Tiles = new ushort[6]; // MC Direction order: down, up, north, south, west, east
        public byte[] Boxes = { 0, 0, 0, 16, 16, 16 }; // n*6, 1/16 block
    }

    /// <summary>Pure-C# (no Unity) store of MC sections received over the link, plus the face-culled mesher.
    /// Unity side converts the float lists into Meshes. Coordinates: section-local, already in Unity orientation
    /// (x, y, -z), so the section object sits at anchor + (sx*16, sy*16, -sz*16).</summary>
    public sealed class BlockStore
    {
        public readonly Dictionary<ushort, BlockPaletteEntry> Palette = new Dictionary<ushort, BlockPaletteEntry>();
        public readonly Dictionary<(int, int, int), ushort[]> Sections = new Dictionary<(int, int, int), ushort[]>();
        readonly Dictionary<(int, int, int), ushort[]> _pending = new Dictionary<(int, int, int), ushort[]>();
        /// <summary>Sections whose mesh must be rebuilt (changed, or a neighbour changed).</summary>
        public readonly HashSet<(int, int, int)> Dirty = new HashSet<(int, int, int)>();
        public int AtlasRevision, AtlasTiles, AtlasPerRow = 64, AtlasTileSize = 16; public string AtlasPath;
        public bool ResetRequested;

        static int I(byte[] b, int o) => BitConverter.ToInt32(b, o);
        static ushort U(byte[] b, int o) => BitConverter.ToUInt16(b, o);

        /// <summary>Handle one M2V block message. Returns false if the type isn't a block message.</summary>
        public bool Handle(ushort type, byte[] p)
        {
            switch (type)
            {
                case ValCraftLink.EvBlocksReset:
                    foreach (var k in Sections.Keys) Dirty.Add(k);
                    Sections.Clear(); _pending.Clear(); Palette.Clear(); ResetRequested = true; return true;
                case ValCraftLink.EvBlockPalette:
                {
                    var e = new BlockPaletteEntry { Flags = p[2] };
                    int n = p[3];
                    for (int i = 0; i < 6; i++) e.Tiles[i] = U(p, 4 + i * 2);
                    if (n > 0 && p.Length >= 16 + n * 6) { e.Boxes = new byte[n * 6]; Array.Copy(p, 16, e.Boxes, 0, n * 6); }
                    else if (n == 0) e.Boxes = new byte[0];
                    Palette[U(p, 0)] = e;
                    return true;
                }
                case ValCraftLink.EvSectionBegin:
                    _pending[(I(p, 0), I(p, 4), I(p, 8))] = new ushort[4096]; return true;
                case ValCraftLink.EvSectionBlocks:
                {
                    if (!_pending.TryGetValue((I(p, 0), I(p, 4), I(p, 8)), out var arr)) return true;
                    int n = U(p, 12);
                    for (int i = 0; i < n && 14 + i * 4 + 4 <= p.Length; i++) arr[U(p, 14 + i * 4) & 4095] = U(p, 16 + i * 4);
                    return true;
                }
                case ValCraftLink.EvSectionEnd:
                {
                    var k = (I(p, 0), I(p, 4), I(p, 8));
                    if (!_pending.TryGetValue(k, out var arr)) return true;
                    _pending.Remove(k);
                    bool any = false; for (int i = 0; i < 4096 && !any; i++) any = arr[i] != 0;
                    if (any) Sections[k] = arr; else Sections.Remove(k);
                    Dirty.Add(k);
                    // neighbours' boundary faces may change
                    Dirty.Add((k.Item1 - 1, k.Item2, k.Item3)); Dirty.Add((k.Item1 + 1, k.Item2, k.Item3));
                    Dirty.Add((k.Item1, k.Item2 - 1, k.Item3)); Dirty.Add((k.Item1, k.Item2 + 1, k.Item3));
                    Dirty.Add((k.Item1, k.Item2, k.Item3 - 1)); Dirty.Add((k.Item1, k.Item2, k.Item3 + 1));
                    return true;
                }
                case ValCraftLink.EvBlockAtlas:
                    AtlasRevision = I(p, 0); AtlasTiles = I(p, 4); AtlasPerRow = Math.Max(1, I(p, 8)); AtlasTileSize = I(p, 12);
                    AtlasPath = System.Text.Encoding.UTF8.GetString(p, 16, p.Length - 16);
                    return true;
            }
            return false;
        }

        /// <summary>Palette id at MC block coords (0 = nothing we draw).</summary>
        public ushort At(int x, int y, int z)
        {
            if (!Sections.TryGetValue((x >> 4, y >> 4, z >> 4), out var a)) return 0;
            return a[((y & 15) << 8) | ((z & 15) << 4) | (x & 15)];
        }

        bool Occludes(int x, int y, int z)
        {
            var id = At(x, y, z);
            return id != 0 && Palette.TryGetValue(id, out var e) && (e.Flags & BlockPaletteEntry.FullOpaque) != 0;
        }

        public sealed class MeshData
        {
            public readonly List<float> Pos = new List<float>(), Nrm = new List<float>(), Uv = new List<float>();
            public readonly List<int> Tris = new List<int>(), ColTris = new List<int>();
            public int Faces => Tris.Count / 6;
            public void Clear() { Pos.Clear(); Nrm.Clear(); Uv.Clear(); Tris.Clear(); ColTris.Clear(); }
        }

        // MC directions: normal (MC coords)
        static readonly int[,] Dir = { { 0, -1, 0 }, { 0, 1, 0 }, { 0, 0, -1 }, { 0, 0, 1 }, { -1, 0, 0 }, { 1, 0, 0 } };

        /// <summary>Build render (Tris) and collision (ColTris, same vertices) geometry for one section.</summary>
        public void Build((int, int, int) key, MeshData m)
        {
            m.Clear();
            if (!Sections.TryGetValue(key, out var arr)) return;
            int bx = key.Item1 << 4, by = key.Item2 << 4, bz = key.Item3 << 4;
            float g = AtlasPerRow, eps = 0.5f / Math.Max(1, AtlasTileSize);
            var c = new float[12];
            for (int i = 0; i < 4096; i++)
            {
                ushort id = arr[i];
                if (id == 0 || !Palette.TryGetValue(id, out var e)) continue;
                int lx = i & 15, lz = (i >> 4) & 15, ly = i >> 8;
                bool collides = (e.Flags & BlockPaletteEntry.Collides) != 0;
                for (int b = 0; b + 6 <= e.Boxes.Length; b += 6)
                {
                    float x0 = e.Boxes[b] / 16f, y0 = e.Boxes[b + 1] / 16f, z0 = e.Boxes[b + 2] / 16f, x1 = e.Boxes[b + 3] / 16f, y1 = e.Boxes[b + 4] / 16f, z1 = e.Boxes[b + 5] / 16f;
                    for (int d = 0; d < 6; d++)
                    {
                        // face on the block boundary next to a full opaque block -> hidden
                        bool atEdge = d switch { 0 => y0 == 0, 1 => y1 == 1, 2 => z0 == 0, 3 => z1 == 1, 4 => x0 == 0, _ => x1 == 1 };
                        if (atEdge && Occludes(bx + lx + Dir[d, 0], by + ly + Dir[d, 1], bz + lz + Dir[d, 2])) continue;
                        // corners (MC block-local) + uv fractions
                        float u0, u1, v0, v1;
                        switch (d)
                        {
                            case 0: case 1: { float y = d == 0 ? y0 : y1; Set(c, x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1); u0 = x0; u1 = x1; v0 = z0; v1 = z1; break; }
                            case 2: case 3: { float z = d == 2 ? z0 : z1; Set(c, x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z); u0 = x0; u1 = x1; v0 = y0; v1 = y1; break; }
                            default: { float x = d == 4 ? x0 : x1; Set(c, x, y0, z0, x, y0, z1, x, y1, z1, x, y1, z0); u0 = z0; u1 = z1; v0 = y0; v1 = y1; break; }
                        }
                        int tile = e.Tiles[d];
                        float col = tile % (int)g, row = tile / (int)g, span = 1 - 2 * eps;
                        float U0 = (col + eps + u0 * span) / g, U1 = (col + eps + u1 * span) / g;
                        float Vb = 1f - (row + 1) / g; // tile bottom (texture origin bottom-left; PNG row 0 = top)
                        float V0 = Vb + (eps + v0 * span) / g, V1 = Vb + (eps + v1 * span) / g;
                        int start = m.Pos.Count / 3;
                        for (int k = 0; k < 4; k++)
                        {
                            m.Pos.Add(lx + c[k * 3]); m.Pos.Add(ly + c[k * 3 + 1]); m.Pos.Add(-(lz + c[k * 3 + 2]));
                            m.Nrm.Add(Dir[d, 0]); m.Nrm.Add(Dir[d, 1]); m.Nrm.Add(-Dir[d, 2]);
                        }
                        // uv per corner, matching Set order: (u0,v0) (u1,v0) (u1,v1) (u0,v1)
                        m.Uv.Add(U0); m.Uv.Add(V0); m.Uv.Add(U1); m.Uv.Add(V0); m.Uv.Add(U1); m.Uv.Add(V1); m.Uv.Add(U0); m.Uv.Add(V1);
                        // winding: Unity front face = cross(b-a, c-a) along the outward normal
                        int a0 = start, a1 = start + 1, a2 = start + 2, a3 = start + 3;
                        if (!Front(m.Pos, a0, a1, a2, Dir[d, 0], Dir[d, 1], -Dir[d, 2])) { int t = a1; a1 = a3; a3 = t; }
                        m.Tris.Add(a0); m.Tris.Add(a1); m.Tris.Add(a2); m.Tris.Add(a0); m.Tris.Add(a2); m.Tris.Add(a3);
                        if (collides) { m.ColTris.Add(a0); m.ColTris.Add(a1); m.ColTris.Add(a2); m.ColTris.Add(a0); m.ColTris.Add(a2); m.ColTris.Add(a3); }
                    }
                }
            }
        }

        static void Set(float[] c, params float[] v) { Array.Copy(v, c, 12); }

        static bool Front(List<float> p, int a, int b, int c, float nx, float ny, float nz)
        {
            float ex = p[b * 3] - p[a * 3], ey = p[b * 3 + 1] - p[a * 3 + 1], ez = p[b * 3 + 2] - p[a * 3 + 2];
            float fx = p[c * 3] - p[a * 3], fy = p[c * 3 + 1] - p[a * 3 + 1], fz = p[c * 3 + 2] - p[a * 3 + 2];
            // Unity's Vector3.Cross formula (same as right-handed math formula)
            float cx = ey * fz - ez * fy, cy = ez * fx - ex * fz, cz = ex * fy - ey * fx;
            return cx * nx + cy * ny + cz * nz > 0;
        }
    }
}
