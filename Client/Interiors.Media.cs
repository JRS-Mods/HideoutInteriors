using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace HideoutInteriors
{
    /// <summary>
    /// Posters and TV screens: decorations of the mod's own that show a picture or play a video, from a link (http or
    /// https) or a file in BepInEx/plugins/HideoutInteriors/media. They're put in blank; what's on one is chosen on it
    /// afterwards (its bar: see Interiors.MediaBar.cs). A poster is lit like the room; a screen glows. Both hang on
    /// walls when carried over one, and a screen let go over a TV (or pasted onto a selected one) fits its front.
    /// Pictures from links are kept in BepInEx/cache so they come back without the internet. Videos loop, with their
    /// sound from where they stand, each as loud as it's set.
    /// </summary>
    internal static partial class Interiors
    {
        internal static ConfigEntry<float> VideoVolume;

        private const string MediaKind = "Media";
        private const string PosterKey = ExtraPrefix + "hideoutinteriors.poster", ScreenKey = ExtraPrefix + "hideoutinteriors.screen";
        private const float PosterWidth = .6f, ScreenWidth = 1f, ComputerWidth = .56f, StandHeight = .14f;
        private const float PosterBorder = .012f, ScreenBorder = .03f, PosterDepth = .008f, ScreenDepth = .035f;

        private static readonly string[] VideoTypes = { ".mp4", ".m4v", ".webm", ".mov", ".avi", ".wmv", ".ogv", ".mpg", ".mpeg", ".3gp", ".asf", ".dv", ".vp8" };
        private static readonly string[] PictureTypes = { ".png", ".jpg", ".jpeg" };
        // Pages that hold a video rather than being one: the player can't play these.
        private static readonly Regex VideoPage = new Regex(@"youtube\.com|youtu\.be|twitch\.tv|tiktok\.com|vimeo\.com/\d|instagram\.com|facebook\.com|x\.com/|twitter\.com", RegexOptions.IgnoreCase);
        // What a TV is called in the scene (and on the panel).
        private static readonly Regex TvName = new Regex(@"(^|_|\s)tv|monitor|screen|display|televi|laptop", RegexOptions.IgnoreCase);

        private sealed class MediaState
        {
            public Transform Thing;
            public DecorTag Tag;
            public bool Screen;
            public Material Material;
            public float Aspect;
            public VideoPlayer Video;
            public AudioSource Audio;
            public RenderTexture Target;
            public bool Paused;
            // Where a video goes on from, once it's ready again (after its screen changed shape).
            public double ResumeAt;
            // A web page on it (a computer's, or a stream's).
            public WebPage Page;
            // Going through a list: which one is on, when the next picture is due, and that a video played to its end.
            public int Index;
            public float NextAt;
            public bool Ended;
            // Its face, looked for once.
            private Transform face;
            private Renderer faceRenderer;
            public Transform Face => face != null ? face : face = Thing != null ? Thing.Find("Face") : null;
            public Renderer FaceRenderer => faceRenderer != null ? faceRenderer : faceRenderer = Face != null ? Face.GetComponent<Renderer>() : null;
        }

        private static readonly Dictionary<Transform, MediaState> mediaStates = new Dictionary<Transform, MediaState>();
        private static readonly Dictionary<string, Texture2D> pictureCache = new Dictionary<string, Texture2D>();
        // What links said they were (image/png, text/html, video/mp4...), for one that isn't a picture.
        private static readonly Dictionary<string, string> contentTypes = new Dictionary<string, string>();
        private static readonly Dictionary<string, List<Action<Texture2D>>> pictureLoads = new Dictionary<string, List<Action<Texture2D>>>();
        private static Material posterFrame, screenFrame;
        private static Mesh frontQuad;
        private static bool audible = true;

        private static string MediaFolder => Path.Combine(Plugin.Folder, "media");

        internal static void BindMedia(ConfigFile config)
        {
            VideoVolume = config.Bind("Decorating", "Video volume", .6f,
                new ConfigDescription("How loud the videos on TV screens are.", new AcceptableValueRange<float>(0f, 1f)));
            VideoVolume.SettingChanged += (_, __) => { foreach (MediaState s in mediaStates.Values) if (s.Audio != null) s.Audio.volume = Loudness(s); };
            extras[PosterKey] = new Extra { Name = "Poster", Kind = MediaKind, Make = () => MediaPattern(false) };
            extras[ScreenKey] = new Extra { Name = "TV screen", Kind = MediaKind, Make = () => MediaPattern(true) };
            extras[ComputerKey] = new Extra { Name = "Computer", Kind = MediaKind, Make = () => MediaPattern(true, computer: true) };
            try { Directory.CreateDirectory(MediaFolder); } catch (Exception) { }
        }

        private static bool IsMediaKey(string key) => key == PosterKey || key == ScreenKey || key == ComputerKey;

        // Which of the mod's own a thing is (null: none of them).
        private static string MediaKeyOf(Transform t)
        {
            if (t == null) return null;
            if (mediaStates.TryGetValue(t, out MediaState s)) return s.Tag.Source;
            DecorTag tag = t.GetComponent<DecorTag>();
            return tag != null && IsMediaKey(tag.Source) ? tag.Source : null;
        }

        private static bool IsComputer(Transform t) => MediaKeyOf(t) == ComputerKey;
        private static bool IsPoster(Transform t) => MediaKeyOf(t) == PosterKey;
        private static bool IsScreen(Transform t) => MediaKeyOf(t) == ScreenKey;
        private static bool IsMedia(Transform t) => MediaKeyOf(t) != null;

        private static readonly Dictionary<Transform, bool> tvs = new Dictionary<Transform, bool>();
        private static bool IsTv(Transform t)
        {
            if (t == null || IsMedia(t)) return false;
            if (!tvs.TryGetValue(t, out bool tv)) tvs[t] = tv = TvName.IsMatch(BaseName(t.name)) || TvName.IsMatch(Pretty(t.name));
            return tv;
        }

        // ---- what's shown -------------------------------------------------------------------------------------

        private static bool IsUrl(string s) => s != null && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        private static string MediaPath(string s)
        {
            if (string.IsNullOrEmpty(s) || IsUrl(s)) return s;
            if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7).TrimStart('/');
            try { return Path.IsPathRooted(s) ? s : Path.Combine(MediaFolder, s); } catch (Exception) { return s; }
        }

        private static string TypeOf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int cut = s.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0 && IsUrl(s)) s = s.Substring(0, cut);
            try { return Path.GetExtension(s).ToLowerInvariant(); } catch (Exception) { return ""; }
        }

        private static bool IsVideo(string s) => VideoTypes.Contains(TypeOf(s));

        // A short name for a link or a file: its file name.
        private static string MediaName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "nothing";
            string t = s;
            int cut = t.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) t = t.Substring(0, cut);
            t = t.TrimEnd('/');
            int slash = Math.Max(t.LastIndexOf('/'), t.LastIndexOf('\\'));
            if (slash >= 0 && slash < t.Length - 1) t = t.Substring(slash + 1);
            return t.Length > 40 ? t.Substring(0, 37) + "..." : t;
        }

        // The files in the media folder that can be shown.
        private static List<string> MediaFiles()
        {
            try
            {
                return Directory.GetFiles(MediaFolder)
                    .Where(f => PictureTypes.Contains(Path.GetExtension(f).ToLowerInvariant()) || VideoTypes.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(Path.GetFileName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception) { return new List<string>(); }
        }

        private static void OpenMediaFolder()
        {
            try
            {
                Directory.CreateDirectory(MediaFolder);
                System.Diagnostics.Process.Start(MediaFolder);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't open the media folder: " + e.Message); }
            EFT.Communications.NotificationManager.DisplayMessageNotification("Put pictures (.png, .jpg) and videos (.mp4, .webm) in BepInEx/plugins/HideoutInteriors/media: they show in the Media tab and in every poster's and screen's LIBRARY.");
        }

        // ---- the pattern --------------------------------------------------------------------------------------

        // A picture or screen facing along its +Z: a frame (a thin box, its back at z = 0, so it sits flat on a wall)
        // and the face on its front. The copies get their own face material.
        private static GameObject MediaPattern(bool screen, bool computer = false)
        {
            var root = new GameObject(computer ? "Computer" : screen ? "TV screen" : "Poster");
            if (computer)
            {
                // A monitor on a stand: a neck and a foot under the screen.
                foreach (string part in new[] { "Stand", "Foot" })
                {
                    GameObject p = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    UnityEngine.Object.DestroyImmediate(p.GetComponent<Collider>());
                    p.name = part;
                    p.transform.SetParent(root.transform, false);
                    p.GetComponent<MeshRenderer>().sharedMaterial = FrameMaterial(true);
                }
            }
            GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(back.GetComponent<Collider>());
            back.name = "Back";
            back.transform.SetParent(root.transform, false);
            back.GetComponent<MeshRenderer>().sharedMaterial = FrameMaterial(screen);
            var face = new GameObject("Face", typeof(MeshFilter), typeof(MeshRenderer));
            face.transform.SetParent(root.transform, false);
            face.GetComponent<MeshFilter>().sharedMesh = FrontQuad();
            face.GetComponent<MeshRenderer>().sharedMaterial = FrameMaterial(screen);
            float w = computer ? ComputerWidth : screen ? ScreenWidth : PosterWidth;
            LayoutMedia(root.transform, screen, w, screen ? w * 9 / 16 : w * 4 / 3);
            return root;
        }

        // A 1 x 1 face seen from +Z, the picture the right way round for someone looking at it.
        private static Mesh FrontQuad()
        {
            if (frontQuad != null) return frontQuad;
            frontQuad = new Mesh
            {
                name = "Hideout Interiors media face",
                vertices = new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0), new Vector3(.5f, .5f, 0), new Vector3(-.5f, .5f, 0) },
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward },
                // Seen from +Z, +X is on the left: the picture's left edge (u = 0) is there.
                uv = new[] { new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1) },
                triangles = new[] { 2, 3, 0, 2, 0, 1 },
            };
            frontQuad.RecalculateBounds();
            frontQuad.RecalculateTangents();
            return frontQuad;
        }

        private static Shader LitShader() => Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Sprites/Default");

        private static Material FrameMaterial(bool screen)
        {
            Material made = screen ? screenFrame : posterFrame;
            if (made != null) return made;
            made = new Material(LitShader()) { name = screen ? "Hideout Interiors screen frame" : "Hideout Interiors poster frame" };
            made.color = screen ? new Color(.035f, .035f, .04f) : new Color(.17f, .13f, .09f);
            if (made.HasProperty("_Glossiness")) made.SetFloat("_Glossiness", screen ? .55f : .15f);
            if (made.HasProperty("_Metallic")) made.SetFloat("_Metallic", 0);
            if (screen) screenFrame = made; else posterFrame = made;
            return made;
        }

        // The frames a poster or screen can have (its bar's FRAME goes through them): how wide, what colour, how shiny.
        private static readonly (string name, float border, Color colour, float gloss, float metal)[] Frames =
        {
            ("OWN", 0, default, 0, 0),
            ("NONE", 0, new Color(.05f, .05f, .05f), .2f, 0),
            ("BLACK", .03f, new Color(.03f, .03f, .035f), .45f, 0),
            ("WHITE", .04f, new Color(.9f, .89f, .86f), .3f, 0),
            ("WOOD", .05f, new Color(.33f, .2f, .1f), .25f, 0),
            ("DARK WOOD", .07f, new Color(.13f, .08f, .05f), .35f, 0),
            ("GOLD", .055f, new Color(.83f, .64f, .22f), .75f, .9f),
            ("SILVER", .04f, new Color(.72f, .73f, .75f), .8f, .9f),
        };
        private static readonly Material[] frameMaterials = new Material[8];

        private static float FrameBorder(bool screen, int frame) =>
            frame > 0 && frame < Frames.Length ? Frames[frame].border : screen ? ScreenBorder : PosterBorder;

        private static Material FrameStyle(bool screen, int frame)
        {
            if (frame <= 0 || frame >= Frames.Length) return FrameMaterial(screen);
            if (frameMaterials[frame] != null) return frameMaterials[frame];
            var made = new Material(LitShader()) { name = "Hideout Interiors frame " + Frames[frame].name };
            made.color = Frames[frame].colour;
            if (made.HasProperty("_Glossiness")) made.SetFloat("_Glossiness", Frames[frame].gloss);
            if (made.HasProperty("_Metallic")) made.SetFloat("_Metallic", Frames[frame].metal);
            return frameMaterials[frame] = made;
        }

        // The frame round a face of w x h metres, and the box round both that the mouse finds it by.
        private static void LayoutMedia(Transform root, bool screen, float w, float h, int frame = 0)
        {
            float b = FrameBorder(screen, frame), d = screen ? ScreenDepth : PosterDepth;
            Transform back = root.Find("Back"), face = root.Find("Face");
            if (back != null && frame > 0 && back.GetComponent<MeshRenderer>() is MeshRenderer edge) edge.sharedMaterial = FrameStyle(screen, frame);
            if (back != null) { back.localPosition = new Vector3(0, 0, d / 2); back.localScale = new Vector3(w + 2 * b, h + 2 * b, d); }
            if (face != null) { face.localPosition = new Vector3(0, 0, d + .0015f); face.localScale = new Vector3(w, h, 1); }
            Transform stand = root.Find("Stand"), foot = root.Find("Foot");
            float under = 0;
            if (stand != null && foot != null)
            {
                under = StandHeight;
                stand.localPosition = new Vector3(0, -h / 2 - b - under / 2, d / 2); stand.localScale = new Vector3(.05f, under, .03f);
                foot.localPosition = new Vector3(0, -h / 2 - b - under + .006f, d / 2); foot.localScale = new Vector3(Mathf.Min(.3f, w * .45f), .012f, .2f);
            }
            BoxCollider box = root.GetComponent<BoxCollider>();
            if (box != null)
            {
                box.center = new Vector3(0, -under / 2, under > 0 ? .1f - d / 2 : d / 2);
                box.size = new Vector3(w + 2 * b, h + 2 * b + under, under > 0 ? .2f : Mathf.Max(d, .02f));
            }
        }

        // ---- a copy's picture or video ------------------------------------------------------------------------

        // Sets a poster or screen up for what its tag says: its size, and the picture or video loading.
        private static void SetupMedia(Transform copy)
        {
            DecorTag tag = copy != null ? copy.GetComponent<DecorTag>() : null;
            if (tag == null || !IsMediaKey(tag.Source)) return;
            if (!mediaStates.TryGetValue(copy, out MediaState state))
            {
                state = new MediaState { Thing = copy, Tag = tag, Screen = tag.Source != PosterKey };
                state.Material = new Material(LitShader()) { name = "Hideout Interiors media" };
                if (state.FaceRenderer != null) state.FaceRenderer.sharedMaterial = state.Material;
                mediaStates[copy] = state;
            }
            StopVideo(state);
            state.Aspect = 0;
            state.Ended = false;
            Blank(state);
            Relayout(state);
            string source = tag.Media;
            // Going through a list: on from the one that's showing, each picture for as long as it's set to.
            if (tag.Playlist != null && tag.Playlist.Length > 0)
            {
                int at = Array.FindIndex(tag.Playlist, e => SameLink(e, source));
                if (at >= 0) state.Index = at;
                else if (string.IsNullOrWhiteSpace(source)) { state.Index = 0; source = tag.Media = tag.Playlist[0]; }
                state.NextAt = Time.unscaledTime + ListSeconds(tag);
            }
            // A computer: a browser, on its page (or the home page).
            if (tag.Source == ComputerKey) { OpenPage(state, string.IsNullOrWhiteSpace(source) ? Homepage : source, false); return; }
            if (string.IsNullOrWhiteSpace(source)) return;
            // YouTube, Twitch, a live stream: the page, its video filling the screen.
            if (IsWebPage(source)) { OpenPage(state, source, true); return; }
            if (IsVideo(source)) { PlayVideo(state, source); return; }
            LoadPicture(source, picture =>
            {
                if (state.Thing == null || state.Tag.Media != source) return;
                if (picture != null) { ShowPicture(state, picture); return; }
                // Not a picture: a video file with no file type in its link, or a web page.
                contentTypes.TryGetValue(source, out string type);
                if (IsUrl(source) && (type == null || !type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))) OpenPage(state, source, true);
                else PlayVideo(state, source);
            });
        }

        // Its face's size now: the one set, or its width and the picture's shape.
        private static Vector2 FaceSize(MediaState s)
        {
            float w = s.Tag.Width > 0 ? s.Tag.Width : s.Tag.Source == ComputerKey ? ComputerWidth : s.Screen ? ScreenWidth : PosterWidth;
            float h = s.Tag.Height > 0 ? s.Tag.Height : w / (s.Aspect > 0 ? s.Aspect : s.Screen ? 16f / 9 : .75f);
            return new Vector2(w, h);
        }

        private static void Relayout(MediaState s)
        {
            Vector2 size = FaceSize(s);
            LayoutMedia(s.Thing, s.Screen, size.x, size.y, s.Tag.Frame);
            if (s.Tag.Frame <= 0 && s.Thing.Find("Back") is Transform plain && plain.GetComponent<MeshRenderer>() is MeshRenderer own) own.sharedMaterial = FrameMaterial(s.Screen);
            // A picture on a face of another shape (a screen fitted to a TV): cut to fill it, not stretched.
            Vector2 scale = Vector2.one, offset = Vector2.zero;
            if (s.Video == null && s.Aspect > 0 && s.Tag.Height > 0)
            {
                float face = size.x / size.y;
                if (s.Aspect > face) { scale.x = face / s.Aspect; offset.x = (1 - scale.x) / 2; }
                else { scale.y = s.Aspect / face; offset.y = (1 - scale.y) / 2; }
            }
            // A page's frames come top row first: turned the right way up.
            if (s.Page != null) { scale = new Vector2(1, -1); offset = new Vector2(0, 1); }
            s.Material.mainTextureScale = scale; s.Material.mainTextureOffset = offset;
            Moved();
        }

        private static void Show(MediaState s, Texture texture)
        {
            Material m = s.Material;
            bool lit = m.HasProperty("_EmissionMap");
            m.mainTexture = texture;
            if (s.Screen && lit)
            {
                // A screen gives its own light: the picture is in its glow, not its colour.
                m.color = new Color(.02f, .02f, .02f);
                m.SetTexture("_EmissionMap", texture);
                m.SetColor("_EmissionColor", texture != null ? Color.white : Color.black);
                if (texture != null) m.EnableKeyword("_EMISSION"); else m.DisableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", .7f);
            }
            else
            {
                m.color = texture != null ? Color.white : new Color(.45f, .43f, .4f);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", .08f);
            }
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0);
        }

        // Nothing on it (yet): a screen that's off, a blank sheet.
        private static void Blank(MediaState s)
        {
            Show(s, null);
            if (s.Screen) s.Material.color = new Color(.025f, .025f, .03f);
        }

        private static void ShowPicture(MediaState s, Texture2D picture)
        {
            s.Aspect = picture.height > 0 ? (float)picture.width / picture.height : 1;
            Show(s, picture);
            Relayout(s);
        }

        // ---- loading pictures ---------------------------------------------------------------------------------

        private static Texture2D Decode(byte[] data, string name)
        {
            if (data == null || data.Length == 0) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "Hideout Interiors " + name };
            try
            {
                if (!ImageConversion.LoadImage(t, data)) { UnityEngine.Object.Destroy(t); return null; }
            }
            catch (Exception) { UnityEngine.Object.Destroy(t); return null; }
            t.wrapMode = TextureWrapMode.Clamp;
            t.anisoLevel = 4;
            t.filterMode = FilterMode.Trilinear;
            return t;
        }

        private static string CacheFile(string url)
        {
            using (MD5 md5 = MD5.Create())
            {
                string hash = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").ToLowerInvariant();
                return Path.Combine(BepInEx.Paths.CachePath, "HideoutInteriors", hash + ".img");
            }
        }

        // A picture from a file or a link, once each (posters of the same picture share it). Links come from the cache
        // if they've been loaded before; the callback runs on the main thread, with null if it isn't a picture.
        private static void LoadPicture(string source, Action<Texture2D> done)
        {
            if (pictureCache.TryGetValue(source, out Texture2D known) && known != null) { done(known); return; }
            if (pictureLoads.TryGetValue(source, out var waiting)) { waiting.Add(done); return; }
            if (!IsUrl(source))
            {
                string path = MediaPath(source);
                Texture2D t = null;
                try { if (File.Exists(path) && !IsVideo(path)) t = Decode(File.ReadAllBytes(path), Path.GetFileName(path)); }
                catch (Exception e) { Plugin.Log.LogWarning($"Decor: couldn't read {path}: {e.Message}"); }
                if (t != null) pictureCache[source] = t;
                else if (!File.Exists(path)) Plugin.Log.LogWarning($"Decor: no file {path}.");
                done(t);
                return;
            }
            string cache = CacheFile(source);
            try
            {
                if (File.Exists(cache) && Decode(File.ReadAllBytes(cache), MediaName(source)) is Texture2D kept)
                {
                    pictureCache[source] = kept;
                    done(kept);
                    return;
                }
            }
            catch (Exception) { }
            pictureLoads[source] = new List<Action<Texture2D>> { done };
            UnityWebRequest request;
            try
            {
                request = UnityWebRequest.Get(source);
                request.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                request.timeout = 40;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Decor: bad link {source}: {e.Message}");
                Finish(source, null);
                return;
            }
            request.SendWebRequest().completed += _ =>
            {
                Texture2D t = null;
                try
                {
                    string type = request.GetResponseHeader("Content-Type") ?? "";
                    contentTypes[source] = type;
                    if (request.result == UnityWebRequest.Result.Success && !type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] data = request.downloadHandler.data;
                        t = Decode(data, MediaName(source));
                        if (t != null)
                            try { Directory.CreateDirectory(Path.GetDirectoryName(cache)); File.WriteAllBytes(cache, data); } catch (Exception) { }
                        else Plugin.Log.LogInfo($"Decor: {source} isn't a .png or .jpg picture ({type}).");
                    }
                    else if (request.result != UnityWebRequest.Result.Success)
                    {
                        Plugin.Log.LogWarning($"Decor: couldn't load {source}: {request.error}");
                        EFT.Communications.NotificationManager.DisplayWarningNotification($"Couldn't load {MediaName(source)}: {request.error}");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Decor: {source}: {e.Message}"); }
                finally { request.Dispose(); }
                Finish(source, t);
            };
        }

        private static void Finish(string source, Texture2D t)
        {
            if (t != null) pictureCache[source] = t;
            if (!pictureLoads.TryGetValue(source, out var waiting)) return;
            pictureLoads.Remove(source);
            foreach (var done in waiting)
                try { done(t); } catch (Exception e) { Plugin.Log.LogWarning("Decor: " + e.Message); }
        }

        // ---- videos -------------------------------------------------------------------------------------------

        private static void PlayVideo(MediaState s, string source)
        {
            string url = source;
            if (!IsUrl(source))
            {
                url = MediaPath(source);
                if (!File.Exists(url)) { Plugin.Log.LogWarning($"Decor: no file {url}."); return; }
            }
            GameObject face = s.Face != null ? s.Face.gameObject : null;
            if (face == null) return;
            try
            {
                AudioSource audio = face.AddComponent<AudioSource>();
                audio.playOnAwake = false;
                audio.spatialBlend = .75f;
                audio.rolloffMode = AudioRolloffMode.Linear;
                audio.minDistance = 3; audio.maxDistance = 40;
                audio.volume = Loudness(s);
                audio.mute = s.Tag.Muted || !audible;
                VideoPlayer video = face.AddComponent<VideoPlayer>();
                video.playOnAwake = false;
                // One of a list plays once, then the next is on.
                video.isLooping = !HasList(s.Tag);
                video.loopPointReached += p => { if (s.Video == p) s.Ended = true; };
                video.skipOnDrop = true;
                video.waitForFirstFrame = true;
                video.source = VideoSource.Url;
                video.url = url;
                video.renderMode = VideoRenderMode.RenderTexture;
                video.aspectRatio = VideoAspectRatio.FitInside;
                video.audioOutputMode = VideoAudioOutputMode.AudioSource;
                video.controlledAudioTrackCount = 1;
                video.EnableAudioTrack(0, true);
                video.SetTargetAudioSource(0, audio);
                s.Video = video; s.Audio = audio; s.Paused = false;
                video.prepareCompleted += p =>
                {
                    if (s.Video != p || s.Thing == null) return;
                    s.Aspect = p.width > 0 && p.height > 0 ? (float)p.width / p.height : 16f / 9;
                    Relayout(s);
                    Vector2 size = FaceSize(s);
                    int w, h;
                    if (s.Tag.Height > 0) { w = 1280; h = Mathf.Clamp(Mathf.RoundToInt(1280 * size.y / size.x), 64, 2048); }
                    else { w = (int)Mathf.Clamp(p.width, 64, 1920); h = Mathf.Clamp(Mathf.RoundToInt(w / s.Aspect), 64, 2048); }
                    s.Target = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "Hideout Interiors video" };
                    s.Target.Create();
                    p.targetTexture = s.Target;
                    Show(s, s.Target);
                    s.Material.mainTextureScale = Vector2.one; s.Material.mainTextureOffset = Vector2.zero;
                    if (s.ResumeAt > 0 && p.canSetTime && s.ResumeAt < p.length) p.time = s.ResumeAt;
                    s.ResumeAt = 0;
                    if (!s.Paused) p.Play();
                };
                video.errorReceived += (p, message) =>
                {
                    if (s.Video != p) return;
                    Plugin.Log.LogWarning($"Decor: couldn't play {source}: {message}");
                    EFT.Communications.NotificationManager.DisplayWarningNotification($"Couldn't play {MediaName(source)}: {message}");
                };
                video.Prepare();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Decor: couldn't play {source}: {e.Message}"); StopVideo(s); }
        }

        private static void StopVideo(MediaState s)
        {
            ClosePage(s);
            if (s.Video != null) { try { s.Video.Stop(); } catch (Exception) { } UnityEngine.Object.DestroyImmediate(s.Video); }
            if (s.Audio != null) UnityEngine.Object.DestroyImmediate(s.Audio);
            if (s.Target != null) { s.Target.Release(); UnityEngine.Object.Destroy(s.Target); }
            s.Video = null; s.Audio = null; s.Target = null;
        }

        // Each frame in the hideout: sound only while you're in it (not in the menus it stays loaded behind), videos
        // playing again after their area hid and showed them, and what's gone let go of.
        private static void MediaTick()
        {
            if (mediaStates.Count == 0) return;
            if (Time.frameCount % 15 != 0) return;
            bool now = raidCopy || (owner != null && owner.FirstPersonMode) || Overview();
            List<Transform> gone = null;
            List<MediaState> next = null;
            foreach (var pair in mediaStates)
            {
                MediaState s = pair.Value;
                if (s.Thing == null) { (gone = gone ?? new List<Transform>()).Add(pair.Key); continue; }
                if (s.Audio != null) s.Audio.mute = s.Tag.Muted || !now;
                // A list: the next one when a video's over, or a picture (or a page) has had its time.
                if (HasList(s.Tag) && s.Thing.gameObject.activeInHierarchy && !IsPaused(s) && s != usingMedia && !(addressTyping && s == barFor))
                {
                    bool due = s.Video != null ? s.Ended : Time.unscaledTime >= s.NextAt;
                    if (due) { (next = next ?? new List<MediaState>()).Add(s); continue; }
                }
                if (s.Video != null && s.Video.isActiveAndEnabled && s.Video.isPrepared && !s.Video.isPlaying && !s.Paused && !s.Ended) s.Video.Play();
            }
            audible = now;
            if (next != null) foreach (MediaState s in next) ListStep(s, 1);
            if (gone == null) return;
            foreach (Transform t in gone)
            {
                MediaState s = mediaStates[t];
                ClosePage(s);
                if (s.Target != null) { s.Target.Release(); UnityEngine.Object.Destroy(s.Target); }
                if (s.Material != null) UnityEngine.Object.Destroy(s.Material);
                mediaStates.Remove(t);
            }
        }

        private static void ForgetMedia()
        {
            StopUsing();
            BarGone();
            ClosePages();
            foreach (MediaState s in mediaStates.Values)
            {
                if (s.Target != null) { s.Target.Release(); UnityEngine.Object.Destroy(s.Target); }
                if (s.Material != null) UnityEngine.Object.Destroy(s.Material);
            }
            mediaStates.Clear();
            tvs.Clear();
            tvBounds.Clear();
        }

        // ---- putting them in ----------------------------------------------------------------------------------

        // A new poster or screen showing a link or file, carried from the panel (or, pasted, following the mouse
        // until a click).
        private static void StartPlacingMedia(string key, string source, bool fromThePanel)
        {
            Transform pattern = ExtraPattern(key);
            if (pattern == null) return;
            Transform copy = MakeCopy(pattern, DecorRoot(), false);
            if (copy == null) return;
            DecorTag tag = copy.GetComponent<DecorTag>();
            tag.Media = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
            SetupMedia(copy);
            ClearSelection();
            Select(copy);
            newCopies.Add(copy);
            fromPanel = fromThePanel;
            clickToPut = !fromThePanel;
            StartDrag(copy);
            Drag();
            Outlines(); Status();
        }

        // A link pasted (Ctrl+V): onto the selected posters and screens, else as new
        // screens fitted to the selected TVs, else a new poster (a picture) or screen (a video) to put down.
        private static void UseLink(string source, bool orNew)
        {
            source = (source ?? "").Trim().Trim('"');
            if (source.Length == 0)
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification("Copy a link to a picture or video first (or type one in the Media tab's box).");
                return;
            }
            selection.RemoveAll(t => t == null);
            var media = selection.Where(IsMedia).ToList();
            if (media.Count > 0)
            {
                foreach (Transform t in media) SetSource(t, source);
                BarLook();
                EFT.Communications.NotificationManager.DisplayMessageNotification(media.Count == 1 ? $"Showing {MediaName(source)}." : $"{media.Count} posters and screens show {MediaName(source)}.");
                return;
            }
            var tvList = selection.Where(IsTv).ToList();
            if (tvList.Count > 0)
            {
                var made = new List<Transform>();
                foreach (Transform tv in tvList)
                {
                    Transform screen = ScreenOnTv(tv, null, source);
                    if (screen != null) made.Add(screen);
                }
                if (made.Count == 0) return;
                ClearSelection();
                foreach (Transform t in made) Select(t);
                Commit(Belong.AreaPutIn);
                EFT.Communications.NotificationManager.DisplayMessageNotification($"A screen on {(tvList.Count == 1 ? Pretty(tvList[0].name) : tvList.Count + " TVs")} plays {MediaName(source)}. Arrows and +/- to fine-tune.");
                return;
            }
            if (orNew) StartPlacingMedia(IsVideo(source) || IsWebPage(source) ? ScreenKey : PosterKey, source, false);
            else EFT.Communications.NotificationManager.DisplayMessageNotification("Select posters, TV screens or TVs to put the link on, or drag a poster or screen in.");
        }

        // What a poster, screen or computer shows from now on, kept. A page already up just goes to another page.
        private static void SetSource(Transform t, string source)
        {
            if (t == null || !mediaStates.TryGetValue(t, out MediaState s)) return;
            source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
            s.Tag.Media = source;
            if (s.Page != null && source != null && (s.Tag.Source == ComputerKey || IsWebPage(source))) Navigate(s.Page, source);
            else SetupMedia(t);
            KeepMedia(s.Tag);
        }

        // A placed poster's or screen's tag (what it shows, its size, its sound), kept where it's kept.
        private static void KeepMedia(DecorTag tag)
        {
            if (tag == null || tag.Id == null || !decorLayout.TryGetValue(tag.Id, out DecorRecord r)) return;
            Checkpoint();
            r.Take(tag);
            SaveDecor();
        }

        // ---- lists: a slideshow, a playlist --------------------------------------------------------------------

        private static readonly float[] ListTimes = { 5, 10, 20, 30, 60, 120, 300, 600 };

        private static bool HasList(DecorTag tag) => tag.Playlist != null && tag.Playlist.Length > 1;

        // How long a picture (or a page) of a list stays on.
        private static float ListSeconds(DecorTag tag) => tag.Interval >= 1 ? tag.Interval : 10;

        // The next (or the one before) of its list is on. What's kept as on it stays the same: the list starts
        // where it was left.
        private static void ListStep(MediaState s, int step)
        {
            DecorTag tag = s.Tag;
            if (!HasList(tag) || s.Thing == null) return;
            int n = tag.Playlist.Length;
            s.Index = ((s.Index + step) % n + n) % n;
            tag.Media = tag.Playlist[s.Index];
            SetupMedia(s.Thing);
            if (s == barFor) BarLook();
        }

        // ADD TO LIST: what's on now joins its list (the list's made of it, the first time).
        private static void ListAdd(MediaState s)
        {
            string source = SourceOf(s);
            if (source.Length == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Put something on it first, then ADD TO LIST: it goes through its list in order."); return; }
            DecorTag tag = s.Tag;
            var list = (tag.Playlist ?? new string[0]).ToList();
            if (list.Any(e => SameLink(e, source))) { EFT.Communications.NotificationManager.DisplayMessageNotification("That's in its list already."); return; }
            list.Add(source);
            tag.Playlist = list.ToArray();
            s.Index = list.Count - 1;
            s.NextAt = Time.unscaledTime + ListSeconds(tag);
            KeepMedia(tag);
            // A video that was looping plays once from now on, then the next is on.
            if (s.Video != null) s.Video.isLooping = !HasList(tag);
            EFT.Communications.NotificationManager.DisplayMessageNotification(list.Count == 1
                ? "Its list has one thing in it. Put something else on (the box, LIBRARY, NEXT) and ADD TO LIST again: it shows them in turn."
                : $"Its list has {list.Count}: it shows them in turn ({(s.Screen ? "videos to their end, " : "")}pictures {ListSeconds(tag):0} s each).");
        }

        // ALL FILES: its list is every file in the media folder it can show.
        private static void ListAllFiles(MediaState s)
        {
            var files = MediaFiles().Where(f => s.Screen || !IsVideo(f)).ToList();
            if (files.Count < 2) { EFT.Communications.NotificationManager.DisplayMessageNotification("The media folder needs two files or more to go through (FOLDER opens it)."); return; }
            s.Tag.Playlist = files.ToArray();
            s.Tag.Media = files[0];
            SetupMedia(s.Thing);
            KeepMedia(s.Tag);
            EFT.Communications.NotificationManager.DisplayMessageNotification($"It goes through the media folder's {files.Count} files in turn.");
        }

        private static void ListClear(MediaState s)
        {
            if (s.Tag.Playlist == null) return;
            s.Tag.Playlist = null;
            if (s.Video != null) s.Video.isLooping = true;
            s.Ended = false;
            KeepMedia(s.Tag);
        }

        // EVERY: how long each picture stays, through a few times.
        private static void ListTime(MediaState s)
        {
            float now = ListSeconds(s.Tag);
            int at = Array.FindIndex(ListTimes, t => t > now + .5f);
            s.Tag.Interval = ListTimes[at < 0 ? 0 : at];
            s.NextAt = Time.unscaledTime + s.Tag.Interval;
            KeepMedia(s.Tag);
        }

        // FRAME: the next style of frame.
        private static void NextFrame(MediaState s)
        {
            s.Tag.Frame = (s.Tag.Frame + 1) % Frames.Length;
            Relayout(s);
            KeepMedia(s.Tag);
        }

        // A new screen on a TV's front: the face given (by where the mouse is on it), or the upright face that's
        // biggest, of the two the one towards the camera.
        private static Transform ScreenOnTv(Transform tv, Vector3? normal, string source)
        {
            if (!TvFace(tv, normal, out Vector3 centre, out Vector3 outward, out Vector3 up, out Vector2 size)) return null;
            Transform pattern = ExtraPattern(ScreenKey);
            if (pattern == null) return null;
            Transform copy = MakeCopy(pattern, DecorRoot(), false);
            if (copy == null) return null;
            DecorTag tag = copy.GetComponent<DecorTag>();
            tag.Media = source;
            FitToFace(copy, centre, outward, up, size);
            SetupMedia(copy);
            return copy;
        }

        private static void FitToFace(Transform screen, Vector3 centre, Vector3 outward, Vector3 up, Vector2 size)
        {
            DecorTag tag = screen.GetComponent<DecorTag>();
            // Inside the TV's own bezel.
            tag.Width = Mathf.Max(.05f, size.x * .9f - 2 * ScreenBorder);
            tag.Height = Mathf.Max(.05f, size.y * .86f - 2 * ScreenBorder);
            tag.Scale = 1;
            ApplyScale(screen);
            screen.SetPositionAndRotation(centre + outward * .004f, Quaternion.LookRotation(outward, up));
            if (mediaStates.TryGetValue(screen, out MediaState s)) Relayout(s);
            else LayoutMedia(screen, true, tag.Width, tag.Height);
        }

        // A thing's box in its own space (its meshes', looked at once).
        private static readonly Dictionary<Transform, Bounds> tvBounds = new Dictionary<Transform, Bounds>();
        private static bool LocalBounds(Transform thing, out Bounds bounds)
        {
            if (tvBounds.TryGetValue(thing, out bounds)) return true;
            bool any = false;
            Matrix4x4 into = thing.worldToLocalMatrix;
            foreach (MeshFilter f in thing.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null || f.GetComponent<MeshRenderer>() == null) continue;
                Renderer r = f.GetComponent<MeshRenderer>();
                if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) continue;
                Bounds local = f.sharedMesh.bounds;
                Matrix4x4 m = into * f.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
                }
            }
            if (any) tvBounds[thing] = bounds;
            return any;
        }

        // One of a TV's upright faces: its middle, which way it faces, which way is up on it and its size in metres.
        private static bool TvFace(Transform tv, Vector3? normal, out Vector3 centre, out Vector3 outward, out Vector3 up, out Vector2 size)
        {
            centre = outward = up = default; size = default;
            if (!LocalBounds(tv, out Bounds b)) return false;
            Vector3 scale = tv.lossyScale;
            Vector3 worldSize = Vector3.Scale(b.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            Camera camera = ViewCamera();
            int bestAxis = -1, bestSign = 1;
            float best = float.MinValue;
            for (int axis = 0; axis < 3; axis++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 local = Vector3.zero; local[axis] = sign;
                    Vector3 way = tv.TransformDirection(local).normalized;
                    if (Mathf.Abs(way.y) > .6f) continue;   // the top or bottom
                    float score;
                    if (normal != null) score = Vector3.Dot(way, normal.Value);
                    else
                    {
                        // The biggest face, then the one towards the camera.
                        float area = 1;
                        for (int k = 0; k < 3; k++) if (k != axis) area *= worldSize[k];
                        Vector3 toward = camera != null ? (camera.transform.position - tv.TransformPoint(b.center)).normalized : Vector3.up;
                        score = area * 10 + Vector3.Dot(way, toward);
                    }
                    if (score > best) { best = score; bestAxis = axis; bestSign = sign; }
                }
            if (bestAxis < 0) return false;
            Vector3 n = Vector3.zero; n[bestAxis] = bestSign;
            outward = tv.TransformDirection(n).normalized;
            Vector3 c = b.center; c[bestAxis] += bestSign * b.extents[bestAxis];
            centre = tv.TransformPoint(c);
            // Of the face's two sides, the more upright one is its height.
            int u = (bestAxis + 1) % 3, v = (bestAxis + 2) % 3;
            Vector3 uWay = tv.TransformDirection(Axis(u)).normalized, vWay = tv.TransformDirection(Axis(v)).normalized;
            if (Mathf.Abs(uWay.y) > Mathf.Abs(vWay.y)) { up = uWay * Mathf.Sign(uWay.y == 0 ? 1 : uWay.y); size = new Vector2(worldSize[v], worldSize[u]); }
            else { up = vWay * Mathf.Sign(vWay.y == 0 ? 1 : vWay.y); size = new Vector2(worldSize[u], worldSize[v]); }
            return size.x > .05f && size.y > .05f;
        }

        private static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

        // The TV the mouse's ray meets first (its box, as most of the game's TVs have no collider of their own), not
        // farther than a surface it meets: where, and its face's outward way.
        private static Transform TvUnderMouse(Ray ray, float before, out Vector3 normal)
        {
            normal = default;
            RefreshThingBoxes();
            Transform best = null;
            float bestDistance = before;
            foreach (var (t, box, _) in thingBoxes)
            {
                if (t == null || !IsTv(t) || Carried(t) || !box.IntersectRay(ray, out float near) || near > bestDistance) continue;
                if (!LocalBounds(t, out Bounds local)) continue;
                Matrix4x4 into = t.worldToLocalMatrix;
                Vector3 o = into.MultiplyPoint3x4(ray.origin), d = into.MultiplyVector(ray.direction);
                var localRay = new Ray(o, d);
                if (!local.IntersectRay(localRay, out float along)) continue;
                Vector3 hit = localRay.GetPoint(along);
                float distance = Vector3.Distance(ray.origin, t.TransformPoint(hit));
                if (distance > bestDistance) continue;
                // The face hit: the side the point is on.
                int axis = 0; float edge = float.MaxValue; int sign = 1;
                for (int k = 0; k < 3; k++)
                {
                    float toMin = Mathf.Abs(hit[k] - local.min[k]), toMax = Mathf.Abs(hit[k] - local.max[k]);
                    float rel = Mathf.Min(toMin, toMax) / Mathf.Max(local.size[k], .001f);
                    if (rel < edge) { edge = rel; axis = k; sign = toMax < toMin ? 1 : -1; }
                }
                Vector3 n = Vector3.zero; n[axis] = sign;
                Vector3 way = t.TransformDirection(n).normalized;
                if (Mathf.Abs(way.y) > .6f) continue;
                best = t; bestDistance = distance; normal = way;
            }
            return best;
        }

        // A carried poster or screen, alone: flat on the wall under the mouse (R turns it on the wall), standing on
        // what's under it otherwise, and a screen over a TV fits its front (Alt: not).
        private static Transform tvHover;
        // Its size when the carry started, for when it's carried off a TV it was fitted to (or the carry's cancelled).
        private static Transform mediaCarried;
        private static float carriedWidth, carriedHeight, carriedScale;

        private static void MediaSizeBack()
        {
            if (mediaCarried == null) return;
            DecorTag tag = mediaCarried.GetComponent<DecorTag>();
            if (tag != null && (tag.Width != carriedWidth || tag.Height != carriedHeight || tag.Scale != carriedScale))
            {
                tag.Width = carriedWidth; tag.Height = carriedHeight; tag.Scale = carriedScale;
                ApplyScale(mediaCarried);
                if (mediaStates.TryGetValue(mediaCarried, out MediaState s)) Relayout(s);
            }
        }

        private static bool MediaDrag(Transform thing)
        {
            tvHover = null;
            DecorTag carriedTag = thing.GetComponent<DecorTag>();
            if (mediaCarried != thing && carriedTag != null)
            {
                mediaCarried = thing;
                carriedWidth = carriedTag.Width; carriedHeight = carriedTag.Height; carriedScale = carriedTag.Scale;
            }
            Camera camera = ViewCamera();
            if (camera == null) return false;
            Ray ray = MouseRay(camera);
            int count = Physics.RaycastNonAlloc(ray, rayHits, 300, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(rayHits, 0, count, HitDistance.Instance);
            RaycastHit? surface = null;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = rayHits[i];
                if (hit.collider.transform.IsChildOf(thing)) continue;
                if (hit.point.y > SeenThrough && hit.normal.y < -.5f) continue;   // a ceiling seen from above
                if (hit.collider.transform.root.name == ExtensionRooms) continue;
                // A computer stands on a desk or the floor, never on a wall.
                if (IsComputer(thing) && hit.normal.y < .6f) continue;
                surface = hit;
                break;
            }
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            DecorTag tag = thing.GetComponent<DecorTag>();
            if (IsScreen(thing) && !alt)
            {
                Transform tv = TvUnderMouse(ray, surface?.distance ?? 300, out Vector3 normal);
                if (tv != null && TvFace(tv, normal, out Vector3 centre, out Vector3 outward, out Vector3 up, out Vector2 size))
                {
                    tvHover = tv;
                    FitToFace(thing, centre, outward, up, size);
                    return true;
                }
            }
            if (surface == null) return false;
            MediaSizeBack();
            RaycastHit s = surface.Value;
            Vector2 face = mediaStates.TryGetValue(thing, out MediaState state) ? FaceSize(state) : new Vector2(PosterWidth, PosterWidth);
            float scale = tag != null ? tag.Scale : 1;
            if (Mathf.Abs(s.normal.y) < .6f)
            {
                // On a wall: its back on the wall, upright, turned on the wall by R.
                Vector3 outward = new Vector3(s.normal.x, 0, s.normal.z).normalized;
                if (outward.sqrMagnitude < .5f) outward = s.normal;
                thing.SetPositionAndRotation(s.point + outward * .003f, Quaternion.LookRotation(outward, Vector3.up) * Quaternion.Euler(0, 0, -dragYaw));
            }
            else
            {
                // On a floor or a table: standing, facing the camera, R turns it round.
                Vector3 facing = -camera.transform.forward; facing.y = 0;
                if (facing.sqrMagnitude < 1e-4f) facing = Vector3.back;
                Quaternion turn = Quaternion.LookRotation(facing.normalized, Vector3.up) * Quaternion.Euler(0, dragYaw, 0);
                float half = (face.y / 2 + FrameBorder(!IsPoster(thing), tag != null ? tag.Frame : 0) + (IsComputer(thing) ? StandHeight : 0)) * scale;
                thing.SetPositionAndRotation(s.point + Vector3.up * half, turn);
            }
            return true;
        }

        // ---- its sound and its video -------------------------------------------------------------------------

        // How loud one is: its own volume, of the settings'.
        private static float Loudness(MediaState s) => VideoVolume.Value * Mathf.Clamp01(s.Tag.Volume);

        private static bool Plays(MediaState s) => s.Video != null || (s.Page != null && s.Page.Video);
        private static bool Sounds(MediaState s) => s.Video != null || s.Page != null;
        private static bool IsPaused(MediaState s) => s.Page != null ? s.Page.Paused : s.Paused;

        private static void SetVolume(MediaState s, float volume)
        {
            s.Tag.Volume = Mathf.Round(Mathf.Clamp01(volume) * 100) / 100;
            // Turned up from nothing, it's meant to be heard.
            if (s.Tag.Volume > 0 && s.Tag.Muted) SetMuted(s, false);
            if (s.Audio != null) s.Audio.volume = Loudness(s);
        }

        private static void SetMuted(MediaState s, bool mute)
        {
            s.Tag.Muted = mute;
            if (s.Audio != null) s.Audio.mute = mute || !audible;
        }

        private static void SetPaused(MediaState s, bool pause)
        {
            if (s.Page != null)
            {
                s.Page.Paused = pause;
                BrowserSend("cmd", s.Page.Id, pause ? "pause" : "play");
            }
            else if (s.Video != null)
            {
                s.Paused = pause;
                if (pause) s.Video.Pause(); else s.Video.Play();
            }
        }

        // On or back by so many seconds.
        private static void Seek(MediaState s, float seconds)
        {
            if (s.Page != null) BrowserSend("cmd", s.Page.Id, "seek", seconds);
            else if (s.Video != null && s.Video.isPrepared && s.Video.canSetTime)
                s.Video.time = Math.Max(0, Math.Min(s.Video.length - .5, s.Video.time + seconds));
        }

        // P and M: the selected screens' videos paused or playing, their sound off or on.
        private static void PauseSelection()
        {
            var screens = selection.Where(t => t != null && mediaStates.TryGetValue(t, out MediaState s) && Plays(s)).Select(t => mediaStates[t]).ToList();
            if (screens.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Select a TV screen that's playing a video to pause it."); return; }
            bool pause = !screens.All(IsPaused);
            foreach (MediaState s in screens) SetPaused(s, pause);
            BarLook(); Status();
        }

        private static void MuteSelection()
        {
            var screens = selection.Where(t => t != null && mediaStates.TryGetValue(t, out MediaState s) && Sounds(s)).Select(t => mediaStates[t]).ToList();
            if (screens.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Select a TV screen that's playing a video to mute it."); return; }
            bool mute = !screens.All(s => s.Tag.Muted);
            foreach (MediaState s in screens) { SetMuted(s, mute); KeepMedia(s.Tag); }
            BarLook(); Status();
            EFT.Communications.NotificationManager.DisplayMessageNotification(mute ? "Sound off." : "Sound on.");
        }

        private static string MediaText(Transform t)
        {
            DecorTag tag = t.GetComponent<DecorTag>();
            if (tag == null || !IsMediaKey(tag.Source)) return "";
            string what = string.IsNullOrEmpty(tag.Media) ? "nothing on it" : MediaName(tag.Media);
            if (mediaStates.TryGetValue(t, out MediaState s) && Sounds(s)) what += (IsPaused(s) ? " (paused)" : "") + (tag.Muted ? " (muted)" : "");
            return " · " + what;
        }
    }
}
