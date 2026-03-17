# Browse Structure Refactor Design

**Date:** 2026-03-17
**Status:** Approved in chat, pending written spec review
**Scope:** Refactor the Browse flow into clearer feature-oriented folders and file responsibilities, while removing redundant naming prefixes and keeping only meaningful prefixes.

## Goal

Reorganize the Browse feature so that file layout, namespaces, and type names reflect actual responsibilities instead of historical layering or habitual prefixes.

The outcome should be:

- clearer Browse-only boundaries
- smaller files with one dominant reason to change
- names that rely on folder and namespace context instead of repeating it
- a safer base for later `Project` and `Settings` refactors

## Constraints

- This batch is `Browse` only.
- `Project` and `Settings` stay out of scope.
- Existing dirty worktree means edits must be done in bounded serial batches.
- Rename/move changes must stay within a single-owner batch.
- Preserve runtime behavior for Browse search, library switching, preview warming, import/delete actions, and detail updates.

## Current State

The codebase already has a first-pass technical split:

- `Editor/App`
- `Editor/Data`
- `Editor/Import`
- `Editor/Shell`
- `Editor/UI`

But Browse responsibilities still cut across those folders:

- [`BrowseTab.cs`](/Users/maemi/Documents/Git/04.Unity/unity-icon-browser-project/Assets/unity-icon-browser/Editor/UI/BrowseTab.cs) mixes view orchestration, visibility rules, preview warmup coordination, and Browse-specific state transitions.
- [`BrowseDataController.cs`](/Users/maemi/Documents/Git/04.Unity/unity-icon-browser-project/Assets/unity-icon-browser/Editor/UI/BrowseDataController.cs) is stateful logic but still sits beside view files.
- [`SvgPreviewCache.cs`](/Users/maemi/Documents/Git/04.Unity/unity-icon-browser-project/Assets/unity-icon-browser/Editor/Import/SvgPreviewCache.cs) carries preview/cache behavior that the Browse flow depends on heavily.
- [`IconDatabase.cs`](/Users/maemi/Documents/Git/04.Unity/unity-icon-browser-project/Assets/unity-icon-browser/Editor/Data/IconDatabase.cs) still contains responsibility that should be browse-facing or remote-catalog-specific.

This means folder names look cleaner than the actual code boundaries are.

## Target Architecture

Use feature-oriented structure for Browse, with supporting shared types left outside the feature when they are genuinely reusable.

### Browse feature folders

- `Editor/Features/Browse/UI/`
  - visual composition and UI event wiring
- `Editor/Features/Browse/State/`
  - stateful browse/search session logic, grouping, selection-adjacent browse logic
- `Editor/Features/Browse/Preview/`
  - preview warming, tooltip preview support, atlas/cache orchestration that exists to support Browse
- `Editor/Features/Browse/Actions/`
  - optional folder for Browse-triggered import/delete façade if action logic becomes browse-specific

### Shared folders that remain shared

- shared models and contracts stay outside Browse:
  - `IconEntry`
  - `IconLibrary`
  - `IconManifest`
  - `IIconManifest`
  - `IIconifyClient`
- shell policies remain shared only if more than Browse uses them
- generic import pipeline or remote client code stays shared only if it is not Browse-owned behavior

## Naming Rules

### Core rule

If folder and namespace already provide the context, do not repeat that context in the file or type name.

### Keep meaningful prefixes

Prefixes stay only when they add real meaning across boundaries. Examples:

- `IconEntry`
- `IconLibrary`
- `IconManifest`

These remain meaningful because they are shared domain concepts.

### Remove habitual prefixes

If a type lives under a Browse-only folder, remove `Browse` when it merely repeats the location. Examples:

- `BrowseGlobalSearchSession` -> `SearchSession`
- `BrowseGlobalSearchSection` -> `SearchSection`
- `BrowseGlobalSearchSectionBuilder` -> `SearchSectionBuilder`
- `BrowseGlobalSearchResultsView` -> `SearchResultsView`

### Prefer role-centered names over implementation-centered prefixes

Rename only when it improves responsibility clarity. Examples:

- `SvgPreviewCache` should keep `Svg` only if SVG specificity is part of the public contract.
- If not, prefer names like `PreviewCache`, `AtlasPreviewCache`, or `TooltipPreviewCache` based on actual responsibility.

### Keep useful suffixes

Suffixes like these stay because they describe the type's job:

