using System;
using FriendsOfYore.Net;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>The local player: finding the body and capturing the state friends see.</summary>
    public partial class Plugin
    {
        Climbing _climbing;          // the climbing rig on a peak (null in cabins)
        PlayerMove _walker;          // the walking body where there is no climbing rig
        int _handLayer = 28;         // layer of NPC hands the game can grab; friends' hands use it too
        internal Climbing LocalClimbing => _climbing;

        /// <summary>The state to send: the climbing state on peaks, the simple walking state in cabins.</summary>
        PlayerState CaptureAny()
        {
            try { return _climbing != null && !IsHub(_scene) ? Capture() : CaptureWalker(); }
            catch (Exception e)
            {   // 0.10: an exception here (in some cabins) meant friends never saw us there
                if (_reported.Add("state capture")) Log.LogWarning($"state capture failed in {_scene}, sending a simple state: {e}");
                try { return CaptureWalker(); }
                catch (Exception e2) { Log.LogWarning("simple state failed too: " + e2.Message); return null; }
            }
        }

        void FindLocalPlayer()
        {
            if (_climbing != null) return;
            // Cabins: walking only. The Northern cabin has a climbing rig without hands (leftGrabber null): 0.10 threw
            // there every frame, before the network was read, so neither player saw the other.
            _climbing = IsHub(_scene) ? null : FindObjectOfType<Climbing>();
            if (_climbing != null && (_climbing.leftGrabber == null || _climbing.rightGrabber == null || _climbing.playerBody == null)) _climbing = null;
            if (_climbing == null)
            {   // the main cabin has no climbing rig: walking only
                if (_walker == null && (_walker = FindObjectOfType<PlayerMove>()) != null && _walker.rigid != null) Log.LogInfo($"local walker found in {_scene} (no climbing here)");
                return;
            }
            _walker = null;
            var npc = FindObjectOfType<NPC_Climber>();
            if (npc != null && npc.reachGrabCollider != null) _handLayer = npc.reachGrabCollider.gameObject.layer;
            AvatarModel.CaptureAxe(_climbing.iceAxes);
            Log.LogInfo($"local player found in {_scene}; remote hand layer {_handLayer} '{LayerMask.LayerToName(_handLayer)}' in grabMask={(_climbing.grabMask.value & (1 << _handLayer)) != 0}");
            foreach (var rp in Remotes.Values) { rp.HandL.gameObject.layer = _handLayer; rp.HandR.gameObject.layer = _handLayer; }
        }

        PlayerState Capture()
        {
            var c = _climbing;
            var cam = Camera.main != null ? Camera.main.transform : c.transform;
            var e = cam.eulerAngles;
            var rope = _net != null ? _ropes.MyRopeRef(_net.LocalId, _activity.Rope) : default;
            var s = new PlayerState
            {
                Seq = ++_seq, Time = Time.unscaledTime, WallMs = Trace.WallMs, Scene = SendScene,
                Body = RemotePlayer.FromV(c.playerBody.position), Velocity = RemotePlayer.FromV(c.playerBody.velocity),
                Yaw = StableYaw(cam), Pitch = e.x > 180f ? e.x - 360f : e.x,
                HandL = RemotePlayer.FromV(c.leftGrabber.position), HandR = RemotePlayer.FromV(c.rightGrabber.position),
                Model = _cfgMyModel.Value, Act = MenuOnScreen ? Activity.Menu : _activity.Detect(_scene), RopeOwner = rope.owner, RopeId = rope.id,
            };
            if (c.grabbingLeft || c.grabbingNPCHandL) s.Flags |= StateFlags.GrabL;
            if (c.grabbingRight || c.grabbingNPCHandR) s.Flags |= StateFlags.GrabR;
            if (c.grabbingNPCHandL && HeldRemote(GameAccess.LeftGrabPoint(c)) != null) s.Flags |= StateFlags.OnFriendL;
            if (c.grabbingNPCHandR && HeldRemote(GameAccess.RightGrabPoint(c)) != null) s.Flags |= StateFlags.OnFriendR;
            if ((s.Flags & (StateFlags.OnFriendL | StateFlags.OnFriendR)) != 0)
            {
                if (LocalFreeSolo) { if ((s.Flags & StateFlags.OnFriendL) != 0) c.ReleaseLHand(false); if ((s.Flags & StateFlags.OnFriendR) != 0) c.ReleaseRHand(false); }
                else CoopRules.MarkAssisted("held a friend's hand");
            }
            if (c.fpController != null && c.fpController.IsGrounded()) s.Flags |= StateFlags.Grounded;
            if (c.grabbingLeft || c.grabbingRight) s.Flags |= StateFlags.OnWall;
            if (c.LTrigger > 0f) s.Flags |= StateFlags.ReachL;
            if (c.RTrigger > 0f) s.Flags |= StateFlags.ReachR;
            // Ice axes: send the real axe rotation and grip point; "reach" means swinging or stuck in the ice.
            var ia = c.iceAxes;
            var yawInv = Quaternion.Inverse(Quaternion.Euler(0f, StableYaw(cam), 0f)); // axe rotation relative to where the player faces
            if (ia != null && ia.usingIceAxeL && AvatarModel.LocalAxe(true, out var rl, out var gl))
            {
                s.Flags |= StateFlags.AxeL; s.AxeRotL = RemotePlayer.FromV((yawInv * rl).eulerAngles); s.HandL = RemotePlayer.FromV(gl);
                if (ia.LTrigger > 0f || ia.successfulLeft) s.Flags |= StateFlags.ReachL; else s.Flags &= ~StateFlags.ReachL;
            }
            if (CarryingLamp()) s.Flags |= StateFlags.Lamp;
            if (Crampons.cramponsActivated) s.Flags |= StateFlags.CramponRest;
            s.Flags |= ModeFlags(); s.Idols = GameIcons.LocalIdols();
            if (ia != null && ia.usingIceAxeR && AvatarModel.LocalAxe(false, out var rr, out var gr))
            {
                s.Flags |= StateFlags.AxeR; s.AxeRotR = RemotePlayer.FromV((yawInv * rr).eulerAngles); s.HandR = RemotePlayer.FromV(gr);
                if (ia.RTrigger > 0f || ia.successfulRight) s.Flags |= StateFlags.ReachR; else s.Flags &= ~StateFlags.ReachR;
            }
            return s;
        }

        /// <summary>
        /// Heading of the view that stays right when looking straight up or down (euler y flips there: on a crampon rest,
        /// looking up the ice, the friend's body stopped turning in 0.18).
        /// </summary>
        static float StableYaw(Transform cam)
        {
            var f = cam.forward; var h = new Vector3(f.x, 0f, f.z);
            if (h.sqrMagnitude < 0.09f) { var u = cam.up * (f.y > 0f ? -1f : 1f); h = new Vector3(u.x, 0f, u.z); }
            return h.sqrMagnitude < 1e-6f ? cam.eulerAngles.y : Mathf.Atan2(h.x, h.z) * Mathf.Rad2Deg;
        }

        // While the game loads the next place the old one is torn down under us and the body drops through the floor;
        // friends saw that fall (0.19). During a load we report no place, so friends simply stop showing us.
        LevelLoader _loader; string _loaderScene;

        bool Loading
        {
            get
            {
                if (_loaderScene != _scene) { _loaderScene = _scene; _loader = FindObjectOfType<LevelLoader>(); }
                return _loader != null && _loader.currentlyLoadingAScene;
            }
        }

        string SendScene => Loading ? "(loading)" : _scene;
        internal Rigidbody LocalBody => _climbing != null ? _climbing.playerBody : _walker != null ? _walker.rigid : null;

        /// <summary>Where we are: the climbing body, the walking body, or (a hub with neither) the camera.</summary>
        Transform LocalBodyTransform => LocalBody != null ? LocalBody.transform : IsHub(_scene) && Camera.main != null ? Camera.main.transform : null;

        /// <summary>
        /// The body the camera is in. In the Northern and Alpine cabins the climbing rig found in the scene is not the one
        /// you walk with (0.10 sent its fixed position, so friends saw nobody); pick whatever body is within 3 m of the camera.
        /// </summary>
        Rigidbody HubBody()
        {
            var cam = Camera.main;
            if (cam == null) return LocalBody;
            if (Time.unscaledTime > _hubScanAt) { _hubScanAt = Time.unscaledTime + 1f; _hubWalkers = FindObjectsOfType<PlayerMove>(); }
            bool Near(Rigidbody rb) => rb != null && rb.gameObject.activeInHierarchy && Vector3.Distance(rb.position, cam.transform.position) < 3f;
            if (_climbing != null && Near(_climbing.playerBody)) return _climbing.playerBody;
            if (_walker != null && Near(_walker.rigid)) return _walker.rigid;
            if (_hubWalkers != null) foreach (var w in _hubWalkers) if (w != null && Near(w.rigid)) return w.rigid;
            if (!_hubCamLogged) { _hubCamLogged = true; Log.LogInfo($"no body near the camera in {_scene}: friends see you at the camera"); }
            return null;
        }

        float _hubScanAt; PlayerMove[] _hubWalkers; bool _hubCamLogged;

        /// <summary>The save's mode and the harness, shown as icons by the name (0.25).</summary>
        static StateFlags ModeFlags()
        {
            var gm = GameManager.control; if (gm == null) return StateFlags.None;
            var f = StateFlags.None;
            if (gm.permaDeathEnabled) f |= StateFlags.Yfyd;
            if (gm.freesoloEnabled) f |= StateFlags.FreeSolo;
            if (gm.isUsingHarness && !gm.permaDeathEnabled) f |= StateFlags.Harness;
            return f;
        }

        public static bool LocalFreeSolo => GameManager.control != null && GameManager.control.freesoloEnabled;
        public static bool LocalHardMode => GameManager.control != null && (GameManager.control.freesoloEnabled || GameManager.control.permaDeathEnabled);

        /// <summary>State for places without a climbing rig (the main cabin): body, look and hands hanging at the sides.</summary>
        PlayerState CaptureWalker()
        {
            var bodyRb = IsHub(_scene) ? HubBody() : LocalBody;
            var cam = Camera.main != null ? Camera.main.transform : LocalBodyTransform;
            var e = cam.eulerAngles;
            var yawRot = Quaternion.Euler(0f, StableYaw(cam), 0f);
            var p = bodyRb != null ? bodyRb.position : cam.position + Vector3.down * 0.7f;
            var vel = bodyRb != null ? bodyRb.velocity : Vector3.zero;
            var s = new PlayerState
            {
                Seq = ++_seq, Time = Time.unscaledTime, WallMs = Trace.WallMs, Scene = SendScene,
                Body = RemotePlayer.FromV(p), Velocity = RemotePlayer.FromV(vel),
                Yaw = StableYaw(cam), Pitch = e.x > 180f ? e.x - 360f : e.x,
                HandL = RemotePlayer.FromV(p + yawRot * new Vector3(-0.25f, -0.35f, 0.1f)), HandR = RemotePlayer.FromV(p + yawRot * new Vector3(0.25f, -0.35f, 0.1f)),
                Model = _cfgMyModel.Value, Act = MenuOnScreen ? Activity.Menu : Activity.None,
            };
            bool grounded = true; try { if (_walker != null) grounded = _walker.IsGrounded(); } catch { }
            if (grounded) s.Flags |= StateFlags.Grounded;
            s.Flags |= ModeFlags(); s.Idols = GameIcons.LocalIdols();
            return s;
        }

        OilLamp _lamp; string _lampScene;

        /// <summary>Aldr Grotto oil lamp: picked up = its pickup collider is off and its light is on.</summary>
        bool CarryingLamp()
        {
            if (_lampScene != _scene) { _lampScene = _scene; _lamp = FindObjectOfType<OilLamp>(); if (_lamp != null) AvatarModel.CaptureLamp(_lamp); }
            return _lamp != null && _lamp.lampCol != null && !_lamp.lampCol.enabled && _lamp.oilLampLight != null && _lamp.oilLampLight.enabled && _lamp.oilLampLight.gameObject.activeInHierarchy;
        }
    }
}
