using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using IconBrowser.Data;

namespace IconBrowser.UI
{
    /// <summary>
    /// Pure data controller for the Browse tab.
    /// Handles collection loading, filtering, variant grouping, and search.
    /// No UI dependencies — fully testable.
    /// </summary>
    internal class BrowseDataController
    {
        private readonly IconDatabase _db;

        private string _currentPrefix = "lucide";
        private string _selectedCategory = "";
        private string _selectedVariant = "";
        private bool _isLoading;
        private bool _hasGlobalSearchResults;
        private string _pendingSearchQuery;
        private int _pendingSearchVersion;
        private int _loadingRequestVersion;

        private List<IconEntry> _allEntries = new();
        private List<IconEntry> _filteredEntries = new();
        private List<IconEntry> _groupedEntries = new();
        private Dictionary<string, List<IconEntry>> _variantMap = new();

        public string CurrentPrefix => _currentPrefix;
        public bool IsLoading => _isLoading;
        public List<IconEntry> AllEntries => _allEntries;
        public List<IconEntry> FilteredEntries => _filteredEntries;
        public List<IconEntry> GroupedEntries => _groupedEntries;
        public IReadOnlyDictionary<string, List<IconEntry>> VariantMap => _variantMap;

        public event Action OnEntriesChanged = delegate { };
        public event Action<List<string>> OnCategoriesLoaded = delegate { };
        public event Action<string, int> OnPendingSearchDrained = delegate { };
        public event Action<bool> OnLoadingChanged = delegate { };

        public BrowseDataController(IconDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task SetPrefixAndLoadAsync(
            string prefix,
            int requestVersion = 0,
            CancellationToken ct = default,
            bool preserveBrowseFilters = false)
        {
            ct.ThrowIfCancellationRequested();
            _currentPrefix = prefix;
            _hasGlobalSearchResults = false;
            if (!preserveBrowseFilters)
            {
                _selectedCategory = "";
                _selectedVariant = "";
            }

            await LoadCollectionAsync(prefix, requestVersion, ct);
            ct.ThrowIfCancellationRequested();
        }

        public void SetCategory(string category)
        {
            _selectedCategory = category;
            ApplyFilters();
        }

        public void SetVariant(string variant)
        {
            _selectedVariant = variant;
            ApplyFilters();
        }

        public async Task ExecuteGlobalSearchAsync(string query, int requestVersion = 0, CancellationToken ct = default)
        {
            var normalizedQuery = query?.Trim();
            if (string.IsNullOrEmpty(normalizedQuery))
                return;

            ct.ThrowIfCancellationRequested();
            if (_isLoading && _loadingRequestVersion == requestVersion)
            {
                _pendingSearchQuery = normalizedQuery;
                _pendingSearchVersion = requestVersion;
                return;
            }

            _isLoading = true;
            _loadingRequestVersion = requestVersion;
            _pendingSearchQuery = null;
            _pendingSearchVersion = 0;
            OnLoadingChanged?.Invoke(true);

            try
            {
                ct.ThrowIfCancellationRequested();
                var entries = await _db.SearchRemoteAsync(normalizedQuery, string.Empty);
                ct.ThrowIfCancellationRequested();
                if (requestVersion != 0 && _loadingRequestVersion != requestVersion)
                    return;

                _allEntries = entries;
                _hasGlobalSearchResults = true;
                ApplyFilters();
            }
            finally
            {
                CompleteRequest(ct, requestVersion);
            }
        }

        public async Task LoadCollectionAsync(string prefix, int requestVersion = 0, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (_isLoading && _loadingRequestVersion == requestVersion)
                return;

            _isLoading = true;
            _loadingRequestVersion = requestVersion;
            _pendingSearchQuery = null;
            _pendingSearchVersion = 0;
            OnLoadingChanged?.Invoke(true);

            try
            {
                ct.ThrowIfCancellationRequested();
                var names = await _db.GetCollectionIconsAsync(prefix);
                ct.ThrowIfCancellationRequested();
                if (requestVersion != 0 && _loadingRequestVersion != requestVersion)
                    return;

                _allEntries = _db.CreateEntries(prefix, names);

                var categories = _db.GetCategories(prefix);
                OnCategoriesLoaded?.Invoke(categories);

                ApplyFilters();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogError($"[IconBrowser] LoadCollectionAsync error: {e}");
            }
            finally
            {
                CompleteRequest(ct, requestVersion);
            }
        }

        public void ApplyFilters()
        {
            if (_hasGlobalSearchResults)
            {
                ApplyGlobalSearchFilters();
                OnEntriesChanged?.Invoke();
                return;
            }

            ApplyBrowseFilters();
            OnEntriesChanged?.Invoke();
        }

        private void ApplyGlobalSearchFilters()
        {
            _filteredEntries = new List<IconEntry>(_allEntries);
            _groupedEntries = new List<IconEntry>(_filteredEntries);
            _variantMap = new Dictionary<string, List<IconEntry>>();

            foreach (var entry in _groupedEntries)
            {
                entry.VariantCount = 1;
                var (_, suffix) = VariantGrouper.ParseVariant(entry.Name, entry.Prefix);
                entry.VariantLabel = suffix;
            }
        }

        private void ApplyBrowseFilters()
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
        }

        public void SyncImportState()
        {
            foreach (var entry in _allEntries)
                entry.IsImported = _db.IsImported(entry.Name, entry.Prefix);
        }

        private void DrainPendingSearch()
        {
            if (_pendingSearchQuery == null) return;

            var query = _pendingSearchQuery;
            var requestVersion = _pendingSearchVersion;
            _pendingSearchQuery = null;
            _pendingSearchVersion = 0;
            OnPendingSearchDrained?.Invoke(query, requestVersion);
        }

        private void CompleteRequest(CancellationToken ct, int requestVersion)
        {
            if (_loadingRequestVersion != requestVersion)
                return;

            _isLoading = false;
            OnLoadingChanged?.Invoke(false);

            if (ct.IsCancellationRequested)
            {
                _pendingSearchQuery = null;
                _pendingSearchVersion = 0;
                return;
            }

            DrainPendingSearch();
        }
    }
}
