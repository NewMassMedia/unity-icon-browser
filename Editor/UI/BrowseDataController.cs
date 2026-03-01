using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IconBrowser.Data;
using UnityEngine;

namespace IconBrowser.UI
{
    /// <summary>
    /// Pure data controller for the Browse tab.
    /// Handles collection loading, filtering, variant grouping, and search.
    /// No UI dependencies — fully testable.
    /// </summary>
    internal class BrowseDataController
    {
        readonly IconDatabase _db;

        string _currentPrefix = "lucide";
        string _selectedCategory = "";
        string _selectedVariant = "";
        bool _isLoading;
        string _pendingSearchQuery;

        List<IconEntry> _allEntries = new();
        List<IconEntry> _filteredEntries = new();
        List<IconEntry> _groupedEntries = new();
        Dictionary<string, List<IconEntry>> _variantMap = new();

        public string CurrentPrefix => _currentPrefix;
        public bool IsLoading => _isLoading;
        public List<IconEntry> AllEntries => _allEntries;
        public List<IconEntry> FilteredEntries => _filteredEntries;
        public List<IconEntry> GroupedEntries => _groupedEntries;
        public IReadOnlyDictionary<string, List<IconEntry>> VariantMap => _variantMap;

        /// <summary>Fired when the filtered/grouped entries change.</summary>
        public event Action OnEntriesChanged;

        /// <summary>Fired when categories are available for the current prefix.</summary>
        public event Action<List<string>> OnCategoriesLoaded;

        /// <summary>Fired when a pending search drains.</summary>
        public event Action<string> OnPendingSearchDrained;

        /// <summary>Fired when loading state changes.</summary>
        public event Action<bool> OnLoadingChanged;

        public BrowseDataController(IconDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Sets the current library prefix and loads its collection.
        /// </summary>
        public async Task SetPrefixAndLoadAsync(string prefix)
        {
            _currentPrefix = prefix;
            _selectedCategory = "";
            _selectedVariant = "";
            await LoadCollectionAsync(prefix);
        }

        /// <summary>
        /// Sets the category filter and reapplies filters.
        /// </summary>
        public void SetCategory(string category)
        {
            _selectedCategory = category;
            ApplyFilters();
        }

        /// <summary>
        /// Sets the variant filter and reapplies filters.
        /// </summary>
        public void SetVariant(string variant)
        {
            _selectedVariant = variant;
            ApplyFilters();
        }

        /// <summary>
        /// Performs a remote search for the given query.
        /// </summary>
        public async Task SearchAsync(string query)
        {
            if (_isLoading)
            {
                _pendingSearchQuery = query;
                return;
            }
            _isLoading = true;
            _pendingSearchQuery = null;
            OnLoadingChanged?.Invoke(true);

            try
            {
                _allEntries = await _db.SearchRemoteAsync(query, _currentPrefix);
                ApplyFilters();
            }
            finally
            {
                _isLoading = false;
                OnLoadingChanged?.Invoke(false);
                DrainPendingSearch();
            }
        }

        /// <summary>
        /// Loads the full collection for the given prefix.
        /// </summary>
        public async Task LoadCollectionAsync(string prefix)
        {
            if (_isLoading) return;
            _isLoading = true;
            _pendingSearchQuery = null;
            OnLoadingChanged?.Invoke(true);

            try
            {
                var names = await _db.GetCollectionIconsAsync(prefix);
                _allEntries = _db.CreateEntries(prefix, names);

                var categories = _db.GetCategories(prefix);
                OnCategoriesLoaded?.Invoke(categories);

                ApplyFilters();
            }
            catch (Exception e)
            {
                Debug.LogError($"[IconBrowser] LoadCollectionAsync error: {e}");
            }
            finally
            {
                _isLoading = false;
                OnLoadingChanged?.Invoke(false);
                DrainPendingSearch();
            }
        }

        /// <summary>
        /// Applies category and variant filters to the current entries.
        /// Pure logic — no side effects beyond updating internal state and firing events.
        /// </summary>
        public void ApplyFilters()
        {
            // 1. Category filter
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

            // 2. Variant filter
            if (!string.IsNullOrEmpty(_selectedVariant))
            {
                if (_selectedVariant == "default")
                {
                    _filteredEntries = _filteredEntries
                        .Where(e => VariantGrouper.ParseVariant(e.Name, _currentPrefix).variantLabel == "")
                        .ToList();
                }
                else
                {
                    _filteredEntries = _filteredEntries
                        .Where(e => VariantGrouper.ParseVariant(e.Name, _currentPrefix).variantLabel == _selectedVariant)
                        .ToList();
                }
            }

            // 3. Variant grouping (only when showing "All")
            if (string.IsNullOrEmpty(_selectedVariant))
            {
                var (reps, map) = VariantGrouper.GroupEntries(_filteredEntries, _currentPrefix);
                _variantMap = map;
                _groupedEntries = reps;

                foreach (var kv in _variantMap)
                {
                    foreach (var entry in kv.Value)
                    {
                        var (_, suffix) = VariantGrouper.ParseVariant(entry.Name, _currentPrefix);
                        entry.VariantLabel = suffix;
                    }
                }
                foreach (var rep in _groupedEntries)
                {
                    var (baseName, _) = VariantGrouper.ParseVariant(rep.Name, _currentPrefix);
                    rep.VariantCount = _variantMap.TryGetValue(baseName, out var group) ? group.Count : 1;
                }
            }
            else
            {
                _groupedEntries = _filteredEntries;
                _variantMap = new Dictionary<string, List<IconEntry>>();
                foreach (var entry in _groupedEntries)
                {
                    entry.VariantCount = 1;
                    var (_, suffix) = VariantGrouper.ParseVariant(entry.Name, _currentPrefix);
                    entry.VariantLabel = suffix;
                }
            }

            OnEntriesChanged?.Invoke();
        }

        /// <summary>
        /// Syncs IsImported state of all entries with the database.
        /// </summary>
        public void SyncImportState()
        {
            foreach (var entry in _allEntries)
                entry.IsImported = _db.IsImported(entry.Name);
        }

        void DrainPendingSearch()
        {
            if (_pendingSearchQuery == null) return;
            var query = _pendingSearchQuery;
            _pendingSearchQuery = null;
            OnPendingSearchDrained?.Invoke(query);
        }
    }
}
