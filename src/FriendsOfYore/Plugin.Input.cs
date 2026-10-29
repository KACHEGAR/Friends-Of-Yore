using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FriendsOfYore.Net;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>The mod's own actions (each with a key and a gamepad button), marks and the gull joke.</summary>
    public partial class Plugin
    {
        internal List<Bind> BindList;
        Bind _bMark, _bPtt, _bOverlay;
        ConfigEntry<bool> _padDefaultsDone;
        ConfigEntry<KeyCode> _pttKey;
        float _swallowPauseUntil;

        void SetupBinds()
        {
            Bind B(string id, string label, ConfigEntry<KeyCode> key, params string[] prefs) =>
                new Bind { Id = id, Label = label, Key = key, Pad = Config.Bind("Gamepad", id, "", $"Gamepad button for: {label} (a Rewired button name, e.g. Right Stick Button). Empty = none."), PadPrefs = prefs };
            _bMark = B("Mark", "Place a mark", _keyMark, "Right Stick Button", "Left Stick Button", "Back", "Y", "X");
            _bPtt = B("PushToTalk", "Push to talk (hold)", _pttKey, "Left Stick Button", "Right Stick Button", "X", "Y");
            _bOverlay = B("Overlay", "Show / hide the mod text", _keyMenu);
            BindList = new List<Bind> { _bMark, _bPtt, _bOverlay };
            _padDefaultsDone = Config.Bind("Gamepad", "DefaultsPicked", false, "Internal: free pad buttons were given to the actions once.");
        }

        void Keys()
        {
            try { Binds.PickPadDefaults(BindList, _padDefaultsDone); } catch (Exception e) { if (!_padDefaultsDone.Value) { _padDefaultsDone.Value = true; Log.LogWarning("gamepad defaults: " + e.Message); } }
            _voice.PushKey = _pttKey.Value; _voice.PushPad = _bPtt.Pad.Value;
            bool free = !MenuOnScreen;
            if (_bOverlay.Down() && (free || Input.GetKeyDown(_keyMenu.Value))) _showOverlay = !_showOverlay;
            if (_bMark.Down() && free) PlaceMark();
            // ESC closes the invite list; the game would also open its pause menu on the same key
            if (_inviteOpen && Input.GetKeyDown(KeyCode.Escape)) { ShowInvite(false); _swallowPauseUntil = Time.unscaledTime + 0.5f; }
            if (Time.unscaledTime < _swallowPauseUntil && _menuInvite.PauseOpen) { _menuInvite.ClosePause(); _swallowPauseUntil = 0f; Ui.LockCursorBack(); }
            if (_picker.Open) _picker.Tick(Time.unscaledDeltaTime);   // the Look tab's previews
            if (_net is SteamTransport st)
            {
                if (Input.GetKeyDown(_keyHost.Value))
                {
                    bool own = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    if (_inviteOpen) ShowInvite(false);
                    else InviteViaSteam(st, own);
                }
                if (Input.GetKeyDown(_keyLeave.Value)) { LeaveLobby(st); ShowInvite(false); }
            }
            if (_inviteOpen)
                for (int i = 0; i < 9; i++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i)) PickInvite(i);
        }

        void PlaceMark()
        {
            if (!Marks.Aim(out var at)) { Banner("Nothing to mark there"); return; }
            _marks.Set(_net != null ? _net.LocalId : 0, "You", new Color(1f, 0.85f, 0.3f), at, _scene);
            BroadcastEvent(WorldKind.Mark, "mark", at);
            Log.LogInfo($"mark placed at {at}");
        }

        int _airSwings; bool _armWasL, _armWasR, _criedThisAir;

        /// <summary>
        /// Ten arm swings in a row while in the air (not standing, not on a hold or rope): a random gull cry, heard by
        /// friends too; once per jump/fall. Standing up or grabbing anything starts the count again.
        /// </summary>
        void GullJoke()
        {
            var c = _climbing;
            if (c == null) { _airSwings = 0; return; }
            bool air = c.fpController != null && !c.fpController.IsGrounded() && !c.grabbingLeft && !c.grabbingRight && !(_activity.Rope != null && _activity.Rope.attached);
            bool l = c.LTrigger > 0f, r = c.RTrigger > 0f;
            if (!air) { _airSwings = 0; _criedThisAir = false; }
            else if (!_criedThisAir)
            {
                if (l && !_armWasL) _airSwings++;
                if (r && !_armWasR) _airSwings++;
                if (_airSwings >= 10)
                {
                    _airSwings = 0; _criedThisAir = true;
                    var at = c.playerBody.position;
                    if (FallSounds.PlayGull(at)) BroadcastEvent(WorldKind.Fall, "gull", at);
                }
            }
            _armWasL = l; _armWasR = r;
        }
    }
}
