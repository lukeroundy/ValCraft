using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValCraft
{
    /// <summary>
    /// 0.11.0: main menu: the Valheim logo image is hidden and "ValCraft" is drawn in its place as TextMeshPro text with
    /// Valheim's own Norse UI font (no Valheim artwork copied, no external assets). Retried until the menu exists.
    /// </summary>
    internal static class Logo
    {
        internal static bool Enabled = true;
        static float _next; static bool _done; static int _tries;
        static Type _imageT, _tmpT, _fontT;

        internal static void Tick(bool inGame)
        {
            if (!Enabled || inGame) { if (inGame) _done = false; return; }
            if (_done || Time.unscaledTime < _next || _tries > 60) return;
            _next = Time.unscaledTime + 1f; _tries++;
            try { _done = Replace(); } catch (Exception e) { ValCraftPlugin.Log.LogDebug("[logo] " + e.Message); }
        }

        static bool Replace()
        {
            _imageT = _imageT ?? AccessTools.TypeByName("UnityEngine.UI.Image");
            _tmpT = _tmpT ?? AccessTools.TypeByName("TMPro.TextMeshProUGUI");
            _fontT = _fontT ?? AccessTools.TypeByName("TMPro.TMP_FontAsset");
            if (_imageT == null || _tmpT == null) return false;
            var spriteP = AccessTools.Property(_imageT, "sprite");
            Component logo = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(_imageT))
            {
                var c = o as Component; if (c == null || !c.gameObject.scene.IsValid()) continue;
                var sp = spriteP.GetValue(c, null) as Sprite;
                string n = (sp != null ? sp.name : "") + "|" + c.gameObject.name;
                if (n.IndexOf("logo", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (n.IndexOf("valcraft", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var rt = c.transform as RectTransform;
                if (rt == null || rt.rect.width < 200) continue; // the big title, not small icons
                logo = c; break;
            }
            if (logo == null) return false;
            ((Behaviour)logo).enabled = false;
            var go = new GameObject("ValCraftLogo");
            go.transform.SetParent(logo.transform, false);
            var tmp = go.AddComponent(_tmpT);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            void P(string name, object v) { try { AccessTools.Property(_tmpT, name)?.SetValue(tmp, v, null); } catch (Exception e) { ValCraftPlugin.Log.LogDebug($"[logo] {name}: {e.Message}"); } }
            object font = null;
            if (_fontT != null)
                foreach (var f in Resources.FindObjectsOfTypeAll(_fontT))
                {
                    var nm = ((UnityEngine.Object)f).name.ToLowerInvariant();
                    if (nm.Contains("norse")) { font = f; if (nm.Contains("bold")) break; }
                }
            if (font != null) P("font", font);
            P("text", "ValCraft");
            P("enableAutoSizing", true); P("fontSizeMin", 24f); P("fontSizeMax", 400f);
            var align = AccessTools.TypeByName("TMPro.TextAlignmentOptions");
            if (align != null) P("alignment", Enum.ToObject(align, 514)); // Center
            P("color", new Color(0.96f, 0.80f, 0.46f, 1f));
            P("characterSpacing", 4f);
            P("outlineWidth", 0.22f);
            P("outlineColor", (Color32)new Color(0.18f, 0.10f, 0.04f, 1f));
            var grad = AccessTools.TypeByName("TMPro.VertexGradient");
            if (grad != null)
            {
                var g = Activator.CreateInstance(grad, new object[] { new Color(1f, 0.9f, 0.62f), new Color(1f, 0.9f, 0.62f), new Color(0.72f, 0.46f, 0.18f), new Color(0.72f, 0.46f, 0.18f) });
                P("enableVertexGradient", true); P("colorGradient", g);
            }
            ValCraftPlugin.Log.LogInfo($"[logo] main menu title replaced with 'ValCraft' ({(font != null ? ((UnityEngine.Object)font).name : "default font")})");
            return true;
        }
    }
}
