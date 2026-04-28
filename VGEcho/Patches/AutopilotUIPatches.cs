using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
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
/// <c>Awake</c>, clones the vanilla "Auto sell" row for each of our opt-in
/// behaviour toggles and binds the cloned Toggle to the corresponding BepInEx
/// config entry:
///   • <c>AutoRefine</c> — right column at NoTravel's y (below Auto-sell).
///   • <c>RefineryRoute</c> ("Divert to refinery") — left column, row below AmmoMinutes.
///
/// The observed Container layout is NOT a clean 2-column grid:
///   • MiningLocationRow / RunMissions / PreferMissions / NoTravel / AmmoMinutes
///     all have stretched anchors that make their RectTransforms span the full
///     container width even though their visible content (toggle, slider) sits
///     on the left half.
///   • Loadout / AutoDetected / Autosell are tucked into the right half of the
///     first three rows via anchoredPosition.x=300.
///   • That leaves only one right-column cell empty (NoTravel's row) and
///     a full empty row below AmmoMinutes — which is where our toggles go.
///
/// A few non-obvious constraints the prefab forced on us:
///   • The cloned Toggle's <c>onValueChanged</c> carries prefab-serialised calls
///     wired to <c>Autopilot.ToggleAutoSell</c>. Public <c>RemoveAllListeners()</c>
///     only clears runtime listeners; the persistent ones have to be nuked via
///     reflection into <c>UnityEventBase.m_PersistentCalls</c> or the click
///     silently continues to flip <c>autopilotSettings.autoSell</c>.
///   • <c>Container</c> has no <c>LayoutGroup</c>, so rows are free-positioned
///     by RectTransform and render order is sibling-order. The vanilla row's
///     label RectTransform is 300px wide and spans across the 2-column grid,
///     so its raycast rect extends into the left-column's hit area. The
///     Disable-Travel / Prioritize-homestation toggles are 160×32 (i.e. the
///     entire left-column row), so clicks on the label's overhang hit the
///     neighbouring left-column toggle instead of our label. We fix this by
///     <see cref="Transform.SetAsLastSibling"/>-ing every clone so its label
///     rect is topmost in the raycast order; a no-op <see cref="LabelClickProxy"/>
///     on the label then absorbs the click.
///   • The row contains a <c>Translatable</c> MonoBehaviour that re-applies the
///     source translate key on every <c>OnEnable</c>, which would overwrite our
///     label rename each time the tab reopens. We destroy it.
///   • The cloned <c>TooltipSource</c>'s body text still points at the
///     "@AutopilotAutoSell" key, so it's destroyed as well — no localised
///     replacement available.
/// </summary>
[HarmonyPatch(typeof(Autopilot), "Awake")]
internal static class AutopilotUIPatches
{
    private const string AutoRefineRowName = "vgecho_autoRefineRow";
    private const string RefineryRouteRowName = "vgecho_refineryRouteRow";

    // Tooltip body strings use the game's #word# highlight convention — the
    // regex in Translation.Translate replaces #text# with <color=#FFD100>text
    // </color> (yellow), matching the vanilla "@Autopilot*Description" keys.
    // Keep titles as plain text (the vanilla titles like "Run Missions" have
    // no highlights).
    private const string AutoRefineLabel = "Auto-refine on dock";
    private const string AutoRefineTooltip =
        "If enabled, #ECHO# will turn on the station's #Auto-Refine# toggle when docking at a " +
        "station with a #refinery#, so pending ore refines passively.";

    private const string RefineryRouteLabel = "Divert to refinery";
    private const string RefineryRouteTooltip =
        "If enabled, #ECHO# will divert to the nearest station with a #refinery# when cargo " +
        "contains #ore# instead of returning to the #Home Station#.";

