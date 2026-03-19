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
        private const int MAX_TOOLTIP_CACHE_COUNT = 512;
        private const int FAILED_RETRY_DELAY_MS = 45000;
        private const int TEMP_STAGING_SLOT_COUNT = 256;
        private const string TEMP_SLOT_FILE_PREFIX = "__iconbrowser_slot_";

        private readonly IIconifyClient _client;
        private readonly Dictionary<string, IconAtlas> _atlases = new();
        private readonly Dictionary<string, long> _atlasAccessTime = new(); // prefix → tick
        private readonly HashSet<string> _protectedPrefixes = new();
        private readonly HashSet<string> _pendingKeys = new();
        private readonly Dictionary<string, long> _failedUntilTicks = new();
        private readonly Dictionary<string, string> _tempSvgHashes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _importValidatedTempAssets = new(StringComparer.Ordinal);
        private readonly Queue<(string prefix, List<string> names, Action onComplete)> _queue = new();
        private readonly object _queueLock = new();
        private readonly Dictionary<string, Sprite> _tooltipSprites = new();
        private readonly Dictionary<string, long> _tooltipAccessTicks = new();
        private bool _isTempStagingPrepared;
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
        /// Gets a standalone sprite copy for the icon.
        /// The sprite is copied into a standalone texture so it survives atlas eviction.
        /// </summary>
        public Sprite GetStablePreview(string prefix, string name)
        {
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(name))
                return null;

            var key = BuildPreviewKey(prefix, name);
            if (_tooltipSprites.TryGetValue(key, out var cached))
            {
                if (cached != null)
                {
                    TouchTooltipKey(key);
                    return cached;
                }

                _tooltipSprites.Remove(key);
                _tooltipAccessTicks.Remove(key);
            }

            var baseSprite = GetPreview(prefix, name);
            if (baseSprite == null)
                return null;

            var cloned = CloneSprite(baseSprite);
            if (cloned == null)
                return baseSprite;

            _tooltipSprites[key] = cloned;
            TouchTooltipKey(key);
            EvictTooltipLruIfNeeded();
            return cloned;
        }

        /// <summary>
        /// Gets a tooltip-dedicated sprite for the icon.
        /// Returns a standalone copy so tooltip previews survive atlas eviction.
        /// </summary>
        public Sprite GetTooltipPreview(string prefix, string name)
        {
            return GetStablePreview(prefix, name);
        }

        /// <summary>
        /// Promotes loaded previews into tooltip-dedicated cache.
        /// </summary>
        public void WarmTooltipCache(string prefix, IEnumerable<string> names)
        {
            if (string.IsNullOrEmpty(prefix) || names == null)
                return;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                GetStablePreview(prefix, name);
            }
        }

        /// <summary>
        /// Queues a batch of icons for preview loading.
        /// Icons already in the atlas are skipped.
        /// </summary>
        public async Task LoadPreviewBatchAsync(string prefix, List<string> names, Action onComplete = null)
        {
            var atlas = GetOrLoadAtlas(prefix) ?? GetOrCreateAtlas(prefix);
            PurgeExpiredFailedKeys();

            var toFetch = new List<string>();
            bool hasPendingRequested = false;
            foreach (var name in names)
            {
                var key = $"{prefix}_{name}";
                if (atlas.HasIcon(name) || IsTemporarilyFailed(key))
                    continue;
                if (_pendingKeys.Contains(key))
                {
                    hasPendingRequested = true;
                    continue;
                }
                _pendingKeys.Add(key);
                toFetch.Add(name);
            }

            if (toFetch.Count == 0)
            {
                if (hasPendingRequested)
                {
                    var pendingDrained = await WaitForPendingKeysAsync(prefix, names);
                    if (!pendingDrained)
                        Debug.LogWarning($"[IconBrowser] Timed out waiting preview pending keys: {prefix} ({names.Count} names)");
                }
                onComplete?.Invoke();
                return;
            }

            bool shouldStartProcessor = false;
            lock (_queueLock)
            {
                // Split into batches of MAX_BATCH_SIZE, only last batch gets the callback
                for (int i = 0; i < toFetch.Count; i += MAX_BATCH_SIZE)
                {
                    int count = Math.Min(MAX_BATCH_SIZE, toFetch.Count - i);
                    var batch = toFetch.GetRange(i, count);
                    bool isLast = i + count >= toFetch.Count;
                    _queue.Enqueue((prefix, batch, isLast ? onComplete : null));
                }

                if (!_isProcessing)
                {
                    _isProcessing = true;
                    shouldStartProcessor = true;
                }
            }

            if (shouldStartProcessor)
                _ = ProcessQueue();

            var drained = await WaitForPendingKeysAsync(prefix, names);
            if (!drained)
                Debug.LogWarning($"[IconBrowser] Timed out waiting preview batch completion: {prefix} ({names.Count} names)");
        }

        private async Task<bool> WaitForPendingKeysAsync(string prefix, List<string> names)
        {
            if (names == null || names.Count == 0) return true;

            const int MAX_WAIT_MS = 8000;
            const int STEP_MS = 16;
            int waitedMs = 0;

            while (waitedMs < MAX_WAIT_MS)
            {
                bool hasPending = false;
                foreach (var name in names)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (_pendingKeys.Contains($"{prefix}_{name}"))
                    {
                        hasPending = true;
                        break;
                    }
                }

                if (!hasPending)
                    return true;

                await Task.Delay(STEP_MS);
                waitedMs += STEP_MS;
            }

            return false;
        }

        private async Task ProcessQueue()
        {
            while (true)
            {
                (string prefix, List<string> names, Action onComplete) job;
                lock (_queueLock)
                {
                    if (_queue.Count == 0)
                    {
                        _isProcessing = false;
                        _isTempStagingPrepared = false;
                        CleanupTempAssets();
                        return;
                    }

                    job = _queue.Dequeue();
                }

                var (prefix, names, onComplete) = job;
                await FetchAndPackBatch(prefix, names, onComplete);
            }
        }

        private async Task FetchAndPackBatch(string prefix, List<string> names, Action onComplete)
        {
            var writtenPaths = new List<TempSvgEntry>();
            try
            {
                var svgBodies = await FetchSvgBodiesAsync(prefix, names);
                if (svgBodies.Count == 0)
                {
                    foreach (var name in names)
                        MarkTemporarilyFailed(BuildPreviewKey(prefix, name));
                    if (IconBrowserSettings.VerboseCacheLogs)
                        Debug.LogWarning($"[IconBrowser] No preview payload returned: {prefix} ({names.Count} names)");
                    return;
                }

                int unresolvedCount = 0;
                foreach (var name in names)
                {
                    if (svgBodies.ContainsKey(name)) continue;
                    MarkTemporarilyFailed(BuildPreviewKey(prefix, name));
                    unresolvedCount++;
                }
                if (unresolvedCount > 0 && IconBrowserSettings.VerboseCacheLogs)
                    Debug.LogWarning($"[IconBrowser] Missing {unresolvedCount} previews from payload: {prefix}");

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
                // Do not delete temp SVG assets per batch.
                // Immediate file-system deletion can race with Unity's SourceAssetDB worker
                // and produce "modification time ... 0001-01-01" import errors.
                // Temp assets are cleaned up at cache destroy via CleanupTempAssets().

                foreach (var name in names)
                    _pendingKeys.Remove(BuildPreviewKey(prefix, name));
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
        private List<TempSvgEntry> WriteTempSvgs(string prefix, Dictionary<string, string> bodies)
        {
            var writtenPaths = new List<TempSvgEntry>();
            EnsureTempAssetsDir();
            EnsureTempStagingLayout();

            var usedSlots = new bool[TEMP_STAGING_SLOT_COUNT];

            foreach (var kv in bodies)
            {
                var slot = AcquireStagingSlot(prefix, kv.Key, usedSlots);
                var svgPath = BuildTempSlotSvgPath(slot);
                var fullPath = Path.GetFullPath(svgPath);
                var svgBody = kv.Value ?? string.Empty;
                var bodyHash = ComputeBodyHash(svgBody);
                try
                {
                    bool needsWrite = true;
                    if (File.Exists(fullPath))
                    {
                        needsWrite = !TryIsTempSvgUpToDate(svgPath, fullPath, bodyHash);
                    }

                    if (needsWrite)
                    {
                        File.WriteAllText(fullPath, svgBody);
                        _tempSvgHashes[svgPath] = bodyHash;
                        _importValidatedTempAssets.Remove(svgPath);
                    }

                    var metaPath = fullPath + ".meta";
                    if (!File.Exists(metaPath))
                        File.WriteAllText(metaPath, IconImporter.GenerateMeta(
                            Guid.NewGuid().ToString("N"),
                            svgType: 1, textureSize: 64,
                            predefinedResolutionIndex: 0, targetResolution: 64));

                    bool needsImport = needsWrite || !IsTempAssetImported(svgPath);
                    writtenPaths.Add(new TempSvgEntry(kv.Key, svgPath, needsImport));
                }
                catch (IOException e)
                {
                    MarkTemporarilyFailed(BuildPreviewKey(prefix, kv.Key));
                    Debug.LogWarning($"[IconBrowser] Failed to write SVG file {fullPath}: {e.Message}");
                }
            }

            return writtenPaths;
        }

        /// <summary>
        /// Phase 3: Batch-import SVGs via AssetDatabase (StartAssetEditing suppresses per-file refresh).
        /// </summary>
        private void ImportTempAssets(List<TempSvgEntry> writtenPaths)
        {
            bool hasImportTarget = false;
            foreach (var entry in writtenPaths)
            {
                if (!entry.NeedsImport) continue;
                hasImportTarget = true;
                break;
            }
            if (!hasImportTarget) return;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var entry in writtenPaths)
                {
                    if (!entry.NeedsImport) continue;
                    AssetDatabase.ImportAsset(entry.SvgPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                    _importValidatedTempAssets.Add(entry.SvgPath);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        /// <summary>
        /// Phase 4: Load imported textures, pack into atlas, and save to disk.
        /// </summary>
        private void PackIntoAtlas(string prefix, List<TempSvgEntry> writtenPaths)
        {
            var atlas = GetOrCreateAtlas(prefix);
            foreach (var entry in writtenPaths)
            {
                var tex = LoadImportedTexture(entry.SvgPath);
                if (tex != null)
                {
                    var readable = IconAtlas.MakeReadable(tex, IconAtlas.ICON_SIZE, IconAtlas.ICON_SIZE);
                    atlas.AddIcon(entry.Name, readable);
                    UnityEngine.Object.DestroyImmediate(readable);
                }
                else
                {
                    _importValidatedTempAssets.Remove(entry.SvgPath);
                    MarkTemporarilyFailed(BuildPreviewKey(prefix, entry.Name));
                    Debug.LogWarning($"[IconBrowser] Failed to load texture for: {entry.SvgPath}");
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
                    if (_protectedPrefixes.Contains(kv.Key)) continue;
                    if (kv.Value < oldestTick && _atlases.ContainsKey(kv.Key))
                    {
                        oldestTick = kv.Value;
                        lruPrefix = kv.Key;
                    }
                }
                if (lruPrefix == null)
                {
                    Debug.LogWarning("[IconBrowser] Atlas cache exceeds limit, but all loaded atlases are protected.");
                    break;
                }

                _atlases[lruPrefix].Destroy();
                _atlases.Remove(lruPrefix);
                _atlasAccessTime.Remove(lruPrefix);
                if (IconBrowserSettings.VerboseCacheLogs)
                    Debug.Log($"[IconBrowser] Evicted LRU atlas: {lruPrefix}");
            }
        }

        /// <summary>
        /// Protects specific library atlases from LRU eviction.
        /// Use this for currently visible libraries.
        /// </summary>
        public void SetProtectedPrefixes(IEnumerable<string> prefixes)
        {
            _protectedPrefixes.Clear();
            if (prefixes == null) return;

            foreach (var prefix in prefixes)
            {
                if (string.IsNullOrEmpty(prefix)) continue;
                _protectedPrefixes.Add(prefix);
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
            _failedUntilTicks.Clear();
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
            _protectedPrefixes.Clear();
            _pendingKeys.Clear();
            _failedUntilTicks.Clear();
            _tempSvgHashes.Clear();
            _importValidatedTempAssets.Clear();
            ClearTooltipCache();
            CleanupTempAssets();
        }

        private bool IsTemporarilyFailed(string key)
        {
            if (!_failedUntilTicks.TryGetValue(key, out var untilTicks))
                return false;

            if (DateTime.UtcNow.Ticks <= untilTicks)
                return true;

            _failedUntilTicks.Remove(key);
            return false;
        }

        private void MarkTemporarilyFailed(string key)
        {
            _failedUntilTicks[key] = DateTime.UtcNow.AddMilliseconds(FAILED_RETRY_DELAY_MS).Ticks;
        }

        private void PurgeExpiredFailedKeys()
        {
            if (_failedUntilTicks.Count == 0) return;

            var nowTicks = DateTime.UtcNow.Ticks;
            var expired = new List<string>();
            foreach (var kv in _failedUntilTicks)
            {
                if (kv.Value <= nowTicks)
                    expired.Add(kv.Key);
            }

            foreach (var key in expired)
                _failedUntilTicks.Remove(key);
        }

        private static string BuildPreviewKey(string prefix, string name) => $"{prefix}_{name}";

        private static string ComputeBodyHash(string body)
        {
            return Hash128.Compute(body ?? string.Empty).ToString();
        }

        private static string BuildTempSlotSvgPath(int slotIndex)
        {
            return $"{TEMP_ASSETS_DIR}/{TEMP_SLOT_FILE_PREFIX}{slotIndex:D3}.svg";
        }

        private static int AcquireStagingSlot(string prefix, string name, bool[] usedSlots)
        {
            var preferredSeed = BuildPreviewKey(prefix, name);
            int slotCount = usedSlots.Length;
            int start = (StringComparer.Ordinal.GetHashCode(preferredSeed) & int.MaxValue) % slotCount;

            for (int offset = 0; offset < slotCount; offset++)
            {
                int slot = (start + offset) % slotCount;
                if (usedSlots[slot]) continue;
                usedSlots[slot] = true;
                return slot;
            }

            throw new InvalidOperationException("No temp staging slot available for preview batch.");
        }

        private bool TryIsTempSvgUpToDate(string svgPath, string fullPath, string newBodyHash)
        {
            if (_tempSvgHashes.TryGetValue(svgPath, out var cachedHash))
                return string.Equals(cachedHash, newBodyHash, StringComparison.Ordinal);

            try
            {
                if (!File.Exists(fullPath))
                    return false;

                var existingHash = ComputeBodyHash(File.ReadAllText(fullPath));
                _tempSvgHashes[svgPath] = existingHash;
                return string.Equals(existingHash, newBodyHash, StringComparison.Ordinal);
            }
            catch (IOException)
            {
                return false;
            }
        }

        private bool IsTempAssetImported(string svgPath)
        {
            if (_importValidatedTempAssets.Contains(svgPath))
                return true;

            if (AssetDatabase.LoadAssetAtPath<Texture2D>(svgPath) != null ||
                AssetDatabase.LoadAssetAtPath<Sprite>(svgPath) != null)
            {
                _importValidatedTempAssets.Add(svgPath);
                return true;
            }

            return false;
        }

        private void EnsureTempStagingLayout()
        {
            if (_isTempStagingPrepared)
                return;

            _isTempStagingPrepared = true;

            var fullDir = Path.GetFullPath(TEMP_ASSETS_DIR);
            if (!Directory.Exists(fullDir))
                return;

            try
            {
                var svgFiles = Directory.GetFiles(fullDir, "*.svg", SearchOption.TopDirectoryOnly);
                bool deletedAny = false;
                foreach (var svgFile in svgFiles)
                {
                    var fileName = Path.GetFileName(svgFile);
                    if (fileName.StartsWith(TEMP_SLOT_FILE_PREFIX, StringComparison.Ordinal))
                        continue;

                    File.Delete(svgFile);
                    deletedAny = true;
                    var metaPath = svgFile + ".meta";
                    if (File.Exists(metaPath))
                    {
                        File.Delete(metaPath);
                        deletedAny = true;
                    }
                }

                if (deletedAny)
                    AssetDatabase.Refresh();
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[IconBrowser] Failed to prepare temp staging layout: {e.Message}");
            }
        }

        private readonly struct TempSvgEntry
        {
            public readonly string Name;
            public readonly string SvgPath;
            public readonly bool NeedsImport;

            public TempSvgEntry(string name, string svgPath, bool needsImport)
            {
                Name = name;
                SvgPath = svgPath;
                NeedsImport = needsImport;
            }
        }

        private static Sprite CloneSprite(Sprite source)
        {
            try
            {
                if (source == null || source.texture == null)
                    return null;

                var rect = source.rect;
                int x = Mathf.RoundToInt(rect.x);
                int y = Mathf.RoundToInt(rect.y);
                int width = Mathf.RoundToInt(rect.width);
                int height = Mathf.RoundToInt(rect.height);
                if (width <= 0 || height <= 0)
                    return null;

                var pixels = source.texture.GetPixels(x, y, width, height);
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSaveInEditor
                };
                tex.SetPixels(pixels);
                tex.Apply();

                var sprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, width, height),
                    new Vector2(0.5f, 0.5f),
                    100f,
                    0,
                    SpriteMeshType.FullRect);
                sprite.hideFlags = HideFlags.DontSaveInEditor;
                return sprite;
            }
            catch
            {
                return null;
            }
        }

        private void TouchTooltipKey(string key)
        {
            _tooltipAccessTicks[key] = DateTime.UtcNow.Ticks;
        }

        private void EvictTooltipLruIfNeeded()
        {
            while (_tooltipSprites.Count > MAX_TOOLTIP_CACHE_COUNT)
            {
                string lruKey = null;
                long oldest = long.MaxValue;
                foreach (var kv in _tooltipAccessTicks)
                {
                    if (!_tooltipSprites.ContainsKey(kv.Key)) continue;
                    if (kv.Value >= oldest) continue;
                    oldest = kv.Value;
                    lruKey = kv.Key;
                }

                if (lruKey == null)
                    break;

                if (_tooltipSprites.TryGetValue(lruKey, out var sprite) && sprite != null)
                {
                    var tex = sprite.texture;
                    UnityEngine.Object.DestroyImmediate(sprite);
                    if (tex != null)
                        UnityEngine.Object.DestroyImmediate(tex);
                }

                _tooltipSprites.Remove(lruKey);
                _tooltipAccessTicks.Remove(lruKey);
            }
        }

        private void ClearTooltipCache()
        {
            foreach (var sprite in _tooltipSprites.Values)
            {
                if (sprite == null) continue;
                var tex = sprite.texture;
                UnityEngine.Object.DestroyImmediate(sprite);
                if (tex != null)
                    UnityEngine.Object.DestroyImmediate(tex);
            }

            _tooltipSprites.Clear();
            _tooltipAccessTicks.Clear();
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
