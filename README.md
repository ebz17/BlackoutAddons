# Blackout Addons

Unofficial add-on for **Blackout** (Labs blackout event) by Vultify, for SPT 4.1.x.

- `BlackoutPowerRestore/` – BepInEx client plugin (`com.blackoutaddons.powerrestore`)
- `BlackoutBlackDivision/` – SPT server mod (`com.blackoutaddons.blackdivision`)
- `audio-tools/` – Python scripts used to make the mod's own sound effects and PA lines

## Building

Client (net472): set `Managed` (or the `SPT_MANAGED` environment variable) to a folder containing the game's
`EscapeFromTarkov_Data/Managed` DLLs plus `BepInEx.dll`, `0Harmony.dll` and `DrakiaXYZ-BigBrain.dll`
(the csproj expects BigBrain in `libs/`). Then `dotnet build -c Release`.

Server (net10.0): put Blackout's server DLL in `lib/BlackoutServer.dll`, then `dotnet build -c Release`.

## How this was made

Designed, directed and play-tested by the mod's author; the code was written with the help of an AI
assistant (Claude, by Anthropic).

## License

MIT (see LICENSE). Not affiliated with Battlestate Games, SPT, Vultify, WTT or the authors of SAIN/BigBrain/MoreBotsAPI.
