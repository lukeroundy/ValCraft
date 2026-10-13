using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// 0.15.2: hit collider of an MC-mob stand-in = the MC bounding box (width x height incl. baby/pose/scale, from
    /// EvMobList, every 0.2 s), body-centred. The prefab's own colliders are reduced to one small movement capsule (also
    /// body-centred) so they neither steal hits nor block the player; the big "ValCraftHitbox" child capsule on the
    /// character layer takes melee sweeps and projectiles and ignores the local player's collider.
    internal static class ProxyHitbox
    {
        internal static bool Draw;
        internal sealed class State { public CapsuleCollider Hit, Move; public LineRenderer Dbg; public float W = -1, H = -1, PrefabScale = 1, LastH = -1, LastR = -1; public bool Logged, LastRender, LoggedRender; public MobCodec.Box Box; public string Tag = ""; }
        static readonly Dictionary<GameObject, State> _st = new Dictionary<GameObject, State>();
        static Material _mat;

        internal static void Setup(GameObject go, float w, float h)
        {
            if (go == null) return;
            var s = new State();
            // 0.15.3: the placeholder prefab's root may be scaled (collider sizes are LOCAL): neutralise it, renderers are hidden anyway
            s.PrefabScale = go.transform.localScale.y;
            go.transform.localScale = Vector3.one;
            var root = go.GetComponent<CapsuleCollider>();
            // every other collider of the placeholder prefab (child hit/ragdoll/attack colliders) goes away
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
                if (c != root) { c.enabled = false; Object.Destroy(c); }
            s.Move = root;
            var child = new GameObject("ValCraftHitbox"); child.layer = go.layer;
            child.transform.SetParent(go.transform, false);
            s.Hit = child.AddComponent<CapsuleCollider>(); s.Hit.direction = 1;
            _st[go] = s;
            Fit(go, null, w, h, "");
            IgnorePlayer(go);
        }

        internal static void Forget(GameObject go) { if (go != null) _st.Remove(go); }

        internal static void Resize(GameObject go, float w, float h) { if (go != null && _st.TryGetValue(go, out var s)) { s.W = w; s.H = h; } }

        /// 0.15.3: fit both capsules to the rendered MC mesh (world Renderer.bounds), MC bbox only as fallback. Called every 0.2 s.
        internal static void Fit(GameObject go, Renderer rendered, float w, float h, string tag)
        {
            if (go == null || !_st.TryGetValue(go, out var s)) return;
            if (w > 0) s.W = w; if (h > 0) s.H = h; if (!string.IsNullOrEmpty(tag)) s.Tag = tag;
            bool has = rendered != null && rendered.enabled && rendered.gameObject.activeInHierarchy;
            var bb = has ? rendered.bounds : default(Bounds);
            var b = MobCodec.Fit(has, bb.min.y, bb.max.y, bb.size.x, bb.size.z, go.transform.position.y, s.W, s.H, go.transform.lossyScale.y);
            bool changed = s.LastR < 0 || Mathf.Abs(b.Height - s.LastH) > 0.03f * Mathf.Max(0.5f, s.LastH) || Mathf.Abs(b.Radius - s.LastR) > 0.03f * Mathf.Max(0.2f, s.LastR) || b.FromRender != s.LastRender;
            if (!changed) return;
            s.LastH = b.Height; s.LastR = b.Radius; s.LastRender = b.FromRender; s.Box = b;
            s.Hit.radius = b.Radius; s.Hit.height = b.Height; s.Hit.center = new Vector3(0, b.CenterY, 0);
            if (s.Move != null) { s.Move.radius = b.MoveRadius; s.Move.height = b.MoveHeight; s.Move.center = new Vector3(0, b.MoveCenterY, 0); }
            UpdateDebug(s, b);
            {
                string line = $"[mobs] proxy {s.Tag}: {(b.FromRender ? $"rendered mesh {bb.size.x:F2}x{bb.size.y:F2}x{bb.size.z:F2} m (top {bb.max.y - go.transform.position.y:F2} above feet)" : "no mesh yet -> MC bbox")}, MC bbox {s.W:F2}x{s.H:F2}, prefab scale was {s.PrefabScale:F2} -> " +
                    $"hit capsule r {b.Radius:F2} h {b.Height:F2} c {b.CenterY:F2}, m_collider r {b.MoveRadius:F2} h {b.MoveHeight:F2}, layer {LayerMask.LayerToName(go.layer)}; {CharInfo(go)}";
                if (!s.Logged || b.FromRender != s.LoggedRender) { s.Logged = true; s.LoggedRender = b.FromRender; ValCraftPlugin.Log.LogInfo(line); } else Diag.Info("hitboxfit", line);
            }
        }

        static MethodInfo _top, _center, _height; static FieldInfo _colF; static bool _ci;
        static string CharInfo(GameObject go)
        {
            try
            {
                var ct = Refl.T("Character"); var ch = ct == null ? null : go.GetComponent(ct); if (ch == null) return "no Character";
                if (!_ci) { _ci = true; _top = AccessTools.Method(ct, "GetTopPoint"); _center = AccessTools.Method(ct, "GetCenterPoint"); _height = AccessTools.Method(ct, "GetHeight"); _colF = AccessTools.Field(ct, "m_collider"); }
                var col = _colF?.GetValue(ch) as Collider; float fy = go.transform.position.y;
                string t = _top != null ? (((Vector3)_top.Invoke(ch, null)).y - fy).ToString("F2") : "?", c = _center != null ? (((Vector3)_center.Invoke(ch, null)).y - fy).ToString("F2") : "?", h = _height != null ? ((float)_height.Invoke(ch, null)).ToString("F2") : "?";
                return $"Character m_collider={(col == null ? "null" : ReferenceEquals(col, go.GetComponent<CapsuleCollider>()) ? "root capsule" : col.name)} GetTopPoint +{t} GetCenterPoint +{c} GetHeight {h}";
            }
            catch (System.Exception e) { return "Character info failed: " + e.GetBaseException().Message; }
        }

        /// The local player's own colliders never collide with the (big) hit capsule, so it can't push or trap you.
        internal static void IgnorePlayer(GameObject go)
        {
            var pl = PlayerSync.LocalPlayerNow();
            if (pl == null || !_st.TryGetValue(go, out var s) || s.Hit == null) return;
            foreach (var c in pl.GetComponentsInChildren<Collider>(true)) if (c != null) Physics.IgnoreCollision(s.Hit, c, true);
        }
        static float _nextIgnore;
        internal static void Tick()
        {
            if (Time.time < _nextIgnore) return; _nextIgnore = Time.time + 2f;
            foreach (var go in new List<GameObject>(_st.Keys)) { if (go == null) { _st.Remove(go); continue; } IgnorePlayer(go); var s = _st[go]; if ((s.Dbg != null) != Draw) UpdateDebug(s, s.Box); }
        }

        static void UpdateDebug(State s, MobCodec.Box b)
        {
            if (!Draw) { if (s.Dbg != null) { Object.Destroy(s.Dbg); s.Dbg = null; } return; }
            if (s.Dbg == null)
            {
                if (_mat == null) { var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color"); _mat = sh != null ? new Material(sh) : null; }
                s.Dbg = s.Hit.gameObject.AddComponent<LineRenderer>();
                s.Dbg.useWorldSpace = false; s.Dbg.widthMultiplier = 0.03f; s.Dbg.loop = false; if (_mat != null) s.Dbg.material = _mat;
                s.Dbg.startColor = s.Dbg.endColor = new Color(1f, 0.25f, 0.2f, 1f);
            }
            // rings at bottom / middle / top of the cylinder part + 4 verticals
            var pts = new List<Vector3>(); float r = b.Radius, y0 = Mathf.Max(r, 0.001f), y1 = Mathf.Max(y0, b.Height - r);
            void Ring(float y) { for (int i = 0; i <= 24; i++) { float a = i / 24f * Mathf.PI * 2; pts.Add(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r)); } }
            Ring(0.02f); Ring(y0); Ring((y0 + y1) / 2); Ring(y1); Ring(b.Height);
            foreach (var a in new[] { 0f, 90f, 180f, 270f }) { float rad = a * Mathf.Deg2Rad; var o = new Vector3(Mathf.Cos(rad) * r, 0, Mathf.Sin(rad) * r); pts.Add(o + Vector3.up * 0.02f); pts.Add(o + Vector3.up * b.Height); pts.Add(o + Vector3.up * 0.02f); }
            s.Dbg.positionCount = pts.Count; s.Dbg.SetPositions(pts.ToArray());
        }
    }
}
