using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IconBrowser.Data
{
    /// <summary>
    /// Tracks which library each imported icon came from.
    /// Stored as a JSON file alongside the icons folder.
    /// Implements IIconManifest for dependency injection.
    /// Static convenience methods delegate to <see cref="Default"/>.
    /// </summary>
    class IconManifest : IIconManifest
    {
        Dictionary<string, string> _data;
        string _loadedPath;

        /// <summary>
        /// Shared default instance for backward compatibility.
        /// </summary>
        public static readonly IconManifest Default = new();

        string ManifestPath
        {
            get
            {
                var iconsDir = Path.GetFullPath(IconBrowserSettings.IconsPath);
                return Path.Combine(iconsDir, ".icon_manifest.json");
            }
        }

        #region IIconManifest (instance methods)

        public string GetPrefix(string name)
        {
            EnsureLoaded();
            return _data.TryGetValue(name, out var p) ? p : null;
        }

        public void Set(string name, string prefix)
        {
            EnsureLoaded();
            _data[name] = prefix;
            Save();
        }

        public void Remove(string name)
        {
            EnsureLoaded();
            if (_data.Remove(name))
                Save();
        }

        public IReadOnlyDictionary<string, string> GetAll()
        {
            EnsureLoaded();
            return _data;
        }

        public HashSet<string> GetPrefixes()
        {
            EnsureLoaded();
            return new HashSet<string>(_data.Values);
        }

        public int ReassignUnknowns(string newPrefix)
        {
            EnsureLoaded();
            int count = 0;
            var toFix = new List<string>();
            foreach (var kv in _data)
            {
                if (kv.Value == "unknown")
                    toFix.Add(kv.Key);
            }
            foreach (var name in toFix)
            {
                _data[name] = newPrefix;
                count++;
            }
            if (count > 0) Save();
            return count;
        }

        public int AddMissing(IEnumerable<string> names, string prefix)
        {
            EnsureLoaded();
            int count = 0;
            foreach (var name in names)
            {
                if (!_data.ContainsKey(name))
                {
                    _data[name] = prefix;
                    count++;
                }
            }
            if (count > 0) Save();
            return count;
        }

        public void Invalidate()
        {
            _data = null;
            _loadedPath = null;
        }

        #endregion

        #region Static convenience methods (backward compatibility)

        public static string GetPrefixStatic(string name) => Default.GetPrefix(name);
        public static void SetStatic(string name, string prefix) => Default.Set(name, prefix);
        public static void RemoveStatic(string name) => Default.Remove(name);
        public static IReadOnlyDictionary<string, string> GetAllStatic() => Default.GetAll();
        public static HashSet<string> GetPrefixesStatic() => Default.GetPrefixes();
        public static int ReassignUnknownsStatic(string newPrefix) => Default.ReassignUnknowns(newPrefix);
        public static int AddMissingStatic(IEnumerable<string> names, string prefix) => Default.AddMissing(names, prefix);
        public static void InvalidateStatic() => Default.Invalidate();

        #endregion

        void EnsureLoaded()
        {
            var path = ManifestPath;
            if (_data != null && _loadedPath == path) return;

            _data = new Dictionary<string, string>();
            _loadedPath = path;

            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            ParseJson(json);
        }

        void Save()
        {
            var path = ManifestPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, ToJson());
        }

        string ToJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            bool first = true;
            foreach (var kv in _data)
            {
                if (!first) sb.AppendLine(",");
                sb.Append($"  \"{SimpleJsonParser.Escape(kv.Key)}\": \"{SimpleJsonParser.Escape(kv.Value)}\"");
                first = false;
            }
            sb.AppendLine();
            sb.Append('}');
            return sb.ToString();
        }

        void ParseJson(string json)
        {
            var obj = SimpleJsonParser.ParseJsonObject(json);
            foreach (var kv in obj)
            {
                var key = SimpleJsonParser.Unescape(kv.Key);
                var val = SimpleJsonParser.UnquoteJson(kv.Value);
                val = SimpleJsonParser.Unescape(val);
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                    _data[key] = val;
            }
        }
    }
}
