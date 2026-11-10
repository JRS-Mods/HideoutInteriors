using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// Things other mods add to decor mode's panel, beside what the hideout's scene has: each made by the mod, once, as a
    /// pattern the copies are taken from. They're kept with the rest of the decorations under a key of their own ("mod:" and
    /// the mod's name for it), so a decoration of one only comes back where that mod's installed and has added it again.
    /// </summary>
    internal static partial class Interiors
    {
        private const string ExtraPrefix = "mod:";

        private sealed class Extra
        {
            public string Name, Kind;
            public Func<GameObject> Make;
            public Transform Pattern;
            // How many a hideout can have (0: as many as you like).
            public int Limit;
        }

        private static readonly Dictionary<string, Extra> extras = new Dictionary<string, Extra>();
        // Tabs of mods' own, after the panel's and before Hidden, in the order they were first asked for.
        private static readonly List<string> extraKinds = new List<string>();
        private static GameObject extraRoot;

        internal static void AddExtra(string key, string name, string kind, Func<GameObject> make, int limit = 0)
        {
            if (string.IsNullOrEmpty(key) || make == null) return;
            kind = string.IsNullOrWhiteSpace(kind) ? "Other" : kind.Trim();
            if (kind == "All" || kind == "Hidden") kind = "Other";
            // The panel's built each time it opens, so a new tab is there from the next time.
            if (!Kinds.Contains(kind) && !extraKinds.Contains(kind)) extraKinds.Add(kind);
            extras[ExtraPrefix + key] = new Extra { Name = string.IsNullOrEmpty(name) ? key : name, Kind = kind, Make = make, Limit = Mathf.Max(0, limit) };
            catalog = null;
            Plugin.Log.LogInfo($"Decor: '{name}' added by another mod, under {kind}{(limit > 0 ? $", at most {limit} in a hideout" : "")}.");
        }

        /// <summary>Every tab: the panel's own, then mods', then Hidden.</summary>
        private static List<string> TabKinds()
        {
            var tabs = Kinds.Where(k => k != "Hidden").ToList();
            tabs.AddRange(extraKinds);
            tabs.Add("Hidden");
            return tabs;
        }

        private static bool IsExtra(string key) => key != null && key.StartsWith(ExtraPrefix, StringComparison.Ordinal);

        // How many of a mod's thing this hideout has, and how many it can have (0: no limit).
        private static int ExtraCount(string key) => decorLayout.Values.Count(r => r.Source == key);
        private static int ExtraLimit(string key) => key != null && extras.TryGetValue(key, out Extra extra) ? extra.Limit : 0;

        // Its name on the panel, and with a limit, how many are in.
        private static string ExtraLabel(Entry e)
        {
            int limit = ExtraLimit(e.Key);
            if (limit <= 0) return e.Name;
            int have = ExtraCount(e.Key);
            string colour = have >= limit ? "#d0674f" : "#a8a49a";
            return $"{e.Name}\n<size=11><color={colour}>{have} of {limit}</color></size>";
        }

        /// <summary>True if another can go in; otherwise says why not.</summary>
        private static bool RoomForAnother(Entry e)
        {
            int limit = ExtraLimit(e.Key);
            if (limit <= 0 || ExtraCount(e.Key) < limit) return true;
            EFT.Communications.NotificationManager.DisplayWarningNotification($"Your hideout can only have {limit} of {e.Name}. Move the ones you have, or select one and press Delete.");
            return false;
        }

        // The panel's counts, after decorations come or go.
        private static void RefreshLimits()
        {
            foreach (var (_, e) in cells)
                if (e.Label != null && ExtraLimit(e.Key) > 0) e.Label.text = ExtraLabel(e);
        }

        // The pattern for one, made when it's first wanted and kept out of sight for the rest of the game.
        private static Transform ExtraPattern(string key)
        {
            if (!extras.TryGetValue(key, out Extra extra)) return null;
            if (extra.Pattern == null)
            {
                if (extraRoot == null)
                {
                    extraRoot = new GameObject("Hideout Interiors mod decor");
                    UnityEngine.Object.DontDestroyOnLoad(extraRoot);
                    extraRoot.transform.position = new Vector3(0f, -1000f, 0f);
                }
                GameObject made = null;
                try { made = extra.Make(); }
                catch (Exception e) { Plugin.Log.LogWarning($"Decor: '{extra.Name}' couldn't be made: {e.Message}"); }
                if (made == null) return null;
                made.transform.SetParent(extraRoot.transform, false);
                // The copies are seen; the pattern isn't.
                foreach (Renderer r in made.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                foreach (Collider c in made.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                extra.Pattern = made.transform;
            }
            resolved[key] = extra.Pattern;
            keyOf[extra.Pattern] = key;
            return extra.Pattern;
        }

        private static string ExtraKeyOf(Transform thing)
        {
            foreach (var pair in extras)
                if (pair.Value.Pattern == thing) return pair.Key;
            return null;
        }

        private static IEnumerable<Entry> ExtraEntries()
        {
            foreach (var pair in extras.ToList())
            {
                Transform pattern = ExtraPattern(pair.Key);
                if (pattern != null) yield return new Entry { Key = pair.Key, Name = pair.Value.Name, Kind = pair.Value.Kind, Source = pattern };
            }
        }

        /// <summary>The decorations of a mod's thing that are in the scene now.</summary>
        internal static List<Transform> PlacedExtras(string key)
        {
            string full = ExtraPrefix + key;
            return decorObjects.Where(d => d.Value != null && decorLayout.TryGetValue(d.Key, out DecorRecord r) && r.Source == full).Select(d => d.Value).ToList();
        }
    }
}
