using System.Linq;
using System;
using FriendsOfYore.Net;
using HarmonyLib;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// A fall to the death (blackout) is heard by everyone: the faller's game reports which hurt and thud clips it played,
    /// the others play the same clips from the faller's position as 3D sounds through the game's own mixer group.
    /// </summary>
    public static class FallSounds
    {
        public static Action<int, int> OnLocalFall;

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(FallingEvent), "FellToDeath"), postfix: new HarmonyMethod(typeof(FallSounds), nameof(Postfix)));
            h.Patch(AccessTools.Method(typeof(CustomPeakJournal), "RefreshAllSteamCompendiums"), postfix: new HarmonyMethod(typeof(FallSounds), nameof(BellPostfix)));
        }

        // ---- Workshop Outpost: the service bell (refresh the maps) is heard by everyone there (owner's request, 0.23.5) ----
        public static Action<Vector3> OnLocalBell;
        static void BellPostfix(CustomPeakJournal __instance)
        {
            try
            {
                if (__instance.globeRefreshCompendiumsSource == null) return;
                OnLocalBell?.Invoke(__instance.globeRefreshCompendiumsSource.transform.position);
            }
            catch (Exception e) { Plugin.Log.LogWarning("bell: " + e.Message); }
        }

        /// <summary>A friend rang it: our own bell rings (same place, same sound), the maps are not refreshed.</summary>
        static void RingBell()
        {
            var j = UnityEngine.Object.FindObjectOfType<CustomPeakJournal>();
            var src = j != null ? j.globeRefreshCompendiumsSource : null;
            if (src == null) { Plugin.Log.LogInfo("bell: no service bell here"); return; }
            src.Play();
            Plugin.Log.LogInfo("a friend rang the service bell");
        }

        static void Postfix(FallingEvent __instance)
        {
            try
            {
                int hurt = __instance.hurtClips != null && __instance.playerHurtSound != null ? Array.IndexOf(__instance.hurtClips, __instance.playerHurtSound.clip) : -1;
                int thud = __instance.fallThudClips != null && __instance.playerFallThudSound != null ? Array.IndexOf(__instance.fallThudClips, __instance.playerFallThudSound.clip) : -1;
                OnLocalFall?.Invoke(Mathf.Max(0, hurt), Mathf.Max(0, thud));
            }
            catch (Exception e) { Plugin.Log.LogWarning("fall sound: " + e.Message); }
        }

        // ---- falling into the sea: the game plays ResetPosition.waterSplashSound; we watch it start ----
        public static Action OnLocalSplash;
        static readonly HarmonyLib.AccessTools.FieldRef<ResetPosition, AudioSource> _splash = HarmonyLib.AccessTools.FieldRefAccess<ResetPosition, AudioSource>("waterSplashSound");
        static ResetPosition[] _seas = new ResetPosition[0]; static string _seaScene; static float _seaScanAt; static bool _wasSplashing;

        public static void Watch(string scene)
        {
            if (_seaScene != scene || Time.unscaledTime > _seaScanAt)
            {
                _seaScene = scene; _seaScanAt = Time.unscaledTime + 5f;
                _seas = Array.FindAll(UnityEngine.Object.FindObjectsOfType<ResetPosition>(), r => r != null && r.isSea);
            }
            bool splashing = false;
            foreach (var r in _seas) { if (r == null) continue; var a = _splash(r); if (a != null && a.isPlaying) { splashing = true; break; } }
            if (splashing && !_wasSplashing) OnLocalSplash?.Invoke();
            _wasSplashing = splashing;
        }

        static AudioSource SeaSource()
        {
            foreach (var r in UnityEngine.Object.FindObjectsOfType<ResetPosition>()) { if (r == null || !r.isSea) continue; var a = _splash(r); if (a != null && a.clip != null) return a; }
            return null;
        }

        // ---- the gull joke: ten arm swings in a row in mid-air make a gull cry ----
        static AudioClip[] _gullClips;

        /// <summary>
        /// Keeps our own copies of the gull cries (sample data copied into new clips), taken whenever gulls are loaded:
        /// a peak with gulls, or the background model loading in the cabins (Gray Gully, Alpine cabin). Then the cry works
        /// on every level, also where there are no gulls (0.21 only used the gulls of the current level).
        /// </summary>
        public static void CacheGullClips()
        {
            if (_gullClips != null && _gullClips.Length > 0 && _gullClips[0] != null) return;
            foreach (var b in Resources.FindObjectsOfTypeAll<Bird>())
            {
                if (b == null || b.isCrow || b.squakClips == null || b.squakClips.Length == 0) continue;
                var copies = new System.Collections.Generic.List<AudioClip>();
                foreach (var src in b.squakClips)
                {
                    if (src == null) continue;
                    try
                    {
                        if (src.loadState != AudioDataLoadState.Loaded) src.LoadAudioData();
                        var data = new float[src.samples * src.channels];
                        if (!src.GetData(data, 0)) continue;
                        var copy = AudioClip.Create("FoY_" + src.name, src.samples, src.channels, src.frequency, false);
                        copy.SetData(data, 0);
                        UnityEngine.Object.DontDestroyOnLoad(copy);
                        copies.Add(copy);
                    }
                    catch (Exception e) { Plugin.Log.LogInfo($"gull cry {src.name}: {e.Message}"); }
                }
                if (copies.Count == 0) continue;
                _gullClips = copies.ToArray();
                Plugin.Log.LogInfo($"gull cries kept: {copies.Count} (from {b.gameObject.scene.name})");
                return;
            }
        }

        // ---- a friend joins the lobby: the Cabin door opens and closes behind them (owner's request, 0.23.1) ----
        static AudioClip[] _doorClips; static UnityEngine.Audio.AudioMixerGroup _doorMixer; static float _doorVolume = 1f;

        static AudioClip CopyClip(AudioClip src)
        {
            if (src == null) return null;
            if (src.loadState != AudioDataLoadState.Loaded) src.LoadAudioData();
            var data = new float[src.samples * src.channels];
            if (!src.GetData(data, 0)) return null;
            var copy = AudioClip.Create("FoY_" + src.name, src.samples, src.channels, src.frequency, false);
            copy.SetData(data, 0);
            UnityEngine.Object.DontDestroyOnLoad(copy);
            return copy;
        }

        public static void CacheDoorClips()
        {
            if (_doorClips != null && _doorClips.Length >= 2 && _doorClips[0] != null) return;
            foreach (var n in Resources.FindObjectsOfTypeAll<NPCEvents>())
            {
                if (n == null || n.doorClip == null || n.doorClip.Length < 2) continue;
                try
                {
                    var open = CopyClip(n.doorClip[0]); var close = CopyClip(n.doorClip[1]);
                    if (open == null) continue;
                    _doorClips = close != null ? new[] { open, close } : new[] { open };
                    var src = n.GetComponent<AudioSource>();
                    if (src != null) { _doorMixer = src.outputAudioMixerGroup; _doorVolume = Mathf.Max(0.6f, src.volume); }
                    Plugin.Log.LogInfo($"door sounds kept: {n.doorClip[0]?.name}, {n.doorClip[1]?.name}");
                    return;
                }
                catch (Exception e) { Plugin.Log.LogInfo("door sound: " + e.Message); }
            }
        }

        /// <summary>Plays for us (not in the world): the door opens, the friend steps in, it shuts.</summary>
        public static void PlayDoor()
        {
            if (_doorClips == null || _doorClips.Length == 0 || _doorClips[0] == null) CacheDoorClips();
            if (_doorClips == null || _doorClips.Length == 0 || _doorClips[0] == null) { Plugin.Log.LogInfo("door sound: not found yet (it is kept from the Cabin)"); return; }
            var go = new GameObject("FoY_DoorSound"); UnityEngine.Object.DontDestroyOnLoad(go);
            float t = 0f;
            for (int i = 0; i < _doorClips.Length; i++)
            {
                var a = go.AddComponent<AudioSource>();
                a.clip = _doorClips[i]; a.outputAudioMixerGroup = _doorMixer; a.volume = _doorVolume; a.spatialBlend = 0f;
                a.pitch = UnityEngine.Random.Range(1.05f, 1.1f); // like the game's door
                a.PlayDelayed(t);
                t += i == 0 ? 1.6f : 0f; // a moment to come in, then the door shuts
            }
            UnityEngine.Object.Destroy(go, t + _doorClips.Max(c => c.length) + 0.5f);
        }

        static bool FindGullClips()
        {
            if (_gullClips == null || _gullClips.Length == 0 || _gullClips[0] == null) CacheGullClips();
            return _gullClips != null && _gullClips.Length > 0 && _gullClips[0] != null;
        }

        public static bool PlayGull(Vector3 at)
        {
            if (!FindGullClips()) return false;
            PlayAt(_gullClips[UnityEngine.Random.Range(0, _gullClips.Length)], null, at, UnityEngine.Random.Range(0.9f, 1.1f));
            return true;
        }

        public static void Play(WorldEvent ev, RemotePlayer rp)
        {
            if (ev.Key == "bell") { RingBell(); return; }
            if (ev.Key == "gull") { PlayGull(rp != null && rp.Shown != null ? RemotePlayer.ToV(rp.Shown.Body) : RemotePlayer.ToV(ev.Pos)); return; }
            if (ev.Key == "splash")
            {
                var src = SeaSource();
                var at = rp != null && rp.Shown != null ? RemotePlayer.ToV(rp.Shown.Body) : RemotePlayer.ToV(ev.Pos);
                if (src != null) { PlayAt(src.clip, src, at, UnityEngine.Random.Range(0.8f, 1.05f)); Plugin.Log.LogInfo($"{(rp != null ? rp.DisplayName : "a friend")} fell into the water: playing the splash at {at}"); }
                return;
            }
            var fe = UnityEngine.Object.FindObjectOfType<FallingEvent>();
            if (fe == null) return;
            var pos = rp != null && rp.Shown != null ? RemotePlayer.ToV(rp.Shown.Body) : RemotePlayer.ToV(ev.Pos);
            int hurt = ev.Index / 100, thud = ev.Index % 100;
            if (fe.hurtClips != null && fe.hurtClips.Length > 0) PlayAt(fe.hurtClips[Mathf.Clamp(hurt, 0, fe.hurtClips.Length - 1)], fe.playerHurtSound, pos, UnityEngine.Random.Range(0.9f, 1.04f));
            if (fe.fallThudClips != null && fe.fallThudClips.Length > 0) PlayAt(fe.fallThudClips[Mathf.Clamp(thud, 0, fe.fallThudClips.Length - 1)], fe.playerFallThudSound, pos, UnityEngine.Random.Range(0.85f, 1f));
            Plugin.Log.LogInfo($"{(rp != null ? rp.DisplayName : "a friend")} fell to a blackout: playing the fall at {pos}");
        }

        static void PlayAt(AudioClip clip, AudioSource like, Vector3 pos, float pitch)
        {
            if (clip == null) return;
            var go = new GameObject("FoY_FallSound");
            go.transform.position = pos;
            var a = go.AddComponent<AudioSource>();
            a.clip = clip; a.pitch = pitch;
            if (like != null) { a.outputAudioMixerGroup = like.outputAudioMixerGroup; a.volume = Mathf.Max(like.volume, 0.8f); } else a.volume = 1f;
            a.spatialBlend = 1f; a.rolloffMode = AudioRolloffMode.Linear; a.minDistance = 5f; a.maxDistance = 200f; a.dopplerLevel = 0f;
            // the same "Mountains" echo as voices, stronger and duller the further away it happens
            float far = Camera.main != null ? Mathf.Clamp01((Vector3.Distance(Camera.main.transform.position, pos) - 5f) / 60f) : 0.5f;
            var lp = go.AddComponent<AudioLowPassFilter>(); lp.cutoffFrequency = Mathf.Lerp(22000f, 5000f, far); lp.lowpassResonanceQ = 1f;
            var rv = go.AddComponent<AudioReverbFilter>(); rv.reverbPreset = AudioReverbPreset.Mountains;
            rv.dryLevel = Mathf.Lerp(0f, -600f, far); rv.reverbLevel = Mathf.Lerp(-500f, 200f, far);
            a.Play();
            UnityEngine.Object.Destroy(go, clip.length / pitch + 4f); // keep the echo tail
        }
    }
}
