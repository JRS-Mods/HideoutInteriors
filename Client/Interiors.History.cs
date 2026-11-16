using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// Undo and redo (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z). Everything decor mode keeps is two lists (the hideout's own
    /// things that were moved, and the decorations), so a step back is those lists as they were before the change, put
    /// into the hideout again: only what differs is touched. The same is how a saved layout is loaded.
    /// </summary>
    internal static partial class Interiors
    {
        private static readonly List<string> undoStack = new List<string>(), redoStack = new List<string>();
        private const int MostUndo = 60;

        // Everything kept, as text.
        private static string StateNow()
        {
            TidyMeta();
            return JsonConvert.SerializeObject(new Snapshot { Items = itemLayout, Decor = decorLayout });
        }

        // Nothing set on a thing any more: nothing kept for it.
        private static void TidyMeta()
        {
            foreach (ItemPlacement p in itemLayout.Values)
                if (p.Meta != null && p.Meta.IsDefault) p.Meta = null;
        }

        /// <summary>Called before anything kept changes: how it is now is what Ctrl+Z goes back to.</summary>
        private static void Checkpoint()
        {
            if (raidCopy || style == null) return;
            string now = StateNow();
            if (undoStack.Count > 0 && undoStack[undoStack.Count - 1] == now) return;
            undoStack.Add(now);
            if (undoStack.Count > MostUndo) undoStack.RemoveAt(0);
            redoStack.Clear();
        }

        private static void ForgetHistory() { undoStack.Clear(); redoStack.Clear(); }

        private static void Undo()
        {
            if (dragging || axisDrag >= 0) return;
            string now = StateNow();
            while (undoStack.Count > 0 && undoStack[undoStack.Count - 1] == now) undoStack.RemoveAt(undoStack.Count - 1);
            if (undoStack.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Nothing to undo."); return; }
            string to = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            redoStack.Add(now);
            ApplyState(to);
            EFT.Communications.NotificationManager.DisplayMessageNotification("Undone (Ctrl+Y: redo).");
        }

        private static void Redo()
        {
            if (dragging || axisDrag >= 0) return;
            if (redoStack.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Nothing to redo."); return; }
            string to = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add(StateNow());
            ApplyState(to);
            EFT.Communications.NotificationManager.DisplayMessageNotification("Redone.");
        }

        private static bool ValidItem(ItemPlacement p) => p?.Position?.Length == 3 && p.Rotation?.Length == 4;
        private static bool ValidDecor(DecorRecord r) => r?.Source != null && r.Position?.Length == 3 && r.Rotation?.Length == 4;
        private static bool SameJson(object a, object b) => JsonConvert.SerializeObject(a) == JsonConvert.SerializeObject(b);

        /// <summary>The hideout made to match a kept state (an undo step, a saved layout): true if it could be read.</summary>
        private static bool ApplyState(string json)
        {
            Snapshot target;
            try { target = JsonConvert.DeserializeObject<Snapshot>(json) ?? new Snapshot(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Decor: that layout couldn't be read: " + e.Message);
                EFT.Communications.NotificationManager.DisplayWarningNotification("That layout couldn't be read.");
                return false;
            }
            if (dragging) CancelDrag();
            StopUsing();
            ClearSelection(); hovered = null;
            var items = target.Items ?? new Dictionary<string, ItemPlacement>();
            var decor = target.Decor ?? new Dictionary<string, DecorRecord>();

            // The hideout's own things: the ones not in it go back as the game has them, the rest where it says.
            foreach (string key in itemLayout.Keys.ToList())
            {
                if (items.TryGetValue(key, out ItemPlacement kept) && ValidItem(kept)) continue;
                Transform t = Resolve(key);
                if (t != null) RestoreItem(t, key);
                else itemLayout.Remove(key);
            }
            foreach (var entry in items)
            {
                ItemPlacement p = entry.Value;
                if (!ValidItem(p)) continue;
                if (p.Owner == null) p.Owner = p.WithArea ? AreaTypeOf(entry.Key) : "";
                if (itemLayout.TryGetValue(entry.Key, out ItemPlacement now) && SameJson(now, p)) continue;
                itemLayout[entry.Key] = p;
                Transform t = Resolve(entry.Key);
                if (t == null) continue;
                // Sized before and not in this state: the game's size again.
                if (p.Scale == null && itemSizes.ContainsKey(t)) { SetSize(t, Vector3.one); itemSizes.Remove(t); }
                ApplyItem(t, entry.Key, p);
            }

            // The decorations: the ones not in it go, the ones that differ are put where it says, new ones are made.
            foreach (string id in decorLayout.Keys.ToList())
                if (!decor.TryGetValue(id, out DecorRecord kept) || !ValidDecor(kept)) RemoveDecor(id);
            foreach (var entry in decor)
            {
                DecorRecord r = entry.Value;
                if (!ValidDecor(r)) continue;
                bool there = decorObjects.TryGetValue(entry.Key, out Transform copy) && copy != null;
                if (there && decorLayout.TryGetValue(entry.Key, out DecorRecord now) && SameJson(now, r)) continue;
                decorLayout[entry.Key] = r;
                if (!there) continue;
                DecorTag tag = copy.GetComponent<DecorTag>();
                if (tag == null || tag.Source != r.Source || !PlaceDecor(entry.Key, r, copy))
                {
                    // Not the same thing any more (or its area's gone): made again by SpawnDecor.
                    thingList.Remove(copy);
                    UnityEngine.Object.Destroy(copy.gameObject);
                    decorObjects.Remove(entry.Key);
                }
            }
            SpawnDecor();
            ThingsChanged();
            SaveItems(); SaveDecor();
            Outlines(); Status(); RefreshHiddenTab(); RefreshRows();
            return true;
        }

        // A decoration gone from the hideout and from what's kept.
        private static void RemoveDecor(string id)
        {
            if (decorObjects.TryGetValue(id, out Transform t) && t != null)
            {
                Unselect(t);
                thingList.Remove(t);
                UnityEngine.Object.Destroy(t.gameObject);
            }
            decorObjects.Remove(id);
            decorLayout.Remove(id);
        }

        // What a poster or screen shows, to tell whether it has to be set up again.
        private static string MediaShown(DecorTag tag) => (tag.Media ?? "") + "|" + (tag.Playlist != null ? string.Join("|", tag.Playlist) : "") + "|" + tag.Frame;

        /// <summary>
        /// A decoration put where its record says, as its record says: under its owner, its size, what it shows, its
        /// lights. False if its owner isn't in the hideout (yet).
        /// </summary>
        private static bool PlaceDecor(string id, DecorRecord r, Transform copy)
        {
            Transform parent = ParentFor(copy, null, r.Owner, out Transform ownerRoot);
            if (parent == null) return false;
            if (copy.parent != parent) copy.SetParent(parent, true);
            DecorTag tag = copy.GetComponent<DecorTag>();
            bool known = mediaStates.TryGetValue(copy, out MediaState state);
            string before = known ? MediaShown(tag) : null;
            tag.Id = id;
            r.Give(tag);
            ApplyScale(copy);
            if (IsMediaKey(tag.Source))
            {
                if (!known || before != MediaShown(tag)) SetupMedia(copy);
                else
                {
                    Relayout(state);
                    if (state.Audio != null) { state.Audio.volume = Loudness(state); state.Audio.mute = tag.Muted || !audible; }
                }
            }
            var position = new Vector3(r.Position[0], r.Position[1], r.Position[2]);
            var rotation = new Quaternion(r.Rotation[0], r.Rotation[1], r.Rotation[2], r.Rotation[3]);
            if (ownerRoot != null) copy.SetPositionAndRotation(ownerRoot.TransformPoint(position), ownerRoot.rotation * rotation);
            else copy.SetPositionAndRotation(position, rotation);
            ApplyMeta(copy, tag.Meta, true);
            Moved();
            return true;
        }
    }
}
