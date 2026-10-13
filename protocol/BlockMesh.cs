using System;
using System.Collections.Generic;

namespace ValCraft.Protocol
{
    /// <summary>One MC block state as Valheim draws it (EvBlockPalette).</summary>
    public sealed class BlockPaletteEntry
    {
        public const byte FullOpaque = 1, Collides = 2, Cutout = 4, Translucent = 8, Cross = 16, Fluid = 32, Shiny = 64, HasQuads = 128;
        public byte Light; public int LightRgb;
        public sealed class Q { public ushort Tile; public byte Dir, Cull; public float[] Pos = new float[12], Uv = new float[8]; }
        public readonly List<Q> Quads = new List<Q>();
        public bool QuadsComplete;
        public bool FullBox => Boxes.Length == 6 && Boxes[0] == 0 && Boxes[1] == 0 && Boxes[2] == 0 && Boxes[3] == 16 && Boxes[4] == 16 && Boxes[5] == 16;
        /// <summary>Submesh: 0 opaque, 1 cutout (alpha test), 2 translucent (blended), 3 emissive (opaque, glowing), 4 shiny (opaque, glossy/reflective).</summary>
        public int Layer { get { int l = BaseLayer; if (l == 0 && Light > 0) return 3; if (l == 0 && (Flags & Shiny) != 0) return 4; return l; } }
        public int BaseLayer => (Flags & Fluid) != 0 ? ((Flags & Translucent) != 0 ? 2 : 0) : (Flags & Cross) != 0 ? 1 : (Flags & Translucent) != 0 ? 2 : (Flags & Cutout) != 0 ? 1 : 0;
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
        public readonly List<(int, int, int)> Urgent = new List<(int, int, int)>();
        public int AtlasRevision, AtlasTiles, AtlasPerRow = 64, AtlasTileSize = 16; public string AtlasPath;
        public bool ResetRequested;
        /// <summary>Animated tiles (EvBlockAnim): the display tile shows Frames[i] for Ticks[i] game ticks (20/s).</summary>
        public sealed class Anim { public ushort Display; public ushort[] Frames, Ticks; public int Total, Current = -1; public int FrameAt(long tick) { if (Total <= 0) return 0; long t = tick % Total; for (int i = 0; i < Ticks.Length; i++) { t -= Ticks[i]; if (t < 0) return i; } return 0; } }
        public readonly Dictionary<ushort, Anim> Anims = new Dictionary<ushort, Anim>();

        static int I(byte[] b, int o) => BitConverter.ToInt32(b, o);
        static ushort U(byte[] b, int o) => BitConverter.ToUInt16(b, o);

