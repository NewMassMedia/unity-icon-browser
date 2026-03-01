using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using IconBrowser.Data;
using UnityEditor;
using UnityEngine;

namespace IconBrowser.Import
{
    /// <summary>
    /// Atlas-based icon preview cache.
    /// Each library gets its own 2048x2048 sprite atlas (48x48 per icon, up to 1764 icons).
    /// First visit: SVG → AssetDatabase import → Texture2D → pack into atlas → save PNG.
    /// Subsequent visits: Load atlas PNG (single file) → instant.
    /// </summary>
    public class SvgPreviewCache
    {
        private const string TEMP_ASSETS_DIR = IconBrowserConstants.TEMP_ASSET_PATH;
        private const int MAX_BATCH_SIZE = IconBrowserConstants.MAX_BATCH_SIZE;
        private const int MAX_ATLAS_COUNT = IconBrowserConstants.MAX_ATLAS_COUNT;

        private readonly IIconifyClient _client;
        private readonly Dictionary<string, IconAtlas> _atlases = new();
        private readonly Dictionary<string, long> _atlasAccessTime = new(); // prefix → tick
        private readonly HashSet<string> _pendingKeys = new();
        private readonly HashSet<string> _failedKeys = new();
        private readonly Queue<(string prefix, List<string> names, Action onComplete)> _queue = new();
        private bool _isProcessing;

        public SvgPreviewCache(IIconifyClient client = null)
        {
            _client = client ?? IconifyClient.Default;
        }

        /// <summary>
        /// Gets a cached preview texture for the given icon.
        /// Returns null if not cached — call LoadPreviewBatchAsync to load.
        /// </summary>
        public Sprite GetPreview(string prefix, string name)
        {
            var atlas = GetOrLoadAtlas(prefix);
            return atlas?.GetSprite(name);
        }

        /// <summary>
        /// Queues a batch of icons for preview loading.
        /// Icons already in the atlas are skipped.
        /// </summary>
        public async Task LoadPreviewBatchAsync(string prefix, List<string> names, Action onComplete = null)
        {
            var atlas = GetOrLoadAtlas(prefix) ?? GetOrCreateAtlas(prefix);

            var toFetch = new List<string>();
            foreach (var name in names)
            {
                var key = $"{prefix}_{name}";
                if (atlas.HasIcon(name) || _pendingKeys.Contains(key) || _failedKeys.Contains(key))
                    continue;
                _pendingKeys.Add(key);
                toFetch.Add(name);
            }

            if (toFetch.Count == 0)
            {
                onComplete?.Invoke();
                return;
            }

            // Split into batches of MAX_BATCH_SIZE, only last batch gets the callback
            for (int i = 0; i < toFetch.Count; i += MAX_BATCH_SIZE)
            {
                int count = Math.Min(MAX_BATCH_SIZE, toFetch.Count - i);
                var batch = toFetch.GetRange(i, count);
                bool isLast = i + count >= toFetch.Count;
                _queue.Enqueue((prefix, batch, isLast ? onComplete : null));
            }

            if (!_isProcessing)
                await ProcessQueue();
        }

        private async Task ProcessQueue()
        {
            _isProcessing = true;
            while (_queue.Count > 0)
            {
                var (prefix, names, onComplete) = _queue.Dequeue();
                await FetchAndPackBatch(prefix, names, onComplete);
            }
            _isProcessing = false;
        }

        private async Task FetchAndPackBatch(string prefix, List<string> names, Action onComplete)
        {
            var writtenPaths = new List<(string name, string svgPath)>();
            try
            {
                var svgBodies = await FetchSvgBodiesAsync(prefix, names);
                if (svgBodies.Count == 0) return;

                writtenPaths = WriteTempSvgs(prefix, svgBodies);
                ImportTempAssets(writtenPaths);
                PackIntoAtlas(prefix, writtenPaths);

                Debug.Log($"[IconBrowser] Packed {writtenPaths.Count} previews into {prefix} atlas");
                onComplete?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IconBrowser] Preview batch failed: {e.Message}");
            }
            finally
            {
                if (writtenPaths.Count > 0)
                    CleanupTempBatch(writtenPaths);

                foreach (var name in names)
                    _pendingKeys.Remove($"{prefix}_{name}");
            }
        }

