using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Steamworks;

namespace FriendsOfYore.Net
{
    /// <summary>
    /// Friends-only Steam lobby + ISteamNetworkingMessages (Valve relay, no port forwarding).
    /// Uses the game's own Steamworks.NET 20.1.0 and its own AppID (2236070): every player owns the game.
    /// </summary>
    public class SteamTransport : ITransport
    {
        const int Channel = 0;
        const int MaxPlayers = 4;
        public string Name => "steam";
        public ulong LocalId { get; private set; }
        public CSteamID Lobby { get; private set; } = CSteamID.Nil;
        public bool InLobby => Lobby != CSteamID.Nil;
        public string Status { get; private set; } = "not in a lobby";
        readonly HashSet<ulong> _peers = new HashSet<ulong>();
        public IReadOnlyCollection<ulong> Peers => _peers;
        readonly IntPtr[] _msgPtrs = new IntPtr[64];
        readonly List<Packet> _inbox = new List<Packet>();
        readonly Action<string> _log;
        string _lastNames = "";

        Callback<GameLobbyJoinRequested_t> _cbJoinReq;
        Callback<LobbyEnter_t> _cbEnter;
        Callback<LobbyChatUpdate_t> _cbChat;
        Callback<SteamNetworkingMessagesSessionRequest_t> _cbSession;
        Callback<SteamNetworkingMessagesSessionFailed_t> _cbFailed;
        CallResult<LobbyCreated_t> _crCreated;

