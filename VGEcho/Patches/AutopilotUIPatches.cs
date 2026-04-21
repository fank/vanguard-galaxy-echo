using System.Linq;
using System.Reflection;
using Behaviour.UI.Side_Menu.SideTabs;
using Behaviour.UI.Tooltip;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VGEcho.Patches;

/// <summary>
/// Runtime UI injection for the Autopilot side-tab. On each <see cref="Autopilot"/>
/// <c>Awake</c>, clones the vanilla "Auto sell" row (toggle + label + ancillary
/// text), reparents the clone next to the original in <c>Container</c>, and binds
/// it to <see cref="Plugin.CfgAutopilotAutoRefine"/> so the in-game checkbox
/// mirrors the BepInEx config entry.
///
/// A few non-obvious constraints the prefab forced on us:
///   • The cloned Toggle's <c>onValueChanged</c> carries prefab-serialised calls
///     wired to <c>Autopilot.ToggleAutoSell</c>. Public <c>RemoveAllListeners()</c>
///     only clears runtime listeners; the persistent ones have to be nuked via
///     reflection into <c>UnityEventBase.m_PersistentCalls</c> or the click
///     silently continues to flip <c>autopilotSettings.autoSell</c>.
///   • <c>Container</c> has no <c>LayoutGroup</c>, so rows are free-positioned
///     by RectTransform and render order is sibling-order. The vanilla row's
///     label RectTransform is 300px wide and spans across the 2-column grid
///     (left column ends at x≈4783 in world coords, label starts at x≈4698), so
///     its raycast rect extends into the left column. The Disable-Travel toggle
///     is 160×32 (i.e. the entire left-column row), so clicks on that overhang
///     hit <c>noTravelToggle</c> instead of our label. We fix this by
///     <see cref="Transform.SetAsLastSibling"/>-ing the clone, making its label
///     rect topmost in the raycast order; a no-op
///     <see cref="LabelClickProxy"/> on the label then absorbs the click.
///   • The row contains a <c>Translatable</c> MonoBehaviour that re-applies the
///     source translate key on every <c>OnEnable</c>, which would overwrite our
///     "Auto refine" rename each time the tab reopens. We destroy it.
///   • The cloned <c>TooltipSource</c>'s body text still points at the
///     "@AutopilotAutoSell" key, so it's destroyed as well — no localised
///     replacement available.
/// </summary>
[HarmonyPatch(typeof(Autopilot), "Awake")]
internal static class AutopilotUIPatches
{
    private const string CloneName = "vgecho_autoRefineRow";
    private const string LabelText = "Auto refine";

    private static readonly AccessTools.FieldRef<Autopilot, Toggle> AutoSellToggleRef =
        AccessTools.FieldRefAccess<Autopilot, Toggle>("autoSellToggle");

    // UnityEventBase internals for scrubbing persistent (prefab-serialised)
    // listeners. Cached on first use.
    private static FieldInfo? _persistentCallsField;
    private static MethodInfo? _persistentCallsClear;

    [HarmonyPostfix]
    private static void Awake_Postfix(Autopilot __instance)
    {
        try
        {
            InjectAutoRefineToggle(__instance);
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[autopilot-ui] Awake postfix failed: {e}");
        }
    }

    private static void InjectAutoRefineToggle(Autopilot panel)
    {
        var source = AutoSellToggleRef(panel);
        if (source == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] autoSellToggle is null; skipping clone");
            return;
        }

