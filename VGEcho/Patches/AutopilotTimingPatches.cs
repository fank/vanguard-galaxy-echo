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
}
