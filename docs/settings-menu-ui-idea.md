# Idea: ECHO options as a category in the game Settings menu

**Status:** shelved (2026-06-23). The in-game UI lives in **Ship → Autopilot** (see
`VGEcho/Patches/AutopilotUIPatches.cs`). This file records an alternative that was
prototyped and parked — not currently shipped.

## The idea

Instead of (or in addition to) cloning rows into the Autopilot side-tab, add a
dedicated **ECHO** category button to the game's main Settings menu
(`Behaviour.UI.Settings.SettingsMenu`), alongside General / Audio / Controls /
Graphics. Clicking it shows a panel listing **all** ECHO options — not just the
two that fit in the Autopilot side-tab, but timing/eta-sync/arrival-snap, the
stack-deposit mode dropdown, refinery-route, auto-refine, and auto-LB-RTR.

Upside: one tidy home for every toggle, room to grow, no fighting the Autopilot
panel's cramped 2-column grid. Downside: it's a *second* place to look, away from
the autopilot controls the options actually affect.

## How it was built (validated against 0.8.1 `Assembly-CSharp.dll`)

- Postfix `SettingsMenu.Start`; find an existing category button by scanning for a
  `Button` whose persistent `onClick` calls one of `GeneralSettings` /
  `AudioSettings` / `ControlSettings` / `GraphicsSettings`; clone it, relabel to
  "ECHO", reassign `onClick`.
- On click: read the private `settingsContainer` (GameObject) field, destroy its
  children, parent in a new panel, register the panel as the active
  `SettingsSubMenu` (via the private `activeMenu` field) so vanilla
  back-navigation tears it down.
- **Controls were cloned, not built from scratch** — harvest real templates from
  the `GeneralSettingsUI` prefab (`SettingsMenu.generalSettingsUI` →
  `travelHintsToggle`, `displayDropdown`). Unity's `Instantiate` remaps a
  control's internal child references onto the clone, so the cloned toggle keeps
  its checkmark and the cloned dropdown keeps a working popup template, with
  native styling. A bare `Toggle`/`TMP_Dropdown` has neither and won't render.

Full implementation is in git history: branch `claude/echo-settings-menu`,
`VGEcho/Patches/SettingsMenuPatches.cs` (commit `cc368f2`).

## Open issue if revived

In testing the panel rendered empty. Prime suspect: `settingsContainer` drives a
layout group, so a child panel with no `LayoutElement` collapses to zero size and
everything inside renders invisibly. Fix direction: add a `LayoutElement`
(flexible width/height) to the panel, or mirror the layout the vanilla sub-panels
rely on. Confirm with the `[settings-diag]` instrumentation (container size +
layout-component probe) that was added on that branch.
