using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Hideout;
using Newtonsoft.Json;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// The hideout's things one at a time. In the scene each area's level holds its things as separate objects (a wooden
    /// box, a cardboard box, the gun safe, a candle: each the topmost object with a LODGroup under the level), so one can be
    /// picked out, put somewhere else, given to another area (then it's outlined with that area and goes where it goes),
    /// set free of every area (it stays put), hidden, or sent back where the game has it. Kept per profile in
    /// BepInEx/config/jrs.hideoutinteriors.items.&lt;profile id&gt;.json, by the area it came from and its path under it.
    /// Right-clicking a thing on the overview carries just that one; decorating (<see cref="Interiors"/>'s decor mode)
    /// works on several at once and adds copies.
    /// </summary>
    internal static partial class Interiors
    {
        private sealed class ItemPlacement
        {
            // The area it belongs to now (its type), "" for none (it stays put), null in files from 1.4.0 (see WithArea).
            public string Owner;
            // 1.4.0 only: true kept against its own area, false kept in the hideout.
            public bool WithArea = true;
            // Against its owner's root, or in the hideout with no owner.
            public float[] Position;
            public float[] Rotation;
            public bool Hidden;
        }

        // Where the game had a thing: its parent, its pose under it and whether it was switched on.
        private struct Origin { public Transform Parent; public Vector3 Position; public Quaternion Rotation; }

        private static readonly Dictionary<string, ItemPlacement> itemLayout = new Dictionary<string, ItemPlacement>();
        private static string itemsFile;
        private static readonly Dictionary<Transform, Origin> originals = new Dictionary<Transform, Origin>();
        // Things found this visit, both ways (a thing given to another area isn't under its own path any more).
        private static readonly Dictionary<string, Transform> resolved = new Dictionary<string, Transform>();
        private static readonly Dictionary<Transform, string> keyOf = new Dictionary<Transform, string>();
        // What hiding a thing switched off, to switch back on.
        private static readonly Dictionary<Transform, List<Component>> hiddenParts = new Dictionary<Transform, List<Component>>();

        // Things bigger than this across are the room itself (walls, floors, paint), not something to pick up.
        private const float BiggestItem = 6f;

        private static readonly RaycastHit[] rayHits = new RaycastHit[64];
        // Surfaces higher than this over the floor are the pipes and ducts under the ceiling, not shelves.
        private const float HighestShelf = 2.3f;

        private static void LoadItems(string id)
        {
            itemLayout.Clear();
            itemsFile = KeptIn("items", id);
            try
            {
                if (File.Exists(itemsFile))
                    foreach (var entry in JsonConvert.DeserializeObject<Dictionary<string, ItemPlacement>>(File.ReadAllText(itemsFile)) ?? new Dictionary<string, ItemPlacement>())
                    {
                        ItemPlacement p = entry.Value;
                        if (p?.Position?.Length != 3 || p.Rotation?.Length != 4) continue;
                        // 1.4.0: with its own area, or kept in the hideout.
                        if (p.Owner == null) p.Owner = p.WithArea ? AreaTypeOf(entry.Key) : "";
                        itemLayout[entry.Key] = p;
                    }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't read {itemsFile}, so the areas' things stand where the game has them: {e.Message}"); }
            LoadDecor(id);
        }

        private static void SaveItems()
        {
            if (itemsFile == null) return;
            try { File.WriteAllText(itemsFile, JsonConvert.SerializeObject(itemLayout, Formatting.Indented)); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't save {itemsFile}: {e.Message}"); }
        }

        // A new hideout visit: nothing found yet.
        private static void ForgetItems()
        {
            originals.Clear(); resolved.Clear(); keyOf.Clear(); hiddenParts.Clear(); pickable.Clear(); renderersOf.Clear(); sceneRoots.Clear();
            thingList.Clear(); thingListAt = -100;
            ForgetDecor();
        }

        // ---- finding things -----------------------------------------------------------------------------------

        private static bool IsLevel(Transform t) => t.name.Length > 5 && t.name.StartsWith("level", StringComparison.Ordinal) && char.IsDigit(t.name[5]);

        private static string AreaTypeOf(string key) { int slash = key.IndexOf('/'); return slash > 0 ? key.Substring(0, slash) : key; }

        private static HideoutArea AreaByType(string type)
        {
            if (string.IsNullOrEmpty(type) || style == null) return null;
            foreach (HideoutArea area in Areas()) if (Key(area) == type) return area;
            return null;
        }

        // The thing a part belongs to: a decoration, a thing set free of every area, or an area's thing (what stands in one
        // of its level's groups). Null for anything else (walls, floors, the rooms).
        private static Transform ItemOf(Transform part, out HideoutArea area)
        {
            area = null;
            DecorTag tag = part.GetComponentInParent<DecorTag>(true);
            if (tag != null) { area = tag.GetComponentInParent<HideoutArea>(true); return tag.transform; }
            if (decorRoot != null && part.IsChildOf(decorRoot.transform))
            {
                Transform free = part;
                while (free.parent != null && free.parent != decorRoot.transform) free = free.parent;
                return free.parent == decorRoot.transform ? free : null;
            }
            area = part.GetComponentInParent<HideoutArea>(true);
            if (area == null || Key(area) == null) { area = null; return SceneItemOf(part); }
            // Up to the first group (or the level itself): the thing is what stands in it.
            Transform found = null, t = part;
            for (; t != null && t != area.transform; t = t.parent)
            {
                if (IsLevel(t) || IsGroup(t)) break;
                found = t;
            }
            if (found == null) return null;
            // Straight under the area, outside its levels (a cable run to the generator): one of its things too, unless
            // it's the area's own machinery (its click zone, camera points) or a room part.
            if (t == area.transform)
            {
                if (NotAThing(found)) return null;
                Transform loose = Narrow(found, part);
                return loose != null && Pickable(loose) ? loose : null;
            }
            for (; t != null && t != area.transform; t = t.parent) if (IsLevel(t)) return Narrow(found, part);
            return null;
        }

        // ---- the hideout's things outside its areas -------------------------------------------------------------

        // The scene's own groups of things that belong to no area: its lamps, barrels and clutter (SceneContent), the
        // cables, the cultists' room, and the seasonal decorations (--_HalloweenIllumination and the like). Not STYLE
        // (walls and floors the customization swaps), nor wall cracks, managers or what other mods add at runtime.
        private static readonly string[] SceneGroups = { "SceneContent", "!wires_light", "EXTENSION" };
        private const string ScenePrefix = "~";
        private static readonly Dictionary<Transform, bool> sceneRoots = new Dictionary<Transform, bool>();

        private static bool IsSceneRoot(Transform root)
        {
            if (root == null || style == null || root.parent != null) return false;
            if (sceneRoots.TryGetValue(root, out bool known)) return known;
            bool ok = root.gameObject.scene == style.scene
                && (Array.IndexOf(SceneGroups, root.name) >= 0 || root.name.StartsWith("--_", StringComparison.Ordinal))
                && root.GetComponentInChildren<HideoutArea>(true) == null;
            sceneRoots[root] = ok;
            return ok;
        }

        private static IEnumerable<Transform> SceneRoots()
        {
            if (style == null) yield break;
            foreach (GameObject go in style.scene.GetRootGameObjects())
                if (go != null && IsSceneRoot(go.transform)) yield return go.transform;
        }

        private static bool IsSceneType(string type) => type != null && type.StartsWith(ScenePrefix, StringComparison.Ordinal);

        // A part of one of the scene's things: what stands in its group. Null for room parts (too big) and the rest.
        private static Transform SceneItemOf(Transform part)
        {
            Transform root = part.root;
            if (part == root || !IsSceneRoot(root)) return null;
            Transform found = part;
            while (found.parent != root) found = found.parent;
            found = Narrow(found, part);
            return found == null || NotAThing(found) || !Pickable(found) ? null : found;
        }

        // An area's click zone, its camera and icon points: never things.
        private static bool NotAThing(Transform t)
        {
            string n = t.name;
            return n.IndexOf("raycast_collider", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("VCamera", StringComparison.Ordinal)
                || n.EndsWith("_point", StringComparison.Ordinal) || n.EndsWith("_collider", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("_trigger", StringComparison.OrdinalIgnoreCase);
        }

        // A group of things with no name the mod knows (PROPS, static_lamp, cut_lamp...): nothing of its own to see, several
        // things in it, and too big to be one thing. A thing made of parts (a bed and its mattress) is small, so stays whole.
        private static bool IsContainer(Transform t) =>
            t.childCount >= 2 && t.GetComponent<Renderer>() == null && t.GetComponent<LODGroup>() == null && !Pickable(t);

        // Down from a container to what in it the part belongs to.
        private static Transform Narrow(Transform thing, Transform part)
        {
            while (thing != null && thing != part && IsContainer(thing))
            {
                Transform next = part;
                while (next != null && next.parent != thing) next = next.parent;
                if (next == null) break;
                thing = next;
            }
            return thing;
        }

        // The things standing loose in a group (a scene group, or an area outside its levels), into containers.
        private static IEnumerable<Transform> LooseThings(Transform group, bool showingOnly)
        {
            foreach (Transform child in group)
            {
                if (showingOnly && !child.gameObject.activeInHierarchy) continue;
                if (IsLevel(child) || NotAThing(child) || child.GetComponent<DecorTag>() != null) continue;
                if (child.GetComponentInChildren<MeshFilter>(true) == null) continue;
                if (IsContainer(child)) { foreach (Transform inner in LooseThings(child, showingOnly)) yield return inner; continue; }
                if (Pickable(child)) yield return child;
            }
        }

        // Every thing outside the areas' levels: the scene's groups', and what stands straight under an area.
        private static IEnumerable<(Transform thing, HideoutArea area)> OutsideThings(bool showingOnly)
        {
            foreach (Transform root in SceneRoots())
            {
                if (showingOnly && !root.gameObject.activeInHierarchy) continue;
                foreach (Transform t in LooseThings(root, showingOnly)) yield return (t, null);
            }
            foreach (HideoutArea area in Areas())
            {
                if (Key(area) == null || (showingOnly && !area.gameObject.activeInHierarchy)) continue;
                foreach (Transform t in LooseThings(area.transform, showingOnly)) yield return (t, area);
            }
        }

        // What an area's level sorts its things into: its highlight_transform, immediate_enable, light_levels (and the
        // 1light, 2light... under it), garbage with its Floor and Wall, and the level_1, level_2... some areas group by.
        private static bool IsGroup(Transform t)
        {
            string n = t.name;
            switch (n)
            {
                case "highlight_transform": case "immediate_enable": case "light_levels": case "Floor": case "Wall": case "Ceiling":
                    return true;
            }
            if (n.StartsWith("garbage", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("level_", StringComparison.Ordinal) && n.Length > 6 && char.IsDigit(n[6])) return true;
            // The Defective Wall's cleared rooms keep their clutter in module_01, module_04... (the bunk bed, the tractor
            // tyre and the ladder by the gym are in module_04): as one thing those were too big to pick up.
            if (n.StartsWith("module_", StringComparison.Ordinal) && n.Length > 7 && char.IsDigit(n[7])) return true;
            return n.Length >= 6 && char.IsDigit(n[0]) && n.EndsWith("light", StringComparison.Ordinal);
        }

        // The things in a level (or a group), whatever groups they're sorted into; only ones with something to see.
        private static IEnumerable<Transform> ItemsUnder(Transform group, bool showingOnly)
        {
            foreach (Transform child in group)
            {
                if (showingOnly && !child.gameObject.activeInHierarchy) continue;
                if (IsGroup(child)) { foreach (Transform inner in ItemsUnder(child, showingOnly)) yield return inner; continue; }
                if (child.GetComponentInChildren<MeshFilter>(true) == null) continue;
                if (child.name.IndexOf("raycast_collider", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (IsContainer(child)) { foreach (Transform inner in ItemsUnder(child, showingOnly)) yield return inner; continue; }
                yield return child;
            }
        }

        // A game thing's key: the area it came from and its path under it, with a #n where siblings share its name.
        private static string ItemKey(HideoutArea area, Transform thing)
        {
            if (keyOf.TryGetValue(thing, out string known)) return known;
            string extra = ExtraKeyOf(thing);
            if (extra != null) return extra;
            Transform top;
            string type;
            if (area != null) { top = area.transform; type = Key(area); }
            else if (thing.root != thing && IsSceneRoot(thing.root)) { top = thing.root; type = ScenePrefix + top.name; }
            else return null;
            var parts = new List<string>();
            Transform t = thing;
            for (; t != null && t != top; t = t.parent)
            {
                int same = 0;
                if (t.parent != null)
                    for (int i = 0; i < t.parent.childCount; i++)
                    {
                        Transform sibling = t.parent.GetChild(i);
                        if (sibling == t) break;
                        if (sibling.name == t.name) same++;
                    }
                parts.Add(same == 0 ? t.name : t.name + "#" + same);
            }
            if (t != top) return null;
            parts.Reverse();
            string key = type + "/" + string.Join("/", parts);
            resolved[key] = thing; keyOf[thing] = key;
            return key;
        }

        private static Transform Resolve(string key)
        {
            if (resolved.TryGetValue(key, out Transform known) && known != null) return known;
            if (IsExtra(key)) return ExtraPattern(key);
            string type = AreaTypeOf(key);
            Transform t;
            if (IsSceneType(type))
            {
                string name = type.Substring(ScenePrefix.Length);
                t = SceneRoots().FirstOrDefault(r => r.name == name);
                if (t == null) return null;
            }
            else
            {
                HideoutArea area = AreaByType(type);
                if (area == null) return null;
                t = area.transform;
            }
            foreach (string part in key.Substring(key.IndexOf('/') + 1).Split('/'))
            {
                string name = part;
                int index = 0, hash = part.LastIndexOf('#');
                if (hash > 0 && int.TryParse(part.Substring(hash + 1), out int n)) { name = part.Substring(0, hash); index = n; }
                Transform next = null;
                for (int i = 0; i < t.childCount && next == null; i++)
                {
                    Transform child = t.GetChild(i);
                    if (child.name != name) continue;
                    if (index-- == 0) next = child;
                }
                if (next == null) return null;
                t = next;
            }
            resolved[key] = t; keyOf[t] = key;
            return t;
        }

        private static void Remember(Transform thing)
        {
            if (!originals.ContainsKey(thing)) originals[thing] = new Origin { Parent = thing.parent, Position = thing.localPosition, Rotation = thing.localRotation };
        }

        // Whom a game thing belongs to now: what it was given to, or the area it came from.
        private static string OwnerOf(string key) => itemLayout.TryGetValue(key, out ItemPlacement p) && p.Owner != null ? p.Owner : AreaTypeOf(key);

        // Where a thing hangs for an owner: under the area it came from as the game had it, under another area's showing
        // level (so it's outlined with it and comes and goes with it), or under the decorations (no area: it stays put).
        private static Transform ParentFor(Transform thing, string originalArea, string owner, out Transform ownerRoot)
        {
            ownerRoot = null;
            if (string.IsNullOrEmpty(owner)) return DecorRoot();
            // One of the scene's own things, kept as the scene has it: where the game had it, in the hideout's space.
            if (IsSceneType(owner)) return originals.TryGetValue(thing, out Origin had) && had.Parent != null ? had.Parent : thing.parent;
            HideoutArea area = AreaByType(owner);
            if (area == null) return null;
            ownerRoot = area.transform;
            if (owner == originalArea && originals.TryGetValue(thing, out Origin o) && o.Parent != null) return o.Parent;
            Transform level = null;
            try { level = area.HighlightTransform; } catch (Exception) { }
            // In a raid copy the area was never set up: its showing level's highlight_transform, found by name.
            if (level == null && raidCopy)
                foreach (Transform child in area.transform)
                    if (IsLevel(child) && child.gameObject.activeSelf) { level = child.Find("highlight_transform") ?? child; break; }
            return level;
        }

        // A thing's renderers, looked up once.
        private static readonly Dictionary<Transform, Renderer[]> renderersOf = new Dictionary<Transform, Renderer[]>();
        private static Renderer[] RenderersOf(Transform thing)
        {
            if (!renderersOf.TryGetValue(thing, out Renderer[] found))
                renderersOf[thing] = found = thing.GetComponentsInChildren<Renderer>(true).Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToArray();
            return found;
        }

        // Where a thing shows (its renderers that are on); false if nothing of it shows.
        private static bool Bounds(Transform thing, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer r in RenderersOf(thing))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any && bounds.size.sqrMagnitude > 0;
        }

        // Its size from its meshes, which works switched off too (a renderer that's off has no bounds).
        private static bool MeshBounds(Transform thing, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (MeshFilter f in thing.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null || f.GetComponent<MeshRenderer>() == null) continue;
                Bounds local = f.sharedMesh.bounds;
                Matrix4x4 m = f.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 w = m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { bounds = new Bounds(w, Vector3.zero); any = true; }
                    else bounds.Encapsulate(w);
                }
            }
            return any;
        }

        private static readonly Dictionary<Transform, bool> pickable = new Dictionary<Transform, bool>();
        private static bool Pickable(Transform thing)
        {
            if (pickable.TryGetValue(thing, out bool known)) return known;
            bool small = !MeshBounds(thing, out Bounds b) || (b.size.x <= BiggestItem && b.size.z <= BiggestItem);
            pickable[thing] = small;
            return small;
        }

        // ---- putting things where they were put ---------------------------------------------------------------

        // Every saved thing that came from this area (wherever it is now).
        private static void PlaceItems(HideoutArea area) => PlaceItems(Key(area));

        // Every saved thing from the scene's own groups.
        private static void PlaceSceneItems()
        {
            foreach (Transform root in SceneRoots()) PlaceItems(ScenePrefix + root.name);
        }

        private static void PlaceItems(string key)
        {
            if (key == null || itemLayout.Count == 0) return;
            string prefix = key + "/";
            foreach (var entry in itemLayout)
            {
                if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                Transform thing = Resolve(entry.Key);
                if (thing == null || Carried(thing)) continue;
                ApplyItem(thing, entry.Key, entry.Value);
            }
        }

        private static void ApplyItem(Transform thing, string key, ItemPlacement p)
        {
            Remember(thing);
            Transform parent = ParentFor(thing, AreaTypeOf(key), p.Owner, out Transform ownerRoot);
            if (parent == null) return;   // its owner isn't set up yet: next time
            if (thing.parent != parent) thing.SetParent(parent, true);
            // The game switches an area's things on one by one with its level; one given away is on from now.
            if (p.Owner != AreaTypeOf(key) && !thing.gameObject.activeSelf) thing.gameObject.SetActive(true);
            var position = new Vector3(p.Position[0], p.Position[1], p.Position[2]);
            var rotation = new Quaternion(p.Rotation[0], p.Rotation[1], p.Rotation[2], p.Rotation[3]);
            if (ownerRoot != null) thing.SetPositionAndRotation(ownerRoot.TransformPoint(position), ownerRoot.rotation * rotation);
            else thing.SetPositionAndRotation(position, rotation);
            SetHidden(thing, p.Hidden);
            Culled(thing);
            Moved();
        }

        // Keeps a game thing where it stands now, for an owner ("" none, else an area's type).
        private static void KeepItem(Transform thing, string key, string owner, bool? hidden = null)
        {
            Remember(thing);
            Transform parent = ParentFor(thing, AreaTypeOf(key), owner, out Transform ownerRoot);
            if (parent == null) { owner = ""; parent = ParentFor(thing, AreaTypeOf(key), owner, out ownerRoot); }
            if (thing.parent != parent) thing.SetParent(parent, true);
            if (!thing.gameObject.activeSelf) thing.gameObject.SetActive(true);
            Vector3 position = ownerRoot != null ? ownerRoot.InverseTransformPoint(thing.position) : thing.position;
            Quaternion rotation = ownerRoot != null ? Quaternion.Inverse(ownerRoot.rotation) * thing.rotation : thing.rotation;
            bool wasHidden = itemLayout.TryGetValue(key, out ItemPlacement before) && before.Hidden;
            itemLayout[key] = new ItemPlacement
            {
                Owner = owner,
                Position = new[] { position.x, position.y, position.z },
                Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                Hidden = hidden ?? wasHidden,
            };
            SetHidden(thing, hidden ?? wasHidden);
            Culled(thing);
            Moved();
        }

        // Back where, and whose, the game has it.
        private static void RestoreItem(Transform thing, string key)
        {
            itemLayout.Remove(key);
            SetHidden(thing, false);
            if (originals.TryGetValue(thing, out Origin o))
            {
                if (o.Parent != null && thing.parent != o.Parent) thing.SetParent(o.Parent, false);
                thing.localPosition = o.Position; thing.localRotation = o.Rotation;
            }
            Culled(thing);
            Moved();
        }

        private static void SetHidden(Transform thing, bool hide)
        {
            if (hide)
            {
                if (hiddenParts.ContainsKey(thing)) return;
                var off = new List<Component>();
                foreach (Renderer r in thing.GetComponentsInChildren<Renderer>(true)) if (r.enabled) { r.enabled = false; off.Add(r); }
                foreach (Collider c in thing.GetComponentsInChildren<Collider>(true)) if (c.enabled) { c.enabled = false; off.Add(c); }
                foreach (Light l in thing.GetComponentsInChildren<Light>(true)) if (l.enabled) { l.enabled = false; off.Add(l); }
                hiddenParts[thing] = off;
            }
            else if (hiddenParts.TryGetValue(thing, out var off))
            {
                foreach (Component c in off)
                {
                    if (c is Renderer r) r.enabled = true;
                    else if (c is Collider col) col.enabled = true;
                    else if (c is Light l) l.enabled = true;
                }
                hiddenParts.Remove(thing);
            }
        }

        private static void Culled(Transform thing)
        {
            if (CullingManager.Instance == null) return;
            foreach (CullingObject culled in thing.GetComponentsInChildren<CullingObject>(true))
                try { CullingManager.Instance.UpdateSphere(culled); } catch (Exception) { }
        }

        // ---- under the mouse ----------------------------------------------------------------------------------

        private static Camera ViewCamera()
        {
            Camera camera = null;
            try { camera = EFT.CameraControl.CameraManager.Instance.Camera; } catch (Exception) { }
            return camera != null ? camera : Camera.main;
        }

        // The thing under the mouse (the room's own walls, floors and the black cap are looked past); tooBig if the
        // mouse is on part of a room.
        private static Transform ThingUnderMouse(out HideoutArea area, out bool tooBig)
        {
            area = null; tooBig = false;
            Camera camera = ViewCamera();
            if (camera == null) return null;
            // Nothing new to find while the mouse, the camera and the things are where they were.
            Vector2 mouse = Input.mousePosition;
            Transform eye = camera.transform;
            if (mouse == lastMouse && eye.position == lastEye && eye.rotation == lastTurn && moves == lastMoves && Time.frameCount - lastLook < 30)
            {
                area = lastArea; tooBig = lastTooBig;
                return lastThing;
            }
            lastMouse = mouse; lastEye = eye.position; lastTurn = eye.rotation; lastMoves = moves; lastLook = Time.frameCount;
            lastThing = FindUnderMouse(camera, out lastArea, out lastTooBig);
            area = lastArea; tooBig = lastTooBig;
            return lastThing;
        }

        private static Vector2 lastMouse;
        private static Vector3 lastEye;
        private static Quaternion lastTurn;
        private static int lastMoves = -1, moves, lastLook;
        private static Transform lastThing;
        private static HideoutArea lastArea;
        private static bool lastTooBig;

        // Something was moved, shown or hidden: what's under the mouse and where things are is looked at again.
        private static void Moved() { moves++; thingBoxesStale = true; }

        private static Transform FindUnderMouse(Camera camera, out HideoutArea area, out bool tooBig)
        {
            area = null; tooBig = false;
            Ray ray = MouseRay(camera);
            int count = Physics.RaycastNonAlloc(ray, rayHits, 300, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(rayHits, 0, count, HitDistance.Instance);
            // The point the mouse is on: the first surface that shows (a thing, a shelf, the floor; not a ceiling seen
            // from above, the extension rooms' see-through walls, a thing that's hidden or a duct overhead).
            Vector3? point = null;
            Transform best = null;
            float bestSize = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = rayHits[i];
                Transform thing = ItemOf(hit.collider.transform, out HideoutArea owner);
                if (thing == null)
                {
                    if (hit.point.y > SeenThrough || hit.collider.transform.root.name == ExtensionRooms) continue;
                    point = hit.point;
                    break;
                }
                if (!Bounds(thing, out Bounds b)) continue;
                if (!Pickable(thing)) { tooBig = true; continue; }
                point = hit.point;
                best = thing; area = owner; bestSize = Size(b);
                break;
            }
            if (point == null) return best;
            // The smallest thing that point is in: a magazine lying on the sofa where the mouse is on the magazine, the
            // carpet only where it's on the floor (most small clutter has no collider of its own to touch).
            RefreshThingBoxes();
            Vector3 p = point.Value;
            foreach (var (t, box, size) in thingBoxes)
            {
                if (t == null || size >= bestSize) continue;
                Bounds near = box;
                near.Expand(.06f);
                if (!near.Contains(p)) continue;
                best = t; bestSize = size; area = null;
            }
            if (best != null && area == null) ItemOf(best, out area);
            return best;
        }

        // Whether a thing can be seen from the camera: no wall or floor between them (looked at as the mouse looks).
        private static bool Seen(Camera camera, Transform thing, Bounds b)
        {
            Vector3 eye = camera.transform.position, to = b.center - eye;
            float far = to.magnitude;
            int count = Physics.RaycastNonAlloc(new Ray(eye, to / far), rayHits, far, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = rayHits[i];
                if (hit.distance > far - .3f) continue;
                if (hit.point.y > SeenThrough || hit.collider.transform.root.name == ExtensionRooms) continue;
                if (hit.collider.transform.IsChildOf(thing) || ItemOf(hit.collider.transform, out _) != null) continue;
                return false;
            }
            return true;
        }

        // Higher than this (the hideout's floors are at 0), what isn't a thing is a ceiling or a roof.
        private const float SeenThrough = 2.75f;

        private static float Size(Bounds b) => Mathf.Max(b.size.x, .02f) * Mathf.Max(b.size.y, .02f) * Mathf.Max(b.size.z, .02f);

        // Where every pickable thing showing is. Which things there are is looked for again every 30 s (or when one is
        // put in or taken away); where they are, when something moved.
        private static readonly List<(Transform t, Bounds box, float size)> thingBoxes = new List<(Transform, Bounds, float)>();
        private static readonly List<Transform> thingList = new List<Transform>();
        private static float thingListAt = -100;
        private static bool thingBoxesStale = true;
        private static void RefreshThingBoxes()
        {
            if (style == null) return;
            int levels = LevelsShowing();
            bool list = thingListAt < 0 || levels != levelsSeen || thingList.Any(t => t == null);
            levelsSeen = levels;
            if (!list && !thingBoxesStale) return;
            if (list) { thingList.Clear(); thingList.AddRange(ShowingThings()); thingListAt = Time.unscaledTime; }
            thingBoxesStale = false;
            thingBoxes.Clear();
            foreach (Transform t in thingList)
                if (t != null && t.gameObject.activeInHierarchy && Bounds(t, out Bounds b)) thingBoxes.Add((t, b, Size(b)));
        }

        // Which levels the areas show, as one number: a build (a level switched on) changes it.
        private static int levelsSeen;
        private static int LevelsShowing()
        {
            int sum = 0;
            foreach (HideoutArea area in Areas())
                foreach (Transform level in area.transform)
                    if (level.gameObject.activeSelf && IsLevel(level)) sum = sum * 31 + level.GetInstanceID();
            return sum;
        }

        // Things were put in or taken away: which there are is looked for again.
        private static void ThingsChanged() { thingListAt = -100; Moved(); }

        private sealed class HitDistance : IComparer<RaycastHit>
        {
            public static readonly HitDistance Instance = new HitDistance();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        // Where something carried sits: the nearest surface under the mouse that faces up and is below the pipes and
        // ducts under the ceiling (the carried things' own colliders are off), else the floor.
        private static bool SurfaceUnderMouse(float floor, out Vector3 at)
        {
            at = default;
            Camera camera = ViewCamera();
            if (camera == null) return false;
            Ray ray = MouseRay(camera);
            int count = Physics.RaycastNonAlloc(ray, rayHits, 300, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = rayHits[i];
                if (hit.distance >= best || hit.normal.y < .6f || hit.point.y > floor + HighestShelf) continue;
                best = hit.distance; at = hit.point;
            }
            if (best < float.MaxValue) return true;
            if (!new Plane(Vector3.up, new Vector3(0, floor, 0)).Raycast(ray, out float distance)) return false;
            at = ray.GetPoint(distance);
            return true;
        }

        // The room's floor under a point: the lowest surface below it.
        private static float FloorUnder(Vector3 point, Transform skip)
        {
            float floor = point.y;
            int count = Physics.RaycastNonAlloc(point + Vector3.up * .05f, Vector3.down, rayHits, 6, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (rayHits[i].normal.y > .6f && (skip == null || !rayHits[i].collider.transform.IsChildOf(skip))) floor = Mathf.Min(floor, rayHits[i].point.y);
            return floor;
        }

        // The area whose click zone a point stands in (the game's *_raycast_collider triggers), not counting the areas
        // that can't be moved (the emergency wall's zone holds a whole room).
        private static HideoutArea AreaAt(Vector3 point)
        {
            HideoutArea best = null;
            float smallest = float.MaxValue;
            int count = Physics.RaycastNonAlloc(point + Vector3.up * 15, Vector3.down, rayHits, 30, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
            {
                Collider c = rayHits[i].collider;
                if (!c.isTrigger || c.name.IndexOf("raycast_collider", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Bounds b = c.bounds;
                if (point.x < b.min.x || point.x > b.max.x || point.z < b.min.z || point.z > b.max.z) continue;
                HideoutArea area = c.GetComponentInParent<HideoutArea>();
                if (area == null || Key(area) == null || IsFixed(area)) continue;
                float size = b.size.x * b.size.z;
                if (size < smallest) { smallest = size; best = area; }
            }
            return best;
        }

        private static bool IsFixed(HideoutArea area) => Fixed.Value.Split(',').Select(s => s.Trim()).Contains(Key(area), StringComparer.OrdinalIgnoreCase);

        private static bool Carried(Transform thing) => (dragging || axisDrag >= 0) && selection.Contains(thing);

    }
}
