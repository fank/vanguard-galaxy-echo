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
}
