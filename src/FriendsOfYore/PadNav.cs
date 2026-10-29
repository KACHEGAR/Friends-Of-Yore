using System;
using System.Collections.Generic;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// Gamepad focus for our IMGUI pages (0.26). A page registers its focusable controls while it draws (rect + what A
    /// does + what left/right does); the stick / D-pad moves the focus to the nearest control in that direction, A
    /// presses it, left/right changes a slider or a toggle. The focused control gets a gold frame while the pad is used.
    /// Input is handled once per frame (in the Layout pass) against the controls of the previous frame.
    /// </summary>
    public class PadNav
    {
        public class Item { public Rect R; public Action Submit; public Action<int> Adjust; }

        readonly List<Item> _building = new List<Item>();
        List<Item> _last = new List<Item>();
        public int Focus;
        static readonly Color Gold = new Color(0.95f, 0.78f, 0.32f, 1f);

        /// <summary>Call at the start of each OnGUI pass.</summary>
        public void Begin() { _building.Clear(); }

        /// <summary>Registers a control; returns true when it has the focus (to draw it highlighted).</summary>
        public bool Add(Rect r, Action submit, Action<int> adjust = null)
        {
            _building.Add(new Item { R = r, Submit = submit, Adjust = adjust });
            return _building.Count - 1 == Focus;
        }

        /// <summary>Call at the end of each OnGUI pass: keeps the list and draws the frame.</summary>
        public void End(bool show)
        {
            if (Event.current.type == EventType.Repaint)
            {
                _last = new List<Item>(_building);
                if (Focus >= _last.Count) Focus = Math.Max(0, _last.Count - 1);
                if (show && Focus >= 0 && Focus < _last.Count) Frame(_last[Focus].R);
            }
        }

        public static void Frame(Rect r)
        {
            var c = GUI.color; GUI.color = Gold;
            const float t = 2f; r = new Rect(r.x - 4f, r.y - 2f, r.width + 8f, r.height + 4f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), Texture2D.whiteTexture);
            GUI.color = c;
        }

        /// <summary>Moves the focus; returns false when nothing lies that way (the page may leave, e.g. back to the game menu).</summary>
        public bool Move(int dx, int dy)
        {
            if (_last.Count == 0) return false;
            if (Focus < 0 || Focus >= _last.Count) { Focus = 0; return true; }
            var fromR = _last[Focus].R; var from = fromR.center;
            int best = -1; float bestScore = float.MaxValue;
            for (int i = 0; i < _last.Count; i++)
            {
                if (i == Focus) continue;
                var d = _last[i].R.center - from;
                float along = dx != 0 ? d.x * dx : d.y * dy;      // how far in the wanted direction
                float across = dx != 0 ? Mathf.Abs(d.y) : Mathf.Abs(d.x);
                if (along <= 2f) continue;
                if (dx != 0 && across > Mathf.Max(fromR.height, _last[i].R.height) * 0.75f) continue; // left / right stay on the row (0.27)
                float score = along + across * 2.5f;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best < 0) return false;
            Focus = best;
            return true;
        }

        public bool SubmitFocused()
        {
            if (Focus < 0 || Focus >= _last.Count || _last[Focus].Submit == null) return false;
            _last[Focus].Submit(); return true;
        }

        public bool AdjustFocused(int dir)
        {
            if (Focus < 0 || Focus >= _last.Count || _last[Focus].Adjust == null) return false;
            _last[Focus].Adjust(dir); return true;
        }

        public int Count => _last.Count;
    }
}
