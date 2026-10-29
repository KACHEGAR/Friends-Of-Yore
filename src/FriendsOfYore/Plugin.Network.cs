using System;
using System.IO;
using System.Linq;
using FriendsOfYore.Net;
using Steamworks;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>Network: incoming messages, friends joining and leaving, notes (summits, deaths, invites...) and goats.</summary>
    public partial class Plugin
    {
        float _versionWarnUntil;
        bool _clearing;
        readonly GoatSync _goatSync = new GoatSync();

        void Handle(Packet pk)
        {
            if (pk.Data.Length < 2) return;
            if (pk.Data[0] != Protocol.Version)
            {
                if (Time.unscaledTime > _versionWarnUntil) { Banner("A friend runs a different Friends of Yore version: update both", 5f); _versionWarnUntil = Time.unscaledTime + 30f; }
                return;
            }
            try
            {
                using (var r = new BinaryReader(new MemoryStream(pk.Data, 2, pk.Data.Length - 2)))
                {
                    switch (pk.Data[1])
                    {
                        case Protocol.MsgState: ReceiveState(pk.From, PlayerState.Read(r)); return;
                        case Protocol.MsgWorld: ReceiveWorld(pk.From, WorldEvent.Read(r)); return;
                        case Protocol.MsgRopes: _ropes.Receive(pk.From, r); return;
                        case Protocol.MsgProp: _world.ReceiveProp(pk.From, PropPose.Read(r)); return;
                        case Protocol.MsgVoice: { if (Remotes.TryGetValue(pk.From, out var vr)) _voice.Receive(vr, pk.Data, 2); return; }
                        case Protocol.MsgPing: { float t = r.ReadSingle(); _net.Send(pk.From, new byte[] { Protocol.Version, Protocol.MsgPong }.Concat(BitConverter.GetBytes(t)).ToArray(), false); return; }
                        case Protocol.MsgPong: { float rtt = (Time.unscaledTime - r.ReadSingle()) * 1000f; if (Remotes.TryGetValue(pk.From, out var pr)) pr.RttMs = pr.RttMs <= 0 ? rtt : pr.RttMs * 0.8f + rtt * 0.2f; return; }
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"bad packet from {pk.From}: {e.Message}"); }
        }

        /// <summary>A friend's state; the first one from someone new is their arrival in the lobby.</summary>
        void ReceiveState(ulong from, PlayerState s)
        {
            if (!Remotes.TryGetValue(from, out var rp))
            {
                string name = _net is SteamTransport ? SteamFriends.GetFriendPersonaName(new CSteamID(from)) : $"udp:{from}";
                rp = RemotePlayer.Create(from, name, _handLayer, Palette[Remotes.Count % Palette.Length]);
                Remotes[from] = rp;
                Log.LogInfo($"remote player joined: {name}");
                Note($"{name} joined the lobby");
                try { FallSounds.PlayDoor(); } catch (Exception e) { Log.LogWarning("door sound: " + e.Message); }
            }
            rp.Push(s);
        }

        void ReceiveWorld(ulong from, WorldEvent ev)
        {
            switch (ev.Kind)
            {
                case WorldKind.RopePull: _ropes.PulledByFriend(from, ev, _net.LocalId, NameOf(from)); return;
                case WorldKind.Note: ReceiveNote(from, ev); return;
                case WorldKind.Place: ReceivePlace(from, ev); return;
                case WorldKind.Mark: _marks.Set(from, NameOf(from), Remotes.TryGetValue(from, out var mr) ? mr.Color : Color.white, RemotePlayer.ToV(ev.Pos), ev.Scene); return;
                case WorldKind.Fall: if (ev.Scene == _scene) FallSounds.Play(ev, Remotes.TryGetValue(from, out var fr) ? fr : null); return;
                default: _world.Receive(from, ev); return;
            }
        }

        /// <summary>Something everyone on this place should see or hear (a fall, the bell, a mark...), reliably.</summary>
        void BroadcastEvent(WorldKind kind, string key, Vector3 at, int index = 0)
        {
            if (_net == null || _net.Peers.Count == 0) return;
            _net.Broadcast(new WorldEvent { Scene = _scene, Kind = kind, Key = key, Index = index, Pos = RemotePlayer.FromV(at) }.Write(), true);
            if (kind == WorldKind.Fall) Log.LogInfo($"sound '{key}' sent to friends");
        }

        string NameOf(ulong id) => Remotes.TryGetValue(id, out var rp) ? rp.DisplayName : "A friend";

        void RemoveRemote(ulong id)
        {
            if (!Remotes.TryGetValue(id, out var rp)) return;
            if (_climbing != null)
            {
                if (_climbing.grabbingNPCHandL && HeldRemote(GameAccess.LeftGrabPoint(_climbing)) == rp) _climbing.ReleaseLHand(false);
                if (_climbing.grabbingNPCHandR && HeldRemote(GameAccess.RightGrabPoint(_climbing)) == rp) _climbing.ReleaseRHand(false);
            }
            Destroy(rp.gameObject); Remotes.Remove(id);
            Log.LogInfo($"remote player left: {id}");
            if (!_clearing) Note($"{rp.DisplayName} left the lobby");
        }

        /// <summary>We left the lobby: everyone goes at once, without a "left" message each.</summary>
        void ClearRemotes() { _clearing = true; try { foreach (var id in Remotes.Keys.ToList()) RemoveRemote(id); } finally { _clearing = false; } }

        void LeaveLobby(SteamTransport st) { st.Leave(); ClearRemotes(); }

        // ---------------------------------------------------------------- notes: "kind\u0001args" in a WorldKind.Note event
        WorldEvent NoteEvent(string kind, string arg) => new WorldEvent { Scene = _scene, Kind = WorldKind.Note, Key = kind + PlaceSep + (arg ?? "") };

        void BroadcastNote(string kind, string arg)
        {
            if (_net == null || _net.Peers.Count == 0) return;
            _net.Broadcast(NoteEvent(kind, arg).Write(), true);
        }

        void SendNote(ulong peer, string kind, string arg) { if (_net != null) _net.Send(peer, NoteEvent(kind, arg).Write(), true); }

        void ReceiveNote(ulong from, WorldEvent ev)
        {
            var who = NameOf(from);
            var parts = (ev.Key ?? "").Split(new[] { PlaceSep }, StringSplitOptions.None);
            string kind = parts[0], arg = parts.Length > 1 ? parts[1] : "";
            switch (kind)
            {
                case "death": Note($"{who} has met an untimely death"); break;
                case "summit": Note($"{who} summited {arg}"); break;
                case "summitfree": Note($"{who} summited {arg} - free solo"); break;
                case "invite": ReceiveInvite(from, ev); break;
                case "goatask": _goatSync.OnAsk(from, ev.Scene); break;
                case "goatseed": _goatSync.OnSeed(ev.Scene, arg); break;
            }
        }

        void SetupGoats()
        {
            _goatSync.Broadcast = BroadcastNote;
            _goatSync.SendTo = SendNote;
        }

    }
}