        /// <summary>
        /// Phase 1: Fetch SVG bodies from the Iconify API.
        /// </summary>
        private async Task<Dictionary<string, string>> FetchSvgBodiesAsync(string prefix, List<string> names)
        {
            return await _client.GetIconsBatchAsync(prefix, names.ToArray());
        }

        /// <summary>
        /// Phase 2: Write SVG files and .meta files to the temp Assets folder.
        /// </summary>
        private List<(string name, string svgPath)> WriteTempSvgs(string prefix, Dictionary<string, string> bodies)
        {
            var writtenPaths = new List<(string name, string svgPath)>();
            EnsureTempAssetsDir();

            foreach (var kv in bodies)
            {
                var svgPath = $"{TEMP_ASSETS_DIR}/{prefix}_{kv.Key}.svg";
                var fullPath = Path.GetFullPath(svgPath);
                try
                {
                    File.WriteAllText(fullPath, kv.Value);

                    var metaPath = fullPath + ".meta";
                    if (!File.Exists(metaPath))
                        File.WriteAllText(metaPath, IconImporter.GenerateMeta(
                            Guid.NewGuid().ToString("N"),
                            svgType: 1, textureSize: 64,
                            predefinedResolutionIndex: 0, targetResolution: 64));

                    writtenPaths.Add((kv.Key, svgPath));
                }
                catch (IOException e)
                {
                    _failedKeys.Add($"{prefix}_{kv.Key}");
                    Debug.LogWarning($"[IconBrowser] Failed to write SVG file {fullPath}: {e.Message}");
                }
            }

            return writtenPaths;
        }

