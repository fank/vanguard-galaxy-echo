# Autopilot ETA-Sync + Arrival Snap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the autopilot (IdleManager) task cycle complete exactly when space travel ends, eliminating the 0–12s idle wait after every arrival, and repurpose the green progress circle as a live travel-ETA indicator.

**Architecture:** Two Harmony patches in the existing `VGEcho/Patches/AutopilotTimingPatches.cs` file. (1) **ETA-sync**: a Postfix on `IdleManager.Update` overwrites `updateTimer`/`updateTimerBase` with live `remainingDistance / travelSpeed` while `TravelManager.Instance.isWarping`. (2) **Arrival-snap**: a Postfix on `TravelManager.TravelToNextWaypoint` zeroes `updateTimer` when the final waypoint is reached (covers jump-gate transitions where ETA math is unavailable). Both patches are gated by the BepInEx config entries already declared in `Plugin.cs` and only engage while `GamePlayer.current.autoPlay` is true. Private setters on `IdleManager` auto-properties are reached via `AccessTools.FieldRefAccess` against the compiler-generated backing fields (already declared in the scaffold).

**Tech Stack:** C# netstandard2.1, BepInEx 5.x, HarmonyX 2.10, Unity 6000.2.6 (Mono backend). No test framework — verification is in-game against the BepInEx console and the autopilot tab's green/orange fill circle (`SideTabAutopilot.timerFill`).

**Verification model:** The VGEcho project has no automated tests. Each task ends with (a) a successful `make deploy`, and (b) a documented in-game acceptance check that the plan spells out step-by-step. Debug log lines (`Plugin.Log.LogDebug(...)` / `LogInfo`) are emitted at each hook point so the user can confirm the patch fired by watching the BepInEx console.

**Prerequisite state** (already set up by the scaffold commit, verify before Task 1):

- `VGEcho/Plugin.cs` exists and declares `CfgAutopilotTiming`, `CfgAutopilotEtaSync`, `CfgAutopilotArrivalSnap`.
- `VGEcho/Patches/AutopilotTimingPatches.cs` exists with an empty `[HarmonyPatch] internal static class AutopilotTimingPatches` containing only the `UpdateTimerRef` and `UpdateTimerBaseRef` field-ref declarations.
- `make build` completes with 0 errors, 0 warnings.
- The Harmony patch is registered in `Plugin.Awake` via `_harmony.PatchAll(typeof(Patches.AutopilotTimingPatches));`.

---

## Task 1: Arrival-snap patch — zero the cycle timer on final waypoint

**Goal of this task:** When space travel ends at the final destination, immediately force the IdleManager's `updateTimer` to zero so `FindActivity()` fires on the next Update tick instead of waiting up to 12 seconds. This alone removes the "ship arrives, then waits 0–12s doing nothing" delay.

**Files:**
- Modify: `/home/fank/repo/vanguard-galaxy-echo/VGEcho/Patches/AutopilotTimingPatches.cs`

- [ ] **Step 1: Add the TravelToNextWaypoint postfix**

In `/home/fank/repo/vanguard-galaxy-echo/VGEcho/Patches/AutopilotTimingPatches.cs`, inside the `AutopilotTimingPatches` class after the two `FieldRef` declarations, add:

