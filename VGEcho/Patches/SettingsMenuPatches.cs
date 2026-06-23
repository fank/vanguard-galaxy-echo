using System;
using System.Linq;
using System.Reflection;
using Behaviour.UI.Settings;
using Behaviour.UI.Tooltip;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VGEcho.Patches;

/// <summary>
/// Adds an "ECHO" category button to the game's settings menu and shows a
/// settings panel with all ECHO config options. Replaces the fragile
/// Autopilot-sidebar row-cloning approach (broken by the 0.8.1 UI restructure).
///
/// Controls are cloned from the vanilla <see cref="GeneralSettingsUI"/> prefab
/// (its <c>travelHintsToggle</c> / <c>displayDropdown</c>) rather than built
/// from scratch: a bare <see cref="Toggle"/> has no checkmark graphic and a
/// bare <see cref="TMP_Dropdown"/> has no template (so it can't open). Unity's
/// Instantiate remaps a control's internal child references onto the clone, so
/// the cloned toggle keeps its checkmark and the cloned dropdown keeps a working
/// popup template — and both inherit the game's native styling for free.
/// </summary>
[HarmonyPatch]
internal static class SettingsMenuPatches
{
    private const string EchoButtonName = "vgecho_settings_button";
    private const string EchoPanelName = "vgecho_settings_panel";
    private const float RowHeight = 44f;
    private const float TopPadding = 16f;

    // SettingsMenu / GeneralSettingsUI fields are private in the game DLL; reach
    // them by reflection (works regardless of the publicized-stub state).
    private static readonly FieldInfo SettingsContainerField =
        AccessTools.Field(typeof(SettingsMenu), "settingsContainer");
    private static readonly FieldInfo ActiveMenuField =
        AccessTools.Field(typeof(SettingsMenu), "activeMenu");
    private static readonly FieldInfo GeneralSettingsUiField =
        AccessTools.Field(typeof(SettingsMenu), "generalSettingsUI");
    private static readonly FieldInfo ToggleTemplateField =
        AccessTools.Field(typeof(GeneralSettingsUI), "travelHintsToggle");
    private static readonly FieldInfo DropdownTemplateField =
        AccessTools.Field(typeof(GeneralSettingsUI), "displayDropdown");

    // Known method names that a settings category button's onClick fires.
    private static readonly string[] CategoryMethodNames =
    {
        "GeneralSettings", "AudioSettings", "ControlSettings", "GraphicsSettings"
    };