        /// <summary>
        /// Phase 3: Batch-import SVGs via AssetDatabase (StartAssetEditing suppresses per-file refresh).
        /// </summary>
        private void ImportTempAssets(List<(string name, string svgPath)> writtenPaths)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (_, path) in writtenPaths)
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        /// <summary>
        /// Phase 4: Load imported textures, pack into atlas, and save to disk.
        /// </summary>
        private void PackIntoAtlas(string prefix, List<(string name, string svgPath)> writtenPaths)
        {
            var atlas = GetOrCreateAtlas(prefix);
            foreach (var (name, path) in writtenPaths)
            {
                var tex = LoadImportedTexture(path);
                if (tex != null)
                {
                    var readable = IconAtlas.MakeReadable(tex, IconAtlas.ICON_SIZE, IconAtlas.ICON_SIZE);
                    atlas.AddIcon(name, readable);
                    UnityEngine.Object.DestroyImmediate(readable);
                }
                else
                {
                    _failedKeys.Add($"{prefix}_{name}");
                    Debug.LogWarning($"[IconBrowser] Failed to load texture for: {path}");
                }
            }

            atlas.Save();
        }

        /// <summary>
        /// Loads the Texture2D from an imported SVG asset.
        /// Tries Texture2D directly, then Sprite.texture, then sub-assets.
        /// </summary>
        private static Texture2D LoadImportedTexture(string assetPath)
        {
            // Try loading directly as Texture2D
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (tex != null) return tex;

            // Try Sprite — TexturedSprite mode produces a Sprite main asset
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (sprite != null && sprite.texture != null)
                return sprite.texture;

            // Fall back to loading all sub-assets
            var objects = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (objects.Length == 0)
            {
                Debug.LogWarning($"[IconBrowser] No assets at path: {assetPath}");
                return null;
            }
            foreach (var obj in objects)
            {
                if (obj is Texture2D t) return t;
                if (obj is Sprite s && s.texture != null) return s.texture;
            }
            Debug.LogWarning($"[IconBrowser] No Texture2D found among {objects.Length} assets at: {assetPath} (types: {string.Join(", ", System.Linq.Enumerable.Select(objects, o => o.GetType().Name))})");
            return null;
        }

        private void CleanupTempBatch(List<(string name, string svgPath)> paths)
        {
            foreach (var (_, svgPath) in paths)
            {
                var fullPath = Path.GetFullPath(svgPath);
                if (File.Exists(fullPath)) File.Delete(fullPath);
                if (File.Exists(fullPath + ".meta")) File.Delete(fullPath + ".meta");
            }
        }

        private IconAtlas GetOrLoadAtlas(string prefix)
        {
            if (_atlases.TryGetValue(prefix, out var cached))
            {
                TouchAtlas(prefix);
                return cached;
            }

            var loaded = IconAtlas.Load(prefix);
            if (loaded != null)
            {
                _atlases[prefix] = loaded;
                TouchAtlas(prefix);
                EvictLruIfNeeded();
                return loaded;
            }
            return null;
        }

        private IconAtlas GetOrCreateAtlas(string prefix)
        {
            if (_atlases.TryGetValue(prefix, out var existing))
            {
                TouchAtlas(prefix);
                return existing;
            }
            var atlas = IconAtlas.Create(prefix);
            _atlases[prefix] = atlas;
            TouchAtlas(prefix);
            EvictLruIfNeeded();
            return atlas;
        }

        private void TouchAtlas(string prefix)
        {
            _atlasAccessTime[prefix] = System.DateTime.UtcNow.Ticks;
        }

        private void EvictLruIfNeeded()
        {
            while (_atlases.Count > MAX_ATLAS_COUNT)
            {
                // Find the least recently used atlas
                string lruPrefix = null;
                long oldestTick = long.MaxValue;
                foreach (var kv in _atlasAccessTime)
                {
                    if (kv.Value < oldestTick && _atlases.ContainsKey(kv.Key))
                    {
                        oldestTick = kv.Value;
                        lruPrefix = kv.Key;
                    }
                }
                if (lruPrefix == null) break;

                _atlases[lruPrefix].Destroy();
                _atlases.Remove(lruPrefix);
                _atlasAccessTime.Remove(lruPrefix);
                Debug.Log($"[IconBrowser] Evicted LRU atlas: {lruPrefix}");
            }
        }

        /// <summary>
        /// Clears in-memory state for the current library (when switching libraries).
        /// Does NOT delete persistent atlas files.
        /// </summary>
        public void ClearMemoryCache()
        {
            // Atlas stays in _atlases dict — it will be reused.
            // Just clear pending operations.
            _pendingKeys.Clear();
            _failedKeys.Clear();
        }

        /// <summary>
        /// Cleans up temp assets folder. Atlas files in AppData are preserved.
        /// </summary>
        public void CleanupTempAssets()
        {
            if (AssetDatabase.IsValidFolder(TEMP_ASSETS_DIR))
                AssetDatabase.DeleteAsset(TEMP_ASSETS_DIR);
        }

        /// <summary>
        /// Destroys all atlas textures and sprites (call on window close).
        /// </summary>
        public void Destroy()
        {
            foreach (var kv in _atlases)
                kv.Value.Destroy();
            _atlases.Clear();
            _atlasAccessTime.Clear();
            _pendingKeys.Clear();
            _failedKeys.Clear();
            CleanupTempAssets();
        }

        #region Temp Assets

        private static void EnsureTempAssetsDir()
        {
            if (AssetDatabase.IsValidFolder(TEMP_ASSETS_DIR)) return;
            var parent = Path.GetDirectoryName(TEMP_ASSETS_DIR)!.Replace('\\', '/');
            var folderName = Path.GetFileName(TEMP_ASSETS_DIR);
            AssetDatabase.CreateFolder(parent, folderName);
        }


        #endregion Temp Assets
    }
}
