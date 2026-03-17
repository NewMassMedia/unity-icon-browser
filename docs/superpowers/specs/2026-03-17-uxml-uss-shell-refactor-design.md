# UXML/USS Shell Refactor Design

**Date:** 2026-03-17
**Status:** Approved in chat, pending written spec review
**Scope:** Move the editor window shell and each tab's static layout from C# UI construction into UXML + USS while keeping runtime-heavy repeated UI in code.

## Goal

Replace constructor-heavy UI Toolkit tree assembly with a declarative structure based on UXML and USS so the window shell and tab layouts are easier to read, maintain, and extend.

## Constraints

- Keep the current dark-style presentation. No light-theme support is required in this batch.
- Keep performance-sensitive and repeated UI in code:
  - `IconGrid`
  - `LibraryListView`
  - global search result cells
  - variant chips/buttons
  - detail-panel repeated variant rows
- Preserve existing behavior for:
  - tab switching
  - shared search field routing
  - browse global search mode
  - settings actions
  - status bar updates
- Avoid broad visual redesign. This is a structural refactor, not a UI refresh.

## Current State

The package already uses a shared stylesheet in [`Editor/IconBrowserWindow.uss`](/Users/maemi/Documents/Git/04.Unity/unity-icon-browser-project/Assets/unity-icon-browser/Editor/IconBrowserWindow.uss), but several views still construct their static hierarchy directly in C#:

- [`Editor/App/IconBrowserWindow.cs`](/Users/maemi/Documents/Git/04.Unity/unity-icon-browser-project/Assets/unity-icon-browser/Editor/App/IconBrowserWindow.cs)
- [`Editor/UI/ProjectTab.cs`](/Users/maemi/Documents/Git/04.Unity/unity-icon-browser-project/Assets/unity-icon-browser/Editor/UI/ProjectTab.cs)
- [`Editor/UI/BrowseTab.cs`](/Users/maemi/Documents/Git/04.Unity/unity-icon-browser-project/Assets/unity-icon-browser/Editor/UI/BrowseTab.cs)
- [`Editor/UI/SettingsTab.cs`](/Users/maemi/Documents/Git/04.Unity/unity-icon-browser-project/Assets/unity-icon-browser/Editor/UI/SettingsTab.cs)

This mixes layout declaration with runtime behavior wiring, which makes tab constructors noisy and increases regression risk when adjusting structure.

## Target Architecture

### Static vs Dynamic Boundary

Use a strict boundary:

- `UXML`: static shells, fixed sections, placeholder hosts, and semantic naming
- `USS`: dark-style layout and presentation rules
- `C#`: querying named nodes, binding events, inserting dynamic child controls, and handling state transitions

This keeps structural intent readable without forcing repeated or virtualized surfaces into clone-tree plumbing.

### UXML Assets

Add these UXML files:

- `Editor/App/IconBrowserWindow.uxml`
- `Editor/UI/ProjectTab.uxml`
- `Editor/UI/BrowseTab.uxml`
- `Editor/UI/SettingsTab.uxml`

Each file owns only one static shell.

### C# Binding Model

Each existing view class remains the behavioral owner of its surface, but stops building the static hierarchy by hand.

- `IconBrowserWindow`
  - loads the window UXML
  - queries tab buttons, search field, tab-content host, and status label by `name`
  - instantiates `ProjectTab`, `BrowseTab`, and `SettingsTab` and mounts them into tab hosts
- `ProjectTab`
  - loads its UXML
  - queries the filter-bar host and body hosts
  - inserts `IconGrid` and `IconDetailPanel`
- `BrowseTab`
  - loads its UXML
  - queries library/info/variant/grid/global-results/detail hosts
  - inserts `LibraryListView`, `IconGrid`, `BrowseGlobalSearchResultsView`, and `IconDetailPanel`
- `SettingsTab`
  - loads its UXML
  - queries path field, warning box, popup rows, and cache buttons
  - binds value-change and click behavior to named controls

### Placeholder Pattern

Every dynamic surface gets a dedicated host element in UXML. Example naming style:

- `project-filter-bar`
- `project-grid-host`
- `project-detail-host`
- `browse-library-host`
- `browse-grid-host`
- `browse-global-results-host`
- `browse-detail-host`
- `settings-root-scroll`

The host exists only to define structure. Runtime controls are still created in code and added to the host.

## File Responsibilities

### New Files

- `Editor/App/IconBrowserWindow.uxml`
  - root shell, tab bar, search slot, content host, status bar
- `Editor/UI/ProjectTab.uxml`
  - filter area and 2-column body shell
- `Editor/UI/BrowseTab.uxml`
  - sidebar + center + detail shell, info bar placeholder, variant bar placeholder
- `Editor/UI/SettingsTab.uxml`
  - static form structure for path/import/cache sections

### Existing Files To Modify

- `Editor/App/IconBrowserWindow.cs`
  - replace manual shell construction with UXML loading and named-node binding
- `Editor/UI/ProjectTab.cs`
  - replace static layout construction with UXML loading and host insertion
- `Editor/UI/BrowseTab.cs`
  - replace static layout construction with UXML loading and host insertion
- `Editor/UI/SettingsTab.cs`
  - replace static form construction with UXML loading and named control binding
