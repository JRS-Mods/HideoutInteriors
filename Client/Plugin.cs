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
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "jrs.hideoutinteriors";
        public const string Version = "0.2.0";
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
            Logger.LogInfo($"Hideout Interiors {Version} loaded.");
        }

        private void Update() => Interiors.Tick();

        private void OnDestroy() => harmony?.UnpatchSelf();
    }
}
