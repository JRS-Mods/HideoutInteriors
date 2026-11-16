using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// How big things are. Anything can be made bigger or smaller, or stretched along one of its own X, Y and Z: the
    /// hideout's own things (kept with where they were put, against the size the game has them) and decorations (kept
    /// on their tag). A poster's or screen's width and height are its face's, so its picture is cut to fill the new
    /// shape rather than stretched. Things grow from where they stand (the middle of their underside); posters and
    /// screens from their middle, as they hang.
    /// </summary>
    internal static partial class Interiors
    {
        // The hideout's own things that aren't the size the game has them: how many times, along X, Y and Z.
        private static readonly Dictionary<Transform, Vector3> itemSizes = new Dictionary<Transform, Vector3>();

        private const float SmallestSize = .05f, BiggestSize = 20f, SmallestFace = .05f, BiggestFace = 12f;

        // What a thing was before a change of size, to measure the change from (and to go back to).
        private sealed class SizeStart
        {
            public Transform Thing;
            public Vector3 Position, Pivot, Size;
            // A poster or screen: its face, in metres.
            public MediaState Media;
            public float Width, Height, TagWidth, TagHeight;
        }

        // How many times its own size a thing is, along its X, Y and Z.
        private static Vector3 SizeOf(Transform t)
        {
            DecorTag tag = t.GetComponent<DecorTag>();
            if (tag != null) return tag.Stretch * (tag.Scale > 0 ? tag.Scale : 1);
            return itemSizes.TryGetValue(t, out Vector3 size) ? size : Vector3.one;
        }

        private static void SetSize(Transform t, Vector3 size)
        {
            // A negative size is a mirrored thing (X / Z): its sign stays.
            for (int i = 0; i < 3; i++) size[i] = (size[i] < 0 ? -1 : 1) * Mathf.Clamp(Mathf.Abs(size[i]), SmallestSize, BiggestSize);
            DecorTag tag = t.GetComponent<DecorTag>();
            if (tag != null)
            {
                bool even = Mathf.Approximately(size.x, size.y) && Mathf.Approximately(size.y, size.z);
                tag.Scale = even ? size.x : 1;
                tag.Stretch = even ? Vector3.one : size;
                ApplyScale(t);
                return;
            }
            Remember(t);
            itemSizes[t] = size;
            SetWorldScale(t, Vector3.Scale(originals[t].WorldScale, size));
        }

        // A decoration's size in the world: what it copies, times its scale and stretch, whatever it hangs under.
        private static void ApplyScale(Transform t)
        {
            DecorTag tag = t != null ? t.GetComponent<DecorTag>() : null;
            if (tag == null) return;
            SetWorldScale(t, Vector3.Scale(tag.BaseScale, tag.Stretch) * (tag.Scale > 0 ? tag.Scale : 1));
        }

        private static void SetWorldScale(Transform t, Vector3 world)
        {
            Vector3 parent = t.parent != null ? t.parent.lossyScale : Vector3.one;
            t.localScale = new Vector3(world.x / NotZero(parent.x), world.y / NotZero(parent.y), world.z / NotZero(parent.z));
            Moved();
        }

        private static float NotZero(float f) => Mathf.Abs(f) < 1e-5f ? 1 : f;

        private static SizeStart StartSize(Transform t)
        {
            var start = new SizeStart { Thing = t, Position = t.position, Pivot = t.position, Size = SizeOf(t) };
            if (mediaStates.TryGetValue(t, out MediaState s))
            {
                Vector2 face = FaceSize(s);
                start.Media = s;
                start.Width = face.x; start.Height = face.y;
                start.TagWidth = s.Tag.Width; start.TagHeight = s.Tag.Height;
            }
            // Posters and screens hang by their middle; everything else stands on its underside.
            if ((start.Media == null || IsComputer(t)) && Bounds(t, out Bounds b)) start.Pivot = new Vector3(b.center.x, b.min.y, b.center.z);
            return start;
        }

        // A thing so many times the size it started at: all of it (axis -1), or along one of its own axes only.
        private static void Resize(SizeStart from, int axis, float by)
        {
            Transform t = from.Thing;
            if (t == null) return;
            MediaState s = from.Media;
            // A poster's or screen's width or height alone is its face's; it has no depth to stretch.
            if (s != null && axis == 2) axis = -1;
            if (s != null && axis >= 0)
            {
                s.Tag.Width = Mathf.Clamp(from.Width * (axis == 0 ? by : 1), SmallestFace, BiggestFace);
                s.Tag.Height = Mathf.Clamp(from.Height * (axis == 1 ? by : 1), SmallestFace, BiggestFace);
                Relayout(s);
                // A computer stays standing on its foot as its screen gets taller.
                if (IsComputer(t)) t.position = from.Position + t.up * ((s.Tag.Height - from.Height) / 2 * t.lossyScale.y);
                Culled(t);
                return;
            }
            Vector3 factor = Vector3.one * by;
            if (axis >= 0) { factor = Vector3.one; factor[axis] = by; }
            SetSize(t, Vector3.Scale(from.Size, factor));
            // What it actually came to (sizes stop at the smallest and biggest), so it stays standing where it stood.
            Vector3 now = SizeOf(t);
            for (int i = 0; i < 3; i++) factor[i] = now[i] / NotZero(from.Size[i]);
            Vector3 offset = Quaternion.Inverse(t.rotation) * (from.Position - from.Pivot);
            t.position = from.Pivot + t.rotation * Vector3.Scale(offset, factor);
            Culled(t);
        }

        private static void SizeBack(SizeStart from)
        {
            Transform t = from.Thing;
            if (t == null) return;
            if (from.Media != null) { from.Media.Tag.Width = from.TagWidth; from.Media.Tag.Height = from.TagHeight; }
            SetSize(t, from.Size);
            t.position = from.Position;
            if (from.Media != null) Relayout(from.Media);
            Culled(t);
        }

        // + / -: the selection bigger or smaller, each thing about where it stands.
        private static void ScaleSelection(float by)
        {
            selection.RemoveAll(t => t == null);
            if (selection.Count == 0) return;
            foreach (Transform t in selection) Resize(StartSize(t), -1, by);
            Commit(Belong.Own);
            Status();
        }

        // + / - by the keys: the bar's size goes with it.
        private static void SizeSelection(float by)
        {
            ScaleSelection(by);
            BarLook();
        }

        // A poster or screen whose face changed shape: its page or video drawn again for the new one.
        private static void Reshaped(Transform t)
        {
            if (t == null || !mediaStates.TryGetValue(t, out MediaState s)) return;
            Vector2 face = FaceSize(s);
            float shape = face.x / Mathf.Max(face.y, .001f), drawn = 0;
            if (s.Page != null) drawn = (float)s.Page.Width / s.Page.Height;
            else if (s.Video != null && s.Target != null) drawn = (float)s.Target.width / s.Target.height;
            if (drawn <= 0 || Mathf.Abs(shape / drawn - 1) < .03f) return;
            double at = s.Video != null ? s.Video.time : 0;
            SetupMedia(t);
            s.ResumeAt = at;
        }

        // How big a thing is, in words: a poster's or screen's face in centimetres, else times its own size.
        private static string SizeText(Transform t)
        {
            if (t == null) return "";
            if (mediaStates.TryGetValue(t, out MediaState s))
            {
                Vector2 face = FaceSize(s) * (s.Tag.Scale > 0 ? s.Tag.Scale : 1) * 100;
                return $"{face.x:0} x {face.y:0} cm";
            }
            Vector3 size = SizeOf(t);
            string mirrored = size.x < 0 || size.y < 0 || size.z < 0 ? ", mirrored" : "";
            size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            return (Mathf.Approximately(size.x, size.y) && Mathf.Approximately(size.y, size.z)
                ? $"{size.x:0.00} x its size"
                : $"X {size.x:0.00}  Y {size.y:0.00}  Z {size.z:0.00} x its size") + mirrored;
        }
    }
}
