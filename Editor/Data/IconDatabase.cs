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
    /// </summary>
    public class IconDatabase
    {
        readonly List<IconEntry> _localIcons = new();
        readonly Dictionary<string, IconLibrary> _libraries = new();
        readonly Dictionary<string, List<string>> _collectionCache = new();
        readonly Dictionary<string, Dictionary<string, List<string>>> _categoryCache = new();
        readonly HashSet<string> _importedNames = new();

        public IReadOnlyList<IconEntry> LocalIcons => _localIcons;
        public IReadOnlyDictionary<string, IconLibrary> Libraries => _libraries;

        public int LocalCount => _localIcons.Count;
        public bool LibrariesLoaded => _libraries.Count > 0;

        public event Action OnLocalIconsChanged;

        /// <summary>
        /// Scans the local icons folder for imported SVG VectorImages.
        /// </summary>
        public void ScanLocalIcons()
        {
            _localIcons.Clear();
            _importedNames.Clear();

            var iconsPath = IconBrowserSettings.IconsPath;
            if (!AssetDatabase.IsValidFolder(iconsPath)) return;

            var guids = AssetDatabase.FindAssets("t:VectorImage", new[] { iconsPath });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".svg")) continue;

                var asset = AssetDatabase.LoadAssetAtPath<VectorImage>(path);
                if (asset == null) continue;

                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                _localIcons.Add(new IconEntry
                {
                    Name = name,
                    Prefix = "lucide",
                    IsImported = true,
                    LocalAsset = asset,
                    Tags = Array.Empty<string>(),
                    Categories = Array.Empty<string>()
                });
                _importedNames.Add(name);
            }

            _localIcons.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            OnLocalIconsChanged?.Invoke();
        }

        /// <summary>
        /// Returns filtered local icons by search query.
        /// </summary>
        public List<IconEntry> SearchLocal(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<IconEntry>(_localIcons);

            var q = query.ToLowerInvariant();
            return _localIcons.Where(e => e.Name.Contains(q)).ToList();
        }

        /// <summary>
        /// Loads available icon libraries from Iconify API.
        /// </summary>
        public async Task LoadLibrariesAsync()
        {
            if (_libraries.Count > 0) return;

            try
            {
                var collections = await IconifyClient.GetCollectionsAsync();
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
        /// Lucide first, then popular libs, then grouped by category.
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

                // Then by category
                int catCmp = string.Compare(a.Category ?? "", b.Category ?? "", StringComparison.OrdinalIgnoreCase);
                if (catCmp != 0) return catCmp;

                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /// <summary>
        /// Fetches all icon names for a given collection prefix.
        /// Results are cached. Also caches category data.
        /// </summary>
        public async Task<List<string>> GetCollectionIconsAsync(string prefix)
        {
            if (_collectionCache.TryGetValue(prefix, out var cached))
                return cached;

            try
            {
                var data = await IconifyClient.GetCollectionAsync(prefix);
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
        /// Returns category names for a given collection prefix (cached from GetCollectionIconsAsync).
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
                var result = await IconifyClient.SearchAsync(query, prefix, limit);
                return result.Icons.Select(fullName =>
                {
                    var parts = fullName.Split(':');
                    var p = parts.Length > 1 ? parts[0] : prefix;
                    var n = parts.Length > 1 ? parts[1] : parts[0];
                    return new IconEntry
                    {
                        Name = n,
                        Prefix = p,
                        IsImported = _importedNames.Contains(n),
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
        /// Fills Categories from cached category data.
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
                    IsImported = _importedNames.Contains(n),
                    Tags = Array.Empty<string>(),
                    Categories = entryCategories
                };
            }).ToList();
        }

        /// <summary>
        /// Checks if an icon name is already imported locally.
        /// </summary>
        public bool IsImported(string name) => _importedNames.Contains(name);

        /// <summary>
        /// Marks an icon as imported and refreshes local cache.
        /// </summary>
        public void MarkImported(string name)
        {
            _importedNames.Add(name);
            ScanLocalIcons();
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