```csharp
    /// <summary>
    /// Postfix on <see cref="TravelManager.TravelToNextWaypoint"/>. The game
    /// invokes this at the end of every leg of a journey: if more waypoints
    /// remain, it starts the next leg; if the list is empty, travel ended.
    /// In the empty-list case, and only when the player is on autopilot, we
    /// zero <c>updateTimer</c> so the very next <see cref="IdleManager.Update"/>
    /// tick calls <c>FindActivity</c> — eliminating the residual 0–12s wait
    /// between drop-out and the next autonomous action.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TravelManager), nameof(TravelManager.TravelToNextWaypoint))]
    private static void TravelToNextWaypoint_Postfix()
    {
        if (!Plugin.Instance.CfgAutopilotTiming.Value) return;
        if (!Plugin.Instance.CfgAutopilotArrivalSnap.Value) return;

        var player = GamePlayer.current;
        if (player == null || !player.autoPlay) return;

        // The final-leg branch of TravelToNextWaypoint runs when waypoints is
        // empty and sets travelCoroutine = null. Guard on both so we don't
        // fire mid-journey between legs of a multi-jump trip.
        if (player.waypoints.Count != 0) return;

        var idle = Singleton<IdleManager>.Instance;
        if (idle == null) return;

        // TravelActive() also consults usingJumpgate. If a jump-gate coroutine
        // is still in flight (it calls TravelToNextWaypoint as its last line),
        // wait for it to finalize rather than firing early. In practice
        // TravelActive is already false by the time the postfix runs because
        // the orig method set travelCoroutine = null and usingJumpgate was
        // cleared before this invocation — but belt-and-braces.
        if (Singleton<TravelManager>.Instance.TravelActive()) return;

        Plugin.Log.LogDebug("[autopilot-timing] arrival-snap: zeroing updateTimer");
        UpdateTimerRef(idle) = 0f;
    }
```

- [ ] **Step 2: Build and verify compilation**

Run from `/home/fank/repo/vanguard-galaxy-echo/`:

```bash
make build
```

Expected: build succeeds with 0 errors, 0 warnings. `VGEcho.dll` appears at `VGEcho/bin/Debug/netstandard2.1/VGEcho.dll`.

- [ ] **Step 3: Deploy**

```bash
make deploy
```

Expected: copies `VGEcho.dll` and `VGEcho.pdb` to the game's `BepInEx/plugins/` directory.

- [ ] **Step 4: In-game verification (user must run this manually)**

1. Enable debug logging: edit `<game>/BepInEx/config/BepInEx.cfg` → `[Logging.Console]` → set `LogLevels` to include `Debug` (or `All`).
2. Launch the game. Load a save where autopilot is unlocked.
3. Open the BepInEx console.
4. Verify the plugin loaded: expect a line like `[Info :Vanguard Galaxy Echo] Vanguard Galaxy Echo v0.1.0 loaded (1 patches)`.
5. Press **T** to enable autopilot. Watch the side-panel "Autopilot" tab — the fill circle should turn green and start ticking.
6. Wait for the autopilot to pick a destination and start warping.
7. When the ship drops out of warp at the destination POI, expect one console line:

    ```
    [Debug  :Vanguard Galaxy Echo] [autopilot-timing] arrival-snap: zeroing updateTimer
    ```

8. Visually: in the vanilla behavior, after drop-out the green circle would keep running up to ~12s before doing anything. With the patch, the circle should snap to empty and the next autopilot activity message (`@IdleCalculating` → `@IdleMining` / `@IdleDockWithSS` / etc.) should appear within one or two frames.
9. Control test: edit `BepInEx/config/dev.fankserver.vgecho.cfg`, set `ArrivalSnap = false`, relaunch. The log line should NOT appear and the 0–12s vanilla wait should return. Re-enable after confirming.

- [ ] **Step 5: Commit**

```bash
git add VGEcho/Patches/AutopilotTimingPatches.cs
git commit -m "feat: autopilot arrival-snap — zero IdleManager updateTimer on final waypoint

Postfix on TravelManager.TravelToNextWaypoint: when the player is on
autopilot and the waypoint list is empty, force updateTimer = 0f so
the next IdleManager.Update tick calls FindActivity immediately.
Removes the 0–12s idle wait between drop-out and the next autonomous
action. Also covers jump-gate transitions where ETA math is unavailable.

Gated by [Autopilot] ArrivalSnap config entry."
```

---

## Task 2: ETA-sync patch — align the cycle timer with live travel ETA

**Goal of this task:** While the ship is warping between POIs in-system, continuously overwrite `updateTimer`/`updateTimerBase` so the green progress circle visibly fills in lockstep with arrival. Re-purposes ECHO's timer as a travel-ETA indicator and eliminates the no-op "cycle completes, calls FindActivity, bails because TravelActive, resets to 12s" spin that happens during long trips.

**Files:**
- Modify: `/home/fank/repo/vanguard-galaxy-echo/VGEcho/Patches/AutopilotTimingPatches.cs`

