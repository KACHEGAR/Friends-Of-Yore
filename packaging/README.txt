Friends of Yore 0.29.3 (test build)
==================================

Online co-op for Peaks of Yore over Steam. Everyone needs their own copy of the game on Steam.
No port forwarding: traffic goes through Steam's relay servers.

INSTALL / UPDATE
1. Steam -> Library -> Peaks of Yore -> right click -> Manage -> Browse local files.
2. Copy EVERYTHING from this archive into that folder and replace files
   (winhttp.dll and the BepInEx folder must sit next to "Peaks of Yore.exe").
   If you still have BepInEx\plugins\PoyCoop or BepInEx\config\poycoop.cfg from an old version, delete them.
3. Start the game from Steam. "Friends of Yore 0.29.3" appears in the top-right corner.
   Both players need the same version.

PLAYING TOGETHER
- Host: F5, or ESC -> "Invite" under the player list on the right, creates a lobby and opens the Steam invite dialog.
  Shift+F5 opens the mod's own friends list instead (click or 1-9) - use it if the Steam overlay doesn't show up.
- Friend: accept the Steam invite (or right click the host in Steam -> Join Game).
- Character models load in the background once you enter a cabin (Northern or Alpine), a few seconds.
  Nothing is loaded in the main menu.
  If that ever causes trouble, set PreloadModels = false in BepInEx\config\friendsofyore.cfg.
- You see each other in the cabins too, not only on the peaks.
- ESC -> Multiplayer Settings -> Look: pick how YOU look to others from a list with previews, a goat (the Alpine cabin one; jumps onto every hold you grab) and gulls (one on each hand: perched while holding, flying while free).
  Middle mouse button: put a mark where you look; everyone sees it (name + distance) for 20 seconds.
  F7: hide the text.   F6: leave.
- Proximity voice chat (Steam voice, your default microphone): friends hear you from your character,
  loud up to 2 m, fading to silence at 50 m; not heard from another place. Open mic by default;
  ESC -> "Multiplayer Settings" (tabs: Voice Chat, Look and Controls; the page scales with the screen).
  Controls: a keyboard key and a gamepad button for every mod action (mark, push to talk - B by default, mod text).
  "Report a bug" at the bottom of the page: coming soon. Gamepad buttons the game doesn't use are given to them the first time a pad is connected.
- Gamepad: every mod menu works with a pad - stick / D-pad move, A presses, left/right change a value, LB/RB switch
  tabs, B goes back. In the pause menu push right to reach the player list. Join prompts: A - yes, B - no.
  Voice Chat tab: on/off, open mic or push-to-talk (and its key), hearing distance, friends' volume,
  mountain echo for far friends, volume up to 300% for everyone and per friend, and a microphone check.
- The Workshop Outpost works like the cabins; when a friend starts a Workshop peak you can follow them there with Y (a Steam Workshop peak you lack is subscribed to and downloaded first, with a progress bar); the service bell is heard by everyone.
- ESC shows the players on the right: picture, name, mode icons, voice volume, height on the peak or the place,
  Join (go to their place) and Invite (they get a Y/N prompt); under it Invite (hidden when 4 players) and Leave lobby.
- Icons by the name: rope = safety harness, skull = "You fall, you die", climber = free solo, and the Alps idols in use.
  A death in those modes is told to everyone; joins, leaves, peaks started/left and summits are shown bottom-left.
- The wandering goats of the Alps DLC move by a shared seed and the computer clock: the same for everyone, also for a late arrival.
- Mouse side buttons can be bound in Controls (e.g. marks).
- Free solo: no friends' ropes or hands. Using a friend's rope or hand makes the summit a normal one (no free solo stamp).
- Self-turning mills follow the computer clock: they keep turning while paused or minimised and stay in step.
- The clocks in the cabins show your computer's time; a door opens and shuts when a friend joins.
- When a friend goes from the cabin to a peak, you are asked to follow: Y - yes, N - no.
- Above your friend: their name and what they are doing (coffee, chalk, monocular, piton, watch, rope, bivouac).
- Aldr Grotto: a friend carrying the oil lamp lights up the cave around them.
- Hands: grab a friend's hand only while they reach out with it, hand to hand; never a hand holding an ice axe or a stone.
  Both players hear the catch. A hand-to-hand grip lasts 20 seconds (the small-hold ring shows it); a chalked hand has no limit.
  Whoever holds the wall is the anchor: a friend can hang on them but can't move them.
- Holds: one player per stone. A friend whose hand comes right next to yours on your stone takes it; you let go.
- Catching a friend's hand sounds lower than grabbing a stone.
- Ice axe position in a friend's hand can be tuned in BepInEx\config\friendsofyore.cfg (AxeGripSlide, AxeGripRight).
- Ropes: everyone's ropes are shared on the peak, so each player carries ropes / players (at least 1).
  Only one player per rope.
  Anyone can pull out anyone's piton: the rope goes back into its owner's backpack, and whoever hangs on it falls.
- Shared world: crumbling holds (including how worn they are), loose bricks, shrubs, seagulls (their flight too),
  brittle ice, snow, falling rocks/icicles, bending branches and trees, vines, mill wheels, beams, swinging hanging bodies, bivouacs.
  You can rest in a friend's bivouac. An aggressive gull attacks for at most 10 seconds.
- A fall that knocks a player out, or a fall into the sea, is heard by everyone nearby.
- Swing your arms 10 times in a row in mid-air: a gull cries (once per jump).

NEEDS
- Steam running. (Steam overlay is not required any more.)

IF SOMETHING GOES WRONG
Send this file from the game folder: BepInEx\LogOutput.log

UNINSTALL
Delete winhttp.dll from the game folder (the mod stops loading), or also doorstop_config.ini,
.doorstop_version and the BepInEx folder.

Not done yet: the player you hang on doesn't feel your weight; save progress is not shared.

Contents: BepInEx 5.4.23.2 (LGPL-2.1, https://github.com/BepInEx/BepInEx) and the Friends of Yore plugin.
The mod contains no game files: character models are taken from your own installed game at runtime.
