using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FriendsOfYore
{
    /// <summary>
    /// The wall clocks in the Cabin and the Alpine cabin show this computer's time (owner's request, 0.23.1).
    /// Their hands (clockarrow_hour / clockarrow_minute) are turned every frame about the clock face's normal: the thinnest
    /// side of the hand mesh is the axis, its long side (toward the tip) is where it points, 12 o'clock is straight up.
    /// </summary>
    public class ClockSync
    {
        class Hand { public Transform T; public bool Hour; public Vector3 PointLocal, AxisLocal, N; public bool HaveN; }
        readonly List<Hand> _hands = new List<Hand>();
        bool _searched; float _retryAt;

        public void OnSceneLoaded() { _hands.Clear(); _searched = false; _retryAt = Time.unscaledTime + 1f; }

        public void LateTick()
        {
            if (!_searched && Time.unscaledTime >= _retryAt) { _searched = true; Find(); }
            if (_hands.Count == 0) return;
            var now = DateTime.Now;
            float min = now.Minute + now.Second / 60f;
            float hour = now.Hour % 12 + min / 60f;
            foreach (var h in _hands)
            {
                if (h.T == null) continue;
                if (!h.HaveN)
                {   // the face looks away from the clock's case (the hands sit in front of it); no case found: toward the player
                    var n = h.T.TransformDirection(h.AxisLocal).normalized;
                    var body = ClockBody(h.T);
                    if (body != null) { if (Vector3.Dot(n, h.T.position - body.bounds.center) < 0f) n = -n; }
                    else { var cam = Camera.main; if (cam == null) continue; if (Vector3.Dot(n, cam.transform.position - h.T.position) < 0f) n = -n; }
                    h.N = n; h.HaveN = true;
                    Plugin.Log.LogInfo($"clock: {h.T.name} faces {n} ({(body != null ? "from its case " + body.name : "toward the player")})");
                }
                var up = Vector3.ProjectOnPlane(Vector3.up, h.N);
                if (up.sqrMagnitude < 1e-4f) continue; // a face lying flat: leave it
                up.Normalize();
                var right = Vector3.Cross(up, -h.N); // as seen by someone looking at the face
                float deg = h.Hour ? hour * 30f : min * 6f;
                var want = Mathf.Cos(deg * Mathf.Deg2Rad) * up + Mathf.Sin(deg * Mathf.Deg2Rad) * right;
                var p = Vector3.ProjectOnPlane(h.T.TransformDirection(h.PointLocal), h.N);
                if (p.sqrMagnitude < 1e-6f) continue;
                float turn = Vector3.SignedAngle(p, want, h.N);
                if (Mathf.Abs(turn) > 0.01f) h.T.rotation = Quaternion.AngleAxis(turn, h.N) * h.T.rotation;
            }
        }

        /// <summary>The clock itself ("Clock", "Clock.001"): the hands' parent or a neighbour, within a metre and a half.</summary>
        static Renderer ClockBody(Transform hand)
        {
            Renderer best = null; float bestD = 1.5f;
            for (var p = hand.parent; p != null && p.parent != p; p = p.parent)
            {
                foreach (var r in p.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.name.StartsWith("Clock") || r.transform == hand || r.transform.IsChildOf(hand)) continue;
                    float d = Vector3.Distance(r.bounds.center, hand.position);
                    if (d < bestD) { bestD = d; best = r; }
                }
                if (best != null || p.parent == null) break;
            }
            return best;
        }

        void Find()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded || sc.name == GameAccess.Preloading) continue;
                foreach (var r in sc.GetRootGameObjects())
                    foreach (var t in r.GetComponentsInChildren<Transform>(true))
                    {
                        var nm = t.name;
                        bool hour = nm.StartsWith("clockarrow_hour"), minute = nm.StartsWith("clockarrow_minute");
                        if (!hour && !minute) continue;
                        var h = Setup(t, hour);
                        if (h != null) _hands.Add(h);
                    }
            }
            if (_hands.Count > 0) Plugin.Log.LogInfo($"clock: {_hands.Count} hand(s) now show this computer's time");
        }

        // clockarrow_hour / clockarrow_minute as modelled (read from the Alpine cabin, where they are not baked)
        static readonly Vector3[] HourV = { new Vector3(0f, -0.000292272f, 0.000267889f), new Vector3(0f, 1.70539e-05f, 7.32898e-06f), new Vector3(0f, -7.32898e-06f, -1.70539e-05f), new Vector3(0f, -0.000267889f, 0.000292272f), new Vector3(0f, -0.000259253f, 0.000332071f), new Vector3(0f, -0.000332071f, 0.000259253f), new Vector3(0f, -0.000302972f, 0.000338398f), new Vector3(0f, -0.000338398f, 0.000302972f), new Vector3(0f, -0.00037513f, 0.00037513f) };
        static readonly Vector3[] MinuteV = { new Vector3(0f, -1.72413e-05f, 0.000718916f), new Vector3(0f, 1.72413e-05f, -6.87656e-06f), new Vector3(0f, -1.72413e-05f, -6.87656e-06f), new Vector3(0f, 1.72413e-05f, 0.000718916f), new Vector3(0f, 5.14899e-05f, 0.000740951f), new Vector3(0f, -5.14899e-05f, 0.000740951f), new Vector3(0f, 2.50498e-05f, 0.000776339f), new Vector3(0f, -2.50498e-05f, 0.000776339f), new Vector3(0f, 1.5797e-17f, 0.000853336f) };
        static readonly int[] HandT = { 0, 1, 2, 0, 3, 1, 0, 4, 3, 0, 5, 4, 6, 4, 5, 6, 5, 7, 7, 8, 6 };
        static Mesh _hourMesh, _minuteMesh;
        static Mesh HandMesh(bool hour)
        {
            ref Mesh m = ref (hour ? ref _hourMesh : ref _minuteMesh);
            if (m != null) return m;
            m = new Mesh { name = hour ? "FoY_clock_hour" : "FoY_clock_minute" };
            var v = hour ? HourV : MinuteV; int n = v.Length;
            var verts = new Vector3[n * 2]; v.CopyTo(verts, 0); v.CopyTo(verts, n); // own vertices per side: clean normals
            m.vertices = verts;
            var both = new int[HandT.Length * 2];
            for (int i = 0; i < HandT.Length; i += 3)
            {   // both sides, so it shows whichever way the face looks
                both[i] = HandT[i]; both[i + 1] = HandT[i + 1]; both[i + 2] = HandT[i + 2];
                both[HandT.Length + i] = HandT[i] + n; both[HandT.Length + i + 1] = HandT[i + 2] + n; both[HandT.Length + i + 2] = HandT[i + 1] + n;
            }
            m.triangles = both;
            m.RecalculateNormals(); m.RecalculateBounds();
            Object.DontDestroyOnLoad(m);
            return m;
        }

        static Hand Setup(Transform t, bool hour)
        {
            var mf = t.GetComponent<MeshFilter>(); if (mf == null) mf = t.GetComponentInChildren<MeshFilter>(true);
            if (mf == null || mf.sharedMesh == null) { Plugin.Log.LogInfo($"clock: {t.name} has no mesh, left alone"); return null; }
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr != null && (mr.isPartOfStaticBatch || mf.sharedMesh.name.StartsWith("Combined Mesh")))
            {   // the Cabin's hands are baked into one static mesh with the room: turning them moves nothing (0.23.1).
                // Hide the baked hand and turn a copy of it (the same hand mesh as the Alpine cabin's, with its material).
                var copy = new GameObject("FoY_ClockHand");
                copy.transform.SetParent(t, false);
                copy.layer = t.gameObject.layer;
                var cmf = copy.AddComponent<MeshFilter>(); cmf.sharedMesh = HandMesh(hour);
                var cmr = copy.AddComponent<MeshRenderer>(); cmr.sharedMaterials = mr.sharedMaterials;
                cmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.enabled = false;
                mf = cmf;
                Plugin.Log.LogInfo($"clock: {t.name} was baked into the room, replaced by a copy that can turn");
            }
            var b = mf.sharedMesh.bounds; var size = b.size;
            int thin = 0, lng = 0;
            for (int i = 1; i < 3; i++) { if (size[i] < size[thin]) thin = i; if (size[i] > size[lng]) lng = i; }
            if (thin == lng) return null;
            var axis = Vector3.zero; axis[thin] = 1f;
            // where the tip is: 0.23.1 took the longest side of the box, but the hands are modelled at an angle (showing
            // about ten past two), so the box's long side is not where they point. The tip is the point of the hand
            // farthest from its pivot: a vertex when the mesh can be read, else the box corner farthest from the pivot.
            var point = Vector3.zero; string how;
            var mesh = mf.sharedMesh;
            if (mesh.isReadable && mesh.vertexCount > 0)
            {
                float best = -1f;
                foreach (var v in mesh.vertices) { var q = v; q[thin] = 0f; if (q.sqrMagnitude > best) { best = q.sqrMagnitude; point = q; } }
                how = "farthest vertex";
            }
            else
            {
                for (int i = 0; i < 3; i++) if (i != thin) point[i] = Mathf.Abs(b.max[i]) >= Mathf.Abs(b.min[i]) ? b.max[i] : b.min[i];
                how = "box corner";
            }
            if (point.sqrMagnitude < 1e-8f) return null;
            point.Normalize();
            var h = new Hand
            {
                T = t, Hour = hour,
                AxisLocal = t.InverseTransformDirection(mf.transform.TransformDirection(axis)),
                PointLocal = t.InverseTransformDirection(mf.transform.TransformDirection(point)),
            };
            Plugin.Log.LogInfo($"clock: {t.name} in {t.gameObject.scene.name}: mesh {mf.sharedMesh.name} size {size} center {b.center}, axis {"xyz"[thin]}, tip toward {point} ({how})");
            return h;
        }
    }
}
