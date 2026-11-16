using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutInteriors
{
    /// <summary>
    /// Lights you control. Anything with a lamp in it (the hideout's own lamps, copies of them, and the mod's own Light
    /// and Spotlight from the Lighting tab) has a switch, a brightness and a colour: select it and its bar is along the
    /// top of the screen, where a poster's or screen's would be. O is the switch. What's set is kept with the thing.
    /// </summary>
    internal static partial class Interiors
    {
        private const string LampKey = ExtraPrefix + "hideoutinteriors.lamp", SpotKey = ExtraPrefix + "hideoutinteriors.spot";

        private struct LightBase { public Color Colour; public float Power; }

        // What each lamp was before it was set, the lamps switched off here, and the things with something set.
        private static readonly Dictionary<Light, LightBase> lightBase = new Dictionary<Light, LightBase>();
        private static readonly HashSet<Light> lightsOff = new HashSet<Light>();
        private static readonly HashSet<Transform> litThings = new HashSet<Transform>();
        private static readonly Dictionary<Transform, bool> hasLights = new Dictionary<Transform, bool>();

        private static readonly (string name, string colour)[] LightColours =
        {
            ("OWN", null), ("WARM", "#FFD29A"), ("WHITE", "#FFFFFF"), ("COOL", "#CFE4FF"), ("RED", "#FF3B30"), ("ORANGE", "#FF8A1E"), ("YELLOW", "#FFE14A"),
            ("GREEN", "#3CFF6A"), ("CYAN", "#35E6FF"), ("BLUE", "#3D6BFF"), ("PURPLE", "#A04BFF"), ("PINK", "#FF4FB3"),
        };

        internal static void BindLights()
        {
            extras[LampKey] = new Extra { Name = "Light (yours to set)", Kind = "Lighting", Make = () => LampPattern(false) };
            extras[SpotKey] = new Extra { Name = "Spotlight (yours to set)", Kind = "Lighting", Make = () => LampPattern(true) };
        }

        // A bare bulb that lights all round, or a small can that lights where it points (along its blue arrow).
        private static GameObject LampPattern(bool spot)
        {
            var root = new GameObject(spot ? "Spotlight" : "Light");
            GameObject bulb = GameObject.CreatePrimitive(spot ? PrimitiveType.Cylinder : PrimitiveType.Sphere);
            UnityEngine.Object.DestroyImmediate(bulb.GetComponent<Collider>());
            bulb.name = "Bulb";
            bulb.transform.SetParent(root.transform, false);
            if (spot)
            {
                bulb.transform.localRotation = Quaternion.Euler(90, 0, 0);
                bulb.transform.localScale = new Vector3(.1f, .06f, .1f);
            }
            else bulb.transform.localScale = Vector3.one * .09f;
            var glow = new Material(LitShader()) { name = "Hideout Interiors lamp" };
            Glow(glow, new Color(1f, .82f, .6f), 1);
            bulb.GetComponent<MeshRenderer>().sharedMaterial = glow;
            var lamp = new GameObject("Glow");
            lamp.transform.SetParent(root.transform, false);
            lamp.transform.localPosition = spot ? new Vector3(0, 0, .07f) : Vector3.zero;
            Light light = lamp.AddComponent<Light>();
            light.type = spot ? LightType.Spot : LightType.Point;
            light.range = spot ? 12 : 7;
            light.spotAngle = 70;
            light.intensity = spot ? 2.4f : 1.5f;
            light.color = new Color(1f, .82f, .6f);
            light.shadows = LightShadows.None;
            // The pattern's kept out of sight; its copies are lit.
            light.enabled = false;
            return root;
        }

        private static void Glow(Material m, Color colour, float power)
        {
            m.color = colour * .35f + Color.black * .65f;
            if (!m.HasProperty("_EmissionColor")) { m.color = colour; return; }
            m.SetColor("_EmissionColor", colour * Mathf.Clamp(power, 0, 3));
            if (power > 0) m.EnableKeyword("_EMISSION"); else m.DisableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }

        private static bool HasLights(Transform t)
        {
            if (t == null) return false;
            if (!hasLights.TryGetValue(t, out bool has)) hasLights[t] = has = t.GetComponentInChildren<Light>(true) != null;
            return has;
        }

        private static void ForgetLights()
        {
            lightBase.Clear(); lightsOff.Clear(); litThings.Clear(); hasLights.Clear();
        }

        /// <summary>A thing's lamps as its switch, brightness and colour say (nothing set: as they come).</summary>
        private static void ApplyLights(Transform t, ThingMeta m)
        {
            bool set = m != null && (m.LightOff || m.LightColor != null || m.LightPower != 1f);
            if (!set && !litThings.Contains(t)) return;
            Color? tint = null;
            if (m != null && m.LightColor != null && ColorUtility.TryParseHtmlString(m.LightColor, out Color parsed)) tint = parsed;
            float power = m != null ? Mathf.Clamp(m.LightPower, 0, 8) : 1;
            bool off = m != null && m.LightOff;
            Color shown = Color.white;
            foreach (Light l in t.GetComponentsInChildren<Light>(true))
            {
                if (!lightBase.TryGetValue(l, out LightBase own)) lightBase[l] = own = new LightBase { Colour = l.color, Power = l.intensity };
                l.color = shown = tint ?? own.Colour;
                l.intensity = own.Power * power;
                if (off) { if (l.enabled) { l.enabled = false; lightsOff.Add(l); } }
                else if (lightsOff.Remove(l)) l.enabled = true;
            }
            // The mod's own lamps: the bulb glows as its light does.
            DecorTag tag = t.GetComponent<DecorTag>();
            if (tag != null && (tag.Source == LampKey || tag.Source == SpotKey))
            {
                Transform bulb = t.Find("Bulb");
                MeshRenderer r = bulb != null ? bulb.GetComponent<MeshRenderer>() : null;
                if (r != null) Glow(r.material, shown, off ? 0 : power);
            }
            if (set) litThings.Add(t); else litThings.Remove(t);
        }

        // Now and then: a lamp switched off here that the game switched back on (its area's lights came on) is off again.
        private static void LightsTick()
        {
            if (lightsOff.Count == 0 || Time.frameCount % 45 != 0) return;
            lightsOff.RemoveWhere(l => l == null);
            foreach (Light l in lightsOff) if (l.enabled) l.enabled = false;
        }

        // The selected things that have lamps: each given a change, shown and kept.
        private static void LightsDo(Action<ThingMeta> change)
        {
            var lit = selection.Where(t => t != null && HasLights(t)).ToList();
            if (lit.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Nothing selected has a light in it. The Lighting tab has lamps, and a Light and a Spotlight of your own."); return; }
            Checkpoint();
            foreach (Transform t in lit)
            {
                ThingMeta m = MetaOf(t, true);
                if (m == null) continue;
                change(m);
                ApplyLights(t, m);
            }
            SaveMeta(lit);
            Status();
        }

        // O: the selection's lamps off, or (all off already) on.
        private static void SwitchLights()
        {
            var lit = selection.Where(t => t != null && HasLights(t)).ToList();
            bool off = !lit.All(t => MetaOf(t, false) is ThingMeta m && m.LightOff);
            LightsDo(m => m.LightOff = off);
        }

        // ---- the light's bar ----------------------------------------------------------------------------------

        private static RectTransform stripRect;
        private static readonly List<(RectTransform rect, Action click)> stripButtons = new List<(RectTransform, Action)>();
        private static int stripSign;
        private static float stripX, stripY, stripWidth;

        // Each frame of decor mode: the bar for the selected things that have lamps (where no poster's or screen's is).
        private static void LightBarTick()
        {
            int sign = 0;
            if (bar == null && !Using && !dragging && axisDrag < 0 && uiRoot != null)
                foreach (Transform t in selection)
                {
                    if (t == null || !HasLights(t)) continue;
                    ThingMeta m = MetaOf(t, false);
                    sign = unchecked(sign * 31 + t.GetInstanceID());
                    if (m != null) sign = unchecked(sign * 31 + (m.LightOff ? 1 : 2) + (m.LightColor != null ? m.LightColor.GetHashCode() : 0) + Mathf.RoundToInt(m.LightPower * 100));
                }
            if (sign != 0) sign = unchecked(sign * 31 + (panelHidden ? 1 : 2));
            if (sign == stripSign && (sign == 0 || stripRect != null)) return;
            stripSign = sign;
            BuildLightBar();
        }

        private static void StripGone()
        {
            if (stripRect != null) UnityEngine.Object.Destroy(stripRect.gameObject);
            stripRect = null; stripSign = 0;
            stripButtons.Clear();
        }

        private static void BuildLightBar()
        {
            int sign = stripSign;
            StripGone();
            stripSign = sign;
            if (sign == 0 || uiRoot == null) return;
            var lit = selection.Where(t => t != null && HasLights(t)).ToList();
            if (lit.Count == 0) return;
            ThingMeta first = MetaOf(lit[0], false);
            bool on = !lit.All(t => MetaOf(t, false) is ThingMeta m && m.LightOff);
            float power = first != null ? first.LightPower : 1;
            string colour = first?.LightColor;

            stripWidth = Mathf.Max(360, builtSize.x - 180 - (panelHidden ? 12 + showTab.sizeDelta.x + 8 : panelW + 24));
            stripRect = UiRect("Light bar", uiRoot, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            stripRect.anchoredPosition = new Vector2(180, -12);
            stripRect.gameObject.AddComponent<Image>().color = new Color(.06f, .06f, .055f, .94f);
            Text("Kind", stripRect, lit.Count == 1 ? "LIGHT" : lit.Count + " LIGHTS", 15, new Vector2(10, -6), new Vector2(92, BarButtonHeight), TextAlignmentOptions.MidlineLeft, Amber, true);
            stripX = 106; stripY = 6;
            StripButton(on ? "ON" : "OFF", 52, SwitchLights, on ? ButtonOn : ButtonOff, Ink);
            StripButton("DIMMER", 68, () => LightsDo(m => m.LightPower = Mathf.Max(.1f, Mathf.Round((m.LightPower / 1.25f) * 100) / 100)), ButtonOff, Ink);
            StripButton("BRIGHTER", 78, () => LightsDo(m => m.LightPower = Mathf.Min(8, Mathf.Round((m.LightPower * 1.25f) * 100) / 100)), ButtonOff, Ink);
            StripButton($"{power * 100:0}%", 56, () => LightsDo(m => m.LightPower = 1), ButtonOff, Dim);
            stripX += 8;
            foreach (var (name, html) in LightColours)
            {
                string pick = html;
                Color back = ButtonOff, ink = Ink;
                if (html != null && ColorUtility.TryParseHtmlString(html, out Color c))
                {
                    back = c * .8f; back.a = 1;
                    ink = c.r * .3f + c.g * .59f + c.b * .11f > .55f ? new Color(.08f, .08f, .08f) : Color.white;
                }
                Image made = StripButton(name, name.Length * 8 + 22, () => LightsDo(m => m.LightColor = pick), back, ink);
                if (pick == colour)
                {
                    var edge = made.gameObject.AddComponent<Outline>();
                    edge.effectColor = Color.white; edge.effectDistance = new Vector2(2, 2);
                }
            }
            stripRect.sizeDelta = new Vector2(stripWidth, stripY + BarRow + 2);
        }

        private static Image StripButton(string label, float width, Action click, Color back, Color ink)
        {
            if (stripX + width > stripWidth - 5.5f && stripX > 6) { stripX = 6; stripY += BarRow; }
            RectTransform r = UiRect("Button " + label, stripRect, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            r.anchoredPosition = new Vector2(stripX, -stripY); r.sizeDelta = new Vector2(width, BarButtonHeight);
            Image image = r.gameObject.AddComponent<Image>();
            image.color = back;
            TextMeshProUGUI t = Text("Label", r, label, 13, Vector2.zero, r.sizeDelta, TextAlignmentOptions.Center, ink, true);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(2, 0); t.rectTransform.offsetMax = new Vector2(-2, 0);
            t.enableAutoSizing = true; t.fontSizeMin = 8; t.fontSizeMax = 13; t.enableWordWrapping = false;
            stripButtons.Add((r, click));
            stripX += width + 4;
            return image;
        }

        // A click on the light's bar (true: it was the bar's).
        private static bool StripClick(Vector2 mouse)
        {
            if (stripRect == null || !Inside(stripRect, mouse)) return false;
            foreach (var (rect, click) in stripButtons.ToList())
                if (Inside(rect, mouse)) { click(); break; }
            return true;
        }
    }
}
