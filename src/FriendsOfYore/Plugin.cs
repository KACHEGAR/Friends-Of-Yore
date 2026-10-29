using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FriendsOfYore.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FriendsOfYore
{
    /// <summary>
    /// Friends of Yore: co-op for Peaks of Yore over a friends-only Steam lobby. This file holds the config, the Unity
    /// lifecycle and the per-frame order of work; the other Plugin.*.cs files hold one concern each (input, local
    /// player, hands, places, network, UI, preload). The bigger systems live in their own classes (WorldSync,
    /// RopeSync, GoatSync, Voice, PauseList, VoiceMenu...).
    /// </summary>
    [BepInPlugin("friendsofyore", "Friends of Yore", Version)]
    public partial class Plugin : BaseUnityPlugin
    {
        public const string Version = "0.29.3";
        internal static ManualLogSource Log;

        // ---- config ----
        ConfigEntry<string> _cfgTransport, _cfgUdpPeers, _cfgMyModel;
        ConfigEntry<int> _cfgUdpPort, _cfgSendRate;
        ConfigEntry<KeyCode> _keyHost, _keyLeave, _keyMenu, _keyMark;
        ConfigEntry<bool> _cfgTrace;

        // ---- parts ----
        ITransport _net;
        RopeSync _ropes;
        WorldSync _world;
        VoiceMenu _voiceMenu;
        readonly Voice _voice = new Voice();
        readonly Marks _marks = new Marks();
        readonly Trace _trace = new Trace();
        readonly ActivityDetector _activity = new ActivityDetector();
        readonly ClockSync _clock = new ClockSync();
        readonly MenuInvite _menuInvite = new MenuInvite();
        internal readonly Dictionary<ulong, RemotePlayer> Remotes = new Dictionary<ulong, RemotePlayer>();

        // ---- state ----
        string _scene = "";
        string _disabledReason;
        uint _seq;
        float _sendTimer, _fontRetryIn = -1f;
        int _pingCounter, _captureFrames;
        bool _showOverlay = true;
        string _banner; float _bannerUntil;
        static readonly Color[] Palette = { new Color(0.9f, 0.35f, 0.25f), new Color(0.25f, 0.55f, 0.95f), new Color(0.35f, 0.8f, 0.35f), new Color(0.95f, 0.8f, 0.2f) };

        internal string SceneName => _scene;
        internal ITransport Net => _net;
        internal void Say(string text) => Banner(text);
        void Banner(string text, float seconds = 3f) { _banner = text; _bannerUntil = Time.unscaledTime + seconds; }
        bool MenuOnScreen => _menuInvite.PauseOpen || _inviteOpen || (_voiceMenu != null && _voiceMenu.Open);

        void Awake()
        {
            Log = Logger;
            _cfgTransport = Config.Bind("Network", "Transport", "steam", "steam = friends-only Steam lobby (normal play). udp = loopback/LAN for tests.");
            _cfgUdpPort = Config.Bind("Network", "UdpPort", 27650, "udp transport: local port.");
            _cfgUdpPeers = Config.Bind("Network", "UdpPeers", "", "udp transport: comma separated ip:port of other instances.");
            _cfgSendRate = Config.Bind("Network", "SendRate", 20, "State packets per second.");
            _cfgMyModel = Config.Bind("Avatar", "MyModel", "NPC_PARCELFATHER", "How YOU look to other players (an NPC body of the game). Picked in ESC - Multiplayer Settings - Look.");
            _keyHost = Config.Bind("Keys", "HostOrInvite", KeyCode.F5, "Create a lobby / open the invite list.");
            _keyLeave = Config.Bind("Keys", "LeaveLobby", KeyCode.F6, "Leave the lobby.");
            _keyMenu = Config.Bind("Keys", "ToggleOverlay", KeyCode.F7, "Show or hide the co-op status text.");
            _keyMark = Config.Bind("Keys", "Mark", KeyCode.Mouse2, "Put a mark where you look; every player sees it for 20 seconds.");
            var vEn = Config.Bind("Voice", "Enabled", true, "Proximity voice chat through Steam voice: friends hear you from where your character is.");
            var vPtt = Config.Bind("Voice", "PushToTalk", false, "false = open microphone (Steam only sends while you speak); true = talk only while the key is held.");
            var vKey = _pttKey = Config.Bind("Voice", "PushToTalkKey", KeyCode.B, "Key for push-to-talk.");
            var vKeyMoved = Config.Bind("Voice", "PushToTalkDefaultB", false, "Internal: the old default V was moved to B once (0.26).");
            if (!vKeyMoved.Value) { if (vKey.Value == KeyCode.V) vKey.Value = KeyCode.B; vKeyMoved.Value = true; }
            var vVol = Config.Bind("Voice", "Volume", 1f, "All friends' voice volume (0-3 = 0-300%).");
            var vGains = Config.Bind("Voice", "PlayerVolumes", "", "Per-friend volume, steamid:gain;... (set in Multiplayer Settings - Voice Chat).");
            _voice.LoadGains(vGains.Value);
            var vEcho = Config.Bind("Voice", "EchoType", "Mountains", "Distance echo for far friends: Mountains or Off.");
            Voice.EchoStrength = Config.Bind("Voice", "EchoStrength", 1f, "How strong the distance echo is (0-2).").Value;
            Voice.EchoType = vEcho.Value == "Off" ? "Off" : "Mountains"; // the owner picked Mountains after trying them all
            _voice.Enabled = vEn.Value; _voice.PushToTalk = vPtt.Value; _voice.PushKey = vKey.Value; _voice.Volume = vVol.Value;
            _voiceMenu = new VoiceMenu(_voice);
            _voiceMenu.Changed = () => { vEn.Value = _voice.Enabled; vPtt.Value = _voice.PushToTalk; vVol.Value = _voice.Volume; vEcho.Value = Voice.EchoType; vGains.Value = _voice.SaveGains(); };
            _voiceMenu.Players = () => Remotes.Values.Select(r => (r.Id, r.DisplayName)).ToList();
            _voiceMenu.Click = () => _menuInvite.Click();
            SetupPauseList();
            SetupGoats();
            _voiceMenu.FallbackSelect = () => _menuInvite.VoiceButton;
            _menuInvite.OnVoice = () => _voiceMenu.Show(); // Multiplayer Settings is opened from the pause menu (ESC)
            _voiceMenu.LooksShown = on => { if (on) { _picker.Show(); _picker.Nav(0, 0, false, _cfgMyModel.Value); } else _picker.Hide(); };
            _voiceMenu.LooksNav = (dx, dy, submit) => { var k = _picker.Nav(dx, dy, submit, _cfgMyModel.Value); if (k != null) { SetMyModel(k); _menuInvite.Click(); } };
            _voiceMenu.Bindings = () => BindList;
            _voiceMenu.DrawLooks = area => { var pick = _picker.Draw(_cfgMyModel.Value, area); if (pick != null) { SetMyModel(pick); _menuInvite.Click(); } };
            SetupBinds();
            _cfgTrace = Config.Bind("Debug", "Trace", false, "Record a 20 Hz position trace to BepInEx/friendsofyore-trace.csv while friends are present (~20 MB/hour).");
            var envT = Environment.GetEnvironmentVariable("FOY_TRANSPORT");
            if (!string.IsNullOrEmpty(envT)) _cfgTransport.Value = envT;
            AvatarModel.AxeSlide = Config.Bind("Avatar", "AxeGripSlide", 0.12f, "Ice axe in a friend's hand: how far (m) the hand sits up the shaft from its end. More = the axe moves further through the hand.").Value;
            AvatarModel.AxeSide = Config.Bind("Avatar", "AxeGripRight", 0.03f, "Ice axe in a friend's hand: shift to the right (m).").Value;
            _cfgPreload = Config.Bind("Avatar", "PreloadModels", true, "Load the NPC bodies in the background once you are in a cabin (Northern or Alpine) so every model is available.");
            SceneManager.sceneLoaded += (s, m) =>
            {
                if (m == LoadSceneMode.Additive && s.name == _preloading)
                {   // background preload: switch the scene off before any Start() runs; we only want its NPC bodies
                    foreach (var go in s.GetRootGameObjects()) go.SetActive(false);
                    return;
                }
                OnSceneLoaded(s);
            };
            // An old build (GUID "poycoop", PoyCoop.dll) left in plugins would run a second network stack.
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("poycoop"))
            {
                _disabledReason = "Old version found: delete the folder BepInEx\\plugins\\PoyCoop, then restart the game.";
                Log.LogError(_disabledReason); return;
            }
            GameAccess.Patch(new HarmonyLib.Harmony("friendsofyore"));
            GameAccess.OnBlockedAttach = who => { Banner($"{who} is on this rope"); Log.LogInfo($"blocked clipping into a rope used by {who}"); };
            Application.runInBackground = true;
            _ropes = new RopeSync(this);
            _menuInvite.OnClick = () => { if (_net is SteamTransport st) InviteViaSteam(st, own: false); else Banner("Steam is not ready yet"); };
            RemotePlayer.HeldByMe = (rp, left) =>
            {
                var c = _climbing; if (c == null) return false;
                bool L(Transform g) => g != null && g.parent == (left ? rp.HandL : rp.HandR);
                return (c.grabbingNPCHandL && L(GameAccess.LeftGrabPoint(c))) || (c.grabbingNPCHandR && L(GameAccess.RightGrabPoint(c)));
            };
            RemotePlayer.LocalHoldPoint = () =>
            {   // a hand on a real hold, not on a friend's hand
                var c = _climbing; if (c == null) return null;
                if (c.grabbingLeft && !c.grabbingNPCHandL) return c.leftGrabber.position;
                if (c.grabbingRight && !c.grabbingNPCHandR) return c.rightGrabber.position;
                return null;
            };
            FallSounds.Patch(new HarmonyLib.Harmony("friendsofyore.fall"));
            try { CoopRules.Patch(new HarmonyLib.Harmony("friendsofyore.rules")); } catch (Exception e) { Log.LogWarning("summit rules: " + e.Message); }
            CoopRules.OnSummit = free => BroadcastNote(free ? "summitfree" : "summit", MyPlaceTitle());
            RopeSync.Say = t => Banner(t, 4f);
            // our falls, splashes and the Workshop bell are heard by friends on the same place
            FallSounds.OnLocalFall = (hurt, thud) =>
            {
                if (LocalHardMode && IsPeak(_scene)) { BroadcastNote("death", ""); Log.LogInfo("fell in a hard save: friends are told"); }
                if (LocalBody != null) BroadcastEvent(WorldKind.Fall, "fall", LocalBody.position, hurt * 100 + thud);
            };
            FallSounds.OnLocalBell = at => BroadcastEvent(WorldKind.Fall, "bell", at);
            FallSounds.OnLocalSplash = () => BroadcastEvent(WorldKind.Fall, "splash", LocalBody != null ? LocalBody.position : Camera.main != null ? Camera.main.transform.position : Vector3.zero);
            _world = new WorldSync(this);
            WorldSync.Patch(new HarmonyLib.Harmony("friendsofyore.world"));
            Log.LogInfo($"Friends of Yore {Version} loaded (build {typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString().Substring(0, 8)})");
        }

        void OnSceneLoaded(Scene s)
        {
            _reported.Clear();
            _menuInvite.OnSceneLoaded(); _voiceMenu?.Hide(); _clock.OnSceneLoaded();
            _ringL = _ringR = null; _ringImgL = _ringImgR = null; _stamL = _stamR = 100f;
            if (_inviteOpen) ShowInvite(false);
            CoopRules.Assisted = false; CoopRules.AssistedHow = null;
            _goatSync.OnSceneLoaded();
            _scene = s.name; _climbing = null; _walker = null; _hubCamLogged = false; _hubScanAt = 0f; _sceneSerial++; Log.LogInfo($"scene loaded: {s.name}");
            _fontRetryIn = 2f; _captureFrames = 3; _ropes?.OnSceneLoaded(); _world?.OnSceneLoaded(s);
        }

        void StartTransport()
        {
            try
            {
                if (_cfgTransport.Value == "udp")
                    _net = new UdpTransport(_cfgUdpPort.Value, _cfgUdpPeers.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                else if (SteamManager.Initialized)
                    _net = new SteamTransport(s => Log.LogInfo("[steam] " + s));
                else return; // Steam not up yet, retry next frame
                Log.LogInfo($"transport {_net.Name} ready, local id {_net.LocalId}");
            }
            catch (Exception e) { Log.LogError("transport start failed: " + e); _cfgTransport.Value = "off"; }
        }

        // ---- per-frame work: each step is isolated, so one failing part never stops the others ----
        readonly HashSet<string> _reported = new HashSet<string>();

        /// <summary>Runs one step; its first failure in each place is logged (with the stack), later ones are not.</summary>
        void Safe(string what, Action step)
        {
            try { step(); }
            catch (Exception e) { if (_reported.Add(what)) Log.LogWarning($"{what} failed in {_scene}: {e}"); }
        }

        void Update()
        {
            if (_disabledReason != null) return;
            float dt = Time.unscaledDeltaTime;
            PadInput.Tick();
            if (_net == null && _cfgTransport.Value != "off") StartTransport();
            if (!_preloadBusy && _cfgPreload.Value && _preloadTriedSerial != _sceneSerial && IsHub(_scene) && PreloadSources.Any(x => !AvatarModel.Ready(x.model) && !_preloadedScenes.Contains(x.scene)))
            { _preloadTriedSerial = _sceneSerial; StartCoroutine(Preload()); }
            if (_captureFrames > 0 && --_captureFrames == 0) { AvatarModel.CaptureTemplates(); Ui.FindGameFont(); }
            if (_fontRetryIn > 0f && (_fontRetryIn -= dt) <= 0f) Ui.FindGameFont();
            Safe("finding the local player", FindLocalPlayer);
            Keys();
            Safe("sending our place", SendPlace);
            Safe("workshop download", () => { try { WorkshopDownloadTick(); } catch { _wsJoin = null; throw; } });
            Safe("join prompt", JoinPrompt);
            Safe("splash watch", () => FallSounds.Watch(_scene));
            Safe("pause menu button", _menuInvite.Update);
            foreach (var rp in Remotes.Values) Safe("showing " + rp.DisplayName, () => rp.Step(dt, _scene));
            Safe("game icons", () => GameIcons.TryCapture(_scene));
            Safe("goats", () => _goatSync.Tick(_net, _scene));
            Safe("gull", GullJoke);
            Safe("hands", () => { GuardHeldHands(); CatchSound(); HandStamina(); OneHandPerHold(); ApplyGripForces(); });
            GameAccess.CoopActive = _net != null && (_net.Peers.Count > 0 || _net is UdpTransport);
            GameAccess.MenuOpen = _inviteOpen;
            if (_inviteOpen) Ui.FreeCursor();
            if (_cfgTrace.Value || _net is UdpTransport) _trace.Tick(this, dt, null);
            if (_net == null) return;
            _net.Tick();
            Safe("voice", () => _voice.Update(_net, _scene != "TitleScreen"));
            foreach (var pk in _net.Receive()) Handle(pk);
            _ropes.Update(_net, dt);
            _world.Update(_net, dt);
            SendState(dt);
            foreach (var gone in Remotes.Keys.Where(k => !_net.Peers.Contains(k)).ToList()) RemoveRemote(gone);
            if (_net is SteamTransport st && _openInviteWhenLobby && st.InLobby) { _openInviteWhenLobby = false; if (_inviteViaOwnList) ShowInvite(true); else st.InviteDialog(); }
        }

        /// <summary>Our state to every friend, SendRate times a second, and a ping every 20th packet.</summary>
        void SendState(float dt)
        {
            _sendTimer += dt;
            if (_sendTimer < 1f / Mathf.Max(1, _cfgSendRate.Value) || LocalBodyTransform == null || _net.Peers.Count == 0) return;
            _sendTimer = 0f;
            var mine = CaptureAny();
            if (mine != null) _net.Broadcast(mine.Write(), false);
            if (++_pingCounter % 20 == 0) _net.Broadcast(new byte[] { Protocol.Version, Protocol.MsgPing }.Concat(BitConverter.GetBytes(Time.unscaledTime)).ToArray(), false);
        }

        void FixedUpdate()
        {
            if (_disabledReason != null || _net == null) return;
            _ropes.UpdateOccupancy(_net.LocalId, Remotes.Values, _activity.Rope);
            _world.FixedTick();
        }

        void LateUpdate()
        {
            Safe("clock", _clock.LateTick);
            if (_inviteOpen || (_preloadBusy && _menuInvite.PauseOpen)) Ui.FreeCursor(); // a background load may lock it under the pause menu
            if (_picker.Open) _picker.LateTick(Time.unscaledDeltaTime);
            if (_disabledReason != null) return;
            if (_net != null) _world.LateTick();
            Safe("mill clock", () => _world?.ClockTick());
        }

        void OnDestroy() { _voice.Stop(); _net?.Dispose(); _trace?.Close(); }
    }
}
