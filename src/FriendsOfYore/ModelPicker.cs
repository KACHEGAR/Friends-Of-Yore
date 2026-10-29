using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// The Look tab of Multiplayer Settings: pick how you look to friends from a grid of live previews. Each model is
    /// spawned far below the world on its own layer, lit by its own light and filmed by its own camera into a small
    /// RenderTexture, only while the tab is open.
    /// </summary>
    public class ModelPicker
    {
        const int Layer = 31;
        class Slot { public string Key; public AvatarModel Model; public Camera Cam; public RenderTexture Rt; public Vector3 Body; }
        readonly List<Slot> _slots = new List<Slot>();
        GameObject _rig;
        Vector2 _scroll;
        /// <summary>Gamepad focus in the grid (0.26); -1 = start at the look you wear.</summary>
        public int Focus = -1;
        int _cols = 4; bool _scrollToFocus;

        /// <summary>Moves the pad focus; returns the key to pick when submit.</summary>
        public string Nav(int dx, int dy, bool submit, string current)
        {
            int n = _slots.Count; if (n == 0) return null;
            if (Focus < 0 || Focus >= n) { Focus = Mathf.Max(0, _slots.FindIndex(x => x.Key == current)); _scrollToFocus = true; if (dx == 0 && dy == 0 && !submit) return null; }
            if (dx != 0) Focus = Mathf.Clamp(Focus + dx, 0, n - 1);
            if (dy != 0) { int f = Focus + dy * _cols; if (f >= 0 && f < n) Focus = f; else if (dy > 0 && Focus / _cols < (n - 1) / _cols) Focus = n - 1; }
            if (dx != 0 || dy != 0) _scrollToFocus = true;
            return submit ? _slots[Focus].Key : null;
        }
        static readonly Color Gold = new Color(0.95f, 0.78f, 0.32f, 1f);
        public bool Open => _rig != null;

        public void Show()
        {
            Hide();
            Ui.ShuffleParcelBoy();
            Focus = -1;
            _scroll = Vector2.zero;
            _rig = new GameObject("FoY_ModelPicker");
            Object.DontDestroyOnLoad(_rig);
            var lightGo = new GameObject("light"); lightGo.transform.SetParent(_rig.transform, false);
            lightGo.transform.rotation = Quaternion.Euler(35f, 160f, 0f);
            var li = lightGo.AddComponent<Light>(); li.type = LightType.Directional; li.intensity = 1.15f; li.cullingMask = 1 << Layer; li.shadows = LightShadows.None;
            int i = 0;
            foreach (var key in AvatarModel.Available.ToList())
            {
                var holder = new GameObject("slot_" + key); holder.transform.SetParent(_rig.transform, false);
                var m = AvatarModel.Spawn(holder.transform, key);
                if (m == null) { Object.Destroy(holder); continue; }
                var body = new Vector3(i * 20f, -5000f, 0f);
                foreach (var t in holder.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
                var camGo = new GameObject("cam"); camGo.transform.SetParent(holder.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.cullingMask = 1 << Layer; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.16f, 0.15f, 0.14f, 1f);
                cam.fieldOfView = 28f; cam.nearClipPlane = 0.1f; cam.farClipPlane = 20f; cam.depth = -50;
                var rt = new RenderTexture(256, 320, 16) { name = "FoY_Preview_" + key };
                cam.targetTexture = rt;
                // the model faces the camera (yaw 180), camera ~4 m in front at chest height
                cam.transform.position = body + new Vector3(0f, -0.05f, -4.2f);
                cam.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0.02f, 1f));
                _slots.Add(new Slot { Key = key, Model = m, Cam = cam, Rt = rt, Body = body });
                i++;
            }
        }

        public void Hide()
        {
            foreach (var s in _slots) { if (s.Cam != null) s.Cam.targetTexture = null; if (s.Rt != null) { s.Rt.Release(); Object.Destroy(s.Rt); } }
            _slots.Clear();
            if (_rig != null) Object.Destroy(_rig);
            _rig = null;
        }

        /// <summary>Animate the previews (idle clip, arms hanging).</summary>
        public void Tick(float dt)
        {
            foreach (var s in _slots)
            {
                if (s.Model == null) continue;
                s.Model.BodyRef = s.Body;
                s.Model.Animate(s.Body, 180f, Vector3.zero, true, false, false);
            }
        }

        public void LateTick(float dt)
        {
            foreach (var s in _slots)
                if (s.Model != null) s.Model.Pose(s.Body, s.Body, 0f, false, false, false, false, dt);
        }

        /// <summary>Draws the grid inside the given area; returns the picked model key or null.</summary>
        public string Draw(string current, Rect inside)
        {
            string picked = null;
            int n = _slots.Count;
            if (n == 0) { GUI.Label(new Rect(inside.center.x - 420f, inside.y + 20f, 840f, 52f), "Visit a cabin once so the NPC bodies can be loaded", Ui.Box); return null; }
            int cols = Mathf.Min(4, n), rows = (n + cols - 1) / cols; _cols = cols;
            float cw = 190f, ch = 270f, pad = 10f;
            float gridW = cols * (cw + pad) + pad, gridH = rows * (ch + pad) + pad;
            float w = gridW + 20f;
            var area = new Rect(inside.center.x - w / 2f, inside.y, w, Mathf.Min(gridH + 80f, inside.height));
            GUI.Label(new Rect(area.x, area.y + 6f, area.width, 30f), "How friends see you (click a look)", Ui.Box);
            var view = new Rect(area.x, area.y + 40f, area.width, area.height - 76f);
            if (_scrollToFocus && Focus >= 0 && Focus < n)
            {   // keep the pad's choice in sight
                _scrollToFocus = false;
                float top = pad + (Focus / cols) * (ch + pad);
                if (top < _scroll.y) _scroll.y = top - pad;
                else if (top + ch > _scroll.y + view.height) _scroll.y = top + ch + pad - view.height;
            }
            _scroll = GUI.BeginScrollView(view, _scroll, new Rect(0f, 0f, gridW, gridH), false, gridH > view.height);
            for (int i = 0; i < n; i++)
            {
                var s = _slots[i];
                var r = new Rect(pad + (i % cols) * (cw + pad), pad + (i / cols) * (ch + pad), cw, ch);
                bool sel = s.Key == current;
                // the look you wear: a gold frame, a warm backdrop behind the model and a gold name (0.24.3)
                if (s.Cam != null) s.Cam.backgroundColor = sel ? new Color(0.42f, 0.33f, 0.16f, 1f) : new Color(0.16f, 0.15f, 0.14f, 1f);
                if (sel)
                {
                    var c0 = GUI.color; GUI.color = Gold;
                    GUI.DrawTexture(new Rect(r.x - 5f, r.y - 5f, r.width + 10f, r.height + 10f), Texture2D.whiteTexture);
                    GUI.color = c0;
                }
                if (GUI.Button(r, GUIContent.none)) picked = s.Key;
                if (i == Focus && PadInput.Active && Event.current.type == EventType.Repaint) PadNav.Frame(new Rect(r.x - 4f, r.y - 4f, r.width + 8f, r.height + 8f));
                if (s.Rt != null) GUI.DrawTexture(new Rect(r.x + 6f, r.y + 6f, r.width - 12f, r.height - 40f), s.Rt, ScaleMode.ScaleToFit);
                var ls = Ui.Button;
                var tc = ls.normal.textColor;
                if (sel) ls.normal.textColor = Gold;
                GUI.Label(new Rect(r.x, r.yMax - 32f, r.width, 28f), $"{i + 1}. {Ui.ModelName(s.Key)}{(sel ? "  - selected" : "")}", ls);
                ls.normal.textColor = tc;
            }
            GUI.EndScrollView();
            return picked;
        }
    }
}
