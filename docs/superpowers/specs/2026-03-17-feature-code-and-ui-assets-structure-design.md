# Feature Code And UI Assets Structure Design

**Date:** 2026-03-17
**Status:** Approved in chat, pending written spec review
**Scope:** Reorganize the whole editor package so code is grouped by feature flow while UXML, USS, and Theme assets are managed under a dedicated `Editor/UI` asset tree.

## Goal

Create a package structure that makes two things obvious at a glance:

- which code belongs to which user-facing feature
- where UI assets live, independent of feature code location

The design must support:

- feature-oriented code ownership
- centralized UI asset management
- clearer naming with only meaningful prefixes
- safer incremental refactors from the current mixed structure

## Core Rule

Use different axes for code and UI assets:

- **Code** is organized by ownership and behavior
- **UI assets** are organized by asset kind

This means a feature can own code in one place while its UXML/USS/Theme files live under a common UI asset tree.

## Target Code Structure

### `Editor/App`

Owns:
- editor window entrypoints
- top-level composition
- menu registration
- app-level asset loading helpers

Examples:
- `IconBrowserWindow`
- `UiAssetLoader`

### `Editor/Features/Browse`

Owns:
- Browse tab behavior
- Browse search/session logic
- Browse preview orchestration
- Browse import/delete UI flow

Suggested internal folders:
- `UI/`
- `State/`
- `Preview/`
- `Actions/`

### `Editor/Features/Project`

Owns:
- Project tab behavior
- local icon filtering
- local selection/delete flow

### `Editor/Features/Settings`

Owns:
- Settings tab behavior
- import-path controls
- cache/settings UI flow

### `Editor/Shared`

Owns only truly cross-feature code.

Candidates:
- shared visual controls
- shared selection/grid helpers
- shared toast/progress helpers

Likely examples:
- `IconGrid`
- `IconGridLayout`
- `IconGridVirtualizer`
- `IconDetailPanel`
- `ToastNotification`
- `EditorProgressHelper`
- `DragSelectionHandler`

This folder should stay small. If a file is effectively Browse-only or Project-only, it should not remain in `Shared`.

### `Editor/Data`

Owns:
- shared models
- shared data contracts
- local/remote catalog access primitives
- remote client and parsing helpers

Examples:
- `IconEntry`
- `IconLibrary`
- `IconManifest`
- `IIconManifest`
- `IIconifyClient`
- `IconifyClient`
- `SimpleJsonParser`
- shared catalog access helpers

### `Editor/Import`

Owns:
- feature-agnostic import pipeline
- atlas generation/import helpers
- Unity importer glue

This folder should contain lower-level import primitives, not Browse-specific orchestration.

## Target UI Asset Structure

All UI assets should live under `Editor/UI`.

### `Editor/UI/UXML`

Owns all UXML files.

Suggested subfolders:
- `Window/`
- `Tabs/`
- `Shared/`

Examples:
- `Editor/UI/UXML/Window/IconBrowserWindow.uxml`
- `Editor/UI/UXML/Tabs/BrowseTab.uxml`
- `Editor/UI/UXML/Tabs/ProjectTab.uxml`
- `Editor/UI/UXML/Tabs/SettingsTab.uxml`

### `Editor/UI/Styles`

Owns shared USS files and non-theme style definitions.

Examples:
- `Editor/UI/Styles/IconBrowserWindow.uss`
- later split files if the stylesheet becomes too large

### `Editor/UI/Themes`

Owns theme-level styling and tokens.

Examples:
- `Editor/UI/Themes/IconBrowserTheme.uss`

Even if the package only supports a dark style today, theme files should live here so visual tokens and future theming are isolated from component/layout rules.

## Naming Rules

### Keep only meaningful prefixes

Keep prefixes only when they add semantic value across folder boundaries.

Examples to keep:
- `IconEntry`
- `IconLibrary`
- `IconManifest`

### Remove repeated folder context

If folder and namespace already say `Browse`, do not repeat it in type names unless it adds meaning.

