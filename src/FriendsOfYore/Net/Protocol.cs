using System;
using System.IO;

namespace FriendsOfYore.Net
{
    /// <summary>Wire format. Pure C#, no Unity, so it is unit-testable outside the game.</summary>
    public static class Protocol
    {
        public const byte Version = 16;
        public const byte MsgState = 2;     // PlayerState
        public const byte MsgPing = 4;      // float senderTime
        public const byte MsgPong = 5;      // float echoedTime
        public const byte MsgRopes = 6;     // RopeList: full list of the sender's ropes in one scene
        public const byte MsgWorld = 8;     // WorldEvent: obstacle changes, rocks, bivouacs, sounds, marks, places, notes
        public const byte MsgProp = 9;      // PropPose: owner streams a physics object / bird pose
        public const byte MsgVoice = 10;    // raw Steam-compressed voice bytes (proximity voice chat)
    }

    [Flags]
    public enum StateFlags : ushort
    {
        None = 0,
        GrabL = 1,          // left hand holding anything
        GrabR = 2,
        OnFriendL = 4,      // left hand holding a remote player's hand
        OnFriendR = 8,
        Grounded = 16,
        ReachL = 32,        // arm button held (hand reaching out)
        ReachR = 64,
        OnWall = 128,       // holding a real hold (anchor)
        AxeL = 256,         // ice axe out in the left hand (key 1)
        AxeR = 512,
        Lamp = 1024,        // carrying the oil lamp (Aldr Grotto)
        CramponRest = 2048, // standing on the wall on crampons (legs straight to the wall)
        Harness = 4096,     // wears the safety harness (a rope can be clipped to them)
        Yfyd = 8192,        // "You fall, you die" save
        FreeSolo = 16384,   // free solo save: no friends' ropes or hands
    }

    /// <summary>What the player is busy with, shown above their head.</summary>
    public enum Activity : byte { None, Coffee, Chalk, Monocular, Hammer, OnRope, Bivouac, Stamping, Watch, Menu }

    public struct V3
    {
        public float x, y, z;
        public V3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static V3 Lerp(V3 a, V3 b, float t) => new V3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        public override string ToString() => $"({x:F2},{y:F2},{z:F2})";
    }

    public class PlayerState
    {
        public uint Seq;
        public float Time;          // sender clock, seconds
        public long WallMs;         // sender wall clock (Unix ms), for latency traces
        public string Scene = "";
        public V3 Body;             // player rigidbody position
        public float Yaw, Pitch;    // camera, degrees
        public V3 HandL, HandR;     // grabber world positions
        public V3 Velocity;
        public StateFlags Flags;
        public string Model = "";   // how the sender wants to look (NPC body key)
        public Activity Act;
        public ulong RopeOwner;     // rope the sender hangs on: (owner id, rope id); 0 = none
        public uint RopeId;
        public V3 AxeRotL, AxeRotR; // ice axe world rotation (euler) while out
        public ushort Idols;        // Alps idols in use, GameIcons.IdolNames bit order (0.27)

