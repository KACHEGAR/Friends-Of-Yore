using HarmonyLib;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FriendsOfYore.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FriendsOfYore
{
    /// <summary>
    /// Shares placed ropes. Every rope a player places is an Instantiate(RopeAnchor.ropePrefab) clone (RopeAnchor.
    /// DetachThenAttachToNew). Owners broadcast their rope list; others spawn the same prefab at the same piton. Anyone
    /// can then clip in with the game's own "Interact" on the RopeAttacher (RopeAnchor.LerpPlayerToLastSegment works on
    /// any rope object). Mirrored pitons are untagged so only the owner can pull a rope out (no free ropes for others).
    /// </summary>
    public class RopeSync
    {
        readonly Plugin _p;
        GameObject _prefab;
        string _cloneName;
        uint _nextId = 1;
        readonly Dictionary<GameObject, uint> _mine = new Dictionary<GameObject, uint>();
        readonly Dictionary<(ulong owner, uint id), GameObject> _mirrors = new Dictionary<(ulong, uint), GameObject>();
        readonly HashSet<GameObject> _mirrorSet = new HashSet<GameObject>();
        float _scanT, _sendT;
        int _lastSentHash;

        public RopeSync(Plugin p)
        {
            _p = p;
            GameAccess.IsMirroredRope = t => { for (var x = t; x != null; x = x.parent) if (_mirrorSet.Contains(x.gameObject)) return true; return false; };
            GameAccess.RopeOccupant = go => _occupied.TryGetValue(go, out var rp) ? rp.DisplayName : null;
            GameAccess.OnMirrorPulled = MirrorPulled;
            GameAccess.OnOwnPulled = OwnPulled;
        }

        // ---- occupancy: who hangs on which rope ----
        readonly Dictionary<GameObject, RemotePlayer> _occupied = new Dictionary<GameObject, RemotePlayer>();
        readonly HashSet<Rigidbody> _pinned = new HashSet<Rigidbody>();

        /// <summary>The rope end segment carries the interactive knot (RopeAttacher) as mesh renderers below it.</summary>
        static void SetKnotVisible(Transform firstSegment, bool v)
        {
            foreach (var r in firstSegment.GetComponentsInChildren<MeshRenderer>(true)) if (r.enabled != v) r.enabled = v;
        }

        /// <summary>(owner, id) of the rope the local player is clipped into, or (0,0).</summary>
        public (ulong owner, uint id) MyRopeRef(ulong myId, RopeAnchor ra)
        {
            var go = GameAccess.AttachedRope(ra);
            if (go == null) return (0, 0);
            if (_mine.TryGetValue(go, out var id)) return (myId, id);
            foreach (var kv in _mirrors) if (kv.Value == go) return kv.Key;
            return (0, 0);
        }

        GameObject Find(ulong owner, uint id, ulong myId)
        {
            if (owner == myId) { foreach (var kv in _mine) if (kv.Value == id) return kv.Key; return null; }
            return _mirrors.TryGetValue((owner, id), out var go) ? go : null;
        }

        /// <summary>Marks ropes friends hang on and pins their rope end to the friend's body so it visibly carries them.</summary>
        public static System.Action<string> Say;

        public void UpdateOccupancy(ulong myId, IEnumerable<RemotePlayer> remotes, RopeAnchor ra)
        {
            _occupied.Clear();
            if (GameAccess.OnDeadRope(ra))
            {   // our rope vanished under us (pulled out by a friend, or its owner left): let go like the game's own detach
                Plugin.Log.LogInfo("the rope we hung on is gone: detaching");
                ra.DisableAttached(); ra.cachedRope = null;
            }
            var mine = GameAccess.AttachedRope(ra);
            if (mine != null && _mirrorSet.Contains(mine))
            {   // on a friend's rope: not allowed in a free solo save; otherwise this climb is no free solo any more (0.25)
                if (Plugin.LocalFreeSolo)
                {
                    Plugin.Log.LogInfo("free solo: let go of a friend's rope");
                    ra.DisableAttached(); ra.cachedRope = null; mine = null;
                    Say?.Invoke("Free solo: you can't use a friend's rope");
                }
                else CoopRules.MarkAssisted("used a friend's rope");
            }
            foreach (var rp in remotes)
            {
                if (!rp.Visible || rp.Shown == null || rp.Shown.RopeId == 0) continue;
                var go = Find(rp.Shown.RopeOwner, rp.Shown.RopeId, myId);
                if (go == null || go == mine) continue; // simultaneous clip-in: each side keeps its own; nothing to pin
                _occupied[go] = rp;
            }
            var stillPinned = new HashSet<Rigidbody>();
            foreach (var kv in _occupied)
            {
                if (kv.Key == null || kv.Key.transform.childCount == 0) continue;
                var first = kv.Key.transform.GetChild(0).GetComponent<Rigidbody>();
                if (first == null) continue;
                if (!first.isKinematic) first.isKinematic = true;
                first.MovePosition(kv.Value.RopeAnchorPoint); // at the friend's waist (harness), not under the feet
                stillPinned.Add(first);
                SetKnotVisible(first.transform, false);   // the clip-in knot is hidden while someone is on the rope
            }
            foreach (var rb in _pinned) if (rb != null && !stillPinned.Contains(rb)) { rb.isKinematic = false; if (!_leaving.Contains(rb.transform.parent != null ? rb.transform.parent.gameObject : null)) SetKnotVisible(rb.transform, true); }
            _pinned.Clear(); foreach (var rb in stillPinned) _pinned.Add(rb);
            // ropes on their way out keep the knot hidden even if the game turns it back on while detaching
            foreach (var g in _leaving) if (g != null) foreach (var r in g.GetComponentsInChildren<Renderer>(true)) if (r.enabled) r.enabled = false;
        }

        bool _unlockedPullOut;
        int _sharePlayers;
        /// <summary>
        /// Co-op rope budget: everyone's ropes are shared on the wall, so each player carries ropesCollected / players
        /// (never fewer than 1). Applied when the lobby size changes or a peak starts; never more than the share in the
        /// backpack + on the wall. Alone again, the full count comes back.
        /// </summary>
        void ShareRopes(ITransport net)
        {
            var ra = Object.FindObjectOfType<RopeAnchor>();
            if (ra == null || GameManager.control == null) return;
            int total = GameManager.control.ropesCollected;
            if (total <= 0) return;
            int players = Mathf.Max(1, net.Peers.Count + 1);
            int share = Mathf.Max(1, total / players);
            int placed = _mine.Count(kv => kv.Key != null);
            if (players != _sharePlayers)
            {
                int was = ra.anchorsInBackpack;
                ra.anchorsInBackpack = Mathf.Max(0, share - placed);
                if (ra.anchorsInBackpack != was) ra.UpdateRopesAmount(useAnim: false);
                Plugin.Log.LogInfo($"ropes: {total} collected, {players} player(s) -> {share} each ({placed} on the wall, {ra.anchorsInBackpack} in the backpack)");
                _sharePlayers = players;
            }
            else if (ra.anchorsInBackpack + placed > share && !_pullingOutAnchor(ra))
            {   // e.g. a friend's piton pulled out counted as ours, or a pull-out finished
                ra.anchorsInBackpack = Mathf.Max(0, share - placed); ra.UpdateRopesAmount(useAnim: false);
            }
        }

        public void OnSceneLoaded() { _unlockedPullOut = false; _sharePlayers = 0; _mine.Clear(); _mirrors.Clear(); _mirrorSet.Clear(); _occupied.Clear(); _pinned.Clear(); _prefab = null; _lastSentHash = 0; }

        bool EnsurePrefab()
        {
            if (_prefab != null) return true;
            var ra = Object.FindObjectOfType<RopeAnchor>();
            if (ra == null || ra.ropePrefab == null) return false;
            _prefab = ra.ropePrefab; _cloneName = _prefab.name + "(Clone)";
            return true;
        }

        public void Update(ITransport net, float dt)
        {
            if (net == null || !EnsurePrefab()) return;
            _scanT += dt; _sendT += dt;
            if (_scanT < 0.25f) return;
            _scanT = 0f;
            // Own ropes: new clones show up at the scene root. A rope stays ours until it is destroyed: while we hang on it
            // the game parents it to the player (0.11 took that for "removed", so friends lost the rope we hung on and
            // could never pull its piton; it came back under a new number when we let go).
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                // a rope a friend pulled out is on its way out: never announce it again (0.21 sent it as a "new" rope for
                // 0.9 s, so the puller saw a fresh copy with its knot pop up where the piton was)
                if (go.name != _cloneName || _mirrorSet.Contains(go) || _leaving.Contains(go) || go.transform.childCount < 2) continue;
                if (!_mine.ContainsKey(go)) { _mine[go] = _nextId++; Plugin.Log.LogInfo($"rope placed locally (#{_mine[go]}) at {Piton(go).position}"); }
            }
            foreach (var gone in _mine.Keys.Where(g => g == null || !g.activeInHierarchy).ToList()) { Plugin.Log.LogInfo($"rope #{_mine[gone]} removed locally"); _mine.Remove(gone); }
            var list = new RopeList { Scene = _p.SceneName };
            foreach (var kv in _mine)
            {
                var pit = Piton(kv.Key);
                var joints = kv.Key.GetComponentsInChildren<SpringJoint>();
                float seg = joints.Length > 0 ? joints.Average(j => j.minDistance) : 0f;
                list.Ropes.Add(new RopeInfo { Id = kv.Value, Point = RemotePlayer.FromV(pit.position), Forward = RemotePlayer.FromV(pit.forward), SegLength = seg });
            }
            int hash = list.Ropes.Aggregate(list.Scene.GetHashCode(), (h, r) => h * 31 + (int)r.Id * 7 + Mathf.RoundToInt(r.SegLength * 10f));
            if ((hash != _lastSentHash || _sendT > 3f) && net.Peers.Count > 0)
            {
                net.Broadcast(list.Write(), hash != _lastSentHash); // changes go reliable, refreshes unreliable
                _lastSentHash = hash; _sendT = 0f;
            }
            // Drop mirrors whose objects died (scene change) or whose owner left.
            foreach (var k in _mirrors.Where(kv => kv.Value == null || !net.Peers.Contains(kv.Key.owner)).Select(kv => kv.Key).ToList()) Remove(k);
            PullOutWatchdog();
            ShareRopes(net);
            // The game only allows pulling a piton out after you have placed one yourself (allowDetachingAnchor starts
            // false and is set 0.9 s after a placement), so a friend's piton could not be pulled before that.
            if (!_unlockedPullOut) { var ra0 = Object.FindObjectOfType<RopeAnchor>(); if (ra0 != null) { _unlockedPullOut = true; if (!ra0.allowDetachingAnchor) { ra0.allowDetachingAnchor = true; Plugin.Log.LogInfo("pulling pitons allowed before placing one"); } } }
        }

        static Transform Piton(GameObject rope) => rope.transform.GetChild(rope.transform.childCount - 1);

        public void Receive(ulong from, BinaryReader r)
        {
            var list = RopeList.Read(r);
            if (list.Scene != _p.SceneName || !EnsurePrefab()) return;
            var listed = new HashSet<uint>(list.Ropes.Select(x => x.Id));
            // a rope we are pulling out ourselves is left to the game's pull-out (0.12 destroyed it mid-animation: the
            // coroutine died and we stayed "hammering a piton" with no new ropes allowed)
            foreach (var k in _mirrors.Keys.Where(k => k.owner == from && !listed.Contains(k.id)).ToList())
            {
                if (_pulled.TryGetValue(k, out var until) && Time.unscaledTime < until) { if (_mirrors[k] == null) _mirrors.Remove(k); continue; }
                Remove(k);
            }
            foreach (var info in list.Ropes)
            {
                if (_pulled.TryGetValue((from, info.Id), out var until) && Time.unscaledTime < until) continue; // we just pulled it out
                if (!_mirrors.ContainsKey((from, info.Id))) Spawn(from, info);
                // paid-out length: the owner's game lengthens the segment springs as they descend (0.6.0 mirrors stayed short)
                if (_mirrors.TryGetValue((from, info.Id), out var go) && go != null && info.SegLength > 0f)
                    foreach (var j in go.GetComponentsInChildren<SpringJoint>()) j.minDistance = info.SegLength;
            }
        }

        // ---- pulling a friend's rope out ----
        /// <summary>Ropes being pulled out: their clip-in knot stays hidden until they are gone (0.13 flashed it).</summary>
        readonly HashSet<GameObject> _leaving = new HashSet<GameObject>();
        void HideKnotForGood(GameObject rope)
        {
            if (rope == null || rope.transform.childCount == 0) return;
            _leaving.RemoveWhere(g => g == null); _leaving.Add(rope);
            // the whole rope goes at once: knots at both ends (the piton one flashed in 0.20), segments and line
            foreach (var r in rope.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        }
        readonly Dictionary<(ulong, uint), float> _pulled = new Dictionary<(ulong, uint), float>();

        /// <summary>Our game is pulling out a friend's piton: tell the owner; the rope goes back into THEIR backpack.</summary>
        void MirrorPulled(Transform hit, RopeAnchor ra)
        {
            var key = _mirrors.FirstOrDefault(kv => kv.Value != null && hit.IsChildOf(kv.Value.transform)).Key;
            if (key.Item1 == 0) return;
            _pulled[key] = Time.unscaledTime + 10f;
            HideKnotForGood(_mirrors[key]);
            var net = _p.Net;
            net?.Broadcast(new WorldEvent { Scene = _p.SceneName, Kind = WorldKind.RopePull, Key = key.Item1 + ":" + key.Item2 }.Write(), true);
            Plugin.Log.LogInfo($"pulling out rope #{key.Item2} of {key.Item1}");
            _p.StartCoroutine(TakeBackBonus(ra));
        }

        /// <summary>
        /// We pull out our own piton: its knot stays hidden (0.20 showed it for a second where the piton was) and friends
        /// are told at once, so whoever hangs on it falls now, not after the 1 s pull-out animation.
        /// </summary>
        void OwnPulled(Transform hit)
        {
            // 0.21 hid our rope and told friends at the start of the pull: it vanished too early. The game's own pull-out
            // runs as usual now; friends drop the rope when it is really gone.
            var root = _mine.Keys.FirstOrDefault(g => g != null && hit.IsChildOf(g.transform));
            if (root != null) Plugin.Log.LogInfo($"pulling out our own rope #{_mine[root]}");
        }

        /// <summary>The game counts the pulled piton as ours (+1 rope); it belongs to the friend, so take it back.</summary>
        static System.Collections.IEnumerator TakeBackBonus(RopeAnchor ra)
        {
            int before = ra.anchorsInBackpack;
            float t = 0f;
            while (ra != null && ra.anchorsInBackpack == before && t < 3f) { t += Time.unscaledDeltaTime; yield return null; }
            if (ra != null && ra.anchorsInBackpack > before) { ra.anchorsInBackpack = before; ra.UpdateRopesAmount(useAnim: false); }
        }

        /// <summary>A friend pulled one of our pitons (or a third player's): it leaves the wall; whoever hangs on it falls.</summary>
        public void PulledByFriend(ulong from, WorldEvent ev, ulong myId, string who)
        {
            if (ev.Scene != _p.SceneName) return;
            var parts = ev.Key.Split(':');
            if (parts.Length != 2 || !ulong.TryParse(parts[0], out var owner) || !uint.TryParse(parts[1], out var id)) return;
            var ra = Object.FindObjectOfType<RopeAnchor>();
            if (owner == myId)
            {
                var go = Find(owner, id, myId);
                if (go == null) return;
                Plugin.Log.LogInfo($"{who} pulled out our rope #{id}");
                _p.Say($"{who} pulled out your rope");
                if (ra != null && ra.attached && (GameAccess.AttachedRope(ra) == go || ra.cachedRope == go)) { ra.DisableAttached(); ra.cachedRope = null; Plugin.Log.LogInfo("we hung on it: falling"); } // we hung on it: we fall
                HideKnotForGood(go);
                _mine.Remove(go); Object.Destroy(go, 0.9f);
                if (ra != null) { ra.anchorsInBackpack++; ra.UpdateRopesAmount(useAnim: false); }
            }
            else if (_mirrors.ContainsKey((owner, id))) { _pulled[(owner, id)] = Time.unscaledTime + 10f; Remove((owner, id)); }
        }

        static readonly AccessTools.FieldRef<RopeAnchor, bool> _pullingOutAnchor = AccessTools.FieldRefAccess<RopeAnchor, bool>("pullingOutAnchor");
        static readonly AccessTools.FieldRef<RopeAnchor, bool> _allowNewRope = AccessTools.FieldRefAccess<RopeAnchor, bool>("allowNewRope");
        float _pullingSince = -1f;

        /// <summary>A pull-out never takes more than ~1 s; if the game's coroutine died, clear its flags.</summary>
        void PullOutWatchdog()
        {
            var ra = Object.FindObjectOfType<RopeAnchor>();
            if (ra == null) return;
            if (!_pullingOutAnchor(ra)) { _pullingSince = -1f; return; }
            if (_pullingSince < 0f) { _pullingSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _pullingSince < 3f) return;
            _pullingOutAnchor(ra) = false; _allowNewRope(ra) = true; ra.usingInteractionHammer = false;
            _pullingSince = -1f;
            Plugin.Log.LogInfo("pull-out got stuck: cleared");
        }

        void Spawn(ulong owner, RopeInfo info)
        {
            var point = RemotePlayer.ToV(info.Point); var fwd = RemotePlayer.ToV(info.Forward);
            var go = Object.Instantiate(_prefab, point, Quaternion.identity);
            go.name = "FoYRope_" + owner + "_" + info.Id; // not the clone name: never re-broadcast as ours
            var piton = Piton(go);
            piton.position = point; piton.forward = fwd;
            float prefabDrag = 0f; int n = 0;
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>())
            {
                if (rb.name != "rope_segment (end)" && rb.name != "rope_segment (piton)") rb.position = point - fwd * 0.5f;
                // the prefab's segments carry a high drag (the game lowers it to 0.5 once a rope is thrown and let go);
                // 0.7.0 mirrors kept the prefab value and sank like through honey
                prefabDrag = Mathf.Max(prefabDrag, rb.drag); rb.drag = 0.5f; n++;
            }
            Plugin.Log.LogInfo($"mirrored rope: {n} segments, prefab drag {prefabDrag:F2} -> 0.50");
            _mirrors[(owner, info.Id)] = go; _mirrorSet.Add(go); // piton stays tagged: anyone may pull it (0.9)
            Plugin.Log.LogInfo($"mirrored rope #{info.Id} from {owner} at {point}");
        }

        void Remove((ulong owner, uint id) k)
        {
            if (_mirrors.TryGetValue(k, out var go) && go != null)
            {
                HideKnotForGood(go);
                var ra = Object.FindObjectOfType<RopeAnchor>();
                if (ra != null && ra.attached && (GameAccess.AttachedRope(ra) == go || ra.cachedRope == go))
                {
                    // Let go first through the game's own detach, then remove the rope a moment later.
                    Plugin.Log.LogInfo("the rope you hang on was removed by its owner: detaching");
                    ra.DisableAttached();
                    ra.cachedRope = null;
                    _mirrorSet.Remove(go); Object.Destroy(go, 0.3f);
                }
                else { _mirrorSet.Remove(go); Object.Destroy(go); }
            }
            _mirrors.Remove(k);
            Plugin.Log.LogInfo($"mirrored rope #{k.id} from {k.owner} removed");
        }
    }
}
