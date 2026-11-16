using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using EFT;
using EFT.InputSystem;
using UnityEngine;

namespace HideoutInteriors
{
    /// <summary>
    /// Posters, TV screens and computers used straight from the hideout, without decor mode: on the overview a click on
    /// one, walking about (first person) looking at one and pressing F. A screen or computer is used as in decor mode
    /// (the mouse and keys work its page or video, its bar along the top); a poster's bar comes up with its box ready for
    /// a link. Esc, DONE or a click off it and the hideout's as it was. In first person the player stands still and the
    /// mouse is free while it lasts.
    /// </summary>
    internal static partial class Interiors
    {
        internal static ConfigEntry<KeyCode> UseKey;

        // Using one thing quickly (decor mode open just for it), in first person or not, it's done, and how the panel was.
        private static bool quickUse, quickFp, quickDone, quickPanelWas;
        private static Transform quickThing;
        // What's pointed or looked at, that a click or the key would use.
        private static Transform quickHover;
        private static bool quickHoverFp;
        private static readonly List<Transform> quickHoverList = new List<Transform>(1);
        private const float ReachInFirstPerson = 3.2f;
        private static GUIStyle hintStyle;

        internal static void BindQuick(ConfigFile config)
        {
            UseKey = config.Bind("Decorating", "Use key in first person", KeyCode.F,
                "Walking about the hideout: look at a poster, TV screen or computer and press this to use it (on the overview, click it).");
        }

        // The player's walking about, looking about freely (no menu or screen of the game's up).
        private static bool FirstPersonView() => owner != null && owner.FirstPersonMode;
        private static bool FreeLooking() => FirstPersonView() && Cursor.lockState == CursorLockMode.Locked;

        /// <summary>Each frame in the hideout while decor mode's shut: what's pointed at, and a click or the key on it.</summary>
        private static void QuickTick()
        {
            Transform t = null;
            bool fp = FreeLooking();
            if (mediaStates.Count > 0)
            {
                if (fp) t = LookedAtMedia();
                else if (Overview() && !AreaOpen() && !OverGameUi()) t = MediaUnderMouse();
            }
            SetQuickHover(t, fp);
            if (t != null && (fp ? Input.GetKeyDown(UseKey.Value) : Input.GetMouseButtonDown(0))) QuickOpen(t, fp);
        }

        private static void SetQuickHover(Transform t, bool fp)
        {
            quickHoverFp = fp;
            if (t == quickHover) return;
            quickHover = t;
            quickHoverList.Clear();
            if (t != null) { quickHoverList.Add(t); ItemOutline.Set(quickHoverList, Color.white); }
            else if (!decorOn) ItemOutline.Clear();
        }

