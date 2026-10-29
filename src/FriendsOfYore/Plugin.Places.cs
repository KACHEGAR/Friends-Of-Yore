using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FriendsOfYore.Net;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FriendsOfYore
{
    /// <summary>Places: where we are (told to friends), following / joining / inviting, Workshop peaks.</summary>
    public partial class Plugin
    {
        const string WorkshopOutpost = "Peaks_CustomLevel_Staging", CustomPeak = "Peaks_CustomLevel";
        /// <summary>Separates the fields of a Place / Note key.</summary>
        const string PlaceSep = "\u0001";

        /// <summary>Places with a location choice (the cabins, the Workshop Outpost): calm enough to load NPC bodies in the background.</summary>
        static bool IsHub(string scene) => scene == "Alps_Main" || scene == WorkshopOutpost || (scene != null && scene.Contains("Cabin"));
        static bool IsPeak(string scene) => !string.IsNullOrEmpty(scene) && scene != "(loading)" && scene != "TitleScreen" && !IsHub(scene);

        // ---------------------------------------------------------------- where we are
        float _placeSentAt; string _placeSentScene;
        EnterPeakScene _eps; string _epsScene;

        /// <summary>Our place as the game names it (the peak title shown on arrival), every 10 s, for friends' player list.</summary>
        void SendPlace()
        {
            if (_net == null || _net.Peers.Count == 0) return;
            if (_placeSentScene == _scene && Time.unscaledTime < _placeSentAt + 10f) return;
            _placeSentAt = Time.unscaledTime; _placeSentScene = _scene;
            string key = MyPlaceTitle();
            var custom = MyCustomPeak();   // a Workshop peak: what a friend needs to load the same one (title | folder | compendium | peak | page)
            if (custom != null) key = key + PlaceSep + string.Join(PlaceSep, custom);
            _net.Broadcast(new WorldEvent { Scene = _scene, Kind = WorldKind.Place, Key = key }.Write(), true);
        }

        string MyPlaceTitle()
        {
            var clm = CustomLevelManager.control;
            if (_scene == CustomPeak && clm != null && !string.IsNullOrEmpty(clm.peakName)) return clm.peakName;
            if (IsPeak(_scene))
            {
                if (_eps == null || _epsScene != _scene) { _eps = FindObjectOfType<EnterPeakScene>(); _epsScene = _scene; }
                if (_eps != null && _eps.peakNameText != null && !string.IsNullOrEmpty(_eps.peakNameText.text)) return _eps.peakNameText.text.Trim();
            }
            return PlaceName(_scene);
        }

        /// <summary>The Workshop peak we are on: folder, compendium, peak, page (null elsewhere).</summary>
        string[] MyCustomPeak()
        {
            var clm = CustomLevelManager.control;
            if (_scene != CustomPeak || clm == null || string.IsNullOrEmpty(clm.desiredFolderPath)) return null;
            return new[] { clm.desiredFolderPath, clm.compendiumName ?? "", clm.peakName ?? "", clm.peakNumber.ToString() };
        }

        /// <summary>A friend told us where they are (WorldKind.Place).</summary>
        void ReceivePlace(ulong from, WorldEvent ev)
        {
            if (!Remotes.TryGetValue(from, out var rp)) return;
            var parts = (ev.Key ?? "").Split(new[] { PlaceSep }, StringSplitOptions.None);
            string prevScene = rp.PlaceScene, prevTitle = rp.PlaceTitle;
            rp.PlaceTitle = parts[0]; rp.PlaceScene = ev.Scene;
            if (prevScene != ev.Scene)
            {
                if (IsPeak(prevScene) && !string.IsNullOrEmpty(prevTitle)) Note($"{rp.DisplayName} left {prevTitle}");
                if (IsPeak(ev.Scene)) Note($"{rp.DisplayName} started climbing {parts[0]}");
            }
            rp.CustomPlace = parts.Length >= 5 ? parts : null;
        }

        Barometer _baro; string _baroScene; Transform _baroStart; float _baroK = 1f;

        /// <summary>Height as the game's barometer shows it ("120 m up"), or null where there is none.</summary>
        string HeightAt(Vector3 p)
        {
            if (_baroScene != _scene)
            {
                _baroScene = _scene; _baro = FindObjectOfType<Barometer>(); _baroStart = null; _baroK = 1f;
                if (_baro != null)
                {
                    try { _baroStart = HarmonyLib.Traverse.Create(_baro).Field("playerTransformStartPosition").GetValue<Transform>(); } catch { }
                    _baroK = _baro.isGreatBulwark ? 3.12f : _baro.isSolemnTempest ? 3.125f : _baro.alp0 ? 1.355f : 1f;
                }
            }
            if (_baroStart == null) return null;
            return $"{(p.y - _baroStart.position.y) * _baroK:F0} m up";
        }

        /// <summary>Where a friend is, for the pause list: the height on our peak, else their place's name.</summary>
        string WhereOf(RemotePlayer rp)
        {
            if (rp.Shown == null) return "";
            var sc = rp.Shown.Scene;
            if (sc == "(loading)") return "loading...";
            if (sc == _scene && IsPeak(sc)) return HeightAt(RemotePlayer.ToV(rp.Shown.Body)) ?? PlaceName(sc);
            return PlaceTitleOf(rp);
        }

        /// <summary>The name of the place a friend is in (the title they sent, or one made from the scene).</summary>
        static string PlaceTitleOf(RemotePlayer rp)
        {
            var sc = rp.Shown?.Scene;
            return rp.PlaceScene == sc && !string.IsNullOrEmpty(rp.PlaceTitle) ? rp.PlaceTitle : PlaceName(sc);
        }

        // ---------------------------------------------------------------- following a friend: Y / N (A / B)
        readonly Dictionary<ulong, string> _lastRemoteScene = new Dictionary<ulong, string>();
        string _joinScene, _joinWho; int _joinIndex = -1; float _joinUntil; bool _joinInvited;
        string[] _joinCustom;   // a Workshop peak: title, folder, compendium, peak, page

        /// <summary>A friend left the cabin for a peak (or invited us): offer to follow, for 25 s (30 s for an invite).</summary>
        void JoinPrompt()
        {
            foreach (var rp in Remotes.Values)
            {
                var sc = rp.Shown?.Scene;
                if (string.IsNullOrEmpty(sc) || sc == "(loading)") continue;
                _lastRemoteScene.TryGetValue(rp.Id, out var last);
                if (sc == last) continue;
                if (sc == CustomPeak && (rp.PlaceScene != sc || rp.CustomPlace == null) && last != null) continue; // wait for which Workshop peak it is
                _lastRemoteScene[rp.Id] = sc;
                if (last == null || !IsHub(_scene) || IsHub(sc) || sc == "TitleScreen" || sc == _scene) continue;
                if (sc == CustomPeak)
                {
                    if (_scene != WorkshopOutpost) { Banner($"{rp.DisplayName} went to a Workshop peak: go to the Workshop Outpost to join them", 6f); continue; }
                    _joinCustom = rp.CustomPlace;
                }
                else _joinCustom = null;
                int idx = BuildIndexOf(sc);
                if (idx < 0) { Log.LogInfo($"{rp.DisplayName} went to {sc}: no build index here"); continue; }
                _joinScene = sc; _joinIndex = idx; _joinWho = rp.DisplayName; _joinUntil = Time.unscaledTime + 25f; _joinInvited = false;
                Log.LogInfo($"{rp.DisplayName} went to {sc} (#{idx}): asking to follow");
            }
            if (_joinScene == null) return;
            if (Time.unscaledTime > _joinUntil || (!IsHub(_scene) && !_joinInvited) || _inviteOpen) { _joinScene = null; return; }
            bool padOk = !MenuOnScreen && PadInput.Active;
            if (Input.GetKeyDown(KeyCode.N) || (padOk && PadInput.Cancel)) { Log.LogInfo("join declined"); _joinScene = null; return; }
            if (!(Input.GetKeyDown(KeyCode.Y) || (padOk && PadInput.Submit))) return;
            if (_joinCustom != null)
            {
                if (_scene != WorkshopOutpost && _scene != CustomPeak) Banner("Go to the Workshop Outpost first to join a Workshop peak", 5f);
                else JoinCustomPeak(_joinCustom);
                _joinScene = null;
                return;
            }
            Log.LogInfo($"following to {_joinScene}");
            GoTo(_joinScene, _joinIndex);
            _joinScene = null;
        }

        string JoinQuestion => $"{_joinWho} {(_joinInvited ? "invites you to" : "went to")} {(_joinCustom != null ? "\"" + _joinCustom[0] + "\"" : PlaceName(_joinScene))}. Join?   {(PadInput.Active ? "A - yes   B - no" : "Y - yes   N - no")}";

        /// <summary>Loads a place the way the game does: peaks with the walk-in, cabins with the walk-back.</summary>
        void GoTo(string scene, int idx)
        {
            if (idx < 0) return;
            _menuInvite.ClosePause();
            var ll = FindObjectOfType<LevelLoader>();
            if (ll == null) { SceneManager.LoadScene(idx); return; }
            if (ll.currentlyLoadingAScene) return;
            if (IsHub(scene)) ll.LoadIndexSceneFellWalkerBackwards(idx); else ll.LoadPeakScene(idx);
        }

        /// <summary>The Join button of the pause list: go to the friend's place.</summary>
        void JoinFriend(RemotePlayer rp)
        {
            var sc = rp.Shown?.Scene; if (string.IsNullOrEmpty(sc)) return;
            Log.LogInfo($"joining {rp.DisplayName} in {sc}");
            if (sc == CustomPeak)
            {
                if (rp.CustomPlace == null) { Banner("Wait a moment: the Workshop peak is not known yet", 4f); return; }
                if (_scene != WorkshopOutpost) { Banner("Go to the Workshop Outpost first to join a Workshop peak", 5f); return; }
                _menuInvite.ClosePause(); JoinCustomPeak(rp.CustomPlace); return;
            }
            GoTo(sc, BuildIndexOf(sc));
        }

        /// <summary>The Invite button of the pause list: the friend gets the join question for our place.</summary>
        void SendInvite(ulong id)
        {
            if (_net == null) return;
            var parts = new List<string> { "invite", _scene, MyPlaceTitle() };
            var custom = MyCustomPeak(); if (custom != null) parts.AddRange(custom);
            _net.Send(id, new WorldEvent { Scene = _scene, Kind = WorldKind.Note, Key = string.Join(PlaceSep, parts) }.Write(), true);
            Banner($"Invitation sent to {NameOf(id)}", 3f);
            Log.LogInfo($"invited {NameOf(id)} to {_scene}");
        }

        void ReceiveInvite(ulong from, WorldEvent ev)
        {
            var p = (ev.Key ?? "").Split(new[] { PlaceSep }, StringSplitOptions.None);   // invite | scene | title [| folder | compendium | peak | page]
            if (p.Length < 3) return;
            string scene = p[1], title = p[2];
            if (scene == _scene) return;
            _joinCustom = p.Length >= 7 ? new[] { title, p[3], p[4], p[5], p[6] } : null;
            if (scene == CustomPeak && _joinCustom == null) return;
            _joinScene = scene; _joinIndex = BuildIndexOf(scene); _joinWho = NameOf(from); _joinUntil = Time.unscaledTime + 30f; _joinInvited = true;
            Note($"{_joinWho} invites you to {title}");
            Log.LogInfo($"{_joinWho} invited us to {scene}");
        }

        // ---------------------------------------------------------------- Workshop peaks
        PublishedFileId_t _wsItem; string[] _wsJoin; float _wsSince, _wsProgress;

        /// <summary>
        /// Load the Workshop peak a friend is on, the way the Outpost's compendium does (CustomPeakJournal.HandlePageClick):
        /// the compendium folder, its name, the peak and its page, then the custom level scene in play mode.
        /// The folder is the friend's own path: a Steam Workshop item is found here by its id, a local one by its name.
        /// Not here yet: subscribe to it and wait for the download (WorkshopDownloadTick).
        /// </summary>
        void JoinCustomPeak(string[] c)
        {
            string title = c[0], folder = c[1], comp = c[2], peak = c[3];
            int.TryParse(c[4], out var page);
            var here = ResolveCustomFolder(folder);
            if (here == null)
            {
                if (ulong.TryParse(FolderLeaf(folder), out var id) && SteamManager.Initialized)
                {
                    var item = new PublishedFileId_t(id);
                    SteamUGC.SubscribeItem(item);
                    SteamUGC.DownloadItem(item, true);
                    _wsItem = item; _wsJoin = c; _wsSince = Time.unscaledTime; _wsProgress = 0f;
                    Log.LogInfo($"custom peak not here: subscribing to Workshop item {id} and downloading it");
                    return;
                }
                Banner($"You don't have \"{title}\" (it is not on the Steam Workshop): copy it from your friend, then try again", 8f);
                Log.LogInfo($"custom peak not found here: {folder}");
                return;
            }
            var clm = CustomLevelManager.control;
            if (clm == null) { Banner("Workshop peaks can't be loaded from here", 5f); return; }
            Log.LogInfo($"following to the Workshop peak '{title}' ({comp} / {peak}, page {page}) from {here}");
            clm.SetCurrentCompendium(here, comp, peak);
            clm.peakNumber = page;
            clm.LoadLevel_Play = true;
            CustomPeakJournal.ViewingCustomJournal = false;
            InGameMenu.isLoading = false;
            SceneManager.LoadScene(CustomPeak);
        }

        /// <summary>Every frame while a friend's Workshop peak downloads: progress for the bar, then load it.</summary>
        void WorkshopDownloadTick()
        {
            if (_wsJoin == null) return;
            if ((Input.GetKeyDown(KeyCode.N) || (PadInput.Cancel && !MenuOnScreen)) && !_inviteOpen) { Log.LogInfo("Workshop download: stopped waiting"); Banner("Stopped waiting (the peak keeps downloading in Steam)", 4f); _wsJoin = null; return; }
            if (_scene != WorkshopOutpost) { _wsJoin = null; return; }
            var state = (EItemState)SteamUGC.GetItemState(_wsItem);
            bool busy = (state & (EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending | EItemState.k_EItemStateNeedsUpdate)) != 0;
            if ((state & EItemState.k_EItemStateInstalled) != 0 && !busy)
            {
                var c = _wsJoin; _wsJoin = null; _wsProgress = 1f;
                Log.LogInfo($"Workshop item {_wsItem.m_PublishedFileId} downloaded in {Time.unscaledTime - _wsSince:F0} s");
                JoinCustomPeak(c);
                return;
            }
            if (SteamUGC.GetItemDownloadInfo(_wsItem, out var got, out var total) && total > 0) _wsProgress = Mathf.Clamp01((float)got / total);
            if (Time.unscaledTime - _wsSince > 600f) { Banner("The Workshop download takes too long; try again later", 6f); Log.LogInfo($"Workshop download timed out, state {state}"); _wsJoin = null; }
        }

        static string FolderLeaf(string folder) => Path.GetFileName(folder.TrimEnd('/', '\\'));

        static string ResolveCustomFolder(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) return null;
                if (Directory.Exists(folder)) return folder;
                var leaf = FolderLeaf(folder);
                if (ulong.TryParse(leaf, out var id) && SteamManager.Initialized
                    && SteamUGC.GetItemInstallInfo(new PublishedFileId_t(id), out _, out var path, 1024, out _) && Directory.Exists(path)) return path;
                foreach (var d in Directory.GetDirectories(Application.persistentDataPath, leaf, SearchOption.AllDirectories)) return d;
            }
            catch (Exception e) { Log.LogInfo("custom peak folder: " + e.Message); }
            return null;
        }

        // ---------------------------------------------------------------- scene names
        static int BuildIndexOf(string scene)
        {
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                if (Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(i)) == scene) return i;
            return -1;
        }

        /// <summary>"Peak_16_ThreeSeaStacks" -> "Three Sea Stacks".</summary>
        static string PlaceName(string scene)
        {
            switch (scene)
            {
                case "Cabin": return "Cabin";
                case "Category4_1_Cabin": return "Northern Cabin";
                case "Alps_Main": return "Alpine Cabin";
                case "TitleScreen": return "Main menu";
                case WorkshopOutpost: return "Workshop Outpost";
                case CustomPeak: return "a Workshop peak";
            }
            var n = Regex.Replace(scene ?? "", @"^(Peak_\d+_|Category\d+_\d+_|Alps_?\d*_?)", "");
            n = n.Replace("NEW", "").Replace('_', ' ');
            return Regex.Replace(n, "([a-z])([A-Z])", "$1 $2").Trim();
        }
    }
}
