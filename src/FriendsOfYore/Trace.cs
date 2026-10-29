using System;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// CSV flight recorder at 20 Hz while other players are present: own true pose and what we show for each remote.
    /// Two traces from the same PC share a wall clock, so they can be joined offline to measure real latency and
    /// position error. File: BepInEx/friendsofyore-trace.csv (overwritten each session).
    /// </summary>
    public class Trace
    {
        StreamWriter _w;
        float _t;
        int _errors;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static long WallMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public void Tick(Plugin p, float dt, string mark)
        {
            if (p.LocalClimbing == null || p.Remotes.Count == 0) { if (mark == null) return; }
            _t += dt;
            if (_t < 0.05f && mark == null) return;
            _t = 0f;
            try
            {
                if (_w == null)
                {
                    var path = Path.Combine(Paths.BepInExRootPath, "friendsofyore-trace.csv");
                    _w = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.ASCII) { AutoFlush = true };
                    _w.WriteLine("wall_ms,kind,id,scene,bx,by,bz,lx,ly,lz,rx,ry,rz,flags,seq,age_ms,mark");
                }
                var c = p.LocalClimbing;
                if (c != null && c.playerBody != null && c.leftGrabber != null && c.rightGrabber != null)
                    Row(WallMs, "self", 0, p.SceneName, c.playerBody.position, c.leftGrabber.position, c.rightGrabber.position, 0, 0, 0, mark);
                foreach (var rp in p.Remotes.Values)
                {
                    var s = rp.Shown; var latest = rp.Buffer.Latest;
                    if (s == null || latest == null) continue;
                    Row(s.WallMs, "shown", rp.Id, s.Scene, RemotePlayer.ToV(s.Body), RemotePlayer.ToV(s.HandL), RemotePlayer.ToV(s.HandR), (int)s.Flags, latest.Seq, WallMs - latest.WallMs, mark, WallMs);
                }
            }
            // Keep the writer: re-opening while the old handle is still open caused endless "Sharing violation" (0.5.0).
            catch (Exception e) { if (++_errors <= 3) Plugin.Log.LogWarning("trace: " + e.Message); }
        }

        void Row(long wall, string kind, ulong id, string scene, Vector3 b, Vector3 l, Vector3 r, int flags, uint seq, long age, string mark, long localWall = 0)
        {
            // for "shown" rows wall_ms is the SENDER time the shown pose corresponds to; local time goes in the mark column
            string m = mark ?? (localWall != 0 ? "local=" + localWall : "");
            _w.WriteLine(string.Join(",", wall.ToString(Inv), kind, id.ToString(Inv), scene,
                F(b.x), F(b.y), F(b.z), F(l.x), F(l.y), F(l.z), F(r.x), F(r.y), F(r.z), flags.ToString(Inv), seq.ToString(Inv), age.ToString(Inv), m));
        }

        static string F(float v) => v.ToString("F3", Inv);
        public void Close() { _w?.Dispose(); _w = null; }
    }
}
