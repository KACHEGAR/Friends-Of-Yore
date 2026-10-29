using System;
using System.Reflection;
using HarmonyLib;

namespace FriendsOfYore
{
    /// <summary>
    /// Rules tied to the game's summit (owner's requests, 0.25):
    /// - a climb that used a friend's rope or hand is not a free solo: the summit is stamped as a normal summit
    ///   (the game gives the free solo stamp when no rope was placed on the map: we count one for the duration of the
    ///   summit code only);
    /// - every summit stamp is announced to the friends.
    /// </summary>
    public static class CoopRules
    {
        /// <summary>This map was climbed with a friend's help (rope, hand). Reset on every scene load.</summary>
        public static bool Assisted;
        public static string AssistedHow;
        public static Action<bool> OnSummit; // freeSolo stamp given?

        public static void MarkAssisted(string how)
        {
            if (Assisted) return;
            Assisted = true; AssistedHow = how;
            Plugin.Log.LogInfo($"this climb is no longer a free solo: {how}");
        }

        public static void Patch(Harmony h)
        {
            var pre = new HarmonyMethod(typeof(CoopRules), nameof(Before));
            var post = new HarmonyMethod(typeof(CoopRules), nameof(After));
            h.Patch(AccessTools.Method(typeof(StamperPeakSummit), "SavePeakProgress"), prefix: pre, postfix: post);
            h.Patch(AccessTools.Method(typeof(StamperPeakSummit), "SetStampForPage"), prefix: pre, postfix: post);
        }

        [ThreadStatic] static int _bumped;

        static void Before(StamperPeakSummit __instance, MethodBase __originalMethod)
        {
            try
            {
                var ra = Traverse.Create(__instance).Field("ropeAnchor").GetValue<RopeAnchor>();
                if (Assisted && ra != null && ra.ropesPlacedDuringMap == 0) { ra.ropesPlacedDuringMap = 1; _bumped++; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("summit rule: " + e.Message); }
        }

        static void After(StamperPeakSummit __instance, MethodBase __originalMethod, object[] __args)
        {
            RopeAnchor ra = null;
            try { ra = Traverse.Create(__instance).Field("ropeAnchor").GetValue<RopeAnchor>(); } catch { }
            bool free = false;
            try { free = GameManager.control != null && (GameManager.control.freesoloEnabled || (ra != null && ra.ropesPlacedDuringMap == 0)); } catch { }
            if (_bumped > 0 && ra != null) { ra.ropesPlacedDuringMap = 0; _bumped--; free = GameManager.control != null && GameManager.control.freesoloEnabled; }
            bool customStamp = __originalMethod != null && __originalMethod.Name == "SetStampForPage" && __args != null && __args.Length > 2 && __args[2] is bool st && st;
            if (__originalMethod != null && (__originalMethod.Name == "SavePeakProgress" || customStamp))
            {
                if (Assisted) Plugin.Log.LogInfo($"summit stamped as a normal summit ({AssistedHow})");
                try { OnSummit?.Invoke(free); } catch (Exception e) { Plugin.Log.LogWarning("summit note: " + e.Message); }
            }
        }
    }
}
