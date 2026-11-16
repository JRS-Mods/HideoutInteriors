using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EFT.Interactive;
using JsonType;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// Things that do something. A decoration can be opened like a door (it swings on one of its upright edges; I, or
    /// a click on a selected door again), play the animations the thing it copies has (a copy is only a look until it's
    /// told to live), and wander about near where it was put, the way a pet would. One of the hideout's own things with
    /// animations or particles can be stopped and started. All of it is kept with the thing.
    /// </summary>
    internal static partial class Interiors
    {
        // ---- opening: a swing on an upright edge ---------------------------------------------------------------

        private sealed class Swing { public Transform Pivot; public Vector3 Axis; public float Now, Want; }

        private static readonly Dictionary<Transform, Swing> swings = new Dictionary<Transform, Swing>();
        private const float SwingOpen = 100f, SwingSpeed = 200f;
        // What a click on it (selected) opens, as well as I.
        private static readonly Regex DoorName = new Regex(@"(^|[_\s\-.])(door|gate|hatch|turnstile|shutter)", RegexOptions.IgnoreCase);

        // The hinge: everything the thing is made of hung under a pivot on its narrower upright edge. The thing's own
        // place and box stay as they were, so where it's kept doesn't change when it opens.
        private static Swing SwingFor(Transform t)
        {
            if (swings.TryGetValue(t, out Swing s) && s.Pivot != null) return s;
            if (!LocalBounds(t, out Bounds b)) return null;
            int up = 0;
            float most = -1;
            for (int i = 0; i < 3; i++)
            {
                float y = Mathf.Abs(t.TransformDirection(Axis(i)).y);
                if (y > most) { most = y; up = i; }
            }
            int a = (up + 1) % 3, c = (up + 2) % 3;
            Vector3 scale = t.lossyScale;
            int wide = b.size[a] * Mathf.Abs(scale[a]) >= b.size[c] * Mathf.Abs(scale[c]) ? a : c;
            Vector3 hinge = b.center;
            hinge[wide] = b.min[wide];
            Transform pivot = new GameObject("Swing").transform;
            pivot.gameObject.layer = t.gameObject.layer;
            pivot.SetParent(t, false);
            pivot.localPosition = hinge;
            var parts = new List<Transform>();
            foreach (Transform child in t) if (child != pivot) parts.Add(child);
            foreach (Transform child in parts) child.SetParent(pivot, true);
            // A thing whose mesh is on itself: a body of the same under the pivot stands in for it.
            MeshFilter filter = t.GetComponent<MeshFilter>();
            MeshRenderer renderer = t.GetComponent<MeshRenderer>();
            if (filter != null && renderer != null && filter.sharedMesh != null && renderer.enabled)
            {
                var body = new GameObject("Body", typeof(MeshFilter), typeof(MeshRenderer));
                body.layer = t.gameObject.layer;
                body.transform.SetParent(t, false);
                body.transform.SetParent(pivot, true);
                body.GetComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                MeshRenderer made = body.GetComponent<MeshRenderer>();
                made.sharedMaterials = renderer.sharedMaterials;
                made.shadowCastingMode = renderer.shadowCastingMode;
                renderer.enabled = false;
            }
            renderersOf.Remove(t);
            s = new Swing { Pivot = pivot, Axis = Axis(up) };
            swings[t] = s;
            return s;
        }

        private static void SetSwing(Transform t, bool open, bool atOnce)
        {
            if (!open && !swings.ContainsKey(t)) return;
            Swing s = SwingFor(t);
            if (s == null) return;
            s.Want = open ? SwingOpen : 0;
            if (!atOnce) return;
            s.Now = s.Want;
            s.Pivot.localRotation = Quaternion.AngleAxis(s.Now, s.Axis);
        }

        // ---- the game's own doors (the Security room's, the cultists', the Emergency Wall's) ---------------------

        private static readonly HashSet<Transform> doorsOpened = new HashSet<Transform>();

        private static Door GameDoor(Transform t) => t != null ? t.GetComponentInChildren<Door>(true) : null;

        // Opened or shut as kept; one never opened here is left to the game. Not in a raid's copy: the raid's mod
        // has its own say over the doors.
        private static void SetGameDoor(Transform t, bool open)
        {
            if (raidCopy || (!open && !doorsOpened.Contains(t))) return;
            Door door = GameDoor(t);
            if (door == null) return;
            try
            {
                EDoorState state = open ? EDoorState.Open : EDoorState.Shut;
                door.SetDoorState(state, true);
                door.CurrentAngle = door.GetAngle(state);
                if (open) doorsOpened.Add(t); else doorsOpened.Remove(t);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Decor: {t.name} couldn't be {(open ? "opened" : "shut")}: {e.Message}"); }
        }

        // ---- alive: the animations of what it copies -----------------------------------------------------------

        private static readonly HashSet<Transform> aliveThings = new HashSet<Transform>();

        private static bool CanLive(Transform source) =>
            source != null && (source.GetComponentInChildren<Animator>(true) != null || source.GetComponentInChildren<Animation>(true) != null);

        // A new copy of a thing with a skeleton is alive from the start: without its animations it stands in its
        // modelling pose.
        private static bool AliveFromTheStart(Transform source) =>
            source != null && source.GetComponentInChildren<SkinnedMeshRenderer>(true) != null && CanLive(source);

        // The same part of a copy as a part of what it copies, by its path of names.
        private static Transform Counterpart(Transform sourceRoot, Transform part, Transform copyRoot)
        {
            if (part == sourceRoot) return copyRoot;
            var names = new List<string>();
            for (Transform t = part; t != null && t != sourceRoot; t = t.parent) names.Add(t.name);
            names.Reverse();
            return copyRoot.Find(string.Join("/", names));
        }

        private static void SetAlive(Transform copy, DecorTag tag, bool on)
        {
            if (on == aliveThings.Contains(copy)) return;
            if (!on)
            {
                foreach (Animator a in copy.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.Destroy(a);
                foreach (Animation a in copy.GetComponentsInChildren<Animation>(true)) UnityEngine.Object.Destroy(a);
                aliveThings.Remove(copy);
                return;
            }
            Transform source = Resolve(tag.Source);
            if (source == null) return;
            try
            {
                foreach (Animator from in source.GetComponentsInChildren<Animator>(true))
                {
                    Transform at = Counterpart(source, from.transform, copy);
                    if (at == null || from.runtimeAnimatorController == null) continue;
                    Animator a = at.GetComponent<Animator>();
                    if (a == null) a = at.gameObject.AddComponent<Animator>();
                    a.runtimeAnimatorController = from.runtimeAnimatorController;
                    a.avatar = from.avatar;
                    a.applyRootMotion = false;
                    // The clips' events call the game's scripts, which a copy doesn't have.
                    a.fireEvents = false;
                    a.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                    a.enabled = true;
                }
                foreach (Animation from in source.GetComponentsInChildren<Animation>(true))
                {
                    Transform at = Counterpart(source, from.transform, copy);
                    if (at == null) continue;
                    Animation a = at.GetComponent<Animation>();
                    if (a == null) a = at.gameObject.AddComponent<Animation>();
                    foreach (AnimationState state in from)
                        if (state != null && state.clip != null && a.GetClip(state.name) == null) a.AddClip(state.clip, state.name);
                    a.clip = from.clip;
                    a.wrapMode = from.wrapMode;
                    a.playAutomatically = true;
                    if (a.clip != null) a.Play();
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Decor: {copy.name}'s animations couldn't be given to its copy: {e.Message}"); }
            aliveThings.Add(copy);
        }

        // ---- still: one of the hideout's own, stopped ----------------------------------------------------------

        private sealed class Stilled { public List<Behaviour> Off = new List<Behaviour>(); public List<ParticleSystem> Stopped = new List<ParticleSystem>(); }

        private static readonly Dictionary<Transform, Stilled> stilled = new Dictionary<Transform, Stilled>();

        private static bool HasMotion(Transform t) =>
            t.GetComponentInChildren<Animator>(true) != null || t.GetComponentInChildren<Animation>(true) != null || t.GetComponentInChildren<ParticleSystem>(true) != null;

        private static void SetStill(Transform t, bool still)
        {
            if (still == stilled.ContainsKey(t)) return;
            if (still)
            {
                var s = new Stilled();
                foreach (Animator a in t.GetComponentsInChildren<Animator>(true)) if (a.enabled) { a.enabled = false; s.Off.Add(a); }
                foreach (Animation a in t.GetComponentsInChildren<Animation>(true)) if (a.enabled) { a.enabled = false; s.Off.Add(a); }
                foreach (ParticleSystem p in t.GetComponentsInChildren<ParticleSystem>(true))
                    if (p.isPlaying) { p.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear); s.Stopped.Add(p); }
                stilled[t] = s;
                return;
            }
            Stilled was = stilled[t];
            stilled.Remove(t);
            foreach (Behaviour b in was.Off) if (b != null) b.enabled = true;
            foreach (ParticleSystem p in was.Stopped) if (p != null) p.Play(false);
        }

        // ---- wandering ----------------------------------------------------------------------------------------

        private sealed class Walker
        {
            public Transform Thing;
            public Vector3 Home, Goal;
            public Quaternion HomeTurn;
            public bool Moving;
            public float WaitUntil;
        }

        private static readonly Dictionary<Transform, Walker> walkers = new Dictionary<Transform, Walker>();
        private const float WanderReach = 2.5f, WanderSpeed = .45f;

        private static void SetWalker(Transform t, bool on)
        {
            if (!on) { walkers.Remove(t); return; }
            if (walkers.TryGetValue(t, out Walker w)) { w.Home = t.position; w.HomeTurn = t.rotation; w.Moving = false; return; }
            walkers[t] = new Walker { Thing = t, Home = t.position, HomeTurn = t.rotation, WaitUntil = Time.unscaledTime + UnityEngine.Random.Range(1f, 4f) };
        }

        // Where it was put is where it wanders about from now on.
        private static void WalkerPut(Transform t)
        {
            if (walkers.TryGetValue(t, out Walker w)) { w.Home = t.position; w.HomeTurn = t.rotation; w.Moving = false; w.WaitUntil = Time.unscaledTime + 2; }
        }

        private static readonly List<Transform> goneWalkers = new List<Transform>();

        private static void WalkersTick(float dt)
        {
            if (walkers.Count == 0) return;
            goneWalkers.Clear();
            foreach (Walker w in walkers.Values)
            {
                Transform t = w.Thing;
                if (t == null) { goneWalkers.Add(t); continue; }
                if (!t.gameObject.activeInHierarchy) continue;
                // Held still while it's selected or carried: it's being worked on.
                if (decorOn && selection.Contains(t)) { w.Moving = false; continue; }
                if (!w.Moving)
                {
                    if (Time.unscaledTime < w.WaitUntil) continue;
                    w.WaitUntil = Time.unscaledTime + 1.5f;
                    if (!WanderGoal(w)) continue;
                    w.Moving = true;
                }
                Vector3 to = w.Goal - t.position;
                Vector3 flat = new Vector3(to.x, 0, to.z);
                if (flat.magnitude < .05f)
                {
                    w.Moving = false;
                    w.WaitUntil = Time.unscaledTime + UnityEngine.Random.Range(2f, 8f);
                    Moved();
                    continue;
                }
                Quaternion heading = Quaternion.LookRotation(flat.normalized, Vector3.up);
                t.rotation = Quaternion.RotateTowards(t.rotation, heading, 220 * dt);
                // It turns first, then goes.
                if (Quaternion.Angle(t.rotation, heading) < 35) t.position += Vector3.ClampMagnitude(to, WanderSpeed * dt);
            }
            if (goneWalkers.Count > 0)
                foreach (Transform key in walkers.Where(p => p.Value.Thing == null).Select(p => p.Key).ToList()) walkers.Remove(key);
        }

        // Somewhere near home to go to: on the same floor, with nothing in the way.
        private static bool WanderGoal(Walker w)
        {
            Transform t = w.Thing;
            Vector2 way = UnityEngine.Random.insideUnitCircle * WanderReach;
            Vector3 goal = new Vector3(w.Home.x + way.x, t.position.y, w.Home.z + way.y);
            Vector3 from = t.position + Vector3.up * .25f, to = goal + Vector3.up * .25f;
            Vector3 along = to - from;
            float far = along.magnitude;
            if (far < .3f) return false;
            int count = Physics.RaycastNonAlloc(new Ray(from, along / far), rayHits, far + .3f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (!rayHits[i].collider.transform.IsChildOf(t)) return false;
            // The floor there is the floor here (not a step down, not a table).
            float lift = float.MaxValue, goalLift = float.MaxValue;
            count = Physics.RaycastNonAlloc(from, Vector3.down, rayHits, 3, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (!rayHits[i].collider.transform.IsChildOf(t) && rayHits[i].normal.y > .6f) lift = Mathf.Min(lift, rayHits[i].distance);
            count = Physics.RaycastNonAlloc(to, Vector3.down, rayHits, 3, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (!rayHits[i].collider.transform.IsChildOf(t) && rayHits[i].normal.y > .6f) goalLift = Mathf.Min(goalLift, rayHits[i].distance);
            if (lift == float.MaxValue || goalLift == float.MaxValue || Mathf.Abs(lift - goalLift) > .12f) return false;
            w.Goal = goal;
            return true;
        }

        // ---- all of it ----------------------------------------------------------------------------------------

        /// <summary>What a thing does, as kept with it: its lights, and whether it's open, alive, wandering or stopped.</summary>
        private static void ApplyMeta(Transform t, ThingMeta m, bool atOnce)
        {
            if (t == null) return;
            ApplyLights(t, m);
            DecorTag tag = t.GetComponent<DecorTag>();
            if (tag != null)
            {
                if ((m != null && m.Alive) || aliveThings.Contains(t)) SetAlive(t, tag, m != null && m.Alive);
                SetSwing(t, m != null && m.Open, atOnce);
                if ((m != null && m.Wander) || walkers.ContainsKey(t)) SetWalker(t, m != null && m.Wander);
            }
            else
            {
                SetStill(t, m != null && m.Still);
                SetGameDoor(t, m != null && m.Open);
            }
        }

        private static void ForgetMotion()
        {
            swings.Clear(); aliveThings.Clear(); stilled.Clear(); walkers.Clear(); doorsOpened.Clear();
            ForgetLights();
        }

        // Each frame in the hideout: doors on their way open or shut, and the wanderers.
        private static void MotionTick()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
            LightsTick();
            if (swings.Count > 0)
            {
                bool moved = false, gone = false;
                foreach (Swing s in swings.Values)
                {
                    if (s.Pivot == null) { gone = true; continue; }
                    if (s.Now == s.Want) continue;
                    s.Now = Mathf.MoveTowards(s.Now, s.Want, SwingSpeed * dt);
                    s.Pivot.localRotation = Quaternion.AngleAxis(s.Now, s.Axis);
                    moved = true;
                }
                if (gone) foreach (Transform key in swings.Where(p => p.Value.Pivot == null).Select(p => p.Key).ToList()) swings.Remove(key);
                if (moved) Moved();
            }
            WalkersTick(dt);
        }

        // A click on the one selected thing again opens it, if it's a door.
        private static bool ClickOpens(Transform t)
        {
            DecorTag tag = t != null ? t.GetComponent<DecorTag>() : null;
            if (tag == null) return GameDoor(t) != null;
            if (IsMediaKey(tag.Source)) return false;
            return (tag.Meta != null && tag.Meta.Open) || DoorName.IsMatch(t.name) || DoorName.IsMatch(Pretty(t.name));
        }

        // I: what's selected does what it does. A decoration with animations plays or stops them, any other opens or
        // shuts; one of the hideout's own with animations or particles stops or starts.
        private static void Interact()
        {
            selection.RemoveAll(t => t == null);
            if (selection.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Select a thing, then I: a door opens, a thing with animations plays or stops."); return; }
            Checkpoint();
            var changed = new List<Transform>();
            int own = 0;
            string said = null;
            foreach (Transform t in selection)
            {
                DecorTag tag = t.GetComponent<DecorTag>();
                if (tag != null)
                {
                    if (IsMediaKey(tag.Source)) continue;
                    ThingMeta m = MetaOf(t, true);
                    if (CanLive(Resolve(tag.Source)))
                    {
                        m.Alive = !m.Alive;
                        SetAlive(t, tag, m.Alive);
                        said = m.Alive ? "Its animations play." : "Its animations are stopped.";
                    }
                    else
                    {
                        m.Open = !m.Open;
                        SetSwing(t, m.Open, false);
                        said = m.Open ? "Open." : "Shut.";
                    }
                    changed.Add(t);
                }
                else if (GameDoor(t) != null)
                {
                    ThingMeta m = MetaOf(t, true);
                    if (m == null) continue;
                    m.Open = !m.Open;
                    // Shutting one opened in an earlier visit: it's ours to shut.
                    if (!m.Open) doorsOpened.Add(t);
                    SetGameDoor(t, m.Open);
                    said = m.Open ? "Open." : "Shut.";
                    changed.Add(t);
                }
                else if (HasMotion(t))
                {
                    ThingMeta m = MetaOf(t, true);
                    if (m == null) continue;
                    m.Still = !m.Still;
                    SetStill(t, m.Still);
                    said = m.Still ? "Stopped." : "Going again.";
                    changed.Add(t);
                }
                else own++;
            }
            if (changed.Count > 0) SaveMeta(changed);
            if (changed.Count == 0)
                EFT.Communications.NotificationManager.DisplayMessageNotification(own > 0
                    ? "Of the hideout's own things, its doors open and things with animations stop and start. To open anything else like a door, copy it (Ctrl+D) and press I on the copy."
                    : "Nothing selected opens or plays.");
            else if (changed.Count == 1 && said != null) EFT.Communications.NotificationManager.DisplayMessageNotification(said);
            Status();
        }

        // J: the selected decorations wander about near where they stand, or stay put again.
        private static void ToggleWander()
        {
            var those = selection.Where(t => t != null && t.GetComponent<DecorTag>() != null && !IsMedia(t)).ToList();
            if (those.Count == 0) { EFT.Communications.NotificationManager.DisplayMessageNotification("Select a decoration (a copy from the panel) to have it wander about. The hideout's own things stay where the game has them: copy one first (Ctrl+D)."); return; }
            Checkpoint();
            bool wander = !those.All(t => MetaOf(t, false) is ThingMeta m && m.Wander);
            foreach (Transform t in those)
            {
                ThingMeta m = MetaOf(t, true);
                m.Wander = wander;
                // Staying put again: back where it was put.
                if (!wander && walkers.TryGetValue(t, out Walker w)) t.SetPositionAndRotation(w.Home, w.HomeTurn);
                SetWalker(t, wander);
            }
            SaveMeta(those);
            EFT.Communications.NotificationManager.DisplayMessageNotification(wander
                ? "It wanders about within a few steps of where it stands, once it isn't selected. Put it somewhere else and it wanders from there."
                : "It stays put.");
            Status();
        }
    }
}
