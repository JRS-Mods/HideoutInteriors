using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>What the co-op link (HideoutInteriors.Fika.dll, loaded only where Fika is) is asked each frame.</summary>
    internal interface IShareLink
    {
        void Tick();
        /// <summary>In a co-op raid as a player, and the host hasn't said yet whether it has the mod (and what its hideout has).</summary>
        bool WaitingForHost { get; }
    }

    /// <summary>
    /// Your layout beyond your own hideout. In a raid whose map is a copy of the hideout, the arrangement is put in
    /// without the raid's mod having to ask (it still can, and what it asks for wins). With Fika, the players of a
    /// raid send one another their layouts: each lands in the others' Layouts tab ("From <name>") to look at or load in
    /// their own hideout, and the host's is the one a hideout-copy raid shows to everyone. A layout whose owner has
    /// sharing switched off is never kept.
    /// </summary>
    internal static partial class Interiors
    {
        internal static ConfigEntry<bool> ShowInRaids, ShareLayouts;

        private const string FikaId = "com.fika.core", LinkFile = "HideoutInteriors.Fika.dll", LinkType = "HideoutInteriors.Fika.FikaLink";
        private static IShareLink link;

        // The raid being looked at for a hideout, how often it's been looked at, and the host's layout for it.
        private static GameWorld raidWorld;
        private static int raidLooks;
        private static float nextRaidLook;
        private static bool raidCopyOurs;
        internal static string HostLayout;

        internal static void BindShare(ConfigFile config)
        {
            ShowInRaids = config.Bind("Sharing", "Show my layout in hideout raids", true,
                "In a raid whose map is a copy of the hideout (another mod's), the things you moved, hid and put in are there too, without that mod having to ask for them. In co-op with Fika everyone sees the host's.");
            ShareLayouts = config.Bind("Sharing", "Share layouts with co-op teammates", true,
                "With Fika: in a raid, your layout is sent to the other players and theirs to you. Each is kept in the Layouts tab as \"From <name>\", to load in your own hideout.");
        }

        // ---- the co-op link -----------------------------------------------------------------------------------

        internal static void LoadLink()
        {
            if (!Chainloader.PluginInfos.ContainsKey(FikaId)) return;
            try
            {
                string path = Path.Combine(Plugin.Folder, LinkFile);
                if (!File.Exists(path)) { Plugin.Log.LogInfo($"Fika is installed but {LinkFile} isn't: layouts aren't shared with the other players."); return; }
                AppDomain.CurrentDomain.AssemblyResolve += AlreadyLoaded;
                link = (IShareLink)Activator.CreateInstance(Assembly.LoadFrom(path).GetType(LinkType, true), true);
                Plugin.Log.LogInfo("Fika found: layouts are shared with the other players in a raid.");
            }
            catch (Exception e) { Plugin.Log.LogError("The Fika link couldn't be started: " + e); link = null; }
        }

        private static Assembly AlreadyLoaded(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name) return a;
            return null;
        }

        /// <summary>Every frame, wherever the game is (the hideout's own Tick stops in a raid).</summary>
        internal static void ShareTick()
        {
            if (link == null) return;
            try { link.Tick(); }
            catch (Exception e) { Plugin.Log.LogWarning("Layout sharing: " + e.Message); link = null; }
        }

        /// <summary>The playing profile's name, for the layout the others get.</summary>
        internal static string PlayerName()
        {
            try
            {
                if (TarkovApplication.Exist(out TarkovApplication app)) return app.Session?.Profile?.Nickname ?? "";
            }
            catch (Exception) { }
            return "";
        }

        /// <summary>The co-op link is gone (the raid's over): the next raid's host says what its hideout has.</summary>
        internal static void LinkClosed() => HostLayout = null;

        /// <summary>Another player's layout came: kept as a layout to load, and the host's shown in a hideout raid.</summary>
        internal static void ReceivedLayout(string from, string json, bool fromHost, bool keep)
        {
            if (string.IsNullOrEmpty(json)) return;
            if (fromHost)
            {
                HostLayout = json;
                // A copy put in before the host's came was this player's own: now it's the host's, as everyone's is.
                if (raidCopy && raidCopyOurs) { BuildRaidCopy(json); raidCopyOurs = true; }
            }
            if (!keep || !ShareLayouts.Value || !LooksLikeLayout(json)) return;
            try
            {
                Snapshot layout = Newtonsoft.Json.JsonConvert.DeserializeObject<Snapshot>(json);
                if (layout == null || ((layout.Items?.Count ?? 0) == 0 && (layout.Decor?.Count ?? 0) == 0)) return;
                string name = "From " + SafeName(string.IsNullOrWhiteSpace(from) ? "a teammate" : from);
                File.WriteAllText(Path.Combine(KeptFolder("layouts"), name + ".json"), json);
                Plugin.Log.LogInfo($"Layout sharing: {name}'s layout is in the Layouts tab ({layout.Items?.Count ?? 0} thing(s), {layout.Decor?.Count ?? 0} decoration(s)).");
                EFT.Communications.NotificationManager.DisplayMessageNotification($"{(string.IsNullOrWhiteSpace(from) ? "A teammate" : from)}'s hideout layout is in your Layouts tab.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Layout sharing: a layout couldn't be kept: " + e.Message); }
        }

        // ---- a raid on a copy of the hideout -------------------------------------------------------------------

        // In a raid: a few looks, in its first half minute, for a hideout loaded as its map. Found, the layout goes in
        // (the host's if it came, else this player's). A mod that asks for it itself, before or after, has its way.
        private static void RaidLook()
        {
            if (!ShowInRaids.Value || !Decorating.Value) return;
            if (!Singleton<GameWorld>.Instantiated) { raidWorld = null; return; }
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || world is HideoutGameWorld) { raidWorld = null; return; }
            if (world != raidWorld) { raidWorld = world; raidLooks = 0; nextRaidLook = Time.unscaledTime + 6; }
            if (raidLooks >= 4 || Time.unscaledTime < nextRaidLook) return;
            // In co-op everyone has the host's: a player waits for it (until the host has had its chance to answer)
            // before putting in their own.
            if (HostLayout == null && link != null && link.WaitingForHost) return;
            raidLooks++;
            nextRaidLook = Time.unscaledTime + 7;
            GameObject found = GameObject.Find("STYLE");
            if (found == null) return;
            // A hideout: its scene has the hideout's areas.
            if (!found.scene.GetRootGameObjects().Any(r => r.GetComponentInChildren<HideoutArea>(true) != null)) { raidLooks = 99; return; }
            raidLooks = 99;
            Plugin.Log.LogInfo("This raid's map is a hideout: putting the layout into it.");
            BuildRaidCopy(HostLayout ?? RaidSnapshot());
            raidCopyOurs = true;
        }
    }
}
