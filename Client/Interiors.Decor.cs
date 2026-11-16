using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using EFT.Hideout;
using EFT.InputSystem;
using HarmonyLib;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutInteriors
{
    /// <summary>
    /// Decor mode: a button under the overview's own (night vision, lights, customization, generator) turns it on. Then
    /// pointing at one of the hideout's things outlines it; a click selects it (Shift or Ctrl adds or takes away), dragging
    /// on the floor draws a box that selects everything in it, and dragging a selected thing carries the whole selection.
    /// R turns, + and - (or the gizmo's size handles) make bigger and smaller, T sets whom what's put down belongs to (the area it's put in, the one it belonged to, or none: it stays put),
    /// Delete takes it away, Backspace sends the hideout's own things back where the game has them. The panel on the right
    /// holds every thing the hideout's scene has, by kind, to drag in as copies (decorations). Decorations are kept per
    /// profile in BepInEx/config/jrs.hideoutinteriors.decor.&lt;profile id&gt;.json, by the thing they copy.
    /// </summary>
    internal static partial class Interiors
    {
        internal static ConfigEntry<bool> Decorating;

        private sealed class DecorRecord
        {
            public string Source;
            public string Owner = "";
            public float[] Position;
            public float[] Rotation;
            // How big against what it copies (1: as the game has it).
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore), System.ComponentModel.DefaultValue(1f)]
            public float Scale = 1;
            // Stretched along its own X, Y and Z on top of that (left out: its own shape).
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public float[] Stretch;
            // Posters and TV screens: the picture or video, the face's size (a height of 0 follows the picture), muted,
            // and how loud (of the settings' video volume).
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Media;
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Width, Height;
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Muted;
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore), System.ComponentModel.DefaultValue(1f)]
            public float Volume = 1;
            // A list it goes through in order, how long each picture stays, and its frame's style.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string[] Playlist;
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Interval;
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int Frame;
            // Its group, name, lock, light and what it does.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public ThingMeta Meta;

            // What a decoration's tag says of its size and what it shows, kept.
            public void Take(DecorTag tag)
            {
                Scale = tag.Scale;
                Stretch = tag.Stretch == Vector3.one ? null : new[] { tag.Stretch.x, tag.Stretch.y, tag.Stretch.z };
                Media = IsMediaKey(tag.Source) ? tag.Media : null;
                Width = tag.Width; Height = tag.Height; Muted = tag.Muted; Volume = tag.Volume;
                bool media = IsMediaKey(tag.Source);
                Playlist = media && tag.Playlist != null && tag.Playlist.Length > 0 ? tag.Playlist : null;
                Interval = media ? tag.Interval : 0; Frame = media ? tag.Frame : 0;
                Meta = tag.Meta == null || tag.Meta.IsDefault ? null : tag.Meta.Clone();
            }

            // And given to a decoration's tag.
            public void Give(DecorTag tag)
            {
                tag.Scale = Scale > 0 ? Scale : 1;
                tag.Stretch = Stretch?.Length == 3 && Stretch.All(f => f != 0) ? new Vector3(Stretch[0], Stretch[1], Stretch[2]) : Vector3.one;
                tag.Media = Media; tag.Width = Width; tag.Height = Height; tag.Muted = Muted;
                tag.Volume = Mathf.Clamp01(Volume);
                tag.Playlist = Playlist; tag.Interval = Interval; tag.Frame = Frame;
                tag.Meta = Meta != null ? Meta.Clone() : new ThingMeta();
            }
        }

        private static readonly Dictionary<string, DecorRecord> decorLayout = new Dictionary<string, DecorRecord>();
        private static readonly Dictionary<string, Transform> decorObjects = new Dictionary<string, Transform>();
        private static string decorFile;
        private static GameObject decorRoot;

        private static bool decorOn;
        private static readonly List<Transform> selection = new List<Transform>();
        private static readonly Dictionary<Transform, string> selectionKeys = new Dictionary<Transform, string>();
        private static Transform hovered;
        private static HideoutArea iconHovered;

        private enum Belong { AreaPutIn, Own, None }
        private static Belong belong = Belong.AreaPutIn;

        // A drag: what's carried and where each was, the floor it's carried over, how far it's turned.
        private sealed class Carry { public Transform Thing; public Vector3 Offset; public Vector3 FromPosition; public Quaternion FromRotation; }
        private static readonly List<Carry> carrying = new List<Carry>();
        private static readonly List<Collider> carryColliders = new List<Collider>();
        private static readonly List<CullingObject> carryCulled = new List<CullingObject>();
        private static bool dragging, pressing, boxing;
        private static Transform pressed;
        // The press was on the one thing already selected (let go without dragging, a poster or screen is gone into).
        private static bool pressedSelected;
        // Copies not put down yet (cancelling takes them away): from the panel (let go over it, they go back), or
        // duplicated or pasted (they follow the mouse until a click).
        private static readonly List<Transform> newCopies = new List<Transform>();
        private static bool fromPanel, clickToPut;
        // Shift or Ctrl held on a thing: a click adds or takes it away; dragging draws a box that adds.
        private static bool pressingToggle;
        private static Vector2 pressAt;
        private static float dragFloor, dragYaw;

        private static readonly Color Selected = new Color(1f, .72f, .25f, 1f);
        private static readonly List<Transform> hoverList = new List<Transform>(1);

        internal static void BindDecor(ConfigFile config)
        {
            Decorating = config.Bind("Decorating", "Enabled", true,
                "A decor button on the hideout's overview: move the hideout's things a few at a time, give them to other areas, hide them, and put in copies of anything the hideout has.");
            BindGizmo(config);
            BindMedia(config);
            BindLights();
            BindArrange(config);
            BindPhoto(config);
            BindShare(config);
            BindQuick(config);
            PanelScale = config.Bind("Decorating", "Panel size", 1f,
                new ConfigDescription("How big decor mode's panel and its line of keys are (1 = as made).", new AcceptableValueRange<float>(.6f, 1.5f)));
        }

        internal static ConfigEntry<float> PanelScale;

        // ---- keeping decorations ------------------------------------------------------------------------------

        private static void LoadDecor(string id)
        {
            decorLayout.Clear();
            decorFile = KeptIn("decor", id);
            try
            {
                if (File.Exists(decorFile))
                    foreach (var entry in JsonConvert.DeserializeObject<Dictionary<string, DecorRecord>>(File.ReadAllText(decorFile)) ?? new Dictionary<string, DecorRecord>())
                        if (entry.Value?.Source != null && entry.Value.Position?.Length == 3 && entry.Value.Rotation?.Length == 4) decorLayout[entry.Key] = entry.Value;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't read {decorFile}, so there are no decorations: {e.Message}"); }
        }

        private static void SaveDecor()
        {
            RefreshLimits();
            if (decorFile == null) return;
            try { File.WriteAllText(decorFile, JsonConvert.SerializeObject(decorLayout, Formatting.Indented)); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't save {decorFile}: {e.Message}"); }
        }

        private static void ForgetDecor()
        {
            CloseDecor();
            ForgetView();
            ForgetMedia();
            decorObjects.Clear();
            decorRoot = null;
            catalog = null;
            decorButton = null; overlay = null;
        }

        private static Transform DecorRoot()
        {
            if (decorRoot == null && style != null)
            {
                decorRoot = new GameObject("Hideout extensions decor");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(decorRoot, style.scene);
            }
            return decorRoot != null ? decorRoot.transform : null;
        }

        // The decorations not put in yet this visit (each waits for the thing it copies and the area it belongs to).
        private static void SpawnDecor()
        {
            foreach (var entry in decorLayout)
            {
                if (decorObjects.TryGetValue(entry.Key, out Transform there) && there != null) continue;
                Transform source = Resolve(entry.Value.Source);
                if (source == null) continue;
                Transform parent = ParentFor(source, null, entry.Value.Owner, out _);
                if (parent == null) continue;
                Transform copy = MakeCopy(source, parent, false);
                if (copy == null) continue;
                if (!PlaceDecor(entry.Key, entry.Value, copy)) { UnityEngine.Object.Destroy(copy.gameObject); continue; }
                decorObjects[entry.Key] = copy;
                thingList.Add(copy);
            }
        }

        // Keeps a decoration where it stands now, for an owner.
        private static void KeepDecor(Transform copy, string owner)
        {
            DecorTag tag = copy.GetComponent<DecorTag>();
            if (tag == null) return;
            if (string.IsNullOrEmpty(tag.Id)) tag.Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            Transform parent = ParentFor(copy, null, owner, out Transform ownerRoot);
            if (parent == null) { owner = ""; parent = ParentFor(copy, null, owner, out ownerRoot); }
            if (copy.parent != parent) copy.SetParent(parent, true);
            Vector3 position = ownerRoot != null ? ownerRoot.InverseTransformPoint(copy.position) : copy.position;
            Quaternion rotation = ownerRoot != null ? Quaternion.Inverse(ownerRoot.rotation) * copy.rotation : copy.rotation;
            var record = new DecorRecord
            {
                Source = tag.Source,
                Owner = owner,
                Position = new[] { position.x, position.y, position.z },
                Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
            };
            record.Take(tag);
            decorLayout[tag.Id] = record;
            decorObjects[tag.Id] = copy;
            if (!thingList.Contains(copy)) thingList.Add(copy);
            WalkerPut(copy);
            Moved();
        }

        private static readonly string[] DropParts = { "BALLISTIC", "COLLIDER", "Collider", "collider", "colider", "Colider", "Ballistic", "ballistic", "bal_", "lootable", "SpawnPoint", "test_hand" };

        // A copy of a thing that's only its look (and its lights, as plain lights): no scripts, no game colliders, one box
        // round it to stand against and point at. For a picture (thumbnail) not even that.
        private static Transform MakeCopy(Transform source, Transform parent, bool picture)
        {
            var holder = new GameObject("Hideout extensions copy holder");
            holder.SetActive(false);
            if (parent != null) holder.transform.SetParent(parent, false);
            GameObject copy;
            try { copy = UnityEngine.Object.Instantiate(source.gameObject, holder.transform); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't copy {source.name}: {e.Message}"); UnityEngine.Object.Destroy(holder); return null; }
            copy.name = source.name;
            copy.transform.SetPositionAndRotation(source.position, source.rotation);
            // As big as the game has what it copies (whatever size that's been made since).
            Vector3 size = originals.TryGetValue(source, out Origin had) ? had.WorldScale : source.lossyScale;
            copy.transform.localScale = size;
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true).ToArray())
                if (t != null && t != copy.transform && DropParts.Any(p => t.name.IndexOf(p, StringComparison.Ordinal) >= 0)) UnityEngine.Object.DestroyImmediate(t.gameObject);
            for (int pass = 0; pass < 3; pass++)
                foreach (Component c in copy.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer || c is LODGroup) continue;
                    if (c is Light && !picture) continue;
                    if (pass == 0 && !(c is MonoBehaviour)) continue;
                    try { UnityEngine.Object.DestroyImmediate(c); } catch (Exception) { }
                }
            foreach (MeshRenderer r in copy.GetComponentsInChildren<MeshRenderer>(true)) { r.enabled = true; r.forceRenderingOff = false; }
            foreach (LODGroup g in copy.GetComponentsInChildren<LODGroup>(true)) g.enabled = true;
            foreach (Light l in copy.GetComponentsInChildren<Light>(true)) { l.enabled = true; l.shadows = LightShadows.None; }
            if (!picture)
            {
                // One box round it (in its own space), so the player stands against it and the mouse finds it.
                if (MeshBounds(copy.transform, out Bounds world))
                {
                    var box = copy.AddComponent<BoxCollider>();
                    Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 local = copy.transform.InverseTransformPoint(world.center + Vector3.Scale(world.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                        lo = Vector3.Min(lo, local); hi = Vector3.Max(hi, local);
                    }
                    box.center = (lo + hi) / 2; box.size = Vector3.Max(hi - lo, Vector3.one * .02f);
                }
                DecorTag tag = copy.AddComponent<DecorTag>();
                tag.Source = ItemKey(null, source);
                tag.BaseScale = size;
            }
            copy.transform.SetParent(parent, true);
            UnityEngine.Object.Destroy(holder);
            copy.SetActive(true);
            return copy.transform;
        }

        // ---- the mode -----------------------------------------------------------------------------------------

        private static void OpenDecor()
        {
            if (decorOn || style == null) return;
            decorOn = true;
            belong = Belong.AreaPutIn;
            // Things given to areas whose level changed since come back to them.
            PlaceAll();
            BuildUi();
            AreaBar(false);
            decorButton?.SetSelectedStatus(true);
            Status();
        }

        private static void CloseDecor()
        {
            if (!decorOn) return;
            if (dragging) CancelDrag();
            StopUsing();
            ExitPhoto();
            ExitView();
            BarGone();
            StripGone();
            CancelAsk();
            decorOn = false;
            pressing = boxing = pressingToggle = false;
            selection.Clear(); selectionKeys.Clear(); hovered = null;
            ItemOutline.Clear();
            if (ui != null) UnityEngine.Object.Destroy(ui);
            ui = null; tipUi = null;
            HideGizmo();
            AreaBar(true);
            if (tipUi != null) tipUi.SetActive(false);
            if (decorButton != null) decorButton.SetSelectedStatus(false);
            QuickReset();
        }

        internal static bool DecorOn => decorOn;

        private static void DecorTick()
        {
            // Open just to use one thing in first person: as long as the player's walking about.
            if (quickFp ? !FirstPersonView() : !Overview()) { CloseDecor(); return; }
            if (quickUse && quickDone) { CloseDecor(); return; }
            if (quickFp && Input.GetKeyDown(KeyCode.Escape)) { QuickEscape(); return; }
            // Photo mode: nothing of decor mode's on screen, and every key and the mouse are the camera's.
            if (photoOn) { PhotoTick(); return; }
            // The game's screen changed size (or the panel's size setting did): the panel's laid out again.
            if (ui != null && UiSizeChanged()) { CloseUi(); BuildUi(); }
            Vector2 mouse = Input.mousePosition;
            bool overPanel = OverPanel(mouse), overBar = OverBar(mouse), overUi = overPanel || overBar;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Ctrl();
            ViewTick(overUi);
            MediaBarTick();
            LightBarTick();
            if (Using) { UseTick(overPanel, overBar); return; }
            // Open just for a poster: only its bar.
            if (quickUse) { QuickIdleTick(mouse, overBar); return; }

            if (axisDrag >= 0)
            {
                AxisDrag(ctrl);
                Gizmo(); Status(); DecorTip(null);
                return;
            }
            if (dragging)
            {
                if (Input.GetKeyDown(KeyCode.R)) dragYaw += shift ? 15 : 90;
                if (Input.GetKeyDown(KeyCode.T)) { belong = (Belong)(((int)belong + 1) % 3); }
                if (Input.GetMouseButtonDown(1)) { CancelDrag(); Status(); return; }
                Drag();
                if (clickToPut ? Input.GetMouseButtonDown(0) : Input.GetMouseButtonUp(0))
                {
                    // A new copy let go over the panel goes back in the box; let go over an area's icon, it's that area's.
                    if (newCopies.Count > 0 && overPanel) CancelDrag();
                    else
                    {
                        HideoutArea iconArea = IconUnderMouse();
                        var put = carrying.Select(c => c.Thing).ToList();
                        // A new poster or screen with nothing on it yet: its bar's box is ready for a link.
                        Transform blank = newCopies.Count == 1 && put.Count == 1 && put[0] != null && mediaStates.TryGetValue(put[0], out MediaState fresh)
                            && fresh.Tag.Source != ComputerKey && string.IsNullOrEmpty(fresh.Tag.Media) ? put[0] : null;
                        Commit(belong);
                        if (iconArea != null) GiveTo(iconArea, put);
                        // A screen fitted to a TV (or carried off one) changed shape: its page or video drawn for it.
                        foreach (Transform t in put) Reshaped(t);
                        addressWanted = blank;
                    }
                }
                Status(); Gizmo(); DecorTip(null);
                return;
            }

            // What's pointed at: one of the move arrows, an area's icon (to give the selection to), or a thing.
            int axis = overUi || boxing || pressing ? -1 : AxisUnderMouse();
            if (axis != axisHot) { axisHot = axis; Status(); }
            HideoutArea icon = overUi || boxing || axis >= 0 ? null : IconUnderMouse();
            if (icon != iconHovered) { iconHovered = icon; Status(); }
            Transform now = overUi || boxing || icon != null || axis >= 0 ? null : ThingUnderMouse(out _, out _);
            if (now != hovered) { hovered = now; Outlines(); Status(); }

            Typing();
            AskTick();
            if (resetArmedUntil > 0 && Time.unscaledTime > resetArmedUntil) { resetArmedUntil = 0; if (resetLabel != null) resetLabel.text = "<b>RESET ALL</b>"; }
            if (!boxing && !pressing && !KeysTaken) Keys(shift, ctrl);
            // A key went into a poster, screen or computer, or into photo mode.
            if (Using || photoOn) return;

            // A right-click on a thing in the panel: a favourite (or not one any more).
            if (Input.GetMouseButtonDown(1) && overPanel && !panelHidden) { PanelRightClick(mouse); return; }
            if (Input.GetMouseButtonDown(0))
            {
                // A click on the box that's asking for a name is nothing; one anywhere else lets it go.
                if (asking && Inside(askBox, mouse)) return;
                CancelAsk();
                if (BarClick(mouse)) return;
                if (StripClick(mouse)) return;
                StopAddress();
                if (overPanel) { PanelClick(mouse); return; }
                StopTyping();
                if (axisHot >= 0) { StartAxisDrag(axisHot); Status(); return; }
                if (iconHovered != null)
                {
                    if (selection.Count > 0) GiveTo(iconHovered, selection);
                    else EFT.Communications.NotificationManager.DisplayMessageNotification("Select things first, then click an area's icon to give them to it.");
                    return;
                }
                if (hovered != null)
                {
                    pressed = hovered; pressAt = mouse;
                    pressedSelected = !shift && !ctrl && selection.Count == 1 && selection[0] == hovered;
                    // With Shift or Ctrl: a click adds or takes away, a drag draws a box that adds (from on a thing too).
                    if (shift || ctrl) pressingToggle = true;
                    else
                    {
                        if (!selection.Contains(hovered)) { ClearSelection(); Select(hovered); SelectGroup(hovered); }
                        pressing = true;
                    }
                }
                else
                {
                    if (!shift && !ctrl) ClearSelection();
                    boxing = true; pressAt = mouse;
                }
                Outlines(); Status();
            }
            if (pressing && Input.GetMouseButton(0) && (mouse - pressAt).magnitude > 6 && pressed != null) { pressing = false; StartDrag(pressed); }
            if (pressingToggle && Input.GetMouseButton(0) && (mouse - pressAt).magnitude > 6) { pressingToggle = false; boxing = true; }
            if (boxing) BoxShow(pressAt, mouse);
            if (Input.GetMouseButtonUp(0))
            {
                if (pressingToggle && pressed != null) { if (selection.Contains(pressed)) UnselectGroup(pressed); else { Select(pressed); SelectGroup(pressed); } }
                if (boxing) { BoxSelect(pressAt, mouse); ExpandGroups(); BoxHide(); }
                // A click (no drag) on the one thing selected, a poster or screen: into it. A door: it opens or shuts.
                bool again = pressing && pressedSelected && pressed != null && pressed == hovered;
                Transform into = again && IsMedia(pressed) ? pressed : null;
                bool swing = again && into == null && ClickOpens(pressed);
                pressing = boxing = pressingToggle = pressedSelected = false;
                Outlines(); Status();
                if (into != null) { EnterMedia(into); if (Using) return; }
                if (swing) Interact();
            }
            if (overPanel) PanelScroll();
            else if (overBar) LibraryScroll();
            Thumbnails();
            Gizmo();
            DecorTip(overUi || boxing ? null : hovered);
        }

        // Decor mode's keys, while nothing's carried or typed.
        private static void Keys(bool shift, bool ctrl)
        {
            if (ctrl)
            {
                if (Input.GetKeyDown(KeyCode.Z)) { if (shift) Redo(); else Undo(); }
                if (Input.GetKeyDown(KeyCode.Y)) Redo();
                if (Input.GetKeyDown(KeyCode.D)) DuplicateSelection();
                if (Input.GetKeyDown(KeyCode.C)) CopySelection();
                if (Input.GetKeyDown(KeyCode.V)) Paste();
                if (Input.GetKeyDown(KeyCode.A)) SelectAllShowing();
                if (Input.GetKeyDown(KeyCode.G)) { if (shift) Ungroup(); else GroupSelection(); }
                if (Input.GetKeyDown(KeyCode.S)) SavePrefabAsk();
                return;
            }
            if (Input.GetKeyDown(KeyCode.N)) CycleSnap();
            if (Input.GetKeyDown(KeyCode.C)) { EnterPhoto(); if (photoOn) return; }
            if (shift && Input.GetKeyDown(KeyCode.L)) { UnlockAll(); return; }
            if (Input.GetKeyDown(KeyCode.T)) { belong = (Belong)(((int)belong + 1) % 3); Status(); }
            if (Input.GetKeyDown(GizmoKey.Value)) SwitchGizmo();
            if (Input.GetKeyDown(KeyCode.H)) TogglePanel();
            // The camera: V goes through the views, 1 to 5 picks one, F looks at the selection.
            if (Input.GetKeyDown(KeyCode.V)) CycleView();
            if (Input.GetKeyDown(KeyCode.F)) Refocus();
            for (int i = 0; i < ViewNames.Length; i++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) SetView((ViewKind)i);
            if (selection.Count == 0) return;
            if (Input.GetKeyDown(KeyCode.R)) TurnSelection(shift ? 15 : 90);
            if (Input.GetKeyDown(KeyCode.Delete)) RemoveSelection();
            if (Input.GetKeyDown(KeyCode.Backspace)) RestoreSelection();
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.RightBracket)) SizeSelection(shift ? 1.02f : 1.1f);
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.LeftBracket)) SizeSelection(shift ? 1 / 1.02f : 1 / 1.1f);
            if (Input.GetKeyDown(KeyCode.U)) UseSelection(true);
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) UseSelection(false);
            if (Input.GetKeyDown(KeyCode.P)) PauseSelection();
            if (Input.GetKeyDown(KeyCode.M)) MuteSelection();
            if (Input.GetKeyDown(KeyCode.L)) { LockSelection(); return; }
            if (Input.GetKeyDown(KeyCode.F2)) RenameSelection();
            if (Input.GetKeyDown(KeyCode.X)) FlipSelection(0);
            if (Input.GetKeyDown(KeyCode.Z)) FlipSelection(2);
            if (Input.GetKeyDown(KeyCode.B)) DropSelection();
            if (Input.GetKeyDown(KeyCode.O)) SwitchLights();
            if (Input.GetKeyDown(KeyCode.I)) Interact();
            if (Input.GetKeyDown(KeyCode.J)) ToggleWander();
        }

        // Ctrl+A: everything showing on screen that can be seen.
        private static void SelectAllShowing()
        {
            Camera camera = ViewCamera();
            if (camera == null) return;
            foreach (Transform t in ShowingThings())
            {
                if (IsLocked(t) || !Bounds(t, out Bounds b)) continue;
                Vector3 v = camera.WorldToViewportPoint(b.center);
                if (v.z > 0 && v.x >= 0 && v.x <= 1 && v.y >= 0 && v.y <= 1 && Seen(camera, t, b)) Select(t);
            }
            ExpandGroups();
            Outlines(); Status();
        }

        // ---- selecting ----------------------------------------------------------------------------------------

        private static void Select(Transform t)
        {
            if (t == null || selection.Contains(t)) return;
            string key = null;
            if (t.GetComponent<DecorTag>() == null)
            {
                ItemOf(t, out HideoutArea area);
                key = ItemKey(area, t);
                if (key == null) return;   // not one of the hideout's own (a room part)
            }
            selection.Add(t); selectionKeys[t] = key;
        }

        private static void Unselect(Transform t) { selection.Remove(t); selectionKeys.Remove(t); }
        private static void ClearSelection() { selection.Clear(); selectionKeys.Clear(); }

        private static void Outlines()
        {
            selection.RemoveAll(t => t == null);
            // One outline (the game's): white round what's pointed at, amber round the selection (and what's pointed
            // at with it) once there is one.
            hoverList.Clear();
            hoverList.AddRange(selection);
            if (hovered != null && !selection.Contains(hovered)) hoverList.Add(hovered);
            ItemOutline.Set(hoverList, selection.Count > 0 ? Selected : Color.white);
        }

        // Everything showing whose middle is in the box on screen.
        private static void BoxSelect(Vector2 a, Vector2 b)
        {
            if ((a - b).magnitude < 6) return;
            Camera camera = ViewCamera();
            if (camera == null) return;
            var rect = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            foreach (Transform t in ShowingThings())
            {
                if (IsLocked(t) || !Bounds(t, out Bounds bounds)) continue;
                Vector3 view = camera.WorldToViewportPoint(bounds.center);
                if (view.z <= 0) continue;
                if (rect.Contains(new Vector2(view.x * Screen.width, view.y * Screen.height)) && Seen(camera, t, bounds)) Select(t);
            }
        }

        // The things that can be picked that are showing now: every area's (its showing level's), the ones set free, the decorations.
        private static IEnumerable<Transform> ShowingThings()
        {
            var seen = new HashSet<Transform>();
            foreach (HideoutArea area in Areas())
            {
                if (Key(area) == null || !area.gameObject.activeInHierarchy) continue;
                foreach (Transform level in area.transform)
                {
                    if (!IsLevel(level) || !level.gameObject.activeInHierarchy) continue;
                    foreach (Transform t in ItemsUnder(level, true))
                        if (seen.Add(t) && Pickable(t)) yield return t;
                }
            }
            foreach (var (t, _) in OutsideThings(true))
                if (seen.Add(t)) yield return t;
            if (decorRoot != null)
                foreach (Transform t in decorRoot.transform)
                    if (t.gameObject.activeInHierarchy && seen.Add(t)) yield return t;
        }

        // ---- carrying -----------------------------------------------------------------------------------------

        private static void StartDrag(Transform grabbed)
        {
            if (selection.Count == 0) return;
            Vector3 anchor = grabbed.position;
            if (Bounds(grabbed, out Bounds b)) anchor = new Vector3(b.center.x, b.min.y, b.center.z);
            dragFloor = fromPanel ? 0 : FloorUnder(anchor, grabbed);
            carrying.Clear(); carryColliders.Clear(); carryCulled.Clear();
            mediaCarried = null;
            foreach (Transform t in selection)
            {
                carrying.Add(new Carry { Thing = t, Offset = t.position - anchor, FromPosition = t.position, FromRotation = t.rotation });
                foreach (Collider c in t.GetComponentsInChildren<Collider>()) if (c.enabled) { c.enabled = false; carryColliders.Add(c); }
                carryCulled.AddRange(t.GetComponentsInChildren<CullingObject>(true));
            }
            dragYaw = 0;
            dragging = true;
        }

        private static void Drag()
        {
            // A poster or screen on its own goes flat on walls and onto TVs.
            if (carrying.Count == 1 && IsMedia(carrying[0].Thing)) { MediaDrag(carrying[0].Thing); return; }
            if (!SurfaceUnderMouse(dragFloor, out Vector3 at)) return;
            Quaternion turn = Quaternion.Euler(0, dragYaw, 0);
            // On the grid, if that's on; then the last few centimetres to a thing beside it, if that is.
            at = SnapPoint(at);
            foreach (Carry c in carrying)
                if (c.Thing != null) c.Thing.SetPositionAndRotation(at + turn * c.Offset, turn * c.FromRotation);
            SnapToEdges();
            if (CullingManager.Instance != null)
                foreach (CullingObject culled in carryCulled)
                    if (culled != null) try { CullingManager.Instance.UpdateSphere(culled); } catch (Exception) { }
        }

        private static void EndDrag()
        {
            foreach (Collider c in carryColliders) if (c != null) c.enabled = true;
            carryColliders.Clear(); carryCulled.Clear(); carrying.Clear();
            dragging = false; newCopies.Clear(); fromPanel = clickToPut = false;
            mediaCarried = null; tvHover = null;
            Physics.SyncTransforms();
        }

        private static void CancelDrag()
        {
            if (newCopies.Count > 0)
            {
                foreach (Transform copy in newCopies.ToList())
                {
                    if (copy == null) continue;
                    Unselect(copy);
                    thingList.Remove(copy);
                    UnityEngine.Object.Destroy(copy.gameObject);
                }
            }
            else
            {
                MediaSizeBack();
                foreach (Carry c in carrying) if (c.Thing != null) c.Thing.SetPositionAndRotation(c.FromPosition, c.FromRotation);
            }
            EndDrag();
            Outlines();
        }

        // Ctrl+D: a copy of each selected thing (a decoration of the hideout's own things, the same again of
        // decorations: size, picture and all), following the mouse until a click puts them down.
        private static void DuplicateSelection()
        {
            selection.RemoveAll(t => t == null);
            if (selection.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Select things first, then Ctrl+D to copy them."); return; }
            var made = new List<Transform>();
            var counted = new Dictionary<string, int>();
            var groups = new Dictionary<string, string>();
            foreach (Transform t in selection)
            {
                DecorTag tag = t.GetComponent<DecorTag>();
                Transform source = tag != null ? Resolve(tag.Source) : t;
                if (source == null) continue;
                if (tag == null && (!selectionKeys.TryGetValue(t, out string key) || key == null)) continue;
                string sourceKey = tag != null ? tag.Source : ItemKey(null, t);
                int limit = ExtraLimit(sourceKey);
                if (limit > 0)
                {
                    counted.TryGetValue(sourceKey, out int extra);
                    if (ExtraCount(sourceKey) + extra >= limit)
                    {
                        EFT.Communications.NotificationManager.DisplayWarningNotification($"Your hideout can only have {limit} of {Pretty(t.name)}.");
                        continue;
                    }
                    counted[sourceKey] = extra + 1;
                }
                Transform copy = MakeCopy(source, DecorRoot(), false);
                if (copy == null) continue;
                copy.SetPositionAndRotation(t.position, t.rotation);
                DecorTag copied = copy.GetComponent<DecorTag>();
                if (tag != null)
                {
                    copied.Scale = tag.Scale; copied.Stretch = tag.Stretch;
                    copied.Media = tag.Media; copied.Width = tag.Width; copied.Height = tag.Height; copied.Muted = tag.Muted; copied.Volume = tag.Volume;
                    copied.Playlist = tag.Playlist; copied.Interval = tag.Interval; copied.Frame = tag.Frame;
                    ApplyScale(copy);
                    SetupMedia(copy);
                }
                // One of the hideout's own that's been sized: its copy's the same size.
                else if (itemSizes.TryGetValue(t, out Vector3 sized)) SetSize(copy, sized);
                // Its name, its light and what it does go with it; its lock doesn't, and a group copied is a group
                // of its own.
                ThingMeta meta = MetaOf(t, false);
                if (meta != null && !meta.IsDefault)
                {
                    copied.Meta = meta.Clone();
                    copied.Meta.Locked = false; copied.Meta.Still = false;
                    if (meta.Group != null)
                    {
                        if (!groups.TryGetValue(meta.Group, out string group)) groups[meta.Group] = group = NewGroup(GroupName(meta.Group));
                        copied.Meta.Group = group;
                    }
                    ApplyMeta(copy, copied.Meta, true);
                }
                made.Add(copy);
            }
            if (made.Count == 0) return;
            ClearSelection();
            foreach (Transform t in made) Select(t);
            newCopies.AddRange(made);
            fromPanel = false;
            StartDrag(made[0]);
            clickToPut = true;
            Drag();
            ThingsChanged();
            Outlines(); Status();
        }

        // Puts what's carried (or turned) down for good: whom it belongs to, then saved.
        private static void Commit(Belong how)
        {
            Checkpoint();
            var things = carrying.Count > 0 ? carrying.Select(c => c.Thing).ToList() : selection.ToList();
            EndDrag();
            string joined = null;
            foreach (Transform t in things)
            {
                if (t == null) continue;
                DecorTag tag = t.GetComponent<DecorTag>();
                string key = tag == null && selectionKeys.TryGetValue(t, out string k) ? k : null;
                string owner;
                if (how == Belong.None) owner = "";
                else if (how == Belong.Own && tag == null && key != null) owner = OwnerOf(key);
                else if (how == Belong.Own && tag != null && tag.Id != null && decorLayout.TryGetValue(tag.Id, out DecorRecord r)) owner = r.Owner;
                else
                {
                    Vector3 middle = Bounds(t, out Bounds b) ? b.center : t.position;
                    // In an area's zone: that area's. Outside every zone (curtains on a wall, things at an area's edge):
                    // whose it was, not none (none is only ever chosen with T).
                    if (AreaAt(middle) is HideoutArea area) owner = Key(area);
                    else if (tag == null && key != null) owner = OwnerOf(key);
                    else owner = tag?.Id != null && decorLayout.TryGetValue(tag.Id, out DecorRecord had) ? had.Owner : "";
                }
                if (tag != null) KeepDecor(t, owner);
                else if (key != null) KeepItem(t, key, owner);
                joined = owner;
            }
            SaveItems(); SaveDecor();
            Outlines();
            RefreshRows();
            if (things.Count > 0) Plugin.Log.LogInfo($"Decor: put down {things.Count} thing(s)" + (things.Count == 1 ? $" ({(joined == "" ? "no area" : joined)})." : "."));
        }

        private static void TurnSelection(float degrees)
        {
            selection.RemoveAll(t => t == null);
            if (selection.Count == 0) return;
            Vector3 middle = Vector3.zero;
            foreach (Transform t in selection) middle += t.position;
            middle /= selection.Count;
            Quaternion turn = Quaternion.Euler(0, degrees, 0);
            foreach (Transform t in selection) t.SetPositionAndRotation(middle + turn * (t.position - middle), turn * t.rotation);
            Commit(Belong.Own);
        }

        // Decorations go; the hideout's own things are hidden (Backspace, or the panel's Hidden list, brings them back).
        private static void RemoveSelection()
        {
            int gone = 0, hidden = 0;
            Checkpoint();
            foreach (Transform t in selection.ToList())
            {
                if (t == null) continue;
                DecorTag tag = t.GetComponent<DecorTag>();
                if (tag != null)
                {
                    if (tag.Id != null) { decorLayout.Remove(tag.Id); decorObjects.Remove(tag.Id); }
                    thingList.Remove(t);
                    UnityEngine.Object.Destroy(t.gameObject);
                    gone++;
                    Moved();
                }
                else if (selectionKeys.TryGetValue(t, out string key) && key != null)
                {
                    KeepItem(t, key, OwnerOf(key), hidden: true);
                    hidden++;
                }
            }
            ClearSelection(); hovered = null;
            SaveItems(); SaveDecor();
            Outlines(); Status(); RefreshHiddenTab(); RefreshRows();
            Plugin.Log.LogInfo($"Decor: {gone} decoration(s) removed, {hidden} thing(s) hidden.");
        }

        private static void RestoreSelection()
        {
            int back = 0;
            Checkpoint();
            foreach (Transform t in selection)
                if (t != null && t.GetComponent<DecorTag>() == null && selectionKeys.TryGetValue(t, out string key) && key != null) { RestoreItem(t, key); back++; }
            SaveItems();
            Outlines(); Status();
            if (back > 0) Plugin.Log.LogInfo($"Decor: {back} thing(s) back where the game has them.");
        }

        // ---- patches ------------------------------------------------------------------------------------------

        internal static void PatchDecor(Harmony harmony)
        {
            var command = AccessTools.Method(typeof(HideoutScreenOverlay), nameof(HideoutScreenOverlay.TranslateCommand));
            if (command != null) harmony.Patch(command, prefix: new HarmonyMethod(typeof(Interiors), nameof(Command)));
            var edge = AccessTools.Method(typeof(FreeSpaceCameraMover), nameof(FreeSpaceCameraMover.MoveAccordingMousePosition));
            if (edge != null) harmony.Patch(edge, prefix: new HarmonyMethod(typeof(Interiors), nameof(EdgePan)));
            // While the search box has the keyboard, the overview's keys (WASD, Q and E) leave the camera alone.
            var move = AccessTools.Method(typeof(FreeSpaceCameraMover), nameof(FreeSpaceCameraMover.Move), new[] { typeof(Vector3) });
            if (move != null) harmony.Patch(move, prefix: new HarmonyMethod(typeof(Interiors), nameof(NotTyping)));
            var turn = AccessTools.Method(typeof(FreeSpaceCameraMover), nameof(FreeSpaceCameraMover.Rotate));
            if (turn != null) harmony.Patch(turn, prefix: new HarmonyMethod(typeof(Interiors), nameof(NotTyping)));
            // Using a screen in first person: the player stands still and the mouse is free.
            var input = AccessTools.Method(typeof(InputNode), nameof(InputNode.TranslateInput));
            if (input != null) harmony.Patch(input, prefix: new HarmonyMethod(typeof(Interiors), nameof(PlayerInput)));
        }

        // In decor mode Escape closes it (not the hideout), Enter doesn't walk in, and the wheel over the panel scrolls
        // it instead of zooming.
        private static bool Command(ECommand command, ref InputNode.ETranslateResult __result)
        {
            if (!decorOn) return true;
            // Photo mode: every key is the camera's (Escape is done).
            if (photoOn)
            {
                if (command == ECommand.Escape) ExitPhoto();
                __result = InputNode.ETranslateResult.Block;
                return false;
            }
            // Using a computer or screen: every key is the page's (Escape puts the library away, lets the box go, is done).
            if (Using)
            {
                if (command == ECommand.Escape)
                {
                    if (libraryRect != null) { CloseLibrary(); BarLook(); }
                    else if (addressTyping) StopAddress();
                    else StopUsing();
                }
                __result = InputNode.ETranslateResult.Block;
                return false;
            }
            // Typing in the search box or a bar's: every key is the box's (Escape lets it go).
            if (typing || addressTyping || asking)
            {
                if (command == ECommand.Escape) { StopTyping(); StopAddress(); CancelAsk(); }
                __result = InputNode.ETranslateResult.Block;
                return false;
            }
            switch (command)
            {
                case ECommand.Escape:
                    // Carrying: put back. In a camera view: back to the overview. Else decor mode closes.
                    if (dragging) { CancelDrag(); Status(); }
                    else if (ViewActive) ExitView();
                    else CloseDecor();
                    __result = InputNode.ETranslateResult.Block;
                    return false;
                case ECommand.Enter:
                    __result = InputNode.ETranslateResult.Block;
                    return false;
                case ECommand.ScrollPrevious:
                case ECommand.ScrollNext:
                    // In a camera view the wheel comes closer (ours), not the overview's zoom.
                    if (!ViewActive && !OverPanel(Input.mousePosition) && !OverBar(Input.mousePosition)) return true;
                    __result = InputNode.ETranslateResult.Block;
                    return false;
            }
            return true;
        }

        // The overview's camera stays put while typing, and while a camera view of ours has the keys.
        private static bool NotTyping() => !(decorOn && (KeysTaken || ViewActive));

        // The camera slides when the mouse nears the screen's edge; not while it's on the panel (on the right edge).
        private static bool EdgePan() => !(decorOn && (OverPanel(Input.mousePosition) || OverBar(Input.mousePosition) || boxing || pressing || ViewActive || volumeHeld));
    
        // ---- pointing at things on the plain overview ------------------------------------------------------------

        private static GameObject tipUi;
        private static RectTransform tipRoot, tip;
        private static TextMeshProUGUI tipText;
        private static readonly AccessTools.FieldRef<HideoutScreenOverlay, AreaScreenSubstrate> substrate =
            AccessTools.FieldRefAccess<HideoutScreenOverlay, AreaScreenSubstrate>("_areaSubstrate");

        // An area's own screen is open (the overview's camera is on it).
        private static bool AreaOpen()
        {
            try { return overlay != null && substrate(overlay) is AreaScreenSubstrate s && s != null && s.gameObject.activeSelf; }
            catch (Exception) { return false; }
        }

        // The note by the mouse, on decor mode's screen (over its panel).
        private static void Tip()
        {
            if (tipUi != null || uiRoot == null) return;
            if (font == null) font = UnityEngine.Object.FindObjectOfType<AreaIcon>()?._currentAreaLevel?.font;
            tipRoot = uiRoot;
            tip = UiRect("Tip", tipRoot, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0, 1));
            tipUi = tip.gameObject;
            tip.sizeDelta = new Vector2(440, 50);
            var back = tip.gameObject.AddComponent<Image>();
            back.color = new Color(0, 0, 0, .75f); back.raycastTarget = false;
            tipText = Text("Text", tip, "", 15, Vector2.zero, Vector2.zero, TextAlignmentOptions.MidlineLeft, Ink, false);
            tipText.rectTransform.anchorMin = Vector2.zero; tipText.rectTransform.anchorMax = Vector2.one;
            tipText.rectTransform.offsetMin = new Vector2(10, 4); tipText.rectTransform.offsetMax = new Vector2(-8, -4);
        }

        // In decor mode the name of what's pointed at goes by the mouse too.
        private static Transform tipFor;
        private static void DecorTip(Transform t)
        {
            if (t != tipFor)
            {
                tipFor = t;
                if (t != null)
                {
                    Tip();
                    if (tipText != null)
                        tipText.text = $"<b>{Plain(NameOf(t)).ToUpperInvariant()}</b> · {OwnerText(t)}{MediaText(t)}{MetaText(t)}\n<size=13>{TipHint(t)}</size>";
                }
            }
            if (tipUi != null)
            {
                tipUi.SetActive(t != null);
                tipUi.transform.SetAsLastSibling();
            }
            if (t != null && tip != null)
            {
                // By the mouse, on its left near the right edge.
                RectTransformUtility.ScreenPointToLocalPointInRectangle(tipRoot, Input.mousePosition, uiCamera, out Vector2 at);
                bool flip = at.x + 22 + tip.sizeDelta.x > tipRoot.rect.width / 2;
                tip.pivot = new Vector2(flip ? 1 : 0, 1);
                tip.anchoredPosition = at + new Vector2(flip ? -22 : 22, -22);
            }
        }

        private static string TipHint(Transform t)
        {
            if (!selection.Contains(t)) return IsMedia(t) ? "Click to select: its bar (what's on it, its size, its sound) is along the top" : "Click to select · Shift+click or Shift+drag to add";
            if (selection.Count == 1 && IsPoster(t)) return "Click again: paste a link · drag to move · Ctrl+D: copy";
            if (selection.Count == 1 && IsMedia(t)) return $"Click again: {(IsComputer(t) ? "use it" : "watch it")} · drag to move · Ctrl+D: copy";
            if (selection.Count == 1 && ClickOpens(t)) return "Click again (or I): open or shut · drag to move · Ctrl+D: copy";
            if (HasLights(t)) return "Drag to move · O: its light's switch (its bar is along the top) · Ctrl+D: copy";
            return "Drag to move · arrows to move exactly · Ctrl+D: copy";
        }

        // What's set on a thing, in a few words: its group, and that it's open, alive or wandering.
        private static string MetaText(Transform t)
        {
            ThingMeta m = MetaOf(t, false);
            if (m == null || m.IsDefault) return "";
            string text = "";
            if (m.Group != null) text += $" · group \"{Plain(GroupName(m.Group))}\"";
            if (m.LightOff) text += " · light off";
            if (m.Open) text += " · open";
            if (m.Wander) text += " · wanders";
            if (m.Still) text += " · stopped";
            return text;
        }

        private static void HideTip()
        {
            if (tipUi != null) tipUi.SetActive(false);
            if (!decorOn && hovered != null) { hovered = null; ItemOutline.Clear(); }
        }

        // ---- giving things to an area by its icon ----------------------------------------------------------------

        private static readonly List<UnityEngine.EventSystems.RaycastResult> uiHits = new List<UnityEngine.EventSystems.RaycastResult>();
        private static UnityEngine.EventSystems.PointerEventData pointer;
        private static UnityEngine.EventSystems.EventSystem pointerSystem;

        // The area whose overview icon is under the mouse (looked for under decor mode's screen too); not another mod's
        // icon that copies an area's (the kennel's is the Rest Space's).
        private static HideoutArea IconUnderMouse()
        {
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (events == null) return null;
            // The icons move only with the camera: nothing new to find while neither it nor the mouse has moved.
            Camera camera = ViewCamera();
            Vector2 mouse = Input.mousePosition;
            if (camera != null && mouse == iconMouse && camera.transform.position == iconEye && camera.transform.rotation == iconTurn && Time.frameCount - iconLook < 15)
                return iconArea;
            if (camera != null) { iconMouse = mouse; iconEye = camera.transform.position; iconTurn = camera.transform.rotation; iconLook = Time.frameCount; }
            iconArea = FindIcon(events);
            return iconArea;
        }

        private static Vector2 iconMouse;
        private static Vector3 iconEye;
        private static Quaternion iconTurn;
        private static int iconLook;
        private static HideoutArea iconArea;

        private static HideoutArea FindIcon(UnityEngine.EventSystems.EventSystem events)
        {
            uiHits.Clear();
            if (pointer == null || pointerSystem != events) { pointer = new UnityEngine.EventSystems.PointerEventData(events); pointerSystem = events; }
            pointer.position = Input.mousePosition;
            events.RaycastAll(pointer, uiHits);
            // An open area panel under the mouse: what's behind it isn't picked.
            if (uiHits.Any(h => h.gameObject != null && h.gameObject.GetComponentInParent<AreaScreenSubstrate>() != null)) return null;
            foreach (var hit in uiHits)
            {
                if (hit.gameObject == null) continue;
                AreaIcon icon = hit.gameObject.GetComponentInParent<AreaIcon>();
                // Only what's on top counts: an icon behind an open area panel (under an item you right-click in its
                // requirements) isn't picked. The first small piece of UI that isn't an icon stops the look further down.
                if (icon == null && (ui == null || !hit.gameObject.transform.IsChildOf(ui.transform)) && !Huge(hit.gameObject)) break;
                if (icon == null || icon.Data == null) continue;
                var type = icon.Data.Template.Type;
                HideoutArea area = Areas().FirstOrDefault(a => a.Data != null && a.Data.Template.Type == type);
                // Another mod's icon copied from an area's (the K9 kennel's is the Rest Space's) stands somewhere else:
                // only the icon over its area's own icon point counts.
                if (area == null || !OverIconPoint(icon, area)) continue;
                return area;
            }
            return null;
        }

        private static bool OverIconPoint(AreaIcon icon, HideoutArea area)
        {
            Camera camera = ViewCamera();
            Transform point = area.AreaIconPoint;
            if (camera == null || point == null || !(icon.transform is RectTransform r)) return true;
            Canvas canvas = icon.GetComponentInParent<Canvas>();
            Camera ui = canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
            Vector2 at = RectTransformUtility.WorldToScreenPoint(ui, r.TransformPoint(r.rect.center));
            Vector3 v = camera.WorldToViewportPoint(point.position);
            if (v.z <= 0) return false;
            return Vector2.Distance(at, new Vector2(v.x * Screen.width, v.y * Screen.height)) < Screen.height * .12f;
        }

        private static readonly Vector3[] corners = new Vector3[4];

        // A screen-sized piece of UI (a backdrop that catches clicks), not a button.
        private static bool Huge(GameObject go)
        {
            if (!(go.transform is RectTransform r)) return false;
            r.GetWorldCorners(corners);
            Canvas canvas = go.GetComponentInParent<Canvas>();
            Camera cam = canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, corners[0]), b = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
            return Mathf.Abs(b.x - a.x) > Screen.width * .4f || Mathf.Abs(b.y - a.y) > Screen.height * .4f;
        }

        // The selection (or what's being carried) belongs to this area from now on, where it stands.
        private static void GiveTo(HideoutArea area, IEnumerable<Transform> those)
        {
            string owner = Key(area);
            if (owner == null) return;
            int n = 0;
            Checkpoint();
            foreach (Transform t in those.ToList())
            {
                if (t == null) continue;
                if (t.GetComponent<DecorTag>() != null) KeepDecor(t, owner);
                else if (selectionKeys.TryGetValue(t, out string key) && key != null) KeepItem(t, key, owner);
                else continue;
                n++;
            }
            if (n == 0) return;
            SaveItems(); SaveDecor();
            EFT.Communications.NotificationManager.DisplayMessageNotification(n == 1 ? $"{Pretty(those.First().name)} belongs to {Name(area)} now." : $"{n} things belong to {Name(area)} now.");
            Outlines(); Status();
        }
    }
}
