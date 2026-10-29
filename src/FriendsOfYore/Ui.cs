using System.Collections.Generic;
using System.Linq;
using FriendsOfYore.Net;
using UnityEngine;

namespace FriendsOfYore
{
    /// <summary>Game font, shared IMGUI styles and small text helpers.</summary>
    public static class Ui
    {
        public static Font GameFont;
        static GUIStyle _label, _box, _button, _title;

        /// <summary>Picks the font most used by the game's own UI Text components (called after scene loads).</summary>
        public static void FindGameFont()
        {
            if (GameFont != null) return;
            var fonts = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>()
                .Where(t => t != null && t.font != null && t.font.name != "Arial")
                .GroupBy(t => t.font).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            if (fonts.Count == 0) return;
            GameFont = fonts[0];
            _label = _box = _button = _title = null;
            Plugin.Log.LogInfo($"game font: {GameFont.name} (dynamic={GameFont.dynamic}); candidates: {string.Join(", ", fonts.Take(5).Select(f => f.name))}");
        }

        static GUIStyle Make(GUIStyle baseStyle, int size, TextAnchor anchor)
        {
            var s = new GUIStyle(baseStyle) { fontSize = size, alignment = anchor, richText = true, wordWrap = false, clipping = TextClipping.Overflow };
            if (GameFont != null) s.font = GameFont;
            return s;
        }

        public static GUIStyle Label => _label ?? (_label = Shadowed(Make(GUI.skin.label, 17, TextAnchor.UpperRight)));
        public static GUIStyle Title => _title ?? (_title = Shadowed(Make(GUI.skin.label, 21, TextAnchor.UpperRight)));
        public static GUIStyle Box => _box ?? (_box = Make(GUI.skin.box, 22, TextAnchor.MiddleCenter));
        public static GUIStyle Button => _button ?? (_button = Make(GUI.skin.button, 17, TextAnchor.MiddleLeft));

        static GUIStyle Shadowed(GUIStyle s) { s.normal.textColor = new Color(1f, 0.97f, 0.9f); return s; }

        /// <summary>Label with a 1 px dark shadow so it reads on snow and sky.</summary>
        public static void ShadowLabel(Rect r, string text, GUIStyle style)
        {
            var c = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.75f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
            style.normal.textColor = c;
            GUI.Label(r, text, style);
        }

        public static void Banner(float y, string text) => GUI.Box(new Rect(Screen.width / 2 - 420, y, 840, 52), text, Box);

        public static string ActivityText(Activity a)
        {
            switch (a)
            {
                case Activity.Coffee: return "drinking coffee";
                case Activity.Chalk: return "chalking up";
                case Activity.Monocular: return "looking through the monocular";
                case Activity.Hammer: return "hammering a piton";
                case Activity.OnRope: return "on a rope";
                case Activity.Bivouac: return "resting at a bivouac";
                case Activity.Stamping: return "stamping the journal";
                case Activity.Watch: return "checking the watch";
                case Activity.Menu: return "in the menu";
                default: return "";
            }
        }

        /// <summary>Applies the game font to a 3D TextMesh (font + its material).</summary>
        public static void Apply(TextMesh tm)
        {
            if (GameFont == null || tm == null || tm.font == GameFont) return;
            tm.font = GameFont;
            var mr = tm.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = GameFont.material;
        }

        /// <summary>Cursor is needed for clicking in our panels; the game re-locks it every frame.</summary>
        public static void LockCursorBack() { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        public static void FreeCursor() { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }

        public static readonly Dictionary<string, string> PrettyModel = new Dictionary<string, string>
        {
            { "NPC_PARCELFATHER", "Parcel father" }, { "NPC_ParcelBoy", "Parcel boy" }, { "NPC_OASOfficial", "OAS official" },
            { "Walker", "Walker" }, { "Porter", "Porter" }, { "OAS_Climber", "OAS climber" },
            { "NPC_Pearse1", "Pearse 1" }, { "NPC_Pearse2_fix", "Pearse 2" }, { "Goat", "Goat" }, { "Gull", "Wally and Dolly" },
        };
        public static string ModelName(string key) => key == "NPC_ParcelBoy" ? ParcelBoyName : PrettyModel.TryGetValue(key ?? "", out var n) ? n : (key ?? "?").Replace("NPC_", "").Replace('_', ' ');

        /// <summary>Parcel Boy gets a new name each time the looks are opened (owner's joke, 0.26).</summary>
        static readonly string[] ParcelBoyNames = { "Cursed Parcel Boy", "Long Parcel Boy", "Parcel boooooy" };
        public static string ParcelBoyName = "Parcel boy";
        public static void ShuffleParcelBoy()
        {
            string n; do n = ParcelBoyNames[UnityEngine.Random.Range(0, ParcelBoyNames.Length)]; while (n == ParcelBoyName && ParcelBoyNames.Length > 1);
            ParcelBoyName = n;
        }
    }
}
