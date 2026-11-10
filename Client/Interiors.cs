using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// What decor mode stands on: the hideout found (its STYLE object, the root of its walls and floors), the profile
    /// that's playing (each keeps its own things and decorations), the overview on screen, and the hideout's areas. When
    /// the hideout loads, the things kept are put back as the areas set themselves up.
    /// </summary>
    internal static partial class Interiors
    {
        internal static ConfigEntry<string> Fixed;

        private static GameObject style;
        private static float nextStyleLook, settleUntil;
        private static string profileId;
        private static List<HideoutArea> areaList;
        private static HideoutPlayerOwner owner;
        private static float nextOwner;
        private static HideoutScreenRear rear;
        private static float nextRear;
        private static bool visited;
        // The hideout here is another mod's raid map, set up once from a snapshot: its areas never get
        // their data, and nothing of it is kept.
        private static bool raidCopy;

        // The extension rooms of Hideout Extensions, if it's installed: their see-through walls hide nothing.
        private const string ExtensionRooms = "Hideout extensions";

        internal static void Bind(ConfigFile config)
        {
            BindDecor(config);
            Fixed = config.Bind("General", "Areas things never join", "EmergencyWall",
                "Areas (by type, comma-separated) that what you put down never joins on its own. The emergency wall's zone holds a whole room.");
        }

        internal static void Patch(Harmony harmony) => PatchDecor(harmony);

        internal static void Tick()
        {
            if (style == null)
            {
                if (visited) { visited = false; raidCopy = false; HideTip(); ForgetItems(); areaList = null; owner = null; rear = null; profileId = null; }
                if (!Decorating.Value || (style = FindStyle()) == null) return;
                if (!LoadProfile()) { style = null; return; }   // no profile yet: next time
                visited = true;
                settleUntil = Time.unscaledTime + 20;
            }
            // A raid's copy was set up once and there's nothing to do in it: no overview, no decor mode. Looking for the
            // overview here searched the whole raid scene every second, and that showed as a stall.
            if (raidCopy) return;
            try
            {
                // The areas set themselves up a moment after the hideout loads: things kept go back as they do.
                if (Time.unscaledTime < settleUntil && Time.frameCount % 30 == 0) PlaceAll();
                ItemOutline.Apply();
                if (decorOn) DecorTick();
                else HideTip();
                if (Overview()) DecorButton();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Decor mode: " + e);
                CloseDecor();
            }
        }

        // The hideout's STYLE object, looked for every couple of seconds (never in a raid).
        private static GameObject FindStyle()
        {
            if (Time.unscaledTime < nextStyleLook) return null;
            nextStyleLook = Time.unscaledTime + 2;
            if (Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld)) return null;
            if (!Singleton<HideoutRepresentation>.Instantiated) return null;
            return GameObject.Find("STYLE");
        }

        private static bool LoadProfile()
        {
            string id = null;
            try { if (TarkovApplication.Exist(out TarkovApplication app)) id = app.Session?.Profile?.Id; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(id)) return false;
            if (id == profileId) return true;
            profileId = id;
            LoadItems(id);
            return true;
        }

        // Where a profile's things or decorations are kept. A file of Hideout Extensions 1.5's (where decor mode
        // started) is taken over the first time.
        private static string KeptIn(string kind, string id)
        {
            string path = Path.Combine(BepInEx.Paths.ConfigPath, $"jrs.hideoutinteriors.{kind}.{id}.json");
            string old = Path.Combine(BepInEx.Paths.ConfigPath, $"jrs.hideoutextensions.{kind}.{id}.json");
            try
            {
                if (!File.Exists(path) && File.Exists(old))
                {
                    File.Copy(old, path);
                    Plugin.Log.LogInfo($"Took {Path.GetFileName(old)} over from Hideout Extensions.");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't take {old} over: {e.Message}"); }
            return path;
        }

        private static void PlaceAll()
        {
            foreach (HideoutArea area in Areas()) PlaceItems(area);
            PlaceSceneItems();
            try { SpawnDecor(); } catch (Exception e) { Plugin.Log.LogWarning("Decorations: " + e.Message); }
        }

        // The hideout's areas, searched for once a visit (again only if the hideout replaced one).
        private static List<HideoutArea> Areas()
        {
            if (style == null) return new List<HideoutArea>();
            if (areaList == null || areaList.Any(a => a == null))
                areaList = style.scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<HideoutArea>(true)).ToList();
            return areaList;
        }

        private static string Key(HideoutArea area)
        {
            try { return area.Data?.Template?.Type.ToString() ?? (raidCopy ? area.AreaTemplate?.Type.ToString() : null); }
            catch (Exception) { return null; }
        }

        // ---- the hideout as another mod's raid map -----------------------------------------------------------

        private sealed class Snapshot
        {
            public Dictionary<string, ItemPlacement> Items = new Dictionary<string, ItemPlacement>();
            public Dictionary<string, DecorRecord> Decor = new Dictionary<string, DecorRecord>();
        }

        /// <summary>The playing profile's moved and hidden things and its decorations, read fresh from its files.</summary>
        internal static string RaidSnapshot()
        {
            var snapshot = new Snapshot();
            try
            {
                string id = null;
                if (TarkovApplication.Exist(out TarkovApplication app)) id = app.Session?.Profile?.Id;
                if (!string.IsNullOrEmpty(id) && Decorating.Value)
                {
                    string items = KeptIn("items", id), decor = KeptIn("decor", id);
                    if (File.Exists(items)) snapshot.Items = JsonConvert.DeserializeObject<Dictionary<string, ItemPlacement>>(File.ReadAllText(items)) ?? snapshot.Items;
                    // As LoadItems reads them: 1.4.0 files kept a thing with its own area or in the hideout.
                    foreach (var entry in snapshot.Items)
                        if (entry.Value != null && entry.Value.Owner == null) entry.Value.Owner = entry.Value.WithArea ? AreaTypeOf(entry.Key) : "";
                    if (File.Exists(decor)) snapshot.Decor = JsonConvert.DeserializeObject<Dictionary<string, DecorRecord>>(File.ReadAllText(decor)) ?? snapshot.Decor;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("Couldn't read the hideout's things for a raid: " + e); }
            Plugin.Log.LogInfo($"Hideout for a raid: {snapshot.Items.Count} thing(s) moved or hidden, {snapshot.Decor.Count} decoration(s).");
            return JsonConvert.SerializeObject(snapshot);
        }

        /// <summary>
        /// A <see cref="RaidSnapshot"/> put into the hideout scene that's loaded as the raid's map: things where they were put
        /// (with their areas, wherever those stand now), hidden ones hidden, and the decorations. Once; it all goes with the raid.
        /// </summary>
        internal static void BuildRaidCopy(string json)
        {
            try
            {
                Snapshot snapshot = string.IsNullOrEmpty(json) ? new Snapshot() : JsonConvert.DeserializeObject<Snapshot>(json) ?? new Snapshot();
                GameObject found = GameObject.Find("STYLE");
                if (found == null) { Plugin.Log.LogWarning("No hideout in this raid's scene to put the things into."); return; }
                // Whatever the menus' hideout had is gone with it.
                if (visited) { HideTip(); ForgetItems(); }
                style = found;
                raidCopy = true;
                visited = true;
                areaList = null; owner = null; rear = null;
                // So the hideout's own files are read again when it's back.
                profileId = null;
                settleUntil = 0;
                itemLayout.Clear();
                foreach (var entry in snapshot.Items ?? new Dictionary<string, ItemPlacement>())
                    if (entry.Value?.Position?.Length == 3 && entry.Value.Rotation?.Length == 4) itemLayout[entry.Key] = entry.Value;
                decorLayout.Clear();
                foreach (var entry in snapshot.Decor ?? new Dictionary<string, DecorRecord>())
                    if (entry.Value?.Source != null && entry.Value.Position?.Length == 3 && entry.Value.Rotation?.Length == 4) decorLayout[entry.Key] = entry.Value;
                itemsFile = decorFile = null;
                PlaceAll();
                Physics.SyncTransforms();
                Plugin.Log.LogInfo($"The raid's copy of the hideout: {resolved.Count} thing(s) in place, {decorObjects.Count} of {decorLayout.Count} decoration(s) put in.");
            }
            catch (Exception e) { Plugin.Log.LogError("Couldn't put the hideout's things into the raid: " + e); }
        }

        private static string Name(HideoutArea area)
        {
            try { return area.Data.Template.Name; } catch (Exception) { return Key(area); }
        }

        // The hideout's overview is on screen: not walking about, and no menu in front (the hideout stays loaded behind
        // every menu, its player in overview mode).
        private static bool Overview()
        {
            if (owner == null && Time.unscaledTime >= nextOwner) { nextOwner = Time.unscaledTime + 1; owner = UnityEngine.Object.FindObjectOfType<HideoutPlayerOwner>(); }
            if (owner == null || owner.FirstPersonMode) return false;
            if (rear == null && Time.unscaledTime >= nextRear) { nextRear = Time.unscaledTime + 1; rear = UnityEngine.Object.FindObjectOfType<HideoutScreenRear>(true); }
            return rear != null && rear.isActiveAndEnabled;
        }

        // The game can draw the 3D view at a lower resolution than the window, so the mouse goes through the viewport.
        private static Ray MouseRay(Camera camera)
        {
            Vector3 mouse = Input.mousePosition;
            return camera.ViewportPointToRay(new Vector3(mouse.x / Screen.width, mouse.y / Screen.height, 0));
        }
    }
}
