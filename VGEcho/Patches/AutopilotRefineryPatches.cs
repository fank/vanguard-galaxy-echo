using Behaviour.Gameplay;
using Behaviour.Managers;
using Behaviour.Mining;
using Behaviour.Util;
using HarmonyLib;
using Source.Galaxy;
using Source.Galaxy.POI;
using Source.Player;
using Source.SpaceShip;

namespace VGEcho.Patches;

/// <summary>
/// Patches that change <i>where</i> the autopilot goes and <i>what</i> it does at the
/// station (not just timing). Two independent opt-in toggles:
///   • <b>RefineryRoute</b> (postfix on <c>IdleManager.IdleTravelToSpaceStation</c>):
///     when vanilla decided to fly back to the home station with ore in cargo, and the
///     home station has no refinery, override the route to the nearest friendly station
///     within <c>RefineryMaxHops</c> that does. Defers to vanilla (no override) if the
///     idle mission already targets a station, or if no refinery station is within
///     range.
///   • <b>AutoRefine</b> (postfix on <c>TravelManager.TravelToNextWaypoint</c>):
///     when travel ends at a station with a refinery while on autopilot, flip
///     <c>station.refinery.autoRefine = true</c> so pending ore refines passively.
///
/// Both default to <c>false</c> — they change behavior, not timing, so existing
/// installs stay on vanilla decisions unless the user opts in.
/// </summary>
[HarmonyPatch]
internal static class AutopilotRefineryPatches
{
    // Private field on IdleManager tracking the last POI that TravelToPoi aimed at.
    // FindActivity reads it against TravelManager.targetPoi to detect "user changed
    // course mid-trip" and emits an interrupted-travel status. Vanilla's home-routing
    // branch sets it to home; when we divert to a refinery we must update it to match
    // or FindActivity will spam "@IdleTravelInterupted" every tick of the redirect.
    private static readonly AccessTools.FieldRef<IdleManager, MapPointOfInterest> IdleTravelTargetRef =
        AccessTools.FieldRefAccess<IdleManager, MapPointOfInterest>("idleTravelTarget");

    /// <summary>
    /// Postfix on the private <c>IdleManager.IdleTravelToSpaceStation</c>. Runs after
    /// vanilla has made its routing decision. If vanilla's target is the home station,
    /// our conditions hold, and a closer refinery exists, re-issue the route via
    /// <see cref="TravelManager.TravelToClosestSpacestationWithFacility"/> and patch
    /// <c>IdleManager.idleTravelTarget</c> to match so <c>FindActivity</c>'s
    /// interrupted-travel check stays consistent. The first <c>SetRouteToPOI</c> call
    /// (from vanilla) is cancelled internally by the second — both happen in the same
    /// frame before any coroutine yield, so the cost is negligible.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(IdleManager), "IdleTravelToSpaceStation")]
    private static void IdleTravelToSpaceStation_Postfix(IdleManager __instance, bool overrideClosests)
    {
        if (!Plugin.Instance.CfgAutopilotRefineryRoute.Value) return;
        // overrideClosests is the ammo-fetch path — don't divert away from a shop.
        if (overrideClosests) return;

        var player = GamePlayer.current;
        if (player == null || !player.autoPlay) return;
        // Respect the vanilla "don't travel" pref — otherwise we'd route to a refinery
        // even though the player has asked the autopilot to stay put. Also covers the
        // case where vanilla bailed out without touching targetPoi, which could leave
        // a stale value matching the home-station check below.
        if (player.autopilotSettings == null || player.autopilotSettings.noTravel) return;

        // We only override vanilla's "fly home" branch. If no home is set, vanilla
        // already picks the closest station — nothing to improve.
        var home = player.homeStation;
        if (home == null) return;
        // If home has a refinery, going home is already optimal.
        if (home.refinery != null) return;

        var travel = Singleton<TravelManager>.Instance;
        if (travel == null || !travel.CanWeTravel()) return;
        // If vanilla routed somewhere other than home (mission station, closest
        // fallback, or couldn't travel for some other reason), don't second-guess it.
        if (travel.targetPoi != home) return;

        // Only divert when the reason we're going back is to deposit ore.
        var ship = player.currentSpaceShip;
        if (ship?.cargo == null) return;
        if (!CargoContainsOre(ship)) return;

        int maxHops = Plugin.Instance.CfgAutopilotRefineryMaxHops.Value;
        var refineryStation = travel.TravelToClosestSpacestationWithFacility(
            SpaceStationFacility.Refinery, maxHops);
        if (refineryStation == null) return;
        // Belt-and-braces: home.refinery == null implied the search can't return home,
        // but defensively bail if somehow it did.
        if (refineryStation == home) return;

        // Keep IdleManager's private idleTravelTarget in sync with the new route so
        // FindActivity's interrupted-travel check doesn't fire every tick of the trip.
        // Clear rudelyInterrupted too: vanilla's TravelToPoi sets it to a ~30% random
        // on fresh routes, so re-using the stale value from the home route would leak
        // into a later real interrupt and misclassify it.
        IdleTravelTargetRef(__instance) = refineryStation;
        __instance.rudelyInterrupted = false;

        Plugin.Log.LogInfo(
            $"[autopilot-refinery] redirecting home ({home.name}) -> refinery ({refineryStation.name}), " +
            $"maxHops={maxHops}");
        IdleManager.UpdateActivity("@IdleGoToSpaceStation");
    }

    /// <summary>
    /// Postfix on <see cref="TravelManager.TravelToNextWaypoint"/>. Only acts on the
    /// final-waypoint arrival (waypoints empty, travel no longer active) while on
    /// autopilot. If the destination is a station with a refinery and auto-refine is
    /// currently off, flip it on. The flag is serialized per-station so the change
    /// persists.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TravelManager), nameof(TravelManager.TravelToNextWaypoint))]
    private static void TravelToNextWaypoint_AutoRefine_Postfix()
    {
        if (!Plugin.Instance.CfgAutopilotAutoRefine.Value) return;

        var player = GamePlayer.current;
        if (player == null || !player.autoPlay) return;
        // Not the final arrival — we're mid-journey.
        if (player.waypoints.Count != 0) return;

        var travel = Singleton<TravelManager>.Instance;
        if (travel == null || travel.TravelActive()) return;

        // currentPointOfInterest is set inside Travel() before TravelToNextWaypoint
        // is called, so by here MapPointOfInterest.current reflects the destination.
        if (MapPointOfInterest.current is not SpaceStation station) return;
        if (station.refinery == null) return;
        if (station.refinery.autoRefine) return;

        station.refinery.autoRefine = true;
        Plugin.Log.LogInfo($"[autopilot-refinery] auto-refine enabled at {station.name}");
    }

    /// <summary>
    /// Mirror of <c>Refinery.GetAvailableItems</c>'s ore detection — an item is ore iff
    /// its prefab has an <see cref="OreItemData"/> component attached. Using the
    /// component test (not <c>itemCategory == Ore</c>) ensures we match exactly what
    /// the refinery itself considers refinable.
    /// </summary>
    private static bool CargoContainsOre(SpaceShipData ship)
    {
        foreach (var item in ship.cargo.items)
        {
            var itemType = item?.item;
            if (itemType == null) continue;
            if (itemType.GetComponent<OreItemData>() != null) return true;
        }
        return false;
    }
}