- `View`
- `Controller`
- `Session`
- `Policy`
- `Cache`
- `Importer`
- `Service`

### Namespace rule

Apply the same rule to namespaces:

- prefer `IconBrowser.Features.Browse.State`
- avoid namespaces where the same context is repeated again in the type name unless it adds meaning

## File Responsibility Targets

### Browse UI

These stay UI-focused and should not own browse-state algorithms:

- `BrowseTab`
- `BrowseTab.Actions`
- `LibraryListView`
- `SearchResultsView`

### Browse State

These own Browse state and pure Browse grouping logic:

- `SearchSession`
- `SearchSection`
- `SearchSectionBuilder`
- `BrowseDataController` or renamed equivalent if it becomes a coordinator rather than controller
- search shell policy if it remains Browse-specific

### Browse Preview

These own preview behavior that exists for Browse:

- tooltip preview staging
- active-tab warmup scheduling
- hover-driven prefetch coordination
- any Browse-only preview persistence helper

### Shared Data

These should not absorb Browse UI concerns:

- remote catalog retrieval
- remote search access
- shared manifest/client/model contracts

If `IconDatabase` still owns multiple unrelated concerns after Browse extraction, it should be split further in a later batch.

## Non-Goals

- Do not refactor `Project` flow in this batch.
- Do not refactor `Settings` flow in this batch.
- Do not perform a full package-wide rename cleanup in one pass.
- Do not change public behavior or menu structure in this batch.

## Execution Model

This refactor should be done in bounded serial batches.

### Recommended batch order

1. Inventory and rename map for Browse-owned files and types
2. Browse state extraction and rename batch
3. Preview/cache extraction batch
4. Data boundary cleanup batch
5. Folder/file move batch
6. Verification and handoff

### Why serial, not parallel

- multiple files share the same Browse contracts
- rename/move changes create conflict pressure immediately
- current worktree is already dirty
- a failed mid-refactor rename is more expensive than slower safe execution

## Risks

### 1. Rename ripple risk

File, class, and namespace renames will break references if done without a strict map.

Mitigation:

- write a rename map first
- keep each rename batch narrow
- verify touched references before moving on

### 2. Browse contract drift

`BrowseTab`, preview helpers, and data access currently coordinate implicitly.

Mitigation:

- separate responsibilities before moving files where possible
- avoid changing behavior while renaming
- verify UI-driven state transitions after each batch

### 3. Dirty worktree interference

There are already staged and modified files in related areas.

Mitigation:

- treat Browse structure refactor as a single-owned serial track
- do not touch `Project` or `Settings`
- avoid opportunistic cleanup outside Browse scope

### 4. Over-cleaning names

Removing too many prefixes can make shared types ambiguous.

Mitigation:

- keep prefixes for shared domain concepts
- remove them only where folder and namespace already provide the meaning

## Validation Plan

### Structural checks

- `git diff --check`
- verify moved/renamed files match the approved rename map
- verify no shared domain type lost meaning through over-shortening

### Behavioral checks

When Unity validation is available:

1. open the Icon Browser
2. switch to Browse
3. select libraries
4. run Browse global search
5. inspect preview warmup/tooltip behavior
6. import and delete from Browse
7. verify detail panel and variant strip still respond correctly

## Rollback Strategy

- execute one batch at a time
- if a batch expands beyond Browse scope, stop and mark blocked
- if a rename batch causes widespread breakage, revert only that batch before continuing
- do not combine preview extraction and folder moves in the same patch

## Team-Mode Snapshot

Leader:
- planning
- rename map review
- batch gating
- risk tracking
- verification summary

Browse UI owner:
- `BrowseTab*`
- `LibraryListView`
- `SearchResultsView`

Browse State owner:
- search/session/grouping/controller files

Browse Preview owner:
- preview/cache/tooltip files tied to Browse behavior

Shared Data owner:
- `IconDatabase`
- remote catalog/client helpers touched by Browse extraction

## Self-Check

### Hardest decision

The hardest decision is where to stop removing prefixes. If the rule is too aggressive, shared types become vague. If it is too conservative, the new folder structure still reads like the old one.

### Rejected alternatives

- folder cleanup only
  - rejected because it keeps responsibility overlap intact
- whole-package feature reorg in one pass
  - rejected because current worktree state makes that too risky

### Least certain area

The least certain area is the Browse preview/cache boundary. It is likely the most entangled part of the Browse flow and should be treated as its own batch instead of being folded into generic file moves.