- [ ] **Step 1: Add the pure ETA-calculation helper**

At the bottom of the `AutopilotTimingPatches` class (after the arrival-snap patch from Task 1), add:

```csharp
    /// <summary>
    /// Compute estimated seconds-to-arrival from a remaining-distance and a
    /// current speed. Floors speed to a small epsilon so we never divide by
    /// zero or return a negative ETA when the ship has stopped momentarily
    /// (e.g. waiting on scene load).
    /// </summary>
    internal static float ComputeEtaSeconds(float remainingDistance, float travelSpeed)
    {
        const float minSpeed = 0.1f;
        float speed = Mathf.Max(travelSpeed, minSpeed);
        return Mathf.Max(0f, remainingDistance) / speed;
    }
```

- [ ] **Step 2: Add state tracking for the warping→non-warping transition**

Still inside the class (place these near the top, below the existing `UpdateTimerBaseRef` declaration), add:

```csharp
    // Tracks whether our previous Update tick saw isWarping=true. Used so we
    // can capture the initial updateTimerBase at warp start and allow the
    // vanilla IdleManager cycle to resume naturally after warp ends.
    private static bool _wasSyncing;

    // Floor for updateTimerBase while ETA-syncing. If initial ETA is smaller
    // (e.g. very short hop), we clamp up so the fill circle always shows some
    // travel progress rather than a flash.
    private const float MinEtaBase = 3f;
```

- [ ] **Step 3: Add the IdleManager.Update postfix**

Still inside the class (place after the arrival-snap patch, before the `ComputeEtaSeconds` helper), add:

```csharp
    /// <summary>
    /// Postfix on <see cref="IdleManager.Update"/>. Runs every frame. When the
    /// player is on autopilot and <see cref="TravelManager.isWarping"/> is
    /// true, overwrite <c>updateTimer</c> with the live ETA so the progress
    /// circle becomes a travel-ETA indicator. When the game re-sets the timer
    /// via <c>FindActivity</c>'s no-op-during-travel branch, our postfix
    /// overwrites the 12s default back to the live ETA immediately.
    ///
    /// <c>updateTimerBase</c> is captured once at warp start and held steady,
    /// so <c>SideTabAutopilot</c>'s <c>fillAmount = 1 - updateTimer/base</c>
    /// formula produces a smooth 0→1 fill over the trip.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(IdleManager), "Update")]
    private static void IdleManager_Update_Postfix(IdleManager __instance)
    {
        if (!Plugin.Instance.CfgAutopilotTiming.Value) return;
        if (!Plugin.Instance.CfgAutopilotEtaSync.Value)
        {
            _wasSyncing = false;
            return;
        }

        var player = GamePlayer.current;
        if (player == null || !player.autoPlay)
        {
            _wasSyncing = false;
            return;
        }

        var travel = Singleton<TravelManager>.Instance;
        if (travel == null || !travel.isWarping)
        {
            // Travel ended or was cancelled. Let the vanilla cycle resume:
            // the next FindActivity call (or the arrival-snap patch) will
            // restore a sane updateTimer/base pairing.
            _wasSyncing = false;
            return;
        }

        float eta = ComputeEtaSeconds(travel.remainingDistance, GetSpaceShipTravelSpeed());

        if (!_wasSyncing)
        {
            // First tick of a new warp — seed the base so the circle starts empty.
            float seededBase = Mathf.Max(eta, MinEtaBase);
            UpdateTimerBaseRef(__instance) = seededBase;
            Plugin.Log.LogInfo(
                $"[autopilot-timing] eta-sync begin: eta={eta:F2}s, base={seededBase:F2}s");
            _wasSyncing = true;
        }

        // Hold the base steady unless the trip lengthened (e.g. waypoint added
        // mid-flight). Only grow, never shrink — shrinking would make fillAmount
        // jump backward visually.
        float currentBase = UpdateTimerBaseRef(__instance);
        if (eta > currentBase)
        {
            UpdateTimerBaseRef(__instance) = eta;
        }

        // Always overwrite updateTimer with the live ETA. This replaces both
        // the vanilla `updateTimer -= Time.deltaTime` from IdleManager.Update
        // and any mid-travel FindActivity re-set.
        UpdateTimerRef(__instance) = eta;
    }

    /// <summary>
    /// Pull the current ship's warp speed off the singleton gameplay manager.
    /// Returns 0 (caller will floor) if the manager or ship is unavailable —
    /// which happens briefly during scene transitions and the first frame
    /// after emergency jumps.
    /// </summary>
    private static float GetSpaceShipTravelSpeed()
    {
        var gm = GameplayManager.Instance;
        if (gm == null) return 0f;
        var ship = gm.spaceShip;
        if (ship == null || ship.unitData == null) return 0f;
        return ship.unitData.travelSpeed;
    }
```

