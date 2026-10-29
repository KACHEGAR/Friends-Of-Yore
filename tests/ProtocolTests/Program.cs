using System; using System.IO; using System.Collections.Generic; using System.Linq; using FriendsOfYore.Net;
int fails = 0, passes = 0;
void Check(bool ok, string name) { if (ok) passes++; else { fails++; Console.WriteLine("FAIL " + name); } }
PlayerState At(uint seq, float t) => new PlayerState { Seq = seq, Time = t, Scene = "Peak_1", Model = "Walker", Act = Activity.Coffee, RopeOwner = 76561198000000001UL, RopeId = 4, Body = new V3(t * 2f, 100 + t, -t), HandL = new V3(t, 1, 2), HandR = new V3(-t, 3, 4), Yaw = (t * 90f) % 360f, Pitch = 10, Flags = StateFlags.GrabL | StateFlags.OnFriendR | StateFlags.AxeR, AxeRotR = new V3(10, 20, 30) };
PlayerState RoundTrip(PlayerState s) { var b = s.Write(); Check(b[0] == Protocol.Version && b[1] == Protocol.MsgState, "header"); using var r = new BinaryReader(new MemoryStream(b, 2, b.Length - 2)); return PlayerState.Read(r); }

// 1. Serialization round trip
var a = At(7, 1.25f); var a2 = RoundTrip(a);
Check(a2.Seq == 7 && a2.Scene == "Peak_1" && a2.Body.x == a.Body.x && a2.HandR.z == 4 && a2.Flags == a.Flags && a2.Yaw == a.Yaw && a2.Model == "Walker" && a2.Act == Activity.Coffee && a2.RopeOwner == 76561198000000001UL && a2.RopeId == 4, "roundtrip");
Check(a.Write().Length < 160, $"packet size {a.Write().Length} < 160 bytes"); // 129 with the idols (0.27)
// 2. Angle wrap: 350 -> 10 must go through 0, not 180
Check(Math.Abs(PlayerState.LerpAngle(350, 10, 0.5f) % 360 - 0) < 0.01f || Math.Abs(PlayerState.LerpAngle(350, 10, 0.5f) - 360) < 0.01f, "angle wrap");
// 3. Duplicate / old packets dropped
var buf = new SnapshotBuffer(); buf.Push(At(1, 0)); buf.Push(At(2, 0.05f)); buf.Push(At(2, 0.05f)); buf.Push(At(1, 0));
Check(buf.Dropped == 2 && buf.Latest.Seq == 2, "drop duplicates/old");
// 4. Interpolation midpoint
var mid = buf.Sample(0.025f); Check(Math.Abs(mid.Body.x - 0.05f) < 1e-4, "interp midpoint");
// 5. Fuzz: 20 Hz sender, random latency 30-250 ms, 15% loss, reordering; renderer 100 ms behind.
//    Truth is the analytic path; check error of shown body position stays small.
var rng = new Random(1234); float maxErr = 0, sumErr = 0; int samples = 0;
for (int run = 0; run < 50; run++)
{
    var b = new SnapshotBuffer { Delay = 0.15f };
    var inflight = new List<(float arrive, PlayerState s)>();
    uint seq = 0; float render = -1;
    for (float now = 0; now < 20f; now += 1f / 60f)
    {
        if ((int)(now * 60) % 3 == 0) { var s = At(++seq, now); if (rng.NextDouble() > 0.15) inflight.Add((now + 0.03f + (float)rng.NextDouble() * 0.22f, s)); }
        foreach (var x in inflight.Where(x => x.arrive <= now).OrderBy(_ => rng.Next()).ToList()) { b.Push(x.s); inflight.Remove(x); }
        if (b.Latest == null) continue;
        if (render < 0) render = b.Latest.Time - b.Delay;
        render += 1f / 60f; float err0 = b.Latest.Time - b.Delay - render; render += Math.Abs(err0) > 1 ? err0 : err0 * 0.1f;
        var shown = b.Sample(render); if (now < 2) continue;
        var truth = At(0, shown.Time);
        float e = MathF.Sqrt(MathF.Pow(shown.Body.x - truth.Body.x, 2) + MathF.Pow(shown.Body.y - truth.Body.y, 2) + MathF.Pow(shown.Body.z - truth.Body.z, 2));
        maxErr = Math.Max(maxErr, e); sumErr += e; samples++;
        float lag = now - shown.Time; Check(lag < 0.6f, "lag bounded");
    }
}
Console.WriteLine($"fuzz: {samples} samples, mean pos error {sumErr / samples * 100:F2} cm, max {maxErr * 100:F2} cm");
Check(maxErr < 0.02f, "fuzz max error < 2 cm (linear motion)");
// 6. Rope list round trip
var rl = new RopeList { Scene = "Peak_1_GreenhornNEW" };
rl.Ropes.Add(new RopeInfo { Id = 3, Point = new V3(1, 2, 3), Forward = new V3(0, 0, 1) });
rl.Ropes.Add(new RopeInfo { Id = 9, Point = new V3(-4, 5.5f, 6), Forward = new V3(1, 0, 0), SegLength = 1.25f });
var rb = rl.Write(); Check(rb[1] == Protocol.MsgRopes, "rope header");
using (var rr = new BinaryReader(new MemoryStream(rb, 2, rb.Length - 2)))
{ var back = RopeList.Read(rr); Check(back.Scene == rl.Scene && back.Ropes.Count == 2 && back.Ropes[1].Id == 9 && back.Ropes[1].Point.y == 5.5f && back.Ropes[0].Forward.z == 1 && back.Ropes[1].SegLength == 1.25f, "rope roundtrip"); }
var empty = new RopeList { Scene = "x" }.Write(); Check(empty.Length < 16, "empty rope list small");
// 7. World event round trip
var we = new WorldEvent { Scene = "Peak_3", Kind = WorldKind.Snow, Key = "0/4/2", Index = 650, Pos = new V3(1, 2, 3) };
var wb = we.Write(); Check(wb[1] == Protocol.MsgWorld, "world header");
using (var wr = new BinaryReader(new MemoryStream(wb, 2, wb.Length - 2))) { var w2 = WorldEvent.Read(wr); Check(w2.Kind == WorldKind.Snow && w2.Key == "0/4/2" && w2.Index == 650 && w2.Pos.z == 3 && w2.Scene == "Peak_3", "world roundtrip"); }
Check(RoundTrip(At(1, 0)).Flags == (StateFlags.GrabL | StateFlags.OnFriendR | StateFlags.AxeR), "16-bit flags");
Check(RoundTrip(At(1, 0)).AxeRotR.y == 20, "axe rotation");
var pp = new PropPose { Scene = "s", Key = "pr:1/2", Kind = 1, Pos = new V3(1, 2, 3), Qx = 0, Qy = 0.7f, Qz = 0, Qw = 0.7f };
var pb = pp.Write();
using (var pr = new BinaryReader(new MemoryStream(pb, 2, pb.Length - 2))) { var p2 = PropPose.Read(pr); Check(p2.Key == "pr:1/2" && p2.Kind == 1 && p2.Qy == 0.7f && p2.Pos.y == 2, "prop roundtrip"); }
Console.WriteLine($"{passes} passed, {fails} failed");
return fails == 0 ? 0 : 1;
