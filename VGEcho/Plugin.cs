using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace VGEcho;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("VanguardGalaxy.exe")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "dev.fankserver.vgecho";
    public const string PluginName = "Vanguard Galaxy Echo";
    // BepInEx parses PluginVersion through System.Version which rejects SemVer
    // pre-release suffixes, so stick to the plain N.N.N form.
    public const string PluginVersion = "0.1.0";

    internal static Plugin Instance { get; private set; } = null!;
    internal static ManualLogSource Log { get; private set; } = null!;

    internal ConfigEntry<bool> CfgAutopilotTiming = null!;
    internal ConfigEntry<bool> CfgAutopilotEtaSync = null!;
    internal ConfigEntry<bool> CfgAutopilotArrivalSnap = null!;
    internal ConfigEntry<bool> CfgAutopilotFastDeposit = null!;

    private Harmony _harmony = null!;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        CfgAutopilotTiming = Config.Bind("Autopilot", "TimingEnabled", true,
            "Master enable for autopilot (IdleManager) timing tweaks. When false, both " +
            "ETA-sync and arrival-snap are skipped and the vanilla 12s cycle runs unchanged.");
        CfgAutopilotEtaSync = Config.Bind("Autopilot", "EtaSync", true,
            "While warping, continuously set the IdleManager cycle to match the live travel ETA " +
            "(remainingDistance / travelSpeed). Makes the green progress circle complete exactly " +
            "on drop-out instead of running its own 12s loop during travel. Requires TimingEnabled.");
        CfgAutopilotArrivalSnap = Config.Bind("Autopilot", "ArrivalSnap", true,
            "When the ship reaches its final waypoint, zero the IdleManager cycle timer so the " +
            "next task fires on the following Update tick instead of waiting up to 12s. Covers " +
            "jump-gate transitions where ETA is unavailable. Requires TimingEnabled.");
        CfgAutopilotFastDeposit = Config.Bind("Autopilot", "FastDeposit", true,
            "Zero the IdleManager cycle timer after each autopilot cargo deposit or auto-sell " +
            "transfer, so successive items move on the next frame instead of after the vanilla " +
            "400/cargoCapacity seconds. Still one unit per cycle — full stack transfer is a " +
            "separate feature. Requires TimingEnabled.");

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(Patches.AutopilotTimingPatches));
        Log.LogInfo($"{PluginName} v{PluginVersion} loaded ({_harmony.GetPatchedMethods().Count()} patches)");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}
