using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// Web pages on the hideout's screens: a computer to browse on, and TV screens showing YouTube, Twitch and other
    /// pages and live streams (the video alone, filling the screen). The pages are drawn by HideoutBrowser.exe (in the
    /// mod's browser folder; Edge's WebView2, which Windows has), off screen; each frame comes through shared memory.
    /// Used (see Interiors.MediaBar.cs), the mouse and keyboard work the page.
    /// </summary>
    internal static partial class Interiors
    {
        private const string ComputerKey = ExtraPrefix + "hideoutinteriors.computer";
        private const string Homepage = "https://www.google.com";
        private const int PageHeader = 32;

        private sealed class WebPage
        {
            public int Id;
            public MediaState State;
            public bool Video;
            public int Width, Height, Seen = -1;
            public IntPtr Map, Memory;
            public Texture2D Texture;
            public string Url = "", Title = "";
            public bool Active = true, Muted;
            public float Volume = -1;
            // Its video paused from the bar, and (a video page) the whole page showing rather than the video alone.
            public bool Paused, Whole;
        }

        private static readonly Dictionary<int, WebPage> pages = new Dictionary<int, WebPage>();
        private static int nextPage = 1;
        private static Process browser;
        private static StreamWriter browserIn;
        private static readonly Queue<string> browserOut = new Queue<string>();
        private static bool browserFailed;

        private static string BrowserExe => Path.Combine(Plugin.Folder, "browser", "HideoutBrowser.exe");

        // A web page to show: a page (not a picture or a video file) on the web.
        private static bool IsWebPage(string s)
        {
            if (!IsUrl(s)) return false;
            if (VideoPage.IsMatch(s)) return true;
            string type = TypeOf(s);
            return type == ".m3u8" || type == ".html" || type == ".htm" || type == ".php" || type == ".aspx";
        }

        // ---- the helper ---------------------------------------------------------------------------------------

        private static bool StartBrowser()
        {
            if (browser != null && !browser.HasExited) return true;
            if (browserFailed) return false;
            if (!File.Exists(BrowserExe))
            {
                browserFailed = true;
                Plugin.Log.LogWarning($"Decor: no {BrowserExe}, so no web pages.");
                EFT.Communications.NotificationManager.DisplayWarningNotification("Web pages need the browser folder that comes with Hideout Interiors (BepInEx/plugins/HideoutInteriors/browser).");
                return false;
            }
            try
            {
                string data = Path.Combine(BepInEx.Paths.CachePath, "HideoutInteriors", "browser");
                var start = new ProcessStartInfo(BrowserExe, $"{Process.GetCurrentProcess().Id} \"{data}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(BrowserExe),
                };
                browser = Process.Start(start);
                browserIn = browser.StandardInput;
                browserIn.AutoFlush = true;
                Process running = browser;
                var reader = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = running.StandardOutput.ReadLine()) != null)
                            lock (browserOut) browserOut.Enqueue(line);
                    }
                    catch (Exception) { }
                }) { IsBackground = true, Name = "Hideout Interiors browser" };
                reader.Start();
                Plugin.Log.LogInfo("Decor: the browser started.");
                return true;
            }
            catch (Exception e)
            {
                browserFailed = true;
                Plugin.Log.LogWarning("Decor: the browser wouldn't start: " + e.Message);
                EFT.Communications.NotificationManager.DisplayWarningNotification("The browser for web pages wouldn't start (see the log).");
                return false;
            }
        }

        private static void BrowserSend(params object[] parts)
        {
            if (browserIn == null || browser == null || browser.HasExited) return;
            var line = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) line.Append('\t');
                line.Append(parts[i] is float f ? f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : parts[i]?.ToString());
            }
            try { browserIn.Write(line.Append('\n').ToString()); }
            catch (Exception e) { Plugin.Log.LogWarning("Decor: the browser's gone: " + e.Message); browserIn = null; }
        }

        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        private static string FromB64(string s) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch (Exception) { return ""; } }

        // ---- shared memory ------------------------------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        // ---- pages --------------------------------------------------------------------------------------------

        // A page on a poster, screen or computer: as big as its face's shape, a video page's video filling it.
        private static void OpenPage(MediaState s, string url, bool video)
        {
            ClosePage(s);
            if (!StartBrowser()) return;
            Vector2 face = FaceSize(s);
            float aspect = s.Tag.Height > 0 ? face.x / face.y : 16f / 9;
            int w = 1280, h = Mathf.Clamp(Mathf.RoundToInt(w / aspect), 240, 1600);
            var page = new WebPage { Id = nextPage++, State = s, Video = video, Width = w, Height = h, Url = url };
            string name = $"Local\\HideoutInteriors_{Process.GetCurrentProcess().Id}_{page.Id}";
            long size = PageHeader + 2L * w * h * 4;
            page.Map = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 4, (uint)(size >> 32), (uint)(size & 0xffffffff), name);
            if (page.Map == IntPtr.Zero) { Plugin.Log.LogWarning("Decor: no shared memory for a page: " + Marshal.GetLastWin32Error()); return; }
            page.Memory = MapViewOfFile(page.Map, 6, 0, 0, new UIntPtr((ulong)size));
            if (page.Memory == IntPtr.Zero) { CloseHandle(page.Map); return; }
            Marshal.WriteInt32(page.Memory, 0, 0);
            page.Texture = new Texture2D(w, h, TextureFormat.BGRA32, false) { name = "Hideout Interiors page", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 4 };
            s.Page = page;
            s.Aspect = (float)w / h;
            pages[page.Id] = page;
            BrowserSend("open", page.Id, w, h, name, video ? "video" : "web", B64(url));
            Show(s, page.Texture);
            Relayout(s);
        }

        private static void ClosePage(MediaState s)
        {
            WebPage page = s.Page;
            if (page == null) return;
            s.Page = null;
            if (usingMedia == s) pageHeld = 0;
            pages.Remove(page.Id);
            BrowserSend("close", page.Id);
            if (page.Memory != IntPtr.Zero) UnmapViewOfFile(page.Memory);
            if (page.Map != IntPtr.Zero) CloseHandle(page.Map);
            page.Memory = page.Map = IntPtr.Zero;
            if (page.Texture != null) UnityEngine.Object.Destroy(page.Texture);
        }

        // The pages there are, to go through while some are closed.
        private static readonly List<WebPage> pageList = new List<WebPage>();
        private static List<WebPage> Pages()
        {
            pageList.Clear();
            pageList.AddRange(pages.Values);
            return pageList;
        }

        private static void ClosePages()
        {
            foreach (WebPage page in Pages()) ClosePage(page.State);
            pages.Clear();
        }

        private static void Navigate(WebPage page, string url)
        {
            page.Url = url;
            page.Paused = false;
            BrowserSend("nav", page.Id, B64(url));
        }

        // Each frame: what the browser said, each page's newest frame onto its screen (if it's seen), and every
        // quarter second whether it plays, how loud (by how far the camera is) and whether it's muted.
        private static void WebTick()
        {
            while (true)
            {
                string line;
                lock (browserOut) { if (browserOut.Count == 0) break; line = browserOut.Dequeue(); }
                BrowserMessage(line);
            }
            if (pages.Count == 0) return;
            bool often = Time.frameCount % 15 == 0;
            Camera camera = often ? ViewCamera() : null;
            int uploads = 0;
            foreach (WebPage page in Pages())
            {
                MediaState s = page.State;
                if (s.Thing == null) { ClosePage(s); continue; }
                if (page.Memory == IntPtr.Zero) continue;
                Renderer face = s.FaceRenderer;
                bool seen = face != null && face.isVisible;
                int sequence = Marshal.ReadInt32(page.Memory, 0);
                if (sequence != page.Seen && (seen || s == usingMedia) && uploads < 3)
                {
                    int buffer = Marshal.ReadInt32(page.Memory, 12) & 1;
                    page.Texture.LoadRawTextureData(new IntPtr(page.Memory.ToInt64() + PageHeader + (long)buffer * page.Width * page.Height * 4), page.Width * page.Height * 4);
                    page.Texture.Apply(false);
                    page.Seen = sequence;
                    uploads++;
                }
                if (!often) continue;
                bool active = audible && s.Thing.gameObject.activeInHierarchy;
                if (active != page.Active) { page.Active = active; BrowserSend("active", page.Id, active ? 1 : 0); }
                bool muted = s.Tag.Muted || !audible;
                if (muted != page.Muted) { page.Muted = muted; BrowserSend("mute", page.Id, muted ? 1 : 0); }
                float distance = camera != null ? Vector3.Distance(camera.transform.position, s.Thing.position) : 5;
                float volume = Loudness(s) * Mathf.Clamp01(1.2f - distance / 30f);
                if (Mathf.Abs(volume - page.Volume) > .015f || (volume == 0) != (page.Volume == 0)) { page.Volume = volume; BrowserSend("vol", page.Id, volume); }
            }
        }

        private static void BrowserMessage(string line)
        {
            string[] a = line.Split('\t');
            switch (a[0])
            {
                case "ready": Plugin.Log.LogInfo("Decor: browser ready, Edge WebView2 " + (a.Length > 1 ? a[1] : "")); break;
                case "error":
                    string error = a.Length > 1 ? FromB64(a[1]) : "";
                    Plugin.Log.LogWarning("Decor: browser: " + error);
                    EFT.Communications.NotificationManager.DisplayWarningNotification("Web pages: " + error);
                    break;
                case "log": Plugin.Log.LogInfo("Decor: browser: " + (a.Length > 1 ? FromB64(a[1]) : "")); break;
                case "url":
                case "title":
                    if (a.Length < 3 || !int.TryParse(a[1], out int id) || !pages.TryGetValue(id, out WebPage page)) break;
                    string said = FromB64(a[2]);
                    if (a[0] == "title") page.Title = said;
                    // Only the web's addresses: a stream's player page (about:blank) keeps the stream's own.
                    else if (IsUrl(said) && said != page.Url) { page.Url = said; page.Paused = false; }
                    if (barFor != null && barFor.Page == page) BarLook();
                    break;
            }
        }

        // ---- the mouse and keyboard on one ---------------------------------------------------------------------

        // Where the page's mouse was last, which of its buttons are down, and the last click (for double clicks).
        private static Vector2 pageMouse = new Vector2(-1, -1);
        private static int pageHeld;
        private static float lastClick;
        private static Vector2 lastClickAt;

        // The mouse to the page: where it is on the face (0, 0 its top left; 1, 1 its bottom right), its buttons, the
        // wheel. A button let go off the page is let go on it too.
        private static void PageMouse(WebPage page, bool onPage, Vector2 on)
        {
            Vector2 at = onPage ? new Vector2(on.x * page.Width, on.y * page.Height) : pageMouse;
            if (onPage && (at - pageMouse).sqrMagnitude > .25f) { pageMouse = at; BrowserSend("mouse", page.Id, "move", at.x, at.y, "none", 0); }
            for (int b = 0; b < 2; b++)
            {
                string button = b == 0 ? "left" : "right";
                if (onPage && Input.GetMouseButtonDown(b))
                {
                    int clicks = b == 0 && Time.unscaledTime - lastClick < .35f && (at - lastClickAt).magnitude < 6 ? 2 : 1;
                    if (b == 0) { lastClick = Time.unscaledTime; lastClickAt = at; }
                    pageHeld |= 1 << b;
                    BrowserSend("mouse", page.Id, "down", at.x, at.y, button, clicks);
                }
                if (Input.GetMouseButtonUp(b) && (pageHeld & (1 << b)) != 0)
                {
                    pageHeld &= ~(1 << b);
                    BrowserSend("mouse", page.Id, "up", at.x, at.y, button, 1);
                }
            }
            float wheel = Input.mouseScrollDelta.y;
            if (onPage && wheel != 0) BrowserSend("wheel", page.Id, at.x, at.y, wheel);
        }

        private static readonly (KeyCode key, string name)[] PageKeyNames =
        {
            (KeyCode.Tab, "Tab"), (KeyCode.Delete, "Delete"), (KeyCode.LeftArrow, "ArrowLeft"), (KeyCode.RightArrow, "ArrowRight"),
            (KeyCode.UpArrow, "ArrowUp"), (KeyCode.DownArrow, "ArrowDown"), (KeyCode.Home, "Home"), (KeyCode.End, "End"),
            (KeyCode.PageUp, "PageUp"), (KeyCode.PageDown, "PageDown"),
        };
        private static readonly KeyCode[] PageCtrlKeys = { KeyCode.A, KeyCode.C, KeyCode.X, KeyCode.Z, KeyCode.Y };
        private static readonly StringBuilder typed = new StringBuilder();

        // The keyboard to the page: text as typed, the keys that aren't text, and Ctrl's copy, cut, paste, undo.
        private static void PageKeys(WebPage page)
        {
            if (!Input.anyKey && Input.inputString.Length == 0) return;
            bool ctrl = Ctrl();
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            int mods = (ctrl ? 2 : 0) | (shift ? 8 : 0);
            if (ctrl)
            {
                if (Input.GetKeyDown(KeyCode.V)) BrowserSend("text", page.Id, B64(GUIUtility.systemCopyBuffer ?? ""));
                foreach (KeyCode k in PageCtrlKeys)
                    if (Input.GetKeyDown(k)) BrowserSend("key", page.Id, k.ToString().ToLowerInvariant(), mods);
                if (Input.GetKeyDown(KeyCode.L)) { StartAddress(); return; }
            }
            typed.Clear();
            foreach (char c in Input.inputString)
            {
                if (c == '\b' || c == '\n' || c == '\r')
                {
                    if (typed.Length > 0) { BrowserSend("text", page.Id, B64(typed.ToString())); typed.Clear(); }
                    BrowserSend("key", page.Id, c == '\b' ? "Backspace" : "Enter", mods);
                }
                else if (!char.IsControl(c) && !ctrl) typed.Append(c);
            }
            if (typed.Length > 0) BrowserSend("text", page.Id, B64(typed.ToString()));
            foreach (var (key, name) in PageKeyNames)
                if (Input.GetKeyDown(key)) BrowserSend("key", page.Id, name, mods);
        }
    }
}
