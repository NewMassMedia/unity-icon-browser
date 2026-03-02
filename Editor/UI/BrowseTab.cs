using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using IconBrowser.Data;
using IconBrowser.Import;

namespace IconBrowser.UI
{
    /// <summary>
    /// Browse tab — search and import icons from Iconify API.
    /// Left library list, center icon grid, right detail panel.
    /// Delegates data operations (loading, filtering, grouping) to BrowseDataController.
    /// </summary>
    internal partial class BrowseTab : VisualElement
    {
        [Serializable]
        private sealed class PreviewSampleCacheItem
        {
            public string prefix;
            public string[] names;
        }

        [Serializable]
        private sealed class PreviewSampleCacheStore
        {
            public List<PreviewSampleCacheItem> items = new();
        }

        /// <summary>
        /// 10 curated libraries covering diverse icon styles.
        /// </summary>
        private static readonly string[] FEATURED_PREFIXES =
        {
            "lucide",             // Clean line icons (most popular modern UI)
            "heroicons",          // Tailwind CSS team, solid/outline
            "ph",                 // Phosphor — flexible, 6 weights
            "tabler",             // 5000+ open source line icons
            "material-symbols",   // Google Material Symbols
            "mdi",                // Material Design Icons (community, 7000+)
            "bi",                 // Bootstrap Icons
            "ri",                 // Remix Icon — neutral style
            "iconoir",            // Minimalist SVG icons
            "carbon",             // IBM Carbon design system
        };

        private readonly IconDatabase _db;
        private readonly SvgPreviewCache _previewCache;
        private readonly BrowseDataController _dc;
        private readonly IconOperationService _ops;
        private readonly IconGrid _grid;
        private readonly IconDetailPanel _detail;
        private readonly LibraryListView _libraryList;
        private ToastNotification _toast;

        private List<IconLibrary> _allLibraries = new();
        private readonly Dictionary<string, List<string>> _libraryPreviewNameCache = new();
        private readonly Dictionary<string, Task<List<string>>> _libraryPreviewNameTasks = new();
        private readonly Dictionary<string, Task> _previewCacheLoadTasks = new();
        private readonly Dictionary<string, List<string>> _persistedPreviewNameCache = new(StringComparer.Ordinal);
        private readonly HashSet<string> _alwaysWarmLibraryPrefixes = new();
        private readonly HashSet<string> _prefetchedTooltipPrefixes = new();
        private readonly List<string> _libraryDisplayOrder = new();
        private readonly Queue<string> _tooltipPrefetchQueue = new();
        private readonly HashSet<string> _tooltipPrefetchQueued = new();
        private bool _isPreviewSampleCacheLoaded;
        private bool _isPreviewSampleCacheDirty;
        private bool _isTooltipPrefetchWorkerRunning;
        private int _prefetchGeneration;
        private string _lastHoveredPrefix;

        private static readonly string[][] LIBRARY_COMMON_PREVIEW_ALIASES =
        {
            new[] { "home", "house", "dashboard" },
            new[] { "user", "account", "person", "profile" },
            new[] { "search", "magnify", "zoom", "find" },
            new[] { "settings", "cog", "gear", "sliders" },
            new[] { "heart", "favorite", "like" },
            new[] { "star", "bookmark" },
            new[] { "check", "checkmark", "done" },
            new[] { "bell", "notification", "alert" }
        };

        private static readonly string[] LIBRARY_COMMON_PREVIEW_REMOTE_BASES =
        {
            "home",
            "account",
            "search"
        };

        private static readonly string[] PREVIEW_VARIANT_SUFFIXES =
        {
            "-outline-rounded",
            "-outline-sharp",
            "-20-solid",
            "-16-solid",
            "-duotone",
            "-outline",
            "-rounded",
            "-filled",
            "-solid",
            "-sharp",
            "-light",
            "-bold",
            "-thin",
            "-line",
            "-fill"
        };

        private const int HOVER_PREFETCH_RADIUS = 10;
        private const int HOVER_PREFETCH_PREFIX_BUDGET = 4;
        private const int WARM_PREFETCH_PREFIX_BUDGET = 5;
        private const int BACKGROUND_PREFETCH_PREFIX_BUDGET = 8;
        private const int PREFETCH_SAMPLE_BUDGET = 3;
        private const int PROTECTED_FEATURED_PREFIX_COUNT = 3;
        private const int HOVER_REMOTE_RESOLVE_BUDGET_MS = 220;
        private const int PREFETCH_WORKER_THROTTLE_MS = 24;
        private const int PREVIEW_NAME_CACHE_LIMIT = 24;
        private static readonly string PREVIEW_SAMPLE_CACHE_PATH = Path.Combine(IconAtlas.CacheDir, "preview_samples.json");

        // Library info bar
        private readonly VisualElement _libraryInfoBar;
        private readonly Label _libraryInfoName;
        private readonly Button _libraryInfoAuthor;
        private readonly Label _libraryInfoLicense;
        private string _libraryInfoUrl;

        // Variant filter tabs
        private readonly VisualElement _variantBar;

        private string _searchQuery = "";
        private bool _isInitialized;
        private bool _isTabActive;

        private IVisualElementScheduledItem _debounceHandle;
        private IVisualElementScheduledItem _scrollDebounceHandle;
        private IVisualElementScheduledItem _hoverPrefetchHandle;
        private IVisualElementScheduledItem _warmupHandle;
        private IVisualElementScheduledItem _fullWarmupHandle;
        private IVisualElementScheduledItem _previewCacheSaveHandle;
        private bool _shouldImmediateLoad;
        private CancellationTokenSource _cts = new();

        public event Action OnIconImported = delegate { };

        /// <summary>
        /// Current library prefix.
        /// </summary>
        public string CurrentPrefix => _dc.CurrentPrefix;

