using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IconBrowser.Data
{
    /// <summary>
    /// Tracks which library each imported icon came from.
    /// Stored as a JSON file alongside the icons folder.
    /// Implements IIconManifest for dependency injection.
    /// Use <see cref="Default"/> for the shared singleton instance.
    /// </summary>
    internal class IconManifest : IIconManifest
    {
        private Dictionary<string, string> _data;
        private string _loadedPath;

        /// <summary>
        /// Shared default instance for backward compatibility.
        /// </summary>
        public static readonly IconManifest Default = new();

        private string ManifestPath
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

        #endregion IIconManifest (instance methods)

        private void EnsureLoaded()
        {
            var path = ManifestPath;
            if (_data != null && _loadedPath == path) return;

            _data = new Dictionary<string, string>();
            _loadedPath = path;

            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            ParseJson(json);
        }

        private void Save()
        {
            var path = ManifestPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, ToJson());
        }

        private string ToJson()
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

        private void ParseJson(string json)
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
