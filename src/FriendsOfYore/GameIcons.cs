using System.Collections.Generic;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>
    /// Icons taken from the game's own menu (TitleScreen assets): the skull of "You fall, you die", the free solo climber
    /// and the rope of the rope counter (used for the safety harness). Copied into our own textures the first time they
    /// are in memory (the title screen), so they stay after the menu is unloaded.
    /// </summary>
    public static class GameIcons
    {
        public static Texture2D Skull, FreeSolo, Rope;
        static float _nextTry;

        public static bool Ready => Skull != null && FreeSolo != null && Rope != null;

        /// <summary>The Alps idols (DLC), in the bit order of PlayerState.Idols (0.27).</summary>
        public static readonly string[] IdolNames = { "crimps", "slopers", "feathers", "pitches", "ice", "pinches", "greaterbalance", "sundown", "seeds", "gravity" };
        public static readonly Texture2D[] Idols = new Texture2D[IdolNames.Length];
        static bool AllIdols { get { foreach (var t in Idols) if (t == null) return false; return true; } }
        static string _triedScene;

        public static void TryCapture(string scene = null)
        {
            if (Ready && AllIdols) return;
            if (Ready && scene == _triedScene) return;          // idols: one look per scene once the main icons are in
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 3f; _triedScene = scene;
            int before = 0; foreach (var t in Idols) if (t != null) before++;
            foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sp == null || sp.texture == null) continue;
                try
                {
                    if (Skull == null && sp.name == "skull") Skull = Copy(sp);
                    else if (FreeSolo == null && sp.name == "climber_transparent_freesolo") FreeSolo = Copy(sp);
                    else if (Rope == null && sp.name == "ropes_used") Rope = Copy(sp);
                    else if (sp.name.StartsWith("idol_"))
                    {
                        int i = System.Array.IndexOf(IdolNames, sp.name.Substring(5));
                        if (i >= 0 && Idols[i] == null) Idols[i] = Copy(sp);
                    }
                }
                catch (System.Exception e) { Plugin.Log.LogInfo($"icon {sp.name}: {e.Message}"); }
            }
            int after = 0; foreach (var t in Idols) if (t != null) after++;
            if (Ready && !_logged) { _logged = true; Plugin.Log.LogInfo("game icons kept: skull, free solo, rope"); }
            if (after > before) Plugin.Log.LogInfo($"idol icons kept: {after}/{Idols.Length}");
        }
        static bool _logged;

        /// <summary>The local save's idols in use (0.27).</summary>
        public static ushort LocalIdols()
        {
            var g = GameManager.control; if (g == null) return 0;
            bool[] on = { g.alps_statue_crimps_InUse, g.alps_statue_slopers_InUse, g.alps_statue_feathers_InUse, g.alps_statue_pitches_InUse, g.alps_statue_ice_InUse,
                          g.alps_statue_pinches_InUse, g.alps_statue_greaterbalance_InUse, g.alps_statue_sundown_InUse, g.alps_statue_seeds_InUse, g.alps_statue_gravity_InUse };
            ushort m = 0; for (int i = 0; i < on.Length; i++) if (on[i]) m |= (ushort)(1 << i);
            return m;
        }

        static Texture2D Copy(Sprite sp)
        {
            var tex = sp.texture; var r = sp.textureRect;
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(Mathf.Max(1, (int)r.width), Mathf.Max(1, (int)r.height), TextureFormat.RGBA32, false) { name = "FoY_" + sp.name };
            t.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            Object.DontDestroyOnLoad(t);
            return t;
        }

        static readonly Dictionary<Texture2D, Sprite> _sprites = new Dictionary<Texture2D, Sprite>();
        public static Sprite AsSprite(Texture2D t)
        {
            if (t == null) return null;
            if (!_sprites.TryGetValue(t, out var s) || s == null)
            {
                s = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), Mathf.Max(t.width, t.height));
                Object.DontDestroyOnLoad(s);
                _sprites[t] = s;
            }
            return s;
        }

        /// <summary>The icons for a player's flags, in a fixed order: harness, you fall you die, free solo.</summary>
        public static List<Texture2D> For(Net.StateFlags f) => For(f, 0);

        public static List<Texture2D> For(Net.StateFlags f, ushort idols)
        {
            var l = new List<Texture2D>(6);
            if ((f & Net.StateFlags.Harness) != 0 && Rope != null) l.Add(Rope);
            if ((f & Net.StateFlags.Yfyd) != 0 && Skull != null) l.Add(Skull);
            if ((f & Net.StateFlags.FreeSolo) != 0 && FreeSolo != null) l.Add(FreeSolo);
            for (int i = 0; i < Idols.Length; i++) if ((idols & (1 << i)) != 0 && Idols[i] != null) l.Add(Idols[i]);
            return l;
        }
    }
}