        /// <summary>
        /// Sets the toast notification element for user-facing feedback.
        /// </summary>
        internal void SetToast(ToastNotification toast) => _toast = toast;

        /// <summary>
        /// Syncs IsImported state of all cached entries with the database.
        /// </summary>
        public void SyncImportState()
        {
            _dc.SyncImportState();
            _grid.RefreshPreviews();
        }

        public BrowseTab(IconDatabase db, SvgPreviewCache previewCache)
        {
            _db = db;
            _previewCache = previewCache;
            _dc = new BrowseDataController(db);
            UpdateProtectedPreviewPrefixes(_dc.CurrentPrefix);
            _ops = new IconOperationService(db, IconImporter.Default);
            AddToClassList("icon-tab");

            // Wire data controller events
            _dc.OnEntriesChanged -= OnDataEntriesChanged;
            _dc.OnEntriesChanged += OnDataEntriesChanged;
            _dc.OnLoadingChanged -= ShowLoading;
            _dc.OnLoadingChanged += ShowLoading;
            _dc.OnPendingSearchDrained -= OnPendingSearchDrained;
            _dc.OnPendingSearchDrained += OnPendingSearchDrained;

            var body = new VisualElement();
            body.AddToClassList("icon-tab__body");
            Add(body);

            // Library list (left)
            _libraryList = new LibraryListView(FEATURED_PREFIXES);
            _libraryList.OnLibrarySelected -= OnLibraryClicked;
            _libraryList.OnLibrarySelected += OnLibraryClicked;
            _libraryList.OnLibraryHovered -= OnLibraryHovered;
            _libraryList.OnLibraryHovered += OnLibraryHovered;
            _libraryList.ResolveLibraryPreviewsAsync = ResolveLibraryPreviewsAsync;
            body.Add(_libraryList);

            // Center column (info + variant tabs + grid)
            var centerColumn = new VisualElement();
            centerColumn.AddToClassList("browse-tab__center");
            body.Add(centerColumn);

            // Library info bar
            _libraryInfoBar = new VisualElement();
            _libraryInfoBar.AddToClassList("browse-tab__info-bar");
            _libraryInfoBar.style.display = DisplayStyle.None;
            centerColumn.Add(_libraryInfoBar);

            _libraryInfoName = new Label();
            _libraryInfoName.AddToClassList("browse-tab__info-name");
            _libraryInfoBar.Add(_libraryInfoName);

            _libraryInfoAuthor = new Button(() =>
            {
                if (!string.IsNullOrEmpty(_libraryInfoUrl))
                    Application.OpenURL(_libraryInfoUrl);
            });
            _libraryInfoAuthor.AddToClassList("browse-tab__info-author");
            _libraryInfoAuthor.style.display = DisplayStyle.None;
            _libraryInfoBar.Add(_libraryInfoAuthor);

            _libraryInfoLicense = new Label();
            _libraryInfoLicense.AddToClassList("browse-tab__info-meta");
            _libraryInfoLicense.style.display = DisplayStyle.None;
            _libraryInfoBar.Add(_libraryInfoLicense);

            _variantBar = new VisualElement();
            _variantBar.AddToClassList("browse-tab__variant-bar");
            _variantBar.style.display = DisplayStyle.None;
            centerColumn.Add(_variantBar);

            _grid = new IconGrid { GroupByAlpha = true };
            centerColumn.Add(_grid);

            // Detail panel (right)
            _detail = new IconDetailPanel();
            body.Add(_detail);

            _grid.ShowActionButtons = true;
            _grid.OnIconSelected -= OnSelected;
            _grid.OnIconSelected += OnSelected;
            _grid.OnVisibleRangeChanged -= OnVisibleRangeChangedDebounced;
            _grid.OnVisibleRangeChanged += OnVisibleRangeChangedDebounced;
            _grid.OnSelectionChanged -= OnGridSelectionChanged;
            _grid.OnSelectionChanged += OnGridSelectionChanged;
            _grid.OnQuickImportClicked -= OnImport;
            _grid.OnQuickImportClicked += OnImport;
            _grid.OnQuickDeleteClicked -= OnQuickDelete;
            _grid.OnQuickDeleteClicked += OnQuickDelete;
            _detail.OnImportClicked -= OnImport;
            _detail.OnImportClicked += OnImport;
            _detail.OnDeleteClicked -= OnQuickDelete;
            _detail.OnDeleteClicked += OnQuickDelete;
            _detail.OnVariantSelected -= OnVariantSelected;
            _detail.OnVariantSelected += OnVariantSelected;
            _detail.OnBatchImportClicked -= OnBatchImport;
            _detail.OnBatchImportClicked += OnBatchImport;
            _detail.OnBatchDeleteClicked -= OnBatchDelete;
            _detail.OnBatchDeleteClicked += OnBatchDelete;
        }

        /// <summary>
        /// Called when BrowseDataController updates entries after filtering/loading.
        /// Handles UI concerns: preview cache assignment and grid update.
        /// </summary>
        private void OnDataEntriesChanged()
        {
            foreach (var entry in _dc.FilteredEntries)
            {
                if (entry.PreviewSprite != null) continue;
                if (entry.IsImported && entry.LocalAsset != null) continue;
                var sprite = _previewCache.GetPreview(entry.Prefix, entry.Name);
                if (sprite != null) entry.PreviewSprite = sprite;
            }

            _shouldImmediateLoad = true;
            _grid.SetItems(_dc.GroupedEntries);
        }

