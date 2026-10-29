using System;
using System.Collections.Generic;

namespace FriendsOfYore.Net
{
    public struct Packet { public ulong From; public byte[] Data; }

    /// <summary>Peer-to-peer transport. Peers are identified by a 64-bit id (SteamID for Steam).</summary>
    public interface ITransport : IDisposable
    {
        string Name { get; }
        ulong LocalId { get; }
        IReadOnlyCollection<ulong> Peers { get; }
        string Status { get; }
        void Tick();                                   // pump callbacks, call every frame
        void Send(ulong peer, byte[] data, bool reliable);
        void Broadcast(byte[] data, bool reliable);
        List<Packet> Receive();
    }
}
