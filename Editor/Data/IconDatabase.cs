using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser.Data
{
    /// <summary>
    /// Manages local (imported) and remote (Iconify) icon data.
    /// Accepts IIconifyClient and IIconManifest for dependency injection.
    /// </summary>
    public class IconDatabase
    {
        readonly IIconifyClient _client;
        readonly IIconManifest _manifest;

        readonly List<IconEntry> _localIcons = new();
        readonly Dictionary<string, IconLibrary> _libraries = new();
        readonly Dictionary<string, List<string>> _collectionCache = new();
        readonly Dictionary<string, Dictionary<string, List<string>>> _categoryCache = new();
        readonly Dictionary<string, string> _importedNames = new(); // name → prefix

        public IReadOnlyList<IconEntry> LocalIcons => _localIcons;
        public IReadOnlyDictionary<string, IconLibrary> Libraries => _libraries;

        public int LocalCount => _localIcons.Count;
        public bool LibrariesLoaded => _libraries.Count > 0;

        public event Action OnLocalIconsChanged;

        /// <summary>
        /// Creates an IconDatabase with explicit dependencies.
        /// </summary>
        public IconDatabase(IIconifyClient client, IIconManifest manifest)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        }

        /// <summary>
        /// Creates an IconDatabase using default implementations (backward compatibility).
        /// </summary>
        public IconDatabase() : this(IconifyClient.Default, IconManifest.Default) { }

        /// <summary>
        /// Scans the entire project for SVG VectorImage assets.
        /// </summary>
        public void ScanLocalIcons()
        {
            _localIcons.Clear();
            _importedNames.Clear();
            _manifest.Invalidate();

            var manifest = _manifest.GetAll();
            var guids = AssetDatabase.FindAssets("t:VectorImage");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".svg")) continue;
                if (path.StartsWith("Assets/_IconBrowserTemp")) continue;

                var asset = AssetDatabase.LoadAssetAtPath<VectorImage>(path);
                if (asset == null) continue;

                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                var prefix = manifest.TryGetValue(name, out var p) ? p : "unknown";

                _localIcons.Add(new IconEntry
                {
                    Name = name,
                    Prefix = prefix,
                    IsImported = true,
                    LocalAsset = asset,
                    LocalAssetPath = path,
                    Tags = Array.Empty<string>(),
                    Categories = Array.Empty<string>()
                });
                _importedNames[name] = prefix;
            }

            _localIcons.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            OnLocalIconsChanged?.Invoke();
        }

        /// <summary>
        /// Returns filtered local icons by search query and optional library prefix.
        /// </summary>
        public List<IconEntry> SearchLocal(string query, string prefixFilter = "")
        {
            IEnumerable<IconEntry> result = _localIcons;

            if (!string.IsNullOrEmpty(prefixFilter))
                result = result.Where(e => e.Prefix == prefixFilter);

            if (!string.IsNullOrWhiteSpace(query))
            {
                var q = query.ToLowerInvariant();
                result = result.Where(e => e.Name.Contains(q));
            }

            return result.ToList();
        }

        /// <summary>
        /// Returns distinct library prefixes from locally imported icons (including "unknown").
        /// </summary>
        public List<string> GetLocalPrefixes()
        {
            return _localIcons
                .Select(e => e.Prefix)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
        }

        /// <summary>
        /// Reassigns all "unknown" prefix icons to the given prefix and rescans.
        /// </summary>
        public int ReassignUnknownIcons(string newPrefix)
        {
            var unknownNames = _localIcons
                .Where(e => e.Prefix == "unknown")
                .Select(e => e.Name)
                .ToList();

            if (unknownNames.Count == 0) return 0;

            int added = _manifest.AddMissing(unknownNames, newPrefix);
            int fixed2 = _manifest.ReassignUnknowns(newPrefix);
            int total = added + fixed2;

            if (total > 0) ScanLocalIcons();
            return total;
        }

        /// <summary>
        /// Loads available icon libraries from Iconify API.
        /// </summary>
        public async Task LoadLibrariesAsync()
        {
            if (_libraries.Count > 0) return;

            try
            {
                var collections = await _client.GetCollectionsAsync();
                _libraries.Clear();
                foreach (var kv in collections)
                {
                    if (kv.Value.Total > 0)
                        _libraries[kv.Key] = kv.Value;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[IconBrowser] Failed to load libraries: {e.Message}");
            }
        }

        /// <summary>
        /// Gets sorted libraries list for dropdown display.
        /// </summary>
        public List<IconLibrary> GetSortedLibraries()
        {
            var popular = new HashSet<string> { "heroicons", "ph", "tabler", "material-symbols", "mdi" };

            var list = _libraries.Values.ToList();
            list.Sort((a, b) =>
            {
                if (a.Prefix == "lucide") return -1;
                if (b.Prefix == "lucide") return 1;

                bool aPop = popular.Contains(a.Prefix);
                bool bPop = popular.Contains(b.Prefix);
                if (aPop && !bPop) return -1;
                if (!aPop && bPop) return 1;

                int catCmp = string.Compare(a.Category ?? "", b.Category ?? "", StringComparison.OrdinalIgnoreCase);
                if (catCmp != 0) return catCmp;

                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /// <summary>
        /// Fetches all icon names for a given collection prefix.
        /// </summary>
        public async Task<List<string>> GetCollectionIconsAsync(string prefix)
        {
            if (_collectionCache.TryGetValue(prefix, out var cached))
                return cached;

            try
            {
                var data = await _client.GetCollectionAsync(prefix);
                _collectionCache[prefix] = data.IconNames;

                if (data.Categories != null && data.Categories.Count > 0)
                    _categoryCache[prefix] = data.Categories;

                return data.IconNames;
            }
            catch (Exception e)
            {
                Debug.LogError($"[IconBrowser] Failed to load collection '{prefix}': {e.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Returns category names for a given collection prefix.
        /// </summary>
        public List<string> GetCategories(string prefix)
        {
            if (_categoryCache.TryGetValue(prefix, out var cats))
                return cats.Keys.ToList();
            return new List<string>();
        }

        /// <summary>
        /// Returns icon names that belong to a specific category within a collection.
        /// </summary>
        public HashSet<string> GetIconsInCategory(string prefix, string category)
        {
            if (_categoryCache.TryGetValue(prefix, out var cats) &&
                cats.TryGetValue(category, out var names))
                return new HashSet<string>(names);
            return new HashSet<string>();
        }

        /// <summary>
        /// Searches icons via Iconify API.
        /// </summary>
        public async Task<List<IconEntry>> SearchRemoteAsync(string query, string prefix, int limit = 64)
        {
            try
            {
                var result = await _client.SearchAsync(query, prefix, limit);
                if (result?.Icons == null) return new List<IconEntry>();
                return result.Icons.Select(fullName =>
                {
                    var parts = fullName.Split(':');
                    var p = parts.Length > 1 ? parts[0] : prefix;
                    var n = parts.Length > 1 ? parts[1] : parts[0];
                    return new IconEntry
                    {
                        Name = n,
                        Prefix = p,
                        IsImported = _importedNames.TryGetValue(n, out var ip) && ip == p,
                        Tags = Array.Empty<string>(),
                        Categories = Array.Empty<string>()
                    };
                }).ToList();
            }
            catch (Exception e)
            {
                Debug.LogError($"[IconBrowser] Search failed: {e.Message}");
                return new List<IconEntry>();
            }
        }

        /// <summary>
        /// Creates IconEntry list from a list of icon names (from collection).
        /// </summary>
        public List<IconEntry> CreateEntries(string prefix, List<string> names)
        {
            _categoryCache.TryGetValue(prefix, out var cats);

            return names.Select(n =>
            {
                string[] entryCategories = Array.Empty<string>();
                if (cats != null)
                {
                    var matching = new List<string>();
                    foreach (var kv in cats)
                    {
                        if (kv.Value.Contains(n))
                            matching.Add(kv.Key);
                    }
                    if (matching.Count > 0)
                        entryCategories = matching.ToArray();
                }

                return new IconEntry
                {
                    Name = n,
                    Prefix = prefix,
                    IsImported = _importedNames.TryGetValue(n, out var ip) && ip == prefix,
                    Tags = Array.Empty<string>(),
                    Categories = entryCategories
                };
            }).ToList();
        }

        /// <summary>
        /// Checks if an icon name is already imported locally.
        /// </summary>
        public bool IsImported(string name) => _importedNames.ContainsKey(name);

        /// <summary>
        /// Marks an icon as imported and refreshes local cache.
        /// </summary>
        public void MarkImported(string name, string prefix)
        {
            _importedNames[name] = prefix;
            ScanLocalIcons();
        }

        /// <summary>
        /// Sets imported state without triggering a full rescan. For testing only.
        /// </summary>
        internal void SetImportedState(string name, string prefix)
        {
            _importedNames[name] = prefix;
        }

        /// <summary>
        /// Removes an icon from local tracking after delete.
        /// </summary>
        public void MarkDeleted(string name)
        {
            _importedNames.Remove(name);
            ScanLocalIcons();
        }
    }
}