    private static TMP_FontAsset? _uiFont;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SettingsMenu), "Start")]
    private static void SettingsMenu_Start_Postfix(SettingsMenu __instance)
    {
        try
        {
            InjectEchoButton(__instance);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[settings-menu] Start postfix failed: {e}");
        }
    }

    private static void InjectEchoButton(SettingsMenu menu)
    {
        var root = menu.gameObject;

        Button? navButton = FindNavButton(root);
        if (navButton == null)
        {
            Plugin.Log.LogWarning("[settings-menu] no nav button found; skipping ECHO button injection");
            return;
        }

        Transform? parent = navButton.transform.parent;
        if (parent == null) return;

        // Idempotency: the clone lives under `parent` (the nav-button container),
        // not under the SettingsMenu root — check there or the guard never matches.
        if (parent.Find(EchoButtonName) != null) return;

        var cloneGO = UnityEngine.Object.Instantiate(navButton.gameObject, parent);
        cloneGO.name = EchoButtonName;
        cloneGO.transform.SetAsLastSibling();

        foreach (var t in cloneGO.GetComponentsInChildren<TMP_Text>(includeInactive: true))
        {
            if (t.text?.Length > 0)
            {
                t.text = "ECHO";
                break;
            }
        }

        // Drop Translatable so it doesn't restore the original label on enable.
        foreach (var tr in cloneGO.GetComponentsInChildren<Behaviour.Util.Translatable>(includeInactive: true))
        {
            UnityEngine.Object.Destroy(tr);
        }

        var button = cloneGO.GetComponentInChildren<Button>(includeInactive: true);
        if (button != null)
        {
            // Fresh event drops the prefab-serialised (persistent) calls too.
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => ShowEchoSettings(menu));
        }

        Plugin.Log.LogInfo("[settings-menu] ECHO settings category injected");
    }

    // Find any Button whose persistent onClick calls one of the known category
    // methods; cache its label font so our panel matches the rest of the menu.
    private static Button? FindNavButton(GameObject root)
    {
        foreach (var btn in root.GetComponentsInChildren<Button>(includeInactive: true))
        {
            int count = btn.onClick.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
            {
                if (!CategoryMethodNames.Contains(btn.onClick.GetPersistentMethodName(i))) continue;

                foreach (var t in btn.GetComponentsInChildren<TMP_Text>(includeInactive: true))
                {
                    if (t.font != null) { _uiFont = t.font; break; }
                }
                return btn;
            }
        }
        return null;
    }

    private static void ShowEchoSettings(SettingsMenu menu)
    {
        // Invoked from the button's onClick, outside the Start postfix try/catch —
        // guard everything so a failure logs cleanly instead of throwing into Unity.
        try
        {
            ShowEchoSettingsCore(menu);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[settings-menu] ShowEchoSettings failed: {e}");
        }
    }

    private static void ShowEchoSettingsCore(SettingsMenu menu)
    {
        if (SettingsContainerField == null || ActiveMenuField == null)
        {
            Plugin.Log.LogError("[settings-menu] settingsContainer/activeMenu field not found; cannot show ECHO settings");
            return;
        }

        var container = (GameObject?)SettingsContainerField.GetValue(menu);
        if (container == null) return;

        for (int i = container.transform.childCount - 1; i >= 0; i--)
        {
            UnityEngine.Object.Destroy(container.transform.GetChild(i).gameObject);
        }

        var panelGO = new GameObject(EchoPanelName, typeof(RectTransform));
        var panelRT = (RectTransform)panelGO.transform;
        panelGO.transform.SetParent(container.transform, false);
        panelRT.anchorMin = Vector2.zero;
        panelRT.anchorMax = Vector2.one;
        panelRT.pivot = new Vector2(0.5f, 0.5f);
        panelRT.anchoredPosition = Vector2.zero;
        panelRT.sizeDelta = Vector2.zero;

        // Register as the active sub-menu so vanilla back-navigation cleans us up.
        var sub = panelGO.AddComponent<EchoSettingsSubmenu>();
        sub.SetSettingsMenu(menu);
        ActiveMenuField.SetValue(menu, sub);

        // Harvest native control templates from the General-settings prefab.
        Toggle? toggleTemplate = null;
        TMP_Dropdown? dropdownTemplate = null;
        var genPrefab = GeneralSettingsUiField?.GetValue(menu);
        if (genPrefab != null)
        {
            toggleTemplate = ToggleTemplateField?.GetValue(genPrefab) as Toggle;
            dropdownTemplate = DropdownTemplateField?.GetValue(genPrefab) as TMP_Dropdown;
        }
        if (toggleTemplate == null)
            Plugin.Log.LogWarning("[settings-menu] toggle template not found; using bare toggles");
        if (dropdownTemplate == null)
            Plugin.Log.LogWarning("[settings-menu] dropdown template not found; using cycle button");

        var cfg = Plugin.Instance;
        float y = TopPadding;

        AddToggle(panelGO, toggleTemplate, "Timing tweaks",
            "Master enable for ETA-sync and arrival-snap. When off, the vanilla 12s idle cycle runs unchanged.",
            () => cfg.CfgAutopilotTiming.Value,
            v => { cfg.CfgAutopilotTiming.Value = v; cfg.Config.Save(); },
            ref y);

        AddToggle(panelGO, toggleTemplate, "ETA-synchronized progress circle",
            "While warping, drive the idle cycle timer from the ship's travel progress instead of the vanilla 12s loop.",
            () => cfg.CfgAutopilotEtaSync.Value,
            v => { cfg.CfgAutopilotEtaSync.Value = v; cfg.Config.Save(); },
            ref y);

        AddToggle(panelGO, toggleTemplate, "Arrival-snap",
            "Zero the cycle timer on final waypoint so the next task fires immediately instead of waiting up to 12s.",
            () => cfg.CfgAutopilotArrivalSnap.Value,
            v => { cfg.CfgAutopilotArrivalSnap.Value = v; cfg.Config.Save(); },
            ref y);

        AddDropdown(panelGO, dropdownTemplate, "Stack-aware deposits",
            "Controls how each autopilot deposit cycle moves cargo (non-ammo/non-currency items only).",
            new[] { "Off", "Tiered (default)", "Always" },
            () => cfg.CfgAutopilotStackDepositMode.Value switch
            {
                Patches.StackDepositMode.Off => 0,
                Patches.StackDepositMode.Tiered => 1,
                Patches.StackDepositMode.Always => 2,
                _ => 0
            },
            v =>
            {
                cfg.CfgAutopilotStackDepositMode.Value = v switch
                {
                    0 => Patches.StackDepositMode.Off,
                    2 => Patches.StackDepositMode.Always,
                    _ => Patches.StackDepositMode.Tiered
                };
                cfg.Config.Save();
            },
            ref y);

        AddToggle(panelGO, toggleTemplate, "Refinery-route diversion",
            "When cargo contains ore, divert to the nearest station with a refinery instead of returning home.",
            () => cfg.CfgAutopilotRefineryRoute.Value,
            v => { cfg.CfgAutopilotRefineryRoute.Value = v; cfg.Config.Save(); },
            ref y);

        AddToggle(panelGO, toggleTemplate, "Auto-refine on dock",
            "Turn on the station's Auto-Refine toggle when docking at a station with a refinery.",
            () => cfg.CfgAutopilotAutoRefine.Value,
            v => { cfg.CfgAutopilotAutoRefine.Value = v; cfg.Config.Save(); },
            ref y);

        AddToggle(panelGO, toggleTemplate, "Auto LB-RTR Bot",
            "Auto-fire the LB-RTR Bot salvage ability at the closest in-range wreck while autopilot is engaged.",
            () => cfg.CfgAutopilotAutoLbrtr.Value,
            v => { cfg.CfgAutopilotAutoLbrtr.Value = v; cfg.Config.Save(); },
            ref y);

        Plugin.Log.LogInfo("[settings-menu] ECHO settings panel shown");
    }

    // Full-width row anchored to the panel top, stacking downward as y grows.
    private static GameObject NewRow(GameObject panel, string label, ref float y)
    {
        var safeName = label.Replace(" ", "").Replace("-", "");
        var rowGO = new GameObject($"Row_{safeName}", typeof(RectTransform));
        var rowRT = (RectTransform)rowGO.transform;
        rowGO.transform.SetParent(panel.transform, false);
        rowRT.anchorMin = new Vector2(0f, 1f);
        rowRT.anchorMax = new Vector2(1f, 1f);
        rowRT.pivot = new Vector2(0.5f, 1f);
        rowRT.anchoredPosition = new Vector2(0f, -y);
        rowRT.sizeDelta = new Vector2(0f, RowHeight);
        y += RowHeight;
        return rowGO;
    }

    private static TMP_Text AddLabel(GameObject row, string text, float leftMargin, float rightMargin)
    {
        var labelGO = new GameObject("Label", typeof(RectTransform));
        var labelRT = (RectTransform)labelGO.transform;
        labelGO.transform.SetParent(row.transform, false);
        labelRT.anchorMin = Vector2.zero;
        labelRT.anchorMax = Vector2.one;
        labelRT.pivot = new Vector2(0.5f, 0.5f);
        // Constrain the actual rect so the label's raycast area leaves the toggle
        // and dropdown clear, rather than insetting only the text via tmp.margin.
        labelRT.offsetMin = new Vector2(leftMargin, 0f);
        labelRT.offsetMax = new Vector2(-rightMargin, 0f);

        var tmp = labelGO.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        if (_uiFont != null) tmp.font = _uiFont;
        tmp.fontSize = 16;
        tmp.alignment = TextAlignmentOptions.Left;
        tmp.color = new Color32(245, 245, 245, 230);
        tmp.raycastTarget = true;
        return tmp;
    }

    private static void AddTooltip(GameObject row, string title, string body)
    {
        var ts = row.AddComponent<TooltipSource>();
        ts.Title = title;
        ts.BodyText = body;
    }

    private static void AddToggle(GameObject panel, Toggle? template, string label, string? tooltip,
        Func<bool> getValue, Action<bool> setValue, ref float y)
    {
        var row = NewRow(panel, label, ref y);

        Toggle toggle;
        if (template != null)
        {
            var clone = UnityEngine.Object.Instantiate(template.gameObject, row.transform);
            clone.name = "Toggle";
            clone.SetActive(true);
            StripTranslatable(clone);
            StripTexts(clone); // drop the vanilla in-toggle label; we add our own
            toggle = clone.GetComponent<Toggle>();
            toggle.group = null; // never join the vanilla ToggleGroup
        }
        else
        {
            var go = new GameObject("Toggle", typeof(RectTransform));
            go.transform.SetParent(row.transform, false);
            toggle = go.AddComponent<Toggle>();
        }

        var tRT = (RectTransform)toggle.transform;
        tRT.anchorMin = new Vector2(0f, 0.5f);
        tRT.anchorMax = new Vector2(0f, 0.5f);
        tRT.pivot = new Vector2(0f, 0.5f);
        tRT.anchoredPosition = new Vector2(12f, 0f);
        tRT.sizeDelta = new Vector2(26f, 26f);

        toggle.onValueChanged = new Toggle.ToggleEvent();
        toggle.SetIsOnWithoutNotify(getValue());
        toggle.onValueChanged.AddListener(isOn => setValue(isOn));

        var labelTmp = AddLabel(row, label, leftMargin: 48f, rightMargin: 8f);
        // Click anywhere on the label flips the toggle (label is a sibling of the
        // toggle, so the toggle's own clicks don't bubble here — no double flip).
        labelTmp.gameObject.AddComponent<SettingsRowClickToToggle>().target = toggle;

        if (tooltip != null) AddTooltip(row, label, tooltip);
    }

    private static void AddDropdown(GameObject panel, TMP_Dropdown? template, string label, string? tooltip,
        string[] options, Func<int> getValue, Action<int> setValue, ref float y)
    {
        var row = NewRow(panel, label, ref y);
        AddLabel(row, label, leftMargin: 12f, rightMargin: 250f);
        if (tooltip != null) AddTooltip(row, label, tooltip);

        if (template == null)
        {
            AddCycleButton(row, options, getValue, setValue);
            return;
        }

        var clone = UnityEngine.Object.Instantiate(template.gameObject, row.transform);
        clone.name = "Dropdown";
        clone.SetActive(true);
        StripTranslatable(clone); // keep caption/item texts; just stop re-localization

        var dd = clone.GetComponent<TMP_Dropdown>();
        var ddRT = (RectTransform)dd.transform;
        ddRT.anchorMin = new Vector2(1f, 0.5f);
        ddRT.anchorMax = new Vector2(1f, 0.5f);
        ddRT.pivot = new Vector2(1f, 0.5f);
        ddRT.anchoredPosition = new Vector2(-12f, 0f);
        ddRT.sizeDelta = new Vector2(230f, 32f);

        dd.onValueChanged = new TMP_Dropdown.DropdownEvent();
        dd.ClearOptions();
        dd.AddOptions(options.ToList());
        dd.SetValueWithoutNotify(getValue());
        dd.RefreshShownValue();
        dd.onValueChanged.AddListener(v => setValue(v));
    }

    // Deterministic fallback when no dropdown template is available: a button
    // whose label cycles Off → Tiered → Always on each click.
    private static void AddCycleButton(GameObject row, string[] options,
        Func<int> getValue, Action<int> setValue)
    {
        var btnGO = new GameObject("CycleButton", typeof(RectTransform), typeof(Image), typeof(Button));
        var btnRT = (RectTransform)btnGO.transform;
        btnGO.transform.SetParent(row.transform, false);
        btnRT.anchorMin = new Vector2(1f, 0.5f);
        btnRT.anchorMax = new Vector2(1f, 0.5f);
        btnRT.pivot = new Vector2(1f, 0.5f);
        btnRT.anchoredPosition = new Vector2(-12f, 0f);
        btnRT.sizeDelta = new Vector2(230f, 32f);
        btnGO.GetComponent<Image>().color = new Color32(255, 255, 255, 30);

        var txtGO = new GameObject("Text", typeof(RectTransform));
        var txtRT = (RectTransform)txtGO.transform;
        txtGO.transform.SetParent(btnGO.transform, false);
        txtRT.anchorMin = Vector2.zero;
        txtRT.anchorMax = Vector2.one;
        txtRT.sizeDelta = Vector2.zero;
        var txt = txtGO.AddComponent<TextMeshProUGUI>();
        if (_uiFont != null) txt.font = _uiFont;
        txt.fontSize = 15;
        txt.alignment = TextAlignmentOptions.Center;
        txt.color = new Color32(245, 245, 245, 240);
        txt.text = options[Mathf.Clamp(getValue(), 0, options.Length - 1)];

        btnGO.GetComponent<Button>().onClick.AddListener(() =>
        {
            int next = (getValue() + 1) % options.Length;
            setValue(next);
            txt.text = options[next];
        });
    }

    private static void StripTranslatable(GameObject go)
    {
        foreach (var tr in go.GetComponentsInChildren<Behaviour.Util.Translatable>(includeInactive: true))
        {
            UnityEngine.Object.Destroy(tr);
        }
    }

    private static void StripTexts(GameObject go)
    {
        foreach (var t in go.GetComponentsInChildren<TMP_Text>(includeInactive: true))
        {
            UnityEngine.Object.Destroy(t.gameObject);
        }
    }
}

/// <summary>
/// Minimal concrete <see cref="SettingsSubMenu"/> so the Echo panel can be the
/// active settings menu and ride the vanilla back-navigation teardown.
/// </summary>
public class EchoSettingsSubmenu : SettingsSubMenu { }

/// <summary>Clicking the label flips its toggle, matching vanilla settings UX.</summary>
public class SettingsRowClickToToggle : MonoBehaviour, IPointerClickHandler
{
    public Toggle target = null!;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (target != null) target.isOn = !target.isOn;
    }
}
