using System;
using UnityEngine;
using UnityEngine.UI;

namespace FriendsOfYore
{
    /// <summary>
    /// One entry in the game's pause menu (ESC), placed just above Quit: "Multiplayer Settings" (0.25: the invite entry
    /// moved to the player list beside the menu).
    /// Each is a copy of the game's own Quit button (same font, size and spacing) with the game's scripts removed and our
    /// own click. (The extra "Keybinding Options" seen in 0.11-0.15 came from the model preload, not from these.)
    /// </summary>
    public class MenuInvite
    {
        InGameMenu _menu; float _scanAt;
        GameObject _invite, _voice;
        public Action OnClick, OnVoice;
        public const string Label = "Invite Friends of Yore", VoiceLabel = "Multiplayer Settings";

        public void OnSceneLoaded() { _menu = null; _scanAt = 0f; _invite = _voice = null; }

        /// <summary>The game's own menu click (InGameMenu.menuClick).</summary>
        public void Click()
        {
            if (_menu == null) _menu = UnityEngine.Object.FindObjectOfType<InGameMenu>();
            var a = _menu != null ? _menu.menuClick : null;
            if (a == null) return;
            a.pitch = 1f; a.Play();
        }

        /// <summary>Closes the game's pause menu (ESC that was meant for one of our panels).</summary>
        public void ClosePause() { if (PauseOpen) try { _menu.Resume(); } catch { } }

        public bool PauseOpen => _menu != null && _menu.inGameMenuObject != null && _menu.inGameMenuObject.activeInHierarchy;
        public GameObject VoiceButton => _voice;
        bool _wasOpen;

        /// <summary>
        /// The game's pause buttons use explicit pad navigation (Resume -> Options -> Quit -> Resume); our button is a copy of
        /// Quit, so the pad skipped it (0.26). Each time the menu opens the visible buttons are chained top to bottom again.
        /// </summary>
        void FixNavigation()
        {
            if (_voice == null) return;
            var parent = _voice.transform.parent; if (parent == null) return;
            var list = new System.Collections.Generic.List<Selectable>();
            foreach (Transform t in parent)
            {
                if (!t.gameObject.activeInHierarchy) continue;
                var sel = t.GetComponent<Selectable>();
                if (sel != null && sel.interactable) list.Add(sel);
            }
            if (list.Count < 2) return;
            list.Sort((a, b) => b.transform.position.y.CompareTo(a.transform.position.y));
            for (int i = 0; i < list.Count; i++)
            {
                var nav = list[i].navigation;
                nav.mode = Navigation.Mode.Explicit;
                nav.selectOnUp = list[(i - 1 + list.Count) % list.Count];
                nav.selectOnDown = list[(i + 1) % list.Count];
                list[i].navigation = nav;
            }
        }

        public void Update()
        {
            bool open = PauseOpen;
            if (open && !_wasOpen) try { FixNavigation(); } catch (Exception e) { Plugin.Log.LogInfo("menu navigation: " + e.Message); }
            _wasOpen = open;
            if (_voice != null || Time.unscaledTime < _scanAt) return;
            _scanAt = Time.unscaledTime + 1f;
            if (_menu == null) _menu = UnityEngine.Object.FindObjectOfType<InGameMenu>();
            if (_menu == null || _menu.quitButton == null) return;
            var quit = _menu.quitButton.gameObject;
            _voice = Make(quit, "FoY_VoiceButton", VoiceLabel, () => { Click(); OnVoice?.Invoke(); });
            // order: ... Options, Multiplayer Settings, Quit (0.25: inviting moved to the player list on the right)
            int q = quit.transform.GetSiblingIndex();
            _voice.transform.SetSiblingIndex(q);
            var parent = quit.transform.parent;
            if (parent != null && parent.GetComponent<LayoutGroup>() == null)
            {   // no layout: shift Quit down a row and put ours in its place
                var qr = quit.transform as RectTransform; float step = qr.rect.height * 1.2f;
                ((RectTransform)_voice.transform).anchoredPosition = qr.anchoredPosition;
                qr.anchoredPosition += new Vector2(0f, -step);
            }
            Plugin.Log.LogInfo($"pause menu: added '{VoiceLabel}' above Quit in {parent?.name}");
            try { FixNavigation(); } catch { }
        }

        static GameObject Make(GameObject src, string name, string label, Action onClick)
        {
            var go = UnityEngine.Object.Instantiate(src, src.transform.parent, false);
            go.name = name;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                var ns = c.GetType().Namespace ?? "";
                bool keep = (ns == "UnityEngine" || ns == "UnityEngine.UI") && !(c is UnityEngine.EventSystems.EventTrigger);
                if (!keep) UnityEngine.Object.DestroyImmediate(c);
            }
            var b = go.GetComponent<UnityEngine.UI.Button>();
            if (b != null) { b.onClick = new UnityEngine.UI.Button.ButtonClickedEvent(); b.onClick.AddListener(() => onClick()); }
            foreach (var t in go.GetComponentsInChildren<Text>(true)) { t.text = label; t.horizontalOverflow = HorizontalWrapMode.Overflow; }
            return go;
        }

        public void OnGUI() { }
    }
}
