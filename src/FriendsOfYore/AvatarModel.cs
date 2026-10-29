using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// Remote-player body built from the game's own Porter NPC (NPC_Stationary_Porter/ACTOR 1/Porter), cloned at runtime
    /// from the player's installed game: nothing is shipped with the mod. Legacy Animation clips drive walk/idle and
    /// fingers; a two-bone IK puts the hands exactly where the friend's grabbers are.
    /// </summary>
    public class AvatarModel
    {
        static readonly Dictionary<string, GameObject> _templates = new Dictionary<string, GameObject>();
        /// <summary>Every NPC clip seen so far. All NPC bodies share the NPC_RIG.003 skeleton, so any clip plays on any body.</summary>
        public static readonly Dictionary<string, AnimationClip> Clips = new Dictionary<string, AnimationClip>();
        public static bool HasTemplate => _templates.Count > 0;
        static readonly string[] PropWords = { "bagpack", "backpack", "alpenstock", "pipe", "npc_rope", "porter_ropes" };
        /// <summary>Bodies offered to players (owner's choice): the Cabin five plus Pearse 1/2 (Gray Gully) and the OAS climber.</summary>
        public static readonly string[] Allowed = { "NPC_PARCELFATHER", "NPC_ParcelBoy", "NPC_OASOfficial", "Walker", "Porter", "NPC_Pearse1", "NPC_Pearse2_fix", "OAS_Climber", Goat, Gull };
        /// <summary>Animals: their own mesh and animation, following the player's body (no arms to place).</summary>
        public const string Goat = "Goat", Gull = "Gull";
        public static bool IsCreatureKey(string k) => k == Goat || k == Gull;
        public static IEnumerable<string> Available => Allowed.Where(_templates.ContainsKey);
        public static bool HasModel(string key) => key != null && _templates.ContainsKey(key);


        /// <summary>
        /// Call after each scene load: keeps one stripped copy of every NPC body found (any object with an Animation and the
        /// NPC skeleton) and remembers all its clips. The Cabin has five bodies; DLC peaks add their own.
        /// </summary>
        public static void CaptureTemplates()
        {
            try { CaptureCreatures(); } catch (System.Exception e) { Plugin.Log.LogWarning("creature capture: " + e.Message); }
            try { FallSounds.CacheDoorClips(); } catch (System.Exception e) { Plugin.Log.LogWarning("door sounds: " + e.Message); }
            try { FallSounds.CacheGullClips(); } catch (System.Exception e) { Plugin.Log.LogWarning("gull cries: " + e.Message); }
            foreach (var anim in Resources.FindObjectsOfTypeAll<Animation>())
            {
                if (anim == null || !Allowed.Contains(anim.name)) continue; // generic capture also caught first-person arm rigs
                if (anim.transform.Find("NPC_RIG.003") == null && !anim.GetComponentsInChildren<Transform>(true).Any(t => t.name == "upperarm.l")) continue;
                var body = anim.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
                if (body == null) continue;
                foreach (AnimationState st in anim)
                    if (st.clip != null && !Clips.ContainsKey(st.name)) Clips[st.name] = st.clip;
                var key = anim.name;
                if (_templates.ContainsKey(key)) continue;
                var go = Object.Instantiate(anim.gameObject);
                go.SetActive(false);
                go.name = "FoY_Template_" + key;
                Object.DontDestroyOnLoad(go);
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c is Transform || c is Animation || c is Renderer || c is MeshFilter) continue;
                    Object.Destroy(c); // game scripts, audio, colliders, rigidbodies, cloth, particles
                }
                int props = 0;
                foreach (var t in go.GetComponentsInChildren<Transform>(true).ToArray())
                {
                    if (t == null || t == go.transform) continue;
                    var n = t.name.ToLowerInvariant();
                    // body parts are skinned meshes: only the rope coil may go; everything else listed is a rigid prop
                    if (t.GetComponent<SkinnedMeshRenderer>() != null && !n.Contains("ropes")) continue;
                    if (PropWords.Any(w => n.Contains(w))) { Object.DestroyImmediate(t.gameObject); props++; }
                }
                _templates[key] = go;
                Plugin.Log.LogInfo($"avatar template '{key}' captured from '{anim.gameObject.scene.name}' (body mesh {body.sharedMesh.name}), {props} props removed; clip library {Clips.Count}");
            }
        }

        /// <summary>For the background loader: this model is in ("GoatClips" = the goat's jump clips from Dunderhorn).</summary>
        public static bool Ready(string model) => model == "GoatClips" ? GoatHasClips : HasModel(model);

        /// <summary>The Cabin goat (alps_goat_idle) and a sea gull from any peak with gulls.</summary>
        static void CaptureCreatures()
        {
            if (_templates.TryGetValue(Goat, out var gt) && gt != null) AddGoatClips(gt.GetComponentInChildren<Animation>(true));
            if (!_templates.ContainsKey(Goat))
                foreach (var g in Resources.FindObjectsOfTypeAll<CabinGoatReact>())
                {
                    if (g == null || !g.gameObject.scene.IsValid()) continue;
                    var src = g.goatAnim != null ? g.goatAnim.gameObject : g.goatObj;
                    if (src == null || src.GetComponentInChildren<Renderer>(true) == null) continue;
                    StoreCreature(Goat, src); break;
                }
            if (!_templates.ContainsKey(Goat))
                foreach (var a in Resources.FindObjectsOfTypeAll<Animation>())
                {   // anything else that plays a goat clip
                    if (a == null || !a.gameObject.scene.IsValid() || a.GetComponentInChildren<SkinnedMeshRenderer>(true) == null) continue;
                    if (!a.Cast<AnimationState>().Any(st => st.name.ToLowerInvariant().Contains("alps_goat"))) continue; // the Alpine cabin's goat only (0.23.0's wall goat stayed invisible)
                    StoreCreature(Goat, a.gameObject); break;
                }
            if (!_templates.ContainsKey(Gull))
                foreach (var b in Resources.FindObjectsOfTypeAll<Bird>())
                {
                    if (b == null || !b.gameObject.scene.IsValid() || b.isCrow || b.birdTr == null) continue;
                    var src = b.birdAnim != null && b.birdTr.IsChildOf(b.birdAnim.transform) ? b.birdAnim.gameObject : b.birdTr.gameObject;
                    if (src.GetComponentInChildren<Renderer>(true) == null) continue;
                    StoreCreature(Gull, src); break;
                }
        }

        /// <summary>
        /// The Alpine cabin's goat (it shows up, 0.23.2) rebuilt the way the wall goats of Dunderhorn are put together, so
        /// it faces where the player looks (0.23.2 stood sideways) and can play their clips (Goat_Idle, Goat_Jump_short...):
        ///   FoY_Template_Goat (turned to the player's yaw) / goat (Animation, turned 90 deg, x2.2) / GoatArmature (= the cabin
        ///   goat's own object, x100) / root (the skeleton). The clips address "GoatArmature/root/..." - the same bones.
        /// </summary>
        static GameObject RigGoat(GameObject cabinGoat)
        {
            var wrap = new GameObject("FoY_Template_Goat"); wrap.SetActive(false); Object.DontDestroyOnLoad(wrap);
            var inner = new GameObject("goat"); inner.transform.SetParent(wrap.transform, false);
            inner.transform.localRotation = Quaternion.Euler(0f, 90f, 0f); inner.transform.localScale = Vector3.one * 2.2f;
            var old = cabinGoat.GetComponent<Animation>();
            var idle = old != null ? old.Cast<AnimationState>().Select(st => st.clip).FirstOrDefault(c => c != null && c.name == "alps_goat_idle") : null;
            if (old != null) Object.DestroyImmediate(old);
            cabinGoat.name = "GoatArmature";
            cabinGoat.transform.SetParent(inner.transform, false);
            cabinGoat.transform.localPosition = Vector3.zero; cabinGoat.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            cabinGoat.transform.localScale = Vector3.one * 100f;
            cabinGoat.SetActive(true);
            var anim = inner.AddComponent<Animation>();
            anim.playAutomatically = false;
            if (idle != null) anim.AddClip(idle, idle.name);
            AddGoatClips(anim);
            return wrap;
        }

        static readonly string[] GoatClipNames = { "Goat_Idle", "Goat_Idle1", "Goat_Jump_short", "Goat_Jump_long", "Goat_WallLick_L", "Goat_WallLick_R" };
        public static bool GoatHasClips => _templates.TryGetValue(Goat, out var g) && g != null && g.GetComponentInChildren<Animation>(true) is Animation a && a["Goat_Jump_short"] != null;

        /// <summary>The wall goats' clips (Alps2_3_Dunderhorn, preloaded from the cabins) put on our goat.</summary>
        static void AddGoatClips(Animation anim)
        {
            if (anim == null || anim["Goat_Jump_short"] != null) return;
            int n = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<AnimationClip>())
            {
                if (c == null || !c.legacy || !GoatClipNames.Contains(c.name) || anim[c.name] != null) continue;
                anim.AddClip(c, c.name); n++;
            }
            if (n > 0) Plugin.Log.LogInfo($"goat: {n} clip(s) of the wall goats added ({string.Join(", ", anim.Cast<AnimationState>().Select(st => st.name))})");
        }

        static void StoreCreature(string key, GameObject src)
        {
            var go = Object.Instantiate(src);
            go.SetActive(false); go.name = "FoY_Template_" + key; Object.DontDestroyOnLoad(go);
            go.transform.localScale = src.transform.lossyScale;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c is Transform || c is Animation || c is Animator || c is Renderer || c is MeshFilter) continue;
                Object.Destroy(c);
            }
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            if (key == Goat) go = RigGoat(go);
            _templates[key] = go;
            var clips = go.GetComponentInChildren<Animation>(true) is Animation a ? string.Join(", ", a.Cast<AnimationState>().Select(st => st.name)) : (go.GetComponentInChildren<Animator>(true) != null ? "animator" : "none");
            Plugin.Log.LogInfo($"avatar template '{key}' captured from '{src.scene.name}' ({src.name}, scale {go.transform.localScale.x:F2}, clips: {clips})");
        }

        public bool IsCreature => IsCreatureKey(Kind);
        /// <summary>Resting on crampons: both knees bent the same (owner's choice), not the jump pose.</summary>
        public bool CramponRest;

        /// <summary>Middle of the model's palm (wrist bone + a bit toward the fingers), or null for animals.</summary>
        public Vector3? Palm(bool left)
        {
            var h = left ? _handL : _handR; var f = left ? _foreL : _foreR;
            if (IsCreature || h == null || f == null) return null;
            var dir = (h.position - f.position); if (dir.sqrMagnitude < 1e-6f) return h.position;
            return h.position + dir.normalized * 0.07f;
        }

        // ---------------------------------------------------------------- creatures as player bodies (owner's design, 0.23)
        // Goat: one goat (the Alpine cabin's) that lives on the player's hands - it jumps in an arc onto every hold the player
        // grabs and stands there; on the ground it stands idle where the player stands. The game's jump clip belongs to the
        // wall goats of Dunderhorn, whose copy never showed up (0.23.0/0.23.1), so the jump is drawn by the mod.
        // Gull: two gulls in place of the hands - perched while that hand holds something, flying while it is free.
        Vector3 _cBody; float _cYaw, _cSpeed, _walkPhase; bool _cGrounded;
        Transform _gull2; Animator _gullA, _gullB;
        Vector3 _goatPos, _jumpFrom, _jumpTo; bool _goatHave; float _jumpT = -1f, _jumpDur = 0.5f, _goatYaw;
        int _goatSide = -1, _goatMode = -2; bool _pHoldL, _pHoldR;

        void AnimateCreature(Vector3 bodyPos, float yaw, Vector3 horizVel, bool grounded)
        {
            _cBody = bodyPos; _cYaw = yaw; _cGrounded = grounded; _cSpeed = horizVel.magnitude;
        }

        void PoseCreature(Vector3 handL, Vector3 handR, bool holdL, bool holdR, float dt)
        {
            if (Kind == Gull) PoseGulls(handL, handR, holdL, holdR, dt); else PoseGoat(handL, handR, holdL, holdR, dt);
        }

        void PoseGulls(Vector3 handL, Vector3 handR, bool holdL, bool holdR, float dt)
        {
            if (_gullA == null) _gullA = Root.GetComponentInChildren<Animator>(true);
            var side = Quaternion.Euler(0f, _cYaw, 0f) * Vector3.right;
            if ((handR - handL).sqrMagnitude < 0.04f) { handL -= side * 0.25f; handR += side * 0.25f; } // Look tab preview: hands at one point
            float bob = Mathf.Sin(Time.time * 6f) * 0.04f;
            Root.transform.position = handL + (holdL ? Vector3.zero : Vector3.up * bob);
            Root.transform.rotation = Quaternion.Euler(0f, _cYaw, 0f);
            SetGull(_gullA, holdL);
            if (_gull2 != null)
            {
                _gull2.position = handR + (holdR ? Vector3.zero : Vector3.up * -bob);
                _gull2.rotation = Quaternion.Euler(0f, _cYaw, 0f);
                SetGull(_gullB, holdR);
            }
        }

        static void SetGull(Animator a, bool perched)
        {
            if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return;
            a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (var p in a.parameters)
            {
                if (p.type != AnimatorControllerParameterType.Bool) continue;
                if (p.name == "rest") a.SetBool(p.name, perched);
                else if (p.name == "flypatrol") a.SetBool(p.name, !perched);
                else if (p.name == "glide") a.SetBool(p.name, false);
            }
        }

        void PoseGoat(Vector3 handL, Vector3 handR, bool holdL, bool holdR, float dt)
        {
            // which hand's hold the goat is on: the one grabbed last, while it still holds
            if (holdL && !_pHoldL) _goatSide = 0;
            if (holdR && !_pHoldR) _goatSide = 1;
            if (_goatSide == 0 && !holdL) _goatSide = holdR ? 1 : -1;
            if (_goatSide == 1 && !holdR) _goatSide = holdL ? 0 : -1;
            _pHoldL = holdL; _pHoldR = holdR;
            var fwd = Quaternion.Euler(0f, _cYaw, 0f) * Vector3.forward;
            Vector3 ground = new Vector3(_cBody.x, _cBody.y - BodyToGround, _cBody.z);
            Vector3 target = _goatSide == 0 ? handL - fwd * 0.3f : _goatSide == 1 ? handR - fwd * 0.3f : ground;
            _offGroundT = _cGrounded ? 0f : _offGroundT + dt; // a step off a bump is not a flight
            int mode = _goatSide >= 0 ? _goatSide : (_offGroundT < 0.2f ? -1 : -3);
            if (!_goatHave) { _goatPos = target; _goatHave = true; _goatYaw = _cYaw; _goatMode = mode; }
            if (mode != _goatMode)
            {   // a new hold (or let go onto the ground): jump there, like the game's goats do between their perches
                bool fromAir = _goatMode == -3;
                _goatMode = mode;
                float d = Vector3.Distance(_goatPos, target);
                if (mode == -3) { _airT = 0f; _jumpT = -1f; } // the player is in the air: the goat flies with them (long jump pose)
                else if (d > 0.35f || fromAir)
                {
                    _jumpFrom = _goatPos; _jumpT = 0f;
                    // landing after a flight: the second half of the long jump; a far hold: the long jump; else the short one
                    _jumpClip = fromAir || d > 1.5f ? "Goat_Jump_long" : "Goat_Jump_short";
                    _jumpStartT = fromAir ? Mathf.Min(0.5f, _airClipT) : 0f;
                    _jumpH = fromAir ? 0.05f + d * 0.1f : 0.25f + d * 0.2f;
                    _jumpDur = fromAir ? Mathf.Clamp(0.25f + d * 0.08f, 0.3f, 0.6f) : Mathf.Clamp(0.35f + d * 0.12f, 0.4f, 0.9f);
                    var flat = target - _goatPos; flat.y = 0f;
                    if (flat.sqrMagnitude > 0.04f) _goatYaw = Quaternion.LookRotation(flat).eulerAngles.y;
                }
            }
            if (_jumpT >= 0f)
            {
                _jumpTo = target;
                _jumpT += dt / _jumpDur;
                float t = Mathf.Clamp01(_jumpT), e = t * t * (3f - 2f * t);
                _goatPos = Vector3.Lerp(_jumpFrom, _jumpTo, e) + Vector3.up * (_jumpH * 4f * t * (1f - t));
                bool clip = GoatClip(_jumpClip, Mathf.Lerp(_jumpStartT, 1f, t));
                _jumpPitch = clip ? 0f : -22f * Mathf.Sin(t * Mathf.PI * 2f); // no clip: nose up on take-off, down on landing
                if (_jumpT >= 1f) { _jumpT = -1f; _jumpPitch = 0f; GoatClip(null, 0f); }
            }
            else if (mode == -3)
            {   // in the air with the player: the long jump, held in its flight pose
                _goatPos = Vector3.Lerp(_goatPos, target, 1f - Mathf.Exp(-14f * dt));
                _airT += dt;
                _airClipT = Mathf.Min(0.5f, _airT / 0.35f * 0.5f);
                GoatClip("Goat_Jump_long", _airClipT);
            }
            else
            {
                _goatPos = Vector3.Lerp(_goatPos, target, 1f - Mathf.Exp(-14f * dt)); // walking along with the player
                if (mode == -1 && _cSpeed > 0.6f)
                {   // walking: one short jump after another (owner's choice), faster when the player runs
                    _walkPhase = Mathf.Repeat(_walkPhase + dt * Mathf.Clamp(_cSpeed / 1.8f, 0.8f, 2.2f), 1f);
                    GoatClip("Goat_Jump_short", _walkPhase);
                    Root.transform.position = _goatPos + Vector3.up * (0.12f * Mathf.Sin(_walkPhase * Mathf.PI));
                    var flat = Quaternion.Euler(0f, _cYaw, 0f);
                    _goatYaw = Mathf.LerpAngle(_goatYaw, flat.eulerAngles.y, 1f - Mathf.Exp(-6f * dt));
                    Root.transform.rotation = Quaternion.Euler(0f, _goatYaw, 0f);
                    return;
                }
                _walkPhase = 0f;
                GoatClip(null, 0f);
                if (mode == -1) _goatYaw = Mathf.LerpAngle(_goatYaw, _cYaw, 1f - Mathf.Exp(-4f * dt));
            }
            Root.transform.position = _goatPos;
            Root.transform.rotation = Quaternion.Euler(_jumpPitch, _goatYaw, 0f);
            if (!_goatLogged && (_goatSeenAt += dt) > 3f)
            {
                _goatLogged = true;
                var rs = Root.GetComponentsInChildren<Renderer>(true);
                Plugin.Log.LogInfo($"goat body check: active={Root.activeInHierarchy} at {Root.transform.position} (player {_cBody}), renderers " +
                    string.Join(", ", rs.Select(r => $"{r.name} on={r.enabled}/{r.gameObject.activeInHierarchy} seen={r.isVisible} size {r.bounds.size}")));
            }
        }
        float _jumpPitch, _goatSeenAt, _offGroundT, _airT, _airClipT, _jumpStartT, _jumpH = 0.3f; bool _goatLogged; string _jumpClip = "Goat_Jump_short";
        static readonly string[] JumpClips = { "Goat_Jump_short", "Goat_Jump_long" };

        bool _goatInit;
        /// <summary>Holds one jump clip at time t (the game drives them the same way: speed 0, time from a curve); the other fades out.
        /// clip null = no jump, idle only. Returns false when the clip is missing.</summary>
        bool GoatClip(string clip, float t)
        {
            if (_anim == null) return false;
            if (!_goatInit)
            {
                _goatInit = true;
                var names = _anim.Cast<AnimationState>().Select(st => st.name).ToList();
                _creatureIdle = names.FirstOrDefault(n => n == "Goat_Idle") ?? names.FirstOrDefault(n => n.ToLowerInvariant().Contains("idle") && !n.ToLowerInvariant().Contains("react")) ?? names.FirstOrDefault() ?? "";
                foreach (AnimationState st in _anim) st.layer = 0;
                foreach (var jn in JumpClips) { var j = _anim[jn]; if (j != null) { j.layer = 1; j.speed = 0f; j.wrapMode = WrapMode.ClampForever; } }
                if (_creatureIdle != "") { _anim[_creatureIdle].wrapMode = WrapMode.Loop; _anim.Play(_creatureIdle); _bodyClip = _creatureIdle; }
                Plugin.Log.LogInfo($"goat body: idle={_creatureIdle}, jumps={string.Join("/", JumpClips.Where(n => _anim[n] != null))} ({string.Join(", ", names)})");
            }
            bool found = false;
            foreach (var jn in JumpClips)
            {
                var jump = _anim[jn]; if (jump == null) continue;
                if (jn == clip)
                {
                    found = true;
                    if (!jump.enabled) { jump.enabled = true; }
                    jump.weight = 1f; jump.normalizedTime = t;
                }
                else if (jump.enabled) { jump.weight = Mathf.MoveTowards(jump.weight, 0f, Time.deltaTime * 6f); if (jump.weight <= 0f) jump.enabled = false; }
            }
            return found;
        }
        string _creatureIdle;

        public readonly GameObject Root;
        readonly Animation _anim;
        public string Kind;
        readonly Transform _upL, _foreL, _handL, _upR, _foreR, _handR, _head, _spine, _pelvis, _hipL, _hipR;
        readonly float _pelvisAboveSoles = 0.95f;
        GameObject _axeL, _axeR;
        public Vector3 PelvisPosition => _pelvis != null ? _pelvis.position : Root.transform.position + Vector3.up * _pelvisAboveSoles;
        readonly Transform[] _feet;
        readonly Quaternion _restUpL, _restForeL, _restUpR, _restForeR, _restHead;
        float _rootY; bool _haveRootY;
        public float BodyToGround = 0.9f; // learned while grounded
        string _bodyClip = "";
        bool _gripL, _gripR, _firstGrip = true;

        AvatarModel(GameObject root)
        {
            Root = root;
            _anim = root.GetComponent<Animation>(); if (_anim == null) _anim = root.GetComponentInChildren<Animation>(true); // the goat's is one level down
            Transform B(string n) => root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
            _upL = B("upperarm.l"); _foreL = B("forearm.l"); _handL = B("hand.l");
            _upR = B("upperarm.r"); _foreR = B("forearm.r"); _handR = B("hand.r");
            _head = B("head"); _spine = B("spine2");
            _feet = new[] { B("foot.l"), B("foot.r"), B("toe.l"), B("toe.r") }.Where(t => t != null).ToArray();
            _pelvis = B("pelvis"); _hipL = B("hip.l"); _hipR = B("hip.r");
            _thighL = B("thigh.l"); _thighR = B("thigh.r"); _calfL = B("calf.l"); _calfR = B("calf.r");
            if (_thighL) _restThighL = _thighL.localRotation; if (_thighR) _restThighR = _thighR.localRotation;
            if (_calfL) _restCalfL = _calfL.localRotation; if (_calfR) _restCalfR = _calfR.localRotation;
            if (_pelvis != null && _feet.Length > 0) _pelvisAboveSoles = _pelvis.position.y - (_feet.Min(f => f.position.y) - 0.04f);
            if (_upL) _restUpL = _upL.localRotation; if (_foreL) _restForeL = _foreL.localRotation;
            if (_upR) _restUpR = _upR.localRotation; if (_foreR) _restForeR = _foreR.localRotation;
            if (_head) _restHead = _head.localRotation;
            if (_anim != null)
            {
                _anim.cullingType = AnimationCullingType.AlwaysAnimate;
                foreach (AnimationState st in _anim) st.layer = 0;
                Finger("NPC_Grab0", 1, _handL); Finger("NPC_OpenHand0", 1, _handL);
                Finger("NPC_Grab1", 2, _handR); Finger("NPC_OpenHand1", 2, _handR);
            }
        }

        void Finger(string clip, int layer, Transform hand)
        {
            var st = _anim[clip];
            if (st == null || hand == null) return;
            st.layer = layer;
            st.AddMixingTransform(hand, true); // fingers only: never freezes legs/arms
        }

        public static AvatarModel Spawn(Transform parent, string preferred)
        {
            if (!HasTemplate) return null;
            var key = HasModel(preferred) ? preferred : Available.First();
            var go = Object.Instantiate(_templates[key], parent, false);
            go.name = "Model_" + key;
            go.SetActive(true);
            var anim = go.GetComponent<Animation>();
            if (anim != null && !IsCreatureKey(key)) foreach (var kv in Clips) if (anim[kv.Key] == null) anim.AddClip(kv.Value, kv.Key);
            var m = new AvatarModel(go) { Kind = key };
            if (key == Gull)
            {   // a second gull for the other hand
                var g2 = Object.Instantiate(_templates[key], go.transform, false);
                g2.name = "Gull_R"; g2.SetActive(true);
                g2.transform.localScale = Vector3.one;
                foreach (var r in g2.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                m._gull2 = g2.transform; m._gullB = g2.GetComponentInChildren<Animator>(true);
                m._gullA = go.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => !a.transform.IsChildOf(g2.transform));
            }
            if (m.IsCreature) return m;

            if (m._upL == null || m._foreL == null || m._handL == null || m._upR == null || m._handR == null)
            {
                Plugin.Log.LogWarning("avatar: rig bones not found, using simple avatar");
                Object.Destroy(go); return null;
            }
            return m;
        }

        bool Has(string clip) => _anim != null && _anim[clip] != null;
        string _idleClip, _walkClip;
        void FindClips()
        {
            if (_anim == null) return;
            var names = _anim.Cast<AnimationState>().Select(st => st.name).ToList();
            _walkClip = names.FirstOrDefault(n => n == "NPC_WALK") ?? names.FirstOrDefault(n => n.ToUpperInvariant().Contains("WALK") && !n.ToUpperInvariant().Contains("FAKE"))
                        ?? names.FirstOrDefault(n => n.ToUpperInvariant().Contains("WALK"));
            _idleClip = names.FirstOrDefault(n => n == "NPC_IDLE0_old") ?? names.FirstOrDefault(n => n.ToUpperInvariant().Contains("IDLE") && !n.ToUpperInvariant().Contains("SIT"));
            Plugin.Log.LogInfo($"avatar {Kind}: {names.Count} clips, idle={_idleClip ?? "-"} walk={_walkClip ?? "-"}");
        }

        /// <summary>Called from Update: animation choice. Applies before LateUpdate IK.</summary>
        public void Animate(Vector3 bodyPos, float yaw, Vector3 horizVel, bool grounded, bool gripL, bool gripR, bool onRope = false)
        {
            if (IsCreature) { AnimateCreature(bodyPos, yaw, horizVel, grounded); return; }
            Root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            // where the soles should be; on a rope the harness (pelvis) hangs at the body point, where the rope ends
            bool resting = Act == FriendsOfYore.Net.Activity.Bivouac && !string.IsNullOrEmpty(_sitClip); // sit only if a sitting clip exists
            float target = onRope ? bodyPos.y - 0.05f - _pelvisAboveSoles : bodyPos.y - BodyToGround;
            _resting = resting;
            // sitting in a bivouac: the pelvis goes just above the floor a standing player would have (0.7 put it on the tent)
            _restPelvisY = bodyPos.y - BodyToGround + 0.08f;
            if (!_haveRootY) { _rootY = target; _haveRootY = true; }
            Root.transform.position = new Vector3(bodyPos.x, _rootY, bodyPos.z);
            _wantSoleY = target;
            if (_anim == null) return;
            if (_firstGrip) FindClips();
            _grounded = grounded || Act == FriendsOfYore.Net.Activity.Bivouac;
            UpdateSitting();
            string want = grounded && horizVel.magnitude > 0.6f && _walkClip != null ? _walkClip : _idleClip ?? "";
            if (want != "" && want != _bodyClip) { _anim.CrossFade(want, 0.25f); _bodyClip = want; }
            if (want != "" && want == _walkClip) _anim[_walkClip].speed = Mathf.Clamp(horizVel.magnitude / 1.6f, 0.5f, 1.6f);
            if (_firstGrip || gripL != _gripL) { var c = gripL ? "NPC_Grab0" : "NPC_OpenHand0"; if (Has(c)) _anim.CrossFade(c, 0.15f); _gripL = gripL; }
            if (_firstGrip || gripR != _gripR) { var c = gripR ? "NPC_Grab1" : "NPC_OpenHand1"; if (Has(c)) _anim.CrossFade(c, 0.15f); _gripR = gripR; }
            _firstGrip = false;
        }

        float _wantSoleY;
        bool _grounded = true;
        bool _resting; float _restPelvisY;
        float _legW;             // 0 = legs as animated, 1 = knees fully bent (eased, not switched)

        // ---- ice axes: a copy of the game's own ice axe (wooden shaft + metal head) ----
        static GameObject _axeTemplate;
        static Vector3 _axeGripLocal, _axeHeadDirLocal = Vector3.up;
        static Transform _localAxeRootL, _localAxeRootR, _localHandL, _localHandR;
        // Each hand has two first-person axe meshes: the swinging one under the "mover" and the one shown while the axe is
        // stuck in the ice (jointIceAxeMesh). 0.9 sent the mover's rotation, which freezes (hidden) while the axe is planted.
        static Transform _moverShaftL, _moverShaftR, _jointShaftL, _jointShaftR;
        static Matrix4x4 _relL = Matrix4x4.identity, _relR = Matrix4x4.identity; // axe root -> shaft, at capture
        /// <summary>How far the hand sits up the shaft from the axe's pivot end (m), and how far right of it (owner tuning).</summary>
        public static float AxeSlide = 0.12f, AxeSide = 0.03f;

        /// <summary>
        /// Finds the local player's axe roots (parents of "iceaxe_mesh" + "IceAxe_metal"). 0.5.0 picked the mesh with the
        /// most vertices, which was the first-person "handclutch" (big hands) or the shaft without its head.
        /// </summary>
        public static void CaptureAxe(IceAxe ia)
        {
            if (ia == null) return;
            var shafts = ia.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name == "iceaxe_mesh" && f.transform.parent != null).ToList();
            if (shafts.Count == 0) { Plugin.Log.LogInfo("ice axe: no iceaxe_mesh under IceAxe"); return; }
            MeshFilter ShaftUnder(GameObject g) => g == null ? null : shafts.FirstOrDefault(f => f.transform.IsChildOf(g.transform));
            var moverR = AccessTools.Field(typeof(IceAxe), "iceAxeMoverGOR")?.GetValue(ia) as GameObject;
            var sL = ShaftUnder(ia.iceAxeMoverGOL) ?? shafts[0];
            var sR = ShaftUnder(moverR) ?? shafts.FirstOrDefault(f => f != sL && ShaftUnder(ia.jointIceAxeMeshL) != f && ShaftUnder(ia.jointIceAxeMeshR) != f) ?? sL;
            _moverShaftL = sL.transform; _moverShaftR = sR.transform;
            _jointShaftL = ShaftUnder(ia.jointIceAxeMeshL)?.transform; _jointShaftR = ShaftUnder(ia.jointIceAxeMeshR)?.transform;
            _localAxeRootL = _moverShaftL.parent; _localAxeRootR = _moverShaftR.parent;
            _relL = _localAxeRootL.worldToLocalMatrix * _moverShaftL.localToWorldMatrix;
            _relR = _localAxeRootR.worldToLocalMatrix * _moverShaftR.localToWorldMatrix;
            _localHandL = ia.handTrL; _localHandR = AccessTools.Field(typeof(IceAxe), "handTrR")?.GetValue(ia) as Transform;
            if (_axeTemplate != null) return;
            var root = _localAxeRootL;
            var go = new GameObject("FoY_IceAxeTemplate"); go.SetActive(false); Object.DontDestroyOnLoad(go);
            Vector3 head = Vector3.zero; bool haveHead = false;
            foreach (var f in root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name == "iceaxe_mesh" || f.name == "IceAxe_metal"))
            {
                var part = new GameObject(f.name);
                part.transform.SetParent(go.transform, false);
                part.transform.localPosition = root.InverseTransformPoint(f.transform.position);
                part.transform.localRotation = Quaternion.Inverse(root.rotation) * f.transform.rotation;
                part.transform.localScale = Div(f.transform.lossyScale, root.lossyScale);
                part.AddComponent<MeshFilter>().sharedMesh = f.sharedMesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = f.GetComponent<MeshRenderer>().sharedMaterials;
                if (f.name == "IceAxe_metal") { var r = f.GetComponent<MeshRenderer>(); head = root.InverseTransformPoint(r.bounds.center); haveHead = true; }
            }
            // The axe's pivot (root origin) sits at one end of the shaft; the hand goes AxeSlide metres up the shaft from it
            // (0.9 held it at the very end: the hand was over the head/leash, not the handle).
            var pivot = _localHandL != null && Vector3.Distance(_localHandL.position, root.position) < 0.6f ? root.InverseTransformPoint(_localHandL.position) : Vector3.zero;
            var mb = sL.sharedMesh != null ? sL.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one * 0.1f);
            var ext = mb.extents; var ax = ext.x >= ext.y && ext.x >= ext.z ? new Vector3(ext.x, 0, 0) : ext.y >= ext.z ? new Vector3(0, ext.y, 0) : new Vector3(0, 0, ext.z);
            var e1 = root.InverseTransformPoint(sL.transform.TransformPoint(mb.center + ax));
            var e2 = root.InverseTransformPoint(sL.transform.TransformPoint(mb.center - ax));
            var near = (e1 - pivot).sqrMagnitude < (e2 - pivot).sqrMagnitude ? e1 : e2; var far = near == e1 ? e2 : e1;
            _axeShaftDirLocal = (far - near).normalized;
            float worldPerLocal = Mathf.Max(1e-4f, root.lossyScale.x);
            _axeGripLocal = pivot + _axeShaftDirLocal * (AxeSlide / worldPerLocal);
            if (haveHead && (head - pivot).sqrMagnitude > 1e-4f) _axeHeadDirLocal = (head - pivot).normalized;
            _axeTemplate = go;
            Plugin.Log.LogInfo($"ice axe for friends: {go.transform.childCount} parts from '{root.name}', pivot {pivot}, grip {_axeGripLocal} (slide {AxeSlide:F2} m, shaft {(far - near).magnitude * worldPerLocal:F2} m), head dir {_axeHeadDirLocal}, joint meshes L={_jointShaftL != null} R={_jointShaftR != null}");
        }

        static Vector3 _axeShaftDirLocal = Vector3.up;

        static Vector3 Div(Vector3 a, Vector3 b) => new Vector3(b.x != 0 ? a.x / b.x : 1, b.y != 0 ? a.y / b.y : 1, b.z != 0 ? a.z / b.z : 1);

        /// <summary>
        /// Sender side: rotation and grip point of the axe the player actually sees (the planted one while stuck in ice),
        /// expressed as the template's root frame.
        /// </summary>
        public static bool LocalAxe(bool left, out Quaternion rot, out Vector3 grip)
        {
            var mover = left ? _moverShaftL : _moverShaftR; var joint = left ? _jointShaftL : _jointShaftR;
            var shaft = joint != null && joint.gameObject.activeInHierarchy ? joint : mover;
            rot = Quaternion.identity; grip = Vector3.zero;
            if (shaft == null) return false;
            var eff = shaft.localToWorldMatrix * (left ? _relL : _relR).inverse;
            rot = eff.rotation;
            grip = eff.MultiplyPoint3x4(_axeGripLocal);
            return true;
        }

        GameObject MakeAxe()
        {
            if (_axeTemplate == null) return null;
            var go = Object.Instantiate(_axeTemplate, Root.transform, false);
            go.name = "FoY_IceAxe"; go.SetActive(false);
            return go;
        }

        /// <summary>
        /// In use (swinging or stuck in ice): the friend's real axe rotation, gripped at the model's hand.
        /// Carried: hanging at the side, head up-forward.
        /// </summary>
        void PoseAxe(ref GameObject axe, Transform hand, bool show, bool inUse, Vector3 netEuler)
        {
            if (show && axe == null) axe = MakeAxe();
            if (axe == null) return;
            if (axe.activeSelf != show) axe.SetActive(show);
            if (!show) return;
            Quaternion rot;
            if (inUse) rot = Root.transform.rotation * Quaternion.Euler(netEuler); // sent relative to the player's facing (0.8: world rotation)
            else rot = Quaternion.AngleAxis(180f, Root.transform.up) * Quaternion.FromToRotation(_axeHeadDirLocal, (Root.transform.up * 0.75f + Root.transform.forward * 0.65f).normalized); // 0.6.0 pose turned half a turn
            axe.transform.rotation = rot;
            axe.transform.position += hand.position - axe.transform.TransformPoint(_axeGripLocal) + Root.transform.right * AxeSide;
        }

        Transform _thighL, _thighR, _calfL, _calfR;
        Quaternion _restThighL, _restThighR, _restCalfL, _restCalfR;

        /// <summary>
        /// Knees bent while off the ground (jump, wall, rope), eased in and out by w; the right leg bends less than the left.
        /// Blends from the animated pose toward a bend built on the rest pose, so nothing accumulates.
        /// </summary>
        void BendLegs(float w)
        {
            if (_thighL == null || _calfL == null || _thighR == null || _calfR == null || w <= 0.001f) return;
            var right = Root.transform.right;
            void Leg(Transform thigh, Transform calf, Quaternion restThigh, Quaternion restCalf, float amount)
            {
                var animThigh = thigh.localRotation; var animCalf = calf.localRotation;
                thigh.localRotation = restThigh; calf.localRotation = restCalf;
                thigh.rotation = Quaternion.AngleAxis(-55f * amount, right) * thigh.rotation;
                calf.rotation = Quaternion.AngleAxis(95f * amount, right) * calf.rotation;
                var bentThigh = thigh.localRotation; var bentCalf = calf.localRotation;
                thigh.localRotation = Quaternion.Slerp(animThigh, bentThigh, w);
                calf.localRotation = Quaternion.Slerp(animCalf, bentCalf, w);
            }
            Leg(_thighL, _calfL, _restThighL, _restCalfL, 1f);
            Leg(_thighR, _calfR, _restThighR, _restCalfR, CramponRest ? 1f : 0.5f);
        }

        float _wL, _wR;          // IK weight toward the networked hand (0 = arm hangs)
        Vector3 _climbShift;     // smoothed offset that lifts the body toward held holds

        /// <summary>Called from LateUpdate after the Animation component has posed the rig.</summary>
        public bool AxeL, AxeR;
        public Vector3 AxeEulerL, AxeEulerR;
        public FriendsOfYore.Net.Activity Act;
        public bool Lamp;
        GameObject _lampObj;

        // ---- oil lamp (Aldr Grotto): copy of the lamp mesh + light at the friend's belt ----
        static GameObject _lampTemplate;
        public static void CaptureLamp(OilLamp l)
        {
            if (_lampTemplate != null || l == null || l.oilLampLight == null) return;
            var go = new GameObject("FoY_LampTemplate"); go.SetActive(false); Object.DontDestroyOnLoad(go);
            if (l.oilLampMeshTr != null)
                foreach (var f in l.oilLampMeshTr.GetComponentsInChildren<MeshFilter>(true))
                {
                    var part = new GameObject(f.name); part.transform.SetParent(go.transform, false);
                    part.transform.localPosition = l.oilLampMeshTr.InverseTransformPoint(f.transform.position);
                    part.transform.localRotation = Quaternion.Inverse(l.oilLampMeshTr.rotation) * f.transform.rotation;
                    part.transform.localScale = Div(f.transform.lossyScale, l.oilLampMeshTr.lossyScale);
                    part.AddComponent<MeshFilter>().sharedMesh = f.sharedMesh;
                    var mr = f.GetComponent<MeshRenderer>(); if (mr != null) part.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                }
            var lightGo = new GameObject("light"); lightGo.transform.SetParent(go.transform, false);
            var src = l.oilLampLight; var li = lightGo.AddComponent<Light>();
            li.type = src.type; li.color = src.color; li.range = src.range; li.intensity = Mathf.Max(src.intensity, 1f); li.shadows = LightShadows.None;
            _lampTemplate = go;
            Plugin.Log.LogInfo($"oil lamp for friends: {go.transform.childCount - 1} mesh parts, light range {src.range:F1}");
        }

        void UpdateProps()
        {
            if (Lamp && _lampObj == null && _lampTemplate != null) { _lampObj = Object.Instantiate(_lampTemplate, Root.transform, false); _lampObj.name = "FoY_Lamp"; }
            if (_lampObj != null)
            {
                if (_lampObj.activeSelf != Lamp) _lampObj.SetActive(Lamp);
                if (Lamp) { _lampObj.transform.position = PelvisPosition + Root.transform.right * 0.22f - Root.transform.forward * 0.05f; _lampObj.transform.rotation = Root.transform.rotation; }
            }
        }

        string _sitClip;
        void UpdateSitting()
        {
            if (_anim == null) return;
            bool sit = Act == FriendsOfYore.Net.Activity.Bivouac;
            if (_sitClip == null) _sitClip = _anim.Cast<AnimationState>().Select(st => st.name).FirstOrDefault(n => n.ToUpperInvariant().Contains("SIT") && n.ToUpperInvariant().Contains("IDLE")) ?? "";
            if (_sitClip == "") return;
            var st0 = _anim[_sitClip];
            if (sit && st0.weight < 0.99f && !_anim.IsPlaying(_sitClip)) { st0.layer = 3; st0.wrapMode = WrapMode.Loop; _anim.CrossFade(_sitClip, 0.3f, PlayMode.StopSameLayer); }
            if (!sit && _anim.IsPlaying(_sitClip)) _anim.Blend(_sitClip, 0f, 0.3f);
        }

        public void Pose(Vector3 handL, Vector3 handR, float pitch, bool useL, bool useR, bool holdL, bool holdR, float dt)
        {
            if (IsCreature) { PoseCreature(handL, handR, holdL, holdR, dt); return; }
            try
            {
                PoseLimbs(handL, handR, pitch, useL, useR, holdL, holdR, dt);
                _legW = Mathf.MoveTowards(_legW, _grounded && !CramponRest ? 0f : 1f, dt * 3.5f);
                float eased = _legW * _legW * (3f - 2f * _legW);
                BendLegs(eased);
            }
            finally { PoseAxe(ref _axeL, _handL, AxeL, AxeL && useL, AxeEulerL); PoseAxe(ref _axeR, _handR, AxeR, AxeR && useR, AxeEulerR); UpdateProps(); }
        }

        void PoseLimbs(Vector3 handL, Vector3 handR, float pitch, bool useL, bool useR, bool holdL, bool holdR, float dt)
        {
            var t = Root.transform;
            // Feet: shift the model so its lowest foot bone sits on the sole height (smoothed, so walking doesn't bob).
            if (_resting && _pelvis != null)
            {
                float errP = _restPelvisY - _pelvis.position.y;
                _rootY += Mathf.Abs(errP) > 1.5f ? errP : errP * Mathf.Clamp01(dt * 12f);
            }
            else if (_feet.Length > 0)
            {
                float sole = _feet.Min(f => f.position.y) - 0.04f;
                float err = _wantSoleY - sole;
                _rootY += Mathf.Abs(err) > 1.5f ? err : err * Mathf.Clamp01(dt * 12f);
            }
            // Start from the rest pose every frame: clips that don't key these bones would otherwise let IK and the
            // head pitch accumulate frame after frame (the "spinning head" bug).
            _upL.localRotation = _restUpL; _foreL.localRotation = _restForeL;
            _upR.localRotation = _restUpR; if (_foreR) _foreR.localRotation = _restForeR;
            if (_head) _head.localRotation = _restHead;

            float armL = Vector3.Distance(_upL.position, _foreL.position) + Vector3.Distance(_foreL.position, _handL.position);
            float armR = Vector3.Distance(_upR.position, _foreR.position) + Vector3.Distance(_foreR.position, _handR.position);

            // Climbing: if the hands that hold something are out of reach, move the whole body toward them.
            // Bone positions here are UNshifted: Animate() put the root back at (x, _rootY, z) this frame.
            // (Bug fixed: subtracting _climbShift again double-counted it and overshot the shoulder above the hold.)
            var want = Vector3.zero; int n = 0;
            if (holdL) { want += Excess(_upL.position, handL, armL); n++; }
            if (holdR) { want += Excess(_upR.position, handR, armR); n++; }
            if (n > 0) want /= n;
            want = Vector3.ClampMagnitude(want, 1.6f);
            _climbShift = Vector3.Lerp(_climbShift, want, Mathf.Clamp01(dt * 10f));
            t.position = new Vector3(t.position.x, _rootY, t.position.z) + _climbShift;

            _wL = Mathf.MoveTowards(_wL, useL ? 1f : 0f, dt * 5f);
            _wR = Mathf.MoveTowards(_wR, useR ? 1f : 0f, dt * 5f);
            var fwd = t.forward; var right = t.right;
            // Hanging arms: hands a little in front of the hips; elbows point back.
            var hangL = _upL.position + Vector3.down * armL * 0.93f + fwd * 0.08f - right * 0.06f;
            var hangR = _upR.position + Vector3.down * armR * 0.93f + fwd * 0.08f + right * 0.06f;
            var tgtL = Vector3.Lerp(hangL, WristFor(_upL.position, handL), _wL);
            var tgtR = Vector3.Lerp(hangR, WristFor(_upR.position, handR), _wR);
            TwoBone(_upL, _foreL, _handL, tgtL, _foreL.position - fwd * 0.35f + Vector3.down * 0.2f - right * 0.15f);
            TwoBone(_upR, _foreR, _handR, tgtR, _foreR.position - fwd * 0.35f + Vector3.down * 0.2f + right * 0.15f);
            if (_head) _head.rotation = Quaternion.AngleAxis(Mathf.Clamp(pitch, -60f, 60f) * 0.7f, right) * _head.rotation;
        }
        /// <summary>Head height above the networked body point, for placing name labels.</summary>
        public float HeadAboveBody => _head != null ? _head.position.y - BodyRef.y : 0.5f;
        public Vector3 BodyRef;

        /// <summary>The player's grabber is the palm; the rig's hand bone is the wrist, ~8 cm closer to the shoulder.</summary>
        static Vector3 WristFor(Vector3 shoulder, Vector3 palm) => palm - (palm - shoulder).normalized * 0.08f;

        static Vector3 Excess(Vector3 shoulder, Vector3 target, float reach)
        {
            var d = target - shoulder; float m = d.magnitude, max = reach * 0.97f;
            return m > max ? d / m * (m - max) : Vector3.zero;
        }

        /// <summary>Analytic two-bone IK; rotates bones by world-space deltas, so local bone axes don't matter.</summary>
        static void TwoBone(Transform a, Transform b, Transform c, Vector3 target, Vector3 hint)
        {
            if (a == null || b == null || c == null) return;
            float la = Vector3.Distance(a.position, b.position), lb = Vector3.Distance(b.position, c.position);
            var toT = target - a.position;
            float d = Mathf.Clamp(toT.magnitude, 0.05f, (la + lb) * 0.999f);
            var dir = toT.normalized;
            var bend = Vector3.ProjectOnPlane(hint - a.position, dir);
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(Vector3.down, dir);
            bend.Normalize();
            float cosA = Mathf.Clamp((la * la + d * d - lb * lb) / (2f * la * d), -1f, 1f);
            var elbow = a.position + dir * (la * cosA) + bend * (la * Mathf.Sqrt(1f - cosA * cosA));
            a.rotation = Quaternion.FromToRotation(b.position - a.position, elbow - a.position) * a.rotation;
            b.rotation = Quaternion.FromToRotation(c.position - b.position, target - b.position) * b.rotation;
        }
    }
}
