#if SELFTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using HarmonyLib;
using Newtonsoft.Json;
using EFT.Interactive;
using JsonType;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// The mod trying itself in the real game with nobody at the keyboard. Only in a build made with -p:SelfTest=true,
    /// and only run when HIDEOUT_INTERIORS_TEST names a folder for its results: the game is muted, the hideout opened
    /// from the menu, every feature called the way its key or button calls it, checked, pictured, and the game shut.
    /// Never packed.
    /// </summary>
    internal static partial class Interiors
    {
        private static string testOut;
        private static readonly List<string> testLines = new List<string>();
        private static int testPass, testFail, testErrors;
        private static bool testRunning;

        internal static void SelfTestStart(MonoBehaviour host)
        {
            testOut = Environment.GetEnvironmentVariable("HIDEOUT_INTERIORS_TEST");
            if (string.IsNullOrEmpty(testOut)) return;
            Directory.CreateDirectory(testOut);
            Application.runInBackground = true;
            Application.logMessageReceived += (message, stack, type) =>
            {
                if (type != LogType.Exception && type != LogType.Error) return;
                if ((message + stack).IndexOf("HideoutInteriors", StringComparison.Ordinal) < 0) return;
                testErrors++;
                Note("ERROR " + message + " | " + (stack ?? "").Replace("\n", " / "));
            };
            testRunning = true;
            host.StartCoroutine(Guarded(TestRun()));
        }

        internal static void SelfTestTick()
        {
            if (testRunning) AudioListener.volume = 0f;
        }

        private static void Note(string line)
        {
            testLines.Add(line);
            Plugin.Log.LogInfo("[selftest] " + line);
            try { File.WriteAllLines(Path.Combine(testOut, "results.txt"), testLines); } catch (Exception) { }
        }

        private static void Check(string what, bool ok, string detail = "")
        {
            if (ok) testPass++; else testFail++;
            Note($"{(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? ": " + detail : "")}");
        }

        // A step that throws is a failure of its own, and the run goes on.
        private static void Step(string what, Action act)
        {
            try { act(); }
            catch (Exception e) { testFail++; Note($"FAIL {what} threw: {e}".Replace("\r", "").Replace("\n", " / ")); }
        }

        private static IEnumerator Guarded(IEnumerator inner)
        {
            while (true)
            {
                bool more;
                try { more = inner.MoveNext(); }
                catch (Exception e) { testFail++; Note("FAIL the run stopped: " + e.ToString().Replace("\r", "").Replace("\n", " / ")); break; }
                if (!more) break;
                yield return inner.Current;
            }
            Note($"DONE {testPass} passed, {testFail} failed, {testErrors} error(s) logged.");
            yield return new WaitForSecondsRealtime(2f);
            if (Environment.GetEnvironmentVariable("HIDEOUT_INTERIORS_TEST_STAY") == null) Application.Quit();
        }

        private static object Shot(string name)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(testOut, name + ".png"));
            return new WaitForSecondsRealtime(.6f);
        }

        private static WaitForSecondsRealtime Wait(float seconds) => new WaitForSecondsRealtime(seconds);

        private static void AnswerAsk(string text)
        {
            if (!asking) throw new InvalidOperationException("no box was asking for a name");
            Action<string> done = askDone;
            CancelAsk();
            done?.Invoke(text);
        }

        // A catalog thing put in the way the panel does, then put down at a place on the floor.
        private static Transform TestPlace(Entry e, Vector3 floor)
        {
            StartPlacing(e);
            if (!dragging || carrying.Count == 0) throw new InvalidOperationException($"{e.Name} wasn't picked up from the panel");
            Transform t = carrying[0].Thing;
            TestStand(t, floor);
            Commit(Belong.None);
            ThingsChanged();
            return t;
        }

        private static void TestStand(Transform t, Vector3 floor)
        {
            renderersOf.Remove(t);
            if (Bounds(t, out Bounds b)) t.position += floor - new Vector3(b.center.x, b.min.y, b.center.z);
            else t.position = floor;
        }

        private static void TestSelect(params Transform[] those)
        {
            ClearSelection();
            foreach (Transform t in those) Select(t);
            Outlines(); Status();
        }

        private static string V(Vector3 v) => $"({v.x:0.00}, {v.y:0.00}, {v.z:0.00})";

        private static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private static IEnumerator TestRun()
        {
            // ---- to the hideout --------------------------------------------------------------------------------
            float began = Time.realtimeSinceStartup;
            while (true)
            {
                var screens = EFT.UI.Screens.EftScreenManager.Instance;
                if (screens != null && screens.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.MainMenu)) break;
                if (Time.realtimeSinceStartup - began > 600) { Check("the main menu came up", false, "not after ten minutes"); yield break; }
                yield return Wait(1);
            }
            Note($"The main menu is up after {Time.realtimeSinceStartup - began:0} s.");
            Check("the Fika link loaded (Fika is installed)", link != null || !BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(FikaId));
            yield return Wait(6);
            TarkovApplication.Exist(out TarkovApplication app);
            AccessTools.Method(typeof(MainMenuShowOperation), "method_24").Invoke(Traverse.Create(app).Field<MainMenuShowOperation>("_menuOperation").Value, new object[] { null });
            float asked = Time.realtimeSinceStartup;
            while (!(Singleton<GameWorld>.Instance is HideoutGameWorld && EFT.UI.Screens.EftScreenManager.Instance.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.Hideout)))
            {
                if (Time.realtimeSinceStartup - asked > 400) { Check("the hideout opened", false, "not after 400 s"); yield break; }
                yield return Wait(1);
            }
            Note($"The hideout's screen is up after {Time.realtimeSinceStartup - asked:0} s.");
            yield return Wait(14);
            float waited = Time.realtimeSinceStartup;
            while ((style == null || !Overview()) && Time.realtimeSinceStartup - waited < 60) yield return Wait(1);
            Check("the mod found the hideout and its overview", style != null && Overview());
            if (style == null) yield break;
            yield return Wait(8);
            yield return Shot("00_overview");
            Check("the decor button is on the overview", decorButton != null && decorButton.gameObject.activeInHierarchy);
            Note($"Kept from before: {itemLayout.Count} thing(s) moved or hidden, {decorLayout.Count} decoration(s), {decorObjects.Count(d => d.Value != null)} put in.");

            // ---- the scene, for what's worth knowing --------------------------------------------------------------
            Step("scene survey", () =>
            {
                var lines = new List<string>();
                foreach (GameObject root in style.scene.GetRootGameObjects())
                {
                    lines.Add($"ROOT {root.name} active={root.activeSelf} children={root.transform.childCount}");
                    foreach (SkinnedMeshRenderer s in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) lines.Add("  SKIN " + PathOf(s.transform) + " bones=" + (s.bones?.Length ?? 0));
                    foreach (Animator a in root.GetComponentsInChildren<Animator>(true))
                        lines.Add("  ANIMATOR " + PathOf(a.transform) + " controller=" + (a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "none") + " clips=" + (a.runtimeAnimatorController != null ? a.runtimeAnimatorController.animationClips.Length : 0) + " enabled=" + a.enabled);
                    foreach (Animation a in root.GetComponentsInChildren<Animation>(true)) lines.Add("  ANIMATION " + PathOf(a.transform) + " clip=" + (a.clip != null ? a.clip.name : "none") + " count=" + a.GetClipCount());
                    foreach (ParticleSystem p in root.GetComponentsInChildren<ParticleSystem>(true)) lines.Add("  PARTICLES " + PathOf(p.transform));
                    foreach (Door d in root.GetComponentsInChildren<Door>(true)) lines.Add("  DOOR " + PathOf(d.transform));
                }
                // Every scene that's loaded, and what the menu keeps alive: where else a pet or a model might be.
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    lines.Add($"SCENE {scene.name} roots={scene.rootCount} loaded={scene.isLoaded}");
                }
                foreach (SkinnedMeshRenderer s in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
                    if (s.gameObject.scene != style.scene) lines.Add("  ELSEWHERE SKIN " + PathOf(s.transform) + " scene=" + s.gameObject.scene.name);
                File.WriteAllLines(Path.Combine(testOut, "scene.txt"), lines);
                Note($"Scene survey: {lines.Count(l => l.StartsWith("  SKIN"))} skinned, {lines.Count(l => l.StartsWith("  ANIMATOR"))} animators, {lines.Count(l => l.StartsWith("  ANIMATION"))} animations, {lines.Count(l => l.StartsWith("  DOOR"))} doors in the hideout's scene.");
            });

            // ---- decor mode and its panel ------------------------------------------------------------------------
            var watch = Stopwatch.StartNew();
            Step("open decor mode", OpenDecor);
            watch.Stop();
            Check("decor mode opens", decorOn && ui != null, $"{watch.ElapsedMilliseconds} ms to open (the catalog is built then)");
            if (!decorOn) yield break;
            Check("the panel has things to put in", catalog != null && catalog.Count > 50, $"{catalog?.Count} kinds");
            Step("catalog dump", () =>
            {
                File.WriteAllLines(Path.Combine(testOut, "catalog.txt"), catalog.Select(e => $"{e.Kind}\t{e.Name}\t{e.Key}"));
                Note("Kinds: " + string.Join(", ", catalog.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
            });
            Check("Rooms & walls has things", catalog.Any(e => e.Kind == RoomKind), $"{catalog.Count(e => e.Kind == RoomKind)}");
            yield return Wait(1.5f);
            yield return Shot("01_decor_all");
            foreach (string kind in TabKinds())
            {
                var took = Stopwatch.StartNew();
                bool threw = false;
                try { tab = kind; FillTab(); }
                catch (Exception e) { threw = true; testFail++; Note($"FAIL tab {kind} threw: {e.Message}"); }
                took.Stop();
                if (!threw) Note($"Tab {kind}: {cells.Count} cell(s), filled in {took.ElapsedMilliseconds} ms.");
                if (took.ElapsedMilliseconds > 400) Check($"tab {kind} fills quickly", false, $"{took.ElapsedMilliseconds} ms");
                yield return Wait(kind == "All" || kind == RoomKind ? 2.5f : .35f);
                if (kind == RoomKind || IsRowTab(kind) || kind == FavTab || kind == MediaKind || kind == "Lighting" || kind == "Furniture")
                    yield return Shot("02_tab_" + kind.Replace(" & ", "_").Replace(' ', '_'));
            }
            tab = "All"; FillTab();
            yield return Wait(.5f);

            // ---- where to put things: the floor in the middle of the screen ---------------------------------------
            Focus();
            Vector3 spot = viewTarget;
            if (Physics.Raycast(viewTarget + Vector3.up * .2f, Vector3.down, out RaycastHit floorHit, 6, ~0, QueryTriggerInteraction.Ignore)) spot = floorHit.point;
            Note("Putting things at " + V(spot));

            var small = catalog.Where(e => e.Source != null && !IsExtra(e.Key) && e.Kind != RoomKind && !CanLive(e.Source) && MeshBounds(e.Source, out Bounds b) && b.size.magnitude > .4f && b.size.magnitude < 1.6f).ToList();
            var picks = small.Where(e => e.Kind == "Furniture" || e.Kind == "Storage").Take(3).ToList();
            if (picks.Count < 3) picks = small.Take(3).ToList();
            Check("three things to try with", picks.Count == 3, string.Join(", ", picks.Select(e => e.Name)));
            if (picks.Count < 3) yield break;
            int decorBefore = decorLayout.Count;
            var put = new List<Transform>();
            for (int i = 0; i < 3; i++)
            {
                int n = i;
                Step("put in " + picks[n].Name, () => put.Add(TestPlace(picks[n], spot + new Vector3(n * 1.1f - 1.1f, 0, n * .35f))));
                yield return null;
            }
            Check("three decorations put in and kept", put.Count == 3 && decorLayout.Count == decorBefore + 3 && put.All(t => t != null && t.GetComponent<DecorTag>()?.Id != null),
                $"{decorLayout.Count - decorBefore} kept");
            Check("they're in the decor file", decorFile != null && File.Exists(decorFile) && put.All(t => File.ReadAllText(decorFile).Contains(t.GetComponent<DecorTag>().Id)));
            Check("Recent has them", Prefs().Recent.Take(3).All(k => picks.Any(p => p.Key == k)));
            TestSelect(put.ToArray());
            yield return Wait(.5f);
            yield return Shot("03_three_put_in");

            // ---- undo and redo ----------------------------------------------------------------------------------
            Step("undo", () =>
            {
                Undo();
                Check("Ctrl+Z takes the last one out", decorLayout.Count == decorBefore + 2, $"{decorLayout.Count - decorBefore} left");
                Redo();
                Check("Ctrl+Y puts it back", decorLayout.Count == decorBefore + 3 && decorObjects.Count(d => d.Value != null) >= 3);
            });
            yield return null;
            // Redo makes the thing again: the ones in the hideout now.
            put = decorObjects.Where(d => d.Value != null && decorLayout.ContainsKey(d.Key)).Select(d => d.Value).Where(t => picks.Any(p => p.Key == t.GetComponent<DecorTag>().Source)).ToList();
            put = put.Skip(Math.Max(0, put.Count - 3)).ToList();
            Check("the three are there after the redo", put.Count == 3);
            if (put.Count < 3) yield break;

            // ---- snapping ---------------------------------------------------------------------------------------
            Step("snapping", () =>
            {
                SnapKind had = Snap.Value;
                float grid = GridSize.Value;
                GridSize.Value = .25f;
                SetSnap(SnapKind.Grid);
                Vector3 snapped = SnapPoint(new Vector3(1.13f, .4f, 2.38f));
                Check("the grid snaps a point", Mathf.Abs(snapped.x - 1.25f) < .001f && Mathf.Abs(snapped.z - 2.5f) < .001f && snapped.y == .4f, V(snapped));
                SetSnap(SnapKind.Off);
                Check("off leaves it alone", SnapPoint(new Vector3(1.13f, 0, 2.38f)).x == 1.13f);
                for (int i = 0; i < 4; i++) CycleSnap();
                Check("N goes round to where it was", Snap.Value == SnapKind.Off);
                // Edges: one thing carried to within reach of another comes flush with it.
                SetSnap(SnapKind.Edges);
                Bounds(put[0], out Bounds a); Bounds(put[1], out Bounds b);
                Vector3 was = put[1].position;
                put[1].position += new Vector3(a.max.x + .06f - b.min.x, 0, a.center.z - b.center.z);
                TestSelect(put[1]);
                StartDrag(put[1]);
                SnapToEdges();
                renderersOf.Remove(put[1]);
                Bounds(put[1], out Bounds after);
                Check("edges pull a carried thing flush", Mathf.Abs(after.min.x - a.max.x) < .005f, $"gap {after.min.x - a.max.x:0.000} m");
                CancelDrag();
                put[1].position = was;
                Snap.Value = had; GridSize.Value = grid;
            });
            yield return null;

            // ---- lining up --------------------------------------------------------------------------------------
            Step("line up", () =>
            {
                TestSelect(put.ToArray());
                AlignSelection(2, 0);
                var z = put.Select(t => { Bounds(t, out Bounds b); return b.center.z; }).ToList();
                Check("middles in one line along Z", z.Max() - z.Min() < .01f, $"spread {z.Max() - z.Min():0.000} m");
                TestSelect(put.ToArray());
                AlignSelection(1, -1);
                var y = put.Select(t => { Bounds(t, out Bounds b); return b.min.y; }).ToList();
                Check("bottoms level", y.Max() - y.Min() < .01f);
                TestSelect(put.ToArray());
                DistributeSelection(0);
                var boxes = put.Select(t => { Bounds(t, out Bounds b); return b; }).OrderBy(b => b.center.x).ToList();
                float gap1 = boxes[1].min.x - boxes[0].max.x, gap2 = boxes[2].min.x - boxes[1].max.x;
                Check("spaced evenly along X", Mathf.Abs(gap1 - gap2) < .01f, $"gaps {gap1:0.000} and {gap2:0.000} m");
                TestSelect(put.ToArray());
                RowSelection(0);
                boxes = put.Select(t => { Bounds(t, out Bounds b); return b; }).OrderBy(b => b.center.x).ToList();
                Check("in a row along X", Mathf.Abs(boxes[1].min.x - boxes[0].max.x - .02f) < .01f && Mathf.Abs(boxes[2].min.x - boxes[1].max.x - .02f) < .01f);
            });
            yield return Wait(.3f);
            yield return Shot("04_row");

            // ---- flip, drop -------------------------------------------------------------------------------------
            Step("flip", () =>
            {
                TestSelect(put[0]);
                Vector3 forward = put[0].forward;
                Bounds(put[0], out Bounds was);
                FlipSelection(0);
                Bounds(put[0], out Bounds now);
                Check("X flips a thing where it stands", Mathf.Abs(put[0].forward.x + forward.x) < .01f && (now.center - was.center).magnitude < .02f,
                    $"forward {V(forward)} -> {V(put[0].forward)}, scale {V(put[0].localScale)}");
                DecorTag tag0 = put[0].GetComponent<DecorTag>();
                Check("and mirrors it (a negative size, kept)", SizeOf(put[0]).x < 0 && decorLayout[tag0.Id].Stretch?[0] < 0, V(SizeOf(put[0])));
                Check("its box is the same size mirrored", (now.size - was.size).magnitude < .02f, $"{V(was.size)} -> {V(now.size)}");
                FlipSelection(0);
                Check("X again and it's as it was", SizeOf(put[0]).x > 0 && Mathf.Abs(put[0].forward.x - forward.x) < .01f && decorLayout[tag0.Id].Stretch == null);
                TestSelect(put[0]);
                put[0].position += Vector3.up * .5f;
                Commit(Belong.Own);
                TestSelect(put[0]);
                DropSelection();
                Bounds(put[0], out Bounds dropped);
                Check("B drops it back onto the floor", Mathf.Abs(dropped.min.y - was.min.y) < .03f, $"{dropped.min.y - was.min.y:0.000} m off");
            });
            yield return null;

            // ---- groups, locks, names ---------------------------------------------------------------------------
            Step("group", () =>
            {
                TestSelect(put.ToArray());
                GroupSelection();
                AnswerAsk("Test group");
                string group = GroupOf(put[0]);
                Check("Ctrl+G groups the three", group != null && Members(group).Count == 3, group ?? "no group");
                ClearSelection(); Select(put[1]); SelectGroup(put[1]);
                Check("picking one picks them all", selection.Count == 3);
                Ungroup();
                Check("Ctrl+Shift+G takes it apart", put.All(t => GroupOf(t) == null));
            });
            Step("lock", () =>
            {
                TestSelect(put[2]);
                LockSelection();
                Check("L locks it and lets it go", IsLocked(put[2]) && selection.Count == 0);
                SelectAllShowing();
                Check("Ctrl+A leaves a locked thing out", !selection.Contains(put[2]), $"{selection.Count} selected");
                ClearSelection();
                UnlockAll();
                Check("Shift+L unlocks everything", !IsLocked(put[2]));
            });
            Step("rename", () =>
            {
                TestSelect(put[0]);
                RenameSelection();
                AnswerAsk("My test thing");
                Check("F2 names it", NameOf(put[0]) == "My test thing" && File.ReadAllText(decorFile).Contains("My test thing"));
            });
            yield return null;
            tab = PlacedTab; FillTab();
            yield return Wait(.4f);
            yield return Shot("05_placed_tab");

            // ---- layouts, prefabs, the clipboard ----------------------------------------------------------------
            string clipboard = GUIUtility.systemCopyBuffer;
            string layoutPath = Path.Combine(KeptFolder("layouts"), "selftest layout.json"), prefabPath = Path.Combine(KeptFolder("prefabs"), "selftest prefab.json");
            Step("layout", () =>
            {
                SaveLayout("selftest layout");
                Check("a layout is saved as a file", File.Exists(layoutPath));
                int had = decorLayout.Count;
                TestSelect(put.ToArray());
                RemoveSelection();
                Check("Delete takes decorations out", decorLayout.Count == had - 3);
                LoadLayoutFile(layoutPath);
                Check("loading the layout puts them back", decorLayout.Count == had && decorObjects.Count(d => d.Value != null) >= 3, $"{decorLayout.Count} of {had}");
                Undo();
                Check("Ctrl+Z after a load is the hideout as it was", decorLayout.Count == had - 3);
                Redo();
                Check("and Ctrl+Y the layout again", decorLayout.Count == had);
                CopyLayout();
                Check("COPY LAYOUT is a layout as text", LooksLikeLayout(GUIUtility.systemCopyBuffer), $"{GUIUtility.systemCopyBuffer?.Length} characters");
            });
            yield return null;
            put = decorObjects.Where(d => d.Value != null && decorLayout.ContainsKey(d.Key)).Select(d => d.Value).Where(t => picks.Any(p => p.Key == t.GetComponent<DecorTag>().Source)).ToList();
            put = put.Skip(Math.Max(0, put.Count - 3)).ToList();
            if (put.Count < 3) { Check("the three are there after the layout", false); yield break; }
            Step("prefab", () =>
            {
                int had = decorLayout.Count;
                TestSelect(put.ToArray());
                SavePrefabAsk();
                AnswerAsk("selftest prefab");
                Check("Ctrl+S saves a prefab file", File.Exists(prefabPath));
                PlacePrefabFile(prefabPath);
                Check("a prefab from the panel is carried", dragging && carrying.Count == 3);
                foreach (Carry c in carrying) c.Thing.position += new Vector3(0, 0, 1.6f) + (spot - carrying[0].Thing.position) * 0;
                Commit(Belong.None);
                ThingsChanged();
                Check("and put down as three more", decorLayout.Count == had + 3);
                TestSelect(put[0], put[1]);
                CopySelection();
                Check("Ctrl+C is a prefab as text", ReadPrefab(GUIUtility.systemCopyBuffer)?.Things.Count == 2);
                Paste();
                Check("Ctrl+V carries the copies", dragging && carrying.Count == 2);
                CancelDrag();
                Check("a right-click lets them go again", !dragging && decorLayout.Count == had + 3);
            });
            GUIUtility.systemCopyBuffer = clipboard ?? "";
            yield return Wait(.3f);
            tab = LayoutTab; FillTab();
            yield return Wait(.3f);
            yield return Shot("06_layouts_tab");

            // ---- favourites -------------------------------------------------------------------------------------
            Step("favourites", () =>
            {
                List<string> favourites = Prefs().Favourites;
                bool was = favourites.Contains(picks[0].Key);
                if (!was) favourites.Add(picks[0].Key);
                tab = FavTab; FillTab();
                Check("a favourite shows in Favourites", cells.Any(c => c.entry.Key == picks[0].Key));
                if (!was) favourites.Remove(picks[0].Key);
                tab = RecentTab; FillTab();
                Check("Recent shows what was put in", cells.Count >= 3, $"{cells.Count}");
            });

            // ---- lights -----------------------------------------------------------------------------------------
            Transform lamp = null, spotLamp = null;
            Step("lights", () =>
            {
                Entry lampEntry = catalog.FirstOrDefault(e => e.Key == LampKey), spotEntry = catalog.FirstOrDefault(e => e.Key == SpotKey);
                Check("the Lighting tab has the mod's Light and Spotlight", lampEntry != null && spotEntry != null);
                lamp = TestPlace(lampEntry, spot + new Vector3(0, 1.2f, -.9f));
                spotLamp = TestPlace(spotEntry, spot + new Vector3(1.2f, 1.6f, -.9f));
                spotLamp.rotation = Quaternion.LookRotation(Vector3.down + Vector3.forward * .3f);
                TestSelect(spotLamp); Commit(Belong.Own);
                Light l = lamp.GetComponentInChildren<Light>(true);
                Check("a placed Light is lit", l != null && l.enabled && HasLights(lamp));
                TestSelect(lamp);
                LightsDo(m => m.LightColor = "#FF3B30");
                Check("its colour can be set", l.color.r > .9f && l.color.g < .4f, l.color.ToString());
                LightsDo(m => m.LightPower = 2.5f);
                Check("and its brightness", Mathf.Abs(l.intensity - 1.5f * 2.5f) < .01f, $"{l.intensity}");
                SwitchLights();
                Check("O switches it off", !l.enabled);
                SwitchLights();
                Check("and on again", l.enabled);
            });
            TestSelect(lamp);
            yield return Wait(.6f);
            Check("the light's bar is along the top", stripRect != null && stripButtons.Count >= 15, $"{stripButtons.Count} buttons");
            yield return Shot("07_light_bar_red");
            // One of the hideout's own lamps.
            Step("own lamp", () =>
            {
                Transform own = ShowingThings().FirstOrDefault(t => t.GetComponent<DecorTag>() == null && HasLights(t) && t.GetComponentsInChildren<Light>(true).Any(l => l.enabled));
                if (own == null) { Note("SKIP no lamp of the hideout's own is showing with a light on."); return; }
                Light l = own.GetComponentsInChildren<Light>(true).First(x => x.enabled);
                TestSelect(own);
                SwitchLights();
                Check($"one of the hideout's own lamps switches off ({own.name})", !l.enabled);
                Undo();
                Check("and Ctrl+Z switches it back on", l.enabled);
            });
            yield return null;

            // ---- doors, animations, wandering -------------------------------------------------------------------
            Transform door = null;
            Step("door", () =>
            {
                Entry e = catalog.FirstOrDefault(c => c.Source != null && !IsExtra(c.Key) && c.Kind != RoomKind && !CanLive(c.Source) && (DoorName.IsMatch(c.Name) || DoorName.IsMatch(c.Source.name)))
                    ?? picks[1];
                Note("Door try with: " + e.Name + " (" + e.Key + ")");
                door = TestPlace(e, spot + new Vector3(-1.3f, 0, -1.6f));
                TestSelect(door);
                Interact();
                Check("I opens a decoration", MetaOf(door, false).Open && swings.ContainsKey(door));
            });
            yield return Wait(1.2f);
            Step("door swung", () =>
            {
                Check("it swings all the way", swings.TryGetValue(door, out Swing s) && Mathf.Abs(s.Now - SwingOpen) < .5f, swings.TryGetValue(door, out Swing w) ? $"{w.Now:0} degrees" : "no swing");
                Check("open is kept", decorLayout[door.GetComponent<DecorTag>().Id].Meta?.Open == true);
            });
            yield return Shot("08_door_open");
            Step("door shut", () => { TestSelect(door); Interact(); });
            yield return Wait(1f);
            Step("door shut check", () => Check("I shuts it again", swings[door].Now < .5f && !MetaOf(door, false).Open));

            // One of the game's own doors.
            Transform gameDoor = null;
            Step("game door", () =>
            {
                gameDoor = ShowingThings().FirstOrDefault(t => t.GetComponent<DecorTag>() == null && GameDoor(t) != null);
                if (gameDoor == null) { Note("SKIP no game door is showing (Security or the cultists' room not built)."); return; }
                Door d = GameDoor(gameDoor);
                Note($"Game door: {gameDoor.name}, {d.DoorState}");
                TestSelect(gameDoor);
                Check("a click on a selected game door opens it", ClickOpens(gameDoor));
                Interact();
                Check("I opens one of the game's doors", d.DoorState == EDoorState.Open && MetaOf(gameDoor, false)?.Open == true, d.DoorState.ToString());
            });
            yield return Wait(1f);
            if (gameDoor != null) yield return Shot("08b_game_door_open");
            Step("game door shut", () =>
            {
                if (gameDoor == null) return;
                TestSelect(gameDoor);
                Interact();
                Check("and shuts it", GameDoor(gameDoor).DoorState == EDoorState.Shut && MetaOf(gameDoor, false)?.Open != true, GameDoor(gameDoor).DoorState.ToString());
            });

            var alive = catalog.Where(e => e.Source != null && AliveFromTheStart(e.Source)).ToList();
            var canLive = catalog.Where(e => e.Source != null && CanLive(e.Source)).ToList();
            Note($"Things with animations in the panel: {canLive.Count} ({string.Join(", ", canLive.Take(12).Select(e => e.Name))}); with a skeleton: {alive.Count} ({string.Join(", ", alive.Take(12).Select(e => e.Name))}).");
            Transform pet = null;
            if (canLive.Count > 0)
            {
                Entry e = alive.FirstOrDefault() ?? canLive[0];
                Step("alive", () =>
                {
                    pet = TestPlace(e, spot + new Vector3(1.4f, 0, -1.8f));
                    TestSelect(pet);
                    if (!MetaOf(pet, false).Alive) Interact();
                    Check($"a copy of {e.Name} plays its animations", aliveThings.Contains(pet) && (pet.GetComponentInChildren<Animator>() != null || pet.GetComponentInChildren<Animation>() != null));
                });
            }
            else Note("SKIP nothing in the hideout's scene has animations to copy.");
            Transform walker = pet ?? put[1];
            Vector3 walkFrom = walker.position;
            Step("wander", () =>
            {
                TestSelect(walker);
                ToggleWander();
                Check("J sets a decoration wandering", walkers.ContainsKey(walker) && MetaOf(walker, false).Wander);
                ClearSelection(); Outlines();
            });
            float farthest = 0;
            for (int i = 0; i < 24; i++) { yield return Wait(.5f); farthest = Mathf.Max(farthest, (walker.position - walkFrom).magnitude); }
            Check("it walks off once it isn't selected", farthest > .1f, $"{farthest:0.00} m in 12 s");
            yield return Shot("09_wandering");
            Step("wander off", () =>
            {
                TestSelect(walker);
                ToggleWander();
                Check("J again and it's back where it was put", !walkers.ContainsKey(walker) && (walker.position - walkFrom).magnitude < .02f, $"{(walker.position - walkFrom).magnitude:0.00} m away");
            });

            // ---- one of the hideout's own things ----------------------------------------------------------------
            Step("own thing", () =>
            {
                Transform own = ShowingThings().FirstOrDefault(t => t.GetComponent<DecorTag>() == null && !IsLocked(t) && Bounds(t, out Bounds b) && b.size.magnitude < 1.5f && b.size.magnitude > .3f
                    && !itemLayout.ContainsKey(ItemKeyFor(t) ?? ""));
                if (own == null) { Check("one of the hideout's own things to try", false); return; }
                string key = ItemKeyFor(own);
                Vector3 was = own.position;
                TestSelect(own);
                Check("it can be selected", selection.Count == 1, own.name + " " + key);
                own.position += new Vector3(.3f, 0, 0);
                Commit(Belong.Own);
                Check("moved, it's kept", itemLayout.ContainsKey(key) && File.ReadAllText(itemsFile).Contains(key.Replace("\\", "\\\\")));
                TestSelect(own);
                SizeSelection(1.1f);
                Check("+ makes it bigger", Mathf.Abs(SizeOf(own).x - 1.1f) < .01f);
                TestSelect(own);
                Interact();
                TestSelect(own);
                RemoveSelection();
                Check("Delete hides it", itemLayout[key].Hidden && !own.GetComponentsInChildren<Renderer>(true).Any(r => r.enabled));
                tab = "Hidden"; FillTab();
                Check("it's in the Hidden tab", hiddenCells.Any(c => c.key == key));
                ClearSelection(); Select(own);
                if (selection.Count == 0) { selection.Add(own); selectionKeys[own] = key; }
                RestoreSelection();
                Check("Backspace sends it back where the game has it", !itemLayout.ContainsKey(key) && (own.position - was).magnitude < .01f && own.GetComponentsInChildren<Renderer>(true).Any(r => r.enabled),
                    $"{(own.position - was).magnitude:0.000} m off");
            });
            yield return null;

            // ---- camera views -----------------------------------------------------------------------------------
            TestSelect(put[0]);
            foreach (ViewKind kind in new[] { ViewKind.Orbit, ViewKind.Front, ViewKind.Side, ViewKind.Top })
            {
                Step("view " + kind, () => { SetView(kind); Check($"the {kind} view is on", view == kind && viewRig != null && viewRig.activeSelf); });
                yield return Wait(1.6f);
                if (kind == ViewKind.Orbit || kind == ViewKind.Top) yield return Shot("10_view_" + kind);
            }
            Step("view off", () => { ExitView(); Check("back to the overview", !ViewActive && !viewRig.activeSelf); });
            yield return Wait(1.5f);

            // ---- photo mode -------------------------------------------------------------------------------------
            ClearSelection(); Outlines();
            int photosBefore = Directory.Exists(PhotoFolder) ? Directory.GetFiles(PhotoFolder, "*.png").Length : 0;
            Canvas host = HostCanvas();
            Step("photo in", () =>
            {
                EnterPhoto();
                Check("C goes into photo mode", photoOn && viewRig.activeSelf);
                Check("the game's screen and the panel are off", host != null && !host.enabled && (ui == null || !ui.activeSelf));
                var still = UnityEngine.Object.FindObjectsOfType<Canvas>().Where(c => c.isRootCanvas && c.enabled && c.renderMode != RenderMode.WorldSpace).Select(c => c.name).ToList();
                Check("no screen canvas is left up (the menu bar too)", still.Count == 0, string.Join(", ", still));
            });
            yield return Wait(1f);
            yield return Shot("11_photo_mode");
            Step("photo", TakePhoto);
            yield return Wait(1.5f);
            Check("Space takes a picture into the photos folder", Directory.Exists(PhotoFolder) && Directory.GetFiles(PhotoFolder, "*.png").Length == photosBefore + 1);
            CameraSpot[] spotsBefore = null;
            Step("spots", () =>
            {
                spotsBefore = Spots().ToArray();
                KeepSpot(7);
                photoAt += Quaternion.Euler(0, photoYaw, 0) * new Vector3(2.5f, .5f, 1.5f); photoYaw += 50;
                PlacePhoto();
                KeepSpot(8);
                Check("Shift+8 and Shift+9 keep camera spots", Spots()[7] != null && Spots()[8] != null && File.Exists(spotsFile));
                FlyToSpot(7, .6f);
                Check("8 flies to its spot", flying);
            });
            yield return Wait(1.2f);
            Step("spot reached", () => Check("and gets there", !flying && (photoAt - new Vector3(Spots()[7].Position[0], Spots()[7].Position[1], Spots()[7].Position[2])).magnitude < .02f));
            Step("tour", () =>
            {
                for (int i = 0; i < 7; i++) Spots()[i] = null;
                TourSeconds.Value = 1.5f;
                StartTour();
                Check("T starts the tour", tourAt == 7);
            });
            yield return Wait(4.5f);
            Step("tour end", () =>
            {
                Check("the tour ends at the last spot", tourAt < 0 && (photoAt - new Vector3(Spots()[8].Position[0], Spots()[8].Position[1], Spots()[8].Position[2])).magnitude < .05f);
                TourSeconds.Value = (float)TourSeconds.DefaultValue;
                for (int i = 0; i < 9; i++) Spots()[i] = spotsBefore[i];
                SaveSpots();
                ExitPhoto();
                Check("Esc leaves photo mode and everything's back", !photoOn && host.enabled && ui != null && ui.activeSelf && !ViewActive);
            });
            yield return Wait(1.5f);
            yield return Shot("12_after_photo");

            // ---- posters and screens: frames and lists ----------------------------------------------------------
            Transform poster = null;
            Step("poster", () =>
            {
                StartPlacingMedia(PosterKey, null, true);
                poster = carrying[0].Thing;
                poster.SetPositionAndRotation(spot + new Vector3(-2.2f, 1.3f, .4f), Quaternion.LookRotation(Vector3.forward));
                Commit(Belong.None);
                ThingsChanged();
                Check("a poster goes in", mediaStates.ContainsKey(poster) && decorLayout.ContainsKey(poster.GetComponent<DecorTag>().Id));
                MediaState s = mediaStates[poster];
                for (int i = 0; i < Frames.Length; i++) NextFrame(s);
                Check($"FRAME goes through its {Frames.Length} styles and round", s.Tag.Frame == 0);
                NextFrame(s); NextFrame(s);
                var files = MediaFiles().Where(f => !IsVideo(f)).ToList();
                Note($"Media folder: {MediaFiles().Count()} file(s), {files.Count} picture(s).");
                if (files.Count >= 2)
                {
                    ListAllFiles(s);
                    Check("ALL FILES makes a slideshow", HasList(s.Tag) && s.Tag.Playlist.Length == files.Count);
                    int at = s.Index;
                    ListStep(s, 1);
                    Check("it steps to the next picture", s.Index != at && s.Tag.Media == s.Tag.Playlist[s.Index]);
                    ListClear(s);
                    Check("CLEAR ends the list", !HasList(s.Tag));
                }
                else Note("SKIP the slideshow: the media folder has fewer than two pictures.");
                TestSelect(poster);
            });
            yield return Wait(1f);
            yield return Shot("13_poster_bar");

            // ---- what a raid would get --------------------------------------------------------------------------
            Step("raid snapshot", () =>
            {
                Snapshot snap = JsonConvert.DeserializeObject<Snapshot>(RaidSnapshot());
                Check("the raid snapshot is what's kept", snap.Decor.Count == decorLayout.Count && snap.Items.Count == itemLayout.Count, $"{snap.Items.Count} thing(s), {snap.Decor.Count} decoration(s)");
            });

            // ---- shutting ---------------------------------------------------------------------------------------
            tab = ToolsTab; FillTab();
            yield return Wait(.4f);
            yield return Shot("14_tools_tab");
            Step("close", () => { CloseDecor(); Check("decor mode closes", !decorOn && ui == null); });
            yield return Wait(1f);
            Step("reopen", () => { OpenDecor(); Check("and opens again", decorOn && ui != null); });
            yield return Wait(1f);
            yield return Shot("15_reopened");
            Step("close again", CloseDecor);
            yield return Wait(.5f);

            // ---- using a screen or poster straight from the overview -------------------------------------------
            Transform screen = null;
            bool panelWas = panelHidden;
            Step("screen in", () =>
            {
                OpenDecor();
                StartPlacingMedia(ScreenKey, null, true);
                screen = carrying[0].Thing;
                screen.SetPositionAndRotation(spot + new Vector3(2.2f, 1.3f, .4f), Quaternion.LookRotation(Vector3.forward));
                Commit(Belong.None);
                ThingsChanged();
                CloseDecor();
            });
            yield return Wait(.5f);
            Step("quick screen", () =>
            {
                QuickOpen(screen, false);
                Check("a click on a screen on the overview uses it straight away", decorOn && quickUse && Using && panelHidden && (showTab == null || !showTab.gameObject.activeSelf));
            });
            yield return Wait(1.5f);
            yield return Shot("16_quick_screen");
            Step("quick screen done", StopUsing);
            yield return null; yield return null;
            Check("done with it, decor mode's shut and the panel as it was", !decorOn && !quickUse && panelHidden == panelWas);
            Step("quick poster", () => QuickOpen(poster, false));
            yield return null; yield return null;
            Check("a click on a poster brings its bar up, its box ready for a link", decorOn && bar != null && addressTyping);
            yield return Wait(.6f);
            yield return Shot("17_quick_poster");
            Step("quick poster done", () => { StopAddress(); CloseDecor(); Check("and Esc puts it away", !decorOn && !quickUse); });
            yield return Wait(.5f);

            // ---- in first person -------------------------------------------------------------------------------
            Step("walk in", () => overlay.EnterHideoutMode());
            float walked = Time.realtimeSinceStartup;
            while (!FirstPersonView() && Time.realtimeSinceStartup - walked < 40) yield return Wait(1);
            Check("the game's ENTER walks into the hideout", FirstPersonView());
            if (!FirstPersonView()) yield break;
            yield return Wait(5);
            Transform fpScreen = null;
            Step("fp screen", () =>
            {
                Camera cam = ViewCamera();
                Vector3 ahead = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                float far = 1.4f;
                if (Physics.Raycast(cam.transform.position, ahead, out RaycastHit wall, 2f, ~0, QueryTriggerInteraction.Ignore) && wall.collider.GetComponentInParent<Player>() == null) far = Mathf.Max(.5f, wall.distance - .12f);
                fpScreen = MakeCopy(ExtraPattern(ScreenKey), DecorRoot(), false);
                fpScreen.SetPositionAndRotation(cam.transform.position + ahead * far - Vector3.up * .05f, Quaternion.LookRotation(-ahead));
                SetupMedia(fpScreen);
                KeepDecor(fpScreen, "");
                SaveDecor();
                Physics.SyncTransforms();
            });
            yield return Wait(.5f);
            Check("looking at a screen in first person, the hint's up", quickHover == fpScreen && quickHoverFp, quickHover != null ? quickHover.name : "nothing looked at");
            yield return Shot("18_fp_hint");
            Step("fp use", () =>
            {
                QuickOpen(fpScreen, true);
                Check("the use key uses it in first person", decorOn && quickFp && Using && ui != null);
            });
            yield return Wait(1.5f);
            Check("the mouse is free while it's used", Cursor.lockState == CursorLockMode.None && Cursor.visible, $"{Cursor.lockState}, visible {Cursor.visible}");
            Step("fp framed", () =>
            {
                bool framed = fpFrame && FpPose(out Vector3 at, out _) && (at - fpToPos).magnitude < .01f;
                Check("the view goes square in front of the screen, as on the overview", framed, $"{(fpToPos - fpScreen.position).magnitude:0.00} m out from it");
                Transform face = mediaStates[fpScreen].Face;
                bool centred = FpRay(ViewCamera(), new Vector3(.5f, .5f, 0), out Ray ray) && new Plane(face.forward, face.position).Raycast(ray, out float d)
                    && face.InverseTransformPoint(ray.GetPoint(d)).magnitude < .02f;
                Check("and a click in the middle of the view is in the middle of the screen", centred);
            });
            yield return Shot("19_fp_using");
            Step("fp done", QuickEscape);
            // The game locks the mouse again itself, a few frames on.
            yield return Wait(1f);
            Check("Esc and it's done: walking about again", !decorOn && Cursor.lockState == CursorLockMode.Locked, $"{Cursor.lockState}");
            Check("and the view's back in the player's eyes", !fpFrame);
            yield return Wait(1f);
            yield return Shot("20_fp_after");
            try { File.Delete(layoutPath); File.Delete(prefabPath); } catch (Exception) { }
        }

        private static string ItemKeyFor(Transform t)
        {
            ItemOf(t, out HideoutArea area);
            return ItemKey(area, t);
        }
    }
}
#endif
