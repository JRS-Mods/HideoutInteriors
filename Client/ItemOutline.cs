using System;
using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;

namespace HideoutInteriors
{
    /// <summary>
    /// The outline round things pointed at or selected: the game's own (<see cref="HighLightMesh"/> on the main camera,
    /// the white line round an area you point at on the overview). A copy of it with the same material draws nothing
    /// here (checked in game), so this lends the game's one our things: it outlines whatever its target holds, so it's
    /// given an empty target of ours and our things' meshes directly. While the game outlines an area of its own
    /// (an icon pointed at), it's left alone, and our things come back after.
    /// </summary>
    internal static class ItemOutline
    {
        private static readonly AccessTools.FieldRef<HighLightMesh, HighLightMesh.Part[]> parts = AccessTools.FieldRefAccess<HighLightMesh, HighLightMesh.Part[]>("_targetParts");
        private static readonly AccessTools.FieldRef<HighLightMesh, Transform> lastTarget = AccessTools.FieldRefAccess<HighLightMesh, Transform>("_lastTarget");

        private static Transform holder;
        private static HighLightMesh.Part[] ours = new HighLightMesh.Part[0];
        private static Color color = Color.white;
        private static readonly List<Transform> things = new List<Transform>();
        private static float gameWidth = -1;
        private const float Width = 3f;

        /// <summary>Outlines these things in this colour (nothing: lets the game's outline go).</summary>
        public static void Set(IList<Transform> those, Color tint)
        {
            bool same = tint == color && those.Count == things.Count;
            for (int i = 0; same && i < those.Count; i++) same = those[i] == things[i];
            if (same) return;
            color = tint;
            things.Clear(); things.AddRange(those);
            var list = new List<HighLightMesh.Part>();
            foreach (Transform t in things)
            {
                if (t == null) continue;
                foreach (Renderer r in t.GetComponentsInChildren<Renderer>(false))
                {
                    if (!r.enabled) continue;
                    if (r is SkinnedMeshRenderer smr) list.Add(new HighLightMesh.Part(null, smr.transform, smr));
                    else if (r is MeshRenderer && r.shadowCastingMode != ShadowCastingMode.ShadowsOnly && r.TryGetComponent(out MeshFilter f) && f.sharedMesh != null)
                        list.Add(new HighLightMesh.Part(f.sharedMesh, r.transform));
                }
            }
            ours = list.ToArray();
            Apply();
        }

        /// <summary>Every frame: our things are outlined unless the game is outlining one of its areas.</summary>
        public static void Apply()
        {
            HighLightMesh line = null;
            try { line = EFT.CameraControl.CameraManager.Instance?.MeshHighlighter; } catch (Exception) { }
            if (line == null) return;
            bool mine = holder != null && line.Target == holder;
            if (ours.Length == 0)
            {
                if (mine) Release(line);
                return;
            }
            // The game's own (an area pointed at): it goes first.
            if (!mine && line.enabled && line.Target != null) return;
            if (holder == null) holder = new GameObject("Hideout extensions outline target").transform;
            if (!mine && gameWidth < 0) gameWidth = line.LineWidth;
            line.Color = color;
            line.LineWidth = Width;
            line.Target = holder;
            lastTarget(line) = holder;   // so it doesn't look for renderers under our empty target
            parts(line) = ours;
            if (!line.enabled) line.enabled = true;
        }

        private static void Release(HighLightMesh line)
        {
            line.Target = null;
            parts(line) = null;
            line.enabled = false;
            if (gameWidth >= 0) { line.LineWidth = gameWidth; gameWidth = -1; }
        }

        /// <summary>No more outlines from us (decor mode closed, the hideout left).</summary>
        public static void Clear()
        {
            things.Clear();
            ours = new HighLightMesh.Part[0];
            Apply();
        }
    }

    /// <summary>Marks a decoration (a copy of one of the hideout's things put in from decor mode) with its id.</summary>
    internal sealed class DecorTag : MonoBehaviour
    {
        public string Id;
        public string Source;
        // How big against the thing it copies (1 = as the game has it), and that thing's size in the world.
        public float Scale = 1;
        public Vector3 BaseScale = Vector3.one;
        // Stretched along its own X, Y and Z on top of that (1, 1, 1: its own shape).
        public Vector3 Stretch = Vector3.one;
        // Posters and TV screens: the picture or video (a link, or a file in the media folder), the face's size in
        // metres (a height of 0 follows the picture's shape), whether its sound is off and how loud it is (of the
        // settings' video volume).
        public string Media;
        public float Width, Height;
        public bool Muted;
        public float Volume = 1;
        // A list of pictures or videos it goes through in order (and how long each picture stays), and its frame's style.
        public string[] Playlist;
        public float Interval;
        public int Frame;
        // Its group, name, lock, light and what it does (see ThingMeta).
        public ThingMeta Meta = new ThingMeta();
    }

    /// <summary>
    /// What a thing has on top of where it stands: the group it's locked into, a name of your own, whether it's locked
    /// against picking, how its lights are set, and whether it's open, alive (its animations play), wandering, or (one
    /// of the hideout's own) stopped. Kept with the thing; left out of the file while it's all as it comes.
    /// </summary>
    internal sealed class ThingMeta
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Group;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Label;
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Locked;
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool LightOff;
        // "#rrggbb", or left out for the lamp's own colour.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string LightColor;
        // How bright against its own (1).
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore), System.ComponentModel.DefaultValue(1f)]
        public float LightPower = 1;
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Open, Alive, Wander, Still;

        [JsonIgnore]
        public bool IsDefault => Group == null && Label == null && !Locked && !LightOff && LightColor == null && LightPower == 1f && !Open && !Alive && !Wander && !Still;

        public ThingMeta Clone() => (ThingMeta)MemberwiseClone();
    }
}