        public byte[] Write()
        {
            using (var ms = new MemoryStream(96))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Protocol.Version); w.Write(Protocol.MsgState);
                w.Write(Seq); w.Write(Time); w.Write(WallMs); w.Write(Scene ?? "");
                W(w, Body); w.Write(Yaw); w.Write(Pitch); W(w, HandL); W(w, HandR); W(w, Velocity);
                w.Write((ushort)Flags);
                w.Write(Model ?? ""); w.Write((byte)Act); w.Write(RopeOwner); w.Write(RopeId);
                W(w, AxeRotL); W(w, AxeRotR);
                w.Write(Idols);
                return ms.ToArray();
            }
        }

        public static PlayerState Read(BinaryReader r)
        {
            var s = new PlayerState();
            s.Seq = r.ReadUInt32(); s.Time = r.ReadSingle(); s.WallMs = r.ReadInt64(); s.Scene = r.ReadString();
            s.Body = R(r); s.Yaw = r.ReadSingle(); s.Pitch = r.ReadSingle(); s.HandL = R(r); s.HandR = R(r); s.Velocity = R(r);
            s.Flags = (StateFlags)r.ReadUInt16();
            s.Model = r.ReadString(); s.Act = (Activity)r.ReadByte(); s.RopeOwner = r.ReadUInt64(); s.RopeId = r.ReadUInt32();
            s.AxeRotL = R(r); s.AxeRotR = R(r);
            s.Idols = r.ReadUInt16();
            return s;
        }

        static void W(BinaryWriter w, V3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static V3 R(BinaryReader r) => new V3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static PlayerState Interpolate(PlayerState a, PlayerState b, float t)
        {
            var s = new PlayerState
            {
                Seq = b.Seq, Time = a.Time + (b.Time - a.Time) * t, WallMs = a.WallMs + (long)((b.WallMs - a.WallMs) * t), Scene = b.Scene,
                Body = V3.Lerp(a.Body, b.Body, t), HandL = V3.Lerp(a.HandL, b.HandL, t), HandR = V3.Lerp(a.HandR, b.HandR, t),
                Velocity = V3.Lerp(a.Velocity, b.Velocity, t),
                Yaw = LerpAngle(a.Yaw, b.Yaw, t), Pitch = a.Pitch + (b.Pitch - a.Pitch) * t, Flags = b.Flags,
                Model = b.Model, Act = b.Act, RopeOwner = b.RopeOwner, RopeId = b.RopeId, AxeRotL = b.AxeRotL, AxeRotR = b.AxeRotR, Idols = b.Idols,
            };
            return s;
        }

        public static float LerpAngle(float a, float b, float t)
        {
            float d = ((b - a) % 360f + 540f) % 360f - 180f;
            return a + d * t;
        }
    }

    /// <summary>Jitter buffer: keeps recent snapshots and samples them a fixed delay behind the newest.</summary>
    public class SnapshotBuffer
    {
        readonly PlayerState[] _buf = new PlayerState[32];
        int _count;
        uint _lastSeq;
        public float Delay = 0.1f;
        public int Dropped;          // stale or duplicate packets (checked by the protocol tests)
        public PlayerState Latest => _count > 0 ? _buf[_count - 1] : null;

        public void Push(PlayerState s)
        {
            if (_count > 0 && s.Seq <= _lastSeq) { Dropped++; return; }
            _lastSeq = s.Seq;
            if (_count == _buf.Length) { Array.Copy(_buf, 1, _buf, 0, _buf.Length - 1); _count--; }
            _buf[_count++] = s;
        }

        /// <param name="renderTime">sender clock time to show, usually Latest.Time - Delay (advanced locally)</param>
        public PlayerState Sample(float renderTime)
        {
            if (_count == 0) return null;
            if (renderTime <= _buf[0].Time) return _buf[0];
            for (int i = 0; i < _count - 1; i++)
            {
                var a = _buf[i]; var b = _buf[i + 1];
                if (renderTime >= a.Time && renderTime <= b.Time)
                {
                    float span = b.Time - a.Time;
                    return PlayerState.Interpolate(a, b, span > 1e-5f ? (renderTime - a.Time) / span : 1f);
                }
            }
            return _buf[_count - 1]; // no extrapolation: hold last
        }

        public void Clear() { _count = 0; _lastSeq = 0; }
    }

    public struct RopeInfo
    {
        public uint Id;        // unique per owner
        public V3 Point;       // piton position (rope_segment (piton) / last child)
        public V3 Forward;     // piton forward = placing camera forward
        public float SegLength; // average segment SpringJoint.minDistance: how far the rope is paid out
    }

    /// <summary>Periodic, idempotent rope state from one owner: receivers add missing ropes and drop ropes not listed.</summary>
    public class RopeList
    {
        public string Scene = "";
        public System.Collections.Generic.List<RopeInfo> Ropes = new System.Collections.Generic.List<RopeInfo>();

        public byte[] Write()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Protocol.Version); w.Write(Protocol.MsgRopes);
                w.Write(Scene ?? ""); w.Write((ushort)Ropes.Count);
                foreach (var r in Ropes)
                {
                    w.Write(r.Id);
                    w.Write(r.Point.x); w.Write(r.Point.y); w.Write(r.Point.z);
                    w.Write(r.Forward.x); w.Write(r.Forward.y); w.Write(r.Forward.z); w.Write(r.SegLength);
                }
                return ms.ToArray();
            }
        }

        public static RopeList Read(BinaryReader r)
        {
            var l = new RopeList { Scene = r.ReadString() };
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++)
                l.Ropes.Add(new RopeInfo { Id = r.ReadUInt32(), Point = new V3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), Forward = new V3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), SegLength = r.ReadSingle() });
            return l;
        }
    }

    public enum WorldKind : byte { CrumblingHold, BrickHold, CrumbleRegular, Shrub, Bird, IceBreak, IcePiece, Snow, Rock, Bivouac, RopePull, Fall, Mark, Place, Note }

    /// <summary>
    /// One world event. For obstacles Key is the object's "name@position" key (identical on every client); a Note carries
    /// "kind\u0001args" (death, summit, summitfree, invite, goatask, goatseed).
    /// </summary>
    public class WorldEvent
    {
        public string Scene = "", Key = "";
        public WorldKind Kind;
        public int Index;       // piece index / snow scale x1000
        public V3 Pos;

        public byte[] Write()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Protocol.Version); w.Write(Protocol.MsgWorld);
                w.Write(Scene ?? ""); w.Write((byte)Kind); w.Write(Key ?? ""); w.Write(Index);
                w.Write(Pos.x); w.Write(Pos.y); w.Write(Pos.z);
                return ms.ToArray();
            }
        }

        public static WorldEvent Read(BinaryReader r) => new WorldEvent
        {
            Scene = r.ReadString(), Kind = (WorldKind)r.ReadByte(), Key = r.ReadString(), Index = r.ReadInt32(),
            Pos = new V3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        };
    }

    /// <summary>Pose of one shared physics object (branch, hanging body, grabbable bird) or flying bird, streamed by its owner.</summary>
    public struct PropPose
    {
        public string Scene, Key;
        public V3 Pos;
        public float Qx, Qy, Qz, Qw;
        public byte Kind;   // 0 = rigidbody prop, 1 = bird

        public byte[] Write()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Protocol.Version); w.Write(Protocol.MsgProp);
                w.Write(Scene ?? ""); w.Write(Key ?? ""); w.Write(Kind);
                w.Write(Pos.x); w.Write(Pos.y); w.Write(Pos.z); w.Write(Qx); w.Write(Qy); w.Write(Qz); w.Write(Qw);
                return ms.ToArray();
            }
        }

        public static PropPose Read(BinaryReader r)
        {
            var p = new PropPose { Scene = r.ReadString(), Key = r.ReadString(), Kind = r.ReadByte() };
            p.Pos = new V3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            p.Qx = r.ReadSingle(); p.Qy = r.ReadSingle(); p.Qz = r.ReadSingle(); p.Qw = r.ReadSingle();
            return p;
        }
    }
}