Note: `GameplayManager` lives in the global namespace of `Assembly-CSharp.dll`, so no extra `using` line is required.

- [ ] **Step 4: Build**

```bash
make build
```

Expected: build succeeds with 0 errors. If you see `CS0246` (type-not-found) for `GameplayManager`, confirm `Assembly-CSharp.dll` is reachable in `VGEcho/lib/` (the Makefile's `link-asm` target runs automatically on every build).

- [ ] **Step 5: Deploy**

```bash
make deploy
```

- [ ] **Step 6: In-game verification — ETA sync (user must run this manually)**

1. Launch the game. Load a save.
2. Open the BepInEx console.
3. Press **T** to enable autopilot. Wait for the autopilot to initiate a warp to a POI at least 30 seconds away (mining, salvage, or cross-system travel — a nearby POI won't give you enough time to observe the effect).
4. The moment warp begins, expect one console line:

    ```
    [Info   :Vanguard Galaxy Echo] [autopilot-timing] eta-sync begin: eta=NN.NNs, base=NN.NNs
    ```

    where NN.NN is the initial ETA in seconds.
5. Watch the green progress circle on the Autopilot side-tab. Expected behavior: starts empty at warp begin, fills smoothly over the entire trip, arrives at 100% at the exact moment the ship drops out of warp.
6. Compare to vanilla: with `EtaSync = false`, the circle would cycle 0→100% every 12 seconds during the trip regardless of distance. With the patch, there's one continuous fill covering the whole journey.
7. Combined with the arrival-snap patch from Task 1: the circle should visibly complete at drop-out AND the next activity should fire within one frame of reaching 100%.

- [ ] **Step 7: In-game verification — jump-gate case**

1. Trigger a multi-system journey (destination in a different system than the current one, so the autopilot will use a jump-gate).
2. Expect: `eta-sync begin` log fires for the in-system leg to the jumpgate. During the jumpgate cutscene (~4–5s of `WaitForSeconds`), `isWarping` is false, so the ETA-sync patch is inactive — the circle behaves vanilla. On the far side of the gate, the next in-system leg re-engages ETA-sync. When the final leg ends, arrival-snap fires.
3. Verify no error spam in the log during jump-gate transitions. If you see `[autopilot-timing] eta-sync begin` firing every frame, the `_wasSyncing` state is not being held correctly — re-check Step 3's implementation.

- [ ] **Step 8: Commit**

```bash
git add VGEcho/Patches/AutopilotTimingPatches.cs
git commit -m "feat: autopilot ETA-sync — map IdleManager cycle to live travel ETA

Postfix on IdleManager.Update: while TravelManager.isWarping is true and
the player is on autopilot, overwrite updateTimer with the live ETA
(remainingDistance / travelSpeed) and hold updateTimerBase at the
initial ETA of the leg. The SideTabAutopilot fill circle becomes a
smooth travel-progress indicator that completes exactly on drop-out.

Also kills the no-op FindActivity spin during long warps — instead of
the cycle resetting to 12s and calling FindActivity only to bail on
TravelActive(), our postfix re-overwrites with the ETA every tick.

Gated by [Autopilot] EtaSync config entry. Jump-gate transitions fall
back to the vanilla cycle (isWarping = false during the cutscene);
arrival-snap covers the post-gate drop-out."
```

---

## Task 3: Polish — README touch-up and config documentation

**Goal of this task:** Ensure the README accurately reflects shipped behavior (it already describes both features, but this confirms no drift after implementation) and add a short "config reference" section listing the three toggles and their defaults.

**Files:**
- Modify: `/home/fank/repo/vanguard-galaxy-echo/README.md`

- [ ] **Step 1: Append a "Config reference" section to README.md**

Open `/home/fank/repo/vanguard-galaxy-echo/README.md`. After the existing "First-run verification" section (currently the last section), append:

```markdown

## Config reference

Config file: `<game>/BepInEx/config/dev.fankserver.vgecho.cfg` (created on first launch).

| Key                          | Default | Purpose                                                                                                   |
| ---------------------------- | ------- | --------------------------------------------------------------------------------------------------------- |
| `[Autopilot] TimingEnabled`  | `true`  | Master toggle. When `false`, both ETA-sync and arrival-snap are skipped and the vanilla 12s cycle runs.   |
| `[Autopilot] EtaSync`        | `true`  | While warping, overwrite the IdleManager cycle with live travel ETA. Circle becomes a progress indicator. |
| `[Autopilot] ArrivalSnap`    | `true`  | On final-waypoint arrival, zero the cycle so the next autonomous action fires immediately.               |

Neither patch changes what the autopilot decides to do — only *when* it decides. Disable either independently via config; no rebuild or redeploy needed, just relaunch the game.
```

- [ ] **Step 2: Verify — no build step needed, README-only change**

```bash
cat README.md | tail -20
```

Expected: the new "Config reference" section appears at the end.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: add Config reference table to README

Lists the three [Autopilot] config keys with defaults and a one-line
purpose each. No behavior change."
```

---

## Rollback notes

If any feature misbehaves in a specific scenario:

- **Disable one feature:** edit `BepInEx/config/dev.fankserver.vgecho.cfg`, set the individual toggle to `false`, relaunch. No rebuild needed.
- **Disable all:** set `[Autopilot] TimingEnabled = false`.
- **Full revert:** `git revert` the Task 1 and Task 2 commits. Task 3 (docs) can stay or be reverted separately.

## Known limitations

1. **Jump-gate ETA is not synced.** `usingJumpgate` state has no exposed numeric ETA — it's driven by hardcoded `WaitForSeconds` calls in `TravelManager.JumpToSystem`. During the gate cutscene the circle reverts to vanilla behavior. The arrival-snap patch handles the post-gate state.
2. **ETA drifts under acceleration/deceleration.** `remainingDistance / travelSpeed` assumes constant speed, but the ship accelerates at warp start and decelerates near the destination. Expect the reported ETA to be slightly optimistic at warp start (overestimates time) and pessimistic near drop-out (underestimates). Drift is typically within 10%. Arrival-snap is the ultimate safety net — whatever the ETA predicted, the cycle zeroes on actual arrival.
3. **Fast-lane travel (7× multiplier) may cause ETA oscillation.** When `fastLaneTravelActive` flips between jumpgates, `travelSpeed` jumps. The ETA recomputes correctly but the `updateTimerBase` grow-only rule means the circle may pause visually while the base catches up. Acceptable for the uncommon case.
4. **Reflection targets C# compiler backing-field names.** If the game is recompiled with different IL output (e.g. ngen/IL2CPP — currently Mono, so not a concern), the `<updateTimer>k__BackingField` name could change. The plugin will log a Harmony exception at startup in that case; recover by updating the two `FieldRefAccess` string literals in `AutopilotTimingPatches.cs`.

## Out of scope

These were discussed and deliberately deferred:

- Shortening `updateTimerBase` directly (12s → 6s) for a faster vanilla cycle. Orthogonal to arrival sync and can be layered later as a third toggle.
- Shortening `DelayIdleActivities(delay = 15f)` post-interaction cooldown. The `delayTimer` auto-clears in space (`IdleManager.Update:94–96`), so its real-world impact is minimal; revisit only if needed.
- Filling in the unused `ExpertActivityDelay = 6f` constant as a fourth skill tier. That's a game-design change, not a timing alignment.
