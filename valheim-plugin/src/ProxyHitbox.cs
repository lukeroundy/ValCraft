using System.Collections.Generic;
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
        internal sealed class State { public CapsuleCollider Hit, Move; public LineRenderer Dbg; public float W = -1, H = -1; }
        static readonly Dictionary<GameObject, State> _st = new Dictionary<GameObject, State>();
        static Material _mat;

        internal static void Setup(GameObject go, float w, float h)
        {
            if (go == null) return;
            var s = new State();
            var root = go.GetComponent<CapsuleCollider>();
            // every other collider of the placeholder prefab (child hit/ragdoll/attack colliders) goes away
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
                if (c != root) { c.enabled = false; Object.Destroy(c); }
            s.Move = root;
            var child = new GameObject("ValCraftHitbox"); child.layer = go.layer;
            child.transform.SetParent(go.transform, false);
            s.Hit = child.AddComponent<CapsuleCollider>(); s.Hit.direction = 1;
            _st[go] = s;
            Resize(go, w, h);
            IgnorePlayer(go);
        }

        internal static void Forget(GameObject go) { if (go != null) _st.Remove(go); }

        internal static void Resize(GameObject go, float w, float h)
        {
            if (go == null || !_st.TryGetValue(go, out var s)) return;
            if (Mathf.Abs(w - s.W) < 0.01f && Mathf.Abs(h - s.H) < 0.01f) return;
            s.W = w; s.H = h;
            var b = MobCodec.Hitbox(w, h);
            s.Hit.radius = b.Radius; s.Hit.height = b.Height; s.Hit.center = new Vector3(0, b.CenterY, 0);
            if (s.Move != null) { s.Move.radius = b.MoveRadius; s.Move.height = b.MoveHeight; s.Move.center = new Vector3(0, b.MoveCenterY, 0); }
            UpdateDebug(s, b);
            Diag.Info("hitbox", $"[mobs] proxy hitbox {w:F2}x{h:F2} blocks -> capsule r {b.Radius:F2} h {b.Height:F2} centre {b.CenterY:F2} (move r {b.MoveRadius:F2} h {b.MoveHeight:F2})");
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
            foreach (var go in new List<GameObject>(_st.Keys)) { if (go == null) { _st.Remove(go); continue; } IgnorePlayer(go); var s = _st[go]; if ((s.Dbg != null) != Draw) UpdateDebug(s, MobCodec.Hitbox(s.W, s.H)); }
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
