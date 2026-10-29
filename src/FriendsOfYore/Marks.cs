using System.Collections.Generic;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// Map marks: the Mark key puts a marker where you look (up to 300 m); everyone in the same place sees it with the
    /// player's name and distance, also at the screen edge when it is off screen. A mark fades out after Lifetime seconds;
    /// each player has one mark at a time.
    /// </summary>
    public class Marks
    {
        public const float Lifetime = 20f, FadeTime = 3f;
        class Mark { public Vector3 Pos; public string Who; public Color Color; public float Until; public string Scene; }
        readonly Dictionary<ulong, Mark> _marks = new Dictionary<ulong, Mark>();
        AudioSource _ping;
        GUIStyle _label;

        /// <summary>Where the local player looks: the first solid surface (players' hands and triggers ignored).</summary>
        public static bool Aim(out Vector3 point)
        {
            point = Vector3.zero;
            var cam = Camera.main; if (cam == null) return false;
            var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, 300f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.distance < 0.3f) continue; // our own body
                if (h.rigidbody != null && h.rigidbody.GetComponentInParent<Climbing>() != null) continue;
                if (h.collider.name.StartsWith("FoY")) continue;
                if (!Visible(h.collider, h.point)) continue; // invisible walls: the mark goes to what you actually see behind them
                if (h.distance < best) { best = h.distance; point = h.point; }
            }
            return best < float.MaxValue;
        }

        /// <summary>
        /// Something you can see is there: terrain, a mesh collider, or any enabled renderer of the collider's object
        /// family (itself, parent, grandparent and their children) covering the hit point. 0.19 only looked at the
        /// collider's own object and parent, so cabin walls (box colliders next to their meshes) counted as invisible.
        /// </summary>
        static bool Visible(Collider c, Vector3 point)
        {
            if (c is MeshCollider || c.GetType().Name == "TerrainCollider") return true;
            var t = c.transform;
            for (int up = 0; up < 3 && t != null; up++, t = t.parent)
            {
                var rs = t.GetComponentsInChildren<Renderer>(false);
                if (rs.Length > 200) break;
                foreach (var r in rs)
                {
                    if (r == null || !r.enabled || r.GetType().Name == "ParticleSystemRenderer") continue;
                    var b = r.bounds; b.Expand(0.6f);
                    if (b.Contains(point)) return true;
                }
            }
            return false;
        }

        public void Set(ulong who, string name, Color color, Vector3 pos, string scene)
        {
            _marks[who] = new Mark { Pos = pos, Who = name, Color = color, Until = Time.unscaledTime + Lifetime, Scene = scene };
            PlayPing(pos);
        }

        void PlayPing(Vector3 pos)
        {
            var c = Object.FindObjectOfType<Climbing>();
            if (c == null || c.grabSounds == null || c.grabSounds.Length == 0) return;
            if (_ping == null) { var go = new GameObject("FoY_MarkSound"); Object.DontDestroyOnLoad(go); _ping = go.AddComponent<AudioSource>(); _ping.playOnAwake = false; }
            if (c.handSourceL != null) _ping.outputAudioMixerGroup = c.handSourceL.outputAudioMixerGroup;
            _ping.spatialBlend = 0f; _ping.pitch = 1.6f;
            _ping.PlayOneShot(c.grabSounds[0], 0.6f);
        }

        public void Clear() => _marks.Clear();

        public void OnGUI(string scene)
        {
            var cam = Camera.main;
            if (cam == null || _marks.Count == 0) return;
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 13, richText = false, wordWrap = false, clipping = TextClipping.Overflow };
                if (Ui.GameFont != null) _label.font = Ui.GameFont;
            }
            float now = Time.unscaledTime;
            var dead = new List<ulong>();
            foreach (var kv in _marks)
            {
                var m = kv.Value;
                if (now > m.Until) { dead.Add(kv.Key); continue; }
                if (m.Scene != scene) continue;
                float alpha = Mathf.Clamp01((m.Until - now) / FadeTime);
                var sp = cam.WorldToScreenPoint(m.Pos);
                bool behind = sp.z < 0f;
                var p = new Vector2(sp.x, Screen.height - sp.y);
                if (behind) p = new Vector2(Screen.width - p.x, Screen.height - p.y);
                bool off = behind || p.x < 0 || p.x > Screen.width || p.y < 0 || p.y > Screen.height;
                if (off)
                {   // clamp to the screen edge so you can turn toward it
                    var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
                    var d = p - center; if (behind && d.sqrMagnitude < 1f) d = Vector2.down;
                    float k = Mathf.Min((Screen.width / 2f - 40f) / Mathf.Max(1f, Mathf.Abs(d.x)), (Screen.height / 2f - 40f) / Mathf.Max(1f, Mathf.Abs(d.y)));
                    p = center + d * k;
                }
                float dist = Vector3.Distance(cam.transform.position, m.Pos);
                float size = off ? 6f : Mathf.Lerp(8f, 4f, Mathf.Clamp01(dist / 120f)); // half-size in px (0.14 was 3x bigger)
                var col = m.Color; col.a = alpha;
                DrawDiamond(p, size, new Color(0f, 0f, 0f, 0.6f * alpha), 1.5f);
                DrawDiamond(p, size, col, 0f);
                string text = $"{m.Who}  {dist:F0} m";
                var r = new Rect(p.x - 150f, p.y + size + 1f, 300f, 20f);
                var old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.8f * alpha); GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, _label);
                GUI.color = new Color(1f, 0.97f, 0.9f, alpha); GUI.Label(r, text, _label);
                GUI.color = old;
            }
            foreach (var k in dead) _marks.Remove(k);
        }

        static void DrawDiamond(Vector2 c, float size, Color col, float grow)
        {
            // a rotated square made of horizontal strips (IMGUI has no polygons)
            var old = GUI.color; GUI.color = col;
            float s = size + grow;
            for (float y = -s; y <= s; y += 1f)
            {
                float half = s - Mathf.Abs(y);
                GUI.DrawTexture(new Rect(c.x - half, c.y + y - 0.5f, half * 2f, 1f), Texture2D.whiteTexture);
            }
            // stem down to the exact spot
            GUI.color = old;
        }
    }
}