- `Editor/IconBrowserWindow.uss`
  - align selectors with UXML classes/names and remove dead structural rules if any

### Explicit Non-Goals For This Batch

Do not convert these to UXML templates in this refactor:

- `Editor/UI/IconGrid.cs`
- `Editor/UI/IconGridVirtualizer.cs`
- `Editor/UI/LibraryListView.cs`
- `Editor/UI/BrowseGlobalSearchResultsView.cs`
- `Editor/UI/IconDetailPanel.cs`

Those areas are dynamic or repeated enough that pushing them into UXML now would expand scope and risk.

## Loading Strategy

Use a small asset-loading helper for UXML/USS lookups under the package's editor path. The helper must fail loudly if an expected asset is missing.

Requirements:

- one clear package-relative path per UXML asset
- no silent fallback to partially built windows
- descriptive error if `VisualTreeAsset` lookup fails

This avoids blank editor windows caused by missing renamed assets.

## Naming Rules

- Give a `name` only to nodes queried directly from code.
- Use USS classes for styling and structural semantics.
- Keep names stable and explicit; do not rely on child index traversal.

This ensures binding code remains robust when layout markup changes slightly.

## Behavior Preservation

The refactor must preserve these behaviors exactly:

- `IconBrowserWindow` still owns search dispatch and tab switching
- Browse search field remains enter-submitted for global search behavior
- Project search remains immediate
- Settings still hides the shared search field
- Browse info bar and variant bar visibility remain state-driven from code
- local/project icon refresh and selection behavior stay unchanged

## Risks

### 1. UXML Load Failures

If a `VisualTreeAsset` is missing or moved, the window can open with an empty UI. Mitigation:

- centralize asset loading
- throw or log a targeted error immediately
- avoid continuing with null trees

### 2. Broken Query Bindings

Moving from direct field construction to `Q()` lookup introduces name-coupling. Mitigation:

- require exact names for all runtime-bound controls
- validate queried nodes up front
- fail fast when required nodes are absent

### 3. USS Regressions

Selectors may stop matching if the hierarchy changes. Mitigation:

- preserve existing class names where practical
- only introduce new classes for new host containers
- manually verify each tab after refactor

### 4. Scope Creep Into Dynamic Surfaces

Once UXML files exist, it is tempting to move repeated cells/buttons too. Mitigation:

- keep repeated and virtualized surfaces explicitly out of scope
- treat them as a later batch only if needed

## Validation Plan

### Manual Verification

1. Open `Window > Icon Browser`
2. Confirm the window renders without missing elements
3. Confirm tab switching still works
4. Confirm shared search field visibility and behavior match current behavior
5. Confirm `Project` tab still shows filter bar, grid, and detail panel
6. Confirm `Browse` tab still shows library sidebar, info/variant bars, grid, global results, and detail panel correctly
7. Confirm `Settings` tab path field, warning, popups, and cache buttons still work
8. Confirm status bar still updates after library loading

### Code-Level Checks

- run `git diff --check`
- review for null-prone `Q()` lookups
- confirm no behavior moved out of current owner classes unless required for asset loading

## Incremental Execution Order

Following the Unity-guide principle of bounded batches, implement in this order:

1. Window shell
2. Settings tab
3. Project tab
4. Browse tab
5. USS cleanup and final verification

Rationale:

- `SettingsTab` has the smallest runtime surface and is the safest first tab conversion
- `ProjectTab` is simpler than `BrowseTab`
- `BrowseTab` is left last because it owns the most stateful visibility and host wiring

## Rollback Strategy

If a batch causes rendering regressions:

- revert only the last converted UXML/C# pair
- leave prior validated batches intact
- do not combine multiple tab migrations into a single unreviewed edit set

## Team-Mode Snapshot

Leader: planning, batch sequencing, risk control, verification, user communication

Ownership for implementation:

- Window shell owner
  - `Editor/App/IconBrowserWindow.cs`
  - `Editor/App/IconBrowserWindow.uxml`
- Settings owner
  - `Editor/UI/SettingsTab.cs`
  - `Editor/UI/SettingsTab.uxml`
- Project owner
  - `Editor/UI/ProjectTab.cs`
  - `Editor/UI/ProjectTab.uxml`
- Browse owner
  - `Editor/UI/BrowseTab.cs`
  - `Editor/UI/BrowseTab.uxml`
- Styling owner
  - `Editor/IconBrowserWindow.uss`

Parallelization note:

- planning/review of separate tab batches is parallelizable
- code edits are serial where they share `Editor/IconBrowserWindow.uss` or shell-loading helpers

## Self-Check

### Hardest decision

The hardest decision was where to stop the UXML split. Repeated UI such as grid cells and global result rows could also be templated, but doing so would blur the line between structure cleanup and rendering-system changes.

### Rejected alternatives

- Shell-only UXML
  - rejected because it removes too little constructor noise
- Deep template split for repeated cells/items
  - rejected because it adds binder complexity and raises regression risk for virtualized or frequently rebuilt UI

### Least certain area

`BrowseTab` is the least certain because it coordinates the most conditional visibility and multiple dynamic child surfaces. It must be validated with real tab switching and global search flows after implementation.