        // The poster, screen or computer in the middle of the view, near enough to reach.
        private static Transform LookedAtMedia()
        {
            Camera camera = ViewCamera();
            if (camera == null) return null;
            Ray ray = camera.ViewportPointToRay(new Vector3(.5f, .5f, 0));
            int count = Physics.RaycastNonAlloc(ray, rayHits, ReachInFirstPerson, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(rayHits, 0, count, HitDistance.Instance);
            for (int i = 0; i < count; i++)
            {
                Collider c = rayHits[i].collider;
                if (c == null || c.GetComponentInParent<Player>() != null) continue;
                DecorTag tag = c.GetComponentInParent<DecorTag>();
                return tag != null && mediaStates.ContainsKey(tag.transform) ? tag.transform : null;
            }
            return null;
        }

        private static Transform MediaUnderMouse()
        {
            Transform t = ThingUnderMouse(out _, out _);
            return t != null && mediaStates.ContainsKey(t) ? t : null;
        }

        // The mouse is on a button or window of the game's (not on a screen-sized backdrop).
        private static bool OverGameUi()
        {
            var events = UnityEngine.EventSystems.EventSystem.current;
            if (events == null) return false;
            uiHits.Clear();
            if (pointer == null || pointerSystem != events) { pointer = new UnityEngine.EventSystems.PointerEventData(events); pointerSystem = events; }
            pointer.position = Input.mousePosition;
            events.RaycastAll(pointer, uiHits);
            foreach (var hit in uiHits)
                if (hit.gameObject != null && !Huge(hit.gameObject)) return true;
            return false;
        }

        /// <summary>Decor mode opened just to use one thing: no panel, the thing selected and in use.</summary>
        private static void QuickOpen(Transform t, bool fp)
        {
            if (decorOn || t == null || !mediaStates.ContainsKey(t)) return;
            quickPanelWas = panelHidden;
            panelHidden = true;
            quickUse = true; quickFp = fp; quickDone = false; quickThing = t;
            SetQuickHover(null, fp);
            OpenDecor();
            if (!decorOn) { QuickReset(); return; }
            ClearSelection();
            Select(t);
            Outlines();
            EnterMedia(t);
            if (fp) FreeCursor();
            Status();
        }

        // Shut with decor mode: how things were.
        private static void QuickReset()
        {
            if (!quickUse) return;
            bool fp = quickFp;
            EndFpFrame();
            panelHidden = quickPanelWas;
            quickUse = quickFp = quickDone = false;
            quickThing = null;
            if (fp && FirstPersonView()) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        private static void FreeCursor()
        {
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }

        /// <summary>
        /// Each frame of decor mode opened quickly, while nothing's in use (a poster's bar up): its bar's clicks, a click
        /// anywhere else is done. True if decor mode's other handling is to be left out this frame.
        /// </summary>
        private static void QuickIdleTick(Vector2 mouse, bool overBar)
        {
            if (Input.GetMouseButtonDown(0))
            {
                if (BarClick(mouse) || StripClick(mouse)) return;
                StopAddress();
                CloseDecor();
                return;
            }
            if (overBar) LibraryScroll();
        }

        // Esc in first person, where the overview's keys don't come (it's what Esc does there on the overview).
        private static void QuickEscape()
        {
            if (Using)
            {
                if (libraryRect != null) { CloseLibrary(); BarLook(); }
                else if (addressTyping) StopAddress();
                else StopUsing();
            }
            else if (addressTyping) StopAddress();
            else CloseDecor();
        }

        private static string QuickText()
        {
            Transform t = quickThing;
            if (t != null && IsPoster(t))
                return "<b>THE POSTER</b> · type or paste a link to a picture in the bar's box, or pick one from LIBRARY · FRAME, size and the list are on the bar\nEsc or a click off it: done";
            return "Esc or a click off it: done";
        }

        // The player's input while a thing's used in first person: nothing (no walking, no looking about), the cursor shown.
        private static bool PlayerInput(InputNode __instance, ref float[] axes, ref ECursorResult shouldLockCursor)
        {
            if (!decorOn || !quickFp || !(__instance is HideoutPlayerOwner)) return true;
            if (axes != null) for (int i = 0; i < axes.Length; i++) axes[i] = 0;
            shouldLockCursor = ECursorResult.ShowCursor;
            return false;
        }

        // ---- in first person, the view in front of the screen ------------------------------------------------
        //
        // The player's camera is the player's: where it is each frame is the game's to say. So it's only moved for
        // drawing, square in front of the screen as decor mode's camera goes on the overview (eased there and back),
        // and put back once the frame's drawn. The game's look (its colour, its effects) comes with it.

        private static Camera fpCamera;
        private static bool fpFrame, fpLeaving, fpMoved, fpHooked;
        private static Vector3 fpFromPos, fpToPos, fpSavedPos;
        private static Quaternion fpFromRot, fpToRot, fpSavedRot;
        private static float fpStart;
        private const float FpEase = .45f;

        private static void StartFpFrame(MediaState s)
        {
            Camera camera = ViewCamera();
            Transform face = s?.Face;
            if (camera == null || face == null) return;
            Vector3 size = face.lossyScale, outward = s.Thing.forward;
            float tan = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad / 2), aspect = Screen.width / (float)Mathf.Max(1, Screen.height);
            float distance = Mathf.Max(.25f, Mathf.Max(Mathf.Abs(size.y) / 2 / tan, Mathf.Abs(size.x) / 2 / (tan * aspect)) / UseFill);
            fpCamera = camera;
            fpFromPos = camera.transform.position; fpFromRot = camera.transform.rotation;
            fpToPos = face.position + outward * distance;
            fpToRot = Quaternion.LookRotation(-outward, Mathf.Abs(outward.y) > .9f ? s.Thing.up : Vector3.up);
            fpStart = Time.unscaledTime;
            fpFrame = true; fpLeaving = false;
            if (!fpHooked) { Camera.onPreCull += FpPreCull; Camera.onPostRender += FpPostRender; fpHooked = true; }
        }

        // Back to the player's eyes, eased.
        private static void EndFpFrame()
        {
            if (!fpFrame || fpLeaving) return;
            FpPose(out fpFromPos, out fpFromRot);
            fpLeaving = true;
            fpStart = Time.unscaledTime;
        }

        // Where the camera is drawn from now; false once it's back with the player.
        private static bool FpPose(out Vector3 pos, out Quaternion rot)
        {
            float t = Mathf.Clamp01((Time.unscaledTime - fpStart) / FpEase), k = t * t * (3 - 2 * t);
            // Leaving goes back to wherever the player's eyes are now.
            Vector3 toPos = fpToPos; Quaternion toRot = fpToRot;
            if (fpLeaving && fpCamera != null) { toPos = fpMoved ? fpSavedPos : fpCamera.transform.position; toRot = fpMoved ? fpSavedRot : fpCamera.transform.rotation; }
            pos = Vector3.Lerp(fpFromPos, toPos, k);
            rot = Quaternion.Slerp(fpFromRot, toRot, k);
            if (fpLeaving && t >= 1) { fpFrame = fpLeaving = false; return false; }
            return true;
        }

        private static void FpPreCull(Camera c)
        {
            if (!fpFrame || c != fpCamera || c == null) return;
            fpSavedPos = c.transform.position; fpSavedRot = c.transform.rotation;
            fpMoved = true;
            if (!FpPose(out Vector3 pos, out Quaternion rot)) { fpMoved = false; return; }
            c.transform.SetPositionAndRotation(pos, rot);
        }

        private static void FpPostRender(Camera c)
        {
            if (!fpMoved || c != fpCamera || c == null) return;
            c.transform.SetPositionAndRotation(fpSavedPos, fpSavedRot);
            fpMoved = false;
        }

        /// <summary>The mouse's ray from where the camera's drawn from (in front of a screen in first person).</summary>
        private static bool FpRay(Camera camera, Vector3 viewport, out Ray ray)
        {
            ray = default;
            if (!fpFrame || camera != fpCamera || camera == null || !FpPose(out Vector3 pos, out Quaternion rot)) return false;
            Transform t = camera.transform;
            Vector3 was = t.position; Quaternion wasRot = t.rotation;
            t.SetPositionAndRotation(pos, rot);
            ray = camera.ViewportPointToRay(viewport);
            t.SetPositionAndRotation(was, wasRot);
            return true;
        }

        // What a click or the key would do, by the mouse or under the middle of the view.
        internal static void QuickGui()
        {
            if (decorOn || quickHover == null || photoOn) return;
            string verb = IsComputer(quickHover) ? "use the computer" : IsPoster(quickHover) ? "put a picture on it" : "watch, or change what's on";
            string text = (quickHoverFp ? $"[{UseKey.Value}] " : "Click: ") + verb;
            if (hintStyle == null) hintStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter };
            var style = hintStyle;
            style.fontSize = Mathf.RoundToInt(Mathf.Max(13, Screen.height / 72f));
            style.normal.textColor = new Color(.9f, .88f, .82f);
            Vector2 size = style.CalcSize(new GUIContent(text)) + new Vector2(16, 8);
            Vector2 at = quickHoverFp
                ? new Vector2(Screen.width / 2f - size.x / 2, Screen.height / 2f + 40)
                : new Vector2(Input.mousePosition.x + 22, Screen.height - Input.mousePosition.y + 22);
            GUI.Box(new Rect(at, size), text, style);
        }
    }
}
