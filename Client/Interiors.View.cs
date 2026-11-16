using System;
using Cinemachine;
using EFT.Hideout;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// Decor mode's camera views, so what's been placed can be seen from the side and not only from the overview's
    /// height: ORBIT (round the selection), FRONT and SIDE (level, along the blue Z and red X arrows: pressing one again
    /// looks from the other side) and TOP (straight down). The game's overview camera is left where it was: a camera of
    /// ours (one of the game's own kind, a Cinemachine virtual camera) takes over and hands back. In a view: right- or
    /// middle-drag orbits, the wheel comes closer, WASD slides, Q / E go round, F looks at the selection again and
    /// Escape goes back to the overview.
    /// </summary>
    internal static partial class Interiors
    {
        // Free is photo mode's camera (see Interiors.Photo.cs): it has no button in the VIEW row.
        private enum ViewKind { Overview, Orbit, Front, Side, Top, Free }
        private static readonly string[] ViewNames = { "OVERVIEW", "ORBIT", "FRONT", "SIDE", "TOP" };
        private static ViewKind view = ViewKind.Overview;

        private static GameObject viewRig;
        private static CinemachineVirtualCamera viewCamera;
        private static Vector3 viewTarget, targetNow;
        private static float viewYaw, viewPitch, viewDistance, yawNow, pitchNow, distanceNow;
        private static Vector2 viewMouse;
        private static bool viewFlipped;

        private static bool ViewActive => view != ViewKind.Overview;

        private static void SetView(ViewKind kind)
        {
            if (kind == ViewKind.Overview) { ExitView(); return; }
            Camera camera = ViewCamera();
            if (camera == null || style == null) return;
            // A view picked with things selected looks at them.
            if (ViewActive && selection.Count > 0) Focus();
            if (!ViewActive)
            {
                // From where the overview's camera is, so it flies there.
                Focus();
                Vector3 from = camera.transform.position;
                Vector3 look = viewTarget - from;
                targetNow = viewTarget;
                distanceNow = Mathf.Max(.5f, look.magnitude);
                yawNow = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
                pitchNow = Mathf.Asin(Mathf.Clamp(-look.y / distanceNow, -1, 1)) * Mathf.Rad2Deg;
                viewYaw = yawNow;
                if (!EnsureRig()) return;
            }
            switch (kind)
            {
                case ViewKind.Orbit:
                    viewPitch = 28;
                    break;
                case ViewKind.Front:
                case ViewKind.Side:
                    // Again: from the other side.
                    viewFlipped = view == kind && !viewFlipped;
                    if (view != kind) viewFlipped = false;
                    viewYaw = (kind == ViewKind.Front ? 0 : 90) + (viewFlipped ? 180 : 0);
                    viewPitch = 6;
                    break;
                case ViewKind.Top:
                    viewPitch = 89;
                    break;
            }
            // The shortest way round to the new heading.
            viewYaw = yawNow + Mathf.DeltaAngle(yawNow, viewYaw);
            view = kind;
            viewMouse = Input.mousePosition;
            viewRig.SetActive(true);
            viewCamera.Priority = 60;
            PlaceRig();
            ModeButtons();
            Status();
        }

        // The camera to look at a point from a heading, a height and a distance (in a view, or into one).
        private static void FlyTo(Vector3 target, float yaw, float pitch, float distance)
        {
            Camera camera = ViewCamera();
            if (camera == null || style == null) return;
            if (!ViewActive)
            {
                Vector3 look = target - camera.transform.position;
                targetNow = target;
                distanceNow = Mathf.Max(.5f, look.magnitude);
                yawNow = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
                pitchNow = Mathf.Asin(Mathf.Clamp(-look.y / distanceNow, -1, 1)) * Mathf.Rad2Deg;
                if (!EnsureRig()) return;
            }
            viewTarget = target;
            viewYaw = yawNow + Mathf.DeltaAngle(yawNow, yaw);
            viewPitch = Mathf.Clamp(pitch, -15, 89);
            viewDistance = distance;
            view = ViewKind.Orbit;
            viewMouse = Input.mousePosition;
            viewRig.SetActive(true);
            viewCamera.Priority = 60;
            PlaceRig();
            ModeButtons();
        }

        private static void CycleView() => SetView(view == ViewKind.Top ? ViewKind.Overview : (ViewKind)((int)view + 1));

        private static void ExitView()
        {
            if (!ViewActive) return;
            view = ViewKind.Overview;
            if (viewRig != null) viewRig.SetActive(false);
            ModeButtons();
            Status();
        }

        private static void ForgetView()
        {
            view = ViewKind.Overview;
            if (viewRig != null) UnityEngine.Object.Destroy(viewRig);
            viewRig = null; viewCamera = null;
        }

        // A virtual camera with the overview's lens, above the game's in priority while a view's on.
        private static bool EnsureRig()
        {
            if (viewRig != null && viewCamera != null) return true;
            try
            {
                viewRig = new GameObject("Hideout Interiors view camera");
                viewRig.SetActive(false);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(viewRig, style.scene);
                viewCamera = viewRig.AddComponent<CinemachineVirtualCamera>();
                HideoutCamera theirs = UnityEngine.Object.FindObjectOfType<HideoutCamera>();
                LensSettings lens = theirs != null && theirs._virtualCamera != null ? theirs._virtualCamera.m_Lens : viewCamera.m_Lens;
                Camera camera = ViewCamera();
                lens.FieldOfView = Mathf.Clamp(camera != null ? camera.fieldOfView : 50, 40, 60);
                lens.NearClipPlane = .05f;
                if (camera != null) lens.FarClipPlane = Mathf.Max(camera.farClipPlane, 100);
                viewCamera.m_Lens = lens;
                viewCamera.Priority = 0;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Decor: the camera views couldn't be set up: " + e);
                EFT.Communications.NotificationManager.DisplayWarningNotification("The camera views couldn't be set up (see the log).");
                if (viewRig != null) UnityEngine.Object.Destroy(viewRig);
                viewRig = null; viewCamera = null;
                return false;
            }
        }

        // What a view looks at: the selection's middle, a bit back from its size; with nothing selected, the middle of
        // the screen where it meets the floor.
        private static void Focus()
        {
            bool any = false;
            Bounds all = default;
            foreach (Transform t in selection)
            {
                if (t == null || !Bounds(t, out Bounds b)) continue;
                if (!any) { all = b; any = true; } else all.Encapsulate(b);
            }
            if (any)
            {
                viewTarget = all.center;
                viewDistance = Mathf.Clamp(all.extents.magnitude * 2.6f + .9f, 1.3f, 16f);
                return;
            }
            Camera camera = ViewCamera();
            viewDistance = 6;
            if (camera == null) return;
            Ray ray = camera.ViewportPointToRay(new Vector3(.5f, .5f, 0));
            int count = Physics.RaycastNonAlloc(ray, rayHits, 300, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(rayHits, 0, count, HitDistance.Instance);
            for (int i = 0; i < count; i++)
            {
                if (rayHits[i].point.y > SeenThrough || rayHits[i].collider.transform.root.name == ExtensionRooms) continue;
                viewTarget = rayHits[i].point + Vector3.up * .8f;
                return;
            }
            viewTarget = new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float d) ? ray.GetPoint(d) + Vector3.up * .8f : camera.transform.position + camera.transform.forward * 8;
        }

        private static void Refocus()
        {
            if (!ViewActive) { SetView(ViewKind.Orbit); return; }
            Focus();
            Status();
        }

        // Each frame of decor mode while a view's on: the keys and mouse that steer it, and the camera eased there.
        private static void ViewTick(bool overPanel)
        {
            if (!ViewActive || viewRig == null || photoOn) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
            Vector2 mouse = Input.mousePosition;
            Vector2 moved = mouse - viewMouse;
            viewMouse = mouse;
            if (!KeysTaken && !Using)
            {
                float round = (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0);
                viewYaw += round * 90 * dt;
                float ahead = (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0);
                float side = (Input.GetKey(KeyCode.D) && !Ctrl() ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0);
                if (ahead != 0 || side != 0)
                {
                    Quaternion heading = Quaternion.Euler(0, viewYaw, 0);
                    float speed = Mathf.Clamp(viewDistance * .9f, 1.2f, 8f) * (Input.GetKey(KeyCode.LeftShift) ? 2.5f : 1);
                    viewTarget += (heading * Vector3.forward * ahead + heading * Vector3.right * side) * speed * dt;
                }
            }
            // Orbiting: the middle button always, the right one when nothing's being carried (there it cancels).
            bool orbit = !Using && (Input.GetMouseButton(2) || (Input.GetMouseButton(1) && !dragging && axisDrag < 0));
            if (orbit && !Input.GetMouseButtonDown(1) && !Input.GetMouseButtonDown(2))
            {
                float k = 1080f / Mathf.Max(Screen.height, 1) * .22f;
                viewYaw += moved.x * k;
                viewPitch = Mathf.Clamp(viewPitch - moved.y * k, -15, 89);
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0 && !overPanel && !Using) viewDistance = Mathf.Clamp(viewDistance * Mathf.Pow(.88f, wheel), .4f, 30f);
            float ease = 1 - Mathf.Exp(-dt * 10);
            yawNow = Mathf.Lerp(yawNow, viewYaw, ease);
            pitchNow = Mathf.Lerp(pitchNow, viewPitch, ease);
            distanceNow = Mathf.Lerp(distanceNow, viewDistance, ease);
            targetNow = Vector3.Lerp(targetNow, viewTarget, ease);
            PlaceRig();
        }

        private static void PlaceRig()
        {
            Quaternion turn = Quaternion.Euler(pitchNow, yawNow, 0);
            Vector3 back = turn * Vector3.back;
            viewRig.transform.SetPositionAndRotation(targetNow + back * Clearance(targetNow, back, distanceNow), turn);
        }

        // How far back the camera can go before a wall (things are looked past, and ceilings from above).
        private static float Clearance(Vector3 from, Vector3 back, float distance)
        {
            int count = Physics.RaycastNonAlloc(new Ray(from, back), rayHits, distance, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(rayHits, 0, count, HitDistance.Instance);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = rayHits[i];
                if (hit.point.y > SeenThrough || hit.collider.transform.root.name == ExtensionRooms) continue;
                if (ItemOf(hit.collider.transform, out _) != null) continue;
                return Mathf.Max(.2f, hit.distance - .1f);
            }
            return distance;
        }

        private static string ViewText()
        {
            if (!ViewActive || view == ViewKind.Free) return "";
            string name = view == ViewKind.Front ? (viewFlipped ? "BACK" : "FRONT") : view == ViewKind.Side ? (viewFlipped ? "OTHER SIDE" : "SIDE") : ViewNames[(int)view];
            return $"<color=#ffb840>VIEW: {name}</color> · right-drag: orbit · wheel: closer · WASD: slide · Q/E: round · F: look at selection · Esc: overview\n";
        }

        private static bool Ctrl() => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }
}
