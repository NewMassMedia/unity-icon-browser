using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using IconBrowser.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser.Import
{
    /// <summary>
    /// 2-tier SVG preview cache:
    /// Tier 1: AppData directory (persistent, cross-project) — raw SVG text files
    /// Tier 2: In-memory Dictionary (session-scoped) — loaded VectorImage assets
    ///
    /// Uses a temporary Assets folder for VectorImage conversion via AssetDatabase.
    /// </summary>
    public class SvgPreviewCache
    {
        const string TEMP_ASSETS_DIR = "Assets/_IconBrowserTemp";
        const int MAX_BATCH_SIZE = 25;

        readonly Dictionary<string, VectorImage> _memoryCache = new();
        readonly HashSet<string> _pendingKeys = new();
        readonly Queue<(string prefix, List<string> names, Action onComplete)> _queue = new();
        bool _isProcessing;

        static string AppDataDir
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "IconBrowser");
            }
        }

        static string GetAppDataPath(string prefix, string name)
            => Path.Combine(AppDataDir, $"{prefix}_{name}.svg");

        public VectorImage GetPreview(string prefix, string name)
        {
            var key = $"{prefix}_{name}";
            _memoryCache.TryGetValue(key, out var cached);
            return cached;
        }

        public async Task LoadPreviewBatchAsync(string prefix, List<string> names, Action onComplete = null)
        {
            var toLoad = new List<string>();
            foreach (var name in names)
            {
                var key = $"{prefix}_{name}";
                if (_memoryCache.ContainsKey(key) || _pendingKeys.Contains(key))
                    continue;
                _pendingKeys.Add(key);
                toLoad.Add(name);
                if (toLoad.Count >= MAX_BATCH_SIZE) break;
            }

            if (toLoad.Count == 0) return;

            _queue.Enqueue((prefix, toLoad, onComplete));
            if (!_isProcessing)
                await ProcessQueue();
        }

        async Task ProcessQueue()
        {
            _isProcessing = true;
            while (_queue.Count > 0)
            {
                var (prefix, names, onComplete) = _queue.Dequeue();
                await LoadBatchInternal(prefix, names, onComplete);
            }
            _isProcessing = false;
        }

        async Task LoadBatchInternal(string prefix, List<string> names, Action onComplete)
        {
            try
            {
                EnsureAppDataDir();
                EnsureTempAssetsDir();

                // Separate into cache hits (AppData) and misses (need API fetch)
                var cacheMisses = new List<string>();
                var cacheHits = new List<(string name, string svgContent)>();

                foreach (var name in names)
                {
                    var appDataPath = GetAppDataPath(prefix, name);
                    if (File.Exists(appDataPath))
                    {
                        var svg = File.ReadAllText(appDataPath);
                        cacheHits.Add((name, svg));
                    }
                    else
                    {
                        cacheMisses.Add(name);
                    }
                }

                // Fetch missing from API
                if (cacheMisses.Count > 0)
                {
                    var svgBodies = await IconifyClient.GetIconsBatchAsync(prefix, cacheMisses.ToArray());
                    foreach (var kv in svgBodies)
                    {
                        // Save to AppData (persistent cache)
                        var appDataPath = GetAppDataPath(prefix, kv.Key);
                        File.WriteAllText(appDataPath, kv.Value);
                        cacheHits.Add((kv.Key, kv.Value));
                    }
                }

                if (cacheHits.Count == 0) return;

                // Write all to temp Assets folder for VectorImage conversion
                var writtenPaths = new List<(string key, string path)>();
                foreach (var (name, svgContent) in cacheHits)
                {
                    var svgPath = $"{TEMP_ASSETS_DIR}/{prefix}_{name}.svg";
                    var fullPath = Path.GetFullPath(svgPath);
                    File.WriteAllText(fullPath, svgContent);

                    var metaPath = fullPath + ".meta";
                    if (!File.Exists(metaPath))
                    {
                        var guid = Guid.NewGuid().ToString("N");
                        File.WriteAllText(metaPath, GenerateMetaContent(guid));
                    }

                    writtenPaths.Add((name, svgPath));
                }

                // Batch import
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var (_, path) in writtenPaths)
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                // Load VectorImages into memory cache
                foreach (var (key, path) in writtenPaths)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<VectorImage>(path);
                    if (asset != null)
                        _memoryCache[$"{prefix}_{key}"] = asset;
                }

                onComplete?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IconBrowser] Preview batch load failed: {e.Message}");
            }
            finally
            {
                foreach (var name in names)
                    _pendingKeys.Remove($"{prefix}_{name}");
            }
        }

        public void ClearMemoryCache()
        {
            _memoryCache.Clear();
            _pendingKeys.Clear();
        }

        /// <summary>
        /// Cleans up the temporary Assets folder used for VectorImage conversion.
        /// AppData cache is preserved for cross-session/cross-project reuse.
        /// </summary>
        public void CleanupTempAssets()
        {
            if (AssetDatabase.IsValidFolder(TEMP_ASSETS_DIR))
                AssetDatabase.DeleteAsset(TEMP_ASSETS_DIR);

            _memoryCache.Clear();
            _pendingKeys.Clear();
        }

        static void EnsureAppDataDir()
        {
            if (!Directory.Exists(AppDataDir))
                Directory.CreateDirectory(AppDataDir);
        }

        static void EnsureTempAssetsDir()
        {
            if (AssetDatabase.IsValidFolder(TEMP_ASSETS_DIR)) return;

            var parent = Path.GetDirectoryName(TEMP_ASSETS_DIR)!.Replace('\\', '/');
            var folderName = Path.GetFileName(TEMP_ASSETS_DIR);
            AssetDatabase.CreateFolder(parent, folderName);
        }

        static string GenerateMetaContent(string guid)
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
  svgType: 3
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
  predefinedResolutionIndex: 1
  targetResolution: 1080
  resolutionMultiplier: 1
  stepDistance: 10
  samplingStepDistance: 100
  maxCordDeviationEnabled: 0
  maxCordDeviation: 1
  maxTangentAngleEnabled: 0
  maxTangentAngle: 5
  keepTextureAspectRatio: 1
  textureSize: 256
  textureWidth: 256
  textureHeight: 256
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
    }
}
