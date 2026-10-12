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
        internal static float Range = 3.5f;
        static Type _hoverable, _interactable;
        static MethodInfo _getHoverText, _interactI, _playerInteract;
        static bool _init;
        static float _next;
        static string _sent = "";
        static GameObject _target;
        static int _mask;

        static void Init()
        {
            _init = true;
            _hoverable = AccessTools.TypeByName("Hoverable");
            _interactable = AccessTools.TypeByName("Interactable");
            _getHoverText = _hoverable == null ? null : AccessTools.Method(_hoverable, "GetHoverText");
            _interactI = _interactable == null ? null : AccessTools.Method(_interactable, "Interact");
            var playerT = AccessTools.TypeByName("Player");
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
            if (active && player != null && cam != null && _hoverable != null)
            {
                var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, Range + 2f, _mask, QueryTriggerInteraction.Collide);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var h in hits)
                {
                    if (h.collider.transform.IsChildOf(player.transform)) continue;
                    // reach is measured from the player's eye, like Valheim (camera may be pulled back in 3rd person)
                    if (Vector3.Distance(h.point, player.transform.position + Vector3.up * 1.6f) > Range) break;
                    var hov = h.collider.GetComponentInParent(_hoverable);
                    if (hov == null) { if (!h.collider.isTrigger) break; else continue; } // solid non-interactable blocks the ray
                    _target = (hov as Component).gameObject;
                    try { text = Clean(_getHoverText.Invoke(hov, null) as string); } catch { text = ""; }
                    if (string.IsNullOrEmpty(text)) text = _target.name;
                    break;
                }
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
            try
            {
                if (_playerInteract != null) { _playerInteract.Invoke(player, new object[] { go, false, false }); ValCraftPlugin.Log.LogInfo($"[interact] {go.name}"); return; }
                var it = _interactable == null ? null : go.GetComponentInParent(_interactable);
                if (it != null && _interactI != null) { _interactI.Invoke(it, new object[] { player, false, false }); ValCraftPlugin.Log.LogInfo($"[interact] {go.name} (Interactable)"); }
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[interact] {go.name}: {e.InnerException?.Message ?? e.Message}"); }
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
