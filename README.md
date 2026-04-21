# Vanguard Galaxy Echo (VGEcho)

A BepInEx plugin for [Vanguard Galaxy](https://store.steampowered.com/app/3471800/) that enhances ECHO — the in-game autopilot AI. Makes the autopilot snappy instead of waiting up to 12 s between actions.

- **ETA-sync** — while the ship is warping, the Autopilot side-tab's green progress circle tracks live distance-based travel progress instead of the vanilla 12 s loop. Completes exactly on drop-out.
- **Arrival-snap** — on reaching the final waypoint, the next autonomous action fires on the following frame instead of after a 0–12 s residual wait.
- **Fast-deposit** — unloading cargo at a station (or auto-selling materials) fires each transfer on the next frame instead of after the vanilla ~1–2 s per-item gap. A full cargo hold drains in a handful of frames.
- **Fast-fetch** — mirror of fast-deposit for the reverse path. Pulling from global inventory or buying ammo / warp fuel fires the next item on the following frame instead of after ~1–2 s (ammo) or 12 s (warp fuel).
- **Refinery routing** *(opt-in)* — when the autopilot would fly home with ore in cargo and home has no refinery, divert to the nearest friendly station with one. Saves the round-trip when mining far from base.
- **Auto-refine on arrival** *(opt-in)* — when the autopilot docks at a station with a refinery, flip that refinery's Auto-Refine toggle on so pending ore refines passively while you're there.

The first four change *when* the autopilot acts, never *what* it decides. The last two opt-in toggles do change routing and station behavior; both default off so existing installs stay on vanilla decisions.

## Install

1. **Install BepInEx 5.x** — grab `BepInEx_win_x64_5.4.x.zip` from the [BepInEx releases](https://github.com/BepInEx/BepInEx/releases) and unzip it into your Vanguard Galaxy install folder (next to `VanguardGalaxy.exe`).
2. **Launch the game once** so BepInEx creates its `BepInEx/plugins/` and `BepInEx/config/` subfolders, then close the game.
3. **Download the VGEcho release** zip from [Releases](https://github.com/fank/vanguard-galaxy-echo/releases) (or Nexus Mods, once published).
4. **Unzip** into `BepInEx/plugins/`. The zip contains a single `VGEcho/` folder that drops in cleanly:
   ```
   VanguardGalaxy/BepInEx/plugins/
     VGEcho/
       VGEcho.dll
       README.md
   ```
5. **Launch the game.** Open the BepInEx console — you should see a load line ending with the number of Harmony patches applied, e.g.:
   ```
   [Info :Vanguard Galaxy Echo] Vanguard Galaxy Echo v0.1.0 loaded (5 patches)
   ```

## Uninstall

Delete the `BepInEx/plugins/VGEcho/` folder. Optionally also delete `BepInEx/config/vgecho.cfg` to reset saved settings.

## Config

BepInEx writes the config to `BepInEx/config/vgecho.cfg` on first launch. All toggles live under `[Autopilot]`:

| Key                | Default | Purpose                                                                                                                                            |
| ------------------ | ------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `TimingEnabled`    | `true`  | Master toggle for the four timing tweaks below. When `false`, all timing tweaks are skipped and the vanilla cycle runs unchanged.                  |
| `EtaSync`          | `true`  | While warping, drive the cycle from distance-based travel progress. Fill circle completes exactly on drop-out.                                     |
| `ArrivalSnap`      | `true`  | On final-waypoint arrival, zero the cycle so the next autonomous action fires immediately.                                                         |
| `FastDeposit`      | `true`  | After each autopilot cargo deposit or auto-sell, zero the cycle so successive items move on the next frame.                                        |
| `FastFetch`        | `true`  | After each autopilot global-inventory transfer or shop buy (ammo / warp fuel), zero the cycle so the next item fires on the next frame.            |
| `RefineryRoute`    | `false` | When cargo contains ore and the autopilot would fly back to your home station (and home has no refinery), divert to the nearest station that does. |
| `RefineryMaxHops`  | `2`     | Maximum jump-gate hops to search for a refinery station. Matches the vanilla mission-station search range. Accepts `1`–`10`.                       |
| `AutoRefine`       | `false` | On autopilot arrival at a station with a refinery, enable that refinery's Auto-Refine toggle. Setting sticks per-station.                          |

Disable any feature independently — no rebuild needed, just relaunch the game. The four timing tweaks are all-on by default; the two routing tweaks are opt-in because they change what ECHO decides, not just when.

## Troubleshooting

**No load line in the BepInEx console**
- Check that `BepInEx/plugins/VGEcho/VGEcho.dll` exists.
- Enable the console: `BepInEx/config/BepInEx.cfg` → `[Logging.Console]` → `Enabled = true`.

**Plugin loads but nothing happens during warp**
- Autopilot has to be engaged (default hotkey `T`). Every patch is gated on `GamePlayer.current.autoPlay`; when autopilot is off, the vanilla cycle runs unchanged.
- Confirm `TimingEnabled = true` and the individual feature toggle is `true` in `vgecho.cfg`.

**`TypeInitializationException` on load**
- Likely a game update renamed an internal field or method that VGEcho hooks. See "Known limitations" below. A rebuild against the new game version is needed.

## Known limitations

- **Game version drift** — VGEcho hooks private method names (`IdleManager.SetQuickerUpdateTimer`, `IdleManager.FetchItem`, `IdleManager.Update`, `IdleManager.IdleTravelToSpaceStation`, `TravelManager.TravelToNextWaypoint`), a private field literal (`IdleManager.idleTravelTarget`), and compiler-generated backing-field literals (`<updateTimer>k__BackingField`, `<updateTimerBase>k__BackingField`). A patch that renames any of these breaks the plugin at load time. File an issue with the BepInEx console output and wait for a new VGEcho build.
- **Full-stack transfer not implemented** — fast-deposit / fast-fetch still move one unit per cycle (20 for ammo, 20 for currency, 1 for everything else). They zero the *delay between* cycles, not the per-cycle quantity. Moving a full stack per cycle would require an IL transpiler on `IdleManager.FindActivity` and is tracked as future work.

## Building from source

Requires .NET SDK 8+. The Makefile targets WSL + a Steam install at `/mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy`; override `GAME_DIR` if your install is elsewhere.

```bash
make deploy
# or with a custom install:
make deploy GAME_DIR="/mnt/d/SteamLibrary/steamapps/common/Vanguard Galaxy"
```

This symlinks the game's `Assembly-CSharp.dll` into `VGEcho/lib/` for compile-time references, builds the plugin, and copies the DLL into `<game>/BepInEx/plugins/`.

## Releasing (for maintainers)

Creating a GitHub Release auto-builds and uploads the zip via `.github/workflows/release.yml`. CI compiles `VGEcho.dll` against **publicized stubs** committed at `VGEcho/lib/`:

- `Assembly-CSharp.dll` — the game's code
- `UnityEngine.UI.dll` — uGUI widgets (Toggle, Button) needed by the UI patches
- `Unity.TextMeshPro.dll` — TMP_Text, used for UI labels

Each stub is method signatures only — no IL bodies. The real runtime assemblies take over in-game via Mono's late binding.

```bash
# One-time per game update — regenerate the publicized stubs from your
# current install and commit them. BepInEx-standard tool, MIT-licensed:
dotnet tool install -g BepInEx.AssemblyPublicizer.Cli
for dll in Assembly-CSharp.dll UnityEngine.UI.dll Unity.TextMeshPro.dll; do
  assembly-publicizer "$GAME_DIR/VanguardGalaxy_Data/Managed/$dll" -o "VGEcho/lib/$dll"
done
git add VGEcho/lib/*.dll && git commit -m "chore: refresh publicized stubs for game vX.Y"

# Tag + release
gh release create v0.1.0 --title "v0.1.0" --notes "Initial release."
```

To re-run on a failed release without recreating it: `gh workflow run release.yml -f tag=v0.1.0`.

## Credits

- **Vanguard Galaxy** by [Bat Roost Games](https://store.steampowered.com/developer/BatRoostGames/) — the game being modded
- **BepInEx 5** — mod loader
- **HarmonyX** — runtime method patching

## License

MIT. See [LICENSE](LICENSE).
