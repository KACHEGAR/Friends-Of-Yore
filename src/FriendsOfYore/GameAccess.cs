using HarmonyLib;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>Private game fields and input injection. All names checked against Assembly-CSharp sha256 99e47bda...</summary>
    public static class GameAccess
    {
        static readonly AccessTools.FieldRef<Climbing, Transform> _lgp = AccessTools.FieldRefAccess<Climbing, Transform>("leftGrabPoint");
        static readonly AccessTools.FieldRef<Climbing, Transform> _rgp = AccessTools.FieldRefAccess<Climbing, Transform>("rightGrabPoint");
        public static Transform LeftGrabPoint(Climbing c) => _lgp(c);
        public static Transform RightGrabPoint(Climbing c) => _rgp(c);

        /// <summary>Set by the self-test bot: forces Rewired "Arm Left"/"Arm Right" to read as held.</summary>
        public static bool ForceArmLeft, ForceArmRight, ForcePullUp, BlockArmLeft, BlockArmRight;
        public static float? ForceMoveV, ForceMoveH;
        /// <summary>One of our panels is open: mouse look and arm clicks must not reach the game.</summary>
        public static bool MenuOpen;
        /// <summary>True while other players are connected: the game must not pause itself when the window loses focus.</summary>
        public static bool CoopActive;

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(Rewired.Player), nameof(Rewired.Player.GetButton), new[] { typeof(string) });
            h.Patch(m, postfix: new HarmonyMethod(typeof(GameAccess), nameof(GetButtonPostfix)));
            foreach (var name in new[] { "GetAxis", "GetAxisRaw" })
                h.Patch(AccessTools.Method(typeof(Rewired.Player), name, new[] { typeof(string) }), postfix: new HarmonyMethod(typeof(GameAccess), nameof(GetAxisPostfix)));
            // InGameMenu.OnApplicationFocus opens the pause menu on focus loss ("UsePauseLostFocus"); in co-op the
            // world must keep running for the others, and tests run two windows side by side.
            // Anyone may pull any piton (0.9). Pulling a friend's rope tells its owner (RopeSync), who loses it from the wall,
            // gets it back in the backpack and falls if hanging on it; the puller gains nothing (no endless ropes).
            h.Patch(AccessTools.Method(typeof(RopeAnchor), "PullOutAnchor"), prefix: new HarmonyMethod(typeof(GameAccess), nameof(PullOutPrefix)));
            // A rope someone else hangs on can't be clipped into (LerpPlayerToLastSegment attaches to ropeHit's rope).
            h.Patch(AccessTools.Method(typeof(RopeAnchor), "LerpPlayerToLastSegment"), prefix: new HarmonyMethod(typeof(GameAccess), nameof(AttachPrefix)));
            h.Patch(AccessTools.Method(typeof(InGameMenu), "OnApplicationFocus"), prefix: new HarmonyMethod(typeof(GameAccess), nameof(FocusPrefix)));
            // A background-loaded scene (model preload) wakes up for a moment; its key-binding button would move itself
            // into OUR options page (GameObject.Find("InGameMenu")) and stay there: one extra "Keybinding Options" per load.
            h.Patch(AccessTools.Method(typeof(ParentOpenControlMapperToMenu), "Awake"), prefix: new HarmonyMethod(typeof(GameAccess), nameof(PreloadAwakePrefix)));
            // Pulling out a FRIEND's piton counts as "putting back" a rope in the summit statistics: never below 0.
            h.Patch(AccessTools.Method(typeof(TimeAttack), "PutBackRope"), prefix: new HarmonyMethod(typeof(GameAccess), nameof(PutBackPrefix)));
        }

        static void GetAxisPostfix(string actionName, ref float __result)
        {
            if (ForceMoveV.HasValue && actionName == "Move Vertical") __result = ForceMoveV.Value;
            if (ForceMoveH.HasValue && actionName == "Move Horizontal") __result = ForceMoveH.Value;
            if (MenuOpen && (actionName == "Look Horizontal" || actionName == "Look Vertical")) __result = 0f;
        }

        /// <summary>Set by RopeSync: true if the transform belongs to a rope mirrored from another player.</summary>
        public static System.Func<Transform, bool> IsMirroredRope = _ => false;
        public static System.Action<Transform, RopeAnchor> OnMirrorPulled;
        public static System.Action<Transform> OnOwnPulled;

        static void PullOutPrefix(RopeAnchor __instance, RaycastHit hit)
        {
            if (hit.transform == null) return;
            if (IsMirroredRope(hit.transform)) OnMirrorPulled?.Invoke(hit.transform, __instance);
            else OnOwnPulled?.Invoke(hit.transform);
        }

        static System.Collections.IEnumerator Empty() { yield break; }

        /// <summary>Set by RopeSync: name of the other player on this rope, or null if free.</summary>
        public static System.Func<GameObject, string> RopeOccupant = _ => null;
        public static System.Action<string> OnBlockedAttach;

        static bool AttachPrefix(RopeAnchor __instance, ref System.Collections.IEnumerator __result)
        {
            var t = __instance.ropeHit.transform;
            var rope = t != null && t.parent != null ? t.parent.gameObject : null;
            var who = rope != null ? RopeOccupant(rope) : null;
            if (who == null) return true;
            __result = Empty();
            OnBlockedAttach?.Invoke(who);
            return false;
        }

        static readonly AccessTools.FieldRef<RopeAnchor, Transform> _firstSeg = AccessTools.FieldRefAccess<RopeAnchor, Transform>("firstSegmentTr");
        /// <summary>Rope root object the local player is clipped into, or null.</summary>
        public static GameObject AttachedRope(RopeAnchor ra)
        {
            if (ra == null || !ra.attached) return null;
            var f = _firstSeg(ra);
            return f != null && f.parent != null ? f.parent.gameObject : null;
        }

        /// <summary>Attached to a rope that no longer exists (pulled out, owner left): the game would keep us "on the rope".</summary>
        public static bool OnDeadRope(RopeAnchor ra)
        {
            if (ra == null || !ra.attached) return false;
            var f = _firstSeg(ra);
            return f == null;
        }

        /// <summary>Name of the scene being loaded in the background for its models, or null.</summary>
        public static string Preloading;
        static bool PreloadAwakePrefix(MonoBehaviour __instance) => Preloading == null || __instance == null || __instance.gameObject.scene.name != Preloading;

        static bool PutBackPrefix(TimeAttack __instance) => __instance.ropesUsed > 0;

        static bool FocusPrefix(bool hasFocus) => !(CoopActive && !hasFocus);

        static void GetButtonPostfix(string actionName, ref bool __result)
        {
            if (MenuOpen && (actionName == "Arm Left" || actionName == "Arm Right")) __result = false;
            // after a friend took our stone: the arm stays down until the player lets go of the button (no instant regrab)
            if (BlockArmLeft && actionName == "Arm Left") { if (!__result) BlockArmLeft = false; __result = false; }
            if (BlockArmRight && actionName == "Arm Right") { if (!__result) BlockArmRight = false; __result = false; }
            if (ForceArmLeft && actionName == "Arm Left") __result = true;
            if (ForceArmRight && actionName == "Arm Right") __result = true;
            if (ForcePullUp && actionName == "Jump / Pull Up") __result = true;
        }
    }
}
