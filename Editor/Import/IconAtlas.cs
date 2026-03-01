using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace IconBrowser.Import
{
    /// <summary>
    /// Packs icon preview textures into a single 2048x2048 atlas.
    /// Each icon occupies a 48x48 slot. Supports up to 1764 icons per atlas.
    /// Saved to AppData as PNG + JSON index for instant cross-session loading.
    /// </summary>
    public class IconAtlas
    {
        public const int ICON_SIZE = IconBrowserConstants.ICON_SIZE;
        const int ATLAS_SIZE = IconBrowserConstants.ATLAS_SIZE;
        const int ICONS_PER_ROW = ATLAS_SIZE / ICON_SIZE; // 42
        const int MAX_ICONS = ICONS_PER_ROW * ICONS_PER_ROW; // 1764

        readonly string _prefix;
        Texture2D _atlas;
        readonly Dictionary<string, int> _index = new();
        readonly Dictionary<string, Sprite> _spriteCache = new();
        int _nextSlot;
        bool _dirty;

        public int Count => _index.Count;
        public bool HasIcon(string name) => _index.ContainsKey(name);

        IconAtlas(string prefix)
        {
            _prefix = prefix;
        }

        /// <summary>
        /// Creates a new empty atlas for the given library prefix.
        /// </summary>
        public static IconAtlas Create(string prefix)
        {
            var atlas = new IconAtlas(prefix);
            atlas._atlas = new Texture2D(ATLAS_SIZE, ATLAS_SIZE, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSaveInEditor
            };
            // Clear to transparent
            var clear = new Color32[ATLAS_SIZE * ATLAS_SIZE];
            atlas._atlas.SetPixelData(clear, 0);
            atlas._atlas.Apply();
            return atlas;
        }

        /// <summary>
        /// Loads an existing atlas from AppData. Returns null if not found.
        /// </summary>
        public static IconAtlas Load(string prefix)
        {
            var pngPath = GetAtlasPath(prefix);
            var jsonPath = GetIndexPath(prefix);
            if (!File.Exists(pngPath) || !File.Exists(jsonPath))
                return null;

            try
            {
                var atlas = new IconAtlas(prefix);
                atlas._atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSaveInEditor
                };
                atlas._atlas.LoadImage(File.ReadAllBytes(pngPath));

                var json = File.ReadAllText(jsonPath);
                atlas.DeserializeIndex(json);
                atlas._nextSlot = atlas._index.Count;
                return atlas;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IconBrowser] Failed to load atlas for '{prefix}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets an individual Texture2D for the given icon name, extracted from the atlas.
        /// </summary>
        public Sprite GetSprite(string name)
        {
            if (!_index.TryGetValue(name, out var slot)) return null;

            if (_spriteCache.TryGetValue(name, out var cached)) return cached;

            int col = slot % ICONS_PER_ROW;
            int row = slot / ICONS_PER_ROW;
            int x = col * ICON_SIZE;
            int y = ATLAS_SIZE - (row + 1) * ICON_SIZE;

            var sprite = Sprite.Create(
                _atlas,
                new Rect(x, y, ICON_SIZE, ICON_SIZE),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect
            );
            sprite.hideFlags = HideFlags.DontSaveInEditor;

            _spriteCache[name] = sprite;
            return sprite;
        }

        /// <summary>
        /// Adds an icon texture to the atlas. The source texture must be readable.
        /// Returns false if atlas is full.
        /// </summary>
        public bool AddIcon(string name, Texture2D source)
        {
            if (_index.ContainsKey(name)) return true; // already exists
            if (_nextSlot >= MAX_ICONS) return false;

            int col = _nextSlot % ICONS_PER_ROW;
            int row = _nextSlot / ICONS_PER_ROW;
            int x = col * ICON_SIZE;
            int y = ATLAS_SIZE - (row + 1) * ICON_SIZE;

            // Resize source to ICON_SIZE if needed
            var pixels = GetResizedPixels(source, ICON_SIZE, ICON_SIZE);
            _atlas.SetPixels(x, y, ICON_SIZE, ICON_SIZE, pixels);

            _index[name] = _nextSlot;
            _nextSlot++;
            _dirty = true;
            return true;
        }

        /// <summary>
        /// Applies pending changes and saves atlas to disk.
        /// </summary>
        public void Save()
        {
            if (!_dirty) return;

            _atlas.Apply();
            EnsureAppDataDir();

            File.WriteAllBytes(GetAtlasPath(_prefix), _atlas.EncodeToPNG());
            File.WriteAllText(GetIndexPath(_prefix), SerializeIndex());
            _dirty = false;
        }

        public void Destroy()
        {
            foreach (var kv in _spriteCache)
            {
                if (kv.Value != null)
                    UnityEngine.Object.DestroyImmediate(kv.Value);
            }
            _spriteCache.Clear();

            if (_atlas != null)
                UnityEngine.Object.DestroyImmediate(_atlas);
        }

        #region Paths

        public static string CacheDir
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "IconBrowser");
            }
        }

        static string AppDataDir => CacheDir;

        static string GetAtlasPath(string prefix) => Path.Combine(AppDataDir, $"{prefix}_atlas.png");
        static string GetIndexPath(string prefix) => Path.Combine(AppDataDir, $"{prefix}_atlas.json");

        static void EnsureAppDataDir()
        {
            if (!Directory.Exists(AppDataDir))
                Directory.CreateDirectory(AppDataDir);
        }

        /// <summary>
        /// Deletes all cached atlas files from AppData.
        /// </summary>
        public static void ClearAllCaches()
        {
            if (Directory.Exists(AppDataDir))
                Directory.Delete(AppDataDir, true);
        }

        #endregion

        #region Pixel Operations

        static Color[] GetResizedPixels(Texture2D source, int targetW, int targetH)
        {
            var rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Texture2D resized = null;
            try
            {
                rt.filterMode = FilterMode.Bilinear;
                Graphics.Blit(source, rt);

                RenderTexture.active = rt;
                resized = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
                resized.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                resized.Apply();

                return resized.GetPixels();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (resized != null)
                    UnityEngine.Object.DestroyImmediate(resized);
            }
        }

        /// <summary>
        /// Creates a readable copy of a texture using RenderTexture blit.
        /// </summary>
        public static Texture2D MakeReadable(Texture source, int width = -1, int height = -1)
        {
            if (width < 0) width = source.width;
            if (height < 0) height = source.height;

            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(source, rt);

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                return readable;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        #endregion

        #region Index Serialization

        string SerializeIndex()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in _index)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(Data.SimpleJsonParser.Escape(kv.Key)).Append("\":").Append(kv.Value);
                first = false;
            }
            sb.Append('}');
            return sb.ToString();
        }

        void DeserializeIndex(string json)
        {
            _index.Clear();
            var obj = Data.SimpleJsonParser.ParseJsonObject(json);
            foreach (var kv in obj)
            {
                if (int.TryParse(kv.Value.Trim(), out var slot))
                    _index[kv.Key] = slot;
            }
        }

        #endregion
    }
}
