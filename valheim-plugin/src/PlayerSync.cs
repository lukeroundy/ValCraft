using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    public enum VerticalMode { GroundSnap, FollowMinecraft }

    /// <summary>
    /// Milestone 3: the Valheim camera and local Player follow Minecraft's camera/player every frame.
    /// - Position mapping: unity = anchor + (mcX, mcY, -mcZ). The anchor is set when following starts (or F9) so the
    ///   player doesn't jump: anchor = valheimPlayerPos - map(mcFeet).
    /// - Vertical (interim until Valheim collision reaches MC in milestone 5):
    ///   GroundSnap (default): Valheim Y = Valheim ground under the player + how far MC's feet are above the last height
    ///   MC stood on (so jumps/falls show, MC's own terrain shape doesn't matter). FollowMinecraft: anchor.y + mcFeetY.
    /// - The local Player is moved kinematically (Rigidbody.isKinematic, Character.UpdateMotion skipped) and stays a real,
    ///   networked, damageable Character; its renderers can be hidden (HideValheimPlayer).
    /// Valheim types are resolved by name (AccessTools); anything missing is logged once and that part is skipped.
    /// </summary>
    internal static class PlayerSync
    {
        // config (set by plugin)
        internal static bool Enabled = true, HidePlayer = true, HideHud = true, ViewBob = true;
        internal static VerticalMode Vertical = VerticalMode.FollowMinecraft;

        internal static bool Following { get; private set; }
        static PlayerState _state;
        static bool _haveState;
        static Vector3 _anchor;
        static bool _anchored;
        static double _lastGroundFeetY;
        static float _smoothedY;
        static bool _haveY;
        static Vector3 _targetPos;
        static Quaternion _targetRot;

        // reflection
        static FieldInfo _localPlayerField, _bodyField, _gcInstanceField, _gcCameraField, _hudInstanceField, _hudRootField;
        static Func<object> _zoneInstance;

        internal static Func<object> FindSingleton(Type t)
        {
            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var n in new[] { "m_instance", "s_instance", "instance", "Instance", "_instance" })
            {
                var f = t.GetField(n, F);
                if (f != null) return () => f.GetValue(null);
                var p = t.GetProperty(n, F);
                if (p != null && p.GetGetMethod(true) != null) return () => p.GetValue(null, null);
            }
            return null;
        }
        internal static Vector3 Anchor => _anchor;
        internal static bool Anchored => Following && _anchored;
        internal static Component Player => _player;
        static MethodInfo _groundHeight;
        static Component _player;          // Player (MonoBehaviour)
        static Rigidbody _body;
        static bool _bodyWasKinematic, _bodyWasGravity;
        static readonly List<Renderer> _hidden = new List<Renderer>();
        static float _nextHideScan;
        static GameObject _hudRoot;
        static int _groundMask;

        public static void Apply(Harmony h)
        {
            var playerT = AccessTools.TypeByName("Player");
            var charT = AccessTools.TypeByName("Character");
            var gcT = AccessTools.TypeByName("GameCamera");
            var hudT = AccessTools.TypeByName("Hud");
            var zoneT = AccessTools.TypeByName("ZoneSystem");
            _localPlayerField = playerT != null ? AccessTools.Field(playerT, "m_localPlayer") : null;
            _bodyField = charT != null ? AccessTools.Field(charT, "m_body") : null;
            _gcInstanceField = gcT != null ? AccessTools.Field(gcT, "m_instance") ?? AccessTools.Field(gcT, "instance") : null;
            _gcCameraField = gcT != null ? AccessTools.Field(gcT, "m_camera") : null;
            _hudInstanceField = hudT != null ? AccessTools.Field(hudT, "m_instance") : null;
            _hudRootField = hudT != null ? AccessTools.Field(hudT, "m_rootObject") : null;
            // 0.3.0 log: ZoneSystem has no m_instance field. Try the usual singleton spellings quietly.
            _zoneInstance = zoneT == null ? null : FindSingleton(zoneT);
            _groundHeight = zoneT != null ? AccessTools.Method(zoneT, "GetGroundHeight", new[] { typeof(Vector3) }) : null;
            ValCraftPlugin.Log.LogInfo($"[sync] reflection: Player.m_localPlayer={_localPlayerField != null}, Character.m_body={_bodyField != null}, " +
                $"GameCamera.instance={_gcInstanceField != null}, GameCamera.m_camera={_gcCameraField != null}, Hud.m_rootObject={_hudRootField != null && _hudInstanceField != null}, " +
                $"ZoneSystem.GetGroundHeight={_groundHeight != null && _zoneInstance != null}");

            int n = 0;
            // Camera last: after Valheim positioned it this frame.
            foreach (var name in new[] { "GameCamera:LateUpdate", "GameCamera:UpdateCamera" })
            {
                var m = AccessTools.Method(name);
                if (m == null) continue;
                try { h.Patch(m, postfix: new HarmonyMethod(typeof(PlayerSync), nameof(CameraPostfix)) { priority = Priority.Last }); n++; ValCraftPlugin.Log.LogInfo($"[sync] camera hook: {name}"); break; }
                catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[sync] {name}: {e.Message}"); }
            }
            n += Patch(h, "Character:UpdateMotion", nameof(SkipMotionForLocalPlayer), prefix: true);
            n += Patch(h, "Character:CustomFixedUpdate", nameof(PinBodyPostfix), prefix: false);
            ValCraftPlugin.Log.LogInfo($"[sync] {n} hooks patched");
            _groundMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
        }

        static int Patch(Harmony h, string target, string patch, bool prefix)
        {
            var m = AccessTools.Method(target);
            if (m == null) { ValCraftPlugin.Log.LogWarning($"[sync] {target} not found"); return 0; }
            try
            {
                var hm = new HarmonyMethod(typeof(PlayerSync), patch);
                if (prefix) h.Patch(m, prefix: hm); else h.Patch(m, postfix: hm);
                return 1;
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[sync] {target}: {e.Message}"); return 0; }
        }

        static Component LocalPlayer() { try { return _localPlayerField?.GetValue(null) as Component; } catch { return null; } }

        /// <summary>Called from the plugin's Update every frame.</summary>
        public static void Update(ValCraftLink link, bool connected)
        {
            _haveState = connected && link.TryReadPlayerState(out _state) && (_state.Flags & ValCraftLink.PsHasPlayer) != 0;
            var player = LocalPlayer();
            bool want = Enabled && _haveState && player != null && (link.PeerFlags & ValCraftLink.McFlagWorldLoaded) != 0;
            if (want && !Following) Start(player);
            else if (!want && Following) Stop("follow conditions ended");
            else if (Following && player != _player) { Stop("local player changed"); if (want) Start(player); }
            if (!Following) return;

            ComputeTarget();
            if (HidePlayer && Time.unscaledTime >= _nextHideScan) { HideRenderers(); _nextHideScan = Time.unscaledTime + 1f; }
            if (HideHud) SetHud(false);
            MovePlayer(); // also done in FixedUpdate postfix; moving here keeps the transform in step with rendering
        }

        public static void ReAnchor() { _anchored = false; _haveY = false; ValCraftPlugin.Log.LogInfo("[sync] re-anchoring at the current Valheim position"); }

        static void Start(Component player)
        {
            _player = player;
            _body = _bodyField?.GetValue(player) as Rigidbody ?? player.GetComponent<Rigidbody>();
            if (_body != null) { _bodyWasKinematic = _body.isKinematic; _bodyWasGravity = _body.useGravity; _body.useGravity = false; _body.isKinematic = true; }
            _anchored = false; _haveY = false;
            Following = true;
            ValCraftPlugin.Log.LogInfo($"[sync] following Minecraft (vertical={Vertical}, hidePlayer={HidePlayer}, hideHud={HideHud})");
        }

        public static void Stop(string why)
        {
            if (!Following) return;
            Following = false;
            if (_body != null) { _body.isKinematic = _bodyWasKinematic; _body.useGravity = _bodyWasGravity; }
            ShowRenderers();
            SetHud(true);
            _player = null; _body = null;
            ValCraftPlugin.Log.LogInfo($"[sync] stopped following: {why}");
        }

        static void ComputeTarget()
        {
            var s = _state;
            CoordMap.ToUnity(s.FeetX, s.FeetY, s.FeetZ, out float fx, out float fy, out float fz);
            if (!_anchored)
            {
                var p = _player.transform.position;
                _anchor = new Vector3(p.x - fx, p.y - fy, p.z - fz);
                _lastGroundFeetY = s.FeetY;
                _anchored = true;
                ValCraftPlugin.Log.LogInfo($"[sync] anchor {_anchor} (Valheim {p} = MC feet {s.FeetX:F2},{s.FeetY:F2},{s.FeetZ:F2})");
            }
            var pos = new Vector3(_anchor.x + fx, _anchor.y + fy, _anchor.z + fz);
            if (Vertical == VerticalMode.GroundSnap)
            {
                if ((s.Flags & ValCraftLink.PsOnGround) != 0) _lastGroundFeetY = s.FeetY;
                float above = (float)Math.Max(0.0, s.FeetY - _lastGroundFeetY);
                float ground = GroundHeight(pos);
                float y = ground + above;
                // Smooth steps down (stairs/slopes) a little; rise immediately so we never sink into terrain.
                _smoothedY = !_haveY || y >= _smoothedY ? y : Mathf.MoveTowards(_smoothedY, y, 12f * Time.deltaTime);
                _haveY = true;
                pos.y = _smoothedY;
            }
            _targetPos = pos;
            _targetRot = Quaternion.Euler(0f, CoordMap.YawToUnity(s.Yaw), 0f);
        }

        static float GroundHeight(Vector3 p)
        {
            var origin = new Vector3(p.x, p.y + 50f, p.z);
            var hits = Physics.RaycastAll(origin, Vector3.down, 200f, _groundMask, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            foreach (var hit in hits)
            {
                if (_player != null && hit.collider.transform.IsChildOf(_player.transform)) continue;
                // highest surface not more than 1.5 m above our current feet (so roofs above us don't count)
                if (hit.point.y > (_haveY ? _smoothedY : p.y) + 1.5f) continue;
                if (hit.point.y > best) best = hit.point.y;
            }
            if (!float.IsNegativeInfinity(best)) return best;
            try
            {
                var zs = _zoneInstance?.Invoke();
                if (zs != null && _groundHeight != null) return (float)_groundHeight.Invoke(zs, new object[] { p });
            }
            catch { }
            return _haveY ? _smoothedY : p.y;
        }

        static void MovePlayer()
        {
            if (_player == null) return;
            if (_body != null) { _body.position = _targetPos; _body.rotation = _targetRot; }
            _player.transform.SetPositionAndRotation(_targetPos, _targetRot);
        }

        // --- Harmony patches ---
        static bool SkipMotionForLocalPlayer(object __instance) => !(Following && ReferenceEquals(__instance, _player));

        static void PinBodyPostfix(object __instance)
        {
            if (Following && ReferenceEquals(__instance, _player)) MovePlayer();
        }

        static void CameraPostfix(object __instance)
        {
            if (!Following || !_haveState) return;
            Camera cam = null;
            try { cam = _gcCameraField?.GetValue(__instance) as Camera; } catch { }
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            var s = _state;
            CoordMap.ToUnity(s.X, s.Y, s.Z, out float ex, out float ey, out float ez);
            var eye = new Vector3(_anchor.x + ex, _anchor.y + ey, _anchor.z + ez);
            if (Vertical == VerticalMode.GroundSnap) eye.y = _targetPos.y + (float)(s.Y - s.FeetY); // same vertical rule as the body
            var rot = Quaternion.Euler(CoordMap.PitchToUnity(s.Pitch), CoordMap.YawToUnity(s.Yaw), 0f);
            if (ViewBob && (s.Flags & ValCraftLink.PsViewBob) != 0)
            {
                // MC applies bob to the world in view space; the camera gets the inverse (z mirrored for Unity). Sign checked by math only.
                eye += rot * new Vector3(-s.BobX, -s.BobY, 0f);
                rot = rot * Quaternion.Euler(s.BobPitchDeg, 0f, -s.BobRollDeg);
            }
            var t = cam.transform;
            // GameCamera may move a parent; set world-space pose on the camera itself.
            t.SetPositionAndRotation(eye, rot);
            if (s.FovDeg > 1f && s.FovDeg < 179f) cam.fieldOfView = s.FovDeg;
        }

        // --- visuals ---
        static void HideRenderers()
        {
            if (_player == null) return;
            foreach (var r in _player.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || _hidden.Contains(r)) continue;
                r.enabled = false; // also removes its shadow
                _hidden.Add(r);
            }
        }

        static void ShowRenderers()
        {
            foreach (var r in _hidden) if (r != null) r.enabled = true;
            _hidden.Clear();
        }

        static void SetHud(bool visible)
        {
            try
            {
                if (_hudRoot == null)
                {
                    var hud = _hudInstanceField?.GetValue(null);
                    if (hud != null) _hudRoot = _hudRootField?.GetValue(hud) as GameObject;
                }
                if (_hudRoot != null && _hudRoot.activeSelf != visible) _hudRoot.SetActive(visible);
            }
            catch { }
        }
    }
}