        /// <summary>
        /// Populates the library list from loaded library data.
        /// </summary>
        public void SetLibraries(List<IconLibrary> libraries)
        {
            _allLibraries = libraries;
            EnsurePreviewSampleCacheLoaded();
            BuildLibraryDisplayOrder(libraries);
            BuildAlwaysWarmLibraryPrefixSet(libraries);
            _lastHoveredPrefix = _dc.CurrentPrefix;
            UpdateProtectedPreviewPrefixes(_dc.CurrentPrefix);
            _prefetchedTooltipPrefixes.Clear();
            _tooltipPrefetchQueue.Clear();
            _tooltipPrefetchQueued.Clear();
            _prefetchGeneration++;
            _libraryList.SetLibraries(libraries);
            _libraryList.HighlightLibrary(_dc.CurrentPrefix);
            if (_isTabActive)
            {
                WarmAlwaysTooltipPreviews();
                WarmAllTooltipPreviewsInBackground();
            }
        }

        public void SetTabActive(bool active)
        {
            if (_isTabActive == active)
                return;

            _isTabActive = active;
            if (!_isTabActive)
            {
                PauseBackgroundPrefetch();
                _previewCache.SetProtectedPrefixes(Array.Empty<string>());
                return;
            }

            UpdateProtectedPreviewPrefixes(_dc.CurrentPrefix);
            WarmAlwaysTooltipPreviews();
            WarmAllTooltipPreviewsInBackground();
        }

        private void OnLibraryHovered(string prefix)
        {
            if (!_isTabActive)
                return;
            if (string.IsNullOrEmpty(prefix) || _libraryDisplayOrder.Count == 0)
                return;

            _lastHoveredPrefix = prefix;
            UpdateProtectedPreviewPrefixes(_dc.CurrentPrefix);

            var index = _libraryDisplayOrder.IndexOf(prefix);
            if (index < 0)
                return;

            var neighborPrefixes = BuildHoverPrefetchOrder(index, prefix);

            _hoverPrefetchHandle?.Pause();
            _hoverPrefetchHandle = schedule.Execute(() =>
                EnqueueTooltipPrefetch(neighborPrefixes, HOVER_PREFETCH_PREFIX_BUDGET))
                .StartingIn(40);
        }

        private void OnLibraryClicked(string prefix)
        {
            if (prefix == _dc.CurrentPrefix && _isInitialized) return;

            // Cancel any in-flight async operations from the previous library
            ResetRequestCts();

            _libraryList.HighlightLibrary(prefix);
            _detail.Clear();
            _previewCache.ClearMemoryCache();
            _tooltipPrefetchQueue.Clear();
            _tooltipPrefetchQueued.Clear();
            _prefetchGeneration++;
            _lastHoveredPrefix = prefix;
            UpdateProtectedPreviewPrefixes(prefix);
            _searchQuery = "";
            _isInitialized = true;

            UpdateLibraryInfo(prefix);
            RebuildVariantBar(prefix);
            var ct = _cts.Token;
            AsyncHelper.FireAndForget(IgnoreOperationCanceledAsync(_dc.SetPrefixAndLoadAsync(prefix, ct)));
        }

        private void UpdateLibraryInfo(string prefix)
        {
            var lib = _allLibraries.FirstOrDefault(l => l.Prefix == prefix);
            if (lib == null)
            {
                _libraryInfoBar.style.display = DisplayStyle.None;
                return;
            }

            _libraryInfoBar.style.display = DisplayStyle.Flex;
            _libraryInfoName.text = lib.Name;

            // Author as clickable link
            _libraryInfoUrl = lib.AuthorUrl;
            if (!string.IsNullOrEmpty(lib.Author))
            {
                _libraryInfoAuthor.text = lib.Author;
                _libraryInfoAuthor.style.display = DisplayStyle.Flex;
                _libraryInfoAuthor.EnableInClassList("browse-tab__info-author--link",
                    !string.IsNullOrEmpty(_libraryInfoUrl));
            }
            else
            {
                _libraryInfoAuthor.style.display = DisplayStyle.None;
            }

            // License
            var license = !string.IsNullOrEmpty(lib.LicenseSpdx) ? lib.LicenseSpdx : lib.License;
            if (!string.IsNullOrEmpty(license))
            {
                _libraryInfoLicense.text = $"License : {license}";
                _libraryInfoLicense.style.display = DisplayStyle.Flex;
            }
            else
            {
                _libraryInfoLicense.style.display = DisplayStyle.None;
            }
        }

        private async Task<IReadOnlyList<LibraryListView.LibraryPreviewItem>> ResolveLibraryPreviewsAsync(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return Array.Empty<LibraryListView.LibraryPreviewItem>();

            var candidateNames = await GetOrLoadPreviewSampleNamesAsync(prefix, allowRemote: false);
            var resolvedNames = candidateNames.Count > 0
                ? await ResolveRenderablePreviewNamesAsync(prefix, candidateNames, targetCount: PREFETCH_SAMPLE_BUDGET)
                : new List<string>();

            // If local/persisted candidates are insufficient, keep UX responsive with a short time budget.
            // A full remote resolution continues in the background for subsequent hovers.
            if (resolvedNames.Count < PREFETCH_SAMPLE_BUDGET)
            {
                EnqueueTooltipPrefetch(new[] { prefix }, 1);

                List<string> remoteCandidates = null;
                try
                {
                    var remoteTask = GetOrLoadPreviewSampleNamesAsync(prefix, allowRemote: true);
                    if (remoteTask.IsCompleted)
                    {
                        remoteCandidates = remoteTask.Result;
                    }
                    else
                    {
                        var completed = await Task.WhenAny(remoteTask, Task.Delay(HOVER_REMOTE_RESOLVE_BUDGET_MS));
                        if (completed == remoteTask)
                            remoteCandidates = remoteTask.Result;
                    }
                }
                catch
                {
                    remoteCandidates = null;
                }

                if (remoteCandidates != null && remoteCandidates.Count > 0)
                {
                    candidateNames = MergePreviewCandidates(candidateNames, remoteCandidates);
                    resolvedNames = await ResolveRenderablePreviewNamesAsync(prefix, candidateNames, targetCount: PREFETCH_SAMPLE_BUDGET);
                }
            }

            var namesForTooltip = resolvedNames.Count > 0
                ? resolvedNames
                : candidateNames.Take(PREFETCH_SAMPLE_BUDGET).ToList();
            if (namesForTooltip.Count == 0)
                namesForTooltip = BuildLocalFallbackCandidateNames(PREFETCH_SAMPLE_BUDGET);

            _previewCache.WarmTooltipCache(prefix, namesForTooltip);
            return BuildPreviewItems(prefix, namesForTooltip);
        }

