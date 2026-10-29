using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FriendsOfYore
{
    /// <summary>Background loading of the NPC bodies (models) while in a cabin, so every look is available.</summary>
    public partial class Plugin
    {
        ConfigEntry<bool> _cfgPreload;
        readonly HashSet<string> _preloadedScenes = new HashSet<string>();
        readonly Dictionary<string, int> _preloadTries = new Dictionary<string, int>();
        string _preloading; bool _preloadBusy; int _sceneSerial, _preloadTriedSerial = -1;
        static readonly (string scene, string model)[] PreloadSources = { ("Cabin", "NPC_PARCELFATHER"), ("Alps_Main", AvatarModel.Goat), ("Alps2_3_Dunderhorn", "GoatClips"), ("Peak_3_GrayGullyNEW", "NPC_Pearse1"), ("Peak_3_GrayGullyNEW", AvatarModel.Gull), ("Peak_16_ThreeSeaStacks", AvatarModel.Gull) };

        System.Collections.IEnumerator Preload()
        {
            _preloadBusy = true;
            try
            {
                yield return new WaitForSecondsRealtime(1f);
                foreach (var (name, model) in PreloadSources)
                {
                    if (AvatarModel.Ready(model) || _preloadedScenes.Contains(name)) continue; // each scene is loaded once per session
                    if (!IsHub(_scene)) yield break; // went climbing: try again in the next cabin
                    if (SceneManager.GetActiveScene().name == name) { AvatarModel.CaptureTemplates(); continue; }
                    // never load while a menu is open: the loading scene grabs the cursor
                    while (MenuOnScreen) { yield return null; if (!IsHub(_scene)) yield break; }
                    _preloading = name; GameAccess.Preloading = name; _preloadedScenes.Add(name);
                    // the loaded scene's objects wake up for a moment: keep the cursor and the UI input of the scene we are in
                    var lockState = Cursor.lockState; bool cursorVisible = Cursor.visible;
                    var eventSystem = UnityEngine.EventSystems.EventSystem.current;
                    var t0 = Time.realtimeSinceStartup;
                    AsyncOperation op = null;
                    try { op = SceneManager.LoadSceneAsync(name, LoadSceneMode.Additive); } catch (Exception e) { Log.LogWarning($"preload {name}: {e.Message}"); }
                    if (op == null) { _preloading = null; GameAccess.Preloading = null; continue; }
                    while (!op.isDone) yield return null;
                    yield return null;
                    AvatarModel.CaptureTemplates();
                    var sc = SceneManager.GetSceneByName(name);
                    _preloading = null; GameAccess.Preloading = null;
                    if (sc.IsValid() && sc.isLoaded && SceneManager.GetActiveScene() != sc) { var un = SceneManager.UnloadSceneAsync(sc); while (un != null && !un.isDone) yield return null; }
                    if (eventSystem != null) UnityEngine.EventSystems.EventSystem.current = eventSystem;
                    // if a menu came up meanwhile, the cursor must be free (0.16: it stayed locked in the centre)
                    if (MenuOnScreen) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                    else { Cursor.lockState = lockState; Cursor.visible = cursorVisible; }
                    Log.LogInfo($"preloaded models from {name} in {_scene} in {Time.realtimeSinceStartup - t0:F1} s; available: {string.Join(", ", AvatarModel.Available.ToArray())}");
                    // not found there this time (scene not ready, DLC missing...): allow up to 3 tries per session
                    if (!AvatarModel.Ready(model)) { _preloadTries.TryGetValue(name, out var n); _preloadTries[name] = n + 1; if (n + 1 < 3) _preloadedScenes.Remove(name); }
                    if (_picker.Open) _picker.Show(); // the Look tab gets the new looks right away
                }
            }
            finally { _preloadBusy = false; _preloading = null; GameAccess.Preloading = null; }
        }
    }
}
