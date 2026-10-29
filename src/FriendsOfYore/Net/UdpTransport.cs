using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace FriendsOfYore.Net
{
    /// <summary>
    /// Plain UDP between instances on one PC or a LAN. Used by automated tests (two game copies, no Steam
    /// account juggling). Peer id = port number. Unreliable only; reliable sends are sent 3 times.
    /// </summary>
    public class UdpTransport : ITransport
    {
        readonly UdpClient _udp;
        readonly Dictionary<ulong, IPEndPoint> _peerEp = new Dictionary<ulong, IPEndPoint>();
        readonly List<Packet> _inbox = new List<Packet>();
        public string Name => "udp";
        public ulong LocalId { get; }
        public IReadOnlyCollection<ulong> Peers => _peerEp.Keys;
        public string Status => $"udp :{LocalId} peers={_peerEp.Count}";

        public UdpTransport(int listenPort, IEnumerable<string> peers)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, listenPort));
            _udp.Client.Blocking = false;
            LocalId = (ulong)listenPort;
            foreach (var p in peers)
            {
                var parts = p.Split(':');
                var ep = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
                _peerEp[(ulong)ep.Port] = ep;
            }
        }

        public void Tick()
        {
            while (_udp.Available > 0)
            {
                var ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] d;
                try { d = _udp.Receive(ref ep); } catch (SocketException) { break; }
                var id = (ulong)ep.Port;
                if (!_peerEp.ContainsKey(id)) _peerEp[id] = ep; // accept new peers (host side)
                _inbox.Add(new Packet { From = id, Data = d });
            }
        }

        public void Send(ulong peer, byte[] data, bool reliable)
        {
            if (!_peerEp.TryGetValue(peer, out var ep)) return;
            int n = reliable ? 3 : 1;
            for (int i = 0; i < n; i++) { try { _udp.Send(data, data.Length, ep); } catch (SocketException) { } }
        }

        public void Broadcast(byte[] data, bool reliable) { foreach (var p in _peerEp.Keys) Send(p, data, reliable); }
        public List<Packet> Receive() { var l = new List<Packet>(_inbox); _inbox.Clear(); return l; }
        public void Dispose() => _udp.Dispose();
    }
}
