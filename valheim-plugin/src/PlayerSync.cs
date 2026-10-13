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
        static double _camFeetY; static bool _haveCamFeet; static float _camYOff;
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
        internal static bool HaveState => _haveState;
        internal static PlayerState State => _state;
        internal static bool HasLocalPlayer => LocalPlayer() != null;
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
            var playerT = Refl.T("Player");
            var charT = Refl.T("Character");
            var gcT = Refl.T("GameCamera");
            var hudT = Refl.T("Hud");
            var zoneT = Refl.T("ZoneSystem");
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

        internal static float? FixedAnchorY;
        static float _teleportX, _teleportY, _teleportZ, _teleportUntil, _nextTeleportSend;
        static Vector3 _holdPos;
        internal static bool TeleportPending => _teleportUntil > 0;
        static void SendTeleport(ValCraftLink link)
        {
            if (!TeleportPending || Time.unscaledTime < _nextTeleportSend) return;
            _nextTeleportSend = Time.unscaledTime + 1f; // repeat until MC reports the new height
            link.TrySend(ValCraftLink.EvTeleport, Payload.F32(_teleportX, _teleportY, _teleportZ));
        }
        internal static float NearClip = 0.05f, HitboxWidth = 0f, HitboxHeight = 0f, HitboxScale = 1f;
        static float _wantFalseSince = -1f;
        static Component _anchorPlayer;
        static float _sentW = -1, _sentH = -1, _nextDims;
        static Camera _clipCam; static float _clipOrig;

        static void SendDims(ValCraftLink link)
        {
            if (Time.unscaledTime < _nextDims) return;
            _nextDims = Time.unscaledTime + 1f;
            float w = HitboxWidth, h = HitboxHeight;
            if ((w <= 0 || h <= 0) && _player != null)
            {
                var cap = _player.GetComponent<CapsuleCollider>();
                if (cap != null)
                {
                    var sc = _player.transform.lossyScale;
                    if (w <= 0) w = 2f * cap.radius * Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.z));
                    if (h <= 0) h = cap.height * Mathf.Abs(sc.y);
                }
            }
            if (w <= 0) w = 0.6f; if (h <= 0) h = 1.8f;
            w *= HitboxScale; h *= HitboxScale;
            if (Mathf.Abs(w - _sentW) < 0.001f && Mathf.Abs(h - _sentH) < 0.001f) return;
            if (link.TrySend(ValCraftLink.EvPlayerDims, Payload.F32(w, h)))
            {
                _sentW = w; _sentH = h;
                ValCraftPlugin.Log.LogInfo($"[sync] MC player hitbox -> {w:F2} x {h:F2} m (eye {0.9f * h:F2}){(HitboxWidth > 0 || HitboxHeight > 0 ? " (config)" : " (Valheim capsule)")}");
            }
        }
        internal static void ResendDims() { _sentW = _sentH = -1; _nextDims = 0; }

        internal static Component LocalPlayerNow() => LocalPlayer();
        static Component LocalPlayer() { try { return _localPlayerField?.GetValue(null) as Component; } catch { return null; } }

        /// <summary>Called from the plugin's Update every frame.</summary>
        public static void Update(ValCraftLink link, bool connected)
        {
            // 0.4.0 log: one failed/odd read stopped following and re-anchored (≈2 m jump). Keep the last good state on
            // a failed seqlock read, and only stop after the conditions have been false for a full second.
            if (!connected) _haveState = false;
            else if (link.TryReadPlayerState(out var fresh)) { _state = fresh; _haveState = (fresh.Flags & ValCraftLink.PsHasPlayer) != 0; }
            var player = LocalPlayer();
            // 0.8.1: Valheim destroys the local player on death/respawn; drop the dead reference at once (Unity null)
            // instead of driving a destroyed object for a second, and pick up the respawned player when it appears.
            if (Following && (_player == null || player == null || !ReferenceEquals(player, _player))) { Stop(player == null ? "local player destroyed (death/respawn)" : "local player changed (respawn)"); _wantFalseSince = -1f; }
            bool want = Enabled && _haveState && player != null && (link.PeerFlags & ValCraftLink.McFlagWorldLoaded) != 0;
            if (want) _wantFalseSince = -1f;
            else if (_wantFalseSince < 0) _wantFalseSince = Time.unscaledTime;
            if (want && !Following) Start(player);
            else if (!want && Following && (!connected || Time.unscaledTime - _wantFalseSince > 1f)) Stop("follow conditions ended");
            if (Following && connected) { SendDims(link); SendTeleport(link); }
            else if (Following && player != _player) { Stop("local player changed"); if (want) Start(player); }
            if (!Following) return;

            ComputeTarget();
            if (HidePlayer && Time.unscaledTime >= _nextHideScan) { HideRenderers(); _nextHideScan = Time.unscaledTime + 1f; }
            if (HideHud) SetHud(HudWanted != null && HudWanted()); // 0.8.4: shown while the Valheim build menu is open
            MovePlayer(); // also done in FixedUpdate postfix; moving here keeps the transform in step with rendering
        }

        static bool _alignPending; static Component _deadPlayer;
        internal static bool AlignPending => _alignPending;
        /// <summary>HealthLink: the Valheim player is being killed for an MC death; align the respawn instead of re-anchoring.</summary>
        internal static void ExpectRespawn() { if (_anchored) { _alignPending = true; _deadPlayer = _player; _alignSince = Time.unscaledTime; } }
        static float _alignSince;
        /// <summary>/valcraft offset: move the horizontal mapping (MC builds move in Valheim; MC blocks are not touched), MC player follows.</summary>
        internal static void SetOffset(float x, float z, bool relative)
        {
            var uid = WorldInfo.Uid(); if (!uid.HasValue) return;
            var cur = AnchorStore.Get(uid.Value); var nv = relative ? new Vector2(cur.x + x, cur.y + z) : new Vector2(x, z);
            AnchorStore.Set(uid.Value, nv);
            ValCraftPlugin.Log.LogInfo($"[sync] mapping offset for world {uid} set to ({nv.x:F2}, {nv.y:F2}) (was {cur.x:F2}, {cur.y:F2}); re-anchoring");
            ReAnchor();
        }
        public static void ReAnchor() { _anchored = false; _haveY = false; ValCraftPlugin.Log.LogInfo("[sync] re-anchoring at the current Valheim position"); }

        static void Start(Component player)
        {
            _player = player;
            _body = _bodyField?.GetValue(player) as Rigidbody ?? player.GetComponent<Rigidbody>();
            if (_body != null) { _bodyWasKinematic = _body.isKinematic; _bodyWasGravity = _body.useGravity; _body.useGravity = false; _body.isKinematic = true; }
            if (_anchorPlayer != player)
            {
                // 0.13.2: after a death keep the mapping (MC builds stay where they are in Valheim) and move the MC player to where
                // Valheim respawned us (bed / spawn point) instead of re-anchoring Valheim's respawn point onto MC's.
                if (_alignPending && _anchored) ValCraftPlugin.Log.LogInfo("[sync] respawned Valheim player: keeping the anchor");
                else { _anchored = false; _alignPending = false; }
                _anchorPlayer = player;
            }
            _haveY = false;
            ResendDims();
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
            if (_clipCam != null) { _clipCam.nearClipPlane = _clipOrig; _clipCam = null; }
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
                bool mp = NetTunnel.Multiplayer;
                if (mp) { _anchor.x = 0; _anchor.z = 0; } // 0.13.0: everyone in one MC world -> one shared mapping (MC x = Valheim x, MC z = -Valheim z)
                // 0.13.5: the horizontal mapping is persisted per Valheim world and authoritative (it used to be recomputed from the two
                // saved player positions at every link, so any divergence between them shifted every MC build in Valheim).
                bool stored = false; var uid = WorldInfo.Uid();
                if (!mp && uid.HasValue) { var off = AnchorStore.Get(uid.Value); _anchor.x = off.x; _anchor.z = off.y; stored = true; }
                if (FixedAnchorY.HasValue || mp || stored)
                {
                    // Terrain blocks: MC Y = Valheim Y - anchor.y must be a fixed mapping so the whole Valheim height range
                    // fits MC's -64..320; move the MC player to the matching height instead of shifting the anchor.
                    if (FixedAnchorY.HasValue) _anchor.y = FixedAnchorY.Value;
                    _teleportY = p.y - _anchor.y;
                    _teleportX = p.x - _anchor.x; _teleportZ = -(p.z - _anchor.z);
                    _teleportUntil = Time.unscaledTime + 8f;
                    _holdPos = p;
                }
                _lastGroundFeetY = s.FeetY;
                _anchored = true;
                ValCraftPlugin.Log.LogInfo($"[sync] anchor {_anchor} (Valheim {p} = MC feet {s.FeetX:F2},{s.FeetY:F2},{s.FeetZ:F2})");
            }
            if (_alignPending)
            {
                var p = _player.transform.position;
                bool same = ReferenceEquals(_player, _deadPlayer);
                if (HealthLink.McDead || (same && !HealthLink.KillFailed && Time.unscaledTime - _alignSince < 30f)) { _targetPos = p; _targetRot = _player.transform.rotation; return; } // wait for both respawns
                // 0.13.4 log: the Valheim kill failed, so the same player was held at the death spot forever while MC walked on -> the two
                // saved positions diverged. Now: Valheim never died -> just align MC to it.
                _alignPending = false; _deadPlayer = null;
                _teleportX = p.x - _anchor.x; _teleportY = p.y - _anchor.y; _teleportZ = -(p.z - _anchor.z);
                _teleportUntil = Time.unscaledTime + 10f; _holdPos = p; _lastGroundFeetY = _teleportY; _haveY = false; _nextTeleportSend = 0;
                ValCraftPlugin.Log.LogInfo($"[sync] respawn: MC player -> Valheim respawn point {p} (MC {_teleportX:F1} {_teleportY:F1} {_teleportZ:F1}), anchor kept");
            }
            if (TeleportPending)
            {
                if ((Math.Abs(s.FeetY - _teleportY) < 3.0 && Math.Abs(s.FeetX - _teleportX) < 3.0 && Math.Abs(s.FeetZ - _teleportZ) < 3.0) || Time.unscaledTime > _teleportUntil) { _teleportUntil = 0; ValCraftPlugin.Log.LogInfo($"[sync] MC player at Valheim height (MC feet Y {s.FeetY:F1})"); }
                else { _targetPos = _holdPos; _targetRot = _player.transform.rotation; return; }
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
            if (!Following || !_haveState || TeleportPending) return;
            Camera cam = null;
            try { cam = _gcCameraField?.GetValue(__instance) as Camera; } catch { }
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            var s = _state;
            CoordMap.ToUnity(s.X, s.Y, s.Z, out float ex, out float ey, out float ez);
            var eye = new Vector3(_anchor.x + ex, _anchor.y + ey, _anchor.z + ez);
            if (Vertical == VerticalMode.GroundSnap) eye.y = _targetPos.y + (float)(s.Y - s.FeetY); // same vertical rule as the body
            else
            {
                // Smooth small vertical steps (stairs, box edges): absorb the jump into an offset that decays quickly.
                float dyFeet = _haveCamFeet ? (float)(s.FeetY - _camFeetY) : 0f;
                _camFeetY = s.FeetY; _haveCamFeet = true;
                if ((s.Flags & ValCraftLink.PsOnGround) != 0 && Mathf.Abs(dyFeet) > 0.3f && Mathf.Abs(dyFeet) <= 1.1f) _camYOff -= dyFeet;
                _camYOff = Mathf.Clamp(_camYOff * Mathf.Exp(-12f * Time.deltaTime), -1.1f, 1.1f);
                if (Mathf.Abs(_camYOff) < 0.002f) _camYOff = 0f;
                eye.y += _camYOff;
            }
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
            // Near plane well inside the hitbox radius so walls you touch aren't clipped away (see-through).
            if (_clipCam != cam) { if (_clipCam != null) _clipCam.nearClipPlane = _clipOrig; _clipCam = cam; _clipOrig = cam.nearClipPlane; ValCraftPlugin.Log.LogInfo($"[sync] camera near clip {_clipOrig:F3} -> {NearClip:F3}"); }
            cam.nearClipPlane = NearClip;
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

        /// <summary>0.8.4: when true, the hidden-HUD mode shows the Valheim HUD anyway (build menu lives under it).</summary>
        internal static Func<bool> HudWanted;
        internal static void SetHud(bool visible)
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
