using FriendsOfYore.Net;
using HarmonyLib;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>Reads what the local player is doing from the game's own state (names from Assembly-CSharp 99e47bda).</summary>
    public class ActivityDetector
    {
        CoffeeDrink _coffee; ChalkBag _chalk; RopeAnchor _rope; string _scene;
        bool _chalkL, _chalkR; int _chalkUses = -1; float _chalkUntil;
        static readonly AccessTools.FieldRef<RopeAnchor, bool> _pullingOut = AccessTools.FieldRefAccess<RopeAnchor, bool>("pullingOutAnchor");

        public Activity Detect(string scene)
        {
            if (scene != _scene) { _scene = scene; _coffee = null; _chalk = null; _rope = null; _chalkUses = -1; _chalkL = _chalkR = false; }
            if (_coffee == null) _coffee = Object.FindObjectOfType<CoffeeDrink>();
            if (_chalk == null) _chalk = Object.FindObjectOfType<ChalkBag>();
            if (_rope == null) _rope = Object.FindObjectOfType<RopeAnchor>();
            if (_coffee != null && (_coffee.drinkingCoffeL || _coffee.drinkingCoffeR)) return Activity.Coffee;
            // chalk: only while the hand goes to the bag (~1.5 s), not while the chalk lasts on the hands (0.20)
            if (_chalk != null)
            {
                bool l = _chalk.usingChalkL, r = _chalk.usingChalkR; int uses = _chalk.chalkBagUsesLeft;
                if ((l && !_chalkL) || (r && !_chalkR) || (_chalkUses >= 0 && uses < _chalkUses)) _chalkUntil = Time.unscaledTime + 1.5f;
                _chalkL = l; _chalkR = r; _chalkUses = uses;
                if (Time.unscaledTime < _chalkUntil) return Activity.Chalk;
            }
            if (Monocular.isUsingMonocular || Monocular.startingMonocular) return Activity.Monocular;
            if (_rope != null && (_rope.usingInteractionHammer || _pullingOut(_rope))) return Activity.Hammer; // usingHammer = rope tool out, not hammering (0.12 showed "hammering" until the tool was put away)
            if (Bivouac.usingBivouac || Bivouac.currentlyUsingBivouac) return Activity.Bivouac;
            if (StamperPeakSummit.currentlyStampingPeakJournal) return Activity.Stamping;
            if (TimeAttack.isInteractingWithWatch) return Activity.Watch;
            if (_rope != null && _rope.attached) return Activity.OnRope;
            return Activity.None;
        }

        public RopeAnchor Rope => _rope;
    }
}