        /// <summary>Handle one M2V block message. Returns false if the type isn't a block message.</summary>
        public bool Handle(ushort type, byte[] p)
        {
            switch (type)
            {
                case ValCraftLink.EvBlocksReset:
                    foreach (var k in Sections.Keys) Dirty.Add(k); Urgent.Clear();
                    Sections.Clear(); _pending.Clear(); Palette.Clear(); Anims.Clear(); ResetRequested = true; return true;
                case ValCraftLink.EvBlockPalette:
                {
                    var e = new BlockPaletteEntry { Flags = p[2] };
                    int n = p[3];
                    for (int i = 0; i < 6; i++) e.Tiles[i] = U(p, 4 + i * 2);
                    if (n > 0 && p.Length >= 16 + n * 6) { e.Boxes = new byte[n * 6]; Array.Copy(p, 16, e.Boxes, 0, n * 6); }
                    else if (n == 0) e.Boxes = new byte[0];
                    int lo = 16 + n * 6;
                    if (p.Length >= lo + 4) { e.Light = p[lo]; e.LightRgb = (p[lo + 1] << 16) | (p[lo + 2] << 8) | p[lo + 3]; }
                    Palette[U(p, 0)] = e;
                    return true;
                }
                case ValCraftLink.EvBlockQuads:
                {
                    if (!Palette.TryGetValue(U(p, 0), out var e)) return true;
                    int first = U(p, 2), n = p[4];
                    if (first == 0) e.Quads.Clear();
                    for (int i = 0; i < n && 6 + (i + 1) * 44 <= p.Length; i++)
                    {
                        int o = 6 + i * 44;
                        e.Quads.Add(ReadQuad(p, o));
                    }
                    if (p[5] != 0) e.QuadsComplete = true;
                    return true;
                }
                case ValCraftLink.EvBlockAnim:
                {
                    int n = U(p, 2);
                    var a = new Anim { Display = U(p, 0), Frames = new ushort[n], Ticks = new ushort[n] };
                    for (int i = 0; i < n && 4 + i * 4 + 4 <= p.Length; i++) { a.Frames[i] = U(p, 4 + i * 4); a.Ticks[i] = Math.Max((ushort)1, U(p, 6 + i * 4)); a.Total += a.Ticks[i]; }
                    Anims[a.Display] = a;
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
                    if (Urgent.Count < 64) Urgent.Add(k); // 0.14.1: the changed section itself rebuilds before neighbours/initial sync
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

        public static BlockPaletteEntry.Q ReadQuad(byte[] p, int o)
        {
            var q = new BlockPaletteEntry.Q { Tile = U(p, o), Dir = p[o + 2], Cull = p[o + 3] };
            for (int v = 0; v < 4; v++)
            {
                int vo = o + 4 + v * 10;
                for (int c = 0; c < 3; c++) q.Pos[v * 3 + c] = BitConverter.ToInt16(p, vo + c * 2) / 1024f;
                for (int c = 0; c < 2; c++) q.Uv[v * 2 + c] = U(p, vo + 6 + c * 2) / 65535f;
            }
            return q;
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
            public readonly List<int>[] Sub = MakeSubs();
            static List<int>[] MakeSubs() { var a = new List<int>[5]; for (int i = 0; i < 5; i++) a[i] = new List<int>(); return a; }
            public readonly List<int> ColTris = new List<int>();
            /// <summary>Clustered light sources: section-local Unity position, max level, colour, block count.</summary>
            public readonly List<(float x, float y, float z, int level, int rgb, int count)> Lights = new List<(float, float, float, int, int, int)>();
            public int Faces => TriCount / 6;
            public int TriCount { get { int n = 0; foreach (var t in Sub) n += t.Count; return n; } }
            public void Clear() { Pos.Clear(); Nrm.Clear(); Uv.Clear(); foreach (var t in Sub) t.Clear(); ColTris.Clear(); Lights.Clear(); }
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
            var hc = new float[4];
            var clusters = new Dictionary<(int, int, int, int), (float x, float y, float z, int level, int count)>();
            for (int i = 0; i < 4096; i++)
            {
                ushort id = arr[i];
                if (id == 0 || !Palette.TryGetValue(id, out var e)) continue;
                int lx = i & 15, lz = (i >> 4) & 15, ly = i >> 8;
                int wx = bx + lx, wy = by + ly, wz = bz + lz;
                var tris = m.Sub[e.Layer];
                bool collides = (e.Flags & BlockPaletteEntry.Collides) != 0;
                if ((e.Light & 15) > 0) // 0.14.0: bit 0x40 = emissive quads without block light (glow only)
                {
                    var ck = (lx >> 3, ly >> 3, lz >> 3, e.LightRgb);
                    clusters.TryGetValue(ck, out var cl);
                    clusters[ck] = (cl.x + lx + 0.5f, cl.y + ly + 0.5f, cl.z - (lz + 0.5f), Math.Max(cl.level, e.Light & 15), cl.count + 1);
                }
                bool useQuads = (e.Flags & BlockPaletteEntry.HasQuads) != 0 && e.Quads.Count > 0;
                if (useQuads)
                {
                    foreach (var q in e.Quads)
                    {
                        if (q.Cull < 6)
                        {
                            int qx = wx + Dir[q.Cull, 0], qy = wy + Dir[q.Cull, 1], qz = wz + Dir[q.Cull, 2];
                            if (Occludes(qx, qy, qz)) continue;
                            if (e.FullBox && (e.Flags & BlockPaletteEntry.FullOpaque) == 0 && At(qx, qy, qz) == id) continue; // glass-glass
                        }
                        ModelQuad(m, tris, q, lx, ly, lz, g, eps);
                    }
                    if (!collides) continue;
                    tris = null; // boxes below: collision only
                }
                if ((e.Flags & BlockPaletteEntry.Cross) != 0) { Cross(m, tris, e.Tiles[2], lx, ly, lz, g, eps); continue; }
                bool fluid = (e.Flags & BlockPaletteEntry.Fluid) != 0;
                if (fluid)
                {
                    bool fullAbove = SameFluid(e, wx, wy + 1, wz);
                    hc[0] = fullAbove ? 1 : CornerH(e, wx, wy, wz, 0, 0); hc[1] = fullAbove ? 1 : CornerH(e, wx, wy, wz, 1, 0);
                    hc[2] = fullAbove ? 1 : CornerH(e, wx, wy, wz, 1, 1); hc[3] = fullAbove ? 1 : CornerH(e, wx, wy, wz, 0, 1);
                }
                for (int b = 0; b + 6 <= e.Boxes.Length; b += 6)
                {
                    float x0 = e.Boxes[b] / 16f, y0 = e.Boxes[b + 1] / 16f, z0 = e.Boxes[b + 2] / 16f, x1 = e.Boxes[b + 3] / 16f, y1 = e.Boxes[b + 4] / 16f, z1 = e.Boxes[b + 5] / 16f;
                    if (fluid) y1 = 1f; // replaced per corner below
                    for (int d = 0; d < 6; d++)
                    {
                        bool atEdge = fluid || d switch { 0 => y0 == 0, 1 => y1 == 1, 2 => z0 == 0, 3 => z1 == 1, 4 => x0 == 0, _ => x1 == 1 };
                        int nx = wx + Dir[d, 0], ny = wy + Dir[d, 1], nz = wz + Dir[d, 2];
                        if (atEdge && Occludes(nx, ny, nz)) continue;
                        if (fluid) { if (SameFluid(e, nx, ny, nz)) continue; }
                        else if (atEdge && e.FullBox && (e.Flags & BlockPaletteEntry.FullOpaque) == 0 && At(nx, ny, nz) == id) continue; // glass-glass, leaves-leaves
                        float u0, u1, v0, v1;
                        switch (d)
                        {
                            case 0: case 1: { float y = d == 0 ? y0 : y1; Set(c, x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1); u0 = x0; u1 = x1; v0 = z0; v1 = z1; break; }
                            case 2: case 3: { float z = d == 2 ? z0 : z1; Set(c, x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z); u0 = x0; u1 = x1; v0 = y0; v1 = y1; break; }
                            default: { float x = d == 4 ? x0 : x1; Set(c, x, y0, z0, x, y0, z1, x, y1, z1, x, y1, z0); u0 = z0; u1 = z1; v0 = y0; v1 = y1; break; }
                        }
                        if (fluid)
                            for (int k = 0; k < 4; k++)
                                if (c[k * 3 + 1] >= 1f) c[k * 3 + 1] = hc[((c[k * 3] > 0.5f ? 1 : 0) + (c[k * 3 + 2] > 0.5f ? 2 : 0)) switch { 0 => 0, 1 => 1, 3 => 2, _ => 3 }];
                        Quad(m, tris, collides, c, lx, ly, lz, Dir[d, 0], Dir[d, 1], -Dir[d, 2], e.Tiles[d], u0, u1, v0, v1, g, eps, false);
                    }
                }
            }
            foreach (var kv in clusters) { var v = kv.Value; m.Lights.Add((v.x / v.count, v.y / v.count, v.z / v.count, v.level, kv.Key.Item4, v.count)); }
        }

        /// <summary>A baked-model quad: positions block-local MC, uv = sprite fractions (v down).</summary>
        public static void ModelQuad(MeshData m, List<int> tris, BlockPaletteEntry.Q q, int lx, int ly, int lz, float g, float eps)
        {
            float col = q.Tile % (int)g, row = q.Tile / (int)g, span = 1 - 2 * eps, vTop = 1f - row / g;
            int start = m.Pos.Count / 3;
            int d = Math.Min((int)q.Dir, 5);
            for (int k = 0; k < 4; k++)
            {
                m.Pos.Add(lx + q.Pos[k * 3]); m.Pos.Add(ly + q.Pos[k * 3 + 1]); m.Pos.Add(-(lz + q.Pos[k * 3 + 2]));
                m.Nrm.Add(Dir[d, 0]); m.Nrm.Add(Dir[d, 1]); m.Nrm.Add(-Dir[d, 2]);
                m.Uv.Add((col + eps + Clamp01(q.Uv[k * 2]) * span) / g); m.Uv.Add(vTop - (eps + Clamp01(q.Uv[k * 2 + 1]) * span) / g);
            }
            int a0 = start, a1 = start + 1, a2 = start + 2, a3 = start + 3;
            if (!Front(m.Pos, a0, a1, a2, Dir[d, 0], Dir[d, 1], -Dir[d, 2])) { int t = a1; a1 = a3; a3 = t; }
            tris.Add(a0); tris.Add(a1); tris.Add(a2); tris.Add(a0); tris.Add(a2); tris.Add(a3);
        }
        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        bool SameFluid(BlockPaletteEntry e, int x, int y, int z)
        {
            var id = At(x, y, z);
            return id != 0 && Palette.TryGetValue(id, out var o) && (o.Flags & BlockPaletteEntry.Fluid) != 0 && o.Tiles[1] == e.Tiles[1];
        }

        /// <summary>Fluid surface height at a block corner: 1 if any sharing column has the same fluid above, else the average of the same-fluid heights.</summary>
        float CornerH(BlockPaletteEntry e, int x, int y, int z, int cx, int cz)
        {
            float sum = 0; int n = 0;
            for (int dx = cx - 1; dx <= cx; dx++)
                for (int dz = cz - 1; dz <= cz; dz++)
                {
                    int ax = x + dx, az = z + dz;
                    if (!SameFluid(e, ax, y, az)) continue;
                    if (SameFluid(e, ax, y + 1, az)) return 1f;
                    var o = Palette[At(ax, y, az)];
                    sum += o.Boxes.Length >= 6 ? o.Boxes[4] / 16f : 1f; n++;
                }
            return n == 0 ? (e.Boxes.Length >= 6 ? e.Boxes[4] / 16f : 1f) : sum / n;
        }

        void Cross(MeshData m, List<int> tris, int tile, int lx, int ly, int lz, float g, float eps)
        {
            var c = new float[12];
            const float a = 0.15f, b = 0.85f; // MC's cross model inset
            Set(c, a, 0, a, b, 0, b, b, 1, b, a, 1, a);
            Quad(m, tris, false, c, lx, ly, lz, 0.707f, 0, 0.707f, tile, 0, 1, 0, 1, g, eps, true);
            Set(c, b, 0, a, a, 0, b, a, 1, b, b, 1, a);
            Quad(m, tris, false, c, lx, ly, lz, -0.707f, 0, 0.707f, tile, 0, 1, 0, 1, g, eps, true);
        }

        /// <summary>Emit one quad (corners c in MC block-local coords; uv order (u0,v0)(u1,v0)(u1,v1)(u0,v1)); normal in Unity coords.</summary>
        static void Quad(MeshData m, List<int> tris, bool collides, float[] c, int lx, int ly, int lz, float nx, float ny, float nz, int tile, float u0, float u1, float v0, float v1, float g, float eps, bool doubleSided)
        {
            float col = tile % (int)g, row = tile / (int)g, span = 1 - 2 * eps;
            float U0 = (col + eps + u0 * span) / g, U1 = (col + eps + u1 * span) / g;
            float Vb = 1f - (row + 1) / g; // tile bottom (texture origin bottom-left; PNG row 0 = top)
            float V0 = Vb + (eps + v0 * span) / g, V1 = Vb + (eps + v1 * span) / g;
            for (int side = 0; side < (doubleSided ? 2 : 1); side++)
            {
                float sn = side == 0 ? 1 : -1;
                int start = m.Pos.Count / 3;
                for (int k = 0; k < 4; k++)
                {
                    m.Pos.Add(lx + c[k * 3]); m.Pos.Add(ly + c[k * 3 + 1]); m.Pos.Add(-(lz + c[k * 3 + 2]));
                    if (doubleSided) { m.Nrm.Add(0); m.Nrm.Add(1); m.Nrm.Add(0); } else { m.Nrm.Add(nx); m.Nrm.Add(ny); m.Nrm.Add(nz); }
                }
                m.Uv.Add(U0); m.Uv.Add(V0); m.Uv.Add(U1); m.Uv.Add(V0); m.Uv.Add(U1); m.Uv.Add(V1); m.Uv.Add(U0); m.Uv.Add(V1);
                int a0 = start, a1 = start + 1, a2 = start + 2, a3 = start + 3;
                if (!Front(m.Pos, a0, a1, a2, nx * sn, ny * sn, nz * sn)) { int t = a1; a1 = a3; a3 = t; }
                tris?.Add(a0); tris?.Add(a1); tris?.Add(a2); tris?.Add(a0); tris?.Add(a2); tris?.Add(a3);
                if (collides) { m.ColTris.Add(a0); m.ColTris.Add(a1); m.ColTris.Add(a2); m.ColTris.Add(a0); m.ColTris.Add(a2); m.ColTris.Add(a3); }
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

    /// <summary>Entities (EvEntMesh / EvEntUpdate): cached meshes by id + the latest full snapshot.</summary>
    public sealed class EntityStore
    {
        public readonly Dictionary<uint, List<BlockPaletteEntry.Q>> Meshes = new Dictionary<uint, List<BlockPaletteEntry.Q>>();
        public readonly HashSet<uint> CompleteMeshes = new HashSet<uint>(), NewMeshes = new HashSet<uint>();
        public struct Ent { public uint Id, Mesh; public float X, Y, Z; public byte Flags; public float GroundY; }
        public Dictionary<uint, Ent> Current = new Dictionary<uint, Ent>();
        Dictionary<uint, Ent> _building;
        public int Snapshots;
        /// <summary>0.14.1 (EvKinetic): rotating block entities: MC axis (0..2), angle since the mesh capture (deg), rate (deg/s), receive time.</summary>
        public struct Spin { public byte Axis; public float Angle, Rate; public double At; }
        public Dictionary<uint, Spin> Spins = new Dictionary<uint, Spin>();
        Dictionary<uint, Spin> _spinBuilding;
        public static Func<double> Clock = () => DateTime.UtcNow.Ticks / 1e7;

        public bool Handle(ushort type, byte[] p)
        {
            if (type == ValCraftLink.EvBlocksReset) { Meshes.Clear(); CompleteMeshes.Clear(); NewMeshes.Clear(); Current.Clear(); Spins.Clear(); _building = null; return false; }
            if (type == ValCraftLink.EvEntMesh)
            {
                uint id = BitConverter.ToUInt32(p, 0); int first = BitConverter.ToUInt16(p, 4), n = p[6];
                if (first == 0 || !Meshes.TryGetValue(id, out var list)) { list = new List<BlockPaletteEntry.Q>(); Meshes[id] = list; CompleteMeshes.Remove(id); }
                for (int i = 0; i < n && 8 + (i + 1) * 44 <= p.Length; i++) list.Add(BlockStore.ReadQuad(p, 8 + i * 44));
                if (p[7] != 0) { CompleteMeshes.Add(id); NewMeshes.Add(id); }
                return true;
            }
            if (type == ValCraftLink.EvKinetic)
            {
                bool first = p[0] != 0, last = p[1] != 0; int n = BitConverter.ToUInt16(p, 2); double now = Clock();
                if (first || _spinBuilding == null) _spinBuilding = new Dictionary<uint, Spin>();
                var d = _spinBuilding;
                for (int i = 0; i < n && 4 + (i + 1) * 13 <= p.Length; i++)
                {
                    int o = 4 + i * 13;
                    d[BitConverter.ToUInt32(p, o)] = new Spin { Axis = p[o + 4], Angle = BitConverter.ToSingle(p, o + 5), Rate = BitConverter.ToSingle(p, o + 9), At = now };
                }
                if (last) { Spins = d; _spinBuilding = null; }
                return true;
            }
            if (type == ValCraftLink.EvEntUpdate)
            {
                bool first = p[0] != 0, last = p[1] != 0; int n = BitConverter.ToUInt16(p, 2);
                if (first || _building == null) _building = new Dictionary<uint, Ent>();
                for (int i = 0; i < n && 4 + (i + 1) * 25 <= p.Length; i++)
                {
                    int o = 4 + i * 25;
                    var e = new Ent { Id = BitConverter.ToUInt32(p, o), Mesh = BitConverter.ToUInt32(p, o + 4), X = BitConverter.ToSingle(p, o + 8), Y = BitConverter.ToSingle(p, o + 12), Z = BitConverter.ToSingle(p, o + 16), Flags = p[o + 20], GroundY = BitConverter.ToSingle(p, o + 21) };
                    _building[e.Id] = e;
                }
                if (last) { Current = _building; _building = null; Snapshots++; }
                return true;
            }
            return false;
        }
    }
}
