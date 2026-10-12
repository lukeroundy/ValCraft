using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// Phase A: Valheim terrain -> MC blocks. Around the player's MC position, 8x8-column tiles (MC block grid, so
    /// 1 block = 1 m on the anchor) are sampled: height by raycast on the terrain layer (fallback ZoneSystem height),
    /// material from biome + paint (cultivated/cleared/paved) + slope + sea level. Nearest tiles first, a few per frame;
    /// a tile is re-sent only when its content changed (rescans pick up hoe/pickaxe edits). Sent as EvTerrainTile events.
    /// </summary>
    internal static class TerrainExport
    {
        internal static bool Enabled = true;
        internal static float Radius = 48f;
        internal static bool Active => Enabled && PlayerSync.Anchored;
        const int T = ValCraftLink.TerrainTile, SamplePerFrame = 4, RescanPerFrame = 2;
        const float SeaLevel = 30f;

        static readonly Dictionary<long, int> _sentHash = new Dictionary<long, int>();
        static readonly Queue<long> _rescan = new Queue<long>();
        static Vector3 _anchorSeen;
        static int _terrainMask, _sentTiles;
        static bool _init;
        static Func<object> _worldGen, _zone;
        static MethodInfo _getBiome, _groundHeight, _findHm, _isCultivated, _isCleared, _isPaved;
        static float _nextLog;

        static void Init()
        {
            _init = true;
            _terrainMask = LayerMask.GetMask("terrain");
            var wg = AccessTools.TypeByName("WorldGenerator");
            _worldGen = wg == null ? null : PlayerSync.FindSingleton(wg);
            _getBiome = wg == null ? null : AccessTools.Method(wg, "GetBiome", new[] { typeof(Vector3) });
            var zs = AccessTools.TypeByName("ZoneSystem");
            _zone = zs == null ? null : PlayerSync.FindSingleton(zs);
            _groundHeight = zs == null ? null : AccessTools.Method(zs, "GetGroundHeight", new[] { typeof(Vector3) });
            var hm = AccessTools.TypeByName("Heightmap");
            _findHm = hm == null ? null : AccessTools.Method(hm, "FindHeightmap", new[] { typeof(Vector3) });
            _isCultivated = hm == null ? null : AccessTools.Method(hm, "IsCultivated", new[] { typeof(Vector3) });
            _isCleared = hm == null ? null : AccessTools.Method(hm, "IsCleared", new[] { typeof(Vector3) });
            _isPaved = hm == null ? null : AccessTools.Method(hm, "IsPaved", new[] { typeof(Vector3) }) ?? AccessTools.Method(hm, "IsLava", new[] { typeof(Vector3) }) /* not paved, ignored below */;
            if (_isPaved != null && _isPaved.Name != "IsPaved") _isPaved = null;
            ValCraftPlugin.Log.LogInfo($"[terrain] WorldGenerator.GetBiome={_getBiome != null && _worldGen != null}, Heightmap.FindHeightmap={_findHm != null}, IsCultivated={_isCultivated != null}, IsCleared={_isCleared != null}, IsPaved={_isPaved != null}");
        }

        public static void Update(ValCraftLink link, bool connected)
        {
            if (!Active || !connected || !PlayerSync.HaveState) return;
            if (!_init) Init();
            var a = PlayerSync.Anchor;
            if (a != _anchorSeen) { _anchorSeen = a; _sentHash.Clear(); _rescan.Clear(); }
            var s = PlayerSync.State;
            int ptx = Mathf.FloorToInt((float)s.FeetX / T), ptz = Mathf.FloorToInt((float)s.FeetZ / T);
            int r = Mathf.CeilToInt(Radius / T);

            // 1) unsent tiles, nearest first (spiral by ring)
            int budget = SamplePerFrame;
            for (int ring = 0; ring <= r && budget > 0; ring++)
                for (int dx = -ring; dx <= ring && budget > 0; dx++)
                    for (int dz = -ring; dz <= ring && budget > 0; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                        long k = Key(ptx + dx, ptz + dz);
                        if (_sentHash.ContainsKey(k)) continue;
                        if (!SendTile(link, ptx + dx, ptz + dz, a)) return; // ring full: try next frame
                        budget--;
                    }
            // 2) rescan already-sent tiles near the player for edits
            for (int i = 0; i < RescanPerFrame && _rescan.Count > 0; i++)
            {
                long k = _rescan.Dequeue();
                int tx = (int)(k >> 32), tz = (int)k;
                if (Math.Abs(tx - ptx) > r || Math.Abs(tz - ptz) > r) { _sentHash.Remove(k); continue; } // out of range: forget
                if (!SendTile(link, tx, tz, a)) { _rescan.Enqueue(k); return; }
            }
            if (Time.unscaledTime > _nextLog) { _nextLog = Time.unscaledTime + 30f; ValCraftPlugin.Log.LogInfo($"[terrain] {_sentTiles} tiles sent, {_sentHash.Count} tracked (radius {Radius} m)"); }
        }

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        /// <summary>Sample a tile; send if new/changed. False = ring full.</summary>
        static bool SendTile(ValCraftLink link, int tx, int tz, Vector3 a)
        {
            var payload = new byte[8 + T * T * 3];
            BitConverter.GetBytes(tx).CopyTo(payload, 0);
            BitConverter.GetBytes(tz).CopyTo(payload, 4);
            // Fine grid: 0.5 m over the tile plus a 1-block border. g[2*(i+1)+1, ...] is block (i,j)'s centre.
            const int G = 2 * (T + 2) + 1;
            var g = new float[G, G];
            for (int i = 0; i < G; i++)
                for (int j = 0; j < G; j++)
                    g[i, j] = HeightAt(tx * T - 1 + i * 0.5f, tz * T - 1 + j * 0.5f, a);
            var h = new float[T + 2, T + 2];
            for (int i = 0; i < T + 2; i++)
                for (int j = 0; j < T + 2; j++)
                    h[i, j] = g[2 * i + 1, 2 * j + 1];
            int hash = 17;
            for (int i = 0; i < T; i++)
                for (int j = 0; j < T; j++)
                {
                    float y = h[i + 1, j + 1];
                    // Block top surface = floor(true height): blocks never stick out above the real ground (no ledges);
                    // the smooth heightfield below carries the player at the true height anyway.
                    short top = float.IsNaN(y) ? short.MinValue : (short)Mathf.Clamp(Mathf.FloorToInt(y - a.y) - 1, -64, 319);
                    byte mat = float.IsNaN(y) ? (byte)0 : Material(tx * T + i, tz * T + j, a, y, h, i + 1, j + 1);
                    int o = 8 + (i * T + j) * 3;
                    BitConverter.GetBytes(top).CopyTo(payload, o);
                    payload[o + 2] = mat;
                    hash = hash * 31 + top * 17 + mat;
                }
            // Smooth heightfield cells (4 m, 0.5 m samples) for movement; included in the change hash (cm).
            var cells = new List<byte[]>();
            for (int cx = 0; cx < T / ValCraftLink.HeightCell; cx++)
                for (int cz = 0; cz < T / ValCraftLink.HeightCell; cz++)
                {
                    const int R = ValCraftLink.HeightRes;
                    var pl = new byte[12 + R * R * 2];
                    int cellX = tx * (T / ValCraftLink.HeightCell) + cx, cellZ = tz * (T / ValCraftLink.HeightCell) + cz;
                    float baseY = float.MaxValue;
                    for (int i = 0; i < R; i++) for (int j = 0; j < R; j++) { float v = g[2 + cx * 8 + i, 2 + cz * 8 + j]; if (!float.IsNaN(v)) baseY = Mathf.Min(baseY, v - a.y); }
                    if (baseY == float.MaxValue) continue;
                    BitConverter.GetBytes(cellX).CopyTo(pl, 0); BitConverter.GetBytes(cellZ).CopyTo(pl, 4); BitConverter.GetBytes(baseY).CopyTo(pl, 8);
                    for (int i = 0; i < R; i++) for (int j = 0; j < R; j++)
                    {
                        float v = g[2 + cx * 8 + i, 2 + cz * 8 + j];
                        short cm = float.IsNaN(v) ? short.MinValue : (short)Mathf.Clamp(Mathf.RoundToInt((v - a.y - baseY) * 100f), 0, 32767);
                        BitConverter.GetBytes(cm).CopyTo(pl, 12 + (i * R + j) * 2);
                        hash = hash * 31 + cm;
                    }
                    cells.Add(pl);
                }
            long k = Key(tx, tz);
            if (_sentHash.TryGetValue(k, out int old) && old == hash) { _rescan.Enqueue(k); return true; }
            foreach (var c in cells) if (!link.TrySend(ValCraftLink.EvTerrainHeights, c)) return false; // resent whole next time
            if (!link.TrySend(ValCraftLink.EvTerrainTile, payload)) return false;
            _sentHash[k] = hash;
            _rescan.Enqueue(k);
            _sentTiles++;
            return true;
        }

        static Vector3 ToUnity(int bx, int bz, Vector3 a) => new Vector3(bx + 0.5f + a.x, 0f, -(bz + 0.5f) + a.z);

        /// <summary>True Valheim ground height at MC (x, z) (continuous coordinates).</summary>
        static float HeightAt(float mx, float mz, Vector3 a)
        {
            var p = new Vector3(mx + a.x, 0f, -mz + a.z);
            if (Physics.Raycast(new Vector3(p.x, 1500f, p.z), Vector3.down, out var hit, 3000f, _terrainMask, QueryTriggerInteraction.Ignore)) return hit.point.y;
            try { var z = _zone?.Invoke(); if (z != null && _groundHeight != null) return (float)_groundHeight.Invoke(z, new object[] { p }); } catch { }
            return float.NaN; // terrain not loaded there yet: column skipped, tile re-sent on rescan
        }

        // 1 grass, 2 cultivated, 3 stone, 4 sand, 5 snow, 6 mud, 7 ash, 8 paved, 9 gravel, 10 dark grass (podzol)
        static byte Material(int bx, int bz, Vector3 a, float y, float[,] h, int i, int j)
        {
            var p = ToUnity(bx, bz, a); p.y = y;
            try
            {
                if (_findHm != null)
                {
                    var hm = _findHm.Invoke(null, new object[] { p });
                    if (hm != null)
                    {
                        if (_isPaved != null && (bool)_isPaved.Invoke(hm, new object[] { p })) return 8;
                        if (_isCultivated != null && (bool)_isCultivated.Invoke(hm, new object[] { p })) return 2;
                        if (_isCleared != null && (bool)_isCleared.Invoke(hm, new object[] { p })) return 9;
                    }
                }
            }
            catch { }
            float slope = 0;
            foreach (var n in new[] { h[i - 1, j], h[i + 1, j], h[i, j - 1], h[i, j + 1] }) if (!float.IsNaN(n)) slope = Mathf.Max(slope, Mathf.Abs(n - y));
            int biome = 1;
            try { var wg = _worldGen?.Invoke(); if (wg != null && _getBiome != null) biome = Convert.ToInt32(_getBiome.Invoke(wg, new object[] { p })); } catch { }
            if (y < SeaLevel - 0.5f) return 4;
            switch (biome)
            {
                case 4: case 64: return slope > 1.5f ? (byte)3 : (byte)5; // Mountain / DeepNorth
                case 2: return 6;                                           // Swamp
                case 8: return slope > 1.5f ? (byte)3 : (byte)10;          // BlackForest
                case 32: return 7;                                          // AshLands
                case 256: return 4;                                         // Ocean
                case 512: return slope > 1.5f ? (byte)3 : (byte)9;         // Mistlands
                default: return slope > 1.5f ? (byte)3 : (byte)1;          // Meadows / Plains
            }
        }

        // Phase B hook: MC block edits are logged here; applying them to Valheim terrain comes next.
        public static void OnBlockChanged(byte[] p)
        {
            if (p.Length < 16) return;
            int x = BitConverter.ToInt32(p, 0), y = BitConverter.ToInt32(p, 4), z = BitConverter.ToInt32(p, 8), kind = BitConverter.ToInt32(p, 12);
            string id = System.Text.Encoding.UTF8.GetString(p, 16, p.Length - 16).TrimEnd('\0');
            var a = PlayerSync.Anchor;
            var u = new Vector3(x + 0.5f + a.x, y + 0.5f + a.y, -(z + 0.5f) + a.z);
            ValCraftPlugin.Log.LogInfo($"[terrain] MC {(kind == 0 ? "broke" : "placed")} {id} at MC ({x},{y},{z}) = Valheim {u} — not applied to Valheim yet (Phase B)");
        }
    }
}
