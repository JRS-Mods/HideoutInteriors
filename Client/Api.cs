namespace HideoutInteriors
{
    /// <summary>
    /// For a mod that loads the hideout's scene as a raid map: the playing profile's arrangement of the
    /// hideout, as text to keep or send to the other players in the raid, and that put into the raid's copy of the hideout.
    /// Reference HideoutInteriors.dll, or find these by name, and call them only where the mod is installed.
    /// </summary>
    public static class HideoutInteriorsApi
    {
        /// <summary>The things the playing profile has moved, given away or hidden, and its decorations. Call it in the raid, as the map loads.</summary>
        public static string RaidCopySnapshot() => Interiors.RaidSnapshot();

        /// <summary>
        /// Puts a <see cref="RaidCopySnapshot"/> (this player's, or the host's in co-op) into the hideout scene that's loaded as
        /// the raid's map, after its areas are where they'll stand. It all goes with the raid; the hideout's own files are left alone.
        /// </summary>
        public static void BuildRaidCopy(string snapshot) => Interiors.BuildRaidCopy(snapshot);

        /// <summary>
        /// Adds a thing of your mod's to decor mode's panel, under <paramref name="kind"/>: one of the panel's tabs (Storage,
        /// Furniture, Lights, Electronics, Food, Tools, Junk or Other), or a name of your own, which becomes a tab of its own
        /// after those. <paramref name="make"/> builds one, which the copies are taken from: its look and lights are kept, and
        /// the copies get one box collider round them on its layer. Placed ones are kept by <paramref name="key"/>, so call
        /// this every game, at startup.
        /// </summary>
        public static void AddDecor(string key, string name, string kind, System.Func<UnityEngine.GameObject> make) => Interiors.AddExtra(key, name, kind, make);

        /// <summary>
        /// As <see cref="AddDecor(string, string, string, System.Func{UnityEngine.GameObject})"/>, but a hideout can only have
        /// <paramref name="limit"/> of it: the panel shows how many are in, and won't put in more (0: no limit).
        /// </summary>
        public static void AddDecor(string key, string name, string kind, System.Func<UnityEngine.GameObject> make, int limit) => Interiors.AddExtra(key, name, kind, make, limit);

        /// <summary>The ones of yours (by the key you added it with) placed in the hideout or the raid's copy of it right now.</summary>
        public static System.Collections.Generic.List<UnityEngine.Transform> DecorOf(string key) => Interiors.PlacedExtras(key);
    }
}
