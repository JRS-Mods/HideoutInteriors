using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HideoutBrowser
{
    /// <summary>
    /// Hideout Interiors' browser: web pages for the hideout's computers and TV screens, drawn by Edge's WebView2 in
    /// windows nobody sees, each frame copied into shared memory the game reads. The game talks to it over stdin and
    /// stdout, a line a command (tab-separated, text in base64): open, nav, close, mouse, wheel, text, key, cmd (back,
    /// forward, reload, page, focus, pause, play, seek), active, mute, vol, quit. It quits when the game does.
    /// </summary>
    internal static class Program
    {
        internal static Control Host;
        internal static CoreWebView2Environment Environment;
        internal static readonly Dictionary<int, Surface> Surfaces = new Dictionary<int, Surface>();
        private static readonly List<string> waiting = new List<string>();
        private static readonly object outLock = new object();
        private static int parent;
        private static StreamWriter output;

        [STAThread]
        private static void Main(string[] args)
        {
            parent = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 0;
            string data = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "HideoutBrowser");
            output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Application.EnableVisualStyles();
            Host = new Control();
            Host.CreateControl();
            _ = Host.Handle;

            var reader = new Thread(Read) { IsBackground = true, Name = "stdin" };
            reader.Start();

            var tick = new System.Windows.Forms.Timer { Interval = 15 };
            tick.Tick += (_, __) => Tick();
            tick.Start();
            var slow = new System.Windows.Forms.Timer { Interval = 1000 };
            slow.Tick += (_, __) => Slow();
            slow.Start();

            StartEnvironment(data);
            Application.Run();
        }

        private static async void StartEnvironment(string data)
        {
            try
            {
                Directory.CreateDirectory(data);
                var options = new CoreWebView2EnvironmentOptions(
                    "--autoplay-policy=no-user-gesture-required --disable-features=CalculateNativeWinOcclusion " +
                    "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows");
                Environment = await CoreWebView2Environment.CreateAsync(null, data, options);
                Send("ready", Environment.BrowserVersionString);
                foreach (string line in waiting.ToArray()) Handle(line);
                waiting.Clear();
            }
            catch (Exception e)
            {
                Send("error", B64("Edge WebView2 isn't there or wouldn't start: " + e.Message));
                Thread.Sleep(500);
                System.Environment.Exit(2);
            }
        }

        private static void Read()
        {
            try
            {
                var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                string line;
                while ((line = input.ReadLine()) != null)
                {
                    string l = line;
                    Host.BeginInvoke((Action)(() => { if (Environment == null) waiting.Add(l); else Handle(l); }));
                }
            }
            catch (Exception) { }
            // The game's gone (or closed us).
            Host.BeginInvoke((Action)Quit);
        }

        internal static void Quit()
        {
            foreach (Surface s in new List<Surface>(Surfaces.Values)) s.Dispose();
            Surfaces.Clear();
            Application.Exit();
            System.Environment.Exit(0);
        }

        internal static void Send(params string[] parts)
        {
            lock (outLock)
            {
                try { output?.Write(string.Join("\t", parts) + "\n"); }
                catch (Exception) { }
            }
        }

        internal static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        internal static string Text(string b64) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); } catch (Exception) { return ""; } }
        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        private static void Handle(string line)
        {
            string[] a = line.Split('\t');
            if (a.Length == 0) return;
            try
            {
                if (a[0] == "quit") { Quit(); return; }
                if (a.Length < 2) return;
                int id = I(a[1]);
                Surfaces.TryGetValue(id, out Surface s);
                switch (a[0])
                {
                    case "open":
                        // open id w h map mode url
                        s?.Dispose();
                        Surfaces[id] = new Surface(id, I(a[2]), I(a[3]), a[4], a[5] == "video", Text(a[6]));
                        break;
                    case "nav": s?.Navigate(Text(a[2])); break;
                    case "close": s?.Dispose(); Surfaces.Remove(id); break;
                    case "active": if (s != null) s.Active = a[2] == "1"; break;
                    case "mute": s?.Mute(a[2] == "1"); break;
                    case "vol": s?.Volume(F(a[2])); break;
                    case "mouse": s?.Mouse(a[2], F(a[3]), F(a[4]), a[5], I(a[6])); break;
                    case "wheel": s?.Wheel(F(a[2]), F(a[3]), F(a[4])); break;
                    case "text": s?.Insert(Text(a[2])); break;
                    case "key": s?.Key(a[2], I(a[3])); break;
                    case "cmd": s?.Command(a[2], a.Length > 3 ? a[3] : null); break;
                }
            }
            catch (Exception e) { Send("log", B64($"'{line}': {e.Message}")); }
        }

        private static void Tick()
        {
            foreach (Surface s in Surfaces.Values) s.Capture();
        }

        private static void Slow()
        {
            if (parent > 0)
            {
                try { if (Process.GetProcessById(parent).HasExited) Quit(); }
                catch (ArgumentException) { Quit(); }
            }
            foreach (Surface s in Surfaces.Values) s.Slow();
        }
    }

    /// <summary>A window nobody sees, off the screen, that doesn't take the keyboard or show in the taskbar.</summary>
    internal sealed class HiddenWindow : Form
    {
        public HiddenWindow(int w, int h)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-30000, -30000);
            ClientSize = new Size(w, h);
            BackColor = Color.Black;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= 0x80 | 0x08000000;   // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return p;
            }
        }
    }

    internal sealed class Surface : IDisposable
    {
        private const int Header = 32;
        private readonly int id, width, height;
        private readonly bool video;
        private readonly HiddenWindow window;
        private readonly WebView2 view;
        private CoreWebView2 core;
        private readonly MemoryMappedFile map;
        private readonly MemoryMappedViewAccessor access;
        private readonly IntPtr memory;
        private readonly Bitmap bitmap;
        private int sequence;
        private string pending;
        private bool muted;
        private float volume = 1;
        // Frames come by PrintWindow; if that gives only black once the page is there, by WebView2's own capture.
        private bool byPreview, previewBusy, loaded;
        private int blackFrames;
        private long lastFrame;
        public bool Active = true;

        private static unsafe IntPtr Acquire(MemoryMappedViewAccessor access)
        {
            byte* p = null;
            access.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            return (IntPtr)p;
        }

        public Surface(int id, int w, int h, string mapName, bool video, string url)
        {
            this.id = id; width = Math.Max(64, w); height = Math.Max(64, h); this.video = video;
            long size = Header + 2L * width * height * 4;
            map = MemoryMappedFile.CreateOrOpen(mapName, size);
            access = map.CreateViewAccessor(0, size);
            memory = Acquire(access);
            Marshal.WriteInt32(memory, 4, width); Marshal.WriteInt32(memory, 8, height);
            bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            window = new HiddenWindow(width, height);
            view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
            window.Controls.Add(view);
            window.Show();
            pending = url;
            Start();
        }

        private async void Start()
        {
            try
            {
                await view.EnsureCoreWebView2Async(Program.Environment);
                core = view.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.IsMuted = muted;
                // A page opening another window: it opens here instead.
                core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
                core.SourceChanged += (_, __) =>
                {
                    Program.Send("url", id.ToString(), Program.B64(core.Source));
                    // Another page (or another video on the same one): it plays, whatever the last was left as.
                    Evaluate("window.__hiPaused=false");
                };
                core.DocumentTitleChanged += (_, __) => Program.Send("title", id.ToString(), Program.B64(core.DocumentTitle));
                core.NavigationCompleted += (_, e) =>
                {
                    loaded = true;
                    blackFrames = 0;
                    Volume(volume);
                };
                if (pending != null) Navigate(pending);
            }
            catch (Exception e) { Program.Send("error", Program.B64("A page couldn't start: " + e.Message)); }
        }

        // A web address, a search, or a live stream (.m3u8, played by hls.js).
        public void Navigate(string url)
        {
            url = (url ?? "").Trim();
            if (core == null) { pending = url; return; }
            if (url.Length == 0) url = "https://www.google.com";
            if (!url.Contains("://") && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                url = url.Contains(" ") || !url.Contains(".") ? "https://www.google.com/search?q=" + Uri.EscapeDataString(url) : "https://" + url;
            string path = url;
            int q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) path = path.Substring(0, q);
            if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) { core.NavigateToString(HlsPage(url)); return; }
            try { core.Navigate(url); }
            catch (Exception e) { Program.Send("log", Program.B64($"Bad address {url}: {e.Message}")); }
        }

        private static string HlsPage(string url) =>
            "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;height:100%;background:#000;overflow:hidden}video{width:100%;height:100%;object-fit:contain}</style>" +
            "<script src='https://cdn.jsdelivr.net/npm/hls.js@1.5.15/dist/hls.min.js'></script></head><body><video id='v' autoplay playsinline></video><script>" +
            "var v=document.getElementById('v'),u=" + Json(url) + ";" +
            "if(v.canPlayType('application/vnd.apple.mpegurl')){v.src=u;}else if(window.Hls&&Hls.isSupported()){var h=new Hls({liveDurationInfinity:true});h.loadSource(u);h.attachMedia(v);}" +
            "v.play().catch(function(){});</script></body></html>";

        private static string Json(string s)
        {
            var b = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32 || c == '<' || c == '>') b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        public void Mute(bool on) { muted = on; if (core != null) core.IsMuted = on; }

        public void Volume(float v)
        {
            volume = Math.Max(0, Math.Min(1, v));
            core?.ExecuteScriptAsync($"document.querySelectorAll('video,audio').forEach(function(m){{m.volume={volume.ToString("0.###", CultureInfo.InvariantCulture)};}})");
        }

        private void Protocol(string method, string json)
        {
            if (core == null) return;
            try { _ = core.CallDevToolsProtocolMethodAsync(method, json); }
            catch (Exception e) { Program.Send("log", Program.B64(method + ": " + e.Message)); }
        }

        private static string N(float f) => f.ToString("0.##", CultureInfo.InvariantCulture);

        // The page's own mouse: moved, pressed or let go at a point on it, as if a person did it.
        public void Mouse(string type, float x, float y, string button, int clicks)
        {
            string kind = type == "down" ? "mousePressed" : type == "up" ? "mouseReleased" : "mouseMoved";
            string b = type == "move" ? "none" : button;
            Protocol("Input.dispatchMouseEvent", $"{{\"type\":\"{kind}\",\"x\":{N(x)},\"y\":{N(y)},\"button\":\"{b}\",\"clickCount\":{Math.Max(0, clicks)}}}");
        }

        public void Wheel(float x, float y, float dy) =>
            Protocol("Input.dispatchMouseEvent", $"{{\"type\":\"mouseWheel\",\"x\":{N(x)},\"y\":{N(y)},\"deltaX\":0,\"deltaY\":{N(-dy * 120)}}}");

        public void Insert(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Protocol("Input.insertText", "{\"text\":" + Json(text) + "}");
        }

        // A key that isn't text: Enter, Backspace, the arrows... (mods: 1 Alt, 2 Ctrl, 8 Shift).
        public void Key(string name, int mods)
        {
            int code;
            string text = null;
            string commands = null;
            switch (name)
            {
                case "Enter": code = 13; text = "\\r"; break;
                case "Backspace": code = 8; break;
                case "Tab": code = 9; break;
                case "Escape": code = 27; break;
                case "Delete": code = 46; break;
                case "ArrowLeft": code = 37; break;
                case "ArrowUp": code = 38; break;
                case "ArrowRight": code = 39; break;
                case "ArrowDown": code = 40; break;
                case "Home": code = 36; break;
                case "End": code = 35; break;
                case "PageUp": code = 33; break;
                case "PageDown": code = 34; break;
                case " ": code = 32; break;
                default:
                    if (name.Length == 1 && char.IsLetter(name[0])) code = char.ToUpperInvariant(name[0]);
                    else return;
                    if ((mods & 2) != 0)
                        switch (char.ToLowerInvariant(name[0]))
                        {
                            case 'a': commands = "selectAll"; break;
                            case 'c': commands = "copy"; break;
                            case 'x': commands = "cut"; break;
                            case 'z': commands = "undo"; break;
                            case 'y': commands = "redo"; break;
                        }
                    break;
            }
            string common = $"\"key\":{Json(name)},\"code\":{Json(name.Length == 1 ? "Key" + name.ToUpperInvariant() : name)},\"windowsVirtualKeyCode\":{code},\"nativeVirtualKeyCode\":{code},\"modifiers\":{mods}";
            string down = text != null ? $"{{\"type\":\"keyDown\",{common},\"text\":\"{text}\",\"unmodifiedText\":\"{text}\"}}" : $"{{\"type\":\"rawKeyDown\",{common}" + (commands != null ? $",\"commands\":[\"{commands}\"]" : "") + "}";
            Protocol("Input.dispatchKeyEvent", down);
            Protocol("Input.dispatchKeyEvent", $"{{\"type\":\"keyUp\",{common}}}");
        }

        // A script run as if from a person's click (so it may go full screen, start sound...).
        private async void Evaluate(string script)
        {
            if (core == null) return;
            try
            {
                string result = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":" + Json(script) + ",\"userGesture\":true,\"awaitPromise\":false}");
                if (System.Environment.GetEnvironmentVariable("HIDEOUT_BROWSER_DEBUG") == "1") Program.Send("log", Program.B64(result));
            }
            catch (Exception e) { Program.Send("log", Program.B64("evaluate: " + e.Message)); }
        }

        public void Command(string what, string value)
        {
            if (core == null) return;
            switch (what)
            {
                case "back": if (core.CanGoBack) core.GoBack(); break;
                case "forward": if (core.CanGoForward) core.GoForward(); break;
                case "reload": core.Reload(); break;
                // The video alone filling the page, or the whole page (to click about in it), and back.
                case "page": focus = false; Evaluate(Unfocus); break;
                case "focus": focus = true; if (video && loaded) Evaluate(Focus); break;
                // The page's video (its biggest) paused, playing again, or on or back by so many seconds.
                case "pause": Evaluate("window.__hiPaused=true;document.querySelectorAll('video,audio').forEach(function(m){m.pause();});"); break;
                case "play": Evaluate("(function(){window.__hiPaused=false;" + Biggest + "if(!b)b=document.querySelector('audio');if(b){var p=b.play();if(p&&p.catch)p.catch(function(){});}})()"); break;
                case "seek":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds))
                        Evaluate("(function(){" + Biggest + "if(b&&isFinite(b.duration))b.currentTime=Math.max(0,Math.min(b.duration-1,b.currentTime+(" + seconds.ToString("0.###", CultureInfo.InvariantCulture) + ")));})()");
                    break;
            }
        }

        // The page's biggest video that has something to show, as b (null: none).
        private const string Biggest = "var b=null,a=-1;document.querySelectorAll('video').forEach(function(v){var s=v.offsetWidth*v.offsetHeight+(v.classList.contains('hi-full')?1e9:0);if(v.readyState>0&&s>a){a=s;b=v;}});";

        private bool focus = true;

        // A cookie box in front (YouTube's, Twitch's...) said no to; then the page's biggest video playing, with sound,
        // filling the page and everything else hidden. Not on YouTube's lists of videos (a search, a channel, the home
        // page: there the whole page shows, to pick one from), nor playing a video paused from the game. Run as a
        // person's click (so sound may start).
        private const string Focus = "(function(){" +
            "var c=[].slice.call(document.querySelectorAll('button,[role=button]')).filter(function(e){return /^\\s*(reject all|decline|reject)\\s*$/i.test(e.innerText||e.getAttribute('aria-label')||'');})[0];" +
            "if(c){c.click();return 'consent';}" +
            "if(/(^|\\.)youtube\\.com$/.test(location.hostname)&&!/^\\/(watch|live|embed|shorts)|\\/live\\/?$/.test(location.pathname)){document.documentElement.classList.remove('hi-on');return 'list';}" +
            "var k=document.querySelector('.ytp-skip-ad-button,.ytp-ad-skip-button,.ytp-ad-skip-button-modern');if(k)k.click();" +
            Biggest +
            "if(!b)return 'none';" +
            "if(!document.getElementById('hi-style')){var s=document.createElement('style');s.id='hi-style';" +
            "s.textContent='html.hi-on,html.hi-on body{overflow:hidden!important;background:#000!important}html.hi-on *{visibility:hidden!important;transform:none!important;filter:none!important;backdrop-filter:none!important;perspective:none!important;contain:none!important;will-change:auto!important;clip-path:none!important}" +
            "html.hi-on video.hi-full{visibility:visible!important;position:fixed!important;left:0!important;top:0!important;right:auto!important;bottom:auto!important;width:100vw!important;height:100vh!important;max-width:none!important;max-height:none!important;margin:0!important;transform:none!important;object-fit:contain!important;background:#000!important;z-index:2147483647!important}';" +
            "document.documentElement.appendChild(s);}" +
            "document.documentElement.classList.add('hi-on');" +
            "document.querySelectorAll('video.hi-full').forEach(function(v){if(v!==b)v.classList.remove('hi-full');});" +
            "b.classList.add('hi-full');if(b.muted)b.muted=false;if(b.paused&&!window.__hiPaused){var p=b.play();if(p&&p.catch)p.catch(function(){});}" +
            "return 'ok';})()";

        private const string Unfocus = "document.documentElement.classList.remove('hi-on')";

        // Once a second: a video page's video filling it, and the volume again (players set their own as they start).
        public void Slow()
        {
            if (core == null) return;
            if (video && focus && loaded) Evaluate(Focus);
            if (loaded) Volume(volume);
        }

        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        // A frame into the buffer the game isn't reading, then that one's made the latest.
        public void Capture()
        {
            if (!Active || core == null) return;
            long now = Environment.TickCount;
            if (now - lastFrame < 30) return;
            lastFrame = now;
            if (byPreview) { CapturePreview(); return; }
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(window.Handle, hdc, 2); }   // PW_RENDERFULLCONTENT
                finally { g.ReleaseHdc(hdc); }
            }
            if (Publish(bitmap) || !loaded) return;
            if (++blackFrames > 90) { byPreview = true; Program.Send("log", Program.B64($"Page {id}: PrintWindow gives black, taking WebView2's own captures.")); }
        }

        private async void CapturePreview()
        {
            if (previewBusy) return;
            previewBusy = true;
            try
            {
                using (var stream = new MemoryStream())
                {
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
                    stream.Position = 0;
                    using (var image = Image.FromStream(stream))
                    using (Graphics g = Graphics.FromImage(bitmap))
                        g.DrawImage(image, 0, 0, width, height);
                    Publish(bitmap);
                }
            }
            catch (Exception) { }
            finally { previewBusy = false; }
        }

        // Copies a frame in; false if it was all black.
        private unsafe bool Publish(Bitmap frame)
        {
            int next = (sequence + 1) & 1;
            BitmapData data = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            bool any = false;
            try
            {
                byte* from = (byte*)data.Scan0;
                byte* to = (byte*)memory + Header + (long)next * width * height * 4;
                long bytes = (long)width * height * 4;
                if (data.Stride == width * 4) Buffer.MemoryCopy(from, to, bytes, bytes);
                else for (int y = 0; y < height; y++) Buffer.MemoryCopy(from + y * data.Stride, to + (long)y * width * 4, width * 4, width * 4);
                // A few pixels down the middle: anything but black.
                for (int i = 0; i < 64 && !any; i++)
                {
                    long at = ((long)(height * (i + 1) / 66) * width + width / 2 + (i - 32) * width / 80) * 4;
                    any = to[at] > 8 || to[at + 1] > 8 || to[at + 2] > 8;
                }
            }
            finally { frame.UnlockBits(data); }
            Marshal.WriteInt32(memory, 12, next);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(memory, 0, ++sequence);
            if (any) blackFrames = 0;
            return any;
        }

        public void Dispose()
        {
            try { view.Dispose(); } catch (Exception) { }
            try { window.Close(); window.Dispose(); } catch (Exception) { }
            try { access.SafeMemoryMappedViewHandle.ReleasePointer(); access.Dispose(); map.Dispose(); } catch (Exception) { }
            bitmap.Dispose();
        }
    }
}
