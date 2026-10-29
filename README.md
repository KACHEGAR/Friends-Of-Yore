# Friends of Yore

BepInEx 5 plugin that adds online co-op to Peaks of Yore (Steam, Windows, Unity 2019.4.41 Mono): a friends-only
Steam lobby of up to 4 players who see each other climb, catch each other's hands, share ropes and the world,
and talk with proximity voice chat. Player-facing notes: `packaging/README.txt`.

## Code map (`src/FriendsOfYore`)
- `Plugin.cs` - config, Unity lifecycle, the per-frame order of work. The rest of `Plugin` is split by concern:
  `Plugin.Input.cs` (actions, marks), `Plugin.LocalPlayer.cs` (finding the body, the state we send),
  `Plugin.Hands.cs` (one player per hold, hand-to-hand grips), `Plugin.Places.cs` (where we are, joining, inviting,
  Workshop peaks), `Plugin.Network.cs` (incoming messages, notes), `Plugin.Gui.cs` (status text, messages, pause
  list, invite list), `Plugin.Preload.cs` (background loading of the NPC bodies).
- `Net/` - wire format (`Protocol.cs`, protocol version byte), Steam and UDP transports.
- `RemotePlayer`, `AvatarModel`, `ModelPicker` - friends' bodies and looks.
- `WorldSync` (obstacles, props, rocks, bivouacs, mills), `RopeSync`, `GoatSync`, `ClockSync`, `SharedClock`.
- `Voice`, `VoiceMenu` (Multiplayer Settings), `PauseList`, `MenuInvite`, `PadInput` / `PadNav` / `Binds` (gamepad),
  `CoopRules` (free solo / summit rules), `FallSounds`, `Marks`, `GameIcons`, `Ui`, `Trace`.

## Build
Put the game's `Peaks of Yore_Data/Managed` folder at `lib/Managed` and BepInEx 5's `BepInEx/core` at `lib/BepInEx/core`
(or pass `-p:GameManaged=...` and `-p:BepInExCore=...`), then:
`dotnet build src/FriendsOfYore -c Release`

## Tests
`dotnet run --project tests/ProtocolTests -c Release` (serialization, duplicate/old packet drop, interpolation,
fuzz with 15% loss, 30-250 ms latency and reordering).

Game code is not included. Credits: game by Anders Lauridsen / Pink Robot (publisher Maple Whispering);
BepInEx; Steamworks.NET.
