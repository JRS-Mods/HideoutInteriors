using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace HideoutInteriors
{
    internal enum SnapKind { Off, Grid, Edges, Both }

    /// <summary>
    /// Arranging what's selected: snapping while things are carried (to a grid, to other things' edges), lining a
    /// selection up, spacing it evenly, putting it in a row, flipping it across X or Z, dropping it onto what's under
    /// it; groups (things that are picked and moved as one), locks (things the mouse looks past) and names of your own.
    /// </summary>
    internal static partial class Interiors
    {
        internal static ConfigEntry<SnapKind> Snap;
        internal static ConfigEntry<float> GridSize;

        // Carried things' edges come to another thing's when they're this near (metres).
        private const float EdgeReach = .12f;

        internal static void BindArrange(ConfigFile config)
        {
            Snap = config.Bind("Decorating", "Snapping", SnapKind.Off,
                "What carried things snap to: a grid, other things' edges, or both (N in decor mode goes through them; hold Alt while carrying for none).");
            GridSize = config.Bind("Decorating", "Grid size", .25f,
                new ConfigDescription("The snapping grid's step, in metres.", new AcceptableValueRange<float>(.05f, 2f)));
        }

        // ---- snapping -----------------------------------------------------------------------------------------

        private static bool SnapHeld => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        private static bool GridOn => !SnapHeld && (Snap.Value == SnapKind.Grid || Snap.Value == SnapKind.Both);
        private static bool EdgesOn => !SnapHeld && (Snap.Value == SnapKind.Edges || Snap.Value == SnapKind.Both);

        private static void SetSnap(SnapKind kind)
        {
            Snap.Value = kind;
            Status();
            RefreshRows();
        }

        private static void CycleSnap()
        {
            SetSnap((SnapKind)(((int)Snap.Value + 1) % 4));
            EFT.Communications.NotificationManager.DisplayMessageNotification("Snapping: " + SnapText() + ".");
        }

        private static string SnapText()
        {
            switch (Snap.Value)
            {
                case SnapKind.Grid: return $"a {GridSize.Value * 100:0} cm grid";
                case SnapKind.Edges: return "other things' edges";
                case SnapKind.Both: return $"a {GridSize.Value * 100:0} cm grid and other things' edges";
                default: return "off";
            }
        }

        private static void GridStep(float by)
        {
            float[] steps = { .05f, .1f, .25f, .5f, 1f, 2f };
            int at = 0;
            for (int i = 0; i < steps.Length; i++) if (Mathf.Abs(steps[i] - GridSize.Value) < Mathf.Abs(steps[at] - GridSize.Value)) at = i;
            GridSize.Value = steps[Mathf.Clamp(at + (by > 0 ? 1 : -1), 0, steps.Length - 1)];
            Status();
            RefreshRows();
        }

        // Where carried things are put, on the grid.
        private static Vector3 SnapPoint(Vector3 at)
        {
            if (!GridOn) return at;
            float g = Mathf.Max(.01f, GridSize.Value);
            return new Vector3(Mathf.Round(at.x / g) * g, at.y, Mathf.Round(at.z / g) * g);
        }

        private static readonly HashSet<Transform> carriedSet = new HashSet<Transform>();

        // What's carried, already put where the mouse is, pulled the last few centimetres so that its edges (or its
        // middle) line up with a thing beside it: along X and along Z, whichever thing is nearest on each.
        private static void SnapToEdges()
        {
            if (!EdgesOn || carrying.Count == 0) return;
            bool any = false;
            Bounds mine = default;
            carriedSet.Clear();
            foreach (Carry c in carrying)
            {
                if (c.Thing == null) continue;
                carriedSet.Add(c.Thing);
                if (!Bounds(c.Thing, out Bounds b)) continue;
                if (!any) { mine = b; any = true; } else mine.Encapsulate(b);
            }
            if (!any) return;
            RefreshThingBoxes();
            float bestX = EdgeReach, bestZ = EdgeReach, moveX = 0, moveZ = 0;
            foreach (var (t, box, _) in thingBoxes)
            {
                if (t == null || carriedSet.Contains(t)) continue;
                // Only things at its height and beside it.
                if (box.max.y < mine.min.y - .05f || box.min.y > mine.max.y + .05f) continue;
                bool nearZ = box.max.z > mine.min.z - .6f && box.min.z < mine.max.z + .6f;
                bool nearX = box.max.x > mine.min.x - .6f && box.min.x < mine.max.x + .6f;
                if (nearZ) Nearest(mine.min.x, mine.max.x, mine.center.x, box.min.x, box.max.x, box.center.x, ref bestX, ref moveX);
                if (nearX) Nearest(mine.min.z, mine.max.z, mine.center.z, box.min.z, box.max.z, box.center.z, ref bestZ, ref moveZ);
            }
            if (moveX == 0 && moveZ == 0) return;
            var move = new Vector3(moveX, 0, moveZ);
            foreach (Carry c in carrying) if (c.Thing != null) c.Thing.position += move;
        }

        // The smallest move that brings one of my edges (or my middle) to one of the other's.
        private static void Nearest(float myMin, float myMax, float myMid, float min, float max, float mid, ref float best, ref float move)
        {
            Try(min - myMax, ref best, ref move);   // side by side
            Try(max - myMin, ref best, ref move);
            Try(min - myMin, ref best, ref move);   // flush
            Try(max - myMax, ref best, ref move);
            Try(mid - myMid, ref best, ref move);   // centred
        }

        private static void Try(float by, ref float best, ref float move)
        {
            if (Mathf.Abs(by) >= best) return;
            best = Mathf.Abs(by); move = by;
        }

        // ---- lining up ----------------------------------------------------------------------------------------

        private static List<(Transform t, Bounds b)> SelectedBoxes(int atLeast, string what)
        {
            selection.RemoveAll(t => t == null);
            var list = new List<(Transform, Bounds)>();
            foreach (Transform t in selection) if (Bounds(t, out Bounds b)) list.Add((t, b));
            if (list.Count >= atLeast) return list;
            EFT.Communications.NotificationManager.DisplayMessageNotification($"Select {(atLeast == 1 ? "something" : atLeast + " things or more")} to {what}.");
            return null;
        }

        private static Bounds Union(List<(Transform t, Bounds b)> list)
        {
            Bounds all = list[0].b;
            foreach (var (_, b) in list) all.Encapsulate(b);
            return all;
        }

        /// <summary>The selection's low edges (-1), middles (0) or high edges (1) brought into one line along an axis.</summary>
        private static void AlignSelection(int axis, int side)
        {
            var list = SelectedBoxes(2, "line them up");
            if (list == null) return;
            Bounds all = Union(list);
            foreach (var (t, b) in list)
            {
                float by = side < 0 ? all.min[axis] - b.min[axis] : side > 0 ? all.max[axis] - b.max[axis] : all.center[axis] - b.center[axis];
                t.position += AxisWays[axis] * by;
            }
            Commit(Belong.Own);
            Status();
        }

        /// <summary>The same gap between each along an axis: the first and last stay where they are.</summary>
        private static void DistributeSelection(int axis)
        {
            var list = SelectedBoxes(3, "space them evenly");
            if (list == null) return;
            list = list.OrderBy(e => e.b.center[axis]).ToList();
            float room = list[list.Count - 1].b.min[axis] - list[0].b.max[axis];
            for (int i = 1; i < list.Count - 1; i++) room -= list[i].b.size[axis];
            float gap = room / (list.Count - 1), at = list[0].b.max[axis] + gap;
            for (int i = 1; i < list.Count - 1; i++)
            {
                list[i].t.position += AxisWays[axis] * (at - list[i].b.min[axis]);
                at += list[i].b.size[axis] + gap;
            }
            Commit(Belong.Own);
            Status();
        }

        /// <summary>Side by side along an axis from where the first stands, in one line, a finger's width between.</summary>
        private static void RowSelection(int axis)
        {
            var list = SelectedBoxes(2, "put them in a row");
            if (list == null) return;
            list = list.OrderBy(e => e.b.center[axis]).ToList();
            int across = axis == 0 ? 2 : 0;
            float at = list[0].b.max[axis] + .02f, line = list[0].b.center[across];
            for (int i = 1; i < list.Count; i++)
            {
                Bounds b = list[i].b;
                list[i].t.position += AxisWays[axis] * (at - b.min[axis]) + AxisWays[across] * (line - b.center[across]);
                at += b.size[axis] + .02f;
            }
            Commit(Belong.Own);
            Status();
        }

        /// <summary>
        /// The selection mirrored across X or Z, about its middle, as in a mirror: each thing goes to the other side and
        /// its shape is mirrored too (a left-handed thing comes out right-handed), so a layout built on one side of a room
        /// makes the other. Done again, it's back as it was.
        /// </summary>
        private static void FlipSelection(int axis)
        {
            var list = SelectedBoxes(1, "flip it");
            if (list == null) return;
            Checkpoint();
            float middle = Union(list).center[axis];
            foreach (var (t, b) in list)
            {
                Vector3 forward = t.forward, up = t.up, centre = b.center;
                forward[axis] = -forward[axis]; up[axis] = -up[axis];
                centre[axis] = 2 * middle - centre[axis];
                // The mirror of its frame is left-handed: turned to the mirrored forward and up, it's its own X that's
                // the wrong way round, so that's the axis its size is negative along.
                t.rotation = Quaternion.LookRotation(forward, up);
                // A poster or screen is only turned: its picture would read backwards.
                if (!IsMedia(t))
                {
                    Vector3 size = SizeOf(t);
                    size.x = -size.x;
                    SetSize(t, size);
                }
                renderersOf.Remove(t);
                // Where its middle belongs now, whatever its own origin is.
                if (Bounds(t, out Bounds now)) t.position += centre - now.center;
                Culled(t);
            }
            Commit(Belong.Own);
            Status();
        }

        /// <summary>Each selected thing down (or up) onto the surface under its middle.</summary>
        private static void DropSelection()
        {
            var list = SelectedBoxes(1, "drop it onto what's under it");
            if (list == null) return;
            int moved = 0;
            foreach (var (t, b) in list)
            {
                int count = Physics.RaycastNonAlloc(b.center, Vector3.down, rayHits, 12, ~0, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue, surface = 0;
                for (int i = 0; i < count; i++)
                {
                    RaycastHit hit = rayHits[i];
                    if (hit.normal.y < .6f || hit.distance >= best || hit.collider.transform.IsChildOf(t)) continue;
                    best = hit.distance; surface = hit.point.y;
                }
                if (best == float.MaxValue) continue;
                t.position += Vector3.up * (surface - b.min.y);
                moved++;
            }
            if (moved == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Nothing under it to stand on."); return; }
            Commit(Belong.Own);
            Status();
        }

        // ---- what's kept with a thing --------------------------------------------------------------------------

        // One of the hideout's own things' key (it's kept by it), whichever area it's under now.
        private static string ThingKey(Transform t)
        {
            if (selectionKeys.TryGetValue(t, out string key) && key != null) return key;
            if (keyOf.TryGetValue(t, out key)) return key;
            ItemOf(t, out EFT.Hideout.HideoutArea area);
            return ItemKey(area, t);
        }

        /// <summary>A thing's group, name, lock and light. With create, one of the hideout's own that isn't kept yet is, where it stands.</summary>
        private static ThingMeta MetaOf(Transform t, bool create)
        {
            if (t == null) return null;
            DecorTag tag = t.GetComponent<DecorTag>();
            if (tag != null) return tag.Meta ?? (tag.Meta = new ThingMeta());
            string key = ThingKey(t);
            if (key == null) return null;
            if (!itemLayout.TryGetValue(key, out ItemPlacement p))
            {
                if (!create) return null;
                KeepItem(t, key, OwnerOf(key));
                if (!itemLayout.TryGetValue(key, out p)) return null;
            }
            if (p.Meta == null && create) p.Meta = new ThingMeta();
            return p.Meta;
        }

        // What was set on these, kept.
        private static void SaveMeta(IEnumerable<Transform> those)
        {
            foreach (Transform t in those)
            {
                DecorTag tag = t != null ? t.GetComponent<DecorTag>() : null;
                if (tag != null && tag.Id != null && decorLayout.TryGetValue(tag.Id, out DecorRecord r)) r.Take(tag);
            }
            TidyMeta();
            SaveItems(); SaveDecor();
            RefreshRows();
        }

        /// <summary>A thing's name: yours if you gave it one, else what it is.</summary>
        private static string NameOf(Transform t)
        {
            string label = MetaOf(t, false)?.Label;
            return string.IsNullOrEmpty(label) ? Pretty(t.name) : label;
        }

        // F2: a name of your own for what's selected (empty: its own again).
        private static void RenameSelection()
        {
            selection.RemoveAll(t => t == null);
            if (selection.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Select a thing to name it."); return; }
            var those = selection.ToList();
            Ask(those.Count == 1 ? $"A name for {Pretty(those[0].name)} (empty: its own)" : $"A name for these {those.Count} things (empty: their own)", MetaOf(those[0], false)?.Label ?? "", name =>
            {
                Checkpoint();
                name = name.Trim();
                foreach (Transform t in those)
                {
                    ThingMeta m = t != null ? MetaOf(t, true) : null;
                    if (m != null) m.Label = name.Length == 0 ? null : name;
                }
                SaveMeta(those);
                tipFor = null;
                Status();
            });
        }

        // ---- groups -------------------------------------------------------------------------------------------

        private static string GroupOf(Transform t) => MetaOf(t, false)?.Group;

        // A group's id is its name and a few letters that tell two groups of one name apart.
        private static string GroupName(string group)
        {
            int cut = group.LastIndexOf('#');
            return cut > 0 ? group.Substring(0, cut) : group;
        }

        private static string NewGroup(string name) => name + "#" + Guid.NewGuid().ToString("N").Substring(0, 6);

        // Every thing in a group that's in the hideout now.
        private static List<Transform> Members(string group)
        {
            var list = new List<Transform>();
            if (string.IsNullOrEmpty(group)) return list;
            foreach (var pair in decorObjects)
            {
                if (pair.Value == null) continue;
                DecorTag tag = pair.Value.GetComponent<DecorTag>();
                if (tag != null && tag.Meta != null && tag.Meta.Group == group) list.Add(pair.Value);
            }
            foreach (var pair in itemLayout)
            {
                if (pair.Value.Meta == null || pair.Value.Meta.Group != group) continue;
                Transform t = Resolve(pair.Key);
                if (t != null) list.Add(t);
            }
            return list;
        }

        // The rest of a thing's group selected with it (Alt: just the one).
        private static void SelectGroup(Transform t)
        {
            if (SnapHeld) return;
            string group = GroupOf(t);
            if (group == null) return;
            foreach (Transform member in Members(group))
                if (member.gameObject.activeInHierarchy) Select(member);
        }

        private static void UnselectGroup(Transform t)
        {
            string group = SnapHeld ? null : GroupOf(t);
            if (group == null) { Unselect(t); return; }
            foreach (Transform member in Members(group)) Unselect(member);
            Unselect(t);
        }

        // After a box was drawn: the groups of what's in it, whole.
        private static void ExpandGroups()
        {
            if (SnapHeld) return;
            var groups = new HashSet<string>();
            foreach (Transform t in selection) if (t != null && GroupOf(t) is string g) groups.Add(g);
            foreach (string g in groups)
                foreach (Transform member in Members(g))
                    if (member.gameObject.activeInHierarchy) Select(member);
        }

        // Ctrl+G: the selection locked together under a name; a click on one of them picks them all from now on.
        private static void GroupSelection()
        {
            selection.RemoveAll(t => t == null);
            if (selection.Count < 2) { EFT.Communications.NotificationManager.DisplayMessageNotification("Select two things or more, then Ctrl+G to group them."); return; }
            var those = selection.ToList();
            Ask($"A name for this group of {those.Count}", "Group", name =>
            {
                Checkpoint();
                string group = NewGroup(name.Trim().Length == 0 ? "Group" : name.Trim().Replace("#", ""));
                int n = 0;
                foreach (Transform t in those)
                {
                    ThingMeta m = t != null ? MetaOf(t, true) : null;
                    if (m == null) continue;
                    m.Group = group;
                    n++;
                }
                SaveMeta(those);
                EFT.Communications.NotificationManager.DisplayMessageNotification($"{n} things are the group \"{GroupName(group)}\": a click on one picks them all (Alt+click: just the one). Ctrl+Shift+G takes it apart.");
                Status();
            });
        }

        // Ctrl+Shift+G: the selected things' groups taken apart.
        private static void Ungroup()
        {
            selection.RemoveAll(t => t == null);
            var groups = new HashSet<string>();
            foreach (Transform t in selection) if (GroupOf(t) is string g) groups.Add(g);
            if (groups.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Nothing selected is in a group."); return; }
            Checkpoint();
            var changed = new List<Transform>();
            foreach (string g in groups)
                foreach (Transform member in Members(g))
                {
                    ThingMeta m = MetaOf(member, false);
                    if (m != null) m.Group = null;
                    changed.Add(member);
                }
            SaveMeta(changed);
            EFT.Communications.NotificationManager.DisplayMessageNotification(groups.Count == 1 ? $"The group \"{GroupName(groups.First())}\" is taken apart." : $"{groups.Count} groups are taken apart.");
            Status();
        }

        // ---- locks --------------------------------------------------------------------------------------------

        /// <summary>Locked: the mouse looks past it, and boxes and Ctrl+A leave it out.</summary>
        private static bool IsLocked(Transform t)
        {
            DecorTag tag = t.GetComponent<DecorTag>();
            if (tag != null) return tag.Meta != null && tag.Meta.Locked;
            return keyOf.TryGetValue(t, out string key) && itemLayout.TryGetValue(key, out ItemPlacement p) && p.Meta != null && p.Meta.Locked;
        }

        // L: the selection locked (or, all of it locked already, unlocked).
        private static void LockSelection()
        {
            selection.RemoveAll(t => t == null);
            if (selection.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Select things to lock them."); return; }
            Checkpoint();
            var those = selection.ToList();
            bool lockThem = !those.All(IsLocked);
            foreach (Transform t in those)
            {
                ThingMeta m = MetaOf(t, true);
                if (m != null) m.Locked = lockThem;
            }
            SaveMeta(those);
            if (lockThem) { ClearSelection(); hovered = null; }
            Moved();
            Outlines(); Status();
            EFT.Communications.NotificationManager.DisplayMessageNotification(lockThem
                ? $"{those.Count} thing(s) locked: the mouse looks past them. The Placed tab lists them (click one there, then L), or Shift+L unlocks everything."
                : $"{those.Count} thing(s) unlocked.");
        }

        // Shift+L: nothing's locked any more.
        private static void UnlockAll()
        {
            var locked = new List<Transform>();
            foreach (Transform t in decorObjects.Values) if (t != null && IsLocked(t)) locked.Add(t);
            foreach (var pair in itemLayout)
                if (pair.Value.Meta != null && pair.Value.Meta.Locked && Resolve(pair.Key) is Transform t) locked.Add(t);
            if (locked.Count == 0 && !itemLayout.Values.Any(p => p.Meta != null && p.Meta.Locked))
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification("Nothing is locked.");
                return;
            }
            Checkpoint();
            foreach (Transform t in locked) { ThingMeta m = MetaOf(t, false); if (m != null) m.Locked = false; }
            foreach (ItemPlacement p in itemLayout.Values) if (p.Meta != null) p.Meta.Locked = false;
            SaveMeta(locked);
            Moved();
            EFT.Communications.NotificationManager.DisplayMessageNotification("Everything is unlocked.");
        }
    }
}
