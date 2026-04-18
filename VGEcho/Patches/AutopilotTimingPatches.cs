using Behaviour.Gameplay;
using Behaviour.Managers;
using Behaviour.Util;
using HarmonyLib;
using Source.Player;
using UnityEngine;

namespace VGEcho.Patches;

/// <summary>
/// Patches on <see cref="IdleManager"/> and <see cref="TravelManager"/> that align
/// the autopilot "next-task" cycle with space-travel arrival. Two mechanisms:
///   • <b>ETA-sync</b> (postfix on <c>IdleManager.Update</c>): while the ship is
///     warping, overwrite <c>updateTimer</c>/<c>updateTimerBase</c> with the live
///     travel ETA so the green progress circle visibly completes on drop-out.
///   • <b>Arrival-snap</b> (postfix on <c>TravelManager.TravelToNextWaypoint</c>):
///     when the final waypoint is reached, zero <c>updateTimer</c> so the next
///     <c>IdleManager.Update</c> tick immediately triggers <c>FindActivity</c>.
///
/// Both engage only when <c>GamePlayer.current.autoPlay</c> is true and the
/// matching config entry is enabled. Private setters on <c>IdleManager</c>'s
/// auto-properties are written via <see cref="AccessTools.FieldRefAccess"/>
/// against the C# compiler-generated backing fields.
/// </summary>
[HarmonyPatch]
internal static class AutopilotTimingPatches
{
    // Compiler-generated backing-field names for auto-properties on IdleManager.
    // If the game is ever recompiled with different property names, update these.
    private static readonly AccessTools.FieldRef<IdleManager, float> UpdateTimerRef =
        AccessTools.FieldRefAccess<IdleManager, float>("<updateTimer>k__BackingField");

    private static readonly AccessTools.FieldRef<IdleManager, float> UpdateTimerBaseRef =
        AccessTools.FieldRefAccess<IdleManager, float>("<updateTimerBase>k__BackingField");

    // Tracks whether our previous Update tick saw isWarping=true. Used so we
    // can capture the initial updateTimerBase at warp start and allow the
    // vanilla IdleManager cycle to resume naturally after warp ends.
    private static bool _wasSyncing;

    // Floor for updateTimerBase while ETA-syncing. If initial ETA is smaller
    // (e.g. very short hop), we clamp up so the fill circle always shows some
    // travel progress rather than a flash.
    private const float MinEtaBase = 3f;

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

    /// <summary>
    /// Postfix on the private <c>IdleManager.SetQuickerUpdateTimer</c>. The
    /// vanilla helper sets <c>updateTimer = 400f / cargoCapacity</c>, giving
    /// a ~1–2 s gap between consecutive cargo deposits (and auto-sells). When
    /// the player is on autopilot and fast-deposit is enabled, we overwrite
    /// that with 0 so the next <see cref="IdleManager.Update"/> tick calls
    /// <c>FindActivity</c> again on the very next frame. A full cargo hold
    /// drains in a handful of frames instead of minutes.
    ///
    /// Note: per-cycle quantity is unchanged (still one unit per cycle for
    /// regular items, 20/mag-size for ammo, 20 for currency). Making each
    /// cycle transfer the full stack is a separate feature that requires an
    /// IL transpiler on <c>IdleManager.FindActivity</c>.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(IdleManager), "SetQuickerUpdateTimer")]
    private static void SetQuickerUpdateTimer_Postfix(IdleManager __instance)
    {
        if (!Plugin.Instance.CfgAutopilotTiming.Value) return;
        if (!Plugin.Instance.CfgAutopilotFastDeposit.Value) return;

        var player = GamePlayer.current;
        if (player == null || !player.autoPlay) return;

        Plugin.Log.LogDebug("[autopilot-timing] fast-deposit: zeroing updateTimer");
        UpdateTimerRef(__instance) = 0f;
    }

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
}
