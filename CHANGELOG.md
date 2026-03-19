# Changelog

All notable changes to this project are documented in this file.

## [1.3.3] - 2026-03-19

### Fixed
- `_IconBrowserTemp` is now automatically deleted after all queued preview batches are processed, eliminating the leftover folder after atlas generation.
- Added `[InitializeOnLoad]` startup cleanup to remove any `_IconBrowserTemp` left behind from a previous Unity crash or force quit.

## [1.3.1] - 2026-03-18

### Changed
- Global search submit now runs on the first Enter press instead of requiring a second submit.
- Project tab single-item delete now executes immediately without an extra confirmation dialog.

### Fixed
- Stabilized global search cell previews so loaded icons do not disappear when preview atlases are evicted or prefixes change.
- Reused imported local `VectorImage` assets in remote search results when the icon already exists in the project.
- Removed redundant global search result refresh work during section rebuilds.

## [1.3.0] - 2026-03-02

### Added
- Library sidebar hover tooltip with 3 preview icons.
- Preview sample name persistence cache at `LocalApplicationData/IconBrowser/preview_samples.json`.
- Verbose cache log toggle (`IconBrowserSettings.VerboseCacheLogs`) to suppress noisy cache logs by default.

### Changed
- Reworked tooltip preview pipeline to prioritize fast local/persisted candidates, with remote enrichment in background.
- Background prefetch strategy now prioritizes current/nearby/featured libraries instead of broad full-list warming.
- Browse prefetch now runs only while Browse tab is active.
- Increased in-memory atlas capacity from 16 to 24.

### Performance
- Introduced fixed temp SVG staging slots (`Assets/_IconBrowserTemp/__iconbrowser_slot_###.svg`) to prevent unbounded temp file growth.
- Added incremental temp write/import behavior to skip unchanged SVG rewrites and unnecessary reimports.
- Added worker throttling and tighter prefetch budgets to reduce editor hitches.

### Fixed
- Removed unreachable code warning in background warmup path.
- Reduced false-positive preview payload warnings in normal mode.