        private List<string> BuildHoverPrefetchOrder(int centerIndex, string centerPrefix)
        {
            var ordered = new List<string>(HOVER_PREFETCH_RADIUS * 2 + 1) { centerPrefix };
            for (int distance = 1; distance <= HOVER_PREFETCH_RADIUS; distance++)
            {
                var left = centerIndex - distance;
                if (left >= 0)
                    ordered.Add(_libraryDisplayOrder[left]);

                var right = centerIndex + distance;
                if (right < _libraryDisplayOrder.Count)
                    ordered.Add(_libraryDisplayOrder[right]);
            }
            return ordered;
        }

        private void EnqueueTooltipPrefetch(IEnumerable<string> prefixes, int prefixBudget)
        {
            if (!_isTabActive)
                return;
            if (prefixes == null || prefixBudget <= 0)
                return;

            var queued = 0;
            foreach (var prefix in prefixes)
            {
                if (queued >= prefixBudget)
                    break;
                if (string.IsNullOrEmpty(prefix))
                    continue;
                if (_prefetchedTooltipPrefixes.Contains(prefix))
                    continue;
                if (!_tooltipPrefetchQueued.Add(prefix))
                    continue;

                _tooltipPrefetchQueue.Enqueue(prefix);
                queued++;
            }

            if (_isTooltipPrefetchWorkerRunning || _tooltipPrefetchQueue.Count == 0)
                return;

            var generation = _prefetchGeneration;
            AsyncHelper.FireAndForget(RunTooltipPrefetchWorkerAsync(generation));
        }

        private async Task RunTooltipPrefetchWorkerAsync(int generation)
        {
            if (_isTooltipPrefetchWorkerRunning)
                return;

            _isTooltipPrefetchWorkerRunning = true;
            try
            {
                while (_isTabActive && generation == _prefetchGeneration && _tooltipPrefetchQueue.Count > 0)
                {
                    var prefix = _tooltipPrefetchQueue.Dequeue();
                    _tooltipPrefetchQueued.Remove(prefix);
                    if (_prefetchedTooltipPrefixes.Contains(prefix))
                        continue;

                    var sampleNames = await GetOrLoadPreviewSampleNamesAsync(prefix, allowRemote: true);
                    if (sampleNames.Count > 0)
                    {
                        var warmBatch = sampleNames.Take(PREFETCH_SAMPLE_BUDGET).ToList();
                        await EnsureLibraryPreviewCachedAsync(prefix, warmBatch);
                        _previewCache.WarmTooltipCache(prefix, warmBatch);
                        if (generation != _prefetchGeneration)
                            break;
                    }

                    _prefetchedTooltipPrefixes.Add(prefix);
                    await Task.Delay(PREFETCH_WORKER_THROTTLE_MS);
                }
            }
            finally
            {
                _isTooltipPrefetchWorkerRunning = false;
                if (generation == _prefetchGeneration && _tooltipPrefetchQueue.Count > 0)
                    AsyncHelper.FireAndForget(RunTooltipPrefetchWorkerAsync(generation));
            }
        }

        private async Task EnsureLibraryPreviewCachedAsync(string prefix, List<string> sampleNames)
        {
            if (sampleNames == null || sampleNames.Count == 0)
                return;

            int stagnantIterations = 0;
            while (true)
            {
                if (_previewCacheLoadTasks.TryGetValue(prefix, out var running))
                {
                    await running;
                    continue;
                }

                var missing = new List<string>();
                foreach (var name in sampleNames)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (_previewCache.GetPreview(prefix, name) == null)
                        missing.Add(name);
                }

                if (missing.Count == 0)
                    return;

                var loadTask = _previewCache.LoadPreviewBatchAsync(prefix, missing);
                _previewCacheLoadTasks[prefix] = loadTask;
                try
                {
                    await loadTask;
                }
                finally
                {
                    if (_previewCacheLoadTasks.TryGetValue(prefix, out var current) && current == loadTask)
                        _previewCacheLoadTasks.Remove(prefix);
                }

                var unresolvedAfterLoad = 0;
                foreach (var name in missing)
                {
                    if (_previewCache.GetPreview(prefix, name) == null)
                        unresolvedAfterLoad++;
                }

                if (unresolvedAfterLoad == 0)
                {
                    stagnantIterations = 0;
                    continue;
                }

                if (unresolvedAfterLoad == missing.Count)
                    stagnantIterations++;
                else
                    stagnantIterations = 0;

                // Prevent infinite retry loop when API/import cannot produce these previews.
                if (stagnantIterations >= 2)
                    return;
            }
        }

        private List<LibraryListView.LibraryPreviewItem> BuildPreviewItems(string prefix, List<string> sampleNames)
        {
            var result = new List<LibraryListView.LibraryPreviewItem>(sampleNames.Count);
            foreach (var name in sampleNames)
            {
                var sprite = _previewCache.GetTooltipPreview(prefix, name)
                    ?? _previewCache.GetPreview(prefix, name);
                result.Add(new LibraryListView.LibraryPreviewItem(name, sprite));
            }
            return result;
        }

