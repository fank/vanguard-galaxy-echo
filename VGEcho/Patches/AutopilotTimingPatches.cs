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
    /// Postfix on <see cref="IdleManager.Update"/>. Runs every frame. When the
    /// player is on autopilot and <see cref="TravelManager.isWarping"/> is
    /// true, overwrite <c>updateTimer</c> and <c>updateTimerBase</c> so the
    /// autopilot side-tab's green fill circle tracks live <b>distance-based
    /// progress</b> along the trip — not seconds remaining.
    ///
    /// Why distance instead of ETA: the ship accelerates for ~10 s, cruises,
    /// then decelerates. A naive <c>eta = remainingDistance / travelSpeed</c>
    /// blows up on the first tick of warp (<c>travelSpeed ≈ 0</c>), producing
    /// a huge seed that then collapses the moment the ship gets any real
    /// speed — the circle rockets to ~99 % in two frames and crawls the rest.
    /// Distance-based progress is speed-independent and monotonic: the circle
    /// fills at the ship's actual spatial progress (slow during accel/decel,
    /// fast during cruise), which matches the player's intuition.
    ///
    /// We seed <c>updateTimerBase = totalDistance</c> once per warp leg and
    /// grow it if the trip lengthens (waypoint added mid-flight). Each tick
    /// we set <c>updateTimer = remainingDistance</c>. <c>SideTabAutopilot</c>'s
    /// <c>fillAmount = 1 - updateTimer/base</c> then renders as the fraction
    /// of the trip covered.
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
            _wasSyncing = false;
            return;
        }

        float totalDistance = travel.totalDistance;
        float remainingDistance = Mathf.Max(0f, travel.remainingDistance);

        // Guard: TravelManager clears totalDistance to 0 on arrival, and there
        // is a brief window at warp start where isWarping has flipped true
        // but totalDistance hasn't been set yet. In both cases, skip.
        if (totalDistance <= 0f) return;

        if (!_wasSyncing)
        {
            UpdateTimerBaseRef(__instance) = totalDistance;
            Plugin.Log.LogInfo(
                $"[autopilot-timing] eta-sync begin: distance={totalDistance:F1}u, " +
                $"est-eta={EstimateTripSeconds(totalDistance):F1}s");
            _wasSyncing = true;
        }

        // Grow-only base: if the trip lengthens (e.g. waypoint added mid-flight),
        // extend the base so fillAmount never jumps backward.
        float currentBase = UpdateTimerBaseRef(__instance);
        if (totalDistance > currentBase)
        {
            UpdateTimerBaseRef(__instance) = totalDistance;
        }

        UpdateTimerRef(__instance) = remainingDistance;
    }

    /// <summary>
    /// Rough best-case trip duration for logging purposes only. Uses the
    /// ship's configured <c>baseMaxWarpSpeed</c> (no fuel/bonus multipliers,
    /// no accel/decel overhead), so the real trip will always take longer.
    /// Returns 0 if the ship or its configured max speed is unavailable — the
    /// log line just prints "est-eta=0.0s" in that case and the circle itself
    /// is unaffected because it uses distance, not this estimate.
    /// </summary>
    private static float EstimateTripSeconds(float distance)
    {
        var gm = GameplayManager.Instance;
        if (gm == null) return 0f;
        var ship = gm.spaceShip;
        if (ship == null) return 0f;
        float maxSpeed = ship.baseMaxWarpSpeed;
        if (maxSpeed <= 0.1f) return 0f;
        return distance / maxSpeed;
    }
}
