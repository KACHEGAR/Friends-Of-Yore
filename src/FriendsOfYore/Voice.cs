using System;
using System.Collections.Generic;
using System.Linq;
using FriendsOfYore.Net;
using Steamworks;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// Proximity voice chat over Steam voice: the microphone is captured and compressed by Steam (SteamUser.GetVoice),
    /// sent to the lobby as unreliable packets and played back as a 3D sound from the friend's head, so it gets quieter
    /// with distance and is silent beyond MaxDistance. Friends in another place are not heard.
    /// </summary>
    public class Voice
    {
        public bool Enabled = true, PushToTalk;
        public KeyCode PushKey = KeyCode.B;
        public string PushPad = ""; // gamepad button for push-to-talk (0.26)
        bool PushHeld => Input.GetKey(PushKey) || PadInput.Button(PushPad);
        public float MaxDistance = 50f, Volume = 1f; // hearing distance fixed at 50 m (owner's choice)
        public bool TestMode;
        /// <summary>Per-friend volume (Steam id -> 0..3), saved in the config.</summary>
        public readonly Dictionary<ulong, float> PlayerGains = new Dictionary<ulong, float>();
        public float PlayerGain(ulong id) => PlayerGains.TryGetValue(id, out var g) ? g : 1f;
        public string SaveGains() => string.Join(";", PlayerGains.Select(kv => kv.Key + ":" + kv.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));
        public void LoadGains(string s)
        {
            PlayerGains.Clear();
            foreach (var part in (s ?? "").Split(';'))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && ulong.TryParse(kv[0], out var id) && float.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g)) PlayerGains[id] = Mathf.Clamp(g, 0f, 3f);
            }
        }
        /// <summary>Distance effect: a farther friend sounds more echoing (mountain reverb) and more muffled.</summary>
        public static string EchoType = "Mountains"; public static float EchoStrength = 1f;
        public static bool Echo => EchoType != "Off";
        const int Rate = 24000;
        readonly byte[] _capture = new byte[16 * 1024];
        readonly byte[] _pcm = new byte[Rate * 2 * 2];
        bool _recording;
        public float LastSpoke = -9f;
        readonly Dictionary<ulong, VoicePlayer> _players = new Dictionary<ulong, VoicePlayer>();

        public bool Talking => Time.unscaledTime - LastSpoke < 0.3f;

        /// <summary>Capture and send; called every frame.</summary>
        public void Update(ITransport net, bool inGame)
        {
            // TestMode: the settings page is open, the microphone runs so you can see it pick you up (nothing is sent alone)
            bool want = Enabled && net is SteamTransport && (TestMode || (net.Peers.Count > 0 && inGame && (!PushToTalk || PushHeld)));
            try
            {
                if (want && !_recording) { SteamUser.StartVoiceRecording(); _recording = true; }
                if (!want && _recording) { SteamUser.StopVoiceRecording(); _recording = false; }
                // keep reading after stopping: Steam flushes the last ~0.25 s
                for (int guard = 0; guard < 4; guard++)
                {
                    var res = SteamUser.GetAvailableVoice(out uint size);
                    if (res != EVoiceResult.k_EVoiceResultOK || size == 0) break;
                    res = SteamUser.GetVoice(true, _capture, (uint)_capture.Length, out uint written);
                    if (res != EVoiceResult.k_EVoiceResultOK || written == 0) break;
                    var pkt = new byte[written + 2];
                    pkt[0] = Protocol.Version; pkt[1] = Protocol.MsgVoice;
                    Buffer.BlockCopy(_capture, 0, pkt, 2, (int)written);
                    if (net != null && net.Peers.Count > 0 && (!TestMode || !PushToTalk || PushHeld)) net.Broadcast(pkt, false);
                    LastSpoke = Time.unscaledTime;
                }
            }
            catch (Exception e) { if (!_warned) { _warned = true; Plugin.Log.LogWarning("voice capture: " + e.Message); } }
        }
        bool _warned;

        public void Receive(RemotePlayer rp, byte[] data, int offset)
        {
            if (rp == null || !Enabled) return;
            int len = data.Length - offset;
            if (len <= 0) return;
            var comp = new byte[len]; Buffer.BlockCopy(data, offset, comp, 0, len);
            var res = SteamUser.DecompressVoice(comp, (uint)len, _pcm, (uint)_pcm.Length, out uint written, Rate);
            if (res != EVoiceResult.k_EVoiceResultOK || written < 2) return;
            rp.LastVoice = Time.unscaledTime;
            if (!rp.Visible) return; // another place: not heard
            if (!_players.TryGetValue(rp.Id, out var vp) || vp == null)
            {
                vp = rp.gameObject.AddComponent<VoicePlayer>();
                vp.Init(Rate);
                _players[rp.Id] = vp;
            }
            vp.Source.maxDistance = MaxDistance; vp.Source.volume = 1f;
            vp.Gain = Mathf.Clamp(Volume, 0f, 3f) * PlayerGain(rp.Id); // up to 300% x 300%: samples are amplified (and clipped)
            vp.Push(_pcm, (int)written);
        }

        public void Stop() { if (_recording) { try { SteamUser.StopVoiceRecording(); } catch { } _recording = false; } }
    }

    /// <summary>Streams decoded voice into a looping 3D AudioClip through a small ring buffer.</summary>
    public class VoicePlayer : MonoBehaviour
    {
        public AudioSource Source;
        public float Gain = 1f;
        GameObject _emitter;
        float[] _ring; int _read, _write, _count; readonly object _lock = new object();
        bool _primed;

        public void Init(int rate)
        {
            _ring = new float[rate * 2];
            _emitter = new GameObject("FoY_Voice");
            _emitter.transform.SetParent(transform, false);
            Source = _emitter.AddComponent<AudioSource>();
            Source.clip = AudioClip.Create("FoY_VoiceStream", rate, 1, rate, true, OnRead);
            Source.loop = true; Source.spatialBlend = 1f; Source.rolloffMode = AudioRolloffMode.Linear;
            Source.minDistance = 2f; Source.maxDistance = 40f; Source.dopplerLevel = 0f; Source.spread = 0f;
            Source.Play();
            _lowpass = _emitter.AddComponent<AudioLowPassFilter>(); _lowpass.cutoffFrequency = 22000f; _lowpass.lowpassResonanceQ = 1f;
            _reverb = _emitter.AddComponent<AudioReverbFilter>();
        }

        string _appliedType;
        float _presetRoom, _presetReverb, _presetReflections;

        /// <summary>Loads a Unity reverb preset (its decay, delays, diffusion...) and remembers its levels for the mix.</summary>
        void ApplyType(string type)
        {
            _appliedType = type;
            AudioReverbPreset p;
            switch (type)
            {
                case "Mountains": p = AudioReverbPreset.Mountains; break;
                case "Quarry": p = AudioReverbPreset.Quarry; break;
                case "Forest": p = AudioReverbPreset.Forest; break;
                case "Cave": p = AudioReverbPreset.Cave; break;
                case "Arena": p = AudioReverbPreset.Arena; break;
                case "StoneCorridor": p = AudioReverbPreset.StoneCorridor; break;
                case "Generic": p = AudioReverbPreset.Generic; break;
                case "Valley": p = AudioReverbPreset.Mountains; break;
                default: p = AudioReverbPreset.Plain; break;
            }
            _reverb.reverbPreset = p;
            _presetRoom = _reverb.room; _presetReverb = _reverb.reverbLevel; _presetReflections = _reverb.reflectionsLevel;
            if (type == "Valley") { _reverb.reverbPreset = AudioReverbPreset.User; _reverb.decayTime = 4.5f; _reverb.reverbDelay = 0.09f; _reverb.reflectionsDelay = 0.12f; _presetReverb = Mathf.Max(_presetReverb, 0f); }
            if (type == "Plain") _presetReverb = Mathf.Max(_presetReverb, -600f); // the Plain preset alone is very faint
            Plugin.Log.LogInfo($"voice echo: {type} (room {_presetRoom:F0}, reverb {_presetReverb:F0}, decay {_reverb.decayTime:F1} s)");
        }

        AudioLowPassFilter _lowpass; AudioReverbFilter _reverb;
        float _mix = -1f;

        /// <summary>Near: dry and clear. Far: quieter direct sound, more echo, fewer highs (smoothed, per frame).</summary>
        void Update()
        {
            if (_reverb == null || _lowpass == null) return;
            var l = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            float max = Source != null ? Mathf.Max(5f, Source.maxDistance) : 40f;
            float t = Voice.Echo ? Mathf.Clamp01((Vector3.Distance(l, Emitter.position) - 3f) / (max - 3f)) * Mathf.Clamp(Voice.EchoStrength, 0f, 2f) : 0f;
            t = Mathf.Clamp01(t);
            _mix = _mix < 0f ? t : Mathf.MoveTowards(_mix, t, Time.unscaledDeltaTime * 0.8f);
            _reverb.enabled = Voice.Echo;
            if (!Voice.Echo) { _lowpass.cutoffFrequency = 22000f; return; }
            if (_appliedType != Voice.EchoType) ApplyType(Voice.EchoType);
            // the preset's own levels at the far end, almost nothing close by (millibels)
            _reverb.dryLevel = Mathf.Lerp(0f, -900f, _mix);
            _reverb.room = Mathf.Lerp(-4000f, Mathf.Max(_presetRoom, -1000f), Mathf.Sqrt(_mix));
            _reverb.reverbLevel = Mathf.Lerp(-2500f, _presetReverb + 300f, _mix);
            _reverb.reflectionsLevel = Mathf.Lerp(-3000f, _presetReflections, _mix);
            _lowpass.cutoffFrequency = Voice.Echo ? Mathf.Lerp(22000f, 2500f, _mix * _mix) : 22000f;
        }

        public Transform Emitter => _emitter != null ? _emitter.transform : transform;

        public void Push(byte[] pcm16, int bytes)
        {
            lock (_lock)
            {
                for (int i = 0; i + 1 < bytes; i += 2)
                {
                    short v = (short)(pcm16[i] | (pcm16[i + 1] << 8));
                    if (_count == _ring.Length) { _read = (_read + 1) % _ring.Length; _count--; } // overflow: drop the oldest
                    _ring[_write] = v / 32768f; _write = (_write + 1) % _ring.Length; _count++;
                }
            }
        }

        void OnRead(float[] data)
        {
            lock (_lock)
            {
                if (!_primed && _count < 2400) { Array.Clear(data, 0, data.Length); return; } // ~100 ms jitter buffer
                _primed = true;
                for (int i = 0; i < data.Length; i++)
                {
                    if (_count > 0) { data[i] = Mathf.Clamp(_ring[_read] * Gain, -1f, 1f); _read = (_read + 1) % _ring.Length; _count--; }
                    else { data[i] = 0f; _primed = false; }
                }
            }
        }
    }
}
