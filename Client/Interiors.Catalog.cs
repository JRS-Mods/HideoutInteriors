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
        private const float GridWidth = PanelWidth - 24 - SideWidth - 8;
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
                    if (t.GetComponent<DecorTag>() != null || !ForPanel(t.name)) continue;
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
                if (!ForPanel(t.name) || !MeshBounds(t, out Bounds b) || b.size.magnitude < .08f) continue;
                string name = Pretty(t.name);
                if (!seen.Add(BaseName(t.name).ToLowerInvariant())) continue;
                string key = ItemKey(area, t);
                if (key == null) continue;
                catalog.Add(new Entry { Key = key, Name = name, Kind = KindOf(t.name), Source = t });
            }
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


        // ---- the screen ----------------------------------------------------------------------------------------

        private static void BuildUi()
        {
            if (catalog == null) BuildCatalog();
            if (font == null)
                font = overlay != null ? overlay.GetComponentsInChildren<TextMeshProUGUI>(true).Select(t => t.font).FirstOrDefault(f => f != null) : null;
            if (font == null) font = UnityEngine.Object.FindObjectOfType<AreaIcon>()?._currentAreaLevel?.font;
            buttons.Clear(); tabButtons.Clear(); cells.Clear(); hiddenCells.Clear();
            typing = false;

            ui = new GameObject("Hideout extensions decor screen", typeof(RectTransform));
            var canvas = ui.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 25;
            var scaler = ui.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;
            ui.AddComponent<GraphicRaycaster>();
            uiRoot = (RectTransform)ui.transform;

            // A see-through screen over the hideout (not the overview's buttons on the left), so the game's icons don't take the clicks.
            RectTransform catcher = UiRect("Catcher", uiRoot, Vector2.zero, Vector2.one, new Vector2(.5f, .5f));
            catcher.offsetMin = new Vector2(170, 0); catcher.offsetMax = Vector2.zero;
            catcher.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .001f);

            panel = UiRect("Panel", uiRoot, new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 1));
            panel.offsetMin = new Vector2(-PanelWidth - 12, 130); panel.offsetMax = new Vector2(-12, -80);
            panel.gameObject.AddComponent<Image>().color = new Color(.06f, .06f, .055f, .93f);

            Text("Title", panel, "DECORATE", 22, new Vector2(12, -10), new Vector2(300, 30), TextAlignmentOptions.TopLeft, Ink, true);
            Button(panel, "DONE", new Vector2(-12 - 90, -10), new Vector2(90, 30), CloseDecor, anchorRight: true);
            moveButton = Button(panel, "MOVE", new Vector2(-12 - 90 - 8 - 150, -10), new Vector2(72, 30), () => { if (gizmoKind != GizmoKind.Move) SwitchGizmo(); }, anchorRight: true);
            turnButton = Button(panel, "TURN", new Vector2(-12 - 90 - 8 - 76, -10), new Vector2(72, 30), () => { if (gizmoKind != GizmoKind.Turn) SwitchGizmo(); }, anchorRight: true);
            ModeButtons();
            Text("Info", panel, "Drag a thing into the hideout to put a copy there. Point at the hideout's things to pick them out.", 14,
                new Vector2(12, -44), new Vector2(PanelWidth - 24, 36), TextAlignmentOptions.TopLeft, Dim, false);

            // The categories down the left of the things: a list has room for as many as there come to be (mods add
            // their own), where a row of tabs ran out of width. Rows get shorter if they'd run off the bottom.
            tabsRow = UiRect("Categories", panel, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 1));
            tabsRow.offsetMin = new Vector2(12, 48); tabsRow.offsetMax = new Vector2(12 + SideWidth, -124);
            var tabs = TabKinds();
            if (!tabs.Contains(tab)) tab = "All";
            // Before the canvas has laid the panel out, its height at the reference 1080 lines.
            float listHeight = panel.rect.height > 300 ? panel.rect.height - 124 - 48 : 1080 - 130 - 80 - 124 - 48;
            float rowHeight = Mathf.Clamp(listHeight / tabs.Count - 2, 16, 26);
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
                buttons.Add((r, () => { tab = kind; FillTab(); }));
            }

            viewport = UiRect("Viewport", panel, Vector2.zero, Vector2.one, new Vector2(.5f, .5f));
            // The search box, over the things.
            searchBox = UiRect("Search", panel, new Vector2(0, 1), new Vector2(1, 1), new Vector2(.5f, 1));
            searchBox.offsetMin = new Vector2(12, -116); searchBox.offsetMax = new Vector2(-12, -86);
            searchBack = searchBox.gameObject.AddComponent<Image>();
            searchText = Text("Text", searchBox, "", 14, Vector2.zero, Vector2.zero, TextAlignmentOptions.MidlineLeft, Ink, false);
            searchText.rectTransform.anchorMin = Vector2.zero; searchText.rectTransform.anchorMax = Vector2.one;
            searchText.rectTransform.offsetMin = new Vector2(10, 0); searchText.rectTransform.offsetMax = new Vector2(-10, 0);
            SearchLook();

            viewport.offsetMin = new Vector2(12 + SideWidth + 8, 48); viewport.offsetMax = new Vector2(-12, -124);

            // Everything back as the game has it, on a second click.
            Image reset = Button(panel, "RESET ALL", Vector2.zero, new Vector2(150, 30), ResetClicked);
            resetRect = reset.rectTransform;
            resetRect.anchorMin = resetRect.anchorMax = Vector2.zero; resetRect.pivot = Vector2.zero;
            resetRect.anchoredPosition = new Vector2(12, 10);
            resetLabel = reset.GetComponentInChildren<TextMeshProUGUI>();
            resetArmedUntil = 0;
            viewport.gameObject.AddComponent<RectMask2D>();
            content = UiRect("Content", viewport, new Vector2(0, 1), new Vector2(1, 1), new Vector2(.5f, 1));
            content.anchoredPosition = Vector2.zero;

            RectTransform band = UiRect("Status", uiRoot, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(.5f, 0));
            band.anchoredPosition = new Vector2(-PanelWidth / 2 - 6, 56); band.sizeDelta = new Vector2(1440, 58);
            band.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .72f);
            statusText = Text("Text", band, "", 16, Vector2.zero, Vector2.zero, TextAlignmentOptions.Center, Ink, false);
            statusText.rectTransform.anchorMin = Vector2.zero; statusText.rectTransform.anchorMax = Vector2.one;
            statusText.rectTransform.offsetMin = new Vector2(12, 4); statusText.rectTransform.offsetMax = new Vector2(-12, -4);

            box = UiRect("Box", uiRoot, new Vector2(.5f, .5f), new Vector2(.5f, .5f), Vector2.zero);
            box.gameObject.AddComponent<Image>().color = new Color(1f, .72f, .25f, .12f);
            var edge = box.gameObject.AddComponent<Outline>();
            edge.effectColor = new Color(1f, .72f, .25f, .9f); edge.effectDistance = new Vector2(1.5f, 1.5f);
            box.gameObject.SetActive(false);

            FillTab();
        }

        private static RectTransform UiRect(string name, Transform parent, Vector2 min, Vector2 max, Vector2 pivot)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
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

        private static Image moveButton, turnButton;
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
            EFT.Communications.NotificationManager.DisplayMessageNotification($"The hideout is as the game has it: {things} thing(s) back, {copies} decoration(s) gone.");
            Plugin.Log.LogInfo($"Decor: reset, {things} thing(s) back, {copies} decoration(s) gone.");
        }

        // The panel's MOVE / TURN: the gizmo's kind lit up.
        private static void ModeButtons()
        {
            if (moveButton != null) moveButton.color = gizmoKind == GizmoKind.Move ? new Color(.42f, .33f, .14f, 1) : new Color(.22f, .21f, .19f, 1);
            if (turnButton != null) turnButton.color = gizmoKind == GizmoKind.Turn ? new Color(.42f, .33f, .14f, 1) : new Color(.22f, .21f, .19f, 1);
        }

        private static Image Button(RectTransform parent, string label, Vector2 at, Vector2 size, Action click, bool anchorRight = false)
        {
            RectTransform r = UiRect("Button " + label, parent, anchorRight ? new Vector2(1, 1) : new Vector2(0, 1), anchorRight ? new Vector2(1, 1) : new Vector2(0, 1), new Vector2(0, 1));
            r.anchoredPosition = at; r.sizeDelta = size;
            Image back = r.gameObject.AddComponent<Image>();
            back.color = new Color(.22f, .21f, .19f, 1);
            TextMeshProUGUI t = Text("Label", r, label, 15, Vector2.zero, size, TextAlignmentOptions.Center, Ink, true);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one; t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;
            buttons.Add((r, click));
            return back;
        }

        private static void FillTab()
        {
            if (content == null) return;
            foreach (Transform child in content) UnityEngine.Object.Destroy(child.gameObject);
            cells.Clear(); hiddenCells.Clear();
            foreach (var (rect, label, back, kind) in tabButtons)
            {
                back.color = kind == tab ? new Color(.42f, .33f, .14f, 1) : new Color(.16f, .155f, .14f, 1);
                label.text = CategoryLabel(kind);
            }
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
            else
            {
                foreach (Entry e in catalog)
                {
                    if (tab != "All" && e.Kind != tab) continue;
                    if (query.Length > 0 && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (e.Source == null) continue;
                    e.Cell = Cell(n++, columns, ExtraLabel(e), e.Picture, out e.Image, out e.Label);
                    cells.Add((e.Cell, e));
                }
            }
            int rows = (n + columns - 1) / columns;
            content.sizeDelta = new Vector2(0, rows * (CellHeight + Gap));
            content.anchoredPosition = Vector2.zero;
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
            int n = kind == "Hidden" ? itemLayout.Count(e => e.Value.Hidden)
                : catalog == null ? 0 : kind == "All" ? catalog.Count(e => e.Source != null) : catalog.Count(e => e.Kind == kind && e.Source != null);
            return $"{kind} <color=#8a877f>{n}</color>";
        }

        private static bool Inside(RectTransform r, Vector2 screen) => r != null && r.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(r, screen, null);

        private static bool OverPanel(Vector2 screen) => ui != null && Inside(panel, screen);

        // The search box: a dark field, the text typed (or what it's for) and a caret while it has the keyboard.
        private static void SearchLook()
        {
            if (searchText == null) return;
            searchBack.color = typing ? new Color(.2f, .19f, .17f, 1) : new Color(.13f, .125f, .115f, 1);
            bool caret = typing && (int)(Time.unscaledTime * 2) % 2 == 0;
            searchText.text = query.Length == 0 && !typing ? "<color=#8a877f>Search...</color>" : query + (caret ? "|" : "");
        }

        // Each frame while the box has the keyboard: letters typed go in, Backspace takes one off, Enter or Escape is done.
        private static void Typing()
        {
            if (!typing) return;
            string before = query;
            foreach (char c in Input.inputString)
            {
                if (c == (char)8) { if (query.Length > 0) query = query.Substring(0, query.Length - 1); }
                else if (c == (char)10 || c == (char)13) { typing = false; }
                else if (!char.IsControl(c) && query.Length < 30) query += c;
            }
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
            if (Inside(searchBox, mouse)) { typing = true; SearchLook(); return; }
            StopTyping();
            foreach (var (rect, click) in buttons.ToList())
                if (Inside(rect, mouse)) { click(); return; }
            if (!Inside(viewport, mouse)) return;
            foreach (var (rect, key) in hiddenCells.ToList())
                if (Inside(rect, mouse))
                {
                    Transform t = Resolve(key);
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
            ClearSelection();
            Select(copy);
            newCopy = copy;
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
            RectTransformUtility.ScreenPointToLocalPointInRectangle(uiRoot, Vector2.Min(a, b), null, out Vector2 lo);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(uiRoot, Vector2.Max(a, b), null, out Vector2 hi);
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
            if (dragging)
            {
                if (Time.frameCount - statusFrame < 6) return;
                statusFrame = Time.frameCount;
                Transform first = carrying.Count > 0 ? carrying[0].Thing : null;
                Vector3? at = first != null && Bounds(first, out Bounds b) ? b.center : (Vector3?)null;
                string what = carrying.Count == 1 && first != null ? Pretty(first.name).ToUpperInvariant() : carrying.Count + " THINGS";
                statusText.text = $"<b>MOVING {what}</b> · belongs to {BelongText(at)}\nLet go to put down · R: turn (Shift+R: 15°) · T: change whom it belongs to · Right-click: cancel"
                                  + (newCopy != null ? " · let go over the panel: put back" : "");
                return;
            }
            selection.RemoveAll(t => t == null);
            if (axisDrag >= 0 || axisHot >= 0)
            {
                int a = axisDrag >= 0 ? axisDrag : axisHot;
                string what = selection.Count == 1 ? Pretty(selection[0].name).ToUpperInvariant() : selection.Count + " THINGS";
                SelectionPivot(out Vector3 at);
                if (gizmoKind == GizmoKind.Turn)
                    statusText.text = axisDrag >= 0
                        ? $"<b>{what}</b> · turning about {AxisNames[a]} {(axisMoved >= 0 ? "+" : "")}{axisMoved:0}°\nLet go to put down · Ctrl: 15° steps · Right-click: cancel"
                        : $"<b>{what}</b> · at {Where(at)}\nDrag the {AxisNames[a]} ring to turn about {AxisNames[a]} (Ctrl: 15° steps) · {GizmoKey.Value}: move arrows";
                else
                    statusText.text = axisDrag >= 0
                        ? $"<b>{what}</b> · along {AxisNames[a]} {(axisMoved >= 0 ? "+" : "")}{axisMoved:0.00} m · at {Where(at)}\nLet go to put down · Ctrl: 5 cm steps · Right-click: cancel"
                        : $"<b>{what}</b> · at {Where(at)}\nDrag the {AxisNames[a]} arrow to move along {AxisNames[a]} only (Ctrl: 5 cm steps) · {GizmoKey.Value}: turn rings";
                return;
            }
            if (iconHovered != null)
            {
                statusText.text = selection.Count > 0
                    ? $"<b>{Name(iconHovered).ToUpperInvariant()}</b>\nClick: the {(selection.Count == 1 ? Pretty(selection[0].name) : selection.Count + " selected things")} belong{(selection.Count == 1 ? "s" : "")} to {Name(iconHovered)} from now on (outlined with it, moving with it)"
                    : $"<b>{Name(iconHovered).ToUpperInvariant()}</b>\nSelect things first, then click this icon to give them to {Name(iconHovered)}";
                return;
            }
            if (selection.Count > 0)
            {
                bool decor = selection.All(t => t.GetComponent<DecorTag>() != null);
                string what = selection.Count == 1 ? $"{Pretty(selection[0].name).ToUpperInvariant()}</b> · {OwnerText(selection[0])}" : $"{selection.Count} SELECTED</b>";
                statusText.text = $"<b>{what}\nDrag to move · R: turn · {GizmoKey.Value}: {(gizmoKind == GizmoKind.Move ? "turn rings" : "move arrows")} · T: puts down in {BelongText(null)} · Click an area's icon: give to it · Delete: {(decor ? "remove" : "hide")} · Backspace: the game's spot · Esc: done";
            }
            else if (hovered != null)
                statusText.text = $"<b>{Pretty(hovered.name).ToUpperInvariant()}</b> · {OwnerText(hovered)}\nClick to select · Shift+click to add · drag on the floor to box-select · Esc: done";
            else
                statusText.text = $"<b>DECOR MODE</b> · what you put down belongs to {BelongText(null)} (T to change)\nPoint at a thing to outline it · click to select · drag on the floor to box-select · drag from the panel to add · Esc: done";
        }

        // ---- pictures ------------------------------------------------------------------------------------------

        private static Camera studio;
        private static Light studioLight;
        private static RenderTexture studioTarget;
        private const int StudioLayer = 31;

        // One picture a frame, of the next thing on show in the panel without one.
        private static void Thumbnails()
        {
            if (ui == null || tab == "Hidden") return;
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
