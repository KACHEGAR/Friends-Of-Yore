using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// The players in the lobby, drawn on the right of the game's pause menu (owner's design, 0.25): Steam picture, name
    /// (crown = lobby owner), mode icons, voice volume slider, height on the peak (or the place), Join / Invite per
    /// player; under the list Invite (hidden when the lobby is full) and Leave lobby. Laid out on a 720-high canvas
    /// scaled to the screen, like Multiplayer Settings.
    /// </summary>
    public class PauseList
    {
        public class Row
        {
            public ulong Id; public string Name; public bool Self, Owner, Talking; public Net.StateFlags Flags;
            public string Where;          // "123 m" on the same peak, else the place
            public bool CanJoin, CanInvite;
            public float Gain = 1f;
            public ushort Idols;
        }

        public Func<List<Row>> Rows;
        public Action<ulong> Join, Invite;
        public Action<ulong, float> SetGain;
        public Action InviteFriends, Leave, Click;
        public Func<bool> InLobby, LobbyFull;
        public Func<GameObject> FallbackSelect;

        GUIStyle _name, _small, _btn, _bigBtn, _title;
        readonly Dictionary<ulong, Texture2D> _avatars = new Dictionary<ulong, Texture2D>();
        readonly Dictionary<ulong, float> _avatarRetry = new Dictionary<ulong, float>();
        static Texture2D _dark;

        void Styles()
        {
            if (_name != null && (Ui.GameFont == null || _name.font == Ui.GameFont)) return;
            _name = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleLeft };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleLeft };
            _btn = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter };
            _bigBtn = new GUIStyle(GUI.skin.label) { fontSize = 17, alignment = TextAnchor.MiddleCenter };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 24, alignment = TextAnchor.MiddleCenter };
            foreach (var s in new[] { _name, _small, _btn, _bigBtn, _title })
            {
                if (Ui.GameFont != null) s.font = Ui.GameFont;
                s.normal.textColor = new Color(0.9f, 0.9f, 0.9f); s.wordWrap = false; s.clipping = TextClipping.Overflow;
            }
            if (_dark == null) { _dark = new Texture2D(1, 1); _dark.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f)); _dark.Apply(); UnityEngine.Object.DontDestroyOnLoad(_dark); }
        }

        bool Button(Rect r, string text, GUIStyle st, bool focused = false)
        {
            bool hover = r.Contains(Event.current.mousePosition) || (focused && _focused && PadInput.Active);
            var c = GUI.color; GUI.color = new Color(1f, 1f, 1f, hover ? 0.35f : 0.18f);
            GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = c;
            st.normal.textColor = hover ? Color.white : new Color(0.85f, 0.85f, 0.85f);
            bool hit = GUI.Button(r, text, st);
            if (hit) Click?.Invoke();
            return hit;
        }

        // ---- gamepad (0.26): right on the stick / D-pad enters the list, left at its edge or B goes back to the game menu ----
        readonly PadNav _nav = new PadNav();
        bool _focused; UnityEngine.EventSystems.EventSystem _paused; GameObject _prevSel;

        void Enter()
        {
            if (_focused || _nav.Count == 0) return;
            _focused = true; _nav.Focus = 0;
            _paused = UnityEngine.EventSystems.EventSystem.current;
            _prevSel = _paused != null ? _paused.currentSelectedGameObject : null;
            if (_paused != null) _paused.enabled = false;   // the game's menu stops listening to the pad meanwhile
            Click?.Invoke();
        }

        /// <summary>Back to the game's menu (also when the pause menu closes).</summary>
        public void Unfocus()
        {
            if (!_focused) return;
            _focused = false;
            if (_paused != null)
            {   // back to the button the pad was on (0.26 left the game's menu with nothing selected: the pad was stuck)
                _paused.enabled = true;
                var sel = _prevSel != null && _prevSel.activeInHierarchy ? _prevSel : FallbackSelect?.Invoke();
                if (sel != null) { _paused.SetSelectedGameObject(null); _paused.SetSelectedGameObject(sel); }
            }
            _paused = null; _prevSel = null;
        }

        void PadInputs()
        {
            if (!_focused)
            {
                if (PadInput.Right && PadInput.Active) { Enter(); PadInput.Use(); }
                return;
            }
            if (PadInput.Cancel) { Unfocus(); Click?.Invoke(); }
            else if (PadInput.Up) _nav.Move(0, -1);
            else if (PadInput.Down) _nav.Move(0, 1);
            else if (PadInput.Left) { if (!_nav.AdjustFocused(-1) && !_nav.Move(-1, 0)) { Unfocus(); Click?.Invoke(); } }
            else if (PadInput.Right) { if (!_nav.AdjustFocused(1)) _nav.Move(1, 0); }
            else if (PadInput.Submit) _nav.SubmitFocused();
            PadInput.Use();
        }

        public void OnGUI()
        {
            if (Rows == null) return;
            Styles();
            if (Event.current.type == EventType.Layout) PadInputs();
            var keep = GUI.matrix;
            float k = Mathf.Max(0.6f, Screen.height / 1080f); // 0.25.1: a compact list (it was half the screen at 1080p)
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try { _nav.Begin(); Draw(Screen.width / k, Screen.height / k); _nav.End(_focused && PadInput.Active); }
            finally { GUI.matrix = keep; }
        }

        void Draw(float sw, float sh)
        {
            var rows = Rows();
            const float w = 340f, pic = 48f;
            bool inLobby = InLobby != null && InLobby();
            float RowH(Row r) => !r.Self && (r.CanJoin || r.CanInvite) ? 78f : 58f;
            float listH = 0f; foreach (var r in rows) listH += RowH(r);
            listH += 40f + 34f;
            float x = sw - w - 30f, y = Mathf.Max(20f, sh / 2f - listH / 2f);
            GUI.DrawTexture(new Rect(x - 10f, y - 8f, w + 20f, listH + 14f), _dark);
            // title, like the game's own headings
            _title.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
            GUI.Label(new Rect(x, y - 4f, w, 32f), "Friends of Yore", _title);
            if (PadInput.Active && !_focused) { _small.alignment = TextAnchor.MiddleRight; GUI.Label(new Rect(x, y + 2f, w, 20f), "right - players", _small); _small.alignment = TextAnchor.MiddleLeft; }
            else if (PadInput.Active) { _small.alignment = TextAnchor.MiddleRight; GUI.Label(new Rect(x, y + 2f, w, 20f), "B - back", _small); _small.alignment = TextAnchor.MiddleLeft; }
            y += 34f;
            foreach (var r in rows)
            {
                // picture
                var av = Avatar(r.Id);
                var pr = new Rect(x, y + 2f, pic, pic);
                if (av != null) GUI.DrawTexture(pr, av, ScaleMode.ScaleToFit);
                else { var c0 = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.15f); GUI.DrawTexture(pr, Texture2D.whiteTexture); GUI.color = c0; }
                float tx = x + pic + 10f, tw = w - pic - 10f;
                // name (gold for the lobby owner), mode icons on the left of the line
                string name = r.Name + (r.Self ? " (you)" : "") + (r.Owner ? " - host" : "") + (r.Talking ? " - talking" : "");
                _name.normal.textColor = r.Owner ? new Color(0.98f, 0.8f, 0.35f) : new Color(0.92f, 0.92f, 0.92f);
                GUI.Label(new Rect(tx, y, tw, 24f), name, _name);   // 0.27: names start right after the picture
                var icons = GameIcons.For(r.Flags, r.Idols);
                float ix = tx + tw - icons.Count * 21f;
                foreach (var ic in icons) { GUI.DrawTexture(new Rect(ix, y + 3f, 18f, 18f), ic, ScaleMode.ScaleToFit); ix += 21f; }
                // height / place, and the voice volume of a friend
                GUI.Label(new Rect(tx, y + 26f, tw * 0.5f, 20f), r.Where ?? "", _small);
                if (!r.Self)
                {
                    GUI.Label(new Rect(tx + tw * 0.5f, y + 26f, 44f, 20f), $"{r.Gain * 100f:F0}%", _small);
                    var sr = new Rect(tx + tw * 0.5f + 42f, y + 31f, tw * 0.5f - 42f, 14f); var rid = r.Id; float gain = r.Gain;
                    _nav.Add(new Rect(sr.x, sr.y - 4f, sr.width, sr.height + 8f), null, d => SetGain?.Invoke(rid, Mathf.Clamp(Mathf.Round((gain + d * 0.1f) * 20f) / 20f, 0f, 3f)));
                    float g = GUI.HorizontalSlider(sr, r.Gain, 0f, 3f);
                    if (Mathf.Abs(g - r.Gain) > 0.005f) SetGain?.Invoke(r.Id, Mathf.Round(g * 20f) / 20f);
                    float bx = tx + tw;
                    if (r.CanJoin) { var jr = new Rect(bx - (r.CanInvite ? 140f : 70f), y + 52f, 66f, 20f); bool f = _nav.Add(jr, () => { Click?.Invoke(); Join?.Invoke(rid); }); if (Button(jr, "Join", _btn, f)) Join?.Invoke(r.Id); }
                    if (r.CanInvite) { var ir = new Rect(bx - 70f, y + 52f, 66f, 20f); bool f = _nav.Add(ir, () => { Click?.Invoke(); Invite?.Invoke(rid); }); if (Button(ir, "Invite", _btn, f)) Invite?.Invoke(r.Id); }
                }
                y += RowH(r);
            }
            // under the list: invite more friends (not when the lobby is full), leave the lobby
            bool full = LobbyFull != null && LobbyFull();
            float half = (w - 8f) / 2f;
            if (!full)
            {
                var ir = new Rect(x, y, inLobby ? half : w, 30f);
                bool f = _nav.Add(ir, () => { Click?.Invoke(); InviteFriends?.Invoke(); });
                if (Button(ir, inLobby ? "Invite" : "Invite friends", _bigBtn, f)) InviteFriends?.Invoke();
            }
            if (inLobby)
            {
                var lr = new Rect(full ? x : x + half + 8f, y, full ? w : half, 30f);
                bool f = _nav.Add(lr, () => { Click?.Invoke(); Leave?.Invoke(); });
                if (Button(lr, "Leave lobby", _bigBtn, f)) Leave?.Invoke();
            }
        }

        Texture2D Avatar(ulong id)
        {
            if (_avatars.TryGetValue(id, out var t) && t != null) return t;
            if (_avatarRetry.TryGetValue(id, out var at) && Time.unscaledTime < at) return null;
            _avatarRetry[id] = Time.unscaledTime + 1f;
            try
            {
                if (!SteamManager.Initialized) return null;
                int h = SteamFriends.GetMediumFriendAvatar(new CSteamID(id));
                if (h <= 0 || !SteamUtils.GetImageSize(h, out uint w, out uint hh) || w == 0) return null;
                var buf = new byte[w * hh * 4];
                if (!SteamUtils.GetImageRGBA(h, buf, buf.Length)) return null;
                var flipped = new byte[buf.Length]; int row = (int)w * 4;
                for (int yy = 0; yy < hh; yy++) Buffer.BlockCopy(buf, yy * row, flipped, ((int)hh - 1 - yy) * row, row);
                t = new Texture2D((int)w, (int)hh, TextureFormat.RGBA32, false);
                t.LoadRawTextureData(flipped); t.Apply();
                UnityEngine.Object.DontDestroyOnLoad(t);
                _avatars[id] = t;
                return t;
            }
            catch (Exception e) { Plugin.Log.LogInfo("avatar: " + e.Message); return null; }
        }
    }
}