Examples:
- `BrowseGlobalSearchSession` -> `SearchSession`
- `BrowseGlobalSearchSectionBuilder` -> `SearchSectionBuilder`
- `BrowseGlobalSearchResultsView` -> `SearchResultsView`

### Prefer role names over implementation-history names

Examples:
- `IconBrowserUiAssetLoader` -> `UiAssetLoader`
- `BrowseSearchShellPolicy` -> `SearchShellPolicy` if it is Browse-owned

### Keep useful suffixes

Keep suffixes that explain the job:
- `View`
- `Session`
- `Controller`
- `Policy`
- `Cache`
- `Importer`
- `Service`

## Asset Loading Rule

All UXML/USS/Theme loads should go through one app-level loader.

The loader must:
- use explicit package-relative paths
- fail loudly if the asset is missing
- support the new `Editor/UI/...` structure without search-by-name fallbacks

This keeps asset moves predictable and avoids blank windows caused by implicit lookups.

## Migration Strategy

This refactor should be performed in bounded serial batches.

### Recommended order

1. Move UI assets into `Editor/UI`
2. Update the asset loader and all explicit asset paths
3. Finalize Browse feature code structure
4. Extract `Project` feature code
5. Extract `Settings` feature code
6. Re-home shared UI helpers into `Editor/Shared`
7. Reduce `Data` and `Import` to truly shared responsibilities

### Why this order

- UI asset relocation affects every feature and should stabilize first
- feature code extraction is safer after asset paths stop moving
- `Shared` decisions are easier once feature ownership is clearer

## Risks

### 1. UI asset path breakage

Moving UXML and USS files will break explicit loads if not updated together.

Mitigation:
- move asset files and loader paths in one bounded batch
- verify every moved asset path immediately

### 2. Shared-folder bloat

`Shared` can become a dump folder if not policed.

Mitigation:
- only place files there if at least two features genuinely depend on them
- otherwise keep them in a feature folder

### 3. Dirty worktree interference

Current worktree already has many modifications and moves in progress.

Mitigation:
- serialize writes
- keep each batch narrow
- do not mix asset relocation with unrelated behavior changes

### 4. Namespace churn

Whole-package feature extraction can produce broad reference churn.

Mitigation:
- rename/move one cluster at a time
- verify old references are eliminated before moving on

## Validation Plan

### Structural checks

- `git diff --check`
- verify moved `.meta` files stay paired with their assets
- verify no UXML/USS path still points to old locations

### Behavioral checks

When Unity validation is available:

1. open `Window/Tools/Icon Browser`
2. switch across all tabs
3. verify Browse global search still works
4. verify Project filtering still works
5. verify Settings actions still work
6. verify no UXML asset fails to load

## Rollback Strategy

- move UI assets in their own batch
- move feature code in separate batches
- if a batch breaks asset loading, revert only that batch before continuing
- never combine `Browse`, `Project`, and `Settings` code moves in one unreviewed patch

## Team-Mode Snapshot

Leader:
- inventory
- folder ownership
- batch sequencing
- risk review
- verification summary

UI asset owner:
- `Editor/UI/UXML`
- `Editor/UI/Styles`
- `Editor/UI/Themes`
- asset loader paths

Browse owner:
- `Editor/Features/Browse`

Project owner:
- `Editor/Features/Project`

Settings owner:
- `Editor/Features/Settings`

Shared/Data/Import owner:
- `Editor/Shared`
- `Editor/Data`
- `Editor/Import`

## Self-Check

### Hardest decision

The hardest decision is splitting code ownership and asset ownership on different axes. It is easy to default to “put everything for one feature together,” but that conflicts with the requirement to manage UXML/USS/Theme centrally.

### Rejected alternatives

- keep UXML/USS beside each feature's code
  - rejected because the user explicitly wants UI assets managed separately
- keep the current technical-layer structure and only rename files
  - rejected because it keeps ownership blurry

### Least certain area

The least certain area is the future boundary of `Editor/Shared`. Files like `IconDetailPanel` and `IconGrid` may look shared now, but could still drift toward one feature if the next refactors expose tighter ownership.
