using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IconBrowser.Data;
using IconBrowser.Import;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser.UI
{
    /// <summary>
    /// Browse tab — search and import icons from Iconify API.
    /// Left library list, center icon grid, right detail panel.
    /// Delegates data operations (loading, filtering, grouping) to BrowseDataController.
    /// </summary>
    public class BrowseTab : VisualElement
    {
        /// <summary>
        /// 10 curated libraries covering diverse icon styles.
        /// </summary>
        static readonly string[] FeaturedPrefixes =
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

        readonly IconDatabase _db;
        readonly SvgPreviewCache _previewCache;
        readonly BrowseDataController _dc;
        readonly IconGrid _grid;
        readonly IconDetailPanel _detail;
        ToastNotification _toast;

        // Library list
        readonly ScrollView _libraryScroll;
        readonly VisualElement _featuredList;
        readonly VisualElement _moreList;
        readonly Button _moreToggle;
        bool _moreExpanded;
        VisualElement _activeLibraryItem;
        List<IconLibrary> _allLibraries = new();

        // Library info bar
        readonly VisualElement _libraryInfoBar;
        readonly Label _libraryInfoName;
        readonly Button _libraryInfoAuthor;
        readonly Label _libraryInfoLicense;
        string _libraryInfoUrl;

        // Variant filter tabs
        readonly VisualElement _variantBar;

        string _searchQuery = "";
        bool _initialized;

        IVisualElementScheduledItem _debounceHandle;
        IVisualElementScheduledItem _scrollDebounceHandle;
        bool _needsImmediateLoad;

        public event Action OnIconImported;
        public event Action<List<string>> OnCategoriesLoaded;

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
            AddToClassList("icon-tab");

            // Wire data controller events
            _dc.OnEntriesChanged += OnDataEntriesChanged;
            _dc.OnCategoriesLoaded += cats => OnCategoriesLoaded?.Invoke(cats);
            _dc.OnLoadingChanged += ShowLoading;
            _dc.OnPendingSearchDrained += query => AsyncHelper.FireAndForget(_dc.SearchAsync(query));

            var body = new VisualElement();
            body.AddToClassList("icon-tab__body");
            Add(body);

            // Library list (left)
            _libraryScroll = new ScrollView(ScrollViewMode.Vertical);
            _libraryScroll.AddToClassList("library-list");
            body.Add(_libraryScroll);

            _featuredList = new VisualElement();
            _featuredList.AddToClassList("library-list__section");
            _libraryScroll.Add(_featuredList);

            // "More Libraries" toggle
            _moreToggle = new Button(ToggleMore);
            _moreToggle.AddToClassList("library-list__more-toggle");
            _moreToggle.text = "More Libraries \u25b6"; // ▶
            _libraryScroll.Add(_moreToggle);

            _moreList = new VisualElement();
            _moreList.AddToClassList("library-list__section");
            _moreList.style.display = DisplayStyle.None;
            _libraryScroll.Add(_moreList);

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
            _grid.OnIconSelected += OnSelected;
            _grid.OnVisibleRangeChanged += OnVisibleRangeChangedDebounced;
            _grid.OnSelectionChanged += OnGridSelectionChanged;
            _grid.OnQuickImportClicked += OnImport;
            _grid.OnQuickDeleteClicked += OnQuickDelete;
            _detail.OnImportClicked += OnImport;
            _detail.OnDeleteClicked += OnQuickDelete;
            _detail.OnVariantSelected += OnVariantSelected;
            _detail.OnBatchImportClicked += OnBatchImport;
            _detail.OnBatchDeleteClicked += OnBatchDelete;
        }

        /// <summary>
        /// Called when BrowseDataController updates entries after filtering/loading.
        /// Handles UI concerns: preview cache assignment and grid update.
        /// </summary>
        void OnDataEntriesChanged()
        {
            foreach (var entry in _dc.FilteredEntries)
            {
                if (entry.PreviewSprite != null) continue;
                if (entry.IsImported && entry.LocalAsset != null) continue;
                var sprite = _previewCache.GetPreview(entry.Prefix, entry.Name);
                if (sprite != null) entry.PreviewSprite = sprite;
            }

            _needsImmediateLoad = true;
            _grid.SetItems(_dc.GroupedEntries);
        }

        /// <summary>
        /// Populates the library list from loaded library data.
        /// </summary>
        public void SetLibraries(List<IconLibrary> libraries)
        {
            _allLibraries = libraries;
            _featuredList.Clear();
            _moreList.Clear();

            var featuredSet = new HashSet<string>(FeaturedPrefixes);

            // Featured libraries — in the curated order
            foreach (var prefix in FeaturedPrefixes)
            {
                var lib = libraries.FirstOrDefault(l => l.Prefix == prefix);
                if (lib != null)
                    _featuredList.Add(CreateLibraryItem(lib));
            }

            // More libraries — everything else, sorted by name
            var rest = libraries
                .Where(l => !featuredSet.Contains(l.Prefix))
                .OrderBy(l => l.Name)
                .ToList();

            foreach (var lib in rest)
                _moreList.Add(CreateLibraryItem(lib));

            _moreToggle.text = $"More Libraries ({rest.Count}) \u25b6";
            _moreToggle.style.display = rest.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            // Highlight initial selection
            HighlightLibrary(_dc.CurrentPrefix);
        }

        VisualElement CreateLibraryItem(IconLibrary lib)
        {
            var item = new Button(() => OnLibraryClicked(lib.Prefix));
            item.AddToClassList("library-list__item");
            item.userData = lib.Prefix;

            var label = new Label(lib.Name);
            label.AddToClassList("library-list__item-name");
            item.Add(label);

            var count = new Label(lib.Total.ToString("N0"));
            count.AddToClassList("library-list__item-count");
            item.Add(count);

            return item;
        }

        void OnLibraryClicked(string prefix)
        {
            if (prefix == _dc.CurrentPrefix && _initialized) return;

            HighlightLibrary(prefix);
            _detail.Clear();
            _previewCache.ClearMemoryCache();
            _searchQuery = "";
            _initialized = true;

            UpdateLibraryInfo(prefix);
            RebuildVariantBar(prefix);
            AsyncHelper.FireAndForget(_dc.SetPrefixAndLoadAsync(prefix));
        }

        void HighlightLibrary(string prefix)
        {
            // Remove previous highlight
            _activeLibraryItem?.RemoveFromClassList("library-list__item--active");

            // Find and highlight new
            _activeLibraryItem = FindLibraryItem(_featuredList, prefix)
                              ?? FindLibraryItem(_moreList, prefix);
            _activeLibraryItem?.AddToClassList("library-list__item--active");
        }

        static VisualElement FindLibraryItem(VisualElement container, string prefix)
        {
            foreach (var child in container.Children())
            {
                if (child.userData is string p && p == prefix)
                    return child;
            }
            return null;
        }

        void ToggleMore()
        {
            _moreExpanded = !_moreExpanded;
            _moreList.style.display = _moreExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            _moreToggle.text = _moreExpanded
                ? $"More Libraries ({_moreList.childCount}) \u25bc"  // ▼
                : $"More Libraries ({_moreList.childCount}) \u25b6"; // ▶
        }

        void UpdateLibraryInfo(string prefix)
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

        void RebuildVariantBar(string prefix)
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

        void OnVariantTabClicked(string variant)
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
            if (_initialized) return;
            _initialized = true;
            UpdateLibraryInfo(_dc.CurrentPrefix);
            RebuildVariantBar(_dc.CurrentPrefix);
            AsyncHelper.FireAndForget(_dc.SetPrefixAndLoadAsync(_dc.CurrentPrefix));
        }

        /// <summary>
        /// Changes the active icon library (called externally).
        /// </summary>
        public void SetLibrary(string prefix)
        {
            _searchQuery = "";
            _detail.Clear();
            _previewCache.ClearMemoryCache();
            HighlightLibrary(prefix);
            UpdateLibraryInfo(prefix);
            RebuildVariantBar(prefix);
            AsyncHelper.FireAndForget(_dc.SetPrefixAndLoadAsync(prefix));
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
                AsyncHelper.FireAndForget(_dc.LoadCollectionAsync(_dc.CurrentPrefix));
                return;
            }

            _debounceHandle = schedule.Execute(() => AsyncHelper.FireAndForget(_dc.SearchAsync(query))).StartingIn(IconBrowserConstants.SEARCH_DEBOUNCE_MS);
        }

        void OnSelected(IconEntry entry)
        {
            // Try to set preview sprite from atlas cache
            var preview = _previewCache.GetPreview(entry.Prefix, entry.Name);
            if (preview != null)
                entry.PreviewSprite = preview;

            var (baseName, _) = VariantGrouper.ParseVariant(entry.Name, _dc.CurrentPrefix);
            _dc.VariantMap.TryGetValue(baseName, out var variants);

            // Preload all variant previews so the variant strip shows icons
            if (variants != null && variants.Count > 1)
                AsyncHelper.FireAndForget(PreloadVariantPreviewsAsync(variants));

            _detail.ShowEntry(entry, variants, browseMode: true);
        }

        void OnGridSelectionChanged(List<IconEntry> entries)
        {
            if (entries.Count == 0)
            {
                _detail.Clear();
            }
            else if (entries.Count == 1)
            {
                OnSelected(entries[0]);
            }
            else
            {
                _detail.ShowMultiSelection(entries);
            }
        }

        void OnBatchImport(List<IconEntry> entries)
        {
            AsyncHelper.FireAndForget(OnBatchImportAsync(entries));
        }

        async Task OnBatchImportAsync(List<IconEntry> entries)
        {
            var toImport = entries.Where(e => !e.IsImported).ToList();
            if (toImport.Count == 0) return;

            try
            {
                for (int i = 0; i < toImport.Count; i++)
                {
                    var entry = toImport[i];
                    var cancelled = EditorUtility.DisplayCancelableProgressBar(
                        "Importing Icons",
                        $"Importing {entry.Name}... ({i + 1}/{toImport.Count})",
                        (float)(i + 1) / toImport.Count);
                    if (cancelled) break;

                    var success = await IconImporter.Default.ImportIconAsync(entry.Prefix, entry.Name);
                    if (success)
                    {
                        entry.IsImported = true;
                        _db.MarkImported(entry.Name, entry.Prefix);
                    }
                }

                _grid.RefreshPreviews();
                OnIconImported?.Invoke();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        async Task PreloadVariantPreviewsAsync(List<IconEntry> variants)
        {
            var toLoad = new List<string>();
            foreach (var v in variants)
            {
                if (v.PreviewSprite != null || v.LocalAsset != null) continue;
                var cached = _previewCache.GetPreview(v.Prefix, v.Name);
                if (cached != null)
                {
                    v.PreviewSprite = cached;
                    continue;
                }
                toLoad.Add(v.Name);
            }
            if (toLoad.Count == 0) return;

            await _previewCache.LoadPreviewBatchAsync(_dc.CurrentPrefix, toLoad, () =>
            {
                foreach (var v in variants)
                {
                    var p = _previewCache.GetPreview(v.Prefix, v.Name);
                    if (p != null) v.PreviewSprite = p;
                }
                // Re-show detail with loaded previews
                if (_detail != null && variants.Count > 0)
                {
                    var current = variants.FirstOrDefault(v => v.PreviewSprite != null) ?? variants[0];
                    _detail.ShowEntry(_detail.CurrentEntry, variants, browseMode: true);
                }
            });
        }

        void OnImport(IconEntry entry)
        {
            AsyncHelper.FireAndForget(OnImportAsync(entry));
        }

        async Task OnImportAsync(IconEntry entry)
        {
            EditorUtility.DisplayProgressBar("Importing Icon", $"Importing {entry.Name}...", 0.5f);
            try
            {
                var success = await IconImporter.Default.ImportIconAsync(entry.Prefix, entry.Name);
                if (success)
                {
                    entry.IsImported = true;
                    _db.MarkImported(entry.Name, entry.Prefix);

                    var (baseName, _) = VariantGrouper.ParseVariant(entry.Name, _dc.CurrentPrefix);
                    _dc.VariantMap.TryGetValue(baseName, out var variants);
                    _detail.ShowEntry(entry, variants, browseMode: true);
                    _grid.RefreshPreviews();
                    OnIconImported?.Invoke();
                    _toast?.ShowInfo($"Imported {entry.Name}");
                }
                else
                {
                    _toast?.ShowError($"Failed to import {entry.Name}");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void OnQuickDelete(IconEntry entry)
        {
            if (IconImporter.Default.DeleteIcon(entry.Name, entry.Prefix))
            {
                entry.IsImported = false;
                _db.MarkDeleted(entry.Name);

                var (baseName, _) = VariantGrouper.ParseVariant(entry.Name, _dc.CurrentPrefix);
                _dc.VariantMap.TryGetValue(baseName, out var variants);
                _detail.ShowEntry(entry, variants, browseMode: true);
                _grid.RefreshPreviews();
                OnIconImported?.Invoke();
                _toast?.ShowError($"Deleted {entry.Name}");
            }
        }

        void OnBatchDelete(List<IconEntry> entries)
        {
            var toDelete = entries.Where(e => e.IsImported).ToList();
            if (toDelete.Count == 0) return;

            int deletedCount = 0;
            foreach (var entry in toDelete)
            {
                if (IconImporter.Default.DeleteIcon(entry.Name, entry.Prefix))
                {
                    entry.IsImported = false;
                    _db.MarkDeleted(entry.Name);
                    deletedCount++;
                }
            }

            _grid.RefreshPreviews();
            OnIconImported?.Invoke();
            if (deletedCount > 0)
                _toast?.ShowError($"Deleted {deletedCount} icon(s)");
        }

        void OnVariantSelected(IconEntry variant)
        {
            AsyncHelper.FireAndForget(OnVariantSelectedAsync(variant));
        }

        async Task OnVariantSelectedAsync(IconEntry variant)
        {
            var (baseName, _) = VariantGrouper.ParseVariant(variant.Name, _dc.CurrentPrefix);
            _dc.VariantMap.TryGetValue(baseName, out var variants);

            if (variant.PreviewSprite == null && variant.LocalAsset == null)
            {
                var cached = _previewCache.GetPreview(variant.Prefix, variant.Name);
                if (cached != null)
                {
                    variant.PreviewSprite = cached;
                }
                else
                {
                    await _previewCache.LoadPreviewBatchAsync(_dc.CurrentPrefix, new List<string> { variant.Name }, () =>
                    {
                        var preview = _previewCache.GetPreview(variant.Prefix, variant.Name);
                        if (preview != null) variant.PreviewSprite = preview;
                        _detail.ShowEntry(variant, variants, browseMode: true);
                    });
                    return;
                }
            }

            _detail.ShowEntry(variant, variants, browseMode: true);
        }

        /// <summary>
        /// Debounced scroll handler — waits before loading previews.
        /// </summary>
        void OnVisibleRangeChangedDebounced(int first, int last)
        {
            _scrollDebounceHandle?.Pause();
            if (_needsImmediateLoad)
            {
                _needsImmediateLoad = false;
                OnVisibleRangeChanged(first, last);
            }
            else
            {
                _scrollDebounceHandle = schedule.Execute(() => OnVisibleRangeChanged(first, last)).StartingIn(IconBrowserConstants.SCROLL_DEBOUNCE_MS);
            }
        }

        void OnVisibleRangeChanged(int first, int last)
        {
            var groupedEntries = _dc.GroupedEntries;
            if (groupedEntries.Count == 0) return;

            // Prefetch margin: load 2 extra rows above and below the visible range
            int margin = _grid.Columns * 2;
            first = Mathf.Clamp(first - margin, 0, groupedEntries.Count - 1);
            last = Mathf.Clamp(last + margin, 0, groupedEntries.Count - 1);

            // Collect names that need previews — include variant siblings
            var nameSet = new HashSet<string>();
            var allVariantEntries = new List<IconEntry>();
            for (int i = first; i <= last; i++)
            {
                var entry = groupedEntries[i];
                // Bug fix: only skip imported icons that already have a local asset loaded
                if (entry.IsImported && entry.LocalAsset != null) continue;
                var cached = _previewCache.GetPreview(entry.Prefix, entry.Name);
                if (cached != null)
                {
                    entry.PreviewSprite = cached;
                }
                else
                {
                    nameSet.Add(entry.Name);
                }

                // Also include all variant siblings for this group
                var (baseName, _) = VariantGrouper.ParseVariant(entry.Name, _dc.CurrentPrefix);
                if (_dc.VariantMap.TryGetValue(baseName, out var variants))
                {
                    foreach (var v in variants)
                    {
                        allVariantEntries.Add(v);
                        if (v.PreviewSprite != null || (v.IsImported && v.LocalAsset != null)) continue;
                        var vc = _previewCache.GetPreview(v.Prefix, v.Name);
                        if (vc != null) { v.PreviewSprite = vc; continue; }
                        nameSet.Add(v.Name);
                    }
                }
            }

            // Refresh cells that just got their preview from cache
            _grid.RefreshPreviews();

            if (nameSet.Count == 0) return;

            var names = new List<string>(nameSet);

            // Load previews asynchronously
            AsyncHelper.FireAndForget(_previewCache.LoadPreviewBatchAsync(_dc.CurrentPrefix, names, () =>
            {
                // Update representative entries
                for (int i = first; i <= last && i < groupedEntries.Count; i++)
                {
                    var entry = groupedEntries[i];
                    var preview = _previewCache.GetPreview(entry.Prefix, entry.Name);
                    if (preview != null) entry.PreviewSprite = preview;
                }
                // Update variant entries
                foreach (var v in allVariantEntries)
                {
                    var preview = _previewCache.GetPreview(v.Prefix, v.Name);
                    if (preview != null) v.PreviewSprite = preview;
                }
                _grid.RefreshPreviews();
            }));
        }

        void ShowLoading(bool loading)
        {
            // Could add a spinner overlay here in the future
        }

        public void Detach()
        {
            _debounceHandle?.Pause();
            _debounceHandle = null;
            _scrollDebounceHandle?.Pause();
            _scrollDebounceHandle = null;

            _dc.OnEntriesChanged -= OnDataEntriesChanged;

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