    // The source row's x (Autosell) is 300 — the right-column anchor. The
    // left-column rows (Homestation, RunMissions, ...) sit at x=5.
    private const float LeftColumnX = 5f;

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
            InjectToggles(__instance);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[autopilot-ui] Awake postfix failed: {e}");
        }
    }

    private static void InjectToggles(Autopilot panel)
    {
        var sourceToggle = AutoSellToggleRef(panel);
        if (sourceToggle == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] autoSellToggle is null; skipping UI injection");
            return;
        }

        // The row we want to duplicate wraps the Toggle with its label/icon —
        // one level up from the toggle itself.
        var sourceRow = sourceToggle.transform.parent;
        if (sourceRow == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] autoSellToggle has no parent; aborting");
            return;
        }
        // Defensive: if a future prefab restructure puts the Toggle at the row
        // level itself, climb one more so we clone the enclosing row.
        if (sourceRow.GetComponent<Toggle>() != null && sourceRow.parent != null)
        {
            sourceRow = sourceRow.parent;
        }

        var container = sourceRow.parent;
        if (container == null)
        {
            Plugin.Log.LogWarning("[autopilot-ui] source row has no parent; aborting");
            return;
        }

        var sourceRT = sourceRow.GetComponent<RectTransform>();
        float rowHeight = sourceRT != null ? sourceRT.rect.height : 30f;
        float rowStep = rowHeight + 6f;
        float rightColumnX = sourceRT != null ? sourceRT.anchoredPosition.x : 300f;

        // AutoRefine goes in the empty right-column cell at NoTravel's y.
        float autoRefineY = sourceRT != null
            ? sourceRT.anchoredPosition.y - rowStep
            : -150f;
        var cfg = Plugin.Instance;
        InjectRow(container, sourceRow, AutoRefineRowName,
            AutoRefineLabel, AutoRefineTooltip,
            rightColumnX, autoRefineY, cfg.CfgAutopilotAutoRefine);

        // Bottom row lives one step below the full-width AmmoMinutes slider.
        // Look it up by name so we don't hardcode coordinates that may shift
        // across game versions; fall back to two rows below Auto-sell.
        var ammoRow = container.Find("AmmoMinutes");
        var ammoRT = ammoRow != null ? ammoRow.GetComponent<RectTransform>() : null;
        float bottomRowY = ammoRT != null
            ? ammoRT.anchoredPosition.y - rowStep
            : autoRefineY - rowStep;

        // Left half of the bottom row: RefineryRoute.
        InjectRow(container, sourceRow, RefineryRouteRowName,
            RefineryRouteLabel, RefineryRouteTooltip,
            LeftColumnX, bottomRowY, cfg.CfgAutopilotRefineryRoute);
    }

    /// <summary>
    /// Convenience overload that binds the cloned Toggle directly to a
    /// <see cref="ConfigEntry{Boolean}"/>. Reads from / writes to
    /// <paramref name="config"/>.Value.
    /// </summary>
    private static void InjectRow(Transform container, Transform sourceRow,
        string cloneName, string labelText, string tooltipText,
        float anchoredX, float anchoredY,
        ConfigEntry<bool> config)
    {
        InjectRow(container, sourceRow, cloneName, labelText, tooltipText,
            anchoredX, anchoredY,
            getValue: () => config.Value,
            setValue: isOn => config.Value = isOn);
    }

    /// <summary>
    /// Clone <paramref name="sourceRow"/> into <paramref name="container"/>,
    /// reposition it at (<paramref name="anchoredX"/>, <paramref name="anchoredY"/>),
    /// relabel the primary TMP_Text to <paramref name="labelText"/>, retarget
    /// every cloned <see cref="TooltipSource"/> at our own title/body, scrub
    /// all prefab-wired handlers, and drive the cloned Toggle via the supplied
    /// getter/setter. Using lambdas (rather than a single ConfigEntry) lets one
    /// toggle mirror multiple configs at once if a future feature needs it.
    /// </summary>
    private static void InjectRow(Transform container, Transform sourceRow,
        string cloneName, string labelText, string tooltipText,
        float anchoredX, float anchoredY,
        Func<bool> getValue, Action<bool> setValue)
    {
        // Idempotency: if we already added one on a previous Awake of the same
        // panel GameObject, don't stack a duplicate.
        if (container.Find(cloneName) != null) return;

        var cloneGO = UnityEngine.Object.Instantiate(sourceRow.gameObject, container);
        cloneGO.name = cloneName;
        // Render order: sibling index decides who wins raycasts at overlap.
        // See class docstring for the left-column-overlap story.
        cloneGO.transform.SetAsLastSibling();

        // Pin to the requested (x, y).
        var cloneRT = cloneGO.GetComponent<RectTransform>();
        if (cloneRT != null)
        {
            cloneRT.anchoredPosition = new Vector2(anchoredX, anchoredY);
        }

        var toggle = cloneGO.GetComponentInChildren<Toggle>(includeInactive: true);
        if (toggle == null)
        {
            Plugin.Log.LogWarning($"[autopilot-ui] cloned row '{cloneName}' has no Toggle; aborting");
            UnityEngine.Object.Destroy(cloneGO);
            return;
        }

        // Full scrub of the cloned Toggle's onValueChanged so clicks on the
        // checkbox don't also fire the source row's prefab-wired handler.
        ScrubUnityEvent(toggle.onValueChanged);

        // Rename the primary (first non-empty) TMP_Text; disable raycast on the
        // rest so they can't intercept anything. Attach LabelClickProxy so
        // clicks on the label have a no-op handler here rather than bubbling.
        var clonedTexts = cloneGO.GetComponentsInChildren<TMP_Text>(includeInactive: true);
        var primary = clonedTexts.FirstOrDefault(t => !string.IsNullOrEmpty(t.text));
        if (primary != null)
        {
            primary.text = labelText;
            var proxy = primary.gameObject.AddComponent<LabelClickProxy>();
            proxy.target = toggle;
        }
        else
        {
            Plugin.Log.LogWarning($"[autopilot-ui] no TMP_Text with non-empty text in clone '{cloneName}'");
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
            UnityEngine.Object.Destroy(translatable);
        }
        // The source row ships TooltipSource on both the label and the Toggle
        // (so hovering anywhere on the row shows the same tooltip). Retarget
        // each of them at our own title + body. TooltipSource accepts raw
        // strings — Translation.TranslateOnly returns the input unchanged when
        // it doesn't start with '@', so plain English just renders as-is.
        foreach (var tooltip in cloneGO.GetComponentsInChildren<TooltipSource>(includeInactive: true))
        {
            tooltip.Title = labelText;
            tooltip.BodyText = tooltipText;
        }

        toggle.SetIsOnWithoutNotify(getValue());
        toggle.onValueChanged.AddListener(isOn =>
        {
            if (getValue() == isOn) return;
            setValue(isOn);
            Plugin.Instance.Config.Save();
        });
    }

    /// <summary>
    /// Wipe both runtime listeners and prefab-serialised persistent calls from
    /// a UnityEvent. The public <c>RemoveAllListeners</c> only clears runtime
    /// listeners; persistent calls survive and keep firing their original
    /// targets. Reach into <c>UnityEventBase.m_PersistentCalls</c> and clear
    /// the internal list to nuke them fully.
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