        public SteamTransport(Action<string> log)
        {
            _log = log;
            LocalId = SteamUser.GetSteamID().m_SteamID;
            _cbJoinReq = Callback<GameLobbyJoinRequested_t>.Create(r => { _log($"invite accepted, joining lobby {r.m_steamIDLobby}"); Join(r.m_steamIDLobby.m_SteamID); });
            _cbEnter = Callback<LobbyEnter_t>.Create(OnEnter);
            _cbChat = Callback<LobbyChatUpdate_t>.Create(_ => RefreshMembers());
            _cbSession = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _cbFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(r => _log($"session failed: {r.m_info.m_eEndReason} {r.m_info.m_szEndDebug}"));
            _crCreated = CallResult<LobbyCreated_t>.Create(OnCreated);
            SteamNetworkingUtils.InitRelayNetworkAccess();
            // Launched by "Join game" from Steam while the game was closed: +connect_lobby <id>
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out var id)) Join(id);
        }

        public void Host()
        {
            if (InLobby) { InviteDialog(); return; }
            Status = "creating lobby...";
            _crCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MaxPlayers));
        }

        public void Join(ulong lobbyId)
        {
            Leave();
            Status = "joining...";
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
        }

        public void InviteDialog() { if (InLobby) SteamFriends.ActivateGameOverlayInviteDialog(Lobby); }

        public struct FriendInfo { public ulong Id; public string Name; public bool InThisGame; public bool Online; public bool InLobby; }

        /// <summary>Steam friends for our own invite list (the Steam overlay doesn't open in copies launched outside Steam).</summary>
        public System.Collections.Generic.List<FriendInfo> Friends()
        {
            var list = new System.Collections.Generic.List<FriendInfo>();
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            var myApp = SteamUtils.GetAppID();
            for (int i = 0; i < n; i++)
            {
                var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                var state = SteamFriends.GetFriendPersonaState(id);
                bool playing = SteamFriends.GetFriendGamePlayed(id, out var gi) && gi.m_gameID.AppID() == myApp;
                list.Add(new FriendInfo { Id = id.m_SteamID, Name = SteamFriends.GetFriendPersonaName(id), InThisGame = playing,
                    Online = state != EPersonaState.k_EPersonaStateOffline, InLobby = _peers.Contains(id.m_SteamID) });
            }
            return list.OrderByDescending(f => f.InThisGame).ThenByDescending(f => f.Online).ThenBy(f => f.Name).ToList();
        }

        public bool Invite(ulong friend)
        {
            if (!InLobby) return false;
            bool ok = SteamMatchmaking.InviteUserToLobby(Lobby, new CSteamID(friend));
            _log($"invite sent to {SteamFriends.GetFriendPersonaName(new CSteamID(friend))}: {ok}");
            return ok;
        }

        public void Leave()
        {
            if (!InLobby) return;
            foreach (var p in _peers) { var idn = Ident(p); SteamNetworkingMessages.CloseSessionWithUser(ref idn); }
            SteamMatchmaking.LeaveLobby(Lobby);
            Lobby = CSteamID.Nil; _peers.Clear(); Status = "not in a lobby";
        }

        void OnCreated(LobbyCreated_t r, bool ioFailure)
        {
            if (ioFailure || r.m_eResult != EResult.k_EResultOK) { Status = $"lobby failed: {r.m_eResult}"; return; }
            var id = new CSteamID(r.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(id, "friendsofyore", Protocol.Version.ToString());
            _log($"lobby created {id}; press the host key again to invite friends");
        }

        void OnEnter(LobbyEnter_t r)
        {
            if (r.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            { Status = $"join failed ({r.m_EChatRoomEnterResponse})"; return; }
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            RefreshMembers();
            _log($"entered lobby {Lobby}, {_peers.Count} other player(s)");
        }

        void RefreshMembers()
        {
            if (!InLobby) return;
            _peers.Clear();
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
            {
                var m = SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i).m_SteamID;
                if (m != LocalId) _peers.Add(m);
            }
            Status = $"in lobby, {_peers.Count + 1}/{MaxPlayers} players";
            var names = string.Join(", ", System.Linq.Enumerable.Select(_peers, p => SteamFriends.GetFriendPersonaName(new CSteamID(p))));
            if (names != _lastNames) { _lastNames = names; _log($"lobby members besides you: {(names == "" ? "none" : names)}"); }
        }

        void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t r)
        {
            var who = r.m_identityRemote.GetSteamID().m_SteamID;
            if (!_peers.Contains(who)) RefreshMembers(); // the first packet can beat LobbyChatUpdate_t
            if (_peers.Contains(who)) { var idn = r.m_identityRemote; SteamNetworkingMessages.AcceptSessionWithUser(ref idn); }
            else _log($"ignored session request from non-member {who}");
        }

        static SteamNetworkingIdentity Ident(ulong id) { var i = new SteamNetworkingIdentity(); i.SetSteamID64(id); return i; }

        public void Tick()
        {
            // SteamAPI.RunCallbacks() is already called by the game's SteamManager.Update.
            int n;
            while ((n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _msgPtrs, _msgPtrs.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    var m = SteamNetworkingMessage_t.FromIntPtr(_msgPtrs[i]);
                    var from = m.m_identityPeer.GetSteamID().m_SteamID;
                    var data = new byte[m.m_cbSize];
                    Marshal.Copy(m.m_pData, data, 0, m.m_cbSize);
                    SteamNetworkingMessage_t.Release(_msgPtrs[i]);
                    if (!_peers.Contains(from)) RefreshMembers();
                    if (_peers.Contains(from)) _inbox.Add(new Packet { From = from, Data = data });
                }
                if (n < _msgPtrs.Length) break;
            }
        }

        public void Send(ulong peer, byte[] data, bool reliable)
        {
            var idn = Ident(peer);
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                int flags = (reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_Unreliable)
                            | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
                SteamNetworkingMessages.SendMessageToUser(ref idn, h.AddrOfPinnedObject(), (uint)data.Length, flags, Channel);
            }
            finally { h.Free(); }
        }

        public void Broadcast(byte[] data, bool reliable) { foreach (var p in _peers) Send(p, data, reliable); }

        public List<Packet> Receive() { var l = new List<Packet>(_inbox); _inbox.Clear(); return l; }

        public void Dispose()
        {
            Leave();
            _cbJoinReq?.Dispose(); _cbEnter?.Dispose(); _cbChat?.Dispose(); _cbSession?.Dispose(); _cbFailed?.Dispose(); _crCreated?.Dispose();
        }
    }
}
