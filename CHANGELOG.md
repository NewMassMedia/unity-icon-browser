# Changelog

All notable changes to this project are documented in this file.

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
