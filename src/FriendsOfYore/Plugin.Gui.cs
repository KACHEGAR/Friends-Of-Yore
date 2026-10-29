using System.Collections.Generic;
using System.Linq;
using FriendsOfYore.Net;
using Steamworks;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>On-screen UI: status text, messages, the pause player list, the invite list, looks and the download bar.</summary>
    public partial class Plugin
    {
        readonly ModelPicker _picker = new ModelPicker();
        PauseList _pauseList;

        void OnGUI()
        {
            if (_disabledReason != null) { Ui.Banner(40, "Friends of Yore: " + _disabledReason); return; }
            if (Time.unscaledTime < _bannerUntil) Ui.Banner(Screen.height - 150, _banner);
            if (_voiceMenu.Open && !_menuInvite.PauseOpen) _voiceMenu.Hide(); // the pause menu closed (ESC): the page goes too
            if (_voiceMenu.Open) { _voiceMenu.OnGUI(); return; }
            _menuInvite.OnGUI();
            if (_menuInvite.PauseOpen && _pauseList != null && _scene != "TitleScreen") _pauseList.OnGUI(); // pause menu only, not the main menu
            else _pauseList?.Unfocus();
            if (!_menuInvite.PauseOpen && !_inviteOpen) _marks.OnGUI(_scene); // marks stay out of menus
            WorkshopDownloadGUI();
            FeedGUI();
            if (_joinScene != null) Ui.Banner(Screen.height / 2f - 160f, JoinQuestion);
            if (_inviteOpen) InvitePanel();
            if (_showOverlay) StatusGUI();
        }

        /// <summary>The status text in the top-right corner (F7 hides it).</summary>
        void StatusGUI()
        {
            var lines = new List<string>();
            if (_net == null) lines.Add(_cfgTransport.Value == "off" ? "network off (see log)" : "waiting for Steam...");
            else
            {
                if (!(_net is SteamTransport)) lines.Add(_net.Status); // the Steam lobby has no line: the pause list shows the players
                foreach (var rp in Remotes.Values)
                {
                    string where = rp.Shown == null ? "?" : rp.Shown.Scene == _scene ? $"{Vector3.Distance(RemotePlayer.ToV(rp.Shown.Body), LocalBody != null ? LocalBody.position : Vector3.zero):F0} m"
                        : rp.Shown.Scene == "(loading)" ? "loading..." : PlaceTitleOf(rp);
                    string ping = rp.RttMs > 0 ? $"{rp.RttMs:F0} ms" : "?";
                    lines.Add($"{rp.DisplayName}{(rp.Talking ? " (talking)" : "")}: {where} · ping {ping}{(rp.ModelMissing ? " · (model not loaded here)" : "")}");
                }
            }
            lines.Add($"You: {Ui.ModelName(_cfgMyModel.Value)}"
                + (!_voice.Enabled ? " · voice off" : _voice.Talking ? " · speaking" : _voice.PushToTalk ? $" · hold {_voice.PushKey} to talk" : " · mic on"));
            lines.Add($"{_bOverlay.Hint} hide");
            // "You fall, you die", free solo and the idols: the game puts its icons in the top-right corner; our text goes below them
            float w = 620, x = Screen.width - w - 16, y = 8f + ((LocalHardMode || GameIcons.LocalIdols() != 0) && _scene != "TitleScreen" ? Screen.height * 0.13f : 0f);
            Ui.ShadowLabel(new Rect(x, y, w, 34), "Friends of Yore " + Version, Ui.Title);
            y += 34f;
            // one label per line with an explicit row height: the game font's line spacing is too tight for multi-line labels
            float row = Ui.Label.fontSize * 1.45f;
            foreach (var line in lines) { Ui.ShadowLabel(new Rect(x, y, w, row + 4), line, Ui.Label); y += row; }
        }

        // ---------------------------------------------------------------- messages, bottom left (lobby, peaks, summits, deaths, stolen holds)
        readonly List<(string text, float at)> _feed = new List<(string, float)>();
        GUIStyle _feedStyle;
        const float FeedSeconds = 5f;

        internal void Note(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _feed.Add((text, Time.unscaledTime));
            while (_feed.Count > 6) _feed.RemoveAt(0);
            Log.LogInfo("message: " + text);
        }

        void FeedGUI()
        {
            float now = Time.unscaledTime, y = Screen.height * 0.62f;
            _feed.RemoveAll(f => now - f.at > FeedSeconds);
            if (_feed.Count == 0) return;
            if (_feedStyle == null || _feedStyle.font != Ui.Label.font) _feedStyle = new GUIStyle(Ui.Label) { alignment = TextAnchor.UpperLeft, fontSize = Mathf.RoundToInt(Ui.Label.fontSize * 1.35f) };
            foreach (var (text, at) in _feed)
            {
                var c = GUI.color; GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(FeedSeconds - (now - at))); // fades out in the last second
                Ui.ShadowLabel(new Rect(24f, y, Screen.width * 0.5f, _feedStyle.fontSize * 1.8f), text, _feedStyle);
                GUI.color = c;
                y += _feedStyle.fontSize * 1.5f;
            }
        }

        void WorkshopDownloadGUI()
        {
            if (_wsJoin == null) return;
            float w = 560f, x = Screen.width / 2f - w / 2f, y = Screen.height / 2f - 120f;
            Ui.Banner(y - 60f, $"Downloading \"{_wsJoin[0]}\" from the Steam Workshop... {Mathf.RoundToInt(_wsProgress * 100f)}%   ({(PadInput.Active ? "B" : "N")} - stop waiting)");
            GUI.Box(new Rect(x, y, w, 22f), GUIContent.none, Ui.Box);
            var c = GUI.color; GUI.color = new Color(0.95f, 0.8f, 0.4f);
            GUI.DrawTexture(new Rect(x + 3f, y + 3f, (w - 6f) * _wsProgress, 16f), Texture2D.whiteTexture);
            GUI.color = c;
        }

        /// <summary>How we look to friends (picked in ESC - Multiplayer Settings - Look).</summary>
        void SetMyModel(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _cfgMyModel.Value = key;
            Banner($"Friends see you as: {Ui.ModelName(key)}");
            Log.LogInfo("my model -> " + key);
        }

        // ---------------------------------------------------------------- the player list beside the pause menu (0.25)
        void SetupPauseList()
        {
            _pauseList = new PauseList();
            _pauseList.Click = () => _menuInvite.Click();
            _pauseList.FallbackSelect = () => _menuInvite.VoiceButton;
            _pauseList.InLobby = () => _net is SteamTransport st && st.InLobby;
            _pauseList.LobbyFull = () => _net != null && _net.Peers.Count + 1 >= 4;
            _pauseList.Rows = PauseRows;
            _pauseList.SetGain = (id, g) => { _voice.PlayerGains[id] = g; _voiceMenu.Changed?.Invoke(); };
            _pauseList.Join = id => { if (Remotes.TryGetValue(id, out var rp)) JoinFriend(rp); };
            _pauseList.Invite = SendInvite;
            _pauseList.InviteFriends = () => { if (_net is SteamTransport st) InviteViaSteam(st, false); };
            _pauseList.Leave = () => { if (_net is SteamTransport st) { LeaveLobby(st); Note("You left the lobby"); } };
        }

        List<PauseList.Row> PauseRows()
        {
            var rows = new List<PauseList.Row>();
            ulong owner = 0;
            if (_net is SteamTransport st && st.InLobby) try { owner = SteamMatchmaking.GetLobbyOwner(st.Lobby).m_SteamID; } catch { }
            ulong me = _net != null ? _net.LocalId : 0;
            string myName = "You"; try { if (SteamManager.Initialized) myName = SteamFriends.GetPersonaName(); } catch { }
            rows.Add(new PauseList.Row
            {
                Id = me, Name = myName, Self = true, Owner = owner == me && owner != 0, Flags = ModeFlags(), Idols = GameIcons.LocalIdols(), Talking = _voice.Talking,
                Where = IsPeak(_scene) && LocalBody != null ? HeightAt(LocalBody.position) ?? MyPlaceTitle() : MyPlaceTitle(),
            });
            foreach (var rp in Remotes.Values)
            {
                var sc = rp.Shown?.Scene;
                bool elsewhere = !string.IsNullOrEmpty(sc) && sc != _scene && sc != "(loading)" && sc != "TitleScreen";
                rows.Add(new PauseList.Row
                {
                    Id = rp.Id, Name = rp.DisplayName, Owner = rp.Id == owner, Flags = rp.Shown != null ? rp.Shown.Flags : 0, Idols = rp.Shown != null ? rp.Shown.Idols : (ushort)0, Talking = rp.Talking,
                    Where = WhereOf(rp) + (rp.RttMs > 0 ? $" · {rp.RttMs:F0} ms" : ""), Gain = _voice.PlayerGain(rp.Id),
                    CanJoin = elsewhere && BuildIndexOf(sc) >= 0,
                    CanInvite = rp.Shown != null && rp.Shown.Scene != _scene && _scene != "TitleScreen",
                });
            }
            return rows;
        }

        // ---------------------------------------------------------------- inviting: the Steam dialog, or our own list (Shift+F5)
        bool _inviteOpen;
        bool _openInviteWhenLobby, _inviteViaOwnList;
        Vector2 _scroll;
        List<SteamTransport.FriendInfo> _friends = new List<SteamTransport.FriendInfo>();
        float _friendsRefresh;

        /// <summary>F5 and the pause list button: create the lobby if needed, then the Steam invite dialog (Shift+F5: own list).</summary>
        void InviteViaSteam(SteamTransport st, bool own)
        {
            if (!st.InLobby) { st.Host(); _openInviteWhenLobby = true; _inviteViaOwnList = own; }
            else if (own) ShowInvite(true);
            else st.InviteDialog();
        }

        void ShowInvite(bool on)
        {
            _inviteOpen = on; _scroll = Vector2.zero; _friendsRefresh = 0f;
            GameAccess.MenuOpen = on;
            if (!on) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        void InvitePanel()
        {
            var st = _net as SteamTransport;
            if (st == null) return;
            if (Time.unscaledTime > _friendsRefresh) { _friends = st.Friends(); _friendsRefresh = Time.unscaledTime + 2f; }
            GUILayout.BeginArea(new Rect(Screen.width / 2 - 300, Screen.height / 2 - 260, 600, 520), GUI.skin.box);
            GUILayout.Label("Invite a friend (click or press 1-9)", Ui.Box);
            if (!st.InLobby) GUILayout.Label("Creating lobby...", Ui.Button);
            _scroll = GUILayout.BeginScrollView(_scroll);
            int i = 0;
            foreach (var f in _friends.Where(f => f.Online))
            {
                string tag = f.InLobby ? " · in lobby" : f.InThisGame ? " · playing Peaks of Yore" : "";
                if (GUILayout.Button($"{(i < 9 ? (i + 1) + ". " : "")}{f.Name}{tag}", Ui.Button, GUILayout.Height(34)) && !f.InLobby) DoInvite(f);
                i++;
            }
            if (i == 0) GUILayout.Label("No friends online", Ui.Button);
            GUILayout.EndScrollView();
            GUILayout.Label($"{_keyHost.Value} - close", Ui.Button);
            GUILayout.EndArea();
        }

        /// <summary>Keys 1-9 in the invite list.</summary>
        void PickInvite(int i)
        {
            var f = _friends.Where(x => x.Online).ToList();
            if (i < f.Count && !f[i].InLobby) DoInvite(f[i]);
        }

        void DoInvite(SteamTransport.FriendInfo f)
        {
            bool ok = ((SteamTransport)_net).Invite(f.Id);
            Banner(ok ? $"Invite sent to {f.Name}" : "Invite failed");
        }
    }
}
