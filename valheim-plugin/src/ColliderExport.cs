using System;
using System.Collections.Generic;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// Milestone 5 (part 1): nearby Valheim colliders -> Minecraft collision boxes (MC coordinates, axis-aligned).
    /// - Building pieces/rocks/trees: Physics.OverlapSphere around the player. Each collider's boxes are computed once
    ///   (Unity world AABBs) and cached by instance id; recomputed only when its transform moves (doors) — incremental.
    ///   * axis-aligned upright box -> 1 exact AABB
    ///   * upright box rotated in yaw (22.5° build snaps) or a thin wall-like mesh -> sliced into ≤0.15 m segments along
    ///     its long axis, each segment's AABB (thin walls stay thin-ish, no gaps)
    ///   * everything else (tilted roofs/ramps, stairs meshes, rocks, big meshes) -> vertical column raster with
    ///     collider.Raycast (top + bottom per 0.5 m cell), which gives MC-steppable stairs/ramps
    ///   * small things (< 1 m footprint, e.g. tree trunks, posts) -> their bounds
    /// - Terrain: heightfield, 1 m cells around the player, each cell a 2 m-thick slab whose top is the terrain height.
    /// - Transfer: a full snapshot of all boxes in range (double-buffered in shared memory) when anything changed.
    /// </summary>
    internal static class ColliderExport
    {
        internal static bool Enabled = true;
        internal static float Radius = 32f;
        const float Interval = 0.25f, TerrainRadius = 24f, TerrainCell = 1f, TerrainSlab = 2f;
        const int RecomputeBudget = 400;

        sealed class Entry { public Vector3 Pos; public Quaternion Rot; public Vector3 Scale; public readonly List<Bounds> Boxes = new List<Bounds>(); }

        static readonly Dictionary<int, Entry> _cache = new Dictionary<int, Entry>();
        static readonly Dictionary<long, float> _terrain = new Dictionary<long, float>(); // cell -> height (NaN = none)
        static readonly Collider[] _overlap = new Collider[8192];
        static float[] _out = new float[ValCraftLink.CollMaxBoxes * 6];
        static float _next;
        static int _objMask = -1, _terrainMask;
        static long _lastHash;
        static float _lastPublish;
        static bool _publishedEmpty;
        static int _lastLoggedCount = -1;
        static Vector3 _lastAnchor;

        public static void Update(ValCraftLink link, bool anchored)
        {
            if (link == null) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;
            if (!Enabled || !anchored)
            {
                // Tell MC "no Valheim collision" so it doesn't hover waiting for a snapshot.
                if (!Enabled && !_publishedEmpty) { link.PublishCollision(_out, 0); _publishedEmpty = true; }
                return;
            }
            _publishedEmpty = false;
            if (_objMask == -1)
            {
                _objMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "vehicle");
                _terrainMask = LayerMask.GetMask("terrain");
                ValCraftPlugin.Log.LogInfo($"[collision] masks objects=0x{_objMask:X} terrain=0x{_terrainMask:X}");
            }
            var player = PlayerSync.Player;
            if (player == null) return;
            var center = player.transform.position;
            var anchor = PlayerSync.Anchor;
            if (anchor != _lastAnchor) { _lastAnchor = anchor; _lastHash = 0; }

            int n = 0;
            long hash = 17;
            void Emit(Bounds b)
            {
                if (n >= ValCraftLink.CollMaxBoxes) return;
                int o = n * 6;
                _out[o] = b.min.x - anchor.x; _out[o + 1] = b.min.y - anchor.y; _out[o + 2] = -(b.max.z - anchor.z);
                _out[o + 3] = b.max.x - anchor.x; _out[o + 4] = b.max.y - anchor.y; _out[o + 5] = -(b.min.z - anchor.z);
                for (int i = 0; i < 6; i++) hash = hash * 31 + BitConverter.ToInt32(BitConverter.GetBytes(_out[o + i]), 0);
                n++;
            }

            // --- objects (pieces first in priority: they're what you walk into) ---
            int found = Physics.OverlapSphereNonAlloc(center, Radius, _overlap, _objMask, QueryTriggerInteraction.Ignore);
            var seen = new HashSet<int>();
            int budget = RecomputeBudget;
            for (int i = 0; i < found; i++)
            {
                var c = _overlap[i];
                if (c == null || !c.enabled || c.isTrigger) continue;
                if (c.transform.IsChildOf(player.transform)) continue;
                if (BlockRender.Root != null && c.transform.IsChildOf(BlockRender.Root)) continue; // MC's own blocks: MC already collides with them
                var rb = c.attachedRigidbody;
                if (rb != null && !rb.isKinematic && rb.gameObject.layer != LayerMask.NameToLayer("piece")) continue; // loose physics objects
                int id = c.GetInstanceID();
                seen.Add(id);
                var t = c.transform;
                if (!_cache.TryGetValue(id, out var e) || e.Pos != t.position || e.Rot != t.rotation || e.Scale != t.lossyScale)
                {
                    if (budget-- <= 0) { if (e == null) continue; }
                    else
                    {
                        if (e == null) _cache[id] = e = new Entry();
                        e.Pos = t.position; e.Rot = t.rotation; e.Scale = t.lossyScale;
                        e.Boxes.Clear();
                        try { Compute(c, e.Boxes); } catch (Exception ex) { ValCraftPlugin.Log.LogDebug($"[collision] {c.name}: {ex.Message}"); }
                    }
                }
                foreach (var b in e.Boxes) Emit(b);
            }
            if (_cache.Count > seen.Count + 2048) // forget far-away colliders
            {
                var drop = new List<int>();
                foreach (var k in _cache.Keys) if (!seen.Contains(k)) drop.Add(k);
                foreach (var k in drop) _cache.Remove(k);
            }

            // --- terrain heightfield (only when terrain isn't streamed as real MC blocks) ---
            int r = TerrainExport.Active ? -1 : Mathf.CeilToInt(TerrainRadius / TerrainCell);
            int cx = Mathf.FloorToInt(center.x / TerrainCell), cz = Mathf.FloorToInt(center.z / TerrainCell);
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    int gx = cx + dx, gz = cz + dz;
                    long key = ((long)gx << 32) ^ (uint)gz;
                    if (!_terrain.TryGetValue(key, out float h))
                    {
                        var origin = new Vector3((gx + 0.5f) * TerrainCell, center.y + 300f, (gz + 0.5f) * TerrainCell);
                        h = Physics.Raycast(origin, Vector3.down, out var hit, 1000f, _terrainMask, QueryTriggerInteraction.Ignore) ? hit.point.y : float.NaN;
                        _terrain[key] = h;
                    }
                    if (float.IsNaN(h)) continue;
                    var min = new Vector3(gx * TerrainCell, h - TerrainSlab, gz * TerrainCell);
                    Emit(new Bounds(min + new Vector3(TerrainCell, TerrainSlab, TerrainCell) * 0.5f, new Vector3(TerrainCell, TerrainSlab, TerrainCell)));
                }
            if (_terrain.Count > 20000) _terrain.Clear(); // terrain edits (hoe/pickaxe) also get picked up this way, eventually

            if (hash != _lastHash || Time.unscaledTime - _lastPublish > 2f)
            {
                link.PublishCollision(_out, n);
                _lastHash = hash; _lastPublish = Time.unscaledTime;
                if (_lastLoggedCount < 0 || Math.Abs(n - _lastLoggedCount) > 500)
                {
                    _lastLoggedCount = n;
                    ValCraftPlugin.Log.LogInfo($"[collision] exported {n} boxes ({found} colliders in {Radius} m, {_cache.Count} cached)");
                }
            }
        }

        /// <summary>Unity-world AABBs approximating one collider.</summary>
        internal static void Compute(Collider c, List<Bounds> outBoxes)
        {
            var b = c.bounds;
            if (b.size.x < 1f && b.size.z < 1f) { outBoxes.Add(b); return; }
            var t = c.transform;
            Vector3 localCenter, localSize;
            bool obb = false;
            if (c is BoxCollider bc) { localCenter = bc.center; localSize = bc.size; obb = true; }
            else if (c is MeshCollider mc && mc.sharedMesh != null)
            {
                localCenter = mc.sharedMesh.bounds.center; localSize = mc.sharedMesh.bounds.size;
                // wall-like slab: thin in one horizontal direction -> treat as box (rasters miss thin walls)
                var ws = Vector3.Scale(localSize, Abs(t.lossyScale));
                obb = Mathf.Min(ws.x, ws.z) < 0.6f && Upright(t);
            }
            else { localCenter = Vector3.zero; localSize = Vector3.zero; }

            if (obb && Upright(t))
            {
                Vector3 c0 = t.TransformPoint(localCenter);
                Vector3 ax = t.TransformVector(new Vector3(localSize.x, 0, 0)), ay = t.TransformVector(new Vector3(0, localSize.y, 0)), az = t.TransformVector(new Vector3(0, 0, localSize.z));
                if (AxisAligned(ax) && AxisAligned(az)) { outBoxes.Add(b); return; }
                // slice along the longer horizontal axis
                Vector3 hl, hs; // long, short (full-length vectors)
                // identify which of ax/ay/az is vertical
                Vector3[] axes = { ax, ay, az };
                int vi = 0; float best = -1;
                for (int i = 0; i < 3; i++) { float d = axes[i].sqrMagnitude < 1e-8f ? 0 : Mathf.Abs(Vector3.Dot(axes[i].normalized, Vector3.up)); if (d > best) { best = d; vi = i; } }
                var h1 = axes[(vi + 1) % 3]; var h2 = axes[(vi + 2) % 3]; var v = axes[vi];
                if (h1.sqrMagnitude >= h2.sqrMagnitude) { hl = h1; hs = h2; } else { hl = h2; hs = h1; }
                int n = Mathf.Clamp(Mathf.CeilToInt(hl.magnitude / 0.15f), 1, 256);
                Vector3 seg = hl / n;
                Vector3 ext = Abs(seg) * 0.5f + Abs(hs) * 0.5f + Abs(v) * 0.5f;
                for (int i = 0; i < n; i++)
                {
                    Vector3 sc = c0 + hl * ((i + 0.5f) / n - 0.5f);
                    outBoxes.Add(new Bounds(sc, ext * 2f));
                }
                return;
            }

            // column raster (tilted boxes, stairs, rocks, terrain-like meshes)
            float cell = 0.5f;
            while ((b.size.x / cell) * (b.size.z / cell) > 1024f) cell *= 1.5f;
            int nx = Mathf.Max(1, Mathf.CeilToInt(b.size.x / cell)), nz = Mathf.Max(1, Mathf.CeilToInt(b.size.z / cell));
            float top = b.max.y + 0.5f, bottom = b.min.y - 0.5f, len = b.size.y + 1f;
            for (int ix = 0; ix < nx; ix++)
                for (int iz = 0; iz < nz; iz++)
                {
                    float x = b.min.x + (ix + 0.5f) * cell, z = b.min.z + (iz + 0.5f) * cell;
                    if (!c.Raycast(new Ray(new Vector3(x, top, z), Vector3.down), out var hd, len)) continue;
                    float y0 = c.Raycast(new Ray(new Vector3(x, bottom, z), Vector3.up), out var hu, len) ? hu.point.y : hd.point.y - 0.5f;
                    if (y0 > hd.point.y - 0.05f) y0 = hd.point.y - 0.05f;
                    var min = new Vector3(x - cell * 0.5f, y0, z - cell * 0.5f);
                    var max = new Vector3(x + cell * 0.5f, hd.point.y, z + cell * 0.5f);
                    outBoxes.Add(new Bounds((min + max) * 0.5f, max - min));
                }
        }

        static bool Upright(Transform t) => Mathf.Abs(Vector3.Dot(t.up, Vector3.up)) > 0.995f || Mathf.Abs(Vector3.Dot(t.right, Vector3.up)) > 0.995f || Mathf.Abs(Vector3.Dot(t.forward, Vector3.up)) > 0.995f;
        static bool AxisAligned(Vector3 v)
        {
            if (v.sqrMagnitude < 1e-8f) return true;
            var n = v.normalized;
            return Mathf.Abs(n.x) > 0.9998f || Mathf.Abs(n.y) > 0.9998f || Mathf.Abs(n.z) > 0.9998f;
        }
        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0, v.z);
        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
