using System.Linq;
using Behaviour.UI.Side_Menu.SideTabs;
using Behaviour.UI.Tooltip;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VGEcho.Patches;

/// <summary>
/// Runtime UI injection for the Autopilot side-tab. Adds an in-game toggle for
/// <c>AutoRefine</c> next to the vanilla "Auto sell" / "Disable travel" checkboxes
/// so players don't need to edit <c>vgecho.cfg</c> to flip it.
///
/// Approach: postfix <see cref="Autopilot"/>'s <c>Awake</c>, clone the serialized
/// <c>autoSellToggle</c> (it already has the right visual style, font, and pointer
/// graphics), reparent the clone to the same layout container, scrub the prefab-
/// wired state (<c>onValueChanged</c> listener that writes to
/// <c>AutopilotSettings.autoSell</c>, tooltip body, label text), and hook our own
/// handler that mirrors to <see cref="Plugin.CfgAutopilotAutoRefine"/>.
/// </summary>
[HarmonyPatch(typeof(Autopilot), "Awake")]
internal static class AutopilotUIPatches
{
    // Marker name on the clone so re-entry (Unity re-awakes the GameObject after a
    // scene reload) doesn't stack duplicate toggles.
    private const string CloneName = "vgecho_autoRefineToggle";

    // Label text on the new toggle. Hardcoded English — no @Autopilot* translation
    // key exists for our custom feature, and passing a raw string to TMP_Text just
    // renders it literally (no localisation pass).
    private const string LabelText = "Auto refine";

    // Reflection accessor for the private serialized field on Autopilot.
    private static readonly AccessTools.FieldRef<Autopilot, Toggle> AutoSellToggleRef =
        AccessTools.FieldRefAccess<Autopilot, Toggle>("autoSellToggle");

    [HarmonyPostfix]
    private static void Awake_Postfix(Autopilot __instance)
    {
        try
        {
            var source = AutoSellToggleRef(__instance);
            if (source == null)
            {
                Plugin.Log.LogWarning("[autopilot-ui] autoSellToggle is null; skipping clone");
                return;
            }

            var parent = source.transform.parent;
            if (parent == null)
            {
                Plugin.Log.LogWarning("[autopilot-ui] autoSellToggle has no parent; skipping clone");
                return;
            }

            // If we already added a clone (e.g. Awake re-fired for the same
            // GameObject), don't add a second one.
            if (parent.Find(CloneName) != null) return;

            var clone = Object.Instantiate(source.gameObject, parent);
            clone.name = CloneName;

            var toggle = clone.GetComponent<Toggle>();
            if (toggle == null)
            {
                Plugin.Log.LogWarning("[autopilot-ui] cloned object has no Toggle component");
                Object.Destroy(clone);
                return;
            }

            // Strip the prefab-wired onValueChanged listener — it writes to
            // autopilotSettings.autoSell, which would make our toggle also flip
            // auto-sell on every click. RemoveAllListeners only clears runtime
            // listeners; persistent (serialized) listeners survive, so we
            // additionally short-circuit via SetPersistentListenerState.
            toggle.onValueChanged.RemoveAllListeners();
            int persistentCount = toggle.onValueChanged.GetPersistentEventCount();
            for (int i = 0; i < persistentCount; i++)
            {
                toggle.onValueChanged.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            }

            // Overwrite the visible label. The label is on a child TMP_Text,
            // typically at Label/Text or similar.
            foreach (var text in clone.GetComponentsInChildren<TMP_Text>(includeInactive: true))
            {
                text.text = LabelText;
            }

            // Remove the cloned tooltip so hovering doesn't surface the old
            // "Auto sell" body text. We don't have a localised body to swap in.
            foreach (var tooltip in clone.GetComponentsInChildren<TooltipSource>(includeInactive: true))
            {
                Object.Destroy(tooltip);
            }

            toggle.SetIsOnWithoutNotify(Plugin.Instance.CfgAutopilotAutoRefine.Value);
            toggle.onValueChanged.AddListener(isOn =>
            {
                if (Plugin.Instance.CfgAutopilotAutoRefine.Value == isOn) return;
                Plugin.Instance.CfgAutopilotAutoRefine.Value = isOn;
                Plugin.Instance.Config.Save();
                Plugin.Log.LogInfo($"[autopilot-ui] AutoRefine toggled -> {isOn}");
            });

            // First-run visibility into the parent layout so we (and the user)
            // can see whether the container supports side-by-side placement or
            // forces a vertical stack. This is one-shot diagnostic output —
            // remove the log once the layout is confirmed.
            var layouts = parent.GetComponents<LayoutGroup>();
            var layoutNames = layouts.Length == 0
                ? "(none)"
                : string.Join(",", layouts.Select(l => l.GetType().Name));
            Plugin.Log.LogInfo(
                $"[autopilot-ui] Auto-refine toggle injected. parent='{parent.name}', " +
                $"layouts=[{layoutNames}], siblings={parent.childCount}");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[autopilot-ui] Awake postfix failed: {e}");
        }
    }
}