        private async Task<List<string>> GetOrLoadPreviewSampleNamesAsync(string prefix, bool allowRemote)
        {
            EnsurePreviewSampleCacheLoaded();

            // If this library is currently loaded, refresh from real collection entries.
            if (_dc.CurrentPrefix == prefix && _dc.AllEntries.Count > 0)
            {
                var currentSamples = BuildPreferredCandidateNames(_dc.AllEntries.Select(e => e.Name).ToList(), maxCount: PREVIEW_NAME_CACHE_LIMIT);
                if (currentSamples.Count > 0)
                {
                    SetPreviewNameCache(prefix, currentSamples);
                    return currentSamples;
                }
            }

            if (_libraryPreviewNameCache.TryGetValue(prefix, out var cached))
                return cached;

            if (_persistedPreviewNameCache.TryGetValue(prefix, out var persisted) && persisted.Count > 0)
            {
                var persistedCopy = new List<string>(persisted);
                _libraryPreviewNameCache[prefix] = persistedCopy;
                return persistedCopy;
            }

            if (!allowRemote)
                return new List<string>();

            if (_libraryPreviewNameTasks.TryGetValue(prefix, out var runningTask))
                return await runningTask;

            var resolveTask = ResolvePreviewSampleNamesForPrefixAsync(prefix);
            _libraryPreviewNameTasks[prefix] = resolveTask;
            try
            {
                var selected = await resolveTask;
                if (selected != null && selected.Count > 0)
                {
                    SetPreviewNameCache(prefix, selected);
                    return _libraryPreviewNameCache[prefix];
                }
                return selected ?? new List<string>();
            }
            finally
            {
                if (_libraryPreviewNameTasks.TryGetValue(prefix, out var current) && current == resolveTask)
                    _libraryPreviewNameTasks.Remove(prefix);
            }
        }

        private async Task<List<string>> ResolvePreviewSampleNamesForPrefixAsync(string prefix)
        {
            if (_dc.CurrentPrefix == prefix && _dc.AllEntries.Count > 0)
                return BuildPreferredCandidateNames(_dc.AllEntries.Select(e => e.Name).ToList(), maxCount: PREVIEW_NAME_CACHE_LIMIT);

            // Prefer actual collection icon names over remote search hits.
            var collectionNames = await _db.GetCollectionIconsAsync(prefix);
            var collectionCandidates = BuildPreferredCandidateNames(collectionNames, maxCount: PREVIEW_NAME_CACHE_LIMIT);
            if (collectionCandidates.Count > 0)
                return collectionCandidates;

            var selected = await ResolvePreviewSampleNamesViaSearchAsync(prefix);
            return selected;
        }

        private async Task<List<string>> ResolvePreviewSampleNamesViaSearchAsync(string prefix)
        {
            var candidatePool = new List<string>(48);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var baseName in LIBRARY_COMMON_PREVIEW_REMOTE_BASES)
            {
                var hits = await _db.SearchRemoteAsync(baseName, prefix, limit: 12);
                foreach (var hit in hits)
                {
                    if (string.IsNullOrEmpty(hit?.Name)) continue;
                    if (!seen.Add(hit.Name)) continue;
                    candidatePool.Add(hit.Name);
                }
            }

            var fallback = await _db.SearchRemoteAsync("a", prefix, limit: 16);
            foreach (var entry in fallback)
            {
                if (string.IsNullOrEmpty(entry?.Name)) continue;
                if (!seen.Add(entry.Name)) continue;
                candidatePool.Add(entry.Name);
            }

