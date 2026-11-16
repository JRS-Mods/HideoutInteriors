using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutInteriors
{
    /// <summary>
    /// The panel's own tabs, beside the kinds of thing: Favourites and Recent (the things you keep reaching for),
    /// Prefabs (selections saved to drag in again), Placed (every decoration in the hideout, to pick one that's behind
    /// something), Layouts (the whole arrangement saved under a name, to swap or share) and Tools (undo, snapping,
    /// lining up, groups, locks and the rest, as buttons). Layouts and prefabs are files in
    /// BepInEx/config/HideoutInteriors, and go to and from the clipboard as text.
    /// </summary>
    internal static partial class Interiors
    {
        private const string FavTab = "Favourites", RecentTab = "Recent", PrefabTab = "Prefabs", PlacedTab = "Placed", LayoutTab = "Layouts", ToolsTab = "Tools";
        private static readonly string[] OwnTabs = { FavTab, RecentTab, PrefabTab, PlacedTab, LayoutTab, ToolsTab };

        private static bool IsRowTab(string kind) => kind == PrefabTab || kind == PlacedTab || kind == LayoutTab || kind == ToolsTab;

        private static string KeptFolder(string kind)
        {
            string path = Path.Combine(BepInEx.Paths.ConfigPath, "HideoutInteriors", kind);
            try { Directory.CreateDirectory(path); } catch (Exception) { }
            return path;
        }

        private static List<string> KeptFiles(string kind)
        {
            try { return Directory.GetFiles(KeptFolder(kind), "*.json").OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception) { return new List<string>(); }
        }

        private static string SafeName(string name)
        {
            name = (name ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
            if (name.Length > 60) name = name.Substring(0, 60).Trim();
            return name.Length == 0 ? "Untitled" : name;
        }

        private static void OpenFolder(string path)
        {
            try { System.Diagnostics.Process.Start(path); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't open {path}: {e.Message}"); }
        }

        // ---- favourites and recents ---------------------------------------------------------------------------

        private sealed class PanelPrefs
        {
            public List<string> Favourites = new List<string>(), Recent = new List<string>();
        }

        private static PanelPrefs prefs;
        private static string PrefsFile => Path.Combine(BepInEx.Paths.ConfigPath, "jrs.hideoutinteriors.panel.json");
        private const int MostRecent = 30;

        private static PanelPrefs Prefs()
        {
            if (prefs != null) return prefs;
            prefs = new PanelPrefs();
            try
            {
                if (File.Exists(PrefsFile)) prefs = JsonConvert.DeserializeObject<PanelPrefs>(File.ReadAllText(PrefsFile)) ?? prefs;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't read {PrefsFile}: {e.Message}"); }
            if (prefs.Favourites == null) prefs.Favourites = new List<string>();
            if (prefs.Recent == null) prefs.Recent = new List<string>();
            return prefs;
        }

        private static void SavePrefs()
        {
            try { File.WriteAllText(PrefsFile, JsonConvert.SerializeObject(Prefs(), Formatting.Indented)); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't save {PrefsFile}: {e.Message}"); }
        }

        private static bool IsFavourite(string key) => key != null && Prefs().Favourites.Contains(key);

        // A thing was taken from the panel: first among the recent ones.
        private static void Recently(string key)
        {
            if (key == null) return;
            List<string> recent = Prefs().Recent;
            recent.Remove(key);
            recent.Insert(0, key);
            if (recent.Count > MostRecent) recent.RemoveRange(MostRecent, recent.Count - MostRecent);
            SavePrefs();
        }

        // The things a tab of the panel's own shows (null: it isn't one of those).
        private static IEnumerable<Entry> OwnTabEntries(string kind)
        {
            if (catalog == null) return null;
            if (kind == FavTab) return catalog.Where(e => IsFavourite(e.Key));
            if (kind == RecentTab)
            {
                List<string> recent = Prefs().Recent;
                return catalog.Where(e => recent.Contains(e.Key)).OrderBy(e => recent.IndexOf(e.Key));
            }
            return null;
        }

        // A right-click on a thing in the panel: a favourite, or not one any more.
        private static void PanelRightClick(Vector2 mouse)
        {
            if (!Inside(viewport, mouse)) return;
            foreach (var (rect, entry) in cells)
            {
                if (!Inside(rect, mouse)) continue;
                List<string> favourites = Prefs().Favourites;
                bool now = !favourites.Remove(entry.Key);
                if (now) favourites.Add(entry.Key);
                SavePrefs();
                EFT.Communications.NotificationManager.DisplayMessageNotification(now ? $"{entry.Name} is a favourite: it's in the Favourites tab." : $"{entry.Name} isn't a favourite any more.");
                Refill();
                return;
            }
        }

        // ---- filling the panel again --------------------------------------------------------------------------

        // The tab filled again, scrolled to where it was.
        private static void Refill()
        {
            if (ui == null || content == null || viewport == null) return;
            float at = content.anchoredPosition.y;
            FillTab();
            float most = Mathf.Max(0, content.sizeDelta.y - viewport.rect.height);
            content.anchoredPosition = new Vector2(0, Mathf.Clamp(at, 0, most));
        }

        // Something a list tab shows changed.
        private static void RefreshRows()
        {
            if (ui == null) return;
            if (IsRowTab(tab)) Refill();
            else
                foreach (var (_, label, _, kind) in tabButtons)
                    if (OwnTabs.Contains(kind)) label.text = CategoryLabel(kind);
        }

        // ---- a box that asks for a name -----------------------------------------------------------------------

        private static bool asking;
        private static string askTitle = "", askText = "";
        private static Action<string> askDone;
        private static RectTransform askBox;
        private static TextMeshProUGUI askLabel;

        /// <summary>A box at the top of the screen that takes a line of text: Enter gives it to done, Escape or a click lets it go.</summary>
        private static void Ask(string title, string initial, Action<string> done)
        {
            if (uiRoot == null) return;
            CancelAsk(); StopTyping(); StopAddress();
            asking = true; askTitle = title; askText = initial ?? ""; askDone = done;
            typedFrame = Time.frameCount;
            askBox = UiRect("Ask", uiRoot, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2(.5f, 1));
            askBox.anchoredPosition = new Vector2(-panelW / 2, -150);
            askBox.sizeDelta = new Vector2(480, 78);
            askBox.gameObject.AddComponent<Image>().color = new Color(.06f, .06f, .055f, .97f);
            var edge = askBox.gameObject.AddComponent<Outline>();
            edge.effectColor = new Color(1f, .72f, .25f, .9f); edge.effectDistance = new Vector2(1.5f, 1.5f);
            TextMeshProUGUI head = Text("Title", askBox, title, 13, new Vector2(12, -6), new Vector2(456, 22), TextAlignmentOptions.MidlineLeft, Amber, true);
            head.enableWordWrapping = false;
            RectTransform field = UiRect("Field", askBox, new Vector2(0, 0), new Vector2(1, 0), new Vector2(.5f, 0));
            field.offsetMin = new Vector2(12, 10); field.offsetMax = new Vector2(-12, 42);
            field.gameObject.AddComponent<Image>().color = new Color(.2f, .19f, .17f, 1);
            askLabel = Text("Text", field, "", 15, Vector2.zero, Vector2.zero, TextAlignmentOptions.MidlineLeft, Ink, false);
            askLabel.rectTransform.anchorMin = Vector2.zero; askLabel.rectTransform.anchorMax = Vector2.one;
            askLabel.rectTransform.offsetMin = new Vector2(10, 0); askLabel.rectTransform.offsetMax = new Vector2(-10, 0);
            askLabel.enableWordWrapping = false;
            askBox.SetAsLastSibling();
            AskLook();
            Status();
        }

        private static void AskLook()
        {
            if (askLabel == null) return;
            askLabel.text = Plain(askText) + ((int)(Time.unscaledTime * 2) % 2 == 0 ? "|" : "") + (askText.Length == 0 ? "  <color=#8a877f>type, then Enter (Esc: never mind)</color>" : "");
        }

        // Each frame while the box is up.
        private static void AskTick()
        {
            if (!asking) return;
            if (askBox == null) { asking = false; askDone = null; return; }
            typedFrame = Time.frameCount;
            bool go = false;
            if (Ctrl() && Input.GetKeyDown(KeyCode.V)) askText += (GUIUtility.systemCopyBuffer ?? "").Trim();
            else if (Ctrl() && Input.GetKeyDown(KeyCode.A)) askText = "";
            else
                foreach (char c in Input.inputString)
                {
                    if (c == '\b') { if (askText.Length > 0) askText = askText.Substring(0, askText.Length - 1); }
                    else if (c == '\n' || c == '\r') go = true;
                    else if (!char.IsControl(c)) askText += c;
                }
            if (askText.Length > 60) askText = askText.Substring(0, 60);
            if (!go) { AskLook(); return; }
            Action<string> done = askDone;
            string text = askText;
            CancelAsk();
            try { done?.Invoke(text); }
            catch (Exception e) { Plugin.Log.LogWarning("Decor: " + e); }
        }

        private static void CancelAsk()
        {
            if (askBox != null) UnityEngine.Object.Destroy(askBox.gameObject);
            askBox = null; askLabel = null; askDone = null;
            if (!asking) return;
            asking = false;
            Status();
        }

        // ---- rows: the list tabs' buttons ---------------------------------------------------------------------

        private static float rowY;
        private const float RowHeight = 28, RowGap = 3;

        private static void FillRows()
        {
            rowY = 0;
            if (tab == ToolsTab) ToolRows();
            else if (tab == PlacedTab) PlacedRows();
            else if (tab == PrefabTab) PrefabRows();
            else if (tab == LayoutTab) LayoutRows();
            content.sizeDelta = new Vector2(0, rowY + 6);
            content.anchoredPosition = Vector2.zero;
        }

        private static void RowHeader(string text)
        {
            rowY += 6;
            TextMeshProUGUI t = Text("Header", content, text, 12, new Vector2(2, -rowY), new Vector2(GridWidth - 4, 18), TextAlignmentOptions.MidlineLeft, Dim, true);
            t.enableWordWrapping = false;
            rowY += 20;
        }

        private static void RowNote(string text)
        {
            TextMeshProUGUI t = Text("Note", content, text, 12, new Vector2(2, -rowY), new Vector2(GridWidth - 4, 34), TextAlignmentOptions.TopLeft, Dim, false);
            t.overflowMode = TextOverflowModes.Overflow;
            rowY += 38;
        }

        private static Image RowCell(float x, float width, string label, Action click, bool lit, float size, TextAlignmentOptions align)
        {
            RectTransform r = UiRect("Row " + label, content, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            r.anchoredPosition = new Vector2(x, -rowY); r.sizeDelta = new Vector2(width, RowHeight);
            Image back = r.gameObject.AddComponent<Image>();
            back.color = lit ? ButtonOn : ButtonOff;
            back.raycastTarget = false;
            TextMeshProUGUI t = Text("Label", r, label, size, Vector2.zero, r.sizeDelta, align, Ink, false);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(align == TextAlignmentOptions.Center ? 2 : 8, 0); t.rectTransform.offsetMax = new Vector2(-2, 0);
            t.enableAutoSizing = true; t.fontSizeMin = 8; t.fontSizeMax = size; t.enableWordWrapping = false;
            if (click != null) mediaCells.Add((r, click));
            return back;
        }

        // A row of buttons that share its width, after a few words saying what they're for (lit: the one that's on).
        private static void RowButtons(string caption, int lit, params (string label, Action click)[] those)
        {
            float x = 0;
            if (caption != null)
            {
                TextMeshProUGUI t = Text("Caption", content, caption, 12, new Vector2(2, -rowY), new Vector2(82, RowHeight), TextAlignmentOptions.MidlineLeft, Ink, false);
                t.enableAutoSizing = true; t.fontSizeMin = 8; t.fontSizeMax = 12; t.enableWordWrapping = false;
                x = 86;
            }
            float each = (GridWidth - x - (those.Length - 1) * RowGap) / those.Length;
            for (int i = 0; i < those.Length; i++)
                RowCell(x + i * (each + RowGap), each, "<b>" + those[i].label + "</b>", those[i].click, i == lit, 12, TextAlignmentOptions.Center);
            rowY += RowHeight + RowGap;
        }

        // One of a list: its name to click, and maybe a small button at its right end.
        private static void RowItem(string label, Action click, string side = null, Action sideClick = null, string side2 = null, Action side2Click = null)
        {
            const float sideWidth = 58;
            float taken = 0;
            if (side != null) { RowCell(GridWidth - sideWidth, sideWidth, side, sideClick, false, 11, TextAlignmentOptions.Center); taken += sideWidth + RowGap; }
            if (side2 != null) { RowCell(GridWidth - taken - sideWidth, sideWidth, side2, side2Click, false, 11, TextAlignmentOptions.Center); taken += sideWidth + RowGap; }
            RowCell(0, GridWidth - taken, label, click, false, 13, TextAlignmentOptions.MidlineLeft);
            rowY += RowHeight + RowGap;
        }

        private static bool Matches(string name) => query.Length == 0 || (name != null && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

        // ---- the Tools tab ------------------------------------------------------------------------------------

        private static void ToolRows()
        {
            RowHeader("EDIT");
            RowButtons(null, -1, ("UNDO  Ctrl+Z", Undo), ("REDO  Ctrl+Y", Redo));
            RowButtons(null, -1, ("DUPLICATE  Ctrl+D", DuplicateSelection), ("RENAME  F2", RenameSelection));

            RowHeader("SNAP WHILE CARRYING  (N · Alt: not this once)");
            RowButtons(null, (int)Snap.Value, ("OFF", () => SetSnap(SnapKind.Off)), ("GRID", () => SetSnap(SnapKind.Grid)), ("EDGES", () => SetSnap(SnapKind.Edges)), ("BOTH", () => SetSnap(SnapKind.Both)));
            RowButtons($"Grid {GridSize.Value * 100:0} cm", -1, ("SMALLER", () => GridStep(-1)), ("BIGGER", () => GridStep(1)));
            RowButtons(null, -1, ("DROP ONTO WHAT'S UNDER IT  B", DropSelection));

            RowHeader("LINE UP THE SELECTION");
            RowButtons("Along X", -1, ("LOW", () => AlignSelection(0, -1)), ("MIDDLE", () => AlignSelection(0, 0)), ("HIGH", () => AlignSelection(0, 1)));
            RowButtons("Along Y", -1, ("BOTTOMS", () => AlignSelection(1, -1)), ("MIDDLE", () => AlignSelection(1, 0)), ("TOPS", () => AlignSelection(1, 1)));
            RowButtons("Along Z", -1, ("LOW", () => AlignSelection(2, -1)), ("MIDDLE", () => AlignSelection(2, 0)), ("HIGH", () => AlignSelection(2, 1)));
            RowButtons("Space evenly", -1, ("X", () => DistributeSelection(0)), ("Y", () => DistributeSelection(1)), ("Z", () => DistributeSelection(2)));
            RowButtons("In a row", -1, ("ALONG X", () => RowSelection(0)), ("ALONG Z", () => RowSelection(2)));
            RowButtons("Flip", -1, ("ACROSS X  (X)", () => FlipSelection(0)), ("ACROSS Z  (Z)", () => FlipSelection(2)));

            RowHeader("GROUPS AND LOCKS");
            RowButtons(null, -1, ("GROUP  Ctrl+G", GroupSelection), ("UNGROUP  Ctrl+Shift+G", Ungroup));
            RowButtons(null, -1, ("LOCK / UNLOCK  L", LockSelection), ("UNLOCK ALL  Shift+L", UnlockAll));

            RowHeader("WHAT THINGS DO");
            RowButtons(null, -1, ("LIGHT SWITCH  O", SwitchLights), ("OPEN / PLAY  I", Interact), ("WANDER  J", ToggleWander));

            RowHeader("KEEP AND SHARE");
            RowButtons(null, -1, ("SAVE SELECTION AS A PREFAB  Ctrl+S", SavePrefabAsk));
            RowButtons(null, -1, ("COPY SELECTION  Ctrl+C", CopySelection), ("PASTE  Ctrl+V", Paste));
            RowButtons(null, -1, ("SAVE THIS LAYOUT...", SaveLayoutAsk), ("COPY LAYOUT", CopyLayout));

            RowHeader("CAMERA");
            RowButtons(null, -1, ("PHOTO MODE  C", EnterPhoto));
            RowNote("In photo mode: WASD and Q / E fly, right-drag looks, the wheel zooms, Space takes a picture, Shift+1 to 9 keep a camera spot, 1 to 9 fly to it, T tours them all.");
            // That note runs to a third line.
            rowY += 18;
        }

        // ---- the Placed tab -----------------------------------------------------------------------------------

        // A decoration's source, in words, for one that isn't in the hideout now.
        private static string SourceName(string source)
        {
            if (source == null) return "?";
            if (extras.TryGetValue(source, out Extra extra)) return extra.Name;
            return Pretty(source.Substring(source.LastIndexOf('/') + 1));
        }

        private static void PlacedRows()
        {
            var rows = new List<(string name, string id)>();
            foreach (var pair in decorLayout)
            {
                string name = pair.Value.Meta?.Label ?? SourceName(pair.Value.Source);
                if (Matches(name) || Matches(pair.Value.Meta?.Group)) rows.Add((name, pair.Key));
            }
            RowHeader($"DECORATIONS · {rows.Count}{(query.Length > 0 ? " found" : "")} · click one to select it");
            if (decorLayout.Count == 0) RowNote("Nothing put in yet: drag a thing in from one of the other tabs.");
            foreach (var (name, id) in rows.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase))
            {
                DecorRecord r = decorLayout[id];
                string flags = (r.Meta != null && r.Meta.Locked ? "  <color=#d0674f>LOCKED</color>" : "") + (r.Meta?.Group != null ? $"  <color=#ffb840>{Plain(GroupName(r.Meta.Group))}</color>" : "");
                string pick = id;
                RowItem($"{Plain(name)}  <color=#8a877f>{Plain(AreaName(r.Owner))}</color>{flags}", () => PickPlaced(pick, false), "LOOK", () => PickPlaced(pick, true));
            }

            var groups = new Dictionary<string, int>();
            foreach (DecorRecord r in decorLayout.Values) if (r.Meta?.Group != null) { groups.TryGetValue(r.Meta.Group, out int n); groups[r.Meta.Group] = n + 1; }
            foreach (ItemPlacement p in itemLayout.Values) if (p.Meta?.Group != null) { groups.TryGetValue(p.Meta.Group, out int n); groups[p.Meta.Group] = n + 1; }
            if (groups.Count > 0)
            {
                RowHeader($"GROUPS · {groups.Count}");
                foreach (var pair in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!Matches(GroupName(pair.Key))) continue;
                    string group = pair.Key;
                    RowItem($"{Plain(GroupName(group))}  <color=#8a877f>{pair.Value} things</color>", () => PickGroup(group));
                }
            }

            var locked = itemLayout.Where(e => e.Value.Meta != null && e.Value.Meta.Locked).ToList();
            if (locked.Count > 0)
            {
                RowHeader($"THE HIDEOUT'S OWN, LOCKED · {locked.Count}");
                foreach (var pair in locked)
                {
                    string name = pair.Value.Meta.Label ?? SourceName(pair.Key);
                    if (!Matches(name)) continue;
                    string key = pair.Key;
                    RowItem($"{Plain(name)}  <color=#d0674f>LOCKED</color>", () => PickThing(Resolve(key), false));
                }
            }
        }

        private static void PickPlaced(string id, bool look)
        {
            decorObjects.TryGetValue(id, out Transform t);
            PickThing(t, look);
        }

        // One picked from a list (Shift: added to the selection), locked or behind something or not.
        private static void PickThing(Transform t, bool look)
        {
            if (t == null || !t.gameObject.activeInHierarchy)
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification("That one isn't showing in the hideout now (the area it belongs to isn't built, or is at another level).");
                return;
            }
            if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) ClearSelection();
            Select(t);
            Outlines(); Status();
            if (look) Refocus();
        }

        private static void PickGroup(string group)
        {
            if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) ClearSelection();
            foreach (Transform t in Members(group)) if (t.gameObject.activeInHierarchy) Select(t);
            Outlines(); Status();
        }

        // ---- deleting a file: click twice ---------------------------------------------------------------------

        private static string armedFile;
        private static float armedUntil;

        private static void DeleteKept(string path)
        {
            if (armedFile != path || Time.unscaledTime > armedUntil)
            {
                armedFile = path; armedUntil = Time.unscaledTime + 4;
                EFT.Communications.NotificationManager.DisplayMessageNotification($"Click DELETE again to delete \"{Path.GetFileNameWithoutExtension(path)}\" for good.");
                return;
            }
            armedFile = null;
            try { File.Delete(path); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't delete {path}: {e.Message}"); }
            Refill();
        }

        // ---- layouts: the whole arrangement under a name ------------------------------------------------------

        private static void LayoutRows()
        {
            RowButtons(null, -1, ("SAVE THIS LAYOUT AS...", SaveLayoutAsk));
            RowButtons(null, -1, ("COPY", CopyLayout), ("PASTE", PasteLayout), ("OPEN FOLDER", () => OpenFolder(KeptFolder("layouts"))));
            List<string> files = KeptFiles("layouts");
            RowHeader($"SAVED LAYOUTS · {files.Count} · click one to load it (Ctrl+Z: yours back)");
            if (files.Count == 0) RowNote("A layout is every thing you've moved, hidden and put in. Save a few (\"cozy\", \"gym focus\") and swap between them, or send the file to someone.");
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!Matches(name)) continue;
                string path = file;
                RowItem(Plain(name), () => LoadLayoutFile(path), "DELETE", () => DeleteKept(path), "SAVE", () => SaveLayout(Path.GetFileNameWithoutExtension(path)));
            }
        }

        private static void SaveLayoutAsk()
        {
            Ask("Save this layout as", "", name =>
            {
                if (name.Trim().Length > 0) SaveLayout(name);
            });
        }

        private static void SaveLayout(string name)
        {
            string path = Path.Combine(KeptFolder("layouts"), SafeName(name) + ".json");
            TidyMeta();
            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(new Snapshot { Items = itemLayout, Decor = decorLayout }, Formatting.Indented));
                EFT.Communications.NotificationManager.DisplayMessageNotification($"Layout \"{SafeName(name)}\" saved: {itemLayout.Count} thing(s) moved or hidden, {decorLayout.Count} decoration(s). It's in the Layouts tab.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't save {path}: {e.Message}");
                EFT.Communications.NotificationManager.DisplayWarningNotification("The layout couldn't be saved (see the log).");
            }
            RefreshRows();
        }

        private static void LoadLayoutFile(string path)
        {
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't read {path}: {e.Message}"); return; }
            LoadLayout(json, Path.GetFileNameWithoutExtension(path));
        }

        // The hideout as a layout has it. What it was is a step back (Ctrl+Z).
        private static void LoadLayout(string json, string name)
        {
            if (dragging || axisDrag >= 0) return;
            Checkpoint();
            if (!ApplyState(json)) return;
            int missing = decorLayout.Count - decorObjects.Count(d => d.Value != null);
            EFT.Communications.NotificationManager.DisplayMessageNotification($"Layout \"{name}\" loaded: {itemLayout.Count} thing(s) moved or hidden, {decorLayout.Count} decoration(s)"
                + (missing > 0 ? $" ({missing} wait for an area that isn't built)" : "") + ". Ctrl+Z puts yours back.");
            Plugin.Log.LogInfo($"Decor: layout '{name}' loaded.");
        }

        private static void CopyLayout()
        {
            TidyMeta();
            GUIUtility.systemCopyBuffer = JsonConvert.SerializeObject(new Snapshot { Items = itemLayout, Decor = decorLayout });
            EFT.Communications.NotificationManager.DisplayMessageNotification("The layout is copied as text: paste it to someone, and they PASTE it in their Layouts tab.");
        }

        private static void PasteLayout()
        {
            string text = (GUIUtility.systemCopyBuffer ?? "").Trim();
            if (!LooksLikeLayout(text)) { EFT.Communications.NotificationManager.DisplayMessageNotification("What's copied isn't a layout: COPY one first (here or in someone else's game)."); return; }
            LoadLayout(text, "pasted");
        }

        private static bool LooksLikeLayout(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '{') return false;
            try
            {
                JObject o = JObject.Parse(text);
                return o["Things"] == null && (o["Items"] is JObject || o["Decor"] is JObject);
            }
            catch (Exception) { return false; }
        }

        // ---- prefabs: a selection saved to put in again ---------------------------------------------------------

        private sealed class Prefab
        {
            // Says what the text is, wherever it's pasted.
            [JsonProperty("HideoutInteriors")]
            public string Kind = "prefab";
            public string Name;
            // Each thing against the middle of the selection's underside.
            public List<DecorRecord> Things = new List<DecorRecord>();
        }

        private static void PrefabRows()
        {
            RowButtons(null, -1, ("SAVE THE SELECTION AS A PREFAB...", SavePrefabAsk));
            RowButtons(null, -1, ("COPY  Ctrl+C", CopySelection), ("PASTE  Ctrl+V", Paste), ("OPEN FOLDER", () => OpenFolder(KeptFolder("prefabs"))));
            List<string> files = KeptFiles("prefabs");
            RowHeader($"PREFABS · {files.Count} · drag one into the hideout");
            if (files.Count == 0) RowNote("A prefab is a few things kept as they stand to one another: a finished desk, a row of lockers. Select them, then SAVE.");
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!Matches(name)) continue;
                string path = file;
                RowItem(Plain(name), () => PlacePrefabFile(path), "DELETE", () => DeleteKept(path));
            }
        }

        // The selection as a prefab: decorations as they are, the hideout's own things as copies of them.
        private static Prefab PrefabOf(string name)
        {
            selection.RemoveAll(t => t == null);
            if (selection.Count == 0 || !SelectionPivot(out Vector3 pivot)) return null;
            var prefab = new Prefab { Name = name };
            foreach (Transform t in selection)
            {
                DecorTag tag = t.GetComponent<DecorTag>();
                string source = tag != null ? tag.Source : ThingKey(t);
                if (source == null) continue;
                Vector3 at = t.position - pivot;
                Quaternion turn = t.rotation;
                var record = new DecorRecord
                {
                    Source = source,
                    Position = new[] { at.x, at.y, at.z },
                    Rotation = new[] { turn.x, turn.y, turn.z, turn.w },
                };
                if (tag != null) record.Take(tag);
                else
                {
                    Vector3 size = SizeOf(t);
                    if (Mathf.Approximately(size.x, size.y) && Mathf.Approximately(size.y, size.z)) record.Scale = size.x;
                    else record.Stretch = new[] { size.x, size.y, size.z };
                    ThingMeta m = MetaOf(t, false);
                    record.Meta = m == null || m.IsDefault ? null : m.Clone();
                }
                if (record.Meta != null) record.Meta.Locked = false;
                prefab.Things.Add(record);
            }
            return prefab.Things.Count > 0 ? prefab : null;
        }

        private static void SavePrefabAsk()
        {
            selection.RemoveAll(t => t == null);
            if (selection.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Select the things to keep together first, then save them as a prefab."); return; }
            string first = selection.Count == 1 ? NameOf(selection[0]) : GroupOf(selection[0]) is string g ? GroupName(g) : "";
            Ask($"Save these {selection.Count} thing(s) as a prefab called", first, name =>
            {
                if (name.Trim().Length == 0) return;
                Prefab prefab = PrefabOf(SafeName(name));
                if (prefab == null) return;
                string path = Path.Combine(KeptFolder("prefabs"), SafeName(name) + ".json");
                try
                {
                    File.WriteAllText(path, JsonConvert.SerializeObject(prefab, Formatting.Indented));
                    EFT.Communications.NotificationManager.DisplayMessageNotification($"Prefab \"{prefab.Name}\" saved ({prefab.Things.Count} thing(s)): drag it in from the Prefabs tab.");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Couldn't save {path}: {e.Message}");
                    EFT.Communications.NotificationManager.DisplayWarningNotification("The prefab couldn't be saved (see the log).");
                }
                RefreshRows();
            });
        }

        // Ctrl+C: the selection as text, to paste again here or in someone else's game.
        private static void CopySelection()
        {
            Prefab prefab = PrefabOf("Copied");
            if (prefab == null) { EFT.Communications.NotificationManager.DisplayMessageNotification("Select things first, then Ctrl+C to copy them."); return; }
            GUIUtility.systemCopyBuffer = JsonConvert.SerializeObject(prefab);
            EFT.Communications.NotificationManager.DisplayMessageNotification($"{prefab.Things.Count} thing(s) copied: Ctrl+V puts them in (in anyone's hideout: it's text).");
        }

        // Ctrl+V: copied things are put in; a copied layout is loaded; anything else is a link for a poster or screen.
        private static void Paste()
        {
            string text = (GUIUtility.systemCopyBuffer ?? "").Trim();
            if (text.Length > 0 && text[0] == '{')
            {
                Prefab prefab = ReadPrefab(text);
                if (prefab != null) { PlacePrefab(prefab, false); return; }
                if (LooksLikeLayout(text)) { LoadLayout(text, "pasted"); return; }
            }
            UseLink(text, true);
        }

        private static Prefab ReadPrefab(string json)
        {
            try
            {
                Prefab prefab = JsonConvert.DeserializeObject<Prefab>(json);
                if (prefab?.Things == null || prefab.Kind != "prefab") return null;
                prefab.Things.RemoveAll(r => !ValidDecor(r));
                return prefab.Things.Count > 0 ? prefab : null;
            }
            catch (Exception) { return null; }
        }

        private static void PlacePrefabFile(string path)
        {
            Prefab prefab = null;
            try { prefab = ReadPrefab(File.ReadAllText(path)); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't read {path}: {e.Message}"); }
            if (prefab == null) { EFT.Communications.NotificationManager.DisplayWarningNotification($"\"{Path.GetFileNameWithoutExtension(path)}\" couldn't be read as a prefab."); return; }
            PlacePrefab(prefab, true);
        }

        // A prefab's things made and carried: from the panel until the mouse is let go, pasted until a click.
        private static void PlacePrefab(Prefab prefab, bool fromThePanel)
        {
            if (dragging || axisDrag >= 0) return;
            var made = new List<Transform>();
            var counted = new Dictionary<string, int>();
            var groups = new Dictionary<string, string>();
            int missing = 0;
            foreach (DecorRecord r in prefab.Things)
            {
                Transform source = Resolve(r.Source);
                if (source == null) { missing++; continue; }
                int limit = ExtraLimit(r.Source);
                if (limit > 0)
                {
                    counted.TryGetValue(r.Source, out int extra);
                    if (ExtraCount(r.Source) + extra >= limit) { missing++; continue; }
                    counted[r.Source] = extra + 1;
                }
                Transform copy = MakeCopy(source, DecorRoot(), false);
                if (copy == null) { missing++; continue; }
                DecorTag tag = copy.GetComponent<DecorTag>();
                r.Give(tag);
                // A group in it is a group of its own each time it's put in.
                if (tag.Meta.Group != null)
                {
                    if (!groups.TryGetValue(tag.Meta.Group, out string group)) groups[tag.Meta.Group] = group = NewGroup(GroupName(tag.Meta.Group));
                    tag.Meta.Group = group;
                }
                ApplyScale(copy);
                SetupMedia(copy);
                copy.SetPositionAndRotation(new Vector3(r.Position[0], r.Position[1], r.Position[2]), new Quaternion(r.Rotation[0], r.Rotation[1], r.Rotation[2], r.Rotation[3]));
                ApplyMeta(copy, tag.Meta, true);
                made.Add(copy);
            }
            if (missing > 0)
                EFT.Communications.NotificationManager.DisplayWarningNotification($"{missing} of its {prefab.Things.Count} thing(s) aren't in this hideout (another mod's, or more than it can have) and were left out.");
            if (made.Count == 0) return;
            ClearSelection();
            foreach (Transform t in made) Select(t);
            newCopies.AddRange(made);
            // Carried over the floor from nowhere in particular, like a thing from the panel.
            fromPanel = true;
            StartDrag(made[0]);
            fromPanel = fromThePanel;
            clickToPut = !fromThePanel;
            Drag();
            ThingsChanged();
            Outlines(); Status();
        }
    }
}
