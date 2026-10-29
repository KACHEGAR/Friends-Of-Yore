using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FriendsOfYore
{
    /// <summary>
    /// "Multiplayer Settings" page opened from the pause menu, with tabs like the game's keybinding page:
    /// Voice Chat (on/off, open mic or push-to-talk, volumes, echo, microphone check), Look (the bodies with previews)
    /// and Controls (keyboard key and gamepad button of every mod action, 0.26). Drawn over the pause menu on a
    /// 720-high canvas scaled to the screen; the game's menu is paused meanwhile and comes back with Back (or B).
    /// Gamepad: stick / D-pad move, A presses, left / right change a value, LB / RB switch tabs, B goes back.
    /// </summary>
    public class VoiceMenu
    {
        public bool Open { get; private set; }
        public Action Changed, Click;
        public Func<IEnumerable<(ulong id, string name)>> Players;
        /// <summary>Look tab: shown/hidden (the previews are only rendered while it is open) and drawn inside a rect.</summary>
        public Action<bool> LooksShown;
        public Action<Rect> DrawLooks;
        /// <summary>Look tab with the pad: move the focus in the grid (dx, dy) or pick the focused look (submit).</summary>
        public Action<int, int, bool> LooksNav;
        public Func<List<Bind>> Bindings;

        int _tab; // 0 voice, 1 look, 2 controls
        static readonly string[] Tabs = { "Voice Chat", "Look", "Controls" };
        float _slideClickAt;
        readonly Voice _v;
        EventSystem _paused; GameObject _prevSel;
        public Func<GameObject> FallbackSelect;
        GUIStyle _title, _row, _btn, _small;
        readonly PadNav _nav = new PadNav();
        // rebinding: which action, keyboard or pad, since when
        Bind _waitBind; bool _waitPad; float _waitSince;

        public VoiceMenu(Voice v) { _v = v; }

        public void Show()
        {
            Open = true; _v.TestMode = true; _tab = 0; _nav.Focus = Tabs.Length; _waitBind = null;
            _paused = EventSystem.current;
            _prevSel = _paused != null ? _paused.currentSelectedGameObject : null;
            if (_paused != null) _paused.enabled = false;
        }

        public void Hide()
        {
            if (!Open) return;
            if (_tab == 1) LooksShown?.Invoke(false);
            Open = false; _v.TestMode = false; _tab = 0; _waitBind = null;
            if (_paused != null)
            {   // the pad goes on from the Multiplayer Settings button (0.27)
                _paused.enabled = true;
                var sel = _prevSel != null && _prevSel.activeInHierarchy ? _prevSel : FallbackSelect?.Invoke();
                if (sel != null) { _paused.SetSelectedGameObject(null); _paused.SetSelectedGameObject(sel); }
            }
            _paused = null; _prevSel = null;
        }

        void SetTab(int t)
        {
            t = (t + Tabs.Length) % Tabs.Length;
            if (t == _tab) return;
            Click?.Invoke(); _waitBind = null;
            if (_tab == 1) LooksShown?.Invoke(false);
            _tab = t; _nav.Focus = Tabs.Length;
            if (_tab == 1) LooksShown?.Invoke(true);
        }

        void Styles()
        {
            if (_title != null && _title.font == Ui.GameFont) return;
            _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 44 };
            _row = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontSize = 22 };
            _btn = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontSize = 22 };
            _small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 17 };
            foreach (var s in new[] { _title, _row, _btn, _small })
            {
                if (Ui.GameFont != null) s.font = Ui.GameFont;
                s.normal.textColor = new Color(0.92f, 0.92f, 0.92f); s.hover.textColor = Color.white;
                s.wordWrap = false; s.clipping = TextClipping.Overflow;
            }
        }

        bool Button(Rect r, string text, bool focused)
        {
            bool hover = r.Contains(Event.current.mousePosition) || focused;
            _btn.normal.textColor = hover ? Color.white : new Color(0.8f, 0.8f, 0.8f);
            bool hit = GUI.Button(r, text, _btn);
            if (hit) Click?.Invoke();
            return hit;
        }

        void SlideClick() { if (Time.unscaledTime > _slideClickAt) { _slideClickAt = Time.unscaledTime + 0.08f; Click?.Invoke(); } }

        public void OnGUI()
        {
            if (!Open) return;
            Styles();
            var ev = Event.current;
            if (_waitBind != null) WaitForBinding(ev);
            else if (ev.type == EventType.Layout) PadInputs();
            var old = GUI.color; GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;
            // the page is laid out on a 720-high canvas and scaled to the screen (0.24.0 was fixed-size: small on 1080p and up)
            var keep = GUI.matrix;
            float k = Mathf.Max(0.75f, Screen.height / 720f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try { _nav.Begin(); Page(Screen.width / k, Screen.height / k); _nav.End(PadInput.Active && _tab != 1); }
            finally { GUI.matrix = keep; }
        }

        /// <summary>The pad, once per frame.</summary>
        void PadInputs()
        {
            if (PadInput.PrevTab) { SetTab(_tab - 1); PadInput.Use(); return; }
            if (PadInput.NextTab) { SetTab(_tab + 1); PadInput.Use(); return; }
            if (PadInput.Cancel) { Click?.Invoke(); PadInput.Use(); Hide(); return; }
            if (_tab == 1)
            {   // Look: the grid moves; up from its first row goes to the tabs
                int dx = PadInput.Left ? -1 : PadInput.Right ? 1 : 0, dy = PadInput.Up ? -1 : PadInput.Down ? 1 : 0;
                if (dx != 0 || dy != 0 || PadInput.Submit) LooksNav?.Invoke(dx, dy, PadInput.Submit);
                PadInput.Use(); return;
            }
            if (PadInput.Up) _nav.Move(0, -1);
            else if (PadInput.Down) _nav.Move(0, 1);
            else if (PadInput.Left) { if (!_nav.AdjustFocused(-1)) _nav.Move(-1, 0); }
            else if (PadInput.Right) { if (!_nav.AdjustFocused(1)) _nav.Move(1, 0); }
            else if (PadInput.Submit) _nav.SubmitFocused();
            PadInput.Use();
        }

        void Page(float sw, float sh)
        {
            float cx = sw / 2f, y = sh * 0.07f;
            GUI.Label(new Rect(cx - 300f, y, 600f, 60f), "Multiplayer Settings", _title); y += 74f;
            // tabs, like the game's keybinding page (LB / RB on a pad)
            float tw = 220f, tx = cx - Tabs.Length * tw / 2f;
            var ts = new GUIStyle(_title) { fontSize = 28 };
            for (int i = 0; i < Tabs.Length; i++)
            {
                var tr = new Rect(tx + i * tw, y, tw, 44f);
                bool on = i == _tab; int ti = i;
                bool f = _tab != 1 && _nav.Add(tr, () => SetTab(ti), d => SetTab(_tab + d));
                ts.normal.textColor = on ? Color.white : tr.Contains(Event.current.mousePosition) || f ? new Color(0.9f, 0.9f, 0.9f) : new Color(0.55f, 0.55f, 0.55f);
                if (GUI.Button(tr, Tabs[i], ts) && !on) SetTab(i);
                if (on) { var c1 = GUI.color; GUI.color = new Color(0.95f, 0.95f, 0.95f); GUI.DrawTexture(new Rect(tr.x + 30f, tr.yMax - 4f, tr.width - 60f, 2f), Texture2D.whiteTexture); GUI.color = c1; }
            }
            if (PadInput.Active) GUI.Label(new Rect(cx - 300f, y + 40f, 600f, 22f), "LB / RB - tabs     B - back", _small);
            y += 66f;
            if (_tab == 1)
            {
                float bottom = sh - 70f;
                DrawLooks?.Invoke(new Rect(cx - 500f, y, 1000f, bottom - y - 10f));
                BackButton(cx, bottom);
                return;
            }
            if (_tab == 2) { ControlsTab(cx, y); return; }
            VoiceTab(cx, y);
        }

        void VoiceTab(float cx, float y)
        {
            const float rowH = 46f, lw = 360f, gap = 30f;
            Rect L(float yy) => new Rect(cx - lw - gap / 2f, yy, lw, rowH);
            Rect R(float yy, float w = 360f) => new Rect(cx + gap / 2f, yy, w, rowH);

            GUI.Label(L(y), "Voice chat", _row);
            Action toggleV = () => { _v.Enabled = !_v.Enabled; Changed?.Invoke(); Click?.Invoke(); };
            if (Button(R(y), _v.Enabled ? "On" : "Off", _nav.Add(R(y, 200f), toggleV, d => toggleV()))) { _v.Enabled = !_v.Enabled; Changed?.Invoke(); }
            y += rowH;

            GUI.Label(L(y), "Microphone", _row);
            Action toggleM = () => { _v.PushToTalk = !_v.PushToTalk; Changed?.Invoke(); Click?.Invoke(); };
            if (Button(R(y), _v.PushToTalk ? $"Push to talk ({PttHint()})" : "Always on (open mic)", _nav.Add(R(y, 300f), toggleM, d => toggleM()))) { _v.PushToTalk = !_v.PushToTalk; Changed?.Invoke(); }
            y += rowH;

            Slider(L(y), new Rect(cx + gap / 2f, y + rowH / 2f - 6f, 300f, 20f), $"All friends: {_v.Volume * 100f:F0}%", _v.Volume, v => { _v.Volume = v; Changed?.Invoke(); });
            // one slider per friend in the lobby (for a quiet microphone)
            if (Players != null)
                foreach (var (id, name) in Players())
                {
                    y += rowH; var pid = id;
                    Slider(L(y), new Rect(cx + gap / 2f, y + rowH / 2f - 6f, 300f, 20f), $"{name}: {_v.PlayerGain(id) * 100f:F0}%", _v.PlayerGain(id), v => { _v.PlayerGains[pid] = v; Changed?.Invoke(); });
                }
            y += rowH;

            GUI.Label(L(y), "Mountain echo with distance", _row);
            Action toggleE = () => { Voice.EchoType = Voice.Echo ? "Off" : "Mountains"; Changed?.Invoke(); Click?.Invoke(); };
            if (Button(R(y), Voice.Echo ? "On" : "Off", _nav.Add(R(y, 200f), toggleE, d => toggleE()))) { Voice.EchoType = Voice.Echo ? "Off" : "Mountains"; Changed?.Invoke(); }
            y += rowH;

            GUI.Label(L(y), "Microphone check", _row);
            string check = !_v.Enabled ? "voice chat is off" : _v.Talking ? "hearing you" : "silent - say something";
            var c0 = _btn.normal.textColor;
            _btn.normal.textColor = _v.Talking ? new Color(0.55f, 1f, 0.55f) : new Color(0.7f, 0.7f, 0.7f);
            GUI.Label(R(y), check, _btn);
            _btn.normal.textColor = c0;
            y += rowH + 10f;
            GUI.Label(new Rect(cx - 400f, y, 800f, 30f), "The microphone is the one chosen in Steam (Settings - Voice). Keys: Controls tab.", _small);
            y += 60f;
            BackButton(cx, y);
        }

        string PttHint()
        {
            var b = Bindings?.Invoke()?.Find(x => x.Id == "PushToTalk");
            return b == null ? "" : PadInput.Active && !string.IsNullOrEmpty(b.Pad.Value) ? b.PadText : b.KeyText;
        }

        void Slider(Rect label, Rect slider, string text, float value, Action<float> set)
        {
            GUI.Label(label, text, _row);
            bool f = _nav.Add(new Rect(slider.x, slider.y - 8f, slider.width, slider.height + 16f), null, d => { set(Mathf.Clamp(Mathf.Round((value + d * 0.1f) * 20f) / 20f, 0f, 3f)); SlideClick(); });
            float nv = GUI.HorizontalSlider(slider, value, 0f, 3f);
            if (Mathf.Abs(nv - value) > 0.005f) { set(Mathf.Round(nv * 20f) / 20f); SlideClick(); }
        }

        // ---- Controls: a key and a pad button for every mod action (0.26) ----
        void ControlsTab(float cx, float y)
        {
            var binds = Bindings?.Invoke(); if (binds == null) return;
            const float rowH = 42f;
            float lx = cx - 430f, kx = cx - 40f, px = cx + 160f, clx = cx + 350f;
            var head = new GUIStyle(_small) { alignment = TextAnchor.MiddleLeft };
            GUI.Label(new Rect(kx, y, 180f, 26f), "Keyboard / mouse", head);
            GUI.Label(new Rect(px, y, 180f, 26f), "Gamepad", head);
            y += 30f;
            foreach (var b in binds)
            {
                var bb = b;
                GUI.Label(new Rect(lx, y, 380f, rowH), b.Label, _row);
                bool waitK = _waitBind == b && !_waitPad, waitP = _waitBind == b && _waitPad;
                var rk = new Rect(kx, y, 180f, rowH); var rp = new Rect(px, y, 180f, rowH); var rc = new Rect(clx, y, 90f, rowH);
                if (Button(rk, waitK ? "press a key..." : b.KeyText, _nav.Add(rk, () => StartWait(bb, false)))) StartWait(b, false);
                if (Button(rp, waitP ? (PadInput.Connected ? "press a button..." : "no gamepad") : b.PadText, _nav.Add(rp, () => StartWait(bb, true)))) StartWait(b, true);
                if (Button(rc, "Clear", _nav.Add(rc, () => { bb.Pad.Value = ""; Click?.Invoke(); }))) { b.Pad.Value = ""; }
                y += rowH;
            }
            y += 10f;
            GUI.Label(new Rect(cx - 450f, y, 900f, 26f), "Click a key or a button, then press the new one. Esc cancels. Clear removes the gamepad button.", _small);
            y += 50f;
            BackButton(cx, y);
        }

        void StartWait(Bind b, bool pad) { _waitBind = b; _waitPad = pad; _waitSince = Time.unscaledTime; }

        void WaitForBinding(Event ev)
        {
            float age = Time.unscaledTime - _waitSince;
            if (_waitPad)
            {
                if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape) { _waitBind = null; ev.Use(); return; }
                if (ev.type != EventType.Layout) return;
                if (age > 8f) { _waitBind = null; return; }
                var name = age > 0.3f ? PadInput.AnyButtonDown() : null;   // the A press that opened the wait is not taken
                if (name != null) { _waitBind.Pad.Value = name; _waitBind = null; Click?.Invoke(); Changed?.Invoke(); PadInput.Use(); }
                return;
            }
            if (ev.type == EventType.KeyDown && ev.keyCode != KeyCode.None)
            {
                if (ev.keyCode != KeyCode.Escape) { _waitBind.Key.Value = ev.keyCode; Click?.Invoke(); Changed?.Invoke(); }
                _waitBind = null; ev.Use(); return;
            }
            if (ev.type == EventType.Layout && age > 0.2f)
            {   // middle and side mouse buttons (IMGUI does not report the side buttons: read them directly, 0.27)
                foreach (var mk in new[] { KeyCode.Mouse2, KeyCode.Mouse3, KeyCode.Mouse4, KeyCode.Mouse5, KeyCode.Mouse6 })
                    if (Input.GetKeyDown(mk)) { _waitBind.Key.Value = mk; _waitBind = null; Click?.Invoke(); Changed?.Invoke(); return; }
            }
            if (ev.type == EventType.Layout && age > 10f) _waitBind = null;
        }

        void BackButton(float cx, float y)
        {
            var back = new Rect(cx - 80f, y, 160f, 54f);
            bool f = _tab != 1 && _nav.Add(back, () => { Click?.Invoke(); Hide(); });
            var bs = new GUIStyle(_title) { fontSize = 36 };
            bs.normal.textColor = back.Contains(Event.current.mousePosition) || f ? Color.white : new Color(0.85f, 0.85f, 0.85f);
            if (GUI.Button(back, "Back", bs)) { Click?.Invoke(); Hide(); }
            ReportButton(new Rect(cx + 140f, y + 8f, 230f, 40f));
        }

        float _reportSaidAt = -9f;

        /// <summary>Report a bug: not done yet, says "Coming soon".</summary>
        void ReportButton(Rect r)
        {
            Action press = () => { Click?.Invoke(); _reportSaidAt = Time.unscaledTime; };
            bool f = _tab != 1 && _nav.Add(r, press);
            var st = new GUIStyle(_title) { fontSize = 26 };
            st.normal.textColor = r.Contains(Event.current.mousePosition) || f ? Color.white : new Color(0.7f, 0.7f, 0.7f);
            if (GUI.Button(r, "Report a bug", st)) press();
            if (Time.unscaledTime - _reportSaidAt < 2.5f)
                GUI.Label(new Rect(r.x, r.y - 30f, r.width, 26f), "Coming soon", _small);
        }
    }
}