            return BuildPreferredCandidateNames(candidatePool, maxCount: PREVIEW_NAME_CACHE_LIMIT);
        }

        private static List<string> BuildPreferredCandidateNames(List<string> iconNames, int maxCount)
        {
            var candidates = new List<string>(maxCount);
            if (iconNames == null || iconNames.Count == 0 || maxCount <= 0)
                return candidates;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var aliasGroup in LIBRARY_COMMON_PREVIEW_ALIASES)
            {
                var match = PickBestCommonMatch(iconNames, aliasGroup, seen);
                if (string.IsNullOrEmpty(match)) continue;

                candidates.Add(match);
                seen.Add(match);
                if (candidates.Count >= maxCount) return candidates;
            }

            foreach (var name in iconNames)
            {
                if (string.IsNullOrEmpty(name) || seen.Contains(name)) continue;
                candidates.Add(name);
                seen.Add(name);
                if (candidates.Count >= maxCount) break;
            }

            return candidates;
        }

        private static List<string> MergePreviewCandidates(List<string> primary, List<string> secondary)
        {
            var merged = new List<string>((primary?.Count ?? 0) + (secondary?.Count ?? 0));
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (primary != null)
            {
                foreach (var name in primary)
                {
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                    merged.Add(name);
                }
            }

            if (secondary != null)
            {
                foreach (var name in secondary)
                {
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                    merged.Add(name);
                }
            }

            return merged;
        }

        private static List<string> BuildLocalFallbackCandidateNames(int maxCount)
        {
            var fallback = new List<string>(maxCount);
            foreach (var aliases in LIBRARY_COMMON_PREVIEW_ALIASES)
            {
                if (aliases == null || aliases.Length == 0) continue;
                var first = aliases[0];
                if (string.IsNullOrEmpty(first)) continue;
                fallback.Add(first);
                if (fallback.Count >= maxCount)
                    break;
            }
            return fallback;
        }

        private void SetPreviewNameCache(string prefix, List<string> names)
        {
            if (string.IsNullOrEmpty(prefix) || names == null || names.Count == 0)
                return;

            var normalized = names
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.Ordinal)
                .Take(PREVIEW_NAME_CACHE_LIMIT)
                .ToList();
            if (normalized.Count == 0)
                return;

            _libraryPreviewNameCache[prefix] = normalized;
            _persistedPreviewNameCache[prefix] = new List<string>(normalized);
            SchedulePreviewSampleCacheSave();
        }

        private void EnsurePreviewSampleCacheLoaded()
        {
            if (_isPreviewSampleCacheLoaded)
                return;
            _isPreviewSampleCacheLoaded = true;

            _persistedPreviewNameCache.Clear();
            try
            {
                if (!File.Exists(PREVIEW_SAMPLE_CACHE_PATH))
                    return;

                var json = File.ReadAllText(PREVIEW_SAMPLE_CACHE_PATH);
                if (string.IsNullOrWhiteSpace(json))
                    return;

                var parsed = JsonUtility.FromJson<PreviewSampleCacheStore>(json);
                if (parsed?.items == null) return;

                foreach (var item in parsed.items)
                {
                    if (item == null || string.IsNullOrEmpty(item.prefix) || item.names == null)
                        continue;

                    var normalized = item.names
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Distinct(StringComparer.Ordinal)
                        .Take(PREVIEW_NAME_CACHE_LIMIT)
                        .ToList();
                    if (normalized.Count == 0)
                        continue;

                    _persistedPreviewNameCache[item.prefix] = normalized;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IconBrowser] Failed to load preview sample cache: {e.Message}");
            }
        }

        private void SchedulePreviewSampleCacheSave()
        {
            _isPreviewSampleCacheDirty = true;
            _previewCacheSaveHandle?.Pause();
            _previewCacheSaveHandle = schedule.Execute(SavePreviewSampleCache).StartingIn(300);
        }

        private void SavePreviewSampleCache()
        {
            if (!_isPreviewSampleCacheDirty)
                return;
            _isPreviewSampleCacheDirty = false;

            try
            {
                var store = new PreviewSampleCacheStore
                {
                    items = _persistedPreviewNameCache
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new PreviewSampleCacheItem
                        {
                            prefix = kv.Key,
                            names = kv.Value?.Take(PREVIEW_NAME_CACHE_LIMIT).ToArray() ?? Array.Empty<string>()
                        })
                        .ToList()
                };

                var dir = Path.GetDirectoryName(PREVIEW_SAMPLE_CACHE_PATH);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonUtility.ToJson(store);
                File.WriteAllText(PREVIEW_SAMPLE_CACHE_PATH, json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IconBrowser] Failed to save preview sample cache: {e.Message}");
            }
        }

        private async Task<List<string>> ResolveRenderablePreviewNamesAsync(string prefix, List<string> candidateNames, int targetCount)
        {
            var resolved = new List<string>(targetCount);
            if (candidateNames == null || candidateNames.Count == 0 || targetCount <= 0)
                return resolved;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = candidateNames
                .Where(n => !string.IsNullOrEmpty(n))
                .Where(n => seen.Add(n))
                .ToList();

            const int batchSize = 6;
            for (int i = 0; i < queue.Count && resolved.Count < targetCount; i += batchSize)
            {
                var count = Math.Min(batchSize, queue.Count - i);
                var batch = queue.GetRange(i, count);
                await EnsureLibraryPreviewCachedAsync(prefix, batch);

                foreach (var name in batch)
                {
                    if (_previewCache.GetPreview(prefix, name) == null) continue;
                    resolved.Add(name);
                    if (resolved.Count >= targetCount)
                        break;
                }
            }

            return resolved;
        }

        private static string PickBestCommonMatch(List<string> names, string baseName, HashSet<string> exclude)
        {
            return PickBestCommonMatch(names, new[] { baseName }, exclude);
        }

        private static string PickBestCommonMatch(List<string> names, IEnumerable<string> baseNames, HashSet<string> exclude)
        {
            if (names == null || names.Count == 0 || baseNames == null)
                return null;

            var normalizedBases = baseNames
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => NormalizePreviewName(n))
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedBases.Count == 0)
                return null;

            string startsWithMatch = null;
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (exclude != null && exclude.Contains(name)) continue;

                var normalized = NormalizePreviewName(name);
                foreach (var baseName in normalizedBases)
                {
                    if (normalized.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                        return name;

                    if (startsWithMatch == null &&
                        normalized.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase))
                    {
                        startsWithMatch = name;
                    }
                }
            }

            return startsWithMatch;
        }

        private static string NormalizePreviewName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var normalized = name.Trim().Replace("_", "-").ToLowerInvariant();

            bool stripped;
            do
            {
                stripped = false;
                foreach (var suffix in PREVIEW_VARIANT_SUFFIXES)
                {
                    if (!normalized.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    normalized = normalized.Substring(0, normalized.Length - suffix.Length);
                    stripped = true;
                    break;
                }
            } while (stripped && normalized.Length > 0);

            return normalized;
        }

        private void RebuildVariantBar(string prefix)
        {
            _variantBar.Clear();

            if (!VariantGrouper.HasVariants(prefix))
            {
                _variantBar.style.display = DisplayStyle.None;
                return;
            }

            _variantBar.style.display = DisplayStyle.Flex;

            // "All" tab
            var allBtn = new Button(() => OnVariantTabClicked("")) { text = "All" };
            allBtn.AddToClassList("browse-tab__variant-tab");
            allBtn.AddToClassList("browse-tab__variant-tab--active");
            _variantBar.Add(allBtn);

            // Get suffix labels for this library
            var suffixes = VariantGrouper.GetSuffixes(prefix);
            foreach (var suffix in suffixes)
            {
                var label = suffix.Substring(1); // strip leading "-"
                var captured = label;
                var btn = new Button(() => OnVariantTabClicked(captured)) { text = label };
                btn.AddToClassList("browse-tab__variant-tab");
                _variantBar.Add(btn);
            }

            // Also add "default" tab for base icons (no suffix)
            // For ri, every icon has a suffix, so no "default" tab
            if (prefix != "ri")
            {
                // Insert "default" right after "All"
                var defaultBtn = new Button(() => OnVariantTabClicked("default")) { text = "default" };
                defaultBtn.AddToClassList("browse-tab__variant-tab");
                _variantBar.Insert(1, defaultBtn);
            }
        }

        private void OnVariantTabClicked(string variant)
        {
            // Update tab active state
            foreach (var child in _variantBar.Children())
            {
                child.EnableInClassList("browse-tab__variant-tab--active", false);
            }
            // Find the clicked button
            foreach (var child in _variantBar.Children())
            {
                if (child is Button btn)
                {
                    bool match = (variant == "" && btn.text == "All")
                              || (variant == "default" && btn.text == "default")
                              || (variant != "" && variant != "default" && btn.text == variant);
                    btn.EnableInClassList("browse-tab__variant-tab--active", match);
                }
            }

            _dc.SetVariant(variant);
        }

        /// <summary>
        /// Initializes the Browse tab — loads the default collection.
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;
            UpdateLibraryInfo(_dc.CurrentPrefix);
            RebuildVariantBar(_dc.CurrentPrefix);
            var ct = _cts.Token;
            AsyncHelper.FireAndForget(IgnoreOperationCanceledAsync(_dc.SetPrefixAndLoadAsync(_dc.CurrentPrefix, ct)));
        }

        /// <summary>
        /// Changes the active icon library (called externally).
        /// </summary>
        public void SetLibrary(string prefix)
        {
            ResetRequestCts();
            _searchQuery = "";
            _detail.Clear();
            _previewCache.ClearMemoryCache();
            _tooltipPrefetchQueue.Clear();
            _tooltipPrefetchQueued.Clear();
            _prefetchGeneration++;
            _lastHoveredPrefix = prefix;
            UpdateProtectedPreviewPrefixes(prefix);
            _libraryList.HighlightLibrary(prefix);
            UpdateLibraryInfo(prefix);
            RebuildVariantBar(prefix);
            var ct = _cts.Token;
            AsyncHelper.FireAndForget(IgnoreOperationCanceledAsync(_dc.SetPrefixAndLoadAsync(prefix, ct)));
        }

        private void BuildAlwaysWarmLibraryPrefixSet(List<IconLibrary> libraries)
        {
            _alwaysWarmLibraryPrefixes.Clear();
            if (libraries == null || libraries.Count == 0) return;

            var available = new HashSet<string>(libraries.Select(l => l.Prefix));
            foreach (var featured in FEATURED_PREFIXES)
            {
                if (available.Contains(featured))
                    _alwaysWarmLibraryPrefixes.Add(featured);
            }
        }

        private void BuildLibraryDisplayOrder(List<IconLibrary> libraries)
        {
            _libraryDisplayOrder.Clear();
            if (libraries == null || libraries.Count == 0) return;

            var featuredSet = new HashSet<string>(FEATURED_PREFIXES);
            var byPrefix = libraries
                .Where(l => !string.IsNullOrEmpty(l.Prefix))
                .GroupBy(l => l.Prefix, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var featured in FEATURED_PREFIXES)
            {
                if (byPrefix.ContainsKey(featured))
                    _libraryDisplayOrder.Add(featured);
            }

            var rest = byPrefix.Values
                .Where(l => !featuredSet.Contains(l.Prefix))
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .Select(l => l.Prefix);

            _libraryDisplayOrder.AddRange(rest);
        }

        private void WarmAlwaysTooltipPreviews()
        {
            if (!_isTabActive) return;
            if (_alwaysWarmLibraryPrefixes.Count == 0) return;
            _warmupHandle?.Pause();
            _warmupHandle = schedule.Execute(() =>
                EnqueueTooltipPrefetch(_alwaysWarmLibraryPrefixes, WARM_PREFETCH_PREFIX_BUDGET))
                .StartingIn(400);
        }

        private void WarmAllTooltipPreviewsInBackground()
        {
            if (!_isTabActive) return;
            if (_libraryDisplayOrder.Count == 0) return;

            var seeds = BuildBackgroundPrefetchSeedOrder();
            if (seeds.Count == 0) return;

            _fullWarmupHandle?.Pause();
            _fullWarmupHandle = schedule.Execute(() =>
                EnqueueTooltipPrefetch(
                    seeds,
                    Math.Min(BACKGROUND_PREFETCH_PREFIX_BUDGET, seeds.Count)))
                .StartingIn(1200);
        }

        private List<string> BuildBackgroundPrefetchSeedOrder()
        {
            var ordered = new List<string>(BACKGROUND_PREFETCH_PREFIX_BUDGET + FEATURED_PREFIXES.Length);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddPrefix(string prefix)
            {
                if (string.IsNullOrEmpty(prefix)) return;
                if (!seen.Add(prefix)) return;
                ordered.Add(prefix);
            }

            AddPrefix(_dc.CurrentPrefix);
            AddPrefix(_lastHoveredPrefix);

            var hoverIndex = !string.IsNullOrEmpty(_lastHoveredPrefix)
                ? _libraryDisplayOrder.IndexOf(_lastHoveredPrefix)
                : -1;
            if (hoverIndex >= 0)
            {
                const int nearbyRadius = 2;
                for (int distance = 1; distance <= nearbyRadius; distance++)
                {
                    var left = hoverIndex - distance;
                    if (left >= 0) AddPrefix(_libraryDisplayOrder[left]);

                    var right = hoverIndex + distance;
                    if (right < _libraryDisplayOrder.Count) AddPrefix(_libraryDisplayOrder[right]);
                }
            }

            foreach (var featured in FEATURED_PREFIXES)
                AddPrefix(featured);

            return ordered;
        }

        private void UpdateProtectedPreviewPrefixes(string currentPrefix)
        {
            var protectedPrefixes = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(currentPrefix))
                protectedPrefixes.Add(currentPrefix);
            if (!string.IsNullOrEmpty(_lastHoveredPrefix))
                protectedPrefixes.Add(_lastHoveredPrefix);

            var pinnedFeatured = 0;
            foreach (var prefix in FEATURED_PREFIXES)
            {
                if (!_alwaysWarmLibraryPrefixes.Contains(prefix)) continue;
                protectedPrefixes.Add(prefix);
                pinnedFeatured++;
                if (pinnedFeatured >= PROTECTED_FEATURED_PREFIX_COUNT)
                    break;
            }

            _previewCache.SetProtectedPrefixes(protectedPrefixes);
        }

        /// <summary>
        /// Sets the category filter and refreshes the grid.
        /// </summary>
        public void SetCategory(string category)
        {
            _dc.SetCategory(category);
        }

        /// <summary>
        /// Searches icons with debounce.
        /// </summary>
        public void Search(string query)
        {
            _searchQuery = query;

            // Cancel previous debounce
            _debounceHandle?.Pause();

            if (string.IsNullOrWhiteSpace(query))
            {
                // Empty query — show full collection
                var ct = _cts.Token;
                AsyncHelper.FireAndForget(IgnoreOperationCanceledAsync(_dc.LoadCollectionAsync(_dc.CurrentPrefix, ct)));
                return;
            }

            var token = _cts.Token;
            _debounceHandle = schedule.Execute(() =>
                    AsyncHelper.FireAndForget(IgnoreOperationCanceledAsync(_dc.SearchAsync(query, token))))
                .StartingIn(IconBrowserConstants.SEARCH_DEBOUNCE_MS);
        }

        /// <summary>
        /// Looks up variant siblings for the given icon name.
        /// </summary>
        private List<IconEntry> FindVariants(string iconName)
        {
            var (baseName, _) = VariantGrouper.ParseVariant(iconName, _dc.CurrentPrefix);
            _dc.VariantMap.TryGetValue(baseName, out var variants);
            return variants;
        }

        private void OnSelected(IconEntry entry)
        {
            // Try to set preview sprite from atlas cache
            var preview = _previewCache.GetPreview(entry.Prefix, entry.Name);
            if (preview != null)
                entry.PreviewSprite = preview;

            var variants = FindVariants(entry.Name);

            // Preload all variant previews so the variant strip shows icons
            if (variants != null && variants.Count > 1)
            {
                var ct = _cts.Token;
                AsyncHelper.FireAndForget(IgnoreOperationCanceledAsync(PreloadVariantPreviewsAsync(variants, ct)));
            }

            _detail.ShowEntry(entry, variants, browseMode: true);
        }

        private void OnGridSelectionChanged(List<IconEntry> entries)
        {
            _detail.HandleSelectionChanged(entries, OnSelected);
        }

        private void OnPendingSearchDrained(string query)
        {
            var ct = _cts.Token;
            AsyncHelper.FireAndForget(IgnoreOperationCanceledAsync(_dc.SearchAsync(query, ct)));
        }

        private void ResetRequestCts()
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        private static async Task IgnoreOperationCanceledAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation during tab/library switch.
            }
        }

        private void ShowLoading(bool loading)
        {
            // Could add a spinner overlay here in the future
        }

        private void PauseBackgroundPrefetch()
        {
            _hoverPrefetchHandle?.Pause();
            _hoverPrefetchHandle = null;
            _warmupHandle?.Pause();
            _warmupHandle = null;
            _fullWarmupHandle?.Pause();
            _fullWarmupHandle = null;
            _tooltipPrefetchQueue.Clear();
            _tooltipPrefetchQueued.Clear();
            _prefetchGeneration++;
        }

        public void Detach()
        {
            SavePreviewSampleCache();

            _cts.Cancel();
            _cts.Dispose();

            _debounceHandle?.Pause();
            _debounceHandle = null;
            _scrollDebounceHandle?.Pause();
            _scrollDebounceHandle = null;
            _hoverPrefetchHandle?.Pause();
            _hoverPrefetchHandle = null;
            _warmupHandle?.Pause();
            _warmupHandle = null;
            _fullWarmupHandle?.Pause();
            _fullWarmupHandle = null;
            _previewCacheSaveHandle?.Pause();
            _previewCacheSaveHandle = null;
            _libraryPreviewNameTasks.Clear();
            _previewCacheLoadTasks.Clear();
            _prefetchedTooltipPrefixes.Clear();
            _tooltipPrefetchQueue.Clear();
            _tooltipPrefetchQueued.Clear();
            _isTooltipPrefetchWorkerRunning = false;
            _prefetchGeneration++;
            _lastHoveredPrefix = null;

            _dc.OnEntriesChanged -= OnDataEntriesChanged;
            _dc.OnLoadingChanged -= ShowLoading;
            _dc.OnPendingSearchDrained -= OnPendingSearchDrained;

            _libraryList.OnLibrarySelected -= OnLibraryClicked;
            _libraryList.OnLibraryHovered -= OnLibraryHovered;
            _libraryList.ResolveLibraryPreviewsAsync = null;

            _grid.OnIconSelected -= OnSelected;
            _grid.OnVisibleRangeChanged -= OnVisibleRangeChangedDebounced;
            _grid.OnSelectionChanged -= OnGridSelectionChanged;
            _grid.OnQuickImportClicked -= OnImport;
            _grid.OnQuickDeleteClicked -= OnQuickDelete;
            _detail.OnImportClicked -= OnImport;
            _detail.OnDeleteClicked -= OnQuickDelete;
            _detail.OnVariantSelected -= OnVariantSelected;
            _detail.OnBatchImportClicked -= OnBatchImport;
            _detail.OnBatchDeleteClicked -= OnBatchDelete;
        }
    }
}
