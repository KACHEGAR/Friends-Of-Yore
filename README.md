# Friends of Yore

**Online co-op for [Peaks of Yore](https://store.steampowered.com/app/2236070/Peaks_of_Yore/).** Climb the same
mountain with up to 3 friends: see each other climb, grab a friend's hand to save them from a fall, share ropes,
talk with proximity voice chat and watch the world react to everyone's moves.

A [BepInEx 5](https://github.com/BepInEx/BepInEx) mod for the Steam version (Windows). Unofficial: not made or
endorsed by the developers of Peaks of Yore.

> **Status: test build.** The mod is played and tested by a small group of friends; expect rough edges and report
> what you find.

---

## Features

**Playing together**
- Friends-only Steam lobby for up to 4 players. No port forwarding: traffic goes through Steam's relay servers.
- Invite from the pause menu or with F5; friends join from the Steam invite or "Join Game".
- You see each other everywhere: on peaks, in the cabins and in the Workshop Outpost.
- When a friend heads to a peak you get a prompt to follow them. **Join** and **Invite** buttons next to each player
  in the pause menu take you to a friend or bring them to you - Steam Workshop peaks included (a peak you don't have
  is subscribed to and downloaded first).
- Player list in the pause menu: avatar, name, mode icons (safety harness, "You fall, you die", free solo, Alps
  idols in use), height on the peak or location, ping, per-player voice volume.
- Messages for joins, leaves, peaks started and summits; a death in a hard mode is told to everyone.

**Climbing together**
- **Grab a friend's hand** while they reach out with it, and hang from it. Both players hear the catch. A hand-to-hand
  grip lasts 20 seconds (the game's small-hold ring shows it); a chalked hand has no limit. Whoever holds the wall is
  the anchor: a friend can hang on them but can't pull them off.
- **One player per hold:** a friend grabbing right next to your hand on your stone takes it from you.
- **Shared ropes:** everyone's ropes are on the wall for everyone; ropes are split between players; anyone can pull
  out anyone's piton (and whoever hangs on that rope falls).
- **Marks:** middle mouse button puts a mark where you look; everyone sees it for 20 seconds.
- **Free solo stays honest:** a free soloist can't use friends' ropes or hands, and a climb that used them is stamped
  as a normal summit.

**A shared world**
- Crumbling holds (including how worn they are), loose bricks, shrubs, brittle ice, snow, falling rocks and icicles,
  bending branches and trees, vines, beams, swinging bodies and bivouacs (you can rest in a friend's).
- Seagulls and their flight; aggressive gulls give up after 10 seconds.
- Falls, splashes and knock-outs are heard by players nearby; a door opens when a friend joins.

**Looks and voice**
- Pick how others see you in **ESC → Multiplayer Settings → Look**, from live previews of the game's characters.
- Proximity voice chat over Steam: loud up to 2 m, silent at 50 m, a mountain echo for far friends. Open mic or
  push-to-talk.

---

## Installation

Download from [**Releases**](../../releases/latest) - one of two archives:

| Archive | For whom |
|---|---|
| `FriendsOfYore-x.y.z.zip` | **No other mods installed.** Everything in one: the mod and BepInEx 5.4.23.2. |
| `FriendsOfYore-x.y.z-plugin-only.zip` | **BepInEx 5 already installed** (for other mods). Only the mod itself. |

1. In Steam: **Library → Peaks of Yore → right click → Manage → Browse local files**.
2. Extract **everything** from the archive into that folder, replacing files. With the full archive, `winhttp.dll`
   and the `BepInEx` folder must sit next to `Peaks of Yore.exe`; with the plugin-only one, the mod ends up in
   `BepInEx\plugins\FriendsOfYore\`.
3. Start the game from Steam. "Friends of Yore x.y.z" appears in the top-right corner.

Everyone needs their own copy of the game on Steam and **the same mod version**.

**Updating:** extract the new archive over the old one (if you have other mods, take the plugin-only archive so your
BepInEx stays as it is). **Uninstalling:** delete the `BepInEx\plugins\FriendsOfYore` folder; to remove BepInEx too
(only if no other mods use it), delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx`
folder.

## Controls

| Action | Default |
|---|---|
| Create a lobby / invite friends | **F5** (Shift+F5: the mod's own friends list) or ESC → Invite |
| Leave the lobby | **F6** or ESC → Leave lobby |
| Show / hide the mod text | **F7** |
| Place a mark | **Middle mouse button** |
| Push to talk (when enabled) | **B** |
| Follow a friend / accept an invite | **Y** / **N** (gamepad: A / B) |

Mark, push-to-talk and the mod text can be rebound - keyboard, mouse side buttons or gamepad - in
**ESC → Multiplayer Settings → Controls**.

## Settings

Most settings are in **ESC → Multiplayer Settings** (tabs Voice Chat, Look, Controls). Everything else is in
`BepInEx\config\friendsofyore.cfg`, created after the first start, for example:

- `PreloadModels` - load the characters' bodies in the background in the cabins (turn off if it causes trouble);
- `AxeGripSlide`, `AxeGripRight` - how an ice axe sits in a friend's hand.

## Known limitations

- The player you hang on doesn't feel your weight.
- Save progress (peaks done, items) is not shared: everyone plays their own save.
- A Workshop peak that isn't on the Steam Workshop has to be copied by hand.

## Reporting bugs

Open an [issue](../../issues) and attach `BepInEx\LogOutput.log` from the game folder. Say what happened, where, and
which player was doing what - logs from both players help most.

---

## Building from source

Requirements: the [.NET SDK](https://dotnet.microsoft.com/download) (6 or newer), your own copy of the game and
BepInEx 5.

1. Copy (or link) the game's `Peaks of Yore_Data\Managed` folder to `lib/Managed`, and BepInEx 5's `BepInEx\core` to
   `lib/BepInEx/core` - or pass `-p:GameManaged=...` and `-p:BepInExCore=...` to the build.
2. Build:
   ```
   dotnet build src/FriendsOfYore -c Release
   ```
   The plugin is `src/FriendsOfYore/bin/Release/net472/FriendsOfYore.dll`; it goes to
   `BepInEx\plugins\FriendsOfYore\` in the game folder.
3. Network protocol tests (serialization, packet loss, latency, reordering):
   ```
   dotnet run --project tests/ProtocolTests -c Release
   ```

No game files are in this repository; character models and sounds are taken from your installed game at runtime.

### Project layout

| Path | What it is |
|---|---|
| `src/FriendsOfYore/Plugin*.cs` | The plugin: config and frame loop (`Plugin.cs`), input, local player, hands, places and joining, network, UI, model preloading |
| `src/FriendsOfYore/Net/` | Wire protocol, Steam and UDP transports |
| `src/FriendsOfYore/RemotePlayer.cs`, `AvatarModel.cs`, `ModelPicker.cs` | Friends' bodies, animation and the Look tab |
| `src/FriendsOfYore/WorldSync.cs`, `RopeSync.cs`, `GoatSync.cs`, `ClockSync.cs` | The shared world, ropes, goats and clocks |
| `src/FriendsOfYore/Voice.cs`, `VoiceMenu.cs`, `PauseList.cs` | Voice chat, Multiplayer Settings, the pause player list |
| `src/FriendsOfYore/PadInput.cs`, `PadNav.cs`, `Binds.cs` | Gamepad support and rebindable actions |
| `packaging/` | The player readme and default config shipped in the release archive |
| `tests/ProtocolTests/` | Protocol tests |
| `tools/analyze_trace.py` | Developer tool: reads the position trace (`Trace = true` in the config) |

## License

The mod's code is released under the [MIT License](LICENSE). The full release archive also contains an unmodified
[BepInEx](https://github.com/BepInEx/BepInEx) 5.4.23.2, which is under its own license, the LGPL-2.1 (included in the
archive as `LICENSE-BepInEx.txt`). Peaks of Yore and its assets belong to their owners.

## Credits

- **Friends of Yore** by **KACHEGAR** and **Vorobushek**.
- **Peaks of Yore** by Anders Lauridsen / Pink Robot, published by Maple Whispering.
- [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1), [Harmony](https://github.com/pardeike/Harmony),
  [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET).
