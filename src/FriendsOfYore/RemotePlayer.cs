using System.Collections.Generic;
using FriendsOfYore.Net;
using System.Linq;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// Puppet for one remote climber. Hands carry a kinematic Rigidbody + trigger collider tagged
    /// "ClimbableNPCHand", the tag Climbing.ClimbingMethod already treats as a grabbable companion hand
    /// (it adds a SpringJoint from the hand object to the local player's body). So grabbing a friend's
    /// hand uses the game's own NPC co-climbing physics.
    /// </summary>
    public class RemotePlayer : MonoBehaviour
    {
        public ulong Id;
        public float RttMs;
        public bool FriendOnWall => Shown != null && (Shown.Flags & StateFlags.OnWall) != 0;
        public string DisplayName = "";
        public readonly SnapshotBuffer Buffer = new SnapshotBuffer();
        public static System.Func<RemotePlayer, bool, bool> HeldByMe;
        /// <summary>Where the local player's hand on a real hold (stone, branch, skeleton...) is; null when on none.</summary>
        public static System.Func<Vector3?> LocalHoldPoint;
        public PlayerState Shown;
        float _renderTime = -1f;
        float _lastPacketLocal;
        Transform _body, _head, _handL, _handR, _armL, _armR;
        Renderer _handRendL, _handRendR;
        static readonly Color HandOpen = new Color(1f, 0.85f, 0.3f), HandGrip = new Color(1f, 0.45f, 0.1f);
        Rigidbody _rbL, _rbR;
        TextMesh _label, _actLabel;
        Transform _labelRoot;
        SphereCollider _colL, _colR;
        float _offeredL = -9f, _offeredR = -9f;
        AvatarModel _model;
        /// <summary>The model the friend chose is not captured on this PC (we never saw that NPC this session).</summary>
        public bool ModelMissing => Shown != null && !string.IsNullOrEmpty(Shown.Model) && !AvatarModel.HasModel(Shown.Model);
        public void ResetModel() { if (_model != null) { Destroy(_model.Root); _model = null; } }
        /// <summary>The friend pulled this hand back (or put it on a stone) for over 0.6 s: a catch on it ends.</summary>
        public bool HandWithdrawn(bool left) => Time.unscaledTime - (left ? _offeredL : _offeredR) > 0.6f;
        Vector3 _prevBody; bool _havePrev;
        Renderer[] _simpleParts;
        public Transform HandL => _handL;
        public Transform HandR => _handR;
        public bool Visible { get; private set; }

        public Color Color = Color.white;
        public float LastVoice = -9f;
        /// <summary>The friend's place as the game names it (sent by them), and the scene it belongs to.</summary>
        public string PlaceTitle, PlaceScene;
        /// <summary>On a Workshop peak: title, folder, compendium, peak, page (Plugin.JoinCustomPeak).</summary>
        public string[] CustomPlace;
        public bool Talking => Time.unscaledTime - LastVoice < 0.35f;
        public static RemotePlayer Create(ulong id, string name, int handLayer, Color color)
        {
            var root = new GameObject($"FoY_Remote_{id}");
            Object.DontDestroyOnLoad(root);
            var rp = root.AddComponent<RemotePlayer>();
            rp.Id = id; rp.DisplayName = name; rp.Color = color;
            rp._body = Prim(PrimitiveType.Capsule, root.transform, new Vector3(0.36f, 0.7f, 0.36f), color, false);
            rp._head = Prim(PrimitiveType.Sphere, root.transform, new Vector3(0.28f, 0.3f, 0.28f), color * 1.15f, false);
            rp._handL = Hand(root.transform, "L", handLayer, color, out rp._rbL);
            rp._handR = Hand(root.transform, "R", handLayer, color, out rp._rbR);
            rp._handRendL = rp._handL.GetComponent<Renderer>(); rp._handRendR = rp._handR.GetComponent<Renderer>();
            rp._armL = Prim(PrimitiveType.Cylinder, root.transform, Vector3.one, color * 0.9f, false);
            rp._armR = Prim(PrimitiveType.Cylinder, root.transform, Vector3.one, color * 0.9f, false);
            rp._labelRoot = new GameObject("labels").transform;
            rp._labelRoot.SetParent(root.transform, false);
            rp._label = Text3D(rp._labelRoot, name, 0.035f, new Vector3(0f, 0.12f, 0f), new Color(1f, 0.97f, 0.9f));
            rp._actLabel = Text3D(rp._labelRoot, "", 0.026f, Vector3.zero, new Color(1f, 0.85f, 0.45f));
            rp._colL = (SphereCollider)rp._handL.GetComponent<Collider>(); rp._colR = (SphereCollider)rp._handR.GetComponent<Collider>();
            rp._simpleParts = new[] { rp._body.GetComponent<Renderer>(), rp._head.GetComponent<Renderer>(), rp._armL.GetComponent<Renderer>(), rp._armR.GetComponent<Renderer>(), rp._handRendL, rp._handRendR };
            rp.Visible = true; rp.SetVisible(false); // force: SetVisible returns early when the flag already matches
            return rp;
        }

        // ---- icons by the name: harness (rope), "You fall, you die" (skull), free solo (climber) - from the game's menu ----
        readonly List<SpriteRenderer> _icons = new List<SpriteRenderer>();
        int _iconKey = -1;
        const StateFlags IconMask = StateFlags.Harness | StateFlags.Yfyd | StateFlags.FreeSolo;

        void UpdateIcons(StateFlags flags, ushort idols)
        {
            var f = flags & IconMask;
            int key = ((int)f << 16) | idols;
            var list = GameIcons.For(f, idols);
            if (key == _iconKey && _icons.Count == list.Count) return;
            _iconKey = key;
            foreach (var r in _icons) if (r != null) Destroy(r.gameObject);
            _icons.Clear();
            const float size = 0.13f, gap = 0.03f;
            float x0 = -(list.Count * size + (list.Count - 1) * gap) / 2f + size / 2f;
            for (int i = 0; i < list.Count; i++)
            {
                var go = new GameObject("FoY_Icon"); go.transform.SetParent(_labelRoot, false);
                go.transform.localPosition = new Vector3(x0 + i * (size + gap), 0.33f, 0f);
                go.transform.localScale = Vector3.one * size;
                var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = GameIcons.AsSprite(list[i]);
                sr.enabled = Visible;
                _icons.Add(sr);
            }
        }

        static TextMesh Text3D(Transform parent, string text, float size, Vector3 local, Color c)
        {
            var go = new GameObject("text");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text; tm.characterSize = size; tm.fontSize = 64; tm.anchor = TextAnchor.LowerCenter; tm.alignment = TextAlignment.Center; tm.color = c;
            Ui.Apply(tm);
            return tm;
        }

        static Transform Prim(PrimitiveType t, Transform parent, Vector3 scale, Color c, bool keepCollider)
        {
            var go = GameObject.CreatePrimitive(t);
            if (!keepCollider) Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.material.color = c;
            return go.transform;
        }

        static Transform Hand(Transform parent, string side, int layer, Color c, out Rigidbody rb)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "FoYHand" + side;
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * 0.17f;
            go.GetComponent<Renderer>().material.color = HandOpen;
            var col = (SphereCollider)go.GetComponent<Collider>();
            col.isTrigger = true;          // never shoves the local player; OverlapSphere still finds it
            col.radius = 1.5f;             // ~0.25 m world radius: a bigger target to catch (0.14 m in 0.7.0); the reach distance itself is unchanged (Plugin.GuardHeldHands)
            go.tag = "ClimbableNPCHand";
            go.layer = layer;
            rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;         // must exist before Climbing adds its SpringJoint
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            return go.transform;
        }

        public void Push(PlayerState s)
        {
            Buffer.Push(s);
            _lastPacketLocal = Time.unscaledTime;
            if (_renderTime < 0f) _renderTime = s.Time - Buffer.Delay;
        }

        public float SecondsSincePacket => Time.unscaledTime - _lastPacketLocal;

        public void SetVisible(bool v)
        {
            if (Visible == v) return;
            Visible = v;
            foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = v;
            if (_model != null) { _model.Root.SetActive(v); if (v) foreach (var r in _simpleParts) r.enabled = false; }
            if (!v) { _colL.enabled = false; _colR.enabled = false; }
        }

        /// <summary>Advance the render clock; stays Buffer.Delay behind the newest snapshot, with slow drift correction.</summary>
        public void Step(float dt, string localScene)
        {
            var latest = Buffer.Latest;
            if (latest == null) { SetVisible(false); return; }
            _renderTime += dt;
            float target = latest.Time - Buffer.Delay;
            float err = target - _renderTime;
            if (Mathf.Abs(err) > 1f) _renderTime = target; else _renderTime += err * 0.1f;
            Shown = Buffer.Sample(_renderTime);
            bool sameScene = Shown != null && Shown.Scene == localScene;
            SetVisible(sameScene && SecondsSincePacket < 5f);
            if (!Visible) return;
            var body = ToV(Shown.Body);
            _body.position = body;
            _head.position = body + Vector3.up * 0.75f;
            _body.rotation = Quaternion.Euler(0, Shown.Yaw, 0);
            _head.rotation = Quaternion.Euler(Shown.Pitch, Shown.Yaw, 0);
            // Hands can be grabbed only while the friend offers them (reaching or holding with that hand).
            // A hand that holds a stone (or any object other than a friend's hand) is not offered (0.10: it could be grabbed).
            bool stoneL = (Shown.Flags & StateFlags.GrabL) != 0 && (Shown.Flags & StateFlags.OnFriendL) == 0;
            bool stoneR = (Shown.Flags & StateFlags.GrabR) != 0 && (Shown.Flags & StateFlags.OnFriendR) == 0;
            bool offerL = (Shown.Flags & (StateFlags.ReachL | StateFlags.GrabL)) != 0 && !stoneL, offerR = (Shown.Flags & (StateFlags.ReachR | StateFlags.GrabR)) != 0 && !stoneR;
            if (offerL) _offeredL = Time.unscaledTime;
            if (offerR) _offeredR = Time.unscaledTime;
            // ...and never a hand that holds an ice axe.
            bool axeL = (Shown.Flags & StateFlags.AxeL) != 0, axeR = (Shown.Flags & StateFlags.AxeR) != 0;
            // a hand we already hold stays grabbable, so the game never drops it early (0.17)
            bool heldL = HeldByMe != null && HeldByMe(this, true), heldR = HeldByMe != null && HeldByMe(this, false);
            _colL.enabled = (offerL || heldL) && !axeL; _colR.enabled = (offerR || heldR) && !axeR;
            // free solo (0.25): a free soloist never takes a friend's hand, and nobody can take theirs
            if (Plugin.LocalFreeSolo || (Shown.Flags & StateFlags.FreeSolo) != 0) { _colL.enabled = _colR.enabled = false; }
            if (axeL) _offeredL = -9f; if (axeR) _offeredR = -9f;
            // Labels: name + what they are doing, game font, facing the camera.
            Ui.Apply(_label); Ui.Apply(_actLabel);
            UpdateIcons(Shown.Flags, Shown.Idols);
            var act = Ui.ActivityText(Shown.Act);
            if (_actLabel.text != act) _actLabel.text = act;
            _labelRoot.position = body + Vector3.up * (_model != null ? 0.55f + _model.HeadAboveBody : 1.0f);
            if (Camera.main != null) _labelRoot.rotation = Quaternion.LookRotation(_labelRoot.position - Camera.main.transform.position);
            // Body: the model the friend chose for themselves; fall back if this PC hasn't seen that NPC.
            if (AvatarModel.HasTemplate)
            {
                string want = AvatarModel.HasModel(Shown.Model) ? Shown.Model : AvatarModel.Available.First();
                if (_model != null && _model.Kind != want) ResetModel();
                if (_model == null)
                {
                    _model = AvatarModel.Spawn(transform, want);
                    if (_model != null) { foreach (var r in _simpleParts) r.enabled = false; Plugin.Log.LogInfo($"{DisplayName}: model {_model.Kind} attached (wanted '{Shown.Model}')"); }
                }
            }
            if (_model != null)
            {
                foreach (var r in _simpleParts) r.enabled = false;
                var hv = _havePrev && dt > 1e-4f ? (body - _prevBody) / dt : Vector3.zero; hv.y = 0f;
                _prevBody = body; _havePrev = true;
                bool grounded = (Shown.Flags & StateFlags.Grounded) != 0;
                if (grounded && GroundBelow(body, out var gy)) _model.BodyToGround = Mathf.Lerp(_model.BodyToGround, Mathf.Clamp(body.y - gy, 0.3f, 2.2f), 0.2f);
                _model.Act = Shown.Act; _model.Lamp = (Shown.Flags & StateFlags.Lamp) != 0;
                _model.CramponRest = (Shown.Flags & StateFlags.CramponRest) != 0;
                _model.AxeL = axeL; _model.AxeR = axeR; _model.AxeEulerL = ToV(Shown.AxeRotL); _model.AxeEulerR = ToV(Shown.AxeRotR);
                _model.Animate(body, Shown.Yaw, hv, (Shown.Flags & StateFlags.Grounded) != 0,
                    (Shown.Flags & StateFlags.GrabL) != 0, (Shown.Flags & StateFlags.GrabR) != 0, Shown.RopeId != 0);
                return;
            }
            Arm(_armL, body + _body.rotation * new Vector3(-0.2f, 0.42f, 0f), ToV(Shown.HandL));
            Arm(_armR, body + _body.rotation * new Vector3(0.2f, 0.42f, 0f), ToV(Shown.HandR));
            bool gl = (Shown.Flags & StateFlags.GrabL) != 0, gr = (Shown.Flags & StateFlags.GrabR) != 0;
            _handRendL.material.color = gl ? HandGrip : HandOpen; _handRendR.material.color = gr ? HandGrip : HandOpen;
        }


        static readonly RaycastHit[] _hits = new RaycastHit[8];
        bool GroundBelow(Vector3 body, out float y)
        {
            y = 0f; float best = float.MaxValue;
            int n = Physics.RaycastNonAlloc(body + Vector3.up * 0.2f, Vector3.down, _hits, 3f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.rigidbody != null && !h.rigidbody.isKinematic) continue; // the local player and loose physics objects
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return best < float.MaxValue;
        }

        /// <summary>Stretch a unit cylinder (height 2 along Y) between shoulder and hand.</summary>
        static void Arm(Transform arm, Vector3 shoulder, Vector3 hand)
        {
            var d = hand - shoulder; float len = d.magnitude;
            if (len < 1e-3f) { arm.localScale = Vector3.zero; return; }
            arm.position = shoulder + d * 0.5f;
            arm.rotation = Quaternion.FromToRotation(Vector3.up, d / len);
            arm.localScale = new Vector3(0.07f, len * 0.5f, 0.07f);
        }

        void LateUpdate()
        {
            var vp = GetComponent<VoicePlayer>();
            if (vp != null && Shown != null) vp.Emitter.position = ToV(Shown.Body) + Vector3.up * (_model != null ? Mathf.Max(0.3f, _model.HeadAboveBody) : 0.6f); // the voice comes from the head
            if (_model != null && Visible && Shown != null)
            {
                _model.BodyRef = ToV(Shown.Body);
                _model.Pose(ToV(Shown.HandL), ToV(Shown.HandR), Shown.Pitch,
                    (Shown.Flags & (StateFlags.ReachL | StateFlags.GrabL)) != 0, (Shown.Flags & (StateFlags.ReachR | StateFlags.GrabR)) != 0,
                    (Shown.Flags & StateFlags.GrabL) != 0, (Shown.Flags & StateFlags.GrabR) != 0, Time.deltaTime);
            }
        }

        void FixedUpdate()
        {
            if (Shown == null || !Visible) return;
            // the catch zone sits on the model's own palm (0.18 put it at the friend's first-person hand, ~0.3 m ahead)
            _rbL.MovePosition(AnchorHold(true, _model?.Palm(true) ?? ToV(Shown.HandL), _handL, ref _pinnedL, ref _pinL));
            _rbR.MovePosition(AnchorHold(false, _model?.Palm(false) ?? ToV(Shown.HandR), _handR, ref _pinnedR, ref _pinR));
        }

        bool _pinnedL, _pinnedR; Vector3 _pinL, _pinR;

        /// <summary>
        /// We stand on a stone and hold this hand: it stays where we caught it. The game puts our hand on this object every
        /// frame, and the friend's game puts THEIR hand on our networked hand, so following their hand meant following our
        /// own hand a round trip ago, plus the small offset of the model's palm each time: our hand crept toward our face in
        /// steps (0.26 trace). Pinned, the loop is broken; the friend hangs on our still hand. Free again once we let go of
        /// it or of the stone. Kept relative to our hand on the hold, so a moving hold (a skeleton, a branch) carries it along.
        /// </summary>
        Vector3 AnchorHold(bool left, Vector3 follow, Transform hand, ref bool pinned, ref Vector3 pin)
        {
            var at = HeldByMe != null && HeldByMe(this, left) && LocalHoldPoint != null ? LocalHoldPoint() : null;
            if (at == null) { if (pinned) { pinned = false; Plugin.Log.LogInfo($"{DisplayName}: {(left ? "left" : "right")} hand follows the network again"); } return follow; }
            if (!pinned) { pinned = true; pin = hand.position - at.Value; Plugin.Log.LogInfo($"{DisplayName}: we hold their {(left ? "left" : "right")} hand from a stone: it stays where it was caught"); }
            return at.Value + pin;
        }

        /// <summary>Where a rope this friend hangs on should end: the model's harness (pelvis), else just below the body point.</summary>
        public Vector3 RopeAnchorPoint => _model != null ? _model.PelvisPosition : ToV(Shown.Body) + Vector3.down * 0.25f;

        public static Vector3 ToV(V3 v) => new Vector3(v.x, v.y, v.z);
        public static V3 FromV(Vector3 v) => new V3(v.x, v.y, v.z);
    }
}
