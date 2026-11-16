using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// Photo mode (C in decor mode): the screen clear of every button, panel, outline and gizmo, and a camera that
    /// flies free: WASD and Q / E move it, a right-drag looks about, the wheel zooms. Space takes a picture, kept in
    /// BepInEx/plugins/HideoutInteriors/photos. Camera spots make a walkthrough: Shift+1 to 9 keeps where the camera
    /// is, 1 to 9 flies to a spot, T flies through them all in order. The spots are kept per profile.
    /// </summary>
    internal static partial class Interiors
    {
        internal static ConfigEntry<KeyCode> PhotoKey;
        internal static ConfigEntry<int> PhotoSize;
        internal static ConfigEntry<float> TourSeconds;

        private sealed class CameraSpot { public float[] Position; public float Yaw, Pitch, Fov = 50; }

        private static bool photoOn;
        private static Vector3 photoAt;
        private static float photoYaw, photoPitch, photoFov = 50, fovBefore = 50;
        private static Vector2 photoMouse;
        private static Canvas photoHost;
        private static readonly List<Canvas> photoCanvases = new List<Canvas>();
        private static bool uiWasOn;
        // A flight from one view to another, and the tour (the spot it's on its way to).
        private static bool flying;
        private static Vector3 flyFrom, flyTo;
        private static Quaternion flyFromTurn, flyToTurn;
        private static float flyFromFov, flyToFov, flyStart, flyTime;
        private static int tourAt = -1;
        private static float tourHoldUntil;
        private static CameraSpot[] spots;
        private static string spotsFile;

        internal static void BindPhoto(ConfigFile config)
        {
            PhotoKey = config.Bind("Photo mode", "Take a picture", KeyCode.Space, "In photo mode (C in decor mode), the key that takes a picture.");
            PhotoSize = config.Bind("Photo mode", "Picture size", 1,
                new ConfigDescription("How many times the screen's size a picture is (2 on a 1080p screen is a 4K picture).", new AcceptableValueRange<int>(1, 4)));
            TourSeconds = config.Bind("Photo mode", "Tour seconds per spot", 4f,
                new ConfigDescription("How long the walkthrough takes to fly from one camera spot to the next.", new AcceptableValueRange<float>(1f, 20f)));
        }

        private static string PhotoFolder => Path.Combine(Plugin.Folder, "photos");

        // ---- in and out ---------------------------------------------------------------------------------------

        private static void EnterPhoto()
        {
            if (photoOn || !decorOn || Using || dragging || axisDrag >= 0) return;
            Camera camera = ViewCamera();
            if (camera == null || style == null || !EnsureRig()) return;
            CancelAsk(); StopTyping(); StopAddress();
            photoAt = camera.transform.position;
            Vector3 look = camera.transform.forward;
            photoYaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
            photoPitch = Mathf.Asin(Mathf.Clamp(-look.y, -1, 1)) * Mathf.Rad2Deg;
            fovBefore = viewCamera.m_Lens.FieldOfView;
            photoFov = fovBefore;
            photoMouse = Input.mousePosition;
            flying = false; tourAt = -1;
            photoOn = true;
            view = ViewKind.Free;
            viewRig.SetActive(true);
            viewCamera.Priority = 60;
            PlacePhoto();
            // Nothing of the mod's or the game's screen in the picture.
            hovered = null;
            HideGizmo();
            ItemOutline.Clear();
            DecorTip(null);
            if (ui != null) { uiWasOn = ui.activeSelf; ui.SetActive(false); }
            // Every screen canvas the game has up (the overview's, the menu bar along the bottom, the version line).
            photoHost = HostCanvas();
            photoCanvases.Clear();
            foreach (Canvas c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                if (c != null && c.isRootCanvas && c.enabled && c.renderMode != RenderMode.WorldSpace) { c.enabled = false; photoCanvases.Add(c); }
            ModeButtons();
            EFT.Communications.NotificationManager.DisplayMessageNotification(
                $"Photo mode. WASD, Q / E: fly · right-drag: look · wheel: zoom · {PhotoKey.Value}: take a picture · Shift+1 to 9: keep this camera spot · 1 to 9: fly to it · T: tour the spots · Esc: done");
        }

        private static void ExitPhoto()
        {
            if (!photoOn) return;
            photoOn = false;
            flying = false; tourAt = -1;
            foreach (Canvas c in photoCanvases) if (c != null) c.enabled = true;
            photoCanvases.Clear();
            photoHost = null;
            if (ui != null && uiWasOn) ui.SetActive(true);
            if (viewCamera != null)
            {
                LensSettingsBack();
            }
            ExitView();
            if (decorOn) { Outlines(); Status(); }
        }

        private static void LensSettingsBack()
        {
            var lens = viewCamera.m_Lens;
            lens.FieldOfView = fovBefore;
            viewCamera.m_Lens = lens;
        }

        private static void PlacePhoto()
        {
            if (viewRig == null || viewCamera == null) return;
            viewRig.transform.SetPositionAndRotation(photoAt, Quaternion.Euler(photoPitch, photoYaw, 0));
            var lens = viewCamera.m_Lens;
            if (Mathf.Abs(lens.FieldOfView - photoFov) > .01f) { lens.FieldOfView = photoFov; viewCamera.m_Lens = lens; }
        }

        // ---- each frame ---------------------------------------------------------------------------------------

        private static void PhotoTick()
        {
            if (viewRig == null || viewCamera == null) { ExitPhoto(); return; }
            float dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
            Vector2 mouse = Input.mousePosition;
            Vector2 moved = mouse - photoMouse;
            photoMouse = mouse;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            if (Input.GetKeyDown(PhotoKey.Value)) TakePhoto();
            for (int i = 0; i < 9; i++)
            {
                if (!Input.GetKeyDown(KeyCode.Alpha1 + i) && !Input.GetKeyDown(KeyCode.Keypad1 + i)) continue;
                if (Ctrl()) ClearSpot(i);
                else if (shift) KeepSpot(i);
                else FlyToSpot(i, 1.8f);
            }
            if (Input.GetKeyDown(KeyCode.T)) { if (tourAt >= 0) StopTour(); else StartTour(); }

            if (flying) { FlyTick(); PlacePhoto(); return; }
            if (tourAt >= 0) { TourTick(); PlacePhoto(); return; }

            // Flying free: along where the camera looks, level sideways, Q down and E up.
            float ahead = (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0);
            float side = (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0);
            float rise = (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0);
            if (ahead != 0 || side != 0 || rise != 0)
            {
                Quaternion turn = Quaternion.Euler(photoPitch, photoYaw, 0);
                float speed = 2.2f * (shift ? 3f : 1) * (Ctrl() ? .25f : 1);
                photoAt += (turn * Vector3.forward * ahead + Quaternion.Euler(0, photoYaw, 0) * Vector3.right * side + Vector3.up * rise) * speed * dt;
            }
            bool looking = Input.GetMouseButton(1) || Input.GetMouseButton(2);
            if (looking && !Input.GetMouseButtonDown(1) && !Input.GetMouseButtonDown(2))
            {
                float k = 1080f / Mathf.Max(Screen.height, 1) * .16f * (photoFov / 50f);
                photoYaw += moved.x * k;
                photoPitch = Mathf.Clamp(photoPitch - moved.y * k, -89, 89);
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0) photoFov = Mathf.Clamp(photoFov * Mathf.Pow(.92f, wheel), 12, 95);
            PlacePhoto();
        }

        // ---- pictures -----------------------------------------------------------------------------------------

        private static void TakePhoto()
        {
            try
            {
                Directory.CreateDirectory(PhotoFolder);
                string name = "hideout-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
                string path = Path.Combine(PhotoFolder, name + ".png");
                for (int n = 2; File.Exists(path); n++) path = Path.Combine(PhotoFolder, $"{name}-{n}.png");
                ScreenCapture.CaptureScreenshot(path, Mathf.Clamp(PhotoSize.Value, 1, 4));
                Plugin.Log.LogInfo("Photo: " + path);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("The picture couldn't be taken: " + e.Message);
                EFT.Communications.NotificationManager.DisplayWarningNotification("The picture couldn't be taken (see the log).");
            }
        }

        // ---- camera spots -------------------------------------------------------------------------------------

        private static CameraSpot[] Spots()
        {
            string file = profileId != null ? KeptIn("cameras", profileId) : null;
            if (spots != null && file == spotsFile) return spots;
            spots = new CameraSpot[9];
            spotsFile = file;
            try
            {
                if (file != null && File.Exists(file))
                {
                    CameraSpot[] kept = JsonConvert.DeserializeObject<CameraSpot[]>(File.ReadAllText(file));
                    for (int i = 0; kept != null && i < kept.Length && i < 9; i++)
                        if (kept[i]?.Position?.Length == 3) spots[i] = kept[i];
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't read {file}, so there are no camera spots: {e.Message}"); }
            return spots;
        }

        private static void SaveSpots()
        {
            if (spotsFile == null) return;
            try { File.WriteAllText(spotsFile, JsonConvert.SerializeObject(Spots(), Formatting.Indented)); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't save {spotsFile}: {e.Message}"); }
        }

        private static void KeepSpot(int i)
        {
            Spots()[i] = new CameraSpot { Position = new[] { photoAt.x, photoAt.y, photoAt.z }, Yaw = photoYaw, Pitch = photoPitch, Fov = photoFov };
            SaveSpots();
            EFT.Communications.NotificationManager.DisplayMessageNotification($"Camera spot {i + 1} kept ({Spots().Count(s => s != null)} in all). {i + 1} flies back to it; T tours them.");
        }

        private static void ClearSpot(int i)
        {
            if (Spots()[i] == null) return;
            Spots()[i] = null;
            SaveSpots();
            EFT.Communications.NotificationManager.DisplayMessageNotification($"Camera spot {i + 1} forgotten.");
        }

        private static void FlyToSpot(int i, float seconds)
        {
            CameraSpot spot = Spots()[i];
            if (spot == null)
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification($"No camera spot {i + 1} yet: Shift+{i + 1} keeps where the camera is now.");
                return;
            }
            tourAt = -1;
            StartFlight(spot, seconds);
        }

        private static void StartFlight(CameraSpot spot, float seconds)
        {
            flyFrom = photoAt; flyFromTurn = Quaternion.Euler(photoPitch, photoYaw, 0); flyFromFov = photoFov;
            flyTo = new Vector3(spot.Position[0], spot.Position[1], spot.Position[2]);
            flyToTurn = Quaternion.Euler(spot.Pitch, spot.Yaw, 0);
            flyToFov = Mathf.Clamp(spot.Fov, 12, 95);
            flyStart = Time.unscaledTime; flyTime = Mathf.Max(.05f, seconds);
            flying = true;
        }

        // Eased: slow away, slow in.
        private static void FlyTick()
        {
            float t = Mathf.Clamp01((Time.unscaledTime - flyStart) / flyTime);
            float eased = t * t * (3 - 2 * t);
            photoAt = Vector3.Lerp(flyFrom, flyTo, eased);
            Vector3 angles = Quaternion.Slerp(flyFromTurn, flyToTurn, eased).eulerAngles;
            photoYaw = angles.y;
            photoPitch = angles.x > 180 ? angles.x - 360 : angles.x;
            photoFov = Mathf.Lerp(flyFromFov, flyToFov, eased);
            if (t >= 1) flying = false;
        }

        private static void StartTour()
        {
            var have = Enumerable.Range(0, 9).Where(i => Spots()[i] != null).ToList();
            if (have.Count < 2)
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification("A tour needs two camera spots or more: fly somewhere and press Shift+1, somewhere else and Shift+2...");
                return;
            }
            // From the first spot, straight away.
            CameraSpot first = Spots()[have[0]];
            photoAt = new Vector3(first.Position[0], first.Position[1], first.Position[2]);
            photoYaw = first.Yaw; photoPitch = first.Pitch; photoFov = Mathf.Clamp(first.Fov, 12, 95);
            tourAt = have[0];
            tourHoldUntil = Time.unscaledTime + .8f;
            flying = false;
        }

        private static void StopTour() { tourAt = -1; flying = false; }

        // On a spot: a moment there, then on to the next one (after the last, it's over).
        private static void TourTick()
        {
            if (Time.unscaledTime < tourHoldUntil) return;
            int next = -1;
            for (int i = tourAt + 1; i < 9; i++) if (Spots()[i] != null) { next = i; break; }
            if (next < 0) { tourAt = -1; return; }
            tourAt = next;
            StartFlight(Spots()[next], TourSeconds.Value);
            tourHoldUntil = Time.unscaledTime + TourSeconds.Value + .8f;
        }
    }
}