        // The row we want to duplicate wraps the Toggle with its label/icon —
        // one level up from the toggle itself (the toggle GameObject contains a
        // Toggle component plus its own checkbox/checkmark children, but the
        // sibling label lives on the row GameObject).
        var row = source.transform.parent;
        if (row == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] autoSellToggle has no parent; aborting");
            return;
        }
        // Defensive: if a future prefab restructure puts the Toggle at the row
        // level itself, climb one more so we clone the enclosing row.
        if (row.GetComponent<Toggle>() != null && row.parent != null)
        {
            row = row.parent;
        }

        var insertParent = row.parent;
        if (insertParent == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] row has no parent; aborting");
            return;
        }

        // Idempotency: skip if a previous Awake on the same tab already added one.
        if (insertParent.Find(CloneName) != null) return;

        var cloneGO = Object.Instantiate(row.gameObject, insertParent);
        cloneGO.name = CloneName;
        // Render order: sibling index decides who wins raycasts at overlap.
        // See class docstring for the Disable-Travel overlap story.
        cloneGO.transform.SetAsLastSibling();

        // Container has no LayoutGroup; Instantiate copies the source row's
        // explicit anchoredPosition, so without this the clone lands directly
        // on top of `Autosell`. Shift it down by one row-height + small gap.
        var sourceRT = row.GetComponent<RectTransform>();
        var cloneRT = cloneGO.GetComponent<RectTransform>();
        if (sourceRT != null && cloneRT != null)
        {
            var pos = cloneRT.anchoredPosition;
            pos.y -= sourceRT.rect.height + 6f;
            cloneRT.anchoredPosition = pos;
        }

        var toggle = cloneGO.GetComponentInChildren<Toggle>(includeInactive: true);
        if (toggle == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] cloned row has no Toggle; aborting");
            Object.Destroy(cloneGO);
            return;
        }

        // Full scrub of the cloned Toggle's onValueChanged so clicks on the
        // checkbox don't also fire Autopilot.ToggleAutoSell.
        ScrubUnityEvent(toggle.onValueChanged);

        // The Autosell row has more than one TMP_Text — a primary label before
        // the checkbox and an empty placeholder inside the Toggle prefab. Rename
        // only the first non-empty TMP_Text (primary label) and disable raycast
        // on the rest so they can't intercept anything.
        var clonedTexts = cloneGO.GetComponentsInChildren<TMP_Text>(includeInactive: true);
        var primary = clonedTexts.FirstOrDefault(t => !string.IsNullOrEmpty(t.text));
        if (primary != null)
        {
            primary.text = LabelText;
            // Attach LabelClickProxy so clicks on the label's raycast rect have
            // a definite no-op handler and cannot bubble to a parent handler
            // (e.g. SideTabAutopilot, which would flip the autoPlay master).
            var proxy = primary.gameObject.AddComponent<LabelClickProxy>();
            proxy.target = toggle;
        }
        else
        {
            Plugin.Log.LogWarning("[autopilot-ui] no TMP_Text with non-empty text in clone; label unchanged");
        }
        foreach (var text in clonedTexts)
        {
            if (text != primary)
            {
                text.raycastTarget = false;
            }
        }

        // Translatable.OnEnable re-applies the source translate key on each
        // enable — would overwrite our rename when the tab is re-opened.
        foreach (var translatable in cloneGO.GetComponentsInChildren<Behaviour.Util.Translatable>(includeInactive: true))
        {
            Object.Destroy(translatable);
        }
        // Tooltip body still points at the "Auto sell" key; no localised
        // "Auto refine" equivalent, so drop it entirely.
        foreach (var tooltip in cloneGO.GetComponentsInChildren<TooltipSource>(includeInactive: true))
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

        Plugin.Log.LogInfo(
            $"[autopilot-ui] AutoRefine row injected at '{insertParent.name}/{cloneGO.name}' " +
            $"(sibling of '{row.name}'), value={Plugin.Instance.CfgAutopilotAutoRefine.Value}");
    }

    /// <summary>
    /// Wipe both runtime listeners and prefab-serialised persistent calls from
    /// a UnityEvent. The public <c>RemoveAllListeners</c> only clears runtime
    /// listeners; persistent calls survive and keep firing their original
    /// targets (e.g. the cloned Toggle's onValueChanged would still call
    /// <c>Autopilot.ToggleAutoSell</c> and flip <c>autopilotSettings.autoSell</c>
    /// whenever the user clicked our new checkbox). Reach into
    /// <c>UnityEventBase.m_PersistentCalls</c> and clear the internal list to
    /// nuke them fully.
    /// </summary>
    private static void ScrubUnityEvent(UnityEventBase evt)
    {
        var removeAll = evt.GetType().GetMethod(
            "RemoveAllListeners", BindingFlags.Instance | BindingFlags.Public);
        removeAll?.Invoke(evt, null);

        int persistentCount = evt.GetPersistentEventCount();
        for (int i = 0; i < persistentCount; i++)
        {
            evt.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        _persistentCallsField ??= typeof(UnityEventBase).GetField(
            "m_PersistentCalls", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_persistentCallsField == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] UnityEventBase.m_PersistentCalls field not found; " +
                                  "persistent scrub fell back to disable-only");
            return;
        }
        var persistentCalls = _persistentCallsField.GetValue(evt);
        if (persistentCalls == null) return;

        _persistentCallsClear ??= persistentCalls.GetType().GetMethod(
            "Clear", BindingFlags.Instance | BindingFlags.Public);
        if (_persistentCallsClear == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] PersistentCallGroup.Clear not found; " +
                                  "persistent scrub fell back to disable-only");
            return;
        }
        _persistentCallsClear.Invoke(persistentCalls, null);
    }
}

/// <summary>
/// Absorbs pointer clicks on the cloned label. Without an IPointerClickHandler
/// on the label's own GameObject, Unity's EventSystem walks up the parent chain
/// looking for one — and depending on the raycast resolution and sibling order,
/// the click can land on a neighbouring Selectable (observed: Disable-Travel
/// toggle) or a parent handler (observed: SideTabAutopilot, which flips the
/// autoPlay master toggle). This handler claims the click for the label's
/// GameObject and deliberately does nothing with it — per the user,
/// "text is text, no actions".
/// </summary>
public sealed class LabelClickProxy : MonoBehaviour, IPointerClickHandler
{
    // Kept as a reference for future behaviour changes (e.g. restoring
    // label-click-toggles-checkbox UX would wire target.isOn = !target.isOn).
    public Toggle? target;

    public void OnPointerClick(PointerEventData eventData)
    {
        // Consume the click. Intentionally no behaviour.
    }
}
