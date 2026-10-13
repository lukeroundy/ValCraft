using System;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// Valheim side of MC right-click -> Valheim interact. Every 0.1 s: ray from the (MC-driven) Valheim camera, nearest
    /// non-player hit within InteractRange; if it (or a parent) is Hoverable/Interactable, its hover text goes to MC (drawn
    /// under MC's crosshair). EvInteract from MC -> Player.Interact(go, hold:false, alt:false), the same call Valheim's E
    /// key makes (doors, chests, beds, stations, item pickup), falling back to Interactable.Interact(player, false, false).
    /// </summary>
    internal static class InteractBridge
    {
        internal static float Range = 4.5f;
        /// <summary>0.9.7: config [Interact] MaxDistance (0 = MC reach); MC reach from EvReach (f32 block, f32 entity).</summary>
        internal static float MaxDistanceCfg = 0f, McBlockReach = 4.5f, McEntityReach = 3f;
        static float _origPlace = -1f; static Component _placeFor; static FieldInfo _maxPlace;
        internal static void SetReach(byte[] p)
        {
            if (p == null || p.Length < 8) return;
            float b = BitConverter.ToSingle(p, 0), e = BitConverter.ToSingle(p, 4);
            if (b > 0.5f && b < 64f && Math.Abs(b - McBlockReach) > 0.01f) { McBlockReach = b; ValCraftPlugin.Log.LogInfo($"[interact] MC block reach {b:F1} m, entity reach {e:F1} m"); }
            if (e > 0.5f && e < 64f) McEntityReach = e;
            ApplyReach();
        }
        internal static void ApplyReach()
        {
            Range = MaxDistanceCfg > 0 ? MaxDistanceCfg : McBlockReach;
            ValheimHit.Range = MaxDistanceCfg > 0 ? Math.Min(MaxDistanceCfg, McEntityReach) : McEntityReach;
        }
        /// <summary>Valheim build placement distance capped at the interact reach (restored values per player kept).</summary>
        static void CapPlace(Component player)
        {
            if (_maxPlace == null) _maxPlace = AccessTools.Field(Refl.T("Player"), "m_maxPlaceDistance");
            if (_maxPlace == null || player == null) return;
            if (!ReferenceEquals(player, _placeFor)) { _placeFor = player; _origPlace = (float)_maxPlace.GetValue(player); }
            float want = Math.Min(_origPlace, Range);
            if (Math.Abs((float)_maxPlace.GetValue(player) - want) > 0.01f) _maxPlace.SetValue(player, want);
        }
        static Type _hoverable, _interactable;
        static MethodInfo _getHoverText, _interactI, _playerInteract;
        static bool _init;
        static float _next;
        static string _sent = "";
        static GameObject _target;
        static int _mask; static Type _effectArea;

        static void Init()
        {
            _effectArea = Refl.T("EffectArea");
            _init = true;
            _hoverable = Refl.T("Hoverable");
            _interactable = Refl.T("Interactable");
            _getHoverText = _hoverable == null ? null : AccessTools.Method(_hoverable, "GetHoverText");
            _interactI = _interactable == null ? null : AccessTools.Method(_interactable, "Interact");
            var playerT = Refl.T("Player");
            _playerInteract = playerT == null ? null : AccessTools.Method(playerT, "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) });
            _mask = ~LayerMask.GetMask("character", "character_net", "character_ghost", "character_noenv", "UI", "Water", "smoke", "viewblock", "skybox", "terrain_nocollide" /* may not exist */);
            ValCraftPlugin.Log.LogInfo($"[interact] Hoverable={_hoverable != null}, Interactable={_interactable != null}, GetHoverText={_getHoverText != null}, Player.Interact(go,hold,alt)={_playerInteract != null}");
        }

        public static void Update(ValCraftLink link, bool active)
        {
            if (!_init) Init();
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.1f;
            string text = "";
            _target = null;
            var player = PlayerSync.Player;
            var cam = Camera.main;
            if (player != null) { try { CapPlace(player); } catch { } }
            if (active && player != null && cam != null && _hoverable != null)
            {
                var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, Range + 2f, _mask, QueryTriggerInteraction.Collide);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var h in hits)
                {
                    if (h.collider.transform.IsChildOf(player.transform)) continue;
                    // 0.9.7: trigger volumes (fire warmth/EffectArea spheres, smoke) are not the object: they made fires reachable from metres away
                    if (h.collider.isTrigger && (_effectArea == null || h.collider.GetComponent(_effectArea) != null || h.collider.GetComponentInParent(_hoverable) == null || h.collider.bounds.extents.magnitude > 1.5f)) continue;
                    // reach is measured from the player's eye, like Valheim (camera may be pulled back in 3rd person)
                    if (Vector3.Distance(h.point, player.transform.position + Vector3.up * 1.6f) > Range) break;
                    var hov = h.collider.GetComponentInParent(_hoverable);
                    if (hov == null) { if (!h.collider.isTrigger) break; else continue; } // solid non-interactable blocks the ray
                    _target = (hov as Component).gameObject;
                    try { text = Clean(_getHoverText.Invoke(hov, null) as string); } catch { text = ""; }
                    // 0.11.0: no raw object/component names (broken MineRock5 showed "___MineRock5 m_meshFilter")
                    if (!Proper(text, _target)) text = "";
                    break;
                }
            }
            // 0.14.2: Hugin/Munin are not hit by the ray (character layer / large trigger): pick by the view ray
            if (_target == null && active && player != null && cam != null)
            {
                try
                {
                    var eye = player.transform.position + Vector3.up * 1.6f;
                    var rv = RavenBridge.Pick(cam.transform.position, cam.transform.forward, Range + Vector3.Distance(cam.transform.position, eye));
                    if (rv != null)
                    {
                        _target = rv.gameObject;
                        try { text = Clean(_getHoverText.Invoke(rv.GetComponentInParent(_hoverable), null) as string); } catch { text = ""; }
                        if (!Proper(text, _target)) text = "Hugin\n[Right-click] Talk";
                    }
                }
                catch { }
            }
            if (text != _sent)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                if (bytes.Length > ValCraftLink.SlotPayload) Array.Resize(ref bytes, ValCraftLink.SlotPayload);
                if (link.TrySend(ValCraftLink.EvHoverText, bytes)) _sent = text;
            }
        }

        public static void Interact()
        {
            var player = PlayerSync.Player;
            var go = _target;
            if (player == null || go == null) return;
            // 0.12.4: per-stage timing; slow stages (> 5 ms) are logged
            var sw = System.Diagnostics.Stopwatch.StartNew(); long last = 0;
            void Stage(string what) { long now = sw.ElapsedTicks; double ms = (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; last = now; if (ms > 5) ValCraftPlugin.Log.LogWarning($"[interact] slow stage '{what}' on {go.name}: {ms:F1} ms"); }
            bool done = ItemBridge.TryUseHeld(go); Stage("held item on station"); if (done) return; // 0.8.0: holding a mapped MC item on a Valheim station
            done = ItemBridge.TryOpenChest(go); Stage("chest"); if (done) return; // 0.10.0: chests open as a Minecraft screen
            done = RavenBridge.TryInteract(go, player); Stage("raven"); if (done) return; // 0.14.2: Hugin text in MC
            done = StoneBridge.TryInteract(go, player); Stage("lore stone"); if (done) return; // 0.11.0: vegvisir / rune stone text in MC
            try
            {
                if (_playerInteract != null) { _playerInteract.Invoke(player, new object[] { go, false, false }); Stage("Player.Interact"); ValCraftPlugin.Log.LogInfo($"[interact] {go.name}"); return; }
                var it = _interactable == null ? null : go.GetComponentInParent(_interactable);
                if (it != null && _interactI != null) { _interactI.Invoke(it, new object[] { player, false, false }); ValCraftPlugin.Log.LogInfo($"[interact] {go.name} (Interactable)"); }
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[interact] {go.name}: {e.InnerException?.Message ?? e.Message}"); }
        }

        /// <summary>Hover text worth showing: localized, not an unlocalized $token or a GameObject/component name.</summary>
        static bool Proper(string t, GameObject go)
        {
            if (string.IsNullOrEmpty(t)) return false;
            if (t.Contains("$") || t.StartsWith("_") || t.Contains("(Clone)") || t.Contains("m_")) return false;
            if (go != null && (t == go.name || t == go.name.Replace("(Clone)", "").Trim())) return false;
            return true;
        }

        static readonly Regex Tags = new Regex("<[^>]+>");
        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Tags.Replace(s, "");
            s = Regex.Replace(s, @"\[\s*E\s*\]", "[Right-click]");
            return s.Trim();
        }
    }
}
