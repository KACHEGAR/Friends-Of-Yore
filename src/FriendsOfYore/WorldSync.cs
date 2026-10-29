using System;
using System.Collections.Generic;
using System.Linq;
using FriendsOfYore.Net;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FriendsOfYore
{
    /// <summary>
    /// Shares the world between players. Field/method names from Assembly-CSharp sha256 99e47bda.
    /// 1. Obstacle state: each client polls the game's obstacle scripts and broadcasts any change (including partial wear:
    ///    crumbling scale, pop-out progress, snow scale); receivers set the same state. Objects are keyed by sibling-index
    ///    path in the scene (+ component index), identical on every client.
    /// 2. Physics props (branches, hanging bodies, grabbable gulls) and flushed birds: whoever moves them streams their
    ///    pose; others follow kinematically.
    /// 3. Falling rocks/icicles (FallingRock.InitialiseRock) and bivouacs.
    /// </summary>
    public class WorldSync
    {
        readonly Plugin _p;
        float _scanT, _resendT, _refreshT, _propT, _bivT;
        string _scene;
        ITransport _net;
        public static WorldSync Instance;

        readonly Dictionary<string, Component> _objs = new Dictionary<string, Component>();
        readonly Dictionary<string, int> _state = new Dictionary<string, int>();
        readonly Dictionary<string, WorldEvent> _changed = new Dictionary<string, WorldEvent>();

        public WorldSync(Plugin p) { _p = p; Instance = this; }

        public void OnSceneLoaded(Scene loaded)
        {
            _objs.Clear(); _state.Clear(); _changed.Clear(); _refreshT = 0f; _scene = null;
            _keyOf.Clear(); _byKey.Clear(); _snapDone = false; _pending.Clear(); _attackSince.Clear();
            // Keys are taken right here, in sceneLoaded (after Awake, before any Start), identically on every PC.
            // 0.8 took them "1 s after loading" but an early rock or prop forced them sooner on one PC only.
            Snapshot(loaded);
            CaptureClockWheels(loaded);
            _anims = null; _ownedCap.Clear();
            _rockInit.Clear(); _rockRemote.Clear(); _owned.Clear(); _following.Clear(); _birdOwned.Clear(); _bivMirrors.Clear(); _myBiv = null; _bivSent = 0;
        }

        // ------------------------------------------------------------------ keys and reflection helpers
        // Keys are "name@position" taken while everything is still at rest: in sceneLoaded, and again 1.5/4/8 s later for
        // objects the level builds itself (the Alps peaks create their trees, vines, mills and beams from "ReplaceMe"
        // markers). 0.8-0.20 used scene paths (sibling indexes); the Alps objects had different paths on the two PCs, so
        // a bending beam on one PC moved the barometer on the other ("pr:0").
        readonly Dictionary<Transform, string> _keyOf = new Dictionary<Transform, string>();
        readonly Dictionary<string, Transform> _byKey = new Dictionary<string, Transform>();
        bool _snapDone; float _sceneLoadedAt; int _lateSnaps;

        static string PosKey(Transform t)
        {
            var p = t.position;
            return $"n{t.name}@{Mathf.RoundToInt(p.x * 10)},{Mathf.RoundToInt(p.y * 10)},{Mathf.RoundToInt(p.z * 10)}";
        }

        void AddKey(Transform t)
        {
            if (t == null || _keyOf.ContainsKey(t) || t.name.StartsWith("FoY")) return;
            var k = PosKey(t);
            if (_byKey.ContainsKey(k)) { int d = 1; while (_byKey.ContainsKey(k + "~" + d)) d++; k += "~" + d; }
            _keyOf[t] = k; _byKey[k] = t;
        }

        void Walk(Transform t) { AddKey(t); for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i)); }

        void Snapshot() => Snapshot(SceneManager.GetActiveScene());
        void Snapshot(Scene scene)
        {
            _keyOf.Clear(); _byKey.Clear(); _snapDone = true; _sceneLoadedAt = Time.unscaledTime; _lateSnaps = 0;
            if (!scene.IsValid() || !scene.isLoaded) return;
            foreach (var r in scene.GetRootGameObjects()) Walk(r.transform);
            Plugin.Log.LogInfo($"world keys: {_keyOf.Count} objects in {scene.name}");
        }

        /// <summary>Objects created after loading (level builders, playmode copies), in every loaded scene incl. DontDestroyOnLoad.</summary>
        void LateSnapshot()
        {
            int before = _keyOf.Count;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded || sc.name == GameAccess.Preloading) continue;
                foreach (var r in sc.GetRootGameObjects()) Walk(r.transform);
            }
            var ddol = _p.gameObject.scene; // the plugin lives in DontDestroyOnLoad
            if (ddol.IsValid()) foreach (var r in ddol.GetRootGameObjects()) if (r != _p.gameObject && !r.name.StartsWith("FoY") && !r.name.StartsWith("BepInEx")) Walk(r.transform);
            if (_keyOf.Count != before) Plugin.Log.LogInfo($"world keys: +{_keyOf.Count - before} objects built after loading");
        }

        static string Path(Transform t)
        {
            var w = Instance; if (!w._snapDone) w.Snapshot();
            if (w._keyOf.TryGetValue(t, out var k)) return k;
            w.AddKey(t); // spawned later still: name + where it is first seen
            return w._keyOf.TryGetValue(t, out k) ? k : PosKey(t);
        }

        /// <summary>Key + index among same-type components on that object (several SnowOnIce shared one key in 0.5.0).</summary>
        static string Key(Component c, string prefix)
        {
            var same = c.GetComponents(c.GetType());
            return prefix + ":" + Path(c.transform) + (same.Length > 1 ? "#" + Array.IndexOf(same, c) : "");
        }

        internal static string KeyOf(Transform t) => Path(t);

        static Transform Resolve(string path)
        {
            var w = Instance; if (!w._snapDone) w.Snapshot();
            if (w._byKey.TryGetValue(path, out var t) && t != null) return t;
            if (!path.StartsWith("n") || path.IndexOf('@') < 0) return null;
            // not known here: the same name nearest to the position the friend saw it at
            int at = path.LastIndexOf('@'); var name = path.Substring(1, at - 1);
            var tail = path.Substring(at + 1); int tilde = tail.IndexOf('~'); if (tilde >= 0) tail = tail.Substring(0, tilde);
            var c = tail.Split(',');
            if (c.Length != 3 || !int.TryParse(c[0], out var x) || !int.TryParse(c[1], out var y) || !int.TryParse(c[2], out var z)) return null;
            var pos = new Vector3(x / 10f, y / 10f, z / 10f);
            var best = UnityEngine.Object.FindObjectsOfType<Transform>().Where(o => o.name == name)
                .OrderBy(o => (o.position - pos).sqrMagnitude).FirstOrDefault();
            if (best == null || (best.position - pos).sqrMagnitude > 4f) return null;
            w._byKey[path] = best; if (!w._keyOf.ContainsKey(best)) w._keyOf[best] = path;
            return best;
        }

        static T F<T>(object o, string name) { var f = AccessTools.Field(o.GetType(), name); return f == null ? default : (T)f.GetValue(o); }
        static void S(object o, string name, object v) => AccessTools.Field(o.GetType(), name)?.SetValue(o, v);

        /// <summary>
        /// Finds the shared obstacles, including switched-off ones (0.8 lost snow that had melted away and anything the
        /// game had hidden, because FindObjectsOfType skips inactive objects); known objects are never dropped.
        /// </summary>
        void Refresh()
        {
            foreach (var k in _objs.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) _objs.Remove(k);
            var scene = SceneManager.GetActiveScene();
            void Add<T>(string prefix) where T : Component
            {
                foreach (var c in Resources.FindObjectsOfTypeAll<T>())
                {
                    if (c == null || !c.gameObject.scene.IsValid() || !c.gameObject.scene.isLoaded || c.gameObject.scene.name == GameAccess.Preloading) continue;
                    var k = Key(c, prefix);
                    if (_objs.ContainsKey(k)) continue;
                    _objs[k] = c;
                    if (!_state.ContainsKey(k)) _state[k] = Read(c);
                    if (_pending.TryGetValue(k, out var ev)) { _pending.Remove(k); ApplyNow(c, ev); }
                }
            }
            Add<CrumblingHold>("ch"); Add<BrickHold>("bh"); Add<CrumblingHoldRegular>("cr"); Add<Shrubbery>("sh");
            Add<Bird>("bd"); Add<BrittleIce>("bi"); Add<SnowOnIce>("sn");
        }

        // ------------------------------------------------------------------ obstacle state
        // Encodings: 1000 = finished (popped/crumbled/gone); otherwise remaining "health" in hundredths.
        const int Gone = 1000;

        static int Read(Component c)
        {
            if (c == null) return -1;
            switch (c)
            {
                case CrumblingHold ch: return F<bool>(ch, "poppedOut") ? Gone : Mathf.Clamp(Mathf.RoundToInt(ch.popoutDuration * 20f), 0, Gone - 1);
                case BrickHold bh: return F<bool>(bh, "poppedOut") ? Gone : Mathf.Clamp(Mathf.RoundToInt(bh.popoutDuration * 20f), 0, Gone - 1);
                case CrumblingHoldRegular cr:
                {
                    var m = F<GameObject>(cr, "meshesHolder");
                    if (m != null && !m.activeSelf) return Gone;
                    return cr.myTr != null ? Mathf.Clamp(Mathf.RoundToInt(cr.myTr.localScale.z * 50f), 0, Gone - 1) : 0;
                }
                case Shrubbery sh: return F<bool>(sh, "hasGrabbedLH") || F<bool>(sh, "hasGrabbedRH") ? Gone : 0;
                case Bird bd: return !F<bool>(bd, "isAgressiveInitially") && F<bool>(bd, "startedRoutine") ? Gone : 0;
                case BrittleIce bi:
                {
                    if (!F<bool>(bi, "isDestroyableIce")) return F<int>(bi, "iceHP") <= 0 ? Gone : 0;
                    var rbs = F<Rigidbody[]>(bi, "myRBs"); int bits = 0;
                    if (rbs != null) for (int i = 0; i < rbs.Length && i < 30; i++) if (rbs[i] != null && !rbs[i].isKinematic) bits |= 1 << i;
                    return bits;
                }
                case SnowOnIce sn:
                {
                    var tr = F<Transform>(sn, "myTr") ?? sn.transform;
                    return !sn.gameObject.activeSelf || !tr.gameObject.activeSelf ? 0 : Mathf.Max(1, Mathf.RoundToInt(tr.localScale.z * 4f));
                }
            }
            return 0;
        }

        static WorldKind KindOf(Component c)
        {
            switch (c)
            {
                case CrumblingHold _: return WorldKind.CrumblingHold;
                case BrickHold _: return WorldKind.BrickHold;
                case CrumblingHoldRegular _: return WorldKind.CrumbleRegular;
                case Shrubbery _: return WorldKind.Shrub;
                case Bird _: return WorldKind.Bird;
                case BrittleIce bi: return F<bool>(bi, "isDestroyableIce") ? WorldKind.IcePiece : WorldKind.IceBreak;
                default: return WorldKind.Snow;
            }
        }

        public void Update(ITransport net, float dt)
        {
            _net = net;
            float since = Time.unscaledTime - _sceneLoadedAt;
            if (_snapDone && ((_lateSnaps == 0 && since > 1.5f) || (_lateSnaps == 1 && since > 4f) || (_lateSnaps == 2 && since > 8f))) { _lateSnaps++; LateSnapshot(); _refreshT = 0f; }
            if (net == null || net.Peers.Count == 0) return;
            if (_scene != _p.SceneName) { _scene = _p.SceneName; _refreshT = 0f; }
            _refreshT -= dt; _scanT += dt; _resendT += dt; _propT += dt; _bivT += dt;
            if (_refreshT <= 0f) { Refresh(); _refreshT = 10f; }
            if (_propT >= 0.05f) { _propT = 0f; StreamProps(); }
            if (_bivT >= 0.5f) { _bivT = 0f; WatchBivouac(); }
            if (_scanT < 0.2f) return;
            _scanT = 0f;
            foreach (var kv in _objs.ToList())
            {
                int now = Read(kv.Value);
                if (now < 0 || !_state.TryGetValue(kv.Key, out var was) || now == was) continue;
                _state[kv.Key] = now;
                var ev = new WorldEvent { Scene = _p.SceneName, Key = kv.Key, Kind = KindOf(kv.Value), Index = now, Pos = RemotePlayer.FromV(kv.Value.transform.position) };
                if (kv.Value is CrumblingHold ch0) ev.Pos = RemotePlayer.FromV(F<Transform>(ch0, "parentTr")?.localPosition ?? Vector3.zero);
                if (kv.Value is BrickHold bh0) ev.Pos = RemotePlayer.FromV(bh0.transform.localPosition);
                _changed[kv.Key] = ev;
                bool final = now == Gone || ev.Kind == WorldKind.IcePiece || (ev.Kind == WorldKind.Snow && now <= 0);
                net.Broadcast(ev.Write(), final);
                if (final || ev.Kind == WorldKind.IceBreak) Plugin.Log.LogInfo($"world: {ev.Kind} {kv.Key} -> {now}");
                if (kv.Value is Bird bd && now == Gone) _birdOwned[kv.Key] = Time.unscaledTime + 10f; // we flushed it: we stream its flight
            }
            if (_resendT > 5f && _changed.Count > 0) { _resendT = 0f; foreach (var ev in _changed.Values) net.Broadcast(ev.Write(), false); }
        }

        public void Receive(ulong from, WorldEvent ev)
        {
            if (ev.Scene != _p.SceneName) return;
            if (ev.Kind == WorldKind.Rock) { ApplyRock(ev); return; }
            if (ev.Kind == WorldKind.Bivouac) { ApplyBivouac(from, ev); return; }
            if (_objs.Count == 0) Refresh();
            if (!_objs.TryGetValue(ev.Key, out var c) || c == null)
            {
                if (_state.TryGetValue(ev.Key, out var known) && known == ev.Index) return; // already applied; the object is gone since
                // not known yet: keep the newest state and apply it when the object turns up
                if (!_pending.ContainsKey(ev.Key)) Plugin.Log.LogInfo($"world: {ev.Kind} {ev.Key} not found here yet, kept for later");
                _pending[ev.Key] = ev; return;
            }
            ApplyNow(c, ev);
        }

        readonly Dictionary<string, WorldEvent> _pending = new Dictionary<string, WorldEvent>();
        readonly HashSet<string> _appliedOnce = new HashSet<string>();

        void ApplyNow(Component c, WorldEvent ev)
        {
            int cur = Read(c);
            if (cur == ev.Index) return;
            try { Apply(c, ev, cur); }
            catch (Exception e) { Plugin.Log.LogWarning($"world apply {ev.Kind} {ev.Key}: {e.Message}"); }
            _state[ev.Key] = Read(c); // never echo what we just applied
            if (ev.Index == Gone || (ev.Kind == WorldKind.Snow && (ev.Index <= 0 || _appliedOnce.Add(ev.Key))))
                Plugin.Log.LogInfo($"world: applied {ev.Kind} {ev.Key} = {ev.Index} from a friend (was {cur}, now {_state[ev.Key]})");
        }

        void Apply(Component c, WorldEvent ev, int cur)
        {
            switch (c)
            {
                case CrumblingHold ch:
                    if (cur == Gone) return;
                    if (ev.Index == Gone)
                    {
                        S(ch, "poppedOut", true); S(ch, "playPopout", true);
                        var rb = F<Rigidbody>(ch, "rb"); if (rb != null) { rb.isKinematic = false; rb.AddForce(Vector3.down, ForceMode.Impulse); }
                        var parent = F<Transform>(ch, "parentTr"); if (parent != null) parent.tag = "Untagged";
                        var col = F<Collider>(ch, "col"); if (col != null) col.isTrigger = false;
                        ch.StartCoroutine("DisableBrickFunctionality");
                        LetGoIfHolding(ch.transform);
                    }
                    else if (ev.Index < cur)
                    {   // partial wear: same remaining time and the same slid-out position
                        ch.popoutDuration = ev.Index / 20f;
                        var parent = F<Transform>(ch, "parentTr"); if (parent != null) parent.localPosition = RemotePlayer.ToV(ev.Pos);
                    }
                    return;
                case BrickHold bh:
                    if (cur == Gone) return;
                    if (ev.Index == Gone)
                    {
                        S(bh, "poppedOut", true); S(bh, "playPopout", true);
                        bh.tag = "Untagged";
                        var col = F<Collider>(bh, "col"); if (col != null) col.isTrigger = false;
                        var rb = F<Rigidbody>(bh, "rb"); if (rb != null) { rb.isKinematic = false; rb.AddForce(Vector3.down, ForceMode.Impulse); }
                        bh.StartCoroutine("DisableBrickFunctionality");
                        LetGoIfHolding(bh.transform);
                    }
                    else if (ev.Index < cur) { bh.popoutDuration = ev.Index / 20f; bh.transform.localPosition = RemotePlayer.ToV(ev.Pos); }
                    return;
                case CrumblingHoldRegular cr:
                    if (ev.Index == Gone)
                    {
                        F<GameObject>(cr, "meshesHolder")?.SetActive(false);
                        var c1 = F<Collider>(cr, "col1"); if (c1 != null) c1.enabled = false;
                        var c2 = F<Collider>(cr, "col2"); if (c2 != null) c2.enabled = false;
                        LetGoIfHolding(cr.transform);
                    }
                    else if (cr.myTr != null)
                    {   // wear (or regrowth on the owner's side): same scale everywhere
                        var m = F<GameObject>(cr, "meshesHolder"); if (m != null && !m.activeSelf) m.SetActive(true);
                        var c1 = F<Collider>(cr, "col1"); if (c1 != null) c1.enabled = true;
                        var c2 = F<Collider>(cr, "col2"); if (c2 != null) c2.enabled = true;
                        cr.myTr.localScale = Vector3.one * (ev.Index / 50f);
                    }
                    return;
                case Shrubbery sh:
                    if (ev.Index == Gone && cur != Gone) { S(sh, "hasGrabbedLH", true); sh.StartCoroutine("ActivateShrub"); }
                    return;
                case Bird bd:
                {
                    var trig = F<Collider>(bd, "triggerCollider");
                    if (ev.Index == Gone && cur != Gone)
                    {
                        S(bd, "playerInRange", true); S(bd, "startedRoutine", true); S(bd, "flyRandomly", true);
                        if (trig != null) trig.enabled = false;
                    }
                    return;
                }
                case BrittleIce bi:
                    if (ev.Kind == WorldKind.IceBreak && ev.Index == Gone && cur != Gone) { S(bi, "iceHP", 1); bi.TakeIceAxeHit(bi.transform, bi.transform); }
                    if (ev.Kind == WorldKind.IcePiece)
                    {   // the game's own break: HitDestroyableChild frees the piece and IncreaseGravity makes it fall at full speed
                        // (0.6.0 only un-froze the piece, so icicles drifted down slowly until touched)
                        var rbs = F<Rigidbody[]>(bi, "myRBs");
                        var hit = AccessTools.Method(typeof(BrittleIce), "HitDestroyableChild");
                        if (rbs != null) for (int i = 0; i < rbs.Length && i < 30; i++)
                            if ((ev.Index & (1 << i)) != 0 && rbs[i] != null && rbs[i].isKinematic)
                            {
                                if (hit != null) bi.StartCoroutine((System.Collections.IEnumerator)hit.Invoke(bi, new object[] { rbs[i].transform }));
                                else { rbs[i].isKinematic = false; }
                            }
                        bi.StartCoroutine("IncreaseGravity");
                    }
                    return;
                case SnowOnIce sn:
                {
                    var tr = F<Transform>(sn, "myTr") ?? sn.transform;
                    if (ev.Index <= 0) { tr.gameObject.SetActive(false); if (tr != sn.transform) sn.gameObject.SetActive(false); LetGoIfHolding(sn.transform); }
                    else if (ev.Index < cur) tr.localScale = Vector3.one * (ev.Index / 4f);
                    return;
                }
            }
        }

        /// <summary>If the local player holds an object a friend just destroyed, drop it like the game would.</summary>
        void LetGoIfHolding(Transform t)
        {
            var c = _p.LocalClimbing; if (c == null) return;
            if (c.grabbedObjectL != null && (c.grabbedObjectL.IsChildOf(t) || t.IsChildOf(c.grabbedObjectL))) c.ReleaseLHand(lostHand: true);
            if (c.grabbedObjectR != null && (c.grabbedObjectR.IsChildOf(t) || t.IsChildOf(c.grabbedObjectR))) c.ReleaseRHand(lostHand: true);
        }

        // ------------------------------------------------------------------ physics props and birds
        readonly Dictionary<Rigidbody, float> _owned = new Dictionary<Rigidbody, float>();       // rb -> stream until
        readonly Dictionary<Rigidbody, float> _ownedCap = new Dictionary<Rigidbody, float>();    // rb -> never past this
        readonly Dictionary<string, float> _birdOwned = new Dictionary<string, float>();          // bird key -> stream until
        class Follow { public Transform T; public Rigidbody Rb; public bool WasKinematic; public Behaviour Disabled; public Vector3 Pos; public Quaternion Rot; public float Last; }
        readonly Dictionary<string, Follow> _following = new Dictionary<string, Follow>();
        readonly HashSet<string> _resolveFails = new HashSet<string>(), _announced = new HashSet<string>();

        void StreamProps()
        {
            var c = _p.LocalClimbing;
            float now = Time.unscaledTime;
            // own whatever physics object the local player is holding (+3 s after letting go so the swing settles everywhere)
            if (c != null)
            {
                foreach (var held in new[] { c.grabbingLeftPhysicsObj ? c.grabbedObjectL : null, c.grabbingRightPhysicsObj ? c.grabbedObjectR : null })
                {
                    if (held == null || held.CompareTag("ClimbableNPCHand") || held.GetComponentInParent<BrickHold>() != null) continue;
                    var rb = held.GetComponentInParent<Rigidbody>();
                    if (rb != null && IsTurningMill(rb.transform)) continue; // self-turning mills follow the clock
                    if (rb != null && (rb.isKinematic || IsAnimated(rb.transform))) continue; // Old Mill wings: moved by an animation, never by hands
                    if (rb != null) { _owned[rb] = now + 3f; _ownedCap[rb] = now + 20f; }
                }
            }
            foreach (var kv in _owned.ToList())
            {
                // after letting go we keep streaming while it still swings or spins (a mill wheel, a bending tree),
                // at most 20 s after the last touch, so friends see the same motion until it settles
                if (kv.Key != null && now > kv.Value && now < (_ownedCap.TryGetValue(kv.Key, out var cap) ? cap : 0f)
                    && (kv.Key.velocity.sqrMagnitude > 0.01f || kv.Key.angularVelocity.sqrMagnitude > 0.01f)) _owned[kv.Key] = now + 0.5f;
                else if (kv.Key == null || now > kv.Value) { _owned.Remove(kv.Key); if (kv.Key != null) _ownedCap.Remove(kv.Key); continue; }
                var key = "pr:" + Path(kv.Key.transform);
                if (_announced.Add(key)) Plugin.Log.LogInfo($"prop {key}: streaming {kv.Key.name} to friends");
                Send(kv.Key.transform, 0, key);
            }
            foreach (var kv in _birdOwned.ToList())
            {
                if (now > kv.Value || !_objs.TryGetValue(kv.Key, out var comp) || !(comp is Bird b) || b.birdTr == null) { _birdOwned.Remove(kv.Key); continue; }
                Send(b.birdTr, 1, kv.Key);
            }
            // release followers that went quiet: physics (or the bird's own script) takes over again
            foreach (var kv in _following.ToList())
            {
                var f = kv.Value;
                if (f.T == null) { _following.Remove(kv.Key); continue; }
                if (now - f.Last > 1.2f)
                {
                    if (f.Rb != null) f.Rb.isKinematic = f.WasKinematic;
                    if (f.Disabled != null) f.Disabled.enabled = true;
                    _following.Remove(kv.Key);
                }
            }
        }

        void Send(Transform t, byte kind, string key)
        {
            var q = t.rotation;
            _net.Broadcast(new PropPose { Scene = _p.SceneName, Key = key, Kind = kind, Pos = RemotePlayer.FromV(t.position), Qx = q.x, Qy = q.y, Qz = q.z, Qw = q.w }.Write(), false);
        }

        /// <summary>Mills that turn by themselves (they follow the clock); wheels players turn by hand are streamed while held.</summary>
        static bool IsTurningMill(Transform t) { var w = t.GetComponentInParent<Wheel>(); return w != null && w.isRotatingWheel; }

        // ------------------------------------------------------------------ mills on a shared clock (0.25)
        // Self-turning wheels (Alps) and animated wings (Old Mill) are never streamed: 0.22 dragged the wing colliders off
        // the wings, and corrections by message drifted again after a pause.
        // Every PC sets its self-turning mills from the computer clock (UTC; Windows keeps it in sync): the angle or the
        // animation time is a function of the time of day. So they agree without messages, keep their place while the
        // game is paused or minimised, and never drift (owner: mills stopped and fell out of step after alt-tab).

        class ClockWheel { public Transform Joint, Pivot; public Vector3 P0, Axis; public Quaternion R0; public float Speed; }
        readonly List<ClockWheel> _clockWheels = new List<ClockWheel>();

        void CaptureClockWheels(Scene loaded)
        {
            _clockWheels.Clear();
            foreach (var w in UnityEngine.Object.FindObjectsOfType<Wheel>())
            {
                if (w == null || !w.isRotatingWheel || w.rotatingJoint == null || w.gameObject.scene != loaded) continue;
                _clockWheels.Add(new ClockWheel { Joint = w.rotatingJoint, Pivot = w.transform, P0 = w.rotatingJoint.position, R0 = w.rotatingJoint.rotation, Axis = w.transform.right, Speed = w.rotateSpeed });
            }
            if (_clockWheels.Count > 0) Plugin.Log.LogInfo($"mills: {_clockWheels.Count} self-turning wheel(s) follow the clock");
        }

        /// <summary>LateUpdate: put every clock mill where the clock says (after the game's own turning).</summary>
        public void ClockTick()
        {
            double t = SharedClock.Now;
            foreach (var w in _clockWheels)
            {
                if (w.Joint == null || w.Pivot == null) continue;
                float a = (float)((t * w.Speed) % 360.0);
                var q = Quaternion.AngleAxis(a, w.Axis);
                var pivot = w.Pivot.position;
                w.Joint.SetPositionAndRotation(pivot + q * (w.P0 - pivot), q * w.R0);
            }
            if (_lateSnaps < 1) return;
            foreach (var m in Anims())
            {
                if (m.A != null)
                {
                    var st = LoopState(m.A); if (st == null || st.length <= 0f) continue;
                    st.time = (float)((t * st.speed) % st.length);
                }
                else if (m.An != null && m.An.isActiveAndEnabled)
                {
                    var info = m.An.GetCurrentAnimatorStateInfo(0); if (info.length <= 0f) continue;
                    m.An.Play(info.fullPathHash, 0, (float)((t * m.An.speed / info.length) % 1.0));
                }
            }
        }

        static bool IsAnimated(Transform t)
        {
            var a = t.GetComponentInParent<Animation>(); if (a != null && a.isPlaying) return true;
            var an = t.GetComponentInParent<Animator>(); return an != null && an.enabled && an.runtimeAnimatorController != null && an.GetComponentInParent<Climbing>() == null;
        }

        class MillAnim { public Animation A; public Animator An; public string Key; }
        List<MillAnim> _anims; string _animsScene;

        List<MillAnim> Anims()
        {
            if (_anims != null && _animsScene == _p.SceneName) return _anims;
            _animsScene = _p.SceneName; _anims = new List<MillAnim>();
            foreach (var a in UnityEngine.Object.FindObjectsOfType<Animation>())
            {
                if (a == null || !a.isPlaying || a.GetComponentInChildren<Collider>() == null || a.GetComponentInParent<Climbing>() != null) continue;
                if (!LooksLikeMill(a.transform)) continue; // 0.22.3 also took the summit stamper box
                if (LoopState(a) == null) continue;
                _anims.Add(new MillAnim { A = a, Key = "an:" + Path(a.transform) });
            }
            foreach (var an in UnityEngine.Object.FindObjectsOfType<Animator>())
            {
                if (an == null || !an.enabled || an.runtimeAnimatorController == null || an.GetComponentInChildren<Collider>() == null) continue;
                if (an.GetComponentInParent<Climbing>() != null || !LooksLikeMill(an.transform)) continue;
                _anims.Add(new MillAnim { An = an, Key = "am:" + Path(an.transform) });
            }
            foreach (var m in _anims) Plugin.Log.LogInfo($"animated mill found: {m.Key}");
            return _anims;
        }

        static bool LooksLikeMill(Transform t)
        {
            foreach (var c in t.GetComponentsInChildren<Transform>(true))
            { var n = c.name.ToLowerInvariant(); if (n.Contains("mill") || n.Contains("wing")) return true; }
            for (var p = t; p != null; p = p.parent) { var n = p.name.ToLowerInvariant(); if (n.Contains("mill") || n.Contains("wing")) return true; }
            return false;
        }

        static AnimationState LoopState(Animation a)
        {
            foreach (AnimationState st in a)
                if (st.enabled && st.length > 0f && (st.wrapMode == WrapMode.Loop || (st.wrapMode == WrapMode.Default && (a.wrapMode == WrapMode.Loop || (st.clip != null && st.clip.wrapMode == WrapMode.Loop))))) return st;
            return null;
        }

        public void ReceiveProp(ulong from, PropPose p)
        {
            if (p.Scene != _p.SceneName) return;
            if (!_following.TryGetValue(p.Key, out var f))
            {
                Transform t = null; Rigidbody rb = null; Behaviour disabled = null;
                if (p.Kind == 0)
                {
                    t = Resolve(p.Key.Substring(3)); rb = t != null ? t.GetComponent<Rigidbody>() : null; if (rb != null && _owned.ContainsKey(rb)) return;
                    if (t != null && ((rb != null && rb.isKinematic) || IsAnimated(t))) return; // never drag animated mill colliders off their wings
                }
                else
                {
                    if (_objs.Count == 0) Refresh();
                    if (_objs.TryGetValue(p.Key, out var comp) && comp is Bird b) { t = b.birdTr; disabled = b; b.enabled = false; }
                }
                if (t == null) { if (_resolveFails.Add(p.Key)) Plugin.Log.LogInfo($"prop {p.Key}: not found here"); return; }
                Plugin.Log.LogInfo($"prop {p.Key}: following a friend's {(p.Kind == 0 ? "physics object" : "bird")} ({t.name})");
                f = new Follow { T = t, Rb = rb, WasKinematic = rb != null && rb.isKinematic, Disabled = disabled };
                if (rb != null) rb.isKinematic = true;
                _following[p.Key] = f;
            }
            f.Pos = RemotePlayer.ToV(p.Pos); f.Rot = new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw); f.Last = Time.unscaledTime;
        }

        /// <summary>Called every FixedUpdate: followed physics objects glide to the latest streamed pose.</summary>
        public void FixedTick()
        {
            BrushSnow();
            foreach (var f in _following.Values)
            {
                if (f.T == null) continue;
                var pos = Vector3.Lerp(f.T.position, f.Pos, 0.35f); var rot = Quaternion.Slerp(f.T.rotation, f.Rot, 0.35f);
                if (f.Rb != null) { f.Rb.MovePosition(pos); f.Rb.MoveRotation(rot); } else { f.T.position = pos; f.T.rotation = rot; }
            }
        }

        // ---- aggressive gulls: an attack lasts at most 10 s (owner: it went on for ~25 s); the game's own range check stays ----
        readonly Dictionary<Bird, float> _attackSince = new Dictionary<Bird, float>();
        const float AttackSeconds = 10f;

        public void LateTick()
        {
            float now = Time.unscaledTime;
            foreach (var comp in _objs.Values)
            {
                if (!(comp is Bird b) || b == null || !b.isActiveAndEnabled) continue;
                if (!b.attackPlayer) { _attackSince.Remove(b); continue; }
                if (!_attackSince.TryGetValue(b, out var since)) { _attackSince[b] = now; continue; }
                if (now - since > AttackSeconds)
                {
                    b.attackPlayer = false; S(b, "flyRandomly", true);
                    _attackSince.Remove(b);
                }
            }
        }

        // ---- a friend's hands (and ice axes) brush snow off on this PC too, like the game does for our own hands ----
        readonly Dictionary<Transform, Vector3> _prevRemoteHand = new Dictionary<Transform, Vector3>();
        static readonly Collider[] _snowHits = new Collider[8];

        void BrushSnow()
        {
            float dt = Time.fixedDeltaTime;
            foreach (var rp in _p.Remotes.Values)
            {
                var st = rp.Shown;
                if (!rp.Visible || st == null || st.Scene != _p.SceneName) continue;
                for (int side = 0; side < 2; side++)
                {
                    var hand = side == 0 ? rp.HandL : rp.HandR;
                    var flags = st.Flags;
                    bool axe = (flags & (side == 0 ? StateFlags.AxeL : StateFlags.AxeR)) != 0;
                    bool active = (flags & (side == 0 ? (StateFlags.ReachL | StateFlags.GrabL) : (StateFlags.ReachR | StateFlags.GrabR))) != 0;
                    var pos = hand.position;
                    float speed = _prevRemoteHand.TryGetValue(hand, out var prev) ? Vector3.Distance(prev, pos) / Mathf.Max(dt, 1e-4f) : 0f;
                    _prevRemoteHand[hand] = pos;
                    if (!active || speed < 0.2f) continue;
                    int n = Physics.OverlapSphereNonAlloc(pos, axe ? 0.25f : 0.12f, _snowHits, ~0, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                    {
                        var sn = _snowHits[i].GetComponent<SnowOnIce>() ?? _snowHits[i].GetComponentInParent<SnowOnIce>();
                        if (sn == null || !sn.gameObject.activeInHierarchy) continue;
                        var tr = F<Transform>(sn, "myTr") ?? sn.transform;
                        if (tr.localScale.z > 8f) tr.localScale -= Vector3.one * 120f * dt;
                        else sn.gameObject.SetActive(false);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ falling rocks / icicles
        // One shared stream per rock. A friend's drop is ignored while our copy of that rock is still falling (0.5.0
        // restarted icicles mid-fall, which looked like slow motion); our random drop is skipped right after a friend's.
        readonly Dictionary<string, float> _rockInit = new Dictionary<string, float>(), _rockRemote = new Dictionary<string, float>();
        static bool _applying;

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(FallingRock), nameof(FallingRock.InitialiseRock)), prefix: new HarmonyMethod(typeof(WorldSync), nameof(RockPrefix)));
            h.Patch(AccessTools.Method(typeof(Bivouac), "MoveToBivouac"), prefix: new HarmonyMethod(typeof(WorldSync), nameof(BivouacPrefix)));
        }

        static bool RockPrefix(FallingRock __instance)
        {
            var me = Instance;
            if (me == null || _applying || me._net == null || me._net.Peers.Count == 0) return true;
            var k = "rk:" + Path(__instance.transform);
            if (me._rockRemote.TryGetValue(k, out var t) && Time.unscaledTime - t < 8f) return false;
            me._rockInit[k] = Time.unscaledTime;
            me._net.Broadcast(new WorldEvent { Scene = me._p.SceneName, Kind = WorldKind.Rock, Key = k, Pos = RemotePlayer.FromV(__instance.transform.position) }.Write(), true);
            return true;
        }

        void ApplyRock(WorldEvent ev)
        {
            var tr = Resolve(ev.Key.Substring(3));
            var rock = tr != null ? tr.GetComponent<FallingRock>() : null;
            if (rock == null) return;
            bool falling = rock.gameObject.activeSelf && rock.rb != null && !rock.rb.isKinematic;
            if (falling && _rockInit.TryGetValue(ev.Key, out var t) && Time.unscaledTime - t < 12f) return;
            _rockRemote[ev.Key] = Time.unscaledTime; _rockInit[ev.Key] = Time.unscaledTime;
            _applying = true;
            try { rock.InitialiseRock(); } finally { _applying = false; }
        }

        // ------------------------------------------------------------------ bivouacs
        GameObject _myBiv; int _bivSent = -1; float _bivResend;
        readonly Dictionary<ulong, GameObject> _bivMirrors = new Dictionary<ulong, GameObject>();

        void WatchBivouac()
        {
            var mine = SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == "BivouacGameObject" && g.activeInHierarchy);
            int code = mine == null ? -1 : 1;
            bool changed = mine != _myBiv || code != _bivSent;
            _myBiv = mine;
            if (!changed && (code < 0 || Time.unscaledTime < _bivResend)) return; // a placed bivouac is resent every 5 s
            _bivSent = code; _bivResend = Time.unscaledTime + 5f;
            var e = mine != null ? mine.transform.eulerAngles : Vector3.zero;
            _net.Broadcast(new WorldEvent { Scene = _p.SceneName, Kind = WorldKind.Bivouac, Key = "biv:" + string.Join(":", new[] { e.x, e.y, e.z }.Select(v => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))), Index = code,
                Pos = RemotePlayer.FromV(mine != null ? mine.transform.position : Vector3.zero) }.Write(), changed);
            if (changed) Plugin.Log.LogInfo(mine != null ? $"bivouac placed at {mine.transform.position}" : "bivouac removed");
        }

        void ApplyBivouac(ulong from, WorldEvent ev)
        {
            _bivMirrors.TryGetValue(from, out var m);
            if (ev.Index < 0) { if (m != null) { UnityEngine.Object.Destroy(m); Plugin.Log.LogInfo("friend's bivouac removed"); } _bivMirrors.Remove(from); return; }
            var biv = UnityEngine.Object.FindObjectOfType<Bivouac>();
            if (biv == null || biv.bivouacPrefab == null) return;
            var pos = RemotePlayer.ToV(ev.Pos);
            var parts = ev.Key.Split(':');
            var rot = parts.Length == 4 ? Quaternion.Euler(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)) : Quaternion.identity;
            if (m == null)
            {
                m = UnityEngine.Object.Instantiate(biv.bivouacPrefab, pos, rot);
                m.name = "FoYBivouac_" + from; // not "BivouacGameObject": never re-broadcast as ours
                _bivMirrors[from] = m;
                Plugin.Log.LogInfo($"friend's bivouac mirrored at {pos}");
            }
            else { m.transform.position = pos; m.transform.rotation = rot; }
        }

        /// <summary>Entering a friend's bivouac: point the game's own player/camera spots at that tent.</summary>
        static void BivouacPrefix(Bivouac __instance)
        {
            var me = Instance; if (me == null || me._bivMirrors.Count == 0) return;
            var player = me._p.LocalClimbing != null ? me._p.LocalClimbing.playerBody.position : __instance.transform.position;
            var near = me._bivMirrors.Values.Where(g => g != null).OrderBy(g => Vector3.Distance(g.transform.position, player)).FirstOrDefault();
            if (near == null || Vector3.Distance(near.transform.position, player) > 4f) return;
            if (me._myBiv != null && Vector3.Distance(me._myBiv.transform.position, player) < Vector3.Distance(near.transform.position, player)) return;
            var ts = near.GetComponentsInChildren<Transform>(true);
            var pp = ts.FirstOrDefault(t => t.name == "Bivouac_PlayerPosition"); var cp = ts.FirstOrDefault(t => t.name == "Bivouac_CameraPosition");
            if (pp != null) __instance.bivouacPlayerPosition = pp;
            if (cp != null) __instance.bivouacCameraPosition = cp;
            Plugin.Log.LogInfo("entering a friend's bivouac");
        }
    }
}
