# Vanguard Galaxy Echo (VGEcho)

BepInEx plugin for [Vanguard Galaxy](https://store.steampowered.com/app/3471800/)
that enhances ECHO — the in-game autopilot AI.

## Current features

- **Autopilot ETA-sync** — while the ship is warping, the green progress circle on
  the Autopilot side-tab fills at the ship's distance-based travel progress
  instead of running its vanilla 12-second cycle. Circle completes exactly on
  drop-out.
- **Autopilot arrival-snap** — when the ship reaches its final waypoint, the
  IdleManager task cycle timer is zeroed so the next autonomous action fires
  on the following frame instead of after a 0–12 s residual wait.
- **Autopilot fast-deposit** — when the autopilot is unloading cargo at a
  station (or auto-selling materials), each transfer fires on the next frame
  instead of after the vanilla ~1–2 s per-item gap. A full cargo hold drains
  in a handful of frames.
- **Autopilot fast-fetch** — mirror of fast-deposit for the reverse path.
  When the autopilot pulls from global inventory or buys ammo / warp fuel
  from a station shop, the next item fires on the following frame instead
  of after the vanilla ~1–2 s (ammo) or 12 s (warp fuel) gap.

All five toggles — master `TimingEnabled` plus the four features above —
live under `[Autopilot]` in `BepInEx/config/vgecho.cfg`.

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

## Config reference

Config file: `<game>/BepInEx/config/vgecho.cfg` (created on first launch).

| Key                          | Default | Purpose                                                                                                         |
| ---------------------------- | ------- | --------------------------------------------------------------------------------------------------------------- |
| `[Autopilot] TimingEnabled`  | `true`  | Master toggle. When `false`, all autopilot timing tweaks are skipped and the vanilla cycle runs unchanged.      |
| `[Autopilot] EtaSync`        | `true`  | While warping, drive the cycle from distance-based travel progress. Fill circle completes exactly on drop-out.  |
| `[Autopilot] ArrivalSnap`    | `true`  | On final-waypoint arrival, zero the cycle so the next autonomous action fires immediately.                      |
| `[Autopilot] FastDeposit`    | `true`  | After each autopilot cargo deposit or auto-sell, zero the cycle so successive items move on the next frame.     |
| `[Autopilot] FastFetch`      | `true`  | After each autopilot global-inventory transfer or shop buy (ammo / warp fuel), zero the cycle so the next item fires on the next frame. |

None of these patches change what the autopilot decides to do — only *when* it decides. Disable any feature independently via config; no rebuild or redeploy needed, just relaunch the game.
