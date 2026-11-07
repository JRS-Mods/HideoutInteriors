using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using HarmonyLib;
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
                if (visited) { visited = false; HideTip(); ForgetItems(); areaList = null; owner = null; rear = null; profileId = null; }
                if (!Decorating.Value || (style = FindStyle()) == null) return;
                if (!LoadProfile()) { style = null; return; }   // no profile yet: next time
                visited = true;
                settleUntil = Time.unscaledTime + 20;
            }
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

        private static string Key(HideoutArea area) => area.Data?.Template?.Type.ToString();

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
