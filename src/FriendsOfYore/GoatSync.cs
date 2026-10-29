using System;
using System.Collections.Generic;
using System.Linq;
using FriendsOfYore.Net;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// The wandering goats of the Alps DLC on a shared seed (owner's design, 0.28).
    /// The game's goats pick perches, waits and idle moves with Random and frame times, so every PC had its own goats.
    /// Now the first player on a peak makes a seed and a start time and sends them to everyone; whoever arrives later
    /// asks and gets the same pair. Each goat's whole life is then a fixed list of steps made from that seed (next perch,
    /// short or long jump, lick / look around / wait), placed on the computer clock (UTC) from the start time. Every PC
    /// shows the step the clock is in, so the goats are in the same place everywhere, also for a late arrival, after a
    /// pause or alt-tab. The game's goat script is stopped (no coroutines); its hand knock (a goat pushes a hand off a hold)
    /// and its sounds are kept.
    /// </summary>
    public class GoatSync
    {
        class Step
        {
            public double Start, JumpEnd, End;     // seconds from the start time
            public float YawFrom, YawTo;
            public Vector3 From, To; public bool Long, FacingWall;
            public int IdleKind; public double IdleStart, IdleEnd; // 0 none, 1 lick L, 2 lick R, 3 idle1
            public bool Bleat;
        }

        class G
        {
            public Goat Goat; public string Key; public int Index;
            public System.Random Rng; public List<Step> Steps = new List<Step>();
            public Vector3 Home; public Quaternion HomeRot, ObjRot0;
            public int WpIdx, WpVal;
            public float Yaw;
            public bool LandedSoundPlayed;
        }

        readonly List<G> _goats = new List<G>();
        string _scene; float _scanAt;
        // seed per scene: (seed, start time); known seeds are answered to anyone who asks
        readonly Dictionary<string, (int seed, double epoch)> _seeds = new Dictionary<string, (int, double)>();
        float _askedAt = -1f; bool _running;

        public Action<string, string> Broadcast;      // kind, arg (scene in the event)
        public Action<ulong, string, string> SendTo;   // peer, kind, arg

        public void OnSceneLoaded() { _goats.Clear(); _scene = null; _scanAt = Time.unscaledTime + 1.5f; _askedAt = -1f; _running = false; }

        void Scan(string scene)
        {
            _scene = scene; _goats.Clear();
            int i = 0;
            foreach (var goat in UnityEngine.Object.FindObjectsOfType<Goat>().OrderBy(g => WorldSync.KeyOf(g.transform)))
            {
                if (goat == null || goat.isCustomLevel || goat.goatAnim == null || goat.gameObject.scene.name != scene) continue;
                if (goat.waypointList == null || goat.waypointList.Length == 0) continue;
                _goats.Add(new G { Goat = goat, Key = WorldSync.KeyOf(goat.transform), Index = i++ });
            }
            if (_goats.Count > 0) Plugin.Log.LogInfo($"goats: {_goats.Count} wandering goat(s) in {scene}");
        }

        /// <summary>Every frame.</summary>
        public void Tick(ITransport net, string scene)
        {
            if (_scene != scene) { if (Time.unscaledTime < _scanAt) return; Scan(scene); }
            if (_goats.Count == 0) return;
            if (!_running)
            {
                if (_seeds.TryGetValue(scene, out var known)) { Begin(known.seed, known.epoch); return; }
                bool alone = net == null || net.Peers.Count == 0;
                if (!alone && _askedAt < 0f) { _askedAt = Time.unscaledTime; Broadcast?.Invoke("goatask", ""); Plugin.Log.LogInfo("goats: asking friends for the goat seed"); return; }
                if (alone || Time.unscaledTime - _askedAt > 1.5f)
                {   // nobody here knows it: make it and tell everyone
                    int seed = UnityEngine.Random.Range(1, int.MaxValue); double epoch = Math.Floor(SharedClock.Now);
                    _seeds[scene] = (seed, epoch);
                    Broadcast?.Invoke("goatseed", $"{seed};{epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    Plugin.Log.LogInfo($"goats: new seed {seed} for {scene}");
                    Begin(seed, epoch);
                }
                return;
            }
            Drive();
        }

        // ---- seed messages ----
        public void OnAsk(ulong from, string scene)
        {
            if (_seeds.TryGetValue(scene, out var s))
                SendTo?.Invoke(from, "goatseed", $"{s.seed};{s.epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        public void OnSeed(string scene, string arg)
        {
            var p = arg.Split(';'); if (p.Length < 2) return;
            if (!int.TryParse(p[0], out var seed) || !double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var epoch)) return;
            if (_seeds.TryGetValue(scene, out var cur))
            {   // two players made one at the same moment: the older start time (then the lower seed) wins everywhere
                if (cur.seed == seed && Math.Abs(cur.epoch - epoch) < 0.5) return;
                if (cur.epoch < epoch || (Math.Abs(cur.epoch - epoch) < 0.5 && cur.seed < seed)) return;
            }
            _seeds[scene] = (seed, epoch);
            Plugin.Log.LogInfo($"goats: seed {seed} for {scene} from a friend");
            if (scene == _scene && _goats.Count > 0) Begin(seed, epoch);
        }

        // ---- the deterministic goat ----
        double _epoch;

        void Begin(int seed, double epoch)
        {
            _epoch = epoch; _running = true;
            foreach (var g in _goats)
            {
                var goat = g.Goat;
                if (g.Steps.Count == 0) { g.Home = goat.transform.position; g.HomeRot = goat.transform.rotation; g.ObjRot0 = goat.goatObjectToRotate != null ? goat.goatObjectToRotate.localRotation : Quaternion.identity; }
                goat.StopAllCoroutines(); goat.enabled = false;           // the game's own wandering stops on every PC
                g.Rng = new System.Random(seed ^ (g.Index * 7919 + 17));
                g.Steps.Clear(); g.WpIdx = 0; g.WpVal = 0; g.Yaw = goat.transform.eulerAngles.y;
                if (goat.goatParentTransformToRotate != null) { goat.goatParentTransformToRotate.localPosition = Vector3.zero; goat.goatParentTransformToRotate.localRotation = Quaternion.identity; }
                var a = goat.goatAnim;
                foreach (var c in new[] { "Goat_Jump_long", "Goat_Jump_short", "Goat_WallLick_L", "Goat_WallLick_R", "Goat_Idle1" })
                { var st = a[c]; if (st != null) { st.layer = 1; st.speed = 0f; st.enabled = false; st.weight = 0f; } }
                var idle = a["Goat_Idle"]; if (idle != null) { idle.layer = 0; idle.wrapMode = WrapMode.Loop; idle.speed = 0f; idle.enabled = true; idle.weight = 1f; }
            }
            Plugin.Log.LogInfo($"goats: following seed {seed}, started {SharedClock.Now - epoch:F0} s ago");
        }

        Step MakeStep(G g, double start, Vector3 from)
        {
            var goat = g.Goat; var wps = goat.waypointList; var r = g.Rng;
            Vector3 to;
            if (!goat.useRandomWaypoint) { g.WpIdx = g.WpIdx < wps.Length - 1 ? g.WpIdx + 1 : 0; to = wps[g.WpIdx].position; }
            else
            {
                int prev = g.WpVal, n = wps.Length, v;
                if (n <= 1) v = 0; else { do v = r.Next(0, n); while (v == prev); }
                g.WpVal = v; to = wps[v].position;
            }
            var wallOut = goat.wallOutDirZ != null ? goat.wallOutDirZ.forward : Vector3.zero;
            to += wallOut * goat.finalWallOffset;
            bool lng = Vector3.Distance(from, to) > 3f || to.y > from.y + 2f;
            float speed = Mathf.Max(0.05f, lng ? goat.lerpSpeed : goat.lerpSpeed * 1.5f);
            var flatDir = to - from; flatDir.y = 0f;
            float yawFrom = g.Steps.Count > 0 ? g.Steps[g.Steps.Count - 1].YawTo : g.HomeRot.eulerAngles.y;
            float yawTo = flatDir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(flatDir).eulerAngles.y : yawFrom;
            var s = new Step { Start = start, From = from, To = to, Long = lng, FacingWall = r.Next(0, 2) == 0, YawFrom = yawFrom, YawTo = yawTo };
            s.JumpEnd = start + goat.jumpDelay + 1.0 / speed;
            double t = s.JumpEnd;
            if (goat.useIdle)
            {
                if (r.Next(0, 100) < 25) { s.IdleKind = r.Next(0, 2) == 0 ? 1 : 2; s.IdleStart = t; t += r.Next(4, 6); s.IdleEnd = t; }
                else if (r.Next(0, 100) < 25) { s.IdleKind = 3; s.IdleStart = t; s.Bleat = r.Next(0, 100) < 45; t += r.Next(3, 7); s.IdleEnd = t; }
            }
            t += goat.waitMin + r.NextDouble() * Math.Max(0f, goat.waitMax - goat.waitMin);
            s.End = t;
            return s;
        }

        void Drive()
        {
            double now = SharedClock.Now - _epoch;
            foreach (var g in _goats)
            {
                if (g.Goat == null) continue;
                // make steps up to now (a late arrival makes them all at once; a few thousand per hour at most)
                if (g.Steps.Count == 0) g.Steps.Add(MakeStep(g, 0, g.Home));
                int guard = 0;
                while (g.Steps[g.Steps.Count - 1].End <= now && guard++ < 5000) { var last = g.Steps[g.Steps.Count - 1]; g.Steps.Add(MakeStep(g, last.End, last.To)); }
                if (g.Steps.Count > 64) g.Steps.RemoveRange(0, g.Steps.Count - 64);
                var s = g.Steps[g.Steps.Count - 1];
                for (int i = g.Steps.Count - 1; i >= 0; i--) if (g.Steps[i].Start <= now) { s = g.Steps[i]; break; }
                Apply(g, s, now);
            }
        }

        void Apply(G g, Step s, double now)
        {
            var goat = g.Goat; var t = goat.transform; var a = goat.goatAnim;
            bool newStep = !_lastStep.TryGetValue(g, out var ls) || !ReferenceEquals(ls, s);
            if (newStep) { _lastStep[g] = s; g.LandedSoundPlayed = false; if (now - s.Start < 0.5) Sound(goat, "PlayJumpSound"); }
            float jumpLen = (float)(s.JumpEnd - s.Start - goat.jumpDelay);
            double jt = now - s.Start - goat.jumpDelay;
            g.Yaw = Mathf.LerpAngle(s.YawFrom, s.YawTo, Mathf.Clamp01((float)((now - s.Start) / 0.5)));   // turns to the next perch in half a second
            t.rotation = Quaternion.Euler(0f, g.Yaw, 0f);
            var jl = a["Goat_Jump_long"]; var js = a["Goat_Jump_short"]; var jump = s.Long ? jl : js; var other = s.Long ? js : jl;
            if (other != null) { other.enabled = false; other.weight = 0f; }
            if (now < s.JumpEnd)
            {
                float e = jt <= 0 ? 0f : Mathf.Clamp01((float)(jt / Math.Max(0.05, jumpLen)));
                t.position = Vector3.Lerp(s.From, new Vector3(s.To.x, s.To.y + goat.finalPosYOffset + (goat.jumpCurve != null ? goat.jumpCurve.Evaluate(e) : 0f), s.To.z), e);
                if (jump != null)
                {
                    float aSpeed = goat.animationJumpLerpSpeed * (s.Long ? 1f : 1.3f);
                    float ae = Mathf.Clamp01((float)((now - s.Start) * Math.Max(0.05f, aSpeed)));
                    jump.enabled = true; jump.weight = 1f; jump.normalizedTime = goat.animationJumpCurve != null ? goat.animationJumpCurve.Evaluate(ae) : ae;
                }
                if (goat.goatObjectToRotate != null && goat.jumpRotation != null && goat.jumpRotationCurve != null)
                    goat.goatObjectToRotate.localRotation = Quaternion.Lerp(g.ObjRot0, goat.jumpRotation.localRotation, goat.jumpRotationCurve.Evaluate(e));
                SetLayer1(a, null, 0f, 0f);
            }
            else
            {
                t.position = new Vector3(s.To.x, s.To.y + goat.finalPosYOffset, s.To.z);
                if (!g.LandedSoundPlayed) { g.LandedSoundPlayed = true; if (now - s.JumpEnd < 0.5) Sound(goat, "PlayLandSound"); }
                if (jump != null) { jump.weight = Mathf.Max(0f, 1f - (float)(now - s.JumpEnd) / 0.4f); jump.enabled = jump.weight > 0f; }
                if (goat.goatObjectToRotate != null) goat.goatObjectToRotate.localRotation = Quaternion.Lerp(goat.goatObjectToRotate.localRotation, g.ObjRot0, 0.2f);
                if (s.IdleKind != 0 && now >= s.IdleStart && now < s.IdleEnd)
                {
                    string clip = s.IdleKind == 1 ? "Goat_WallLick_L" : s.IdleKind == 2 ? "Goat_WallLick_R" : "Goat_Idle1";
                    float w = Mathf.Clamp01((float)Math.Min(now - s.IdleStart, s.IdleEnd - now) / 1.2f);
                    SetLayer1(a, clip, w, (float)(now - s.IdleStart));
                    if (s.Bleat && !_bleated.Contains(s) && now - s.IdleStart > 0.5) { _bleated.Add(s); if (now - s.IdleStart < 1.5) Sound(goat, "PlayBleatSound"); }
                }
                else SetLayer1(a, null, 0f, 0f);
            }
            var idle = a["Goat_Idle"]; if (idle != null && idle.length > 0f) { idle.enabled = true; idle.weight = 1f; idle.time = (float)(now % idle.length); }
        }

        readonly Dictionary<G, Step> _lastStep = new Dictionary<G, Step>();
        readonly HashSet<Step> _bleated = new HashSet<Step>();

        static void SetLayer1(Animation a, string clip, float w, float time)
        {
            foreach (var c in new[] { "Goat_WallLick_L", "Goat_WallLick_R", "Goat_Idle1" })
            {
                var st = a[c]; if (st == null) continue;
                if (c == clip && w > 0f) { st.enabled = true; st.weight = w; st.time = st.length > 0f ? time % st.length : 0f; }
                else if (st.enabled) { st.weight = 0f; st.enabled = false; }
            }
        }

        static void Sound(Goat goat, string method)
        {
            try { HarmonyLib.AccessTools.Method(typeof(Goat), method)?.Invoke(goat, null); } catch { }
        }
    }
}
