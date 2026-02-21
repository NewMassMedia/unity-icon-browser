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
    /// Supports category filtering and scroll debounce.
    /// </summary>
    public class BrowseTab : VisualElement
    {
        readonly IconDatabase _db;
        readonly SvgPreviewCache _previewCache;
        readonly IconGrid _grid;
        readonly IconDetailPanel _detail;

        string _currentPrefix = "lucide";
        string _searchQuery = "";
        string _selectedCategory = "";
        List<IconEntry> _allEntries = new();
        List<IconEntry> _filteredEntries = new();
        bool _isLoading;
        bool _initialized;

        IVisualElementScheduledItem _debounceHandle;
        IVisualElementScheduledItem _scrollDebounceHandle;

        public event Action OnIconImported;
        public event Action<List<string>> OnCategoriesLoaded;

        /// <summary>
        /// Current library prefix.
        /// </summary>
        public string CurrentPrefix => _currentPrefix;

        public BrowseTab(IconDatabase db, SvgPreviewCache previewCache)
        {
            _db = db;
            _previewCache = previewCache;
            AddToClassList("icon-tab");

            var body = new VisualElement();
            body.AddToClassList("icon-tab__body");
            Add(body);

            _grid = new IconGrid();
            body.Add(_grid);

            _detail = new IconDetailPanel();
            body.Add(_detail);

            _grid.OnIconSelected += OnSelected;
            _grid.OnIconDoubleClicked += OnDoubleClicked;
            _grid.OnVisibleRangeChanged += OnVisibleRangeChangedDebounced;
            _detail.OnImportClicked += OnImport;
        }

        /// <summary>
        /// Initializes the Browse tab — loads the default collection.
        /// </summary>
        public async void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            await LoadCollectionAsync(_currentPrefix);
        }

        /// <summary>
        /// Changes the active icon library.
        /// </summary>
        public async void SetLibrary(string prefix)
        {
            _currentPrefix = prefix;
            _searchQuery = "";
            _selectedCategory = "";
            _detail.Clear();
            _previewCache.ClearMemoryCache();
            await LoadCollectionAsync(prefix);
        }

        /// <summary>
        /// Sets the category filter and refreshes the grid.
        /// </summary>
        public void SetCategory(string category)
        {
            _selectedCategory = category;
            ApplyCategoryFilter();
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
                _ = LoadCollectionAsync(_currentPrefix);
                return;
            }

            // 300ms debounce
            _debounceHandle = schedule.Execute(() => _ = SearchRemoteAsync(query)).StartingIn(300);
        }

        async Task LoadCollectionAsync(string prefix)
        {
            if (_isLoading) return;
            _isLoading = true;

            ShowLoading(true);
            try
            {
                var names = await _db.GetCollectionIconsAsync(prefix);
                _allEntries = _db.CreateEntries(prefix, names);

                // Notify about available categories
                var categories = _db.GetCategories(prefix);
                OnCategoriesLoaded?.Invoke(categories);

                ApplyCategoryFilter();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[IconBrowser] LoadCollectionAsync error: {e}");
            }
            finally
            {
                _isLoading = false;
                ShowLoading(false);
            }
        }

        void ApplyCategoryFilter()
        {
            if (string.IsNullOrEmpty(_selectedCategory))
            {
                _filteredEntries = new List<IconEntry>(_allEntries);
            }
            else
            {
                var iconsInCategory = _db.GetIconsInCategory(_currentPrefix, _selectedCategory);
                _filteredEntries = _allEntries
                    .Where(e => iconsInCategory.Contains(e.Name))
                    .ToList();
            }
            _grid.SetItems(_filteredEntries);
        }

        async Task SearchRemoteAsync(string query)
        {
            if (_isLoading) return;
            _isLoading = true;

            ShowLoading(true);
            try
            {
                _allEntries = await _db.SearchRemoteAsync(query, _currentPrefix);
                _filteredEntries = new List<IconEntry>(_allEntries);
                _grid.SetItems(_filteredEntries);
            }
            finally
            {
                _isLoading = false;
                ShowLoading(false);
            }
        }

        void OnSelected(IconEntry entry)
        {
            // Try to set preview from cache
            var preview = _previewCache.GetPreview(entry.Prefix, entry.Name);
            if (preview != null) entry.PreviewAsset = preview;

            _detail.ShowEntry(entry);
        }

        void OnDoubleClicked(IconEntry entry)
        {
            // Double-click imports the icon
            if (!entry.IsImported)
                OnImport(entry);
        }

        async void OnImport(IconEntry entry)
        {
            EditorUtility.DisplayProgressBar("Importing Icon", $"Importing {entry.Name}...", 0.5f);
            try
            {
                var success = await IconImporter.ImportIconAsync(entry.Prefix, entry.Name);
                if (success)
                {
                    entry.IsImported = true;
                    _db.MarkImported(entry.Name);
                    _detail.ShowEntry(entry);
                    _grid.RefreshPreviews();
                    OnIconImported?.Invoke();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Debounced scroll handler — waits 100ms after last scroll before loading previews.
        /// </summary>
        void OnVisibleRangeChangedDebounced(int first, int last)
        {
            _scrollDebounceHandle?.Pause();
            _scrollDebounceHandle = schedule.Execute(() => OnVisibleRangeChanged(first, last)).StartingIn(100);
        }

        void OnVisibleRangeChanged(int first, int last)
        {
            if (_filteredEntries.Count == 0) return;

            first = Mathf.Clamp(first, 0, _filteredEntries.Count - 1);
            last = Mathf.Clamp(last, 0, _filteredEntries.Count - 1);

            // Collect names that need previews
            var names = new List<string>();
            for (int i = first; i <= last; i++)
            {
                var entry = _filteredEntries[i];
                if (entry.IsImported) continue; // local icons already have assets
                if (_previewCache.GetPreview(entry.Prefix, entry.Name) != null)
                {
                    entry.PreviewAsset = _previewCache.GetPreview(entry.Prefix, entry.Name);
                    continue;
                }
                names.Add(entry.Name);
            }

            if (names.Count == 0) return;

            // Load previews asynchronously
            _ = _previewCache.LoadPreviewBatchAsync(_currentPrefix, names, () =>
            {
                // Update entries with cached previews
                for (int i = first; i <= last && i < _filteredEntries.Count; i++)
                {
                    var entry = _filteredEntries[i];
                    var preview = _previewCache.GetPreview(entry.Prefix, entry.Name);
                    if (preview != null) entry.PreviewAsset = preview;
                }
                _grid.RefreshPreviews();
            });
        }

        void ShowLoading(bool loading)
        {
            // Could add a spinner overlay here in the future
        }
    }
}
