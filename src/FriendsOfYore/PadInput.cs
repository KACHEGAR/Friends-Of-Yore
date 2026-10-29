using System;
using System.Collections.Generic;
using System.Linq;
using Rewired;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// Gamepad input for the mod (0.26), read through the game's own Rewired:
    /// - menu navigation from the game's UI actions (UIHorizontal / UIVertical / UISubmit / UICancel), so it follows
    ///   whatever the game maps there (sticks, D-pad, A / B), with key-repeat when held;
    /// - mod buttons (mark, push-to-talk, mod text) bound to a gamepad button BY NAME ("Right Stick
    ///   Button", "Y", ...), so any pad Rewired knows works; defaults are picked among buttons the game does not use.
    /// </summary>
    public static class PadInput
    {
        static Player P { get { try { return ReInput.isReady ? ReInput.players.GetPlayer(0) : null; } catch { return null; } } }

        /// <summary>The pad was the last thing touched: menus show their focus frame and pad hints.</summary>
        public static bool Active
        {
            get
            {
                try { var c = P?.controllers.GetLastActiveController(); return c != null && c.type == ControllerType.Joystick; }
                catch { return false; }
            }
        }

        public static bool Connected { get { try { return P != null && P.controllers.joystickCount > 0; } catch { return false; } } }

        // ---- navigation, sampled once per frame (Tick from Update), read by the menus in OnGUI ----
        public static bool Up, Down, Left, Right, Submit, Cancel, PrevTab, NextTab;
        static int _dirY, _dirX; static float _repeatY, _repeatX;

        public static void Tick()
        {
            Up = Down = Left = Right = Submit = Cancel = PrevTab = NextTab = false;
            var p = P; if (p == null) return;
            try
            {
                float v = p.GetAxisRaw("UIVertical"), h = p.GetAxisRaw("UIHorizontal");
                Step(v, ref _dirY, ref _repeatY, out var pos, out var neg); Up = pos; Down = neg;
                Step(h, ref _dirX, ref _repeatX, out pos, out neg); Right = pos; Left = neg;
                Submit = p.GetButtonDown("UISubmit");
                Cancel = p.GetButtonDown("UICancel");
                PrevTab = ButtonDown("Left Shoulder") || ButtonDown("LB");
                NextTab = ButtonDown("Right Shoulder") || ButtonDown("RB");
            }
            catch { }
        }

        static void Step(float a, ref int dir, ref float repeatAt, out bool pos, out bool neg)
        {
            pos = neg = false;
            int d = a > 0.55f ? 1 : a < -0.55f ? -1 : 0;
            float now = Time.unscaledTime;
            if (d == 0) { dir = 0; return; }
            if (d != dir) { dir = d; repeatAt = now + 0.38f; }
            else if (now >= repeatAt) repeatAt = now + 0.11f;
            else return;
            if (d > 0) pos = true; else neg = true;
        }

        /// <summary>Consumes this frame's navigation (so two menus never act on one press).</summary>
        public static void Use() { Up = Down = Left = Right = Submit = Cancel = PrevTab = NextTab = false; }

        // ---- buttons by name ----
        static IEnumerable<Joystick> Pads() { var p = P; if (p == null) return Enumerable.Empty<Joystick>(); try { return p.controllers.Joysticks; } catch { return Enumerable.Empty<Joystick>(); } }

        static int Id(Joystick j, string name)
        {
            foreach (var e in j.ButtonElementIdentifiers) if (e != null && string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase)) return e.id;
            return -1;
        }

        public static bool ButtonDown(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var j in Pads()) { int id = Id(j, name); if (id >= 0 && j.GetButtonDownById(id)) return true; }
            return false;
        }

        public static bool Button(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var j in Pads()) { int id = Id(j, name); if (id >= 0 && j.GetButtonById(id)) return true; }
            return false;
        }

        /// <summary>For rebinding: the name of a pad button pressed this frame, or null.</summary>
        public static string AnyButtonDown()
        {
            foreach (var j in Pads())
                foreach (var e in j.ButtonElementIdentifiers)
                    if (e != null && j.GetButtonDownById(e.id)) return e.name;
            return null;
        }

        /// <summary>Pad buttons the game itself has actions on (player 0's joystick maps).</summary>
        public static HashSet<string> GameButtons(out string summary)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var parts = new List<string>();
            summary = "";
            var p = P; if (p == null) return used;
            try
            {
                foreach (var map in p.controllers.maps.GetAllMaps(ControllerType.Joystick))
                    foreach (var aem in map.AllMaps)
                    {
                        if (aem == null || string.IsNullOrEmpty(aem.elementIdentifierName)) continue;
                        used.Add(aem.elementIdentifierName);
                        parts.Add($"{aem.elementIdentifierName}={aem.actionDescriptiveName}");
                    }
            }
            catch (Exception e) { summary = "maps unreadable: " + e.Message; return used; }
            summary = string.Join(", ", parts.Distinct());
            return used;
        }

        public static List<string> AllButtons()
        {
            var l = new List<string>();
            foreach (var j in Pads()) foreach (var e in j.ButtonElementIdentifiers) if (e != null && !l.Contains(e.name)) l.Add(e.name);
            return l;
        }

        /// <summary>Short labels for the hints: the pad's own names, shortened.</summary>
        public static string Short(string name)
        {
            if (string.IsNullOrEmpty(name)) return "-";
            switch (name)
            {
                case "Right Stick Button": return "R3";
                case "Left Stick Button": return "L3";
                case "Left Shoulder": return "LB";
                case "Right Shoulder": return "RB";
                case "Left Trigger": return "LT";
                case "Right Trigger": return "RT";
                default: return name;
            }
        }
    }
}
