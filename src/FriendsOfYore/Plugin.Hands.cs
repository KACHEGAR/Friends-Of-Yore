using System.Collections.Generic;
using System.Linq;
using FriendsOfYore.Net;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>Hands: one player per hold, catching a friend's hand, grip stamina and who moves whom.</summary>
    public partial class Plugin
    {
        /// <summary>
        /// One player per hold: when a friend grabs the stone we hold, we let go (the newer grab wins). Grab start times
        /// are compared after taking the network delay off the friend's, so both PCs agree on who was first.
        /// </summary>
        float _myGrabL = -1f, _myGrabR = -1f; Transform _myHoldL, _myHoldR;
        class RemoteGrab { public bool On; public Vector3 Pos; public float Since; }
        readonly Dictionary<(ulong, int), RemoteGrab> _remoteGrabs = new Dictionary<(ulong, int), RemoteGrab>();

        void OneHandPerHold()
        {
            float now = Time.unscaledTime;
            foreach (var rp in Remotes.Values)
            {
                var st = rp.Shown;
                for (int side = 0; side < 2; side++)
                {
                    bool on = st != null && rp.Visible && OnRealHold(st, side);
                    var pos = st == null ? Vector3.zero : RemotePlayer.ToV(side == 0 ? st.HandL : st.HandR);
                    if (!_remoteGrabs.TryGetValue((rp.Id, side), out var g)) _remoteGrabs[(rp.Id, side)] = g = new RemoteGrab();
                    // a new grab, or the hand jumped to another stone without letting go in between
                    if (on && (!g.On || Vector3.Distance(pos, g.Pos) > 0.3f)) g.Since = now - (rp.RttMs > 0 ? rp.RttMs / 2000f : 0.05f) - rp.Buffer.Delay;
                    g.On = on; g.Pos = pos;
                }
            }
            var c = _climbing;
            if (c == null) { _myGrabL = _myGrabR = -1f; return; }
            Track(c.grabbingLeft && !c.grabbingNPCHandL ? c.grabbedObjectL : null, ref _myHoldL, ref _myGrabL, now);
            Track(c.grabbingRight && !c.grabbingNPCHandR ? c.grabbedObjectR : null, ref _myHoldR, ref _myGrabR, now);
            // 0.10 let go, but the arm button was still held and the game grabbed the same stone again at once (a "newer"
            // grab), so the take rarely stuck. Now the arm stays down until the button is released.
            if (_myHoldL != null && FriendTookHold(_myHoldL, c.leftGrabber.position, _myGrabL, out var whoL)) { c.ReleaseLHand(lostHand: false); GameAccess.BlockArmLeft = true; Taken(whoL); _myHoldL = null; }
            if (_myHoldR != null && FriendTookHold(_myHoldR, c.rightGrabber.position, _myGrabR, out var whoR)) { c.ReleaseRHand(lostHand: false); GameAccess.BlockArmRight = true; Taken(whoR); _myHoldR = null; }
        }

        static void Track(Transform hold, ref Transform current, ref float since, float now)
        {
            if (hold != current) { current = hold; since = hold != null ? now : -1f; }
        }

        static bool OnRealHold(PlayerState st, int side)
        {
            var grab = side == 0 ? StateFlags.GrabL : StateFlags.GrabR;
            var onFriend = side == 0 ? StateFlags.OnFriendL : StateFlags.OnFriendR;
            var axe = side == 0 ? StateFlags.AxeL : StateFlags.AxeR;
            return (st.Flags & grab) != 0 && (st.Flags & (onFriend | axe)) == 0 && (st.Flags & StateFlags.OnWall) != 0;
        }

        void Taken(string who) { Note($"{who} stole your hold"); Log.LogInfo($"let go: {who} grabbed the hold we were holding"); }

        bool FriendTookHold(Transform hold, Vector3 myHand, float myStart, out string who)
        {
            who = null;
            var col = hold.GetComponent<Collider>();
            foreach (var rp in Remotes.Values)
            {
                var st = rp.Shown;
                if (!rp.Visible || st == null || st.Scene != _scene) continue;
                for (int side = 0; side < 2; side++)
                {
                    if (!OnRealHold(st, side) || !_remoteGrabs.TryGetValue((rp.Id, side), out var g)) continue;
                    // only a grab newer than ours takes the stone (ties: the higher id wins, same answer on both PCs)
                    if (g.Since < myStart || (Mathf.Abs(g.Since - myStart) < 0.02f && rp.Id < _net.LocalId)) continue;
                    var h = g.Pos;
                    float apart = Vector3.Distance(h, myHand);
                    // only when the friend's hand comes into our hand's catch zone (its grab sphere, ~0.26 m) on that stone
                    if (apart > 0.3f) continue;
                    // the whole stone counts: anywhere on (or within 0.35 m of) its collider
                    bool touches;
                    if (col == null) touches = Vector3.Distance(h, hold.position) < 0.5f;
                    else if (col is MeshCollider mc && !mc.convex) { var b = col.bounds; b.Expand(0.7f); touches = b.Contains(h); }
                    else touches = (col.ClosestPoint(h) - h).sqrMagnitude < 0.35f * 0.35f;
                    // a big rock face counts as one hold only near our hand (0.6 m)
                    bool small = col == null || col.bounds.size.magnitude < 4f;
                    if (touches && (small || apart < 0.6f)) { who = rp.DisplayName; return true; }
                }
            }
            return false;
        }

        // ---- holding a friend's hand ----
        /// <summary>The remote player whose hand transform this grab point is parented to, if any.</summary>
        internal RemotePlayer HeldRemote(Transform grabPoint)
        {
            if (grabPoint == null || grabPoint.parent == null) return null;
            foreach (var rp in Remotes.Values)
                if (grabPoint.parent == rp.HandL || grabPoint.parent == rp.HandR) return rp;
            return null;
        }

        readonly Dictionary<Transform, Vector3> _lastHeldPos = new Dictionary<Transform, Vector3>();
        readonly HashSet<Transform> _heldThisFrame = new HashSet<Transform>();

        /// <summary>
        /// Let go of a friend's hand when it disappears, jumps (lag), is pulled back by the friend, or is too far from our
        /// body: the game's SpringJoint on that hand would otherwise yank us across the map.
        /// </summary>
        void GuardHeldHands()
        {
            var c = _climbing;
            if (c == null) return;
            _heldThisFrame.Clear();
            if (c.grabbingNPCHandL) Guard(c, GameAccess.LeftGrabPoint(c), true);
            if (c.grabbingNPCHandR) Guard(c, GameAccess.RightGrabPoint(c), false);
            foreach (var k in _lastHeldPos.Keys.Where(k => !_heldThisFrame.Contains(k)).ToList()) _lastHeldPos.Remove(k);
        }

        void Guard(Climbing c, Transform gp, bool left)
        {
            var rp = HeldRemote(gp);
            if (rp == null) return; // a real NPC hand: the game handles it
            var hand = gp.parent;
            _heldThisFrame.Add(hand);
            float body = Vector3.Distance(hand.position, c.playerBody.position);
            bool firstFrame = !_lastHeldPos.ContainsKey(hand);
            if (firstFrame)
            {
                // The game's grab sphere is generous; for a hand-to-hand catch the hands must really meet (0.5.0: grabs from ~0.5 m).
                float gap = Vector3.Distance(left ? c.leftGrabber.position : c.rightGrabber.position, hand.position);
                if (gap > MaxCatchGap) { Log.LogInfo($"{(left ? "left" : "right")} hand too far from {rp.DisplayName}'s hand ({gap:F2} m), not a catch"); if (left) c.ReleaseLHand(false); else c.ReleaseRHand(false); return; }
            }
            float jump = _lastHeldPos.TryGetValue(hand, out var last) ? Vector3.Distance(last, hand.position) : 0f;
            _lastHeldPos[hand] = hand.position;
            // The grip lasts until the 20 s ring runs out or we let go ourselves. 0.17 also let go when the friend stopped
            // offering the hand for 0.3 s ("withdrawn") and after 0.5 s without packets; now only a real loss breaks it.
            bool stale = rp.SecondsSincePacket > 2f;
            // the friend pulled the hand back (owner: the grip must not stay then); 0.6 s so a short hiccup is not a let-go
            bool withdrawn = rp.HandWithdrawn(hand == rp.HandL);
            // no speed limit: a fast-moving hand (jump between frames) no longer breaks the grip (owner's request)
            if (!rp.Visible || stale || withdrawn || body > 3.5f)
            {
                Log.LogInfo($"{(left ? "left" : "right")} hand lost {rp.DisplayName} (visible={rp.Visible} stale={stale} withdrawn={withdrawn} body={body:F2} jump={jump:F2}), releasing");
                _lastHeldPos.Remove(hand);
                if (left) c.ReleaseLHand(lostHand: false); else c.ReleaseRHand(lostHand: false);
            }
        }

        const float MaxCatchGap = 0.38f;

        // ---- catching a friend's hand has its own, lower sound, every time (the game's grab sound was sometimes cut off) ----
        bool _caughtL, _caughtR; AudioSource _catchSource;

        void CatchSound()
        {
            var c = _climbing;
            if (c == null) { _caughtL = _caughtR = false; return; }
            bool l = c.grabbingNPCHandL && HeldRemote(GameAccess.LeftGrabPoint(c)) != null;
            bool r = c.grabbingNPCHandR && HeldRemote(GameAccess.RightGrabPoint(c)) != null;
            if (l && !_caughtL) PlayCatch(c, c.handSourceL);
            if (r && !_caughtR) PlayCatch(c, c.handSourceR);
            _caughtL = l; _caughtR = r;
            // the player being caught hears it too (0.10: only the catcher did)
            _heldMyL = _heldMyR = false;
            foreach (var rp in Remotes.Values)
            {
                var st = rp.Shown;
                for (int side = 0; side < 2; side++)
                {
                    bool holds = false; bool mineLeft = true;
                    if (st != null && rp.Visible && (st.Flags & (side == 0 ? StateFlags.OnFriendL : StateFlags.OnFriendR)) != 0)
                    {
                        var h = RemotePlayer.ToV(side == 0 ? st.HandL : st.HandR);
                        float dl = Vector3.Distance(h, c.leftGrabber.position), dr = Vector3.Distance(h, c.rightGrabber.position);
                        holds = Mathf.Min(dl, dr) < 0.45f; mineLeft = dl <= dr;
                    }
                    if (holds) { if (mineLeft) _heldMyL = true; else _heldMyR = true; }
                    var k = (rp.Id, side);
                    bool was = _caughtBy.Contains(k);
                    if (holds && !was) { _caughtBy.Add(k); PlayCatch(c, mineLeft ? c.handSourceL : c.handSourceR); }
                    else if (!holds && was) _caughtBy.Remove(k);
                }
            }
        }

        readonly HashSet<(ulong, int)> _caughtBy = new HashSet<(ulong, int)>();
        bool _heldMyL, _heldMyR;

        // ---- hand-to-hand grip stamina: the game's small-hold ring, 20 s for both players, then the catcher lets go; none on a chalked hand ----
        const float GripSeconds = 20f, GripRecoverSeconds = 3f;
        float _stamL = 100f, _stamR = 100f;
        GameObject _ringL, _ringR; UnityEngine.UI.Image _ringImgL, _ringImgR; MicroHolds _micro; string _microScene;

        static readonly HarmonyLib.AccessTools.FieldRef<Climbing, ChalkBag> ChalkOf = HarmonyLib.AccessTools.FieldRefAccess<Climbing, ChalkBag>("chalkBag");

        void HandStamina()
        {
            var c = _climbing;
            bool linkL = c != null && (_caughtL || _heldMyL), linkR = c != null && (_caughtR || _heldMyR);
            // a chalked hand has no time limit and no ring (owner's request, 0.29.3)
            var chalk = c != null ? ChalkOf(c) : null;
            if (chalk != null && chalk.usingChalkL) { linkL = false; _stamL = 100f; }
            if (chalk != null && chalk.usingChalkR) { linkR = false; _stamR = 100f; }
            float dt = Time.deltaTime;
            _stamL = linkL ? _stamL - 100f / GripSeconds * dt : Mathf.Min(100f, _stamL + 100f / GripRecoverSeconds * dt);
            _stamR = linkR ? _stamR - 100f / GripSeconds * dt : Mathf.Min(100f, _stamR + 100f / GripRecoverSeconds * dt);
            if (c != null && _stamL <= 0f && _caughtL) { c.ReleaseLHand(lostHand: true); GameAccess.BlockArmLeft = true; Log.LogInfo("hand-to-hand grip ran out (left)"); }
            if (c != null && _stamR <= 0f && _caughtR) { c.ReleaseRHand(lostHand: true); GameAccess.BlockArmRight = true; Log.LogInfo("hand-to-hand grip ran out (right)"); }
            _stamL = Mathf.Max(0f, _stamL); _stamR = Mathf.Max(0f, _stamR);
            Ring(ref _ringL, ref _ringImgL, linkL, _stamL, c != null ? c.leftGrabber : null);
            Ring(ref _ringR, ref _ringImgR, linkR, _stamR, c != null ? c.rightGrabber : null);
        }

        void Ring(ref GameObject ring, ref UnityEngine.UI.Image img, bool show, float stamina, Transform hand)
        {
            if (!show || hand == null) { if (ring != null && ring.activeSelf) ring.SetActive(false); return; }
            if (_microScene != _scene || _micro == null) { _microScene = _scene; _micro = FindObjectOfType<MicroHolds>(); }
            if (ring == null)
            {
                var tpl = _micro != null ? _micro.lhMicroGripObj : null;
                if (tpl == null) return;
                ring = Instantiate(tpl, tpl.transform.parent, false);
                ring.name = "FoY_HandGripRing";
                foreach (var comp in ring.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null || comp is Transform) continue;
                    var ns = comp.GetType().Namespace ?? "";
                    if (ns != "UnityEngine" && ns != "UnityEngine.UI") DestroyImmediate(comp);
                }
                img = ring.GetComponent<UnityEngine.UI.Image>() ?? ring.GetComponentInChildren<UnityEngine.UI.Image>(true);
                var cg = ring.GetComponent<CanvasGroup>(); if (cg != null) cg.alpha = 1f;
            }
            if (!ring.activeSelf) ring.SetActive(true);
            var cam = _micro != null && _micro.playerCamera != null ? _micro.playerCamera : Camera.main;
            if (cam != null) ring.transform.position = cam.WorldToScreenPoint(hand.position + cam.transform.up * 0.18f);
            if (img != null) img.fillAmount = stamina / 100f;
        }

        void PlayCatch(Climbing c, AudioSource gameSource)
        {
            if (c.grabSounds == null || c.grabSounds.Length == 0) return;
            if (gameSource != null && gameSource.isPlaying && System.Array.IndexOf(c.grabSounds, gameSource.clip) >= 0) gameSource.Stop(); // replaces the stone sound
            if (_catchSource == null)
            {
                var go = new GameObject("FoY_CatchSound"); DontDestroyOnLoad(go);
                _catchSource = go.AddComponent<AudioSource>();
                _catchSource.playOnAwake = false;
            }
            if (gameSource != null)
            {
                _catchSource.outputAudioMixerGroup = gameSource.outputAudioMixerGroup;
                _catchSource.spatialBlend = gameSource.spatialBlend;
                _catchSource.transform.position = gameSource.transform.position;
            }
            _catchSource.pitch = UnityEngine.Random.Range(0.66f, 0.74f); // clearly lower than a stone (0.9-1.05)
            _catchSource.PlayOneShot(c.grabSounds[UnityEngine.Random.Range(0, c.grabSounds.Length)], 0.75f);
        }

        /// <summary>
        /// Who moves whom. A player holding a real hold is an anchor: nothing a friend does may move them, so their own
        /// grip on a friend's hand carries no force (spring 0). If two free players hold each other, both springs would
        /// chase each other's networked hands (20-28 m/s flings in the first Steam test); the lower id anchors.
        /// </summary>
        void ApplyGripForces()
        {
            var c = _climbing;
            if (c == null || _net == null) return;
            bool anchoredOnWall = c.grabbingLeft || c.grabbingRight;
            foreach (var rp in Remotes.Values)
                foreach (var hand in new[] { rp.HandL, rp.HandR })
                {
                    var joint = FindMySpring(hand, c.playerBody);
                    if (joint == null) continue;
                    bool holdsNow = rp.Shown != null && (rp.Shown.Flags & (StateFlags.OnFriendL | StateFlags.OnFriendR)) != 0 && FriendHandNear(rp, c);
                    // sticky for 1 s: the "mutual" check flickered and switched the spring on and off every few frames (0.19 log)
                    if (holdsNow) _mutualUntil[rp.Id] = Time.unscaledTime + 1f;
                    bool friendHoldsMe = holdsNow || (_mutualUntil.TryGetValue(rp.Id, out var mu) && Time.unscaledTime < mu);
                    bool iAmAnchor = anchoredOnWall || (friendHoldsMe && !rp.FriendOnWall && _net.LocalId < rp.Id);
                    float spring = iAmAnchor ? 0f : 255f, damper = iAmAnchor ? 0f : 5f;
                    if (joint.spring != spring)
                    {
                        joint.spring = spring; joint.damper = damper;
                        Log.LogInfo($"grip on {rp.DisplayName}: {(iAmAnchor ? "anchor (no force on me)" : "hanging (spring on)")} wall={anchoredOnWall} mutual={friendHoldsMe}");
                    }
                }
        }

        readonly Dictionary<ulong, float> _mutualUntil = new Dictionary<ulong, float>();

        static SpringJoint FindMySpring(Transform hand, Rigidbody body)
        {
            foreach (var j in hand.GetComponents<SpringJoint>()) if (j.connectedBody == body) return j;
            return null;
        }

        static bool FriendHandNear(RemotePlayer rp, Climbing c)
        {
            var s = rp.Shown; bool near(Vector3 h) => Vector3.Distance(h, c.leftGrabber.position) < 0.4f || Vector3.Distance(h, c.rightGrabber.position) < 0.4f;
            return ((s.Flags & StateFlags.OnFriendL) != 0 && near(RemotePlayer.ToV(s.HandL))) || ((s.Flags & StateFlags.OnFriendR) != 0 && near(RemotePlayer.ToV(s.HandR)));
        }
    }
}
