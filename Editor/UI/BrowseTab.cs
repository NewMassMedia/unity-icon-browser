using System;
using System.Collections.Generic;
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

        private IVisualElementScheduledItem _debounceHandle;
        private IVisualElementScheduledItem _scrollDebounceHandle;
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
            _libraryList.SetLibraries(libraries);
            _libraryList.HighlightLibrary(_dc.CurrentPrefix);
        }

        private void OnLibraryClicked(string prefix)
        {
            if (prefix == _dc.CurrentPrefix && _isInitialized) return;

            // Cancel any in-flight async operations from the previous library
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            _libraryList.HighlightLibrary(prefix);
            _detail.Clear();
            _previewCache.ClearMemoryCache();
            _searchQuery = "";
            _isInitialized = true;

            UpdateLibraryInfo(prefix);
            RebuildVariantBar(prefix);
            AsyncHelper.FireAndForget(_dc.SetPrefixAndLoadAsync(prefix));
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
            _libraryList.HighlightLibrary(prefix);
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
                AsyncHelper.FireAndForget(PreloadVariantPreviewsAsync(variants));

            _detail.ShowEntry(entry, variants, browseMode: true);
        }

        private void OnGridSelectionChanged(List<IconEntry> entries)
        {
            _detail.HandleSelectionChanged(entries, OnSelected);
        }

        private void OnPendingSearchDrained(string query) => AsyncHelper.FireAndForget(_dc.SearchAsync(query));

        private void ShowLoading(bool loading)
        {
            // Could add a spinner overlay here in the future
        }

        public void Detach()
        {
            _cts.Cancel();
            _cts.Dispose();

            _debounceHandle?.Pause();
            _debounceHandle = null;
            _scrollDebounceHandle?.Pause();
            _scrollDebounceHandle = null;

            _dc.OnEntriesChanged -= OnDataEntriesChanged;
            _dc.OnLoadingChanged -= ShowLoading;
            _dc.OnPendingSearchDrained -= OnPendingSearchDrained;

            _libraryList.OnLibrarySelected -= OnLibraryClicked;

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
