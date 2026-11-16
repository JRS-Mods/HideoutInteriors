using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Hideout;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutInteriors
{
    /// <summary>
    /// Decor mode's screen: the button under the overview's own, the panel of things to put in (every thing the hideout's
    /// scene has, once each, by kind, with a picture taken of it), the line of keys at the bottom and the box drawn while
    /// selecting. The panel does its own clicking and scrolling (the game's input doesn't reach a mod's UI by itself).
    /// </summary>
    internal static partial class Interiors
    {
        private sealed class Entry
        {
            public string Key, Name, Kind;
            public Transform Source;
            public Texture2D Picture;
            public bool Tried;
            public RectTransform Cell;
            public RawImage Image;
            public TextMeshProUGUI Label;
        }


        private static List<Entry> catalog;
        private static string tab = "All";
        // The search: what's typed, and whether the box has the keyboard.
        private static string query = "";
        private static bool typing;
        private static RectTransform searchBox;
        private static TextMeshProUGUI searchText;
        private static Image searchBack;

        private static GameObject ui;
        private static RectTransform uiRoot, panel, viewport, content, box, tabsRow;
        private static TextMeshProUGUI statusText;
        private static TMP_FontAsset font;
        private static readonly List<(RectTransform rect, Action click)> buttons = new List<(RectTransform, Action)>();
        private static readonly List<(RectTransform rect, TextMeshProUGUI label, Image back, string kind)> tabButtons = new List<(RectTransform, TextMeshProUGUI, Image, string)>();
        private static readonly List<(RectTransform rect, Entry entry)> cells = new List<(RectTransform, Entry)>();
        private static readonly List<(RectTransform rect, string key)> hiddenCells = new List<(RectTransform, string)>();

        private const float PanelWidth = 540, SideWidth = 132, CellWidth = 116, CellHeight = 132, Gap = 4;
        // The grid's width: the panel less its margins and the category list.
        private static float GridWidth => panelW - 24 - SideWidth - 8;
        private static readonly Color Ink = new Color(.85f, .83f, .78f), Dim = new Color(.55f, .54f, .5f), Amber = new Color(1f, .72f, .25f);

        // ---- the button ----------------------------------------------------------------------------------------

        private static ComplementaryButton decorButton;
        private static HideoutScreenOverlay overlay;
        private static float nextButtonLook;
        private const string DecorButtonName = "Hideout extensions decor button";

        // A copy of the customization button, one step under the generator's, with our picture.
        private static void DecorButton()
        {
            if (!Decorating.Value || Time.unscaledTime < nextButtonLook) return;
            nextButtonLook = Time.unscaledTime + .5f;
            if (overlay == null) overlay = UnityEngine.Object.FindObjectOfType<HideoutScreenOverlay>();
            if (overlay == null) return;
            ComplementaryButton custom = overlay._customizationButton, generator = overlay._generatorButton;
            if (custom == null || generator == null) return;
            // Shown when the overview shows its own (the game hides them while an area's open).
            if (decorButton != null)
            {
                bool show = custom.gameObject.activeSelf;
                if (decorButton.gameObject.activeSelf != show) decorButton.gameObject.SetActive(show);
                return;
            }
            if (!generator.gameObject.activeInHierarchy) return;
            try
            {
                // The overview's screen outlives a hideout visit: the button from the last one is still there.
                Transform there = generator.transform.parent.Find(DecorButtonName);
                GameObject go = there != null ? there.gameObject : UnityEngine.Object.Instantiate(custom.gameObject, generator.transform.parent);
                go.name = DecorButtonName;
                var button = go.GetComponent<ComplementaryButton>();
                // Drawn at the size the customization button's own picture is (the game sizes them to their pictures).
                Sprite theirs = custom._defaultIcon;
                Sprite icon = LoadSprite("decor_icon.png", theirs);
                if (icon != null)
                {
                    button._defaultIcon = icon; button._selectedIcon = icon;
                    if (button._icon != null) button._icon.sprite = icon;
                }
                if (there != null) { }
                else if (generator.transform.parent.GetComponent<LayoutGroup>() != null) go.transform.SetSiblingIndex(generator.transform.GetSiblingIndex() + 1);
                else
                {
                    Vector3 step = generator.transform.position - custom.transform.position;
                    if (step.sqrMagnitude < 1) step = new Vector3(0, -((RectTransform)generator.transform).rect.height * generator.transform.lossyScale.y * 1.15f, 0);
                    go.transform.position = generator.transform.position + step;
                }
                button.Show(_ => { if (decorOn) CloseDecor(); else OpenDecor(); });
                button.SetTooltipMessages(() => "Decorate the hideout", () => "Decorate the hideout");
                button.SetSelectedStatus(decorOn);
                decorButton = button;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("The decor button couldn't be put in: " + e.Message);
                nextButtonLook = Time.unscaledTime + 30;
            }
        }

        private static Sprite LoadSprite(string file, Sprite like = null)
        {
            string path = Path.Combine(Plugin.Folder, file);
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = file };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path))) return null;
            texture.wrapMode = TextureWrapMode.Clamp;
            float perUnit = like != null && like.rect.width > 0 ? like.pixelsPerUnit * texture.width / like.rect.width : 100;
            if (like != null) Plugin.Log.LogInfo($"Decor button: the game's picture is {like.rect.width}x{like.rect.height} at {like.pixelsPerUnit}/unit; ours {texture.width} at {perUnit:0.#}/unit.");
            return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), perUnit);
        }

        // ---- the catalog ---------------------------------------------------------------------------------------

        // The game's customization slots (where the posters and statuettes you own go, set from its Customization
        // screen) and the models it puts in them. Copied as decor they're a blank stand-in that does nothing.
        private static bool GameSlot(Transform t) =>
            t.GetComponentInParent<StashItemModelSpawnPoint>(true) != null || t.GetComponentInParent<StashItemModel>(true) != null
            || t.GetComponentInChildren<StashItemModelSpawnPoint>(true) != null || t.GetComponentInChildren<StashItemModel>(true) != null;

        // Every thing the hideout's areas have, at every level (shown or not), once each by name.
        private static void BuildCatalog()
        {
            catalog = new List<Entry>();
            var seen = new HashSet<string>();
            foreach (HideoutArea area in Areas())
            {
                if (Key(area) == null) continue;
                foreach (Transform t in area.transform.Cast<Transform>().Where(IsLevel).SelectMany(l => ItemsUnder(l, false)).ToList())
                {
                    if (t.GetComponent<DecorTag>() != null || !ForPanel(t.name) || GameSlot(t)) continue;
                    ItemOf(t, out HideoutArea from);
                    if (!MeshBounds(t, out Bounds b) || b.size.x > BiggestItem || b.size.z > BiggestItem || b.size.magnitude < .08f) continue;
                    string name = Pretty(t.name);
                    if (!seen.Add(BaseName(t.name).ToLowerInvariant())) continue;
                    string key = ItemKey(from ?? area, t);
                    if (key == null) continue;
                    catalog.Add(new Entry { Key = key, Name = name, Kind = KindOf(t.name), Source = t });
                }
            }
            // And what stands outside the areas' levels: the scene's lamps, barrels, cables and seasonal decorations.
            foreach (var (t, area) in OutsideThings(false).ToList())
            {
                if (!ForPanel(t.name) || GameSlot(t) || !MeshBounds(t, out Bounds b) || b.size.magnitude < .08f) continue;
                string name = Pretty(t.name);
                if (!seen.Add(BaseName(t.name).ToLowerInvariant())) continue;
                string key = ItemKey(area, t);
                if (key == null) continue;
                catalog.Add(new Entry { Key = key, Name = name, Kind = KindOf(t.name), Source = t });
            }
            // And the rest of what the scene has: parts of things, parts of the rooms, anything under a root the passes
            // above don't go into.
            int before = catalog.Count;
            try { RestOfScene(seen); }
            catch (Exception e) { Plugin.Log.LogWarning("Decor: the rest of the scene's things couldn't be listed: " + e.Message); }
            Plugin.Log.LogInfo($"Decor: {before} kinds of thing from the areas and the scene's groups, {catalog.Count - before} more from the rest of the scene.");
            // Different models with the same name (four armchairs): Armchair, Armchair 2, Armchair 3...
            foreach (var same in catalog.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                int i = 0;
                foreach (Entry e in same.OrderBy(e => BaseName(e.Source != null ? e.Source.name : e.Name), StringComparer.OrdinalIgnoreCase))
                    if (i++ > 0) e.Name += " " + i;
            }
            catalog.AddRange(ExtraEntries());
            var tabs = TabKinds();
            catalog = catalog.OrderBy(e => tabs.IndexOf(e.Kind)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Plugin.Log.LogInfo($"Decor: {catalog.Count} kinds of thing to put in.");
        }

        // Left out of the rest of the scene: lesser levels of detail and shadow-only meshes (the thing itself is its
        // LOD0), the mod's own objects and other mods' rooms.
        private static readonly System.Text.RegularExpressions.Regex LesserPart = new System.Text.RegularExpressions.Regex(
            @"_lod[1-9]\d*$|_lod_[1-9]|shadow|_sl$|_occluder|_occlusion|^lod[1-9]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        // The kind for what's too big to be a thing, or is part of the rooms.
        private const string RoomKind = "Rooms & walls";

        // Every root of the hideout's scene, walked down to the first object that's something to see by itself. What
        // the areas' levels and the scene's groups gave is already in; this adds what they leave out: the parts things
        // are made of, the parts of the rooms (walls, floors, pipes: under Rooms & walls), things with a skeleton.
        private static void RestOfScene(HashSet<string> seen)
        {
            if (style == null) return;
            var had = new HashSet<Transform>(catalog.Where(e => e.Source != null).Select(e => e.Source));
            foreach (GameObject root in style.scene.GetRootGameObjects())
            {
                if (root == null || root.name.StartsWith("Hideout extensions", StringComparison.OrdinalIgnoreCase) || root.name.StartsWith("Hideout Interiors", StringComparison.OrdinalIgnoreCase)) continue;
                RestUnder(root.transform, root.name == "STYLE", seen, had, 0);
            }
        }

        private static void RestUnder(Transform t, bool room, HashSet<string> seen, HashSet<Transform> had, int depth)
        {
            if (depth > 24 || t.GetComponent<DecorTag>() != null) return;
            bool skin = t.GetComponent<SkinnedMeshRenderer>() != null || (t.GetComponent<Animator>() != null && t.GetComponentInChildren<SkinnedMeshRenderer>(true) != null);
            bool whole = skin || t.GetComponent<LODGroup>() != null || (t.GetComponent<MeshRenderer>() != null && t.GetComponent<MeshFilter>() != null);
            if (whole)
            {
                if (!had.Contains(t)) OfferRest(t, room, seen);
                return;
            }
            foreach (Transform child in t) RestUnder(child, room, seen, had, depth + 1);
        }

        private static void OfferRest(Transform t, bool room, HashSet<string> seen)
        {
            if (t.parent == null || !ForPanel(t.name) || LesserPart.IsMatch(BaseName(t.name)) || GameSlot(t)) return;
            MeshRenderer own = t.GetComponent<MeshRenderer>();
            if (own != null && own.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) return;
            if (!MeshBounds(t, out Bounds b) || b.size.magnitude < .08f) return;
            // "bed_lod0" is the bed: once, whichever is met first.
            string name = System.Text.RegularExpressions.Regex.Replace(BaseName(t.name).ToLowerInvariant(), @"_lod_?0$", "");
            if (!seen.Add(name) || (name != BaseName(t.name).ToLowerInvariant() && !seen.Add(BaseName(t.name).ToLowerInvariant()))) return;
            HideoutArea area = t.GetComponentInParent<HideoutArea>(true);
            if (area != null && Key(area) == null) area = null;
            string key = area != null ? ItemKey(area, t) : RootKey(t);
            if (key == null) return;
            bool big = room || b.size.x > BiggestItem || b.size.z > BiggestItem;
            catalog.Add(new Entry { Key = key, Name = Pretty(t.name), Kind = big ? RoomKind : KindOf(t.name), Source = t });
        }


        // ---- the screen ----------------------------------------------------------------------------------------

        // The camera the screen's drawn for (none: straight on the screen), the size it was laid out for, the panel's
        // width that fits it, and whether the panel's tucked away (H).
        private static Camera uiCamera;
        private static Vector2 builtSize, builtFor;
        private static float builtScale;
        private static float panelW = PanelWidth;
        private static bool panelHidden;
        private static RectTransform showTab, statusBand;
        private static readonly Image[] viewButtons = new Image[5];

        // The game's own canvas for the overview's screen: decor mode's screen goes on it, so it's the size the
        // game's screen is (a screen of our own could be bigger than the window when the game draws at another size).
        private static Canvas HostCanvas()
        {
            if (overlay == null) overlay = UnityEngine.Object.FindObjectOfType<HideoutScreenOverlay>();
            Canvas c = overlay != null ? overlay.GetComponentInParent<Canvas>() : null;
            return c != null ? c.rootCanvas : null;
        }

        // The screen it was laid out on, and the size setting, changed since.
        private static bool UiSizeChanged() =>
            ui != null && (builtScale != PanelScale.Value || (((RectTransform)ui.transform).rect.size - builtFor).sqrMagnitude > 4);

        private static void CloseUi()
        {
            if (ui != null) UnityEngine.Object.Destroy(ui);
            ui = null; tipUi = null; tipFor = null;
            // What was on it went with it.
            StripGone();
            CancelAsk();
            BoxHide();
        }

        private static void BuildUi()
        {
            // Using one thing quickly: no panel, so no need to list the hideout (it takes a second).
            if (catalog == null && !quickUse) BuildCatalog();
            if (font == null)
                font = overlay != null ? overlay.GetComponentsInChildren<TextMeshProUGUI>(true).Select(t => t.font).FirstOrDefault(f => f != null) : null;
            if (font == null) font = UnityEngine.Object.FindObjectOfType<AreaIcon>()?._currentAreaLevel?.font;
            buttons.Clear(); tabButtons.Clear(); cells.Clear(); hiddenCells.Clear(); mediaCells.Clear(); mediaThumbs.Clear();
            typing = false;

            ui = new GameObject("Hideout Interiors decor screen", typeof(RectTransform));
            var canvas = ui.AddComponent<Canvas>();
            // In first person the overview's screen is off: a screen of our own.
            Canvas host = quickFp ? null : HostCanvas();
            var outer = (RectTransform)ui.transform;
            if (host != null)
            {
                outer.SetParent(host.transform, false);
                outer.anchorMin = Vector2.zero; outer.anchorMax = Vector2.one; outer.pivot = new Vector2(.5f, .5f);
                outer.offsetMin = outer.offsetMax = Vector2.zero; outer.localScale = Vector3.one;
                ui.layer = host.gameObject.layer;
                canvas.overrideSorting = true;
                canvas.sortingOrder = host.sortingOrder + 25;
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 25;
                var scaler = ui.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 1;
            }
            ui.AddComponent<GraphicRaycaster>();
            Canvas.ForceUpdateCanvases();
            uiCamera = canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;

            // Everything in a box the panel's size setting scales.
            float scale = builtScale = PanelScale.Value;
            builtFor = outer.rect.size;
            Vector2 size = outer.rect.size / scale;
            if (size.x < 400 || size.y < 300) size = new Vector2(1920, 1080) / scale;
            builtSize = size;
            uiRoot = UiRect("Scaled", outer, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(.5f, .5f));
            uiRoot.localScale = Vector3.one * scale;
            uiRoot.sizeDelta = size;
            panelW = Mathf.Clamp(size.x * .4f, 380, PanelWidth);
            // The bottom clears the game's menu bar (MAIN MENU, HIDEOUT, CHARACTER...), which is drawn over us.
            const float top = 60, bottom = MenuBarClear;
            float panelHeight = size.y - top - bottom;

            // A see-through screen over the hideout (not the overview's buttons on the left), so the game's icons don't take the clicks.
            RectTransform catcher = UiRect("Catcher", uiRoot, Vector2.zero, Vector2.one, new Vector2(.5f, .5f));
            catcher.offsetMin = new Vector2(170, 0); catcher.offsetMax = Vector2.zero;
            catcher.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .001f);

            panel = UiRect("Panel", uiRoot, new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 1));
            panel.offsetMin = new Vector2(-panelW - 12, bottom); panel.offsetMax = new Vector2(-12, -top);
            panel.gameObject.AddComponent<Image>().color = new Color(.06f, .06f, .055f, .94f);

            // Row 1: the title, MOVE / TURN / SIZE, hide, DONE (from the right; the title has what's left).
            float x = -12 - 64;
            Button(panel, "DONE", new Vector2(x, -10), new Vector2(64, 28), CloseDecor, anchorRight: true);
            Button(panel, "HIDE", new Vector2(x -= 4 + 52, -10), new Vector2(52, 28), TogglePanel, anchorRight: true);
            x -= 6;
            for (int i = gizmoButtons.Length - 1; i >= 0; i--)
            {
                var kind = (GizmoKind)i;
                gizmoButtons[i] = Button(panel, GizmoNames[i], new Vector2(x -= 4 + 54, -10), new Vector2(54, 28), () => SetGizmo(kind), anchorRight: true);
            }
            TextMeshProUGUI title = Text("Title", panel, "DECORATE", 22, new Vector2(12, -10), new Vector2(Mathf.Max(40, panelW + x - 20), 30), TextAlignmentOptions.MidlineLeft, Ink, true);
            title.enableAutoSizing = true; title.fontSizeMin = 11; title.fontSizeMax = 22; title.enableWordWrapping = false;

            // Row 2: the camera views.
            Text("View", panel, "VIEW", 13, new Vector2(12, -50), new Vector2(44, 24), TextAlignmentOptions.MidlineLeft, Dim, true);
            float viewWidth = (panelW - 24 - 46 - 4 * 4) / 5;
            for (int i = 0; i < ViewNames.Length; i++)
            {
                var kind = (ViewKind)i;
                viewButtons[i] = Button(panel, ViewNames[i], new Vector2(12 + 46 + i * (viewWidth + 4), -48), new Vector2(viewWidth, 26), () => SetView(kind), fontSize: 12);
            }

            // Row 3: the search box.
            searchBox = UiRect("Search", panel, new Vector2(0, 1), new Vector2(1, 1), new Vector2(.5f, 1));
            searchBox.offsetMin = new Vector2(12, -114); searchBox.offsetMax = new Vector2(-12, -84);
            searchBack = searchBox.gameObject.AddComponent<Image>();
            searchText = Text("Text", searchBox, "", 14, Vector2.zero, Vector2.zero, TextAlignmentOptions.MidlineLeft, Ink, false);
            searchText.rectTransform.anchorMin = Vector2.zero; searchText.rectTransform.anchorMax = Vector2.one;
            searchText.rectTransform.offsetMin = new Vector2(10, 0); searchText.rectTransform.offsetMax = new Vector2(-10, 0);
            searchText.enableWordWrapping = false;
            SearchLook();

            // The categories down the left of the things: a list has room for as many as there come to be (mods add
            // their own). Rows get shorter if they'd run off the bottom.
            const float listTop = 122, listBottom = 48;
            tabsRow = UiRect("Categories", panel, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 1));
            tabsRow.offsetMin = new Vector2(12, listBottom); tabsRow.offsetMax = new Vector2(12 + SideWidth, -listTop);
            var tabs = TabKinds();
            if (!tabs.Contains(tab)) tab = "All";
            float rowHeight = Mathf.Clamp((panelHeight - listTop - listBottom) / tabs.Count - 2, 14, 26);
            for (int i = 0; i < tabs.Count; i++)
            {
                string kind = tabs[i];
                RectTransform r = UiRect("Category " + kind, tabsRow, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1));
                r.anchoredPosition = new Vector2(0, -i * (rowHeight + 2)); r.sizeDelta = new Vector2(0, rowHeight);
                Image back = r.gameObject.AddComponent<Image>();
                TextMeshProUGUI label = Text("Label", r, kind, 13, Vector2.zero, new Vector2(SideWidth, rowHeight), TextAlignmentOptions.MidlineLeft, Ink, false);
                label.enableAutoSizing = true; label.fontSizeMin = 9; label.fontSizeMax = 13; label.enableWordWrapping = false;
                label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
                label.rectTransform.offsetMin = new Vector2(8, 0); label.rectTransform.offsetMax = new Vector2(-4, 0);
                tabButtons.Add((r, label, back, kind));
                buttons.Add((r, () => { if (tab == kind) return; tab = kind; StopTyping(); SearchLook(); FillTab(); }));
            }

            viewport = UiRect("Viewport", panel, Vector2.zero, Vector2.one, new Vector2(.5f, .5f));
            viewport.offsetMin = new Vector2(12 + SideWidth + 8, listBottom); viewport.offsetMax = new Vector2(-12, -listTop);
            viewport.gameObject.AddComponent<RectMask2D>();
            content = UiRect("Content", viewport, new Vector2(0, 1), new Vector2(1, 1), new Vector2(.5f, 1));
            content.anchoredPosition = Vector2.zero;

            // The bottom: everything back as the game has it (on a second click), and copying the selection.
            Image reset = Button(panel, "RESET ALL", Vector2.zero, new Vector2(130, 30), ResetClicked);
            resetRect = reset.rectTransform;
            resetRect.anchorMin = resetRect.anchorMax = Vector2.zero; resetRect.pivot = Vector2.zero;
            resetRect.anchoredPosition = new Vector2(12, 10);
            resetLabel = reset.GetComponentInChildren<TextMeshProUGUI>();
            resetArmedUntil = 0;
            RectTransform copy = Button(panel, "DUPLICATE", Vector2.zero, new Vector2(130, 30), DuplicateSelection).rectTransform;
            copy.anchorMin = copy.anchorMax = new Vector2(1, 0); copy.pivot = new Vector2(1, 0);
            copy.anchoredPosition = new Vector2(-12, 10);

            // With the panel tucked away: a tab on the right edge to bring it back.
            showTab = UiRect("Show panel", uiRoot, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
            showTab.anchoredPosition = new Vector2(-12, -top); showTab.sizeDelta = new Vector2(130, 30);
            showTab.gameObject.AddComponent<Image>().color = new Color(.22f, .21f, .19f, 1);
            TextMeshProUGUI showLabel = Text("Label", showTab, "DECORATE (H)", 14, Vector2.zero, showTab.sizeDelta, TextAlignmentOptions.Center, Ink, true);
            showLabel.rectTransform.anchorMin = Vector2.zero; showLabel.rectTransform.anchorMax = Vector2.one; showLabel.rectTransform.offsetMin = showLabel.rectTransform.offsetMax = Vector2.zero;
            buttons.Add((showTab, TogglePanel));

            // The line of keys along the bottom, left of the panel.
            statusBand = UiRect("Status", uiRoot, new Vector2(0, 0), new Vector2(1, 0), new Vector2(.5f, 0));
            statusBand.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .72f);
            statusText = Text("Text", statusBand, "", 16, Vector2.zero, Vector2.zero, TextAlignmentOptions.Center, Ink, false);
            statusText.rectTransform.anchorMin = Vector2.zero; statusText.rectTransform.anchorMax = Vector2.one;
            statusText.rectTransform.offsetMin = new Vector2(12, 4); statusText.rectTransform.offsetMax = new Vector2(-12, -4);
            statusText.enableAutoSizing = true; statusText.fontSizeMin = 10; statusText.fontSizeMax = 16;

            box = UiRect("Box", uiRoot, new Vector2(.5f, .5f), new Vector2(.5f, .5f), Vector2.zero);
            box.gameObject.AddComponent<Image>().color = new Color(1f, .72f, .25f, .12f);
            var edge = box.gameObject.AddComponent<Outline>();
            edge.effectColor = new Color(1f, .72f, .25f, .9f); edge.effectDistance = new Vector2(1.5f, 1.5f);
            box.gameObject.SetActive(false);

            PanelShown();
            ModeButtons();
            FillTab();
            Status();
        }

        // H, or the panel's HIDE: the panel out of the way to see more of the hideout, and back.
        private static void TogglePanel()
        {
            panelHidden = !panelHidden;
            StopTyping();
            PanelShown();
        }

        private static void PanelShown()
        {
            if (panel == null) return;
            panel.gameObject.SetActive(!panelHidden);
            // Using one thing straight from the overview or first person: no way into the panel from here.
            if (showTab != null) showTab.gameObject.SetActive(panelHidden && !quickUse);
            if (statusBand != null)
            {
                statusBand.offsetMin = new Vector2(180, MenuBarClear);
                statusBand.offsetMax = new Vector2(panelHidden ? -12 : -panelW - 24, MenuBarClear + 72);
            }
        }

        // How high off the bottom decor mode's screen starts: above the game's menu bar.
        private const float MenuBarClear = 52;

        private static RectTransform UiRect(string name, Transform parent, Vector2 min, Vector2 max, Vector2 pivot)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.gameObject.layer = parent.gameObject.layer;
            r.SetParent(parent, false);
            r.anchorMin = min; r.anchorMax = max; r.pivot = pivot;
            return r;
        }

        private static TextMeshProUGUI Text(string name, Transform parent, string text, float size, Vector2 at, Vector2 sizeDelta, TextAlignmentOptions align, Color color, bool bold)
        {
            RectTransform r = UiRect(name, parent, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            r.anchoredPosition = at; r.sizeDelta = sizeDelta;
            var t = r.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.text = bold ? "<b>" + text + "</b>" : text;
            t.fontSize = size; t.alignment = align; t.color = color; t.raycastTarget = false;
            t.enableWordWrapping = true; t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        private static readonly Image[] gizmoButtons = new Image[3];
        private static readonly string[] GizmoNames = { "MOVE", "TURN", "SIZE" };
        private static RectTransform resetRect;
        private static TextMeshProUGUI resetLabel;
        private static float resetArmedUntil;

        // The game's bar of areas along the bottom, out of the way while decorating (and back after, if it was there).
        private static bool barWasOn;
        private static void AreaBar(bool show)
        {
            try
            {
                if (overlay == null) overlay = UnityEngine.Object.FindObjectOfType<HideoutScreenOverlay>();
                GameObject bar = overlay?._areasPanel?.gameObject;
                if (bar == null) return;
                if (!show) { barWasOn = bar.activeSelf; bar.SetActive(false); }
                else if (barWasOn) { bar.SetActive(true); barWasOn = false; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Decor: the area bar: " + e.Message); }
        }

        private static void ResetClicked()
        {
            if (Time.unscaledTime > resetArmedUntil)
            {
                resetArmedUntil = Time.unscaledTime + 3;
                if (resetLabel != null) resetLabel.text = "<b>CLICK AGAIN</b>";
                return;
            }
            resetArmedUntil = 0;
            if (resetLabel != null) resetLabel.text = "<b>RESET ALL</b>";
            ResetAll();
        }

        // Every thing back where, and whose, the game has it (shown again if hidden), and every decoration gone.
        private static void ResetAll()
        {
            int things = 0, copies = decorObjects.Count;
            Checkpoint();
            foreach (string key in itemLayout.Keys.ToList())
            {
                Transform t = Resolve(key);
                if (t != null) { RestoreItem(t, key); things++; }
                else itemLayout.Remove(key);
            }
            foreach (Transform copy in decorObjects.Values) if (copy != null) UnityEngine.Object.Destroy(copy.gameObject);
            decorObjects.Clear();
            decorLayout.Clear();
            ClearSelection(); hovered = null;
            ThingsChanged();
            SaveItems(); SaveDecor();
            Outlines(); Status(); RefreshHiddenTab();
            RefreshRows();
            EFT.Communications.NotificationManager.DisplayMessageNotification($"The hideout is as the game has it: {things} thing(s) back, {copies} decoration(s) gone. Ctrl+Z brings it all back.");
            Plugin.Log.LogInfo($"Decor: reset, {things} thing(s) back, {copies} decoration(s) gone.");
        }

        private static readonly Color ButtonOn = new Color(.42f, .33f, .14f, 1), ButtonOff = new Color(.22f, .21f, .19f, 1);

        // The panel's MOVE / TURN / SIZE and the view on: lit up.
        private static void ModeButtons()
        {
            for (int i = 0; i < gizmoButtons.Length; i++)
                if (gizmoButtons[i] != null) gizmoButtons[i].color = (int)gizmoKind == i ? ButtonOn : ButtonOff;
            for (int i = 0; i < viewButtons.Length; i++)
                if (viewButtons[i] != null) viewButtons[i].color = (int)view == i ? ButtonOn : ButtonOff;
        }

        private static Image Button(RectTransform parent, string label, Vector2 at, Vector2 size, Action click, bool anchorRight = false, float fontSize = 15)
        {
            RectTransform r = UiRect("Button " + label, parent, anchorRight ? new Vector2(1, 1) : new Vector2(0, 1), anchorRight ? new Vector2(1, 1) : new Vector2(0, 1), new Vector2(0, 1));
            r.anchoredPosition = at; r.sizeDelta = size;
            Image back = r.gameObject.AddComponent<Image>();
            back.color = ButtonOff;
            TextMeshProUGUI t = Text("Label", r, label, fontSize, Vector2.zero, size, TextAlignmentOptions.Center, Ink, true);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one; t.rectTransform.offsetMin = new Vector2(2, 0); t.rectTransform.offsetMax = new Vector2(-2, 0);
            t.enableAutoSizing = true; t.fontSizeMin = 8; t.fontSizeMax = fontSize; t.enableWordWrapping = false;
            buttons.Add((r, click));
            return back;
        }

        private static void FillTab()
        {
            if (content == null) return;
            if (catalog == null && !IsRowTab(tab) && tab != "Hidden" && tab != MediaKind)
            {
                if (panelHidden) return;
                BuildCatalog();
            }
            foreach (Transform child in content) UnityEngine.Object.Destroy(child.gameObject);
            cells.Clear(); hiddenCells.Clear(); mediaCells.Clear(); mediaThumbs.Clear();
            foreach (var (rect, label, back, kind) in tabButtons)
            {
                back.color = kind == tab ? ButtonOn : new Color(.16f, .155f, .14f, 1);
                label.text = CategoryLabel(kind);
                label.color = OwnTabs.Contains(kind) || kind == MediaKind ? Amber : Ink;
            }
            // The panel's own lists: buttons and rows, not pictures.
            if (IsRowTab(tab)) { FillRows(); return; }
            int columns = Mathf.Max(1, Mathf.FloorToInt((GridWidth + Gap) / (CellWidth + Gap)));
            int n = 0;
            if (tab == "Hidden")
            {
                foreach (var entry in itemLayout.Where(e => e.Value.Hidden))
                {
                    if (query.Length > 0 && entry.Key.IndexOf(query.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase) < 0 && entry.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    RectTransform cell = Cell(n++, columns, Pretty(entry.Key.Substring(entry.Key.LastIndexOf('/') + 1)) + "\n<size=11>click to show</size>", null, out _, out _);
                    hiddenCells.Add((cell, entry.Key));
                }
            }
            else if (tab == MediaKind) n = FillMedia(columns);
            else
            {
                // Favourites and Recent pick from every kind.
                IEnumerable<Entry> own = OwnTabEntries(tab);
                foreach (Entry e in own ?? catalog)
                {
                    if (own == null && tab != "All" && e.Kind != tab) continue;
                    if (query.Length > 0 && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (e.Source == null) continue;
                    e.Cell = Cell(n++, columns, ExtraLabel(e), e.Picture, out e.Image, out e.Label);
                    if (IsFavourite(e.Key)) e.Label.color = Amber;
                    cells.Add((e.Cell, e));
                }
                if (n == 0 && own != null)
                {
                    TextMeshProUGUI none = Text("None", content, tab == FavTab
                        ? "No favourites yet. Right-click a thing in any tab to make it one: it's here from then on (its name is amber wherever it shows)."
                        : "Nothing taken from the panel yet. The things you put in show here, the last first.", 13, new Vector2(4, -6), new Vector2(GridWidth - 8, 80), TextAlignmentOptions.TopLeft, Dim, false);
                    none.overflowMode = TextOverflowModes.Overflow;
                }
            }
            int rows = (n + columns - 1) / columns;
            content.sizeDelta = new Vector2(0, rows * (CellHeight + Gap));
            content.anchoredPosition = Vector2.zero;
        }

        // ---- the Media tab: posters and screens -------------------------------------------------------------------

        // The Media tab's cells, and what a click on each does: a poster, a screen and a computer to put in blank (what's
        // on one is chosen on it, once it's in), then the media folder's files, each put in already showing.
        private static readonly List<(RectTransform rect, Action click)> mediaCells = new List<(RectTransform, Action)>();
        private static readonly List<(RawImage image, string file)> mediaThumbs = new List<(RawImage, string)>();
        private static readonly Color ScreenOff = new Color(.03f, .03f, .035f, 1);

        private static int FillMedia(int columns)
        {
            int n = 0;
            MediaCell(ref n, columns, "<b>Poster</b>\n<size=11>drag in, then click it</size>", null, () => StartPlacingMedia(PosterKey, null, true));
            MediaCell(ref n, columns, "<b>TV screen</b>\n<size=11>drag in, then click it</size>", ScreenOff, () => StartPlacingMedia(ScreenKey, null, true));
            MediaCell(ref n, columns, "<b>Computer</b>\n<size=11>drag in, then click it</size>", ScreenOff, () => StartPlacingMedia(ComputerKey, null, true));
            MediaCell(ref n, columns, "<b>Open the media folder</b>\n<size=11>your pictures and videos</size>", null, OpenMediaFolder);
            foreach (string file in MediaFiles())
            {
                if (query.Length > 0 && file.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool video = IsVideo(file);
                string chosen = file;
                RawImage image = MediaCell(ref n, columns, (video ? "<color=#ffb840>VIDEO</color> " : "") + file, video ? ScreenOff : (Color?)null,
                    () => StartPlacingMedia(video ? ScreenKey : PosterKey, chosen, true));
                if (!video) mediaThumbs.Add((image, file));
            }
            return n;
        }

        private static RawImage MediaCell(ref int n, int columns, string label, Color? colour, Action click)
        {
            RectTransform cell = Cell(n++, columns, label, null, out RawImage image, out _);
            if (colour != null) image.color = colour.Value;
            mediaCells.Add((cell, click));
            return image;
        }

        private static RectTransform Cell(int index, int columns, string name, Texture2D picture, out RawImage image, out TextMeshProUGUI label)
        {
            RectTransform r = UiRect("Cell", content, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            r.anchoredPosition = new Vector2((index % columns) * (CellWidth + Gap), -(index / columns) * (CellHeight + Gap));
            r.sizeDelta = new Vector2(CellWidth, CellHeight);
            r.gameObject.AddComponent<Image>().color = new Color(.12f, .115f, .105f, 1);
            RectTransform pic = UiRect("Picture", r, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            pic.anchoredPosition = new Vector2(3, -3); pic.sizeDelta = new Vector2(CellWidth - 6, CellHeight - 36);
            image = pic.gameObject.AddComponent<RawImage>();
            image.raycastTarget = false;
            image.texture = picture;
            image.color = picture != null ? Color.white : new Color(.18f, .17f, .15f, 1);
            label = Text("Name", r, name, 12, new Vector2(4, -(CellHeight - 32)), new Vector2(CellWidth - 8, 30), TextAlignmentOptions.Top, Ink, false);
            return r;
        }

        private static void RefreshHiddenTab()
        {
            if (ui == null) return;
            if (tab == "Hidden") FillTab();
            else foreach (var (rect, label, back, kind) in tabButtons) if (kind == "Hidden") label.text = CategoryLabel(kind);
        }

        // A category's row: its name and how many things are in it.
        private static string CategoryLabel(string kind)
        {
            if (kind == MediaKind) return $"{kind} <color=#8a877f>{MediaFiles().Count}</color>";
            if (kind == ToolsTab) return kind;
            if (kind == FavTab) return $"{kind} <color=#8a877f>{Prefs().Favourites.Count}</color>";
            if (kind == RecentTab) return $"{kind} <color=#8a877f>{Prefs().Recent.Count}</color>";
            if (kind == PlacedTab) return $"{kind} <color=#8a877f>{decorLayout.Count}</color>";
            if (kind == PrefabTab) return $"{kind} <color=#8a877f>{KeptFiles("prefabs").Count}</color>";
            if (kind == LayoutTab) return $"{kind} <color=#8a877f>{KeptFiles("layouts").Count}</color>";
            int n = kind == "Hidden" ? itemLayout.Count(e => e.Value.Hidden)
                : catalog == null ? 0 : kind == "All" ? catalog.Count(e => e.Source != null) : catalog.Count(e => e.Kind == kind && e.Source != null);
            return $"{kind} <color=#8a877f>{n}</color>";
        }

        private static bool Inside(RectTransform r, Vector2 screen) => r != null && r.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(r, screen, uiCamera);

        private static bool OverPanel(Vector2 screen) => ui != null && (Inside(panel, screen) || Inside(showTab, screen));

        // The search box: a dark field, the text typed (or what it's for) and a caret while it has the keyboard.
        private static void SearchLook()
        {
            if (searchText == null) return;
            searchBack.color = typing ? new Color(.2f, .19f, .17f, 1) : new Color(.13f, .125f, .115f, 1);
            bool caret = typing && (int)(Time.unscaledTime * 2) % 2 == 0;
            string hint = tab == MediaKind ? "Search your files..." : "Search...";
            searchText.text = query.Length == 0 && !typing ? $"<color=#8a877f>{hint}</color>" : Plain(query) + (caret ? "|" : "");
        }

        // Each frame while the box has the keyboard: letters typed go in, Backspace takes one off, Ctrl+V pastes,
        // Enter or Escape is done.
        private static void Typing()
        {
            if (!typing) return;
            typedFrame = Time.frameCount;
            string before = query;
            if (Ctrl() && Input.GetKeyDown(KeyCode.V)) query += (GUIUtility.systemCopyBuffer ?? "").Trim();
            else
                foreach (char c in Input.inputString)
                {
                    if (c == '\b') { if (query.Length > 0) query = query.Substring(0, query.Length - 1); }
                    else if (c == '\n' || c == '\r') typing = false;
                    else if (!char.IsControl(c)) query += c;
                }
            if (query.Length > 30) query = query.Substring(0, 30);
            if (query != before) FillTab();
            SearchLook();
        }

        private static void StopTyping()
        {
            if (!typing) return;
            typing = false;
            SearchLook();
        }

        private static void PanelClick(Vector2 mouse)
        {
            if (Inside(searchBox, mouse)) { typing = true; typedFrame = Time.frameCount; SearchLook(); return; }
            StopTyping();
            foreach (var (rect, click) in buttons.ToList())
                if (Inside(rect, mouse)) { click(); return; }
            if (!Inside(viewport, mouse)) return;
            foreach (var (rect, click) in mediaCells.ToList())
                if (Inside(rect, mouse)) { click(); return; }
            foreach (var (rect, key) in hiddenCells.ToList())
                if (Inside(rect, mouse))
                {
                    Transform t = Resolve(key);
                    Checkpoint();
                    if (t != null) KeepItem(t, key, OwnerOf(key), hidden: false);
                    SaveItems();
                    RefreshHiddenTab();
                    return;
                }
            foreach (var (rect, entry) in cells)
                if (Inside(rect, mouse)) { StartPlacing(entry); return; }
        }

        // A new copy of a catalog thing, carried from the panel.
        private static void StartPlacing(Entry entry)
        {
            if (entry.Source == null || !RoomForAnother(entry)) return;
            Transform copy = MakeCopy(entry.Source, DecorRoot(), false);
            if (copy == null) { EFT.Communications.NotificationManager.DisplayWarningNotification($"{entry.Name} couldn't be copied."); return; }
            Recently(entry.Key);
            // A thing with a skeleton stands in its modelling pose unless its animations play.
            if (AliveFromTheStart(entry.Source))
            {
                DecorTag tag = copy.GetComponent<DecorTag>();
                tag.Meta.Alive = true;
                ApplyMeta(copy, tag.Meta, true);
            }
            ClearSelection();
            Select(copy);
            newCopies.Add(copy);
            fromPanel = true;
            StartDrag(copy);
            Drag();
            Outlines();
        }

        private static void PanelScroll()
        {
            float wheel = Input.mouseScrollDelta.y;
            if (wheel == 0 || content == null || viewport == null) return;
            float most = Mathf.Max(0, content.sizeDelta.y - viewport.rect.height);
            content.anchoredPosition = new Vector2(0, Mathf.Clamp(content.anchoredPosition.y - wheel * (CellHeight + Gap), 0, most));
        }

        private static void BoxShow(Vector2 a, Vector2 b)
        {
            if (box == null) return;
            if ((a - b).magnitude < 6) { box.gameObject.SetActive(false); return; }
            RectTransformUtility.ScreenPointToLocalPointInRectangle(uiRoot, Vector2.Min(a, b), uiCamera, out Vector2 lo);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(uiRoot, Vector2.Max(a, b), uiCamera, out Vector2 hi);
            box.anchoredPosition = lo; box.sizeDelta = hi - lo;
            box.gameObject.SetActive(true);
        }

        private static void BoxHide() { if (box != null) box.gameObject.SetActive(false); }

        private static string AreaName(string type)
        {
            if (string.IsNullOrEmpty(type)) return "no area";
            if (IsSceneType(type)) return "the hideout (no area)";
            HideoutArea area = AreaByType(type);
            return area != null ? Name(area) : type;
        }

        private static string OwnerText(Transform t)
        {
            DecorTag tag = t.GetComponent<DecorTag>();
            if (tag != null) return "decoration, " + (tag.Id != null && decorLayout.TryGetValue(tag.Id, out DecorRecord r) ? AreaName(r.Owner) : "new");
            string key = ItemKey(null, t);
            if (key == null) { ItemOf(t, out HideoutArea area); key = ItemKey(area, t); }
            return key == null ? "" : AreaName(OwnerOf(key));
        }

        private static string BelongText(Vector3? at)
        {
            switch (belong)
            {
                case Belong.None: return "no area: stays put";
                case Belong.Own: return "the area it belongs to now";
                default:
                    if (at == null) return "the area it's put in";
                    HideoutArea area = AreaAt(at.Value);
                    return area != null ? Name(area) + " (the area it's put in)" : "the area it belongs to now (not put in another)";
            }
        }

        private static int statusFrame;
        private static void Status()
        {
            if (statusText == null) return;
            if (Using) { statusText.text = UseText(); return; }
            if (asking) { statusText.text = $"<b>{Plain(askTitle).ToUpperInvariant()}</b>\nType, then Enter · Ctrl+V: paste · Esc or a click: never mind"; return; }
            if (quickUse) { statusText.text = QuickText(); return; }
            string viewLine = ViewText();
            if (dragging)
            {
                if (Time.frameCount - statusFrame < 6) return;
                statusFrame = Time.frameCount;
                Transform first = carrying.Count > 0 ? carrying[0].Thing : null;
                Vector3? at = first != null && Bounds(first, out Bounds b) ? b.center : (Vector3?)null;
                string what = carrying.Count == 1 && first != null ? Pretty(first.name).ToUpperInvariant() : carrying.Count + " THINGS";
                string put = clickToPut ? "Click to put down" : "Let go to put down";
                string media = carrying.Count == 1 && IsMedia(first)
                    ? (tvHover != null ? $" · on {Pretty(tvHover.name)} (Alt: not fitted)" : " · flat on walls, standing on floors · R: turn on the wall")
                    : " · R: turn (Shift+R: 15°)" + (Snap.Value != SnapKind.Off ? $" · snapping to {SnapText()} (Alt: not)" : " · N: snapping");
                statusText.text = viewLine + $"<b>MOVING {what}</b> · belongs to {BelongText(at)}\n{put}{media} · T: whom it belongs to · Right-click: cancel"
                                  + (fromPanel ? " · let go over the panel: put back" : "");
                return;
            }
            selection.RemoveAll(t => t == null);
            if (axisDrag >= 0 || axisHot >= 0)
            {
                int a = axisDrag >= 0 ? axisDrag : axisHot;
                string what = selection.Count == 1 ? Pretty(selection[0].name).ToUpperInvariant() : selection.Count + " THINGS";
                SelectionPivot(out Vector3 at);
                if (gizmoKind == GizmoKind.Size)
                {
                    bool all = a == AllAxes || selection.Count > 1;
                    string along = all ? "all of it" : $"along its own {AxisNames[a]}";
                    string size = selection.Count == 1 ? SizeText(selection[0]) : $"{axisMoved:0.00} x";
                    statusText.text = viewLine + (axisDrag >= 0
                        ? $"<b>{what}</b> · {along} · {size}\nLet go to keep · Ctrl: steps of a tenth · Right-click: cancel"
                        : $"<b>{what}</b> · {size}\nDrag {(a == AllAxes ? "the middle block: all of it bigger or smaller" : all ? "a handle: all of them bigger or smaller" : $"the {AxisNames[a]} handle to stretch it along its own {AxisNames[a]}")} (Ctrl: steps of a tenth) · {GizmoKey.Value}: move arrows");
                }
                else if (gizmoKind == GizmoKind.Turn)
                    statusText.text = viewLine + (axisDrag >= 0
                        ? $"<b>{what}</b> · turning about {AxisNames[a]} {(axisMoved >= 0 ? "+" : "")}{axisMoved:0}°\nLet go to put down · Ctrl: 15° steps · Right-click: cancel"
                        : $"<b>{what}</b> · at {Where(at)}\nDrag the {AxisNames[a]} ring to turn about {AxisNames[a]} (Ctrl: 15° steps) · {GizmoKey.Value}: size handles");
                else
                    statusText.text = viewLine + (axisDrag >= 0
                        ? $"<b>{what}</b> · along {AxisNames[a]} {(axisMoved >= 0 ? "+" : "")}{axisMoved:0.00} m · at {Where(at)}\nLet go to put down · Ctrl: 5 cm steps · Right-click: cancel"
                        : $"<b>{what}</b> · at {Where(at)}\nDrag the {AxisNames[a]} arrow to move along {AxisNames[a]} only (Ctrl: 5 cm steps) · {GizmoKey.Value}: turn rings");
                return;
            }
            if (iconHovered != null)
            {
                statusText.text = viewLine + (selection.Count > 0
                    ? $"<b>{Name(iconHovered).ToUpperInvariant()}</b>\nClick: the {(selection.Count == 1 ? Pretty(selection[0].name) : selection.Count + " selected things")} belong{(selection.Count == 1 ? "s" : "")} to {Name(iconHovered)} from now on (outlined with it, moving with it)"
                    : $"<b>{Name(iconHovered).ToUpperInvariant()}</b>\nSelect things first, then click this icon to give them to {Name(iconHovered)}");
                return;
            }
            if (selection.Count > 0)
            {
                bool decor = selection.All(t => t.GetComponent<DecorTag>() != null);
                bool media = selection.Any(IsMedia), tv = selection.Any(IsTv);
                bool lit = selection.Any(HasLights);
                string what = selection.Count == 1 ? $"{Plain(NameOf(selection[0])).ToUpperInvariant()}</b> · {OwnerText(selection[0])}{MediaText(selection[0])}{MetaText(selection[0])}" : $"{selection.Count} SELECTED</b>";
                string extra = media ? (selection.Count == 1 ? IsPoster(selection[0]) ? " · click it again: paste a link" : " · click it again (U): " + (IsComputer(selection[0]) ? "use it" : "watch it") : "") + " · Ctrl+V: the copied link · P: pause · M: mute"
                    : tv ? " · Ctrl+V: a screen on it playing the copied link" : "";
                if (lit && !media) extra += " · O: light switch";
                string many = selection.Count > 1 ? " · Ctrl+G: group · Tools tab: line up, space, flip" : " · F2: name · X / Z: flip";
                statusText.text = viewLine + $"<b>{what}\nDrag · R: turn · +/-: size · {GizmoKey.Value}: {NextGizmo()} · Ctrl+D: copy{extra}{many} · L: lock · Ctrl+Z: undo · Delete: {(decor ? "remove" : "hide")} · Backspace: the game's spot";
            }
            else if (hovered != null)
                statusText.text = viewLine + $"<b>{Plain(NameOf(hovered)).ToUpperInvariant()}</b> · {OwnerText(hovered)}{MediaText(hovered)}{MetaText(hovered)}\nClick to select · Shift+click or Shift+drag to add · drag on the floor to box-select · Ctrl+A: all on screen · Esc: done";
            else
                statusText.text = viewLine + $"<b>DECOR MODE</b> · what you put down belongs to {BelongText(null)} (T to change)\nClick a thing to select · drag (Shift+drag from anywhere) to box-select · drag from the panel to add (posters, screens and computers: the Media tab) · Ctrl+V: what's copied (things, a layout, a link) · Ctrl+Z / Ctrl+Y: undo, redo · N: snapping ({SnapText()}) · V: camera views · C: photo mode · H: hide the panel · the Tools tab has the rest · Esc: done";
        }

        private static string NextGizmo() => gizmoKind == GizmoKind.Move ? "turn rings" : gizmoKind == GizmoKind.Turn ? "size handles" : "move arrows";

        // ---- pictures ------------------------------------------------------------------------------------------

        private static Camera studio;
        private static Light studioLight;
        private static RenderTexture studioTarget;
        private const int StudioLayer = 31;

        // One picture a frame, of the next thing on show in the panel without one (and the media folder's pictures).
        private static void Thumbnails()
        {
            if (ui == null || tab == "Hidden") return;
            if (tab == MediaKind)
            {
                if (mediaThumbs.Count == 0) return;
                var (image, file) = mediaThumbs[0];
                mediaThumbs.RemoveAt(0);
                LoadPicture(file, picture => { if (image != null && picture != null) { image.texture = picture; image.color = Color.white; } });
                return;
            }
            float height = viewport.rect.height, scroll = content.anchoredPosition.y;
            foreach (var (rect, entry) in cells)
            {
                if (entry.Tried) continue;
                float top = rect.anchoredPosition.y + scroll;
                if (top < -height - 10 || top - CellHeight > 10) continue;
                entry.Tried = true;
                try { entry.Picture = Picture(entry.Source); }
                catch (Exception e) { Plugin.Log.LogWarning($"Decor: no picture of {entry.Name}: {e.Message}"); }
                if (entry.Image != null && entry.Picture != null) { entry.Image.texture = entry.Picture; entry.Image.color = Color.white; }
                return;
            }
        }
        // The game's materials use its own shaders, which only draw for its own cameras (deferred, with its lighting
        // steps): for the picture each is swapped for Unity's Standard shader with the same texture, colour and normal
        // map. Shadow-only meshes (the _SHADOW_LODs) are left out, and only the most detailed LOD shows.
        private static readonly Dictionary<Material, Material> pictureMaterials = new Dictionary<Material, Material>();
        private static Shader standard;

        private static void ForThePicture(Transform copy)
        {
            if (standard == null) standard = Shader.Find("Standard");
            foreach (LODGroup g in copy.GetComponentsInChildren<LODGroup>(true)) g.ForceLOD(0);
            foreach (Renderer r in copy.GetComponentsInChildren<Renderer>(true))
            {
                if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) { r.enabled = false; continue; }
                if (standard == null) continue;
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = PictureMaterial(mats[i]);
                r.sharedMaterials = mats;
            }
        }

        private static Material PictureMaterial(Material from)
        {
            if (from == null) return null;
            if (pictureMaterials.TryGetValue(from, out Material made) && made != null) return made;
            made = new Material(standard) { name = "Hideout Interiors picture " + from.name };
            Texture main = from.HasProperty("_MainTex") ? from.GetTexture("_MainTex") : null;
            if (main == null) main = from.mainTexture;
            if (main != null) { made.mainTexture = main; made.mainTextureScale = from.HasProperty("_MainTex") ? from.mainTextureScale : Vector2.one; }
            Color colour = from.HasProperty("_Color") ? from.GetColor("_Color") : Color.white;
            colour.a = 1;
            made.color = colour;
            if (from.HasProperty("_BumpMap") && from.GetTexture("_BumpMap") is Texture bump)
            {
                made.SetTexture("_BumpMap", bump);
                made.EnableKeyword("_NORMALMAP");
            }
            made.SetFloat("_Glossiness", .25f);
            made.SetFloat("_Metallic", 0);
            // Leaves, grilles, cloth with holes: cut out where the texture is see-through.
            if (from.renderQueue >= 2450 && main != null)
            {
                made.SetFloat("_Mode", 1);
                made.SetFloat("_Cutoff", .5f);
                made.EnableKeyword("_ALPHATEST_ON");
                made.renderQueue = 2450;
            }
            return pictureMaterials[from] = made;
        }

        // A copy stood far under the hideout, lit by its own light, seen by its own camera.
        private static Texture2D Picture(Transform source)
        {
            if (source == null) return null;
            if (studio == null)
            {
                var go = new GameObject("Hideout extensions picture studio");
                if (style != null) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, style.scene);
                studio = go.AddComponent<Camera>();
                studio.enabled = false;
                studio.clearFlags = CameraClearFlags.SolidColor;
                studio.backgroundColor = new Color(.12f, .115f, .105f, 1);
                studio.cullingMask = 1 << StudioLayer;
                studio.fieldOfView = 30;
                studio.renderingPath = RenderingPath.Forward;
                studio.useOcclusionCulling = false;
                studio.allowHDR = false; studio.allowMSAA = false;
                var lightGo = new GameObject("Light");
                lightGo.transform.SetParent(go.transform, false);
                studioLight = lightGo.AddComponent<Light>();
                studioLight.type = LightType.Directional;
                studioLight.cullingMask = 1 << StudioLayer;
                studioLight.intensity = 1.3f;
                studioLight.color = new Color(1f, .95f, .88f);
                studioLight.shadows = LightShadows.None;
                studioTarget = new RenderTexture(128, 128, 16, RenderTextureFormat.ARGB32) { name = "Hideout extensions picture" };
            }
            Transform copy = MakeCopy(source, null, true);
            if (copy == null) return null;
            try
            {
                copy.position = new Vector3(0, -400, 0);
                foreach (Transform t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = StudioLayer;
                foreach (Light l in copy.GetComponentsInChildren<Light>(true)) l.enabled = false;
                ForThePicture(copy);
                if (!Bounds(copy, out Bounds b)) return null;
                Vector3 from = new Vector3(-1f, .85f, -1f).normalized;
                float radius = Mathf.Max(.05f, b.extents.magnitude);
                float distance = radius / Mathf.Sin(15 * Mathf.Deg2Rad) * 1.02f;
                studio.transform.position = b.center + from * distance;
                studio.transform.LookAt(b.center);
                studio.nearClipPlane = Mathf.Max(.01f, distance - radius * 2);
                studio.farClipPlane = distance + radius * 2;
                studioLight.transform.rotation = Quaternion.LookRotation(-(from + new Vector3(.6f, .4f, .2f)).normalized);
                studioLight.enabled = true;
                Color ambient = RenderSettings.ambientLight;
                bool fog = RenderSettings.fog;
                var ambientMode = RenderSettings.ambientMode;
                float ambientIntensity = RenderSettings.ambientIntensity;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(.5f, .49f, .47f);
                RenderSettings.ambientIntensity = 1;
                RenderSettings.fog = false;
                studio.targetTexture = studioTarget;
                try { studio.Render(); }
                finally
                {
                    RenderSettings.ambientMode = ambientMode; RenderSettings.ambientLight = ambient; RenderSettings.ambientIntensity = ambientIntensity;
                    RenderSettings.fog = fog; studioLight.enabled = false; studio.targetTexture = null;
                }
                RenderTexture before = RenderTexture.active;
                RenderTexture.active = studioTarget;
                var picture = new Texture2D(128, 128, TextureFormat.RGB24, false) { name = "Hideout extensions picture " + source.name };
                picture.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                picture.Apply();
                RenderTexture.active = before;
                // Nothing drawn (the game's shaders need what only its own camera has): no picture, the name alone.
                Color[] pixels = picture.GetPixels();
                Color back = studio.backgroundColor;
                int drawn = 0;
                for (int i = 0; i < pixels.Length; i += 7)
                    if (Mathf.Abs(pixels[i].r - back.r) + Mathf.Abs(pixels[i].g - back.g) + Mathf.Abs(pixels[i].b - back.b) > .06f) drawn++;
                if (drawn < pixels.Length / 7 / 40) { UnityEngine.Object.Destroy(picture); return null; }
                return picture;
            }
            finally { UnityEngine.Object.DestroyImmediate(copy.gameObject); }
        }
    }
}
