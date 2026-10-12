using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ValCraft
{
    /// <summary>
    /// 0.11.2: main menu title. Valheim 1.0 shows its logo as one or more UI images (title + "Deep North" art, possibly
    /// animated or instantiated late, with ember particles). Every Image / RawImage / SpriteRenderer in loaded scenes whose
    /// sprite/texture/object path mentions logo/valheim/deepnorth is logged and hidden (re-enforced every frame, so the
    /// menu Animator cannot bring it back; matched on asset name and object/parent name); the largest one's RectTransform hosts our replacement: "VALCRAFT" in Valheim's
    /// Norse font with a fiery metal gradient, dark outline and glow (generated at runtime, no Valheim artwork copied).
    /// </summary>
    internal static class Logo
    {
        internal static bool Enabled = true;
        internal static bool ShowVersion = true;
        static Type _imageT, _rawT, _tmpT, _fontT, _graphicT;
        static float _menuSince = -1f, _nextScan;
        static bool _dumped;
        static readonly List<Component> Hidden = new List<Component>();
        static readonly HashSet<int> Seen = new HashSet<int>();
        static GameObject _ours;
        static readonly string[] Keys = { "logo", "valheim", "deepnorth", "deep_north", "deep north" };

        internal static void Tick(bool inGame)
        {
            if (!Enabled) return;
            if (inGame) { if (_menuSince >= 0) Reset(); return; }
            float now = Time.unscaledTime;
            if (_menuSince < 0) _menuSince = now;
            try { Enforce(); } catch { }
            // scan every 0.5 s for the first 30 s on the menu (late-instantiated logos), then every 5 s
            if (now < _nextScan) return;
            _nextScan = now + (now - _menuSince < 30f ? 0.5f : 5f);
            try { Scan(); } catch (Exception e) { ValCraftPlugin.Log.LogDebug("[logo] " + e); }
        }

        static void Reset() { _menuSince = -1f; Hidden.Clear(); Seen.Clear(); _ours = null; _dumped = false; }

        static void Enforce()
        {
            for (int i = Hidden.Count - 1; i >= 0; i--)
            {
                var c = Hidden[i];
                if (c == null) { Hidden.RemoveAt(i); continue; }
                if (c is Behaviour b) { if (b.enabled) b.enabled = false; }
                else if (c is Renderer r) { if (r.enabled) r.enabled = false; }
            }
        }

        static string Path(Transform t) { var sb = new StringBuilder(t.name); for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/"); return sb.ToString(); }
        static bool Match(string s) { s = s.ToLowerInvariant(); if (s.Contains("valcraft")) return false; foreach (var k in Keys) if (s.Contains(k)) return true; return false; }

        static void Scan()
        {
            _imageT = _imageT ?? AccessTools.TypeByName("UnityEngine.UI.Image");
            _rawT = _rawT ?? AccessTools.TypeByName("UnityEngine.UI.RawImage");
            _graphicT = _graphicT ?? AccessTools.TypeByName("UnityEngine.UI.Graphic");
            _tmpT = _tmpT ?? AccessTools.TypeByName("TMPro.TextMeshProUGUI");
            _fontT = _fontT ?? AccessTools.TypeByName("TMPro.TMP_FontAsset");
            var found = new List<(Component c, string desc, float area)>();
            var big = new List<string>();
            void Consider(Component c, string asset, string kind)
            {
                if (c == null || c.gameObject == _ours || (_ours != null && c.transform.IsChildOf(_ours.transform))) return;
                var sc = c.gameObject.scene; if (!sc.IsValid()) return; // prefabs / assets, not scene objects
                string path = Path(c.transform);
                var rt = c.transform as RectTransform;
                float w = rt != null ? rt.rect.width * Mathf.Abs(rt.lossyScale.x) : 0, h = rt != null ? rt.rect.height * Mathf.Abs(rt.lossyScale.y) : 0;
                string desc = $"{kind} '{path}' asset='{asset}' size={w:F0}x{h:F0} active={c.gameObject.activeInHierarchy} scene={sc.name}";
                if (!_dumped && w * h > 200 * 60) big.Add(desc);
                // the small "title" texts of panels are TMP, not images; buttons/icons are small: require some size for UI images
                // match the sprite/texture name and the object + parent names only (a root named "Valheim..." must not match everything)
                if (!Match(asset + "|" + c.gameObject.name + "|" + (c.transform.parent != null ? c.transform.parent.name : ""))) return;
                if (rt != null && w * h < 120 * 40) return;
                found.Add((c, desc, w * h));
            }
            if (_imageT != null) { var sp = AccessTools.Property(_imageT, "sprite"); foreach (var o in Resources.FindObjectsOfTypeAll(_imageT)) { var c = o as Component; var s = c != null ? sp.GetValue(c, null) as Sprite : null; Consider(c, s != null ? s.name + "/" + (s.texture != null ? s.texture.name : "") : "", "Image"); } }
            if (_rawT != null) { var tp = AccessTools.Property(_rawT, "texture"); foreach (var o in Resources.FindObjectsOfTypeAll(_rawT)) { var c = o as Component; var t = c != null ? tp.GetValue(c, null) as Texture : null; Consider(c, t != null ? t.name : "", "RawImage"); } }
            foreach (var sr in Resources.FindObjectsOfTypeAll<SpriteRenderer>()) Consider(sr, sr.sprite != null ? sr.sprite.name : "", "SpriteRenderer");
            if (!_dumped) { _dumped = true; ValCraftPlugin.Log.LogInfo($"[logo] diagnostic: {big.Count} large UI images on the menu"); foreach (var d in big) ValCraftPlugin.Log.LogInfo("[logo]   big " + d); }
            Component host = null; float best = 0;
            foreach (var (c, desc, area) in found)
            {
                if (area > best && c.transform is RectTransform) { best = area; host = c; }
                if (!Seen.Add(c.GetInstanceID())) continue;
                ValCraftPlugin.Log.LogInfo("[logo] hiding " + desc);
                Hidden.Add(c);
                // ember / glow particles attached to the logo
                foreach (var ps in c.GetComponentsInChildren<ParticleSystemRenderer>(true)) if (Seen.Add(ps.GetInstanceID())) { Hidden.Add(ps); ValCraftPlugin.Log.LogInfo("[logo] hiding particles " + Path(ps.transform)); }
            }
            Enforce();
            if (_ours == null && host != null) Build((RectTransform)host.transform);
        }

        static void Build(RectTransform logo)
        {
            if (_tmpT == null) { ValCraftPlugin.Log.LogWarning("[logo] TextMeshPro not found; logo hidden without replacement"); return; }
            _ours = new GameObject("ValCraftLogo", typeof(RectTransform));
            var rect = (RectTransform)_ours.transform;
            rect.SetParent(logo.parent, false);
            rect.SetSiblingIndex(logo.GetSiblingIndex() + 1);
            rect.anchorMin = logo.anchorMin; rect.anchorMax = logo.anchorMax; rect.pivot = logo.pivot;
            rect.anchoredPosition = logo.anchoredPosition; rect.sizeDelta = logo.sizeDelta; rect.localScale = logo.localScale; rect.localRotation = logo.localRotation;
            object font = null;
            if (_fontT != null)
                foreach (var f in Resources.FindObjectsOfTypeAll(_fontT))
                {
                    var nm = ((UnityEngine.Object)f).name.ToLowerInvariant();
                    if (nm.Contains("norse")) { font = f; if (nm.Contains("bold")) break; }
                }
            var title = Text(_ours.transform, "Title", "VALCRAFT", font, new Vector2(0, 0.18f), new Vector2(1, 1),
                new Color(1f, 0.86f, 0.55f), new Color(1f, 0.55f, 0.18f), new Color(0.55f, 0.16f, 0.05f), 0.28f, new Color(0.12f, 0.04f, 0.02f), new Color(1f, 0.42f, 0.08f, 0.85f));
            if (ShowVersion)
                Text(_ours.transform, "Version", "v" + MyPluginInfo.PLUGIN_VERSION, font, new Vector2(0.62f, 0f), new Vector2(0.98f, 0.24f),
                    new Color(0.85f, 0.88f, 0.92f), new Color(0.62f, 0.66f, 0.72f), new Color(0.35f, 0.38f, 0.44f), 0.2f, new Color(0.05f, 0.05f, 0.08f), new Color(0.6f, 0.8f, 1f, 0.35f));
            ValCraftPlugin.Log.LogInfo($"[logo] ValCraft title placed at '{Path(logo)}' ({logo.rect.width:F0}x{logo.rect.height:F0}, font {(font != null ? ((UnityEngine.Object)font).name : "default")}, ok={title != null})");
        }

        static Component Text(Transform parent, string name, string text, object font, Vector2 aMin, Vector2 aMax, Color top, Color mid, Color bottom, float outline, Color outlineC, Color glow)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform; r.anchorMin = aMin; r.anchorMax = aMax; r.offsetMin = r.offsetMax = Vector2.zero;
            var tmp = go.AddComponent(_tmpT);
            void P(string n, object v) { try { AccessTools.Property(_tmpT, n)?.SetValue(tmp, v, null); } catch (Exception e) { ValCraftPlugin.Log.LogDebug($"[logo] {n}: {e.Message}"); } }
            if (font != null) P("font", font);
            P("text", text);
            P("enableAutoSizing", true); P("fontSizeMin", 12f); P("fontSizeMax", 600f);
            var align = AccessTools.TypeByName("TMPro.TextAlignmentOptions"); if (align != null) P("alignment", Enum.ToObject(align, 514));
            P("color", Color.white); P("characterSpacing", 6f); P("raycastTarget", false);
            var grad = AccessTools.TypeByName("TMPro.VertexGradient");
            if (grad != null) { P("enableVertexGradient", true); P("colorGradient", Activator.CreateInstance(grad, new object[] { top, top, Color.Lerp(mid, bottom, 0.6f), bottom })); }
            // material instance: outline + outer glow + soft underlay (TMP SDF shader)
            try
            {
                var mat = AccessTools.Property(_tmpT, "fontMaterial")?.GetValue(tmp, null) as Material;
                if (mat != null)
                {
                    mat.SetFloat("_OutlineWidth", outline); mat.SetColor("_OutlineColor", outlineC); mat.SetFloat("_FaceDilate", 0.1f);
                    mat.EnableKeyword("GLOW_ON"); mat.SetColor("_GlowColor", glow); mat.SetFloat("_GlowOffset", 0.2f); mat.SetFloat("_GlowOuter", 0.55f); mat.SetFloat("_GlowInner", 0.05f); mat.SetFloat("_GlowPower", 0.6f);
                    mat.EnableKeyword("UNDERLAY_ON"); mat.SetColor("_UnderlayColor", new Color(0, 0, 0, 0.6f)); mat.SetFloat("_UnderlayOffsetY", -0.4f); mat.SetFloat("_UnderlaySoftness", 0.5f);
                    AccessTools.Method(_tmpT, "UpdateMeshPadding")?.Invoke(tmp, null);
                }
            }
            catch (Exception e) { ValCraftPlugin.Log.LogDebug("[logo] material: " + e.Message); }
            return tmp;
        }
    }
}
