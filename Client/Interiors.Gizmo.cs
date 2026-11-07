using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace HideoutInteriors
{
    /// <summary>
    /// Decor mode's gizmo on the selection, drawn over everything at the same size on screen however far the camera is,
    /// in two kinds (G, or the panel's MOVE / TURN, switches): arrows (red X, green Y up, blue Z) to slide the selection
    /// along just that line, and three quarter rings to turn it about X, Y or Z, about the middle of its underside.
    /// Ctrl steps (5 cm, 15°); the line at the bottom shows where it is or how far it's turned. Its size and thickness
    /// are in the settings.
    /// </summary>
    internal static partial class Interiors
    {
        internal static ConfigEntry<float> GizmoLength, GizmoThickness;
        internal static ConfigEntry<KeyCode> GizmoKey;

        private enum GizmoKind { Move, Turn }
        private static GizmoKind gizmoKind = GizmoKind.Move;

        private static GameObject gizmo;
        private static Transform moveSet, turnSet;
        private static readonly Transform[] rings = new Transform[3];
        private static readonly Material[] axisMats = new Material[3];
        private static readonly Vector3[] AxisWays = { Vector3.right, Vector3.up, Vector3.forward };
        // Each ring lies across the other two axes: about X it spans Y and Z, and so on.
        private static readonly Vector3[] RingUp = { Vector3.forward, Vector3.right, Vector3.up };
        private static readonly Color[] AxisColors = { new Color(.95f, .2f, .2f), new Color(.25f, .9f, .25f), new Color(.25f, .45f, 1f) };
        private static readonly string[] AxisNames = { "X", "Y", "Z" };
        private static readonly Color AxisHot = new Color(1f, .9f, .2f);
        private static int axisHot = -1, axisDrag = -1;
        private static Vector3 axisOrigin;
        private static float axisFrom, axisMoved;
        private static readonly List<(Transform thing, Vector3 from, Quaternion turn)> axisStart = new List<(Transform, Vector3, Quaternion)>();
        // A turn: where on the ring's plane the mouse started (or, looking along the ring, where on screen), and how far.
        private static Vector3 turnFrom;
        private static float turnLast;
        private static bool turnByScreen;
        private static Vector2 turnMouse;

        // The rings' outside, as a part of the gizmo's size (the arrows' tips are at 1), and how wide their band is.
        private const float RingOuter = .95f, RingBand = .16f;

        internal static void BindGizmo(ConfigFile config)
        {
            GizmoLength = config.Bind("Decorating", "Gizmo size", 1f,
                new ConfigDescription("How big decor mode's move arrows and turn rings are (1 = as made).", new AcceptableValueRange<float>(.3f, 3f)));
            GizmoThickness = config.Bind("Decorating", "Gizmo thickness", 1f,
                new ConfigDescription("How thick the move arrows and turn rings are (1 = as made).", new AcceptableValueRange<float>(.3f, 3f)));
            GizmoKey = config.Bind("Decorating", "Switch move / turn", KeyCode.G, "The key that switches decor mode's gizmo between moving and turning.");
            // A new thickness: made again.
            GizmoThickness.SettingChanged += (_, __) => { if (gizmo != null) UnityEngine.Object.Destroy(gizmo); gizmo = null; };
        }

        private static void BuildGizmo()
        {
            if (gizmo != null) return;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) { Plugin.Log.LogWarning("Decor: no shader for the gizmo."); return; }
            float thick = GizmoThickness.Value;
            gizmo = new GameObject("Hideout extensions gizmo");
            if (style != null) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(gizmo, style.scene);
            moveSet = new GameObject("Move").transform; moveSet.SetParent(gizmo.transform, false);
            turnSet = new GameObject("Turn").transform; turnSet.SetParent(gizmo.transform, false);
            for (int i = 0; i < 3; i++)
            {
                var mat = new Material(shader) { name = "Hideout extensions gizmo " + AxisNames[i] };
                mat.SetInt("_ZTest", (int)CompareFunction.Always);
                mat.SetInt("_ZWrite", 0);
                mat.SetInt("_Cull", (int)CullMode.Off);
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                mat.renderQueue = 5000;
                mat.color = AxisColors[i];
                axisMats[i] = mat;
                // An arrow along local up: a shaft and a head.
                var arrow = new GameObject(AxisNames[i]).transform;
                arrow.SetParent(moveSet, false);
                arrow.localRotation = Quaternion.FromToRotation(Vector3.up, AxisWays[i]);
                Part(arrow, PrimitiveType.Cylinder, new Vector3(0, .42f, 0), new Vector3(.028f * thick, .42f, .028f * thick), mat);
                Part(arrow, PrimitiveType.Cylinder, new Vector3(0, .88f, 0), new Vector3(.085f * thick, .045f, .085f * thick), mat);
                Part(arrow, PrimitiveType.Cube, new Vector3(0, .97f, 0), new Vector3(.055f * thick, .11f, .055f * thick), mat);
                // A quarter ring in its plane (local x and y), turned and mirrored each frame to face the camera.
                var ring = new GameObject("Turn " + AxisNames[i], typeof(MeshFilter), typeof(MeshRenderer)).transform;
                ring.SetParent(turnSet, false);
                ring.GetComponent<MeshFilter>().sharedMesh = QuarterRing(RingOuter - RingBand * thick, RingOuter, 28);
                var r = ring.GetComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                rings[i] = ring;
            }
            gizmo.SetActive(false);
        }

        private static Mesh QuarterRing(float inner, float outer, int steps)
        {
            var vertices = new Vector3[(steps + 1) * 2];
            var colors = new Color[vertices.Length];
            var triangles = new int[steps * 6];
            for (int s = 0; s <= steps; s++)
            {
                float a = s * Mathf.PI / 2 / steps;
                var way = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                vertices[s * 2] = way * inner; vertices[s * 2 + 1] = way * outer;
                colors[s * 2] = colors[s * 2 + 1] = Color.white;
                if (s == steps) continue;
                int v = s * 2, t = s * 6;
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 3;
                triangles[t + 3] = v; triangles[t + 4] = v + 3; triangles[t + 5] = v + 2;
            }
            var mesh = new Mesh { name = "Hideout extensions quarter ring", vertices = vertices, colors = colors, triangles = triangles };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void Part(Transform parent, PrimitiveType kind, Vector3 at, Vector3 size, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(kind);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at; go.transform.localScale = size;
            // The shader multiplies by the mesh's colours, which the built-in shapes don't have.
            Mesh mesh = go.GetComponent<MeshFilter>().mesh;
            var white = new Color[mesh.vertexCount];
            for (int i = 0; i < white.Length; i++) white[i] = Color.white;
            mesh.colors = white;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
        }

        // Where the gizmo stands: the middle of the selection's underside (so Y lifts it off what it's on, and it turns
        // about where it stands).
        private static bool SelectionPivot(out Vector3 pivot)
        {
            pivot = default;
            bool any = false;
            Bounds all = default;
            foreach (Transform t in selection)
            {
                if (t == null || !Bounds(t, out Bounds b)) continue;
                if (!any) { all = b; any = true; } else all.Encapsulate(b);
            }
            if (any) pivot = new Vector3(all.center.x, all.min.y, all.center.z);
            return any;
        }

        private static float GizmoSize(Vector3 at)
        {
            Camera camera = ViewCamera();
            float size = camera == null ? 1 : Mathf.Clamp(Vector3.Distance(camera.transform.position, at) * .055f, .2f, 6f);
            return size * GizmoLength.Value;
        }

        // Each frame in decor mode: shown on a selection that isn't being carried.
        private static void Gizmo()
        {
            bool show = decorOn && !dragging && !boxing && selection.Count > 0 && (axisDrag >= 0 || SelectionPivot(out axisOrigin));
            if (show && gizmo == null) BuildGizmo();
            if (gizmo == null) return;
            if (gizmo.activeSelf != show) gizmo.SetActive(show);
            if (!show) return;
            moveSet.gameObject.SetActive(gizmoKind == GizmoKind.Move);
            turnSet.gameObject.SetActive(gizmoKind == GizmoKind.Turn);
            // While an arrow's dragged the arrows go along (the drag itself measures from where they started).
            gizmo.transform.position = axisDrag >= 0 && gizmoKind == GizmoKind.Move ? axisOrigin + AxisWays[axisDrag] * axisMoved : axisOrigin;
            gizmo.transform.localScale = Vector3.one * GizmoSize(axisOrigin);
            if (gizmoKind == GizmoKind.Turn)
            {
                // Each quarter ring in the quarter facing the camera.
                Camera camera = ViewCamera();
                Vector3 look = camera != null ? camera.transform.position - axisOrigin : Vector3.one;
                for (int i = 0; i < 3; i++)
                {
                    Vector3 up = RingUp[i], right = Vector3.Cross(up, AxisWays[i]);
                    rings[i].localRotation = Quaternion.LookRotation(AxisWays[i], up);
                    rings[i].localScale = new Vector3(Vector3.Dot(look, right) < 0 ? -1 : 1, Vector3.Dot(look, up) < 0 ? -1 : 1, 1);
                }
            }
            for (int i = 0; i < 3; i++) axisMats[i].color = i == axisHot || i == axisDrag ? AxisHot : AxisColors[i];
        }

        private static void HideGizmo()
        {
            axisHot = axisDrag = -1;
            if (gizmo != null) gizmo.SetActive(false);
        }

        private static void SwitchGizmo()
        {
            gizmoKind = gizmoKind == GizmoKind.Move ? GizmoKind.Turn : GizmoKind.Move;
            axisHot = -1;
            ModeButtons();
            Status();
        }

        // The arrow or ring the mouse is on (within a few pixels of it on screen), or -1.
        private static int AxisUnderMouse()
        {
            if (gizmo == null || !gizmo.activeSelf) return -1;
            Camera camera = ViewCamera();
            if (camera == null) return -1;
            Vector2 mouse = Input.mousePosition;
            float size = gizmo.transform.localScale.x, reach = (10 + 4 * GizmoThickness.Value) * Screen.height / 1080f;
            int best = -1;
            float bestGap = reach;
            for (int i = 0; i < 3; i++)
            {
                float gap = float.MaxValue;
                if (gizmoKind == GizmoKind.Move)
                {
                    // Along the arrow, past the first fifth where all three meet.
                    if (Screen2(camera, axisOrigin + AxisWays[i] * size * .2f, out Vector2 a) && Screen2(camera, axisOrigin + AxisWays[i] * size * 1.07f, out Vector2 b))
                        gap = SegmentGap(mouse, a, b);
                }
                else
                {
                    // Along the middle of the band, in the quarter it's drawn in.
                    Transform ring = rings[i];
                    float middle = RingOuter - RingBand * GizmoThickness.Value / 2;
                    bool had = false;
                    Vector2 last = default;
                    for (int s = 0; s <= 12; s++)
                    {
                        float t = s * Mathf.PI / 2 / 12;
                        if (!Screen2(camera, ring.TransformPoint(new Vector3(Mathf.Cos(t), Mathf.Sin(t), 0) * middle), out Vector2 p)) { had = false; continue; }
                        if (had) gap = Mathf.Min(gap, SegmentGap(mouse, last, p));
                        last = p; had = true;
                    }
                }
                if (gap < bestGap) { bestGap = gap; best = i; }
            }
            return best;
        }

        private static bool Screen2(Camera camera, Vector3 world, out Vector2 screen)
        {
            Vector3 v = camera.WorldToViewportPoint(world);
            screen = new Vector2(v.x * Screen.width, v.y * Screen.height);
            return v.z > 0;
        }

        private static float SegmentGap(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude < 1 ? 0 : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        // How far along an arrow's line the mouse is: the point on the line nearest the mouse's ray.
        private static bool AlongAxis(int axis, out float along)
        {
            along = 0;
            Camera camera = ViewCamera();
            if (camera == null) return false;
            Ray ray = MouseRay(camera);
            Vector3 u = AxisWays[axis], v = ray.direction, w = axisOrigin - ray.origin;
            float b = Vector3.Dot(u, v), d = Vector3.Dot(u, w), e = Vector3.Dot(v, w);
            float denominator = 1 - b * b;
            if (Mathf.Abs(denominator) < 1e-4f) return false;   // looking straight down the arrow
            along = (b * e - d) / denominator;
            return true;
        }

        // Where the mouse is on a ring's plane, from the gizmo's middle.
        private static bool OnRingPlane(int axis, out Vector3 way)
        {
            way = default;
            Camera camera = ViewCamera();
            if (camera == null) return false;
            Ray ray = MouseRay(camera);
            if (Mathf.Abs(Vector3.Dot(ray.direction, AxisWays[axis])) < .12f) return false;   // looking along the ring
            if (!new Plane(AxisWays[axis], axisOrigin).Raycast(ray, out float d)) return false;
            way = Vector3.ProjectOnPlane(ray.GetPoint(d) - axisOrigin, AxisWays[axis]);
            return way.sqrMagnitude > 1e-6f;
        }

        private static void StartAxisDrag(int axis)
        {
            if (gizmoKind == GizmoKind.Move)
            {
                if (!AlongAxis(axis, out axisFrom)) return;
            }
            else
            {
                turnByScreen = !OnRingPlane(axis, out turnFrom);
                turnMouse = Input.mousePosition;
                turnLast = 0;
            }
            axisDrag = axis; axisMoved = 0;
            axisStart.Clear(); carryCulled.Clear();
            foreach (Transform t in selection)
            {
                if (t == null) continue;
                axisStart.Add((t, t.position, t.rotation));
                carryCulled.AddRange(t.GetComponentsInChildren<CullingObject>(true));
            }
        }

        private static void AxisDrag(bool fine)
        {
            if (Input.GetMouseButtonDown(1))
            {
                foreach (var (thing, from, turn) in axisStart) if (thing != null) thing.SetPositionAndRotation(from, turn);
                EndAxisDrag(false);
                return;
            }
            if (gizmoKind == GizmoKind.Move)
            {
                if (AlongAxis(axisDrag, out float along))
                {
                    axisMoved = along - axisFrom;
                    if (fine) axisMoved = Mathf.Round(axisMoved / .05f) * .05f;
                    foreach (var (thing, from, _) in axisStart) if (thing != null) thing.position = from + AxisWays[axisDrag] * axisMoved;
                }
            }
            else
            {
                // Round and round: each frame's change added up, so it can go past half a turn.
                float now;
                if (turnByScreen) now = (Input.mousePosition.x - turnMouse.x) * .5f;
                else if (OnRingPlane(axisDrag, out Vector3 way)) now = turnLast + Mathf.DeltaAngle(turnLast, Vector3.SignedAngle(turnFrom, way, AxisWays[axisDrag]));
                else now = turnLast;
                turnLast = now;
                axisMoved = fine ? Mathf.Round(now / 15) * 15 : now;
                Quaternion turn = Quaternion.AngleAxis(axisMoved, AxisWays[axisDrag]);
                foreach (var (thing, from, was) in axisStart)
                    if (thing != null) thing.SetPositionAndRotation(axisOrigin + turn * (from - axisOrigin), turn * was);
            }
            if (CullingManager.Instance != null)
                foreach (CullingObject culled in carryCulled) if (culled != null) try { CullingManager.Instance.UpdateSphere(culled); } catch (Exception) { }
            if (Input.GetMouseButtonUp(0)) EndAxisDrag(true);
        }

        private static void EndAxisDrag(bool keep)
        {
            axisDrag = -1;
            axisStart.Clear(); carryCulled.Clear();
            Physics.SyncTransforms();
            if (keep) Commit(Belong.Own);
            Moved();
            Status();
        }

        private static string Where(Vector3 p) => $"X {p.x:0.00}  Y {p.y:0.00}  Z {p.z:0.00}";
    }
}
