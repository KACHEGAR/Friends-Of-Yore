using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>One mod action with a keyboard key and a gamepad button (0.26: Controls tab of Multiplayer Settings).</summary>
    public class Bind
    {
        public string Id, Label;
        public ConfigEntry<KeyCode> Key;
        public ConfigEntry<string> Pad;
        public string[] PadPrefs = new string[0]; // default pad buttons, first free one wins

        public bool Down() => (Key != null && Key.Value != KeyCode.None && Input.GetKeyDown(Key.Value)) || PadInput.ButtonDown(Pad?.Value);
        public string KeyText => Key == null || Key.Value == KeyCode.None ? "-" : Key.Value == KeyCode.Mouse2 ? "Middle mouse" : Key.Value == KeyCode.Mouse3 ? "Mouse 4 (side)" : Key.Value == KeyCode.Mouse4 ? "Mouse 5 (side)" : Key.Value == KeyCode.Mouse5 ? "Mouse 6" : Key.Value == KeyCode.Mouse6 ? "Mouse 7" : Key.Value.ToString();
        public string PadText => string.IsNullOrEmpty(Pad?.Value) ? "-" : PadInput.Short(Pad.Value);
        /// <summary>What to show in hints: the pad button when the pad is in use, else the key.</summary>
        public string Hint => PadInput.Active && !string.IsNullOrEmpty(Pad?.Value) ? PadText : KeyText;
    }

    public static class Binds
    {
        /// <summary>
        /// First time a pad is connected: give each action without a pad button the first of its preferred buttons
        /// that neither the game nor another action uses (the game's own pad map is read from Rewired).
        /// </summary>
        public static void PickPadDefaults(List<Bind> binds, ConfigEntry<bool> done)
        {
            if (done.Value || !PadInput.Connected) return;
            var all = PadInput.AllButtons(); if (all.Count == 0) return;
            var used = PadInput.GameButtons(out var summary);
            Plugin.Log.LogInfo($"gamepad: buttons {string.Join(", ", all)}; the game uses: {summary}");
            foreach (var b in binds) if (!string.IsNullOrEmpty(b.Pad.Value)) used.Add(b.Pad.Value);
            foreach (var b in binds)
            {
                if (!string.IsNullOrEmpty(b.Pad.Value)) continue;
                var pick = b.PadPrefs.FirstOrDefault(n => all.Any(a => string.Equals(a, n, System.StringComparison.OrdinalIgnoreCase)) && !used.Contains(n));
                if (pick == null) continue;
                b.Pad.Value = pick; used.Add(pick);
                Plugin.Log.LogInfo($"gamepad: {b.Label} -> {pick}");
            }
            done.Value = true;
        }
    }
}
