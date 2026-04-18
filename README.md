# Vanguard Galaxy Echo (VGEcho)

BepInEx plugin for [Vanguard Galaxy](https://store.steampowered.com/app/3471800/)
that enhances ECHO — the in-game autopilot AI.

## Current features

- **Autopilot ETA-sync** — while the ship is warping, the green progress circle on
  the Autopilot side-tab fills at the live travel ETA instead of running its
  vanilla 12-second cycle. Circle completes exactly on drop-out.
- **Autopilot arrival-snap** — when the ship reaches its final waypoint, the
  IdleManager task cycle timer is zeroed so the next autonomous action fires
  on the following frame instead of after a 0–12 s residual wait.

Both toggles live under `[Autopilot]` in `BepInEx/config/dev.fankserver.vgecho.cfg`.

## Requirements

- Vanguard Galaxy (Steam)
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) installed into the game folder
- .NET SDK 8+ for building

## Build + deploy (WSL)

```bash
make deploy
```

This:
1. Symlinks the game's `Assembly-CSharp.dll` into `VGEcho/lib/` for compile-time references
2. Builds the plugin
3. Copies the DLL into `<game>/BepInEx/plugins/`

If your Steam install is elsewhere, set `GAME_DIR` on the command line:

```bash
make deploy GAME_DIR="/mnt/d/SteamLibrary/steamapps/common/Vanguard Galaxy"
```

## First-run verification

1. Launch the game once with BepInEx installed (creates `BepInEx/` subfolders)
2. `make deploy`
3. Launch the game, open the BepInEx console
4. Expect a load line: `[Info :Vanguard Galaxy Echo] Vanguard Galaxy Echo v0.1.0 loaded`
