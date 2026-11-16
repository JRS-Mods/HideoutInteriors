using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace HideoutInteriors
{
    /// <summary>
    /// Hideout Interiors: decor mode for the hideout's overview. Point at the hideout's things to outline them, select a few,
    /// move and turn them (arrows, rings or by dragging), give them to another area or to none, hide them, and put in copies
    /// of anything the hideout has. Works with or without Hideout Extensions.
    /// </summary>
    [BepInPlugin(Id, "JRS-HideoutInteriors", Version)]
    // Loaded after Fika where it's installed, so the co-op link finds it.
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "jrs.hideoutinteriors";
        public const string Version = "1.0.0";
        internal static ManualLogSource Log;
        internal static string Folder;
        private Harmony harmony;

        private void Awake()
        {
            Log = Logger;
            Folder = System.IO.Path.GetDirectoryName(Info.Location);
            Interiors.Bind(Config);
            harmony = new Harmony(Id);
            try { Interiors.Patch(harmony); }
            catch (Exception e) { Logger.LogWarning("Decor mode's keys couldn't be set up (Escape may leave the hideout): " + e.Message); }
            Interiors.LoadLink();
            Logger.LogInfo($"Hideout Interiors {Version} loaded.");
#if SELFTEST
            Interiors.SelfTestStart(this);
#endif
        }

        private void Update()
        {
#if SELFTEST
            Interiors.SelfTestTick();
#endif
            Interiors.Tick();
            Interiors.ShareTick();
        }

        // What a click or the use key would do to the poster, screen or computer pointed at.
        private void OnGUI() => Interiors.QuickGui();

        private void OnDestroy() => harmony?.UnpatchSelf();
    }
}
