using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        const string TEMP_ASSETS_DIR = "Assets/_IconBrowserTemp";
        const int MAX_BATCH_SIZE = 100;

        readonly Dictionary<string, IconAtlas> _atlases = new();
        readonly HashSet<string> _pendingKeys = new();
        readonly HashSet<string> _failedKeys = new();
        readonly Queue<(string prefix, List<string> names, Action onComplete)> _queue = new();
        bool _isProcessing;

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

        async Task ProcessQueue()
        {
            _isProcessing = true;
            while (_queue.Count > 0)
            {
                var (prefix, names, onComplete) = _queue.Dequeue();
                await FetchAndPackBatch(prefix, names, onComplete);
            }
            _isProcessing = false;
        }

        async Task FetchAndPackBatch(string prefix, List<string> names, Action onComplete)
        {
            try
            {
                // 1. Fetch SVG bodies from Iconify API
                var svgBodies = await IconifyClient.GetIconsBatchAsync(prefix, names.ToArray());
                if (svgBodies.Count == 0) return;

                // 2. Write SVGs to temp Assets folder
                EnsureTempAssetsDir();
                var writtenPaths = new List<(string name, string svgPath)>();
                foreach (var kv in svgBodies)
                {
                    var svgPath = $"{TEMP_ASSETS_DIR}/{prefix}_{kv.Key}.svg";
                    var fullPath = Path.GetFullPath(svgPath);
                    File.WriteAllText(fullPath, kv.Value);

                    var metaPath = fullPath + ".meta";
                    if (!File.Exists(metaPath))
                        File.WriteAllText(metaPath, GenerateTextureMeta(Guid.NewGuid().ToString("N")));

                    writtenPaths.Add((kv.Key, svgPath));
                }

                // 3. Batch-import SVGs (StartAssetEditing suppresses per-file refresh)
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

                // 4. Load textures and pack into atlas
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

                // 5. Save atlas to disk (PNG + index)
                atlas.Save();

                // 6. Clean up temp files immediately
                CleanupTempBatch(writtenPaths);

                Debug.Log($"[IconBrowser] Packed {writtenPaths.Count} previews into {prefix} atlas ({atlas.Count} total)");
                onComplete?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IconBrowser] Preview batch failed: {e.Message}");
            }
            finally
            {
                foreach (var name in names)
                    _pendingKeys.Remove($"{prefix}_{name}");
            }
        }

        /// <summary>
        /// Loads the Texture2D from an imported SVG asset.
        /// Tries Texture2D directly, then Sprite.texture, then sub-assets.
        /// </summary>
        static Texture2D LoadImportedTexture(string assetPath)
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

        void CleanupTempBatch(List<(string name, string svgPath)> paths)
        {
            foreach (var (_, svgPath) in paths)
            {
                var fullPath = Path.GetFullPath(svgPath);
                if (File.Exists(fullPath)) File.Delete(fullPath);
                if (File.Exists(fullPath + ".meta")) File.Delete(fullPath + ".meta");
            }
        }

        IconAtlas GetOrLoadAtlas(string prefix)
        {
            if (_atlases.TryGetValue(prefix, out var cached)) return cached;

            var loaded = IconAtlas.Load(prefix);
            if (loaded != null)
            {
                _atlases[prefix] = loaded;
                return loaded;
            }
            return null;
        }

        IconAtlas GetOrCreateAtlas(string prefix)
        {
            if (_atlases.TryGetValue(prefix, out var existing)) return existing;
            var atlas = IconAtlas.Create(prefix);
            _atlases[prefix] = atlas;
            return atlas;
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
            _pendingKeys.Clear();
            _failedKeys.Clear();
            CleanupTempAssets();
        }

        #region Temp Assets

        static void EnsureTempAssetsDir()
        {
            if (AssetDatabase.IsValidFolder(TEMP_ASSETS_DIR)) return;
            var parent = Path.GetDirectoryName(TEMP_ASSETS_DIR)!.Replace('\\', '/');
            var folderName = Path.GetFileName(TEMP_ASSETS_DIR);
            AssetDatabase.CreateFolder(parent, folderName);
        }

        /// <summary>
        /// Meta file for SVG import as TexturedSprite (small texture for fast import).
        /// </summary>
        static string GenerateTextureMeta(string guid)
        {
            return $@"fileFormatVersion: 2
guid: {guid}
ScriptedImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 2
  userData:
  assetBundleName:
  assetBundleVariant:
  script: {{fileID: 12408, guid: 0000000000000000e000000000000000, type: 0}}
  svgType: 1
  texturedSpriteMeshType: 0
  svgPixelsPerUnit: 100
  gradientResolution: 64
  alignment: 0
  customPivot: {{x: 0, y: 0}}
  generatePhysicsShape: 0
  viewportOptions: 1
  preserveViewport: 0
  advancedMode: 0
  tessellationMode: 0
  predefinedResolutionIndex: 0
  targetResolution: 64
  resolutionMultiplier: 1
  stepDistance: 10
  samplingStepDistance: 100
  maxCordDeviationEnabled: 0
  maxCordDeviation: 1
  maxTangentAngleEnabled: 0
  maxTangentAngle: 5
  keepTextureAspectRatio: 1
  textureSize: 64
  textureWidth: 64
  textureHeight: 64
  wrapMode: 0
  filterMode: 1
  sampleCount: 4
  preserveSVGImageAspect: 0
  useSVGPixelsPerUnit: 0
  spriteData:
    TessellationDetail: 0
    SpriteName:
    SpritePivot: {{x: 0, y: 0}}
    SpriteAlignment: 0
    SpriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
    SpriteRect:
      serializedVersion: 2
      x: 0
      y: 0
      width: 0
      height: 0
    SpriteID:
    PhysicsOutlines: []
";
        }

        #endregion
    }
}
