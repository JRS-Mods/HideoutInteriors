using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutInteriors
{
    /// <summary>
    /// What's on a poster, a TV screen or a computer is chosen on it, once it's in the hideout: select one and its bar
    /// shows along the top. The bar's box takes a link (or a file in the media folder, or on a screen words to search
    /// YouTube for); LIBRARY lists the media folder's files and the links saved as channels, PREV and NEXT go through
    /// them. A screen's bar pauses, skips, mutes and sets how loud it is; every bar sizes its thing. Click a selected
    /// screen or computer again (or U, Enter, the bar's WATCH or USE) and the camera goes in front of it, the decor
    /// panel out of the way: there the mouse and keyboard work a web page, and Space, M and the arrows a video.
    /// Esc, DONE or a click off the screen comes back.
    /// </summary>
    internal static partial class Interiors
    {
        // ---- channels: links kept to come back to -------------------------------------------------------------

        private sealed class Channel { public string Name, Url; }

        private static List<Channel> channels;
        private static string ChannelsFile => Path.Combine(BepInEx.Paths.ConfigPath, "jrs.hideoutinteriors.channels.json");

        private static List<Channel> Channels()
        {
            if (channels != null) return channels;
            channels = new List<Channel>();
            try
            {
                if (File.Exists(ChannelsFile))
                    channels = (JsonConvert.DeserializeObject<List<Channel>>(File.ReadAllText(ChannelsFile)) ?? channels).Where(c => !string.IsNullOrWhiteSpace(c?.Url)).ToList();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't read {ChannelsFile}, so there are no saved channels: {e.Message}"); }
            return channels;
        }

        private static void SaveChannels()
        {
            try { File.WriteAllText(ChannelsFile, JsonConvert.SerializeObject(Channels(), Formatting.Indented)); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't save {ChannelsFile}: {e.Message}"); }
        }

        private static bool SameLink(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool IsSaved(string source) => !string.IsNullOrEmpty(source) && Channels().Any(c => SameLink(c.Url, source));

        // What one shows now: the page it's on, else what it was given.
        private static string SourceOf(MediaState s) => s.Page != null && IsUrl(s.Page.Url) ? s.Page.Url : s.Tag.Media ?? "";

        // SAVE: what's on now kept as a channel, or (kept already) not any more.
        private static void ToggleSaved(MediaState s)
        {
            string source = SourceOf(s);
            if (source.Length == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("There's nothing on it to save yet."); return; }
            if (Channels().RemoveAll(c => SameLink(c.Url, source)) == 0)
            {
                string title = s.Page != null && IsUrl(s.Page.Url) ? s.Page.Title : "";
                Channels().Add(new Channel { Name = string.IsNullOrWhiteSpace(title) ? MediaName(source) : title.Trim(), Url = source });
                EFT.Communications.NotificationManager.DisplayMessageNotification("Saved: it's in every LIBRARY, and PREV / NEXT go through what's saved.");
            }
            SaveChannels();
        }

        private struct LibraryEntry { public string Label, Source; public bool Saved; }

        // What can be put on one: the saved channels, then the media folder's files (pictures only on a poster; a
        // computer has only the channels, as bookmarks).
        private static List<LibraryEntry> Library(MediaState s)
        {
            var list = new List<LibraryEntry>();
            foreach (Channel c in Channels())
                list.Add(new LibraryEntry { Label = string.IsNullOrWhiteSpace(c.Name) ? MediaName(c.Url) : c.Name, Source = c.Url, Saved = true });
            if (s.Tag.Source == ComputerKey) return list;
            foreach (string file in MediaFiles())
                if (s.Screen || !IsVideo(file)) list.Add(new LibraryEntry { Label = file, Source = file });
            return list;
        }

        // PREV and NEXT: the one before or after what's on now.
        private static void StepChannel(MediaState s, int step)
        {
            var list = Library(s);
            if (list.Count == 0)
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification("Nothing to go through yet: SAVE keeps what's on now as a channel, and the media folder's files are channels too.");
                return;
            }
            // With a list of its own, PREV and NEXT go through that.
            if (HasList(s.Tag)) { ListStep(s, step); return; }
            string now = SourceOf(s);
            int at = list.FindIndex(e => SameLink(e.Source, now));
            if (at < 0 && step < 0) at = 0;
            PutOn(s, list[((at + step) % list.Count + list.Count) % list.Count].Source);
        }

        // PASTE: the copied link (or file name, or words) goes on it.
        private static void PasteLink()
        {
            string copied = (GUIUtility.systemCopyBuffer ?? "").Trim();
            if (copied.Length == 0) EFT.Communications.NotificationManager.DisplayMessageNotification("Copy a link first (in your browser: right-click a picture or video, copy its address), then PASTE.");
            else GoTo(barFor, copied);
        }

        private static void PutOn(MediaState s, string source)
        {
            SetSource(s.Thing, source);
            BarLook();
        }

        // What's typed in the bar's box: a link, a file in the media folder, an address without its https://, or (on
        // a screen) words to search YouTube for. A computer's browser sorts its own out.
        private static void GoTo(MediaState s, string text)
        {
            text = (text ?? "").Trim().Trim('"');
            if (text.Length == 0 || s?.Thing == null) return;
            string source = text;
            if (s.Tag.Source != ComputerKey && !IsUrl(text) && !File.Exists(MediaPath(text)))
            {
                string type = TypeOf(text);
                if (text.IndexOf(' ') < 0 && (PictureTypes.Contains(type) || VideoTypes.Contains(type)))
                {
                    EFT.Communications.NotificationManager.DisplayWarningNotification($"There's no {text} in the media folder.");
                    return;
                }
                if (text.IndexOf(' ') < 0 && text.IndexOf('.') > 0) source = "https://" + text;
                else if (s.Screen) source = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(text);
                else
                {
                    EFT.Communications.NotificationManager.DisplayWarningNotification("A poster needs a link to a picture (.png or .jpg), or a file from the media folder: LIBRARY lists them.");
                    return;
                }
            }
            PutOn(s, source);
        }

        // ---- using one: the camera in front of it -------------------------------------------------------------

        private static MediaState usingMedia;
        private static bool Using => usingMedia != null;
        private static bool viewBeforeUse, panelHiddenForUse;

        // How much of the screen's height or width one fills while it's used (the bar's above it, the keys below).
        private const float UseFill = .76f;

        // U or Enter, or a click on the one thing selected: a screen or computer is used, a poster's box takes a link.
        private static void EnterMedia(Transform t)
        {
            if (t == null || !mediaStates.TryGetValue(t, out MediaState s)) return;
            if (s.Screen) StartUsing(s);
            else addressWanted = t;
        }

        private static void UseSelection(bool say)
        {
            Transform media = selection.FirstOrDefault(t => t != null && mediaStates.ContainsKey(t));
            if (media != null) EnterMedia(media);
            else if (say) EFT.Communications.NotificationManager.DisplayMessageNotification("Select a TV screen or a computer, then press U (or click it again) to use it.");
        }

        private static void StartUsing(MediaState s)
        {
            if (s?.Thing == null || dragging || axisDrag >= 0) return;
            usingMedia = s;
            StopTyping(); StopAddress();
            pageMouse = new Vector2(-1, -1); pageHeld = 0;
            viewBeforeUse = ViewActive;
            if (selection.Count != 1 || selection[0] != s.Thing) { ClearSelection(); Select(s.Thing); }
            hovered = null;
            pressing = boxing = pressingToggle = false;
            BoxHide();
            HideGizmo();
            ItemOutline.Clear();
            DecorTip(null);
            // The panel's where the screen will be: out of the way until done.
            panelHiddenForUse = !panelHidden;
            if (panelHiddenForUse) { panelHidden = true; PanelShown(); }
            // In first person the player's camera is drawn from in front of it instead.
            if (quickFp) StartFpFrame(s);
            else FrameMedia(s);
            Status();
        }

        // The camera square in front of one, its face filling most of the view.
        private static void FrameMedia(MediaState s)
        {
            Transform face = s.Face;
            if (face == null) return;
            Vector3 size = face.lossyScale, outward = s.Thing.forward;
            Camera camera = ViewCamera();
            float fov = viewCamera != null ? viewCamera.m_Lens.FieldOfView : Mathf.Clamp(camera != null ? camera.fieldOfView : 50, 40, 60);
            float tan = Mathf.Tan(fov * Mathf.Deg2Rad / 2), aspect = Screen.width / (float)Mathf.Max(1, Screen.height);
            float distance = Mathf.Max(Mathf.Abs(size.y) / 2 / tan, Mathf.Abs(size.x) / 2 / (tan * aspect)) / UseFill;
            FlyTo(face.position, Mathf.Atan2(-outward.x, -outward.z) * Mathf.Rad2Deg, Mathf.Asin(Mathf.Clamp(outward.y, -1, 1)) * Mathf.Rad2Deg, Mathf.Max(.25f, distance));
        }

        private static void StopUsing()
        {
            MediaState s = usingMedia;
            if (s == null) return;
            usingMedia = null;
            StopAddress();
            CloseLibrary();
            WebPage page = s.Page;
            if (page != null && s.Thing != null)
            {
                // A video page shown whole (to sign in, to pick something): the video alone again.
                if (page.Video && page.Whole) { page.Whole = false; BrowserSend("cmd", page.Id, "focus"); }
                // It comes back on the page it was left on.
                if (IsUrl(page.Url) && s.Tag.Media != page.Url) { s.Tag.Media = page.Url; KeepMedia(s.Tag); }
            }
            if (panelHiddenForUse && panelHidden) { panelHidden = false; PanelShown(); }
            panelHiddenForUse = false;
            if (!viewBeforeUse) ExitView();
            EndFpFrame();
            // Opened just to use it: done is done.
            if (quickUse) quickDone = true;
            if (decorOn) { Outlines(); Status(); }
        }

        // Where the mouse is on one's face: 0, 0 its top left, 1, 1 its bottom right.
        private static bool OnFace(MediaState s, out Vector2 on)
        {
            on = default;
            Camera camera = ViewCamera();
            Transform face = s.Face;
            if (camera == null || face == null) return false;
            Ray ray = MouseRay(camera);
            if (!new Plane(face.forward, face.position).Raycast(ray, out float d)) return false;
            Vector3 local = face.InverseTransformPoint(ray.GetPoint(d));
            if (Mathf.Abs(local.x) > .5f || Mathf.Abs(local.y) > .5f) return false;
            on = new Vector2(.5f - local.x, .5f - local.y);
            return true;
        }

        // Each frame while one's used: the mouse on its page and the keys to it (or to its video).
        private static void UseTick(bool overPanel, bool overBar)
        {
            MediaState s = usingMedia;
            if (s.Thing == null || !s.Thing.gameObject.activeInHierarchy) { StopUsing(); return; }
            Vector2 mouse = Input.mousePosition;
            if (Input.GetMouseButtonDown(0))
            {
                if (BarClick(mouse)) return;
                // The panel, or the tab that brings it back: done here, and the click is the panel's (unless being done
                // brought the panel back already).
                if (overPanel)
                {
                    bool tucked = panelHidden;
                    StopUsing();
                    if (tucked == panelHidden) PanelClick(mouse);
                    return;
                }
                StopAddress();
            }
            Vector2 on = default;
            bool onFace = !overPanel && !overBar && OnFace(s, out on);
            if (s.Page != null) PageMouse(s.Page, onFace, on);
            if (overPanel) { PanelScroll(); return; }
            if (overBar) { LibraryScroll(); return; }
            if (!onFace && Input.GetMouseButtonDown(0)) { StopUsing(); return; }
            if (KeysTaken) return;
            if (s.Page != null) PageKeys(s.Page);
            else ScreenKeys(s);
        }

        // A video's keys: Space pauses, M mutes, left and right skip, up and down are louder and quieter.
        private static void ScreenKeys(MediaState s)
        {
            if (!Input.anyKeyDown) return;
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.P)) SetPaused(s, !IsPaused(s));
            else if (Input.GetKeyDown(KeyCode.M)) { SetMuted(s, !s.Tag.Muted); KeepMedia(s.Tag); }
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) Seek(s, -10);
            else if (Input.GetKeyDown(KeyCode.RightArrow)) Seek(s, 10);
            else if (Input.GetKeyDown(KeyCode.UpArrow)) { SetVolume(s, s.Tag.Volume + .1f); KeepMedia(s.Tag); }
            else if (Input.GetKeyDown(KeyCode.DownArrow)) { SetVolume(s, s.Tag.Volume - .1f); KeepMedia(s.Tag); }
            else if (Input.GetKeyDown(KeyCode.Comma)) StepChannel(s, -1);
            else if (Input.GetKeyDown(KeyCode.Period)) StepChannel(s, 1);
            else return;
            BarLook();
        }

        private static string UseText()
        {
            MediaState s = usingMedia;
            if (s == null) return "";
            if (s.Tag.Source == ComputerKey)
                return "<b>USING THE COMPUTER</b> · the mouse and keyboard work the page · click the bar's box (Ctrl+L) for an address or a search\nEsc, DONE or a click off the screen: done · wheel: scroll";
            if (s.Page != null)
                return "<b>WATCHING THE SCREEN</b> · the mouse and keyboard work the page · the bar's box takes a link, or words to search YouTube for\nWHOLE PAGE shows the page round the video (to sign in, to pick another) · Esc, DONE or a click off the screen: done";
            return "<b>WATCHING THE SCREEN</b> · Space: pause · left / right: skip 10 s · up / down: volume · M: mute · , and .: the channel before / after\nThe bar's box takes a link, a file from the media folder, or words to search YouTube for · Esc, DONE or a click off the screen: done";
        }

        // ---- the bar ------------------------------------------------------------------------------------------

        private static GameObject bar;
        private static MediaState barFor;
        private static bool barUsing, barPanelHidden;
        private static RectTransform barRect, addressRect, volumeRect, volumeFill;
        private static TextMeshProUGUI addressText, volumeText, sizeText, clockText;
        private static Image addressBack;
        private static float barWidth, barX, barY;
        private static bool volumeHeld;
        private static readonly List<(RectTransform rect, Action click)> barButtons = new List<(RectTransform, Action)>();
        // The buttons whose look says something (PAUSE or PLAY, MUTE lit...), by name.
        private static readonly Dictionary<string, (Image back, TextMeshProUGUI label)> barParts = new Dictionary<string, (Image, TextMeshProUGUI)>();

        // The bar's box: what's typed, whether it has the keyboard, and a thing whose box is to take it once its bar's up.
        private static string address = "";
        private static bool addressTyping;
        private static Transform addressWanted;
        // The frame a box last had the keyboard: its Enter isn't decor mode's too.
        private static int typedFrame = -1;

        // A box (the search, or a bar's) has the keyboard: decor mode's keys and the camera's wait.
        private static bool KeysTaken => typing || addressTyping || asking || typedFrame == Time.frameCount;

        private const float BarRow = 34, BarButtonHeight = 30, VolumeWidth = 100;

        // Each frame of decor mode: the bar for the one poster, screen or computer selected (or used), kept up to date.
        private static void MediaBarTick()
        {
            MediaState want = usingMedia;
            if (want == null && !dragging && selection.Count == 1 && selection[0] != null) mediaStates.TryGetValue(selection[0], out want);
            if (want != null && want.Thing == null) want = null;
            bool stale = bar == null ? want != null : want != barFor || Using != barUsing || panelHidden != barPanelHidden;
            if (stale) BuildBar(want);
            if (bar == null) { addressWanted = null; return; }
            if (addressWanted != null)
            {
                if (addressWanted == barFor.Thing) StartAddress();
                addressWanted = null;
            }
            else if (addressTyping) AddressTick();
            VolumeTick();
            if (Time.frameCount % 15 == 0) BarLook();
        }

        // No bar any more (decor mode closed, the hideout left).
        private static void BarGone()
        {
            ClearBar();
            addressWanted = null;
        }

        private static void ClearBar()
        {
            if (bar != null) UnityEngine.Object.Destroy(bar);
            bar = null; barFor = null;
            barRect = addressRect = volumeRect = volumeFill = null;
            addressText = volumeText = sizeText = clockText = null;
            barButtons.Clear(); barParts.Clear();
            addressTyping = volumeHeld = false;
            CloseLibrary();
        }

        private static bool OverBar(Vector2 screen) =>
            (bar != null && (Inside(barRect, screen) || Inside(libraryRect, screen))) || (stripRect != null && Inside(stripRect, screen)) || (askBox != null && Inside(askBox, screen));

        private static void BuildBar(MediaState s)
        {
            ClearBar();
            if (s == null || uiRoot == null) return;
            barFor = s; barUsing = Using; barPanelHidden = panelHidden;
            bool computer = s.Tag.Source == ComputerKey, poster = !s.Screen;
            // Left of the panel, or (it's tucked away) of the tab that brings it back.
            barWidth = Mathf.Max(360, builtSize.x - 180 - (panelHidden ? 12 + showTab.sizeDelta.x + 8 : panelW + 24));
            barRect = UiRect("Media bar", uiRoot, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            barRect.anchoredPosition = new Vector2(180, -12);
            barRect.gameObject.AddComponent<Image>().color = new Color(.06f, .06f, .055f, .94f);
            bar = barRect.gameObject;

            // The first row: what it is, the box, PASTE and LIBRARY.
            const float kindWidth = 112, pasteWidth = 64, libraryWidth = 84;
            Text("Kind", barRect, computer ? "COMPUTER" : poster ? "POSTER" : "TV SCREEN", 15, new Vector2(10, -6), new Vector2(kindWidth - 10, BarButtonHeight), TextAlignmentOptions.MidlineLeft, Amber, true);
            barY = 6;
            barX = barWidth - 6 - libraryWidth;
            BarButton("LIBRARY", libraryWidth, ToggleLibrary, "LIBRARY");
            barX = barWidth - 6 - libraryWidth - 4 - pasteWidth;
            BarButton("PASTE", pasteWidth, PasteLink);
            addressRect = UiRect("Address", barRect, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            addressRect.anchoredPosition = new Vector2(kindWidth, -6);
            addressRect.sizeDelta = new Vector2(barWidth - kindWidth - 6 - libraryWidth - 4 - pasteWidth - 4, BarButtonHeight);
            addressBack = addressRect.gameObject.AddComponent<Image>();
            addressText = Text("Text", addressRect, "", 14, Vector2.zero, Vector2.zero, TextAlignmentOptions.MidlineLeft, Ink, false);
            addressText.rectTransform.anchorMin = Vector2.zero; addressText.rectTransform.anchorMax = Vector2.one;
            addressText.rectTransform.offsetMin = new Vector2(10, 0); addressText.rectTransform.offsetMax = new Vector2(-10, 0);
            addressText.enableWordWrapping = false;

            // Under it, as many rows as the buttons need.
            barX = 6; barY += BarRow;
            if (computer)
            {
                BarButton("BACK", 54, () => PageCommand("back"), "BACK");
                BarButton("FORWARD", 78, () => PageCommand("forward"), "FORWARD");
                BarButton("RELOAD", 66, () => PageCommand("reload"), "RELOAD");
                BarButton("HOME", 56, () => PutOn(barFor, Homepage));
                barX += 8;
            }
            else if (!poster)
            {
                BarButton("PAUSE", 62, () => SetPaused(barFor, !IsPaused(barFor)), "PAUSE");
                BarButton("-10 s", 50, () => Seek(barFor, -10), "BACK10");
                BarButton("+10 s", 50, () => Seek(barFor, 10), "ON10");
                clockText = BarText(92);
                barX += 8;
            }
            if (!poster)
            {
                BarButton("MUTE", 62, () => { SetMuted(barFor, !barFor.Tag.Muted); KeepMedia(barFor.Tag); }, "MUTE");
                BarVolume();
                barX += 8;
            }
            if (!computer)
            {
                BarButton("PREV", 52, () => StepChannel(barFor, -1));
                BarButton("NEXT", 52, () => StepChannel(barFor, 1));
            }
            BarButton("SAVE", 60, () => ToggleSaved(barFor), "SAVE");
            barX += 8;
            BarButton("SMALLER", 74, () => SizeFromBar(1 / 1.1f));
            BarButton("BIGGER", 66, () => SizeFromBar(1.1f));
            if (!computer) BarButton("OWN SHAPE", 88, OwnShape, "SHAPE");
            sizeText = BarText(104);
            if (!computer)
            {
                // Its frame, and a list to go through: a slideshow on a poster, a playlist on a screen.
                BarButton("FRAME", 112, () => NextFrame(barFor), "FRAME");
                barX += 8;
                BarButton("ADD TO LIST", 96, () => ListAdd(barFor), "LISTADD");
                BarButton("ALL FILES", 80, () => ListAllFiles(barFor));
                BarButton("EVERY 10 s", 92, () => ListTime(barFor), "EVERY");
                BarButton("NO LIST", 96, () => ListClear(barFor), "LISTCLEAR");
                barX += 8;
            }
            if (!poster && !computer)
            {
                BarButton("WHOLE PAGE", 96, TogglePage, "PAGE");
                BarButton("BACK", 54, () => PageCommand("back"), "BACK");
                BarButton("RELOAD", 66, () => PageCommand("reload"), "RELOAD");
                barX += 8;
            }
            if (!computer) BarButton("FOLDER", 68, OpenMediaFolder);
            if (!poster)
            {
                Image enter = Using ? BarButton("DONE", 64, StopUsing) : BarButton(computer ? "USE" : "WATCH", 70, () => StartUsing(barFor));
                enter.color = ButtonOn;
            }
            barRect.sizeDelta = new Vector2(barWidth, barY + BarRow + 2);
            BarLook();
        }

        // A button at the row's next place (on the next row when this one's full).
        private static Image BarButton(string label, float width, Action click, string name = null)
        {
            if (barX + width > barWidth - 5.5f && barX > 6) { barX = 6; barY += BarRow; }
            RectTransform r = UiRect("Button " + label, barRect, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            r.anchoredPosition = new Vector2(barX, -barY); r.sizeDelta = new Vector2(width, BarButtonHeight);
            Image back = r.gameObject.AddComponent<Image>();
            back.color = ButtonOff;
            TextMeshProUGUI t = Text("Label", r, label, 13, Vector2.zero, r.sizeDelta, TextAlignmentOptions.Center, Ink, true);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(2, 0); t.rectTransform.offsetMax = new Vector2(-2, 0);
            t.enableAutoSizing = true; t.fontSizeMin = 8; t.fontSizeMax = 13; t.enableWordWrapping = false;
            barButtons.Add((r, click));
            if (name != null) barParts[name] = (back, t);
            barX += width + 4;
            return back;
        }

        // A few words at the row's next place.
        private static TextMeshProUGUI BarText(float width)
        {
            if (barX + width > barWidth - 5.5f && barX > 6) { barX = 6; barY += BarRow; }
            TextMeshProUGUI t = Text("Text", barRect, "", 13, new Vector2(barX + 4, -barY), new Vector2(width - 4, BarButtonHeight), TextAlignmentOptions.MidlineLeft, Dim, false);
            t.enableWordWrapping = false;
            barX += width + 4;
            return t;
        }

        // How loud: a track to click or drag along, and the number.
        private static void BarVolume()
        {
            const float numberWidth = 44;
            if (barX + VolumeWidth + numberWidth > barWidth - 5.5f && barX > 6) { barX = 6; barY += BarRow; }
            volumeRect = UiRect("Volume", barRect, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            volumeRect.anchoredPosition = new Vector2(barX, -barY); volumeRect.sizeDelta = new Vector2(VolumeWidth, BarButtonHeight);
            RectTransform track = UiRect("Track", volumeRect, new Vector2(0, .5f), new Vector2(1, .5f), new Vector2(.5f, .5f));
            track.sizeDelta = new Vector2(0, 12);
            track.gameObject.AddComponent<Image>().color = new Color(.2f, .19f, .17f, 1);
            volumeFill = UiRect("Fill", track, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, .5f));
            volumeFill.gameObject.AddComponent<Image>().color = Amber;
            volumeText = Text("Number", barRect, "", 13, new Vector2(barX + VolumeWidth + 4, -barY), new Vector2(numberWidth, BarButtonHeight), TextAlignmentOptions.MidlineLeft, Ink, false);
            barX += VolumeWidth + numberWidth + 4;
        }

        // A click on the bar or its library (true: it was theirs).
        private static bool BarClick(Vector2 mouse)
        {
            if (bar == null) return false;
            if (libraryRect != null)
            {
                if (Inside(libraryRect, mouse)) { LibraryClick(mouse); return true; }
                // A click anywhere else puts the list away (LIBRARY itself does it below).
                if (!barParts.TryGetValue("LIBRARY", out var library) || !Inside(library.back.rectTransform, mouse)) CloseLibrary();
            }
            if (!Inside(barRect, mouse)) return false;
            if (Inside(addressRect, mouse)) { StartAddress(); return true; }
            StopAddress();
            if (Inside(volumeRect, mouse)) { volumeHeld = true; VolumeTick(); return true; }
            foreach (var (rect, click) in barButtons)
                if (Inside(rect, mouse)) { click(); break; }   // (the click may have built the bar again)
            BarLook();
            return true;
        }

        // While the volume's track is held: as loud as where the mouse is along it; kept when it's let go.
        private static void VolumeTick()
        {
            if (!volumeHeld || barFor == null || volumeRect == null) { volumeHeld = false; return; }
            if (!Input.GetMouseButton(0))
            {
                volumeHeld = false;
                KeepMedia(barFor.Tag);
                return;
            }
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(volumeRect, Input.mousePosition, uiCamera, out Vector2 at)) return;
            float volume = Mathf.Clamp01((at.x - volumeRect.rect.xMin) / Mathf.Max(1, volumeRect.rect.width));
            if (Mathf.Abs(volume - barFor.Tag.Volume) < .005f) return;
            SetVolume(barFor, volume);
            BarLook();
        }

        private static void PageCommand(string what)
        {
            if (barFor?.Page != null) BrowserSend("cmd", barFor.Page.Id, what);
        }

        // A video page: the whole page (to click about in), or its video alone filling the screen.
        private static void TogglePage()
        {
            WebPage page = barFor?.Page;
            if (page == null || !page.Video) return;
            page.Whole = !page.Whole;
            BrowserSend("cmd", page.Id, page.Whole ? "page" : "focus");
        }

        // SMALLER and BIGGER: the bar's thing (it's the selection), and the camera back in front of it.
        private static void SizeFromBar(float by)
        {
            MediaState s = barFor;
            if (s?.Thing == null) return;
            if (selection.Count != 1 || selection[0] != s.Thing) { ClearSelection(); Select(s.Thing); }
            ScaleSelection(by);
            if (Using) { HideGizmo(); ItemOutline.Clear(); FrameMedia(s); }
        }

        // OWN SHAPE: a face stretched (or fitted to a TV) goes back to its picture's or video's shape.
        private static void OwnShape()
        {
            MediaState s = barFor;
            if (s?.Thing == null || s.Tag.Height <= 0) return;
            s.Tag.Height = 0;
            // A page has no shape of its own: a screen's (or a poster's) usual one.
            if (s.Page != null) s.Aspect = 0;
            Relayout(s);
            Reshaped(s.Thing);
            KeepMedia(s.Tag);
            if (Using) FrameMedia(s);
        }

        // What the bar says now: the box, which buttons are lit and what they'd do, how loud, how big.
        private static void BarLook()
        {
            if (bar == null || barFor == null || barFor.Thing == null) return;
            MediaState s = barFor;
            WebPage page = s.Page;
            addressBack.color = addressTyping ? new Color(.2f, .19f, .17f, 1) : new Color(.13f, .125f, .115f, 1);
            if (addressTyping)
            {
                string tail = address.Length > 110 ? "..." + address.Substring(address.Length - 107) : address;
                addressText.text = Plain(tail) + ((int)(Time.unscaledTime * 2) % 2 == 0 ? "|" : "");
            }
            else
            {
                string source = SourceOf(s);
                string title = page != null && page.Title.Length > 0 && IsUrl(page.Url) ? (page.Title.Length > 50 ? page.Title.Substring(0, 47) + "..." : page.Title) : "";
                if (source.Length == 0)
                    addressText.text = $"<color=#8a877f>{(s.Tag.Source == ComputerKey ? "Click here: an address or a search" : s.Screen ? "Click here: a link to a video, YouTube, Twitch, a stream... or words to search YouTube for (Ctrl+V pastes)" : "Click here and paste a link to a picture (Ctrl+V), or pick one of your files from LIBRARY")}</color>";
                else
                    addressText.text = (title.Length > 0 ? $"<b>{Plain(title)}</b>  " : "") + $"<color=#8a877f>{Plain(source)}</color>";
            }
            bool plays = Plays(s), paused = IsPaused(s);
            Part("PAUSE", plays && paused, paused ? "PLAY" : "PAUSE", plays);
            Part("BACK10", false, null, plays);
            Part("ON10", false, null, plays);
            Part("MUTE", s.Tag.Muted, s.Tag.Muted ? "MUTED" : "MUTE", true);
            bool saved = IsSaved(SourceOf(s));
            Part("SAVE", saved, saved ? "SAVED" : "SAVE", true);
            Part("LIBRARY", libraryRect != null, null, true);
            Part("SHAPE", false, null, s.Tag.Height > 0);
            int frame = Mathf.Clamp(s.Tag.Frame, 0, Frames.Length - 1), listed = s.Tag.Playlist != null ? s.Tag.Playlist.Length : 0;
            Part("FRAME", frame > 0, "FRAME: " + Frames[frame].name, true);
            Part("LISTADD", listed > 0 && s.Tag.Playlist.Any(e => SameLink(e, SourceOf(s))), null, true);
            Part("EVERY", false, $"EVERY {(ListSeconds(s.Tag) >= 60 ? ListSeconds(s.Tag) / 60 + " min" : ListSeconds(s.Tag) + " s")}", listed > 1);
            Part("LISTCLEAR", listed > 1, listed > 0 ? $"LIST OF {listed}: CLEAR" : "NO LIST", listed > 0);
            Part("PAGE", page != null && page.Whole, page != null && page.Whole ? "VIDEO ONLY" : "WHOLE PAGE", page != null && page.Video);
            Part("BACK", false, null, page != null);
            Part("FORWARD", false, null, page != null);
            Part("RELOAD", false, null, page != null);
            if (volumeFill != null)
            {
                float volume = s.Tag.Muted ? 0 : Mathf.Clamp01(s.Tag.Volume);
                volumeFill.sizeDelta = new Vector2(VolumeWidth * volume, 0);
                volumeText.text = $"{Mathf.Clamp01(s.Tag.Volume) * 100:0}%";
            }
            if (sizeText != null) sizeText.text = SizeText(s.Thing);
            if (clockText != null)
                clockText.text = s.Video != null && s.Video.isPrepared && s.Video.length > 0 ? $"{Clock(s.Video.time)} / {Clock(s.Video.length)}" : "";
        }

        private static void Part(string name, bool lit, string label, bool works)
        {
            if (!barParts.TryGetValue(name, out var part) || part.back == null) return;
            part.back.color = lit ? ButtonOn : ButtonOff;
            part.label.color = works ? Ink : Dim;
            if (label != null) part.label.text = "<b>" + label + "</b>";
        }

        // Text as it is, not read as TextMeshPro's tags.
        private static string Plain(string s) => s.Replace("<", "<​");

        private static string Clock(double seconds)
        {
            int s = Mathf.Max(0, (int)seconds);
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }

        // ---- the bar's box ------------------------------------------------------------------------------------

        private static void StartAddress()
        {
            if (bar == null || barFor == null) return;
            StopTyping();
            addressTyping = true;
            typedFrame = Time.frameCount;
            address = SourceOf(barFor);
            BarLook();
        }

        private static void StopAddress()
        {
            if (!addressTyping) return;
            addressTyping = false;
            BarLook();
        }

        // Each frame while the box has the keyboard: letters go in, Backspace takes one off, Ctrl+V puts the copied
        // link in its place, Ctrl+A empties it, Enter goes there (Escape lets it go).
        private static void AddressTick()
        {
            typedFrame = Time.frameCount;
            string before = address;
            bool go = false;
            if (Ctrl() && Input.GetKeyDown(KeyCode.V)) address = (GUIUtility.systemCopyBuffer ?? "").Trim();
            else if (Ctrl() && Input.GetKeyDown(KeyCode.A)) address = "";
            else
                foreach (char c in Input.inputString)
                {
                    if (c == '\b') { if (address.Length > 0) address = address.Substring(0, address.Length - 1); }
                    else if (c == '\n' || c == '\r') go = true;
                    else if (!char.IsControl(c) && address.Length < 2048) address += c;
                }
            if (go)
            {
                addressTyping = false;
                GoTo(barFor, address);
                BarLook();
            }
            else if (address != before) BarLook();
        }

        // ---- the library: what can be put on it ---------------------------------------------------------------

        private static RectTransform libraryRect, libraryContent;
        private static readonly List<(RectTransform row, RectTransform remove, string source)> libraryRows = new List<(RectTransform, RectTransform, string)>();
        private const float LibraryRow = 26, LibraryWidth = 480;
        private const int LibraryRows = 12;

        private static void ToggleLibrary()
        {
            if (libraryRect != null) CloseLibrary();
            else OpenLibrary();
        }

        private static void CloseLibrary()
        {
            if (libraryRect != null) UnityEngine.Object.Destroy(libraryRect.gameObject);
            libraryRect = null; libraryContent = null;
            libraryRows.Clear();
        }

        // A list under the bar's right end: the saved channels (REMOVE takes one out), then the media folder's files.
        private static void OpenLibrary()
        {
            CloseLibrary();
            if (bar == null || barFor == null) return;
            var entries = Library(barFor);
            string now = SourceOf(barFor);
            float width = Mathf.Min(LibraryWidth, barWidth);
            libraryRect = UiRect("Library", uiRoot, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            libraryRect.anchoredPosition = barRect.anchoredPosition + new Vector2(barWidth - width, -barRect.sizeDelta.y - 2);
            libraryRect.sizeDelta = new Vector2(width, Mathf.Clamp(entries.Count, 2, LibraryRows) * LibraryRow + 8);
            libraryRect.gameObject.AddComponent<Image>().color = new Color(.06f, .06f, .055f, .97f);
            libraryRect.gameObject.AddComponent<RectMask2D>();
            libraryContent = UiRect("Rows", libraryRect, new Vector2(0, 1), new Vector2(1, 1), new Vector2(.5f, 1));
            libraryContent.anchoredPosition = Vector2.zero;
            libraryContent.sizeDelta = new Vector2(0, entries.Count * LibraryRow + 8);
            if (entries.Count == 0)
            {
                string hint = barFor.Tag.Source == ComputerKey
                    ? "No bookmarks yet: SAVE keeps the page you're on."
                    : "Nothing here yet. Put pictures and videos in the media folder (FOLDER opens it), or SAVE what's on now to keep its link.";
                TextMeshProUGUI none = Text("None", libraryContent, hint, 13, new Vector2(10, -4), new Vector2(width - 20, LibraryRow * 2), TextAlignmentOptions.MidlineLeft, Dim, false);
                none.overflowMode = TextOverflowModes.Overflow;
                return;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                LibraryEntry entry = entries[i];
                RectTransform row = UiRect("Row", libraryContent, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
                row.anchoredPosition = new Vector2(4, -4 - i * LibraryRow); row.sizeDelta = new Vector2(width - 8, LibraryRow - 2);
                row.gameObject.AddComponent<Image>().color = SameLink(entry.Source, now) ? ButtonOn : new Color(.12f, .115f, .105f, 1);
                float removeWidth = entry.Saved ? 66 : 0;
                string label = (entry.Saved ? "<color=#ffb840>SAVED</color>  " : IsVideo(entry.Source) ? "<color=#8a877f>VIDEO</color>  " : "<color=#8a877f>PICTURE</color>  ") + Plain(entry.Label);
                TextMeshProUGUI text = Text("Name", row, label, 13, new Vector2(8, 0), new Vector2(width - 8 - 16 - removeWidth, LibraryRow - 2), TextAlignmentOptions.MidlineLeft, Ink, false);
                text.enableWordWrapping = false;
                RectTransform remove = null;
                if (entry.Saved)
                {
                    remove = UiRect("Remove", row, new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, .5f));
                    remove.sizeDelta = new Vector2(removeWidth, 0);
                    TextMeshProUGUI x = Text("Label", remove, "REMOVE", 11, Vector2.zero, Vector2.zero, TextAlignmentOptions.Center, Dim, false);
                    x.rectTransform.anchorMin = Vector2.zero; x.rectTransform.anchorMax = Vector2.one;
                    x.rectTransform.offsetMin = x.rectTransform.offsetMax = Vector2.zero;
                }
                libraryRows.Add((row, remove, entry.Source));
            }
            libraryRect.SetAsLastSibling();
        }

        private static void LibraryClick(Vector2 mouse)
        {
            foreach (var (row, remove, source) in libraryRows)
            {
                if (!Inside(row, mouse)) continue;
                if (remove != null && Inside(remove, mouse))
                {
                    Channels().RemoveAll(c => SameLink(c.Url, source));
                    SaveChannels();
                    OpenLibrary();
                }
                else
                {
                    CloseLibrary();
                    PutOn(barFor, source);
                }
                BarLook();
                return;
            }
        }

        private static void LibraryScroll()
        {
            float wheel = Input.mouseScrollDelta.y;
            if (wheel == 0 || libraryRect == null || libraryContent == null || !Inside(libraryRect, Input.mousePosition)) return;
            float most = Mathf.Max(0, libraryContent.sizeDelta.y - libraryRect.rect.height);
            libraryContent.anchoredPosition = new Vector2(0, Mathf.Clamp(libraryContent.anchoredPosition.y - wheel * LibraryRow * 2, 0, most));
        }
    }
}
