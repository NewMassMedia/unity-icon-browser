using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IconBrowser.Data
{
    /// <summary>
    /// Tracks which library each imported icon came from.
    /// Stored as a JSON file alongside the icons folder.
    /// </summary>
    static class IconManifest
    {
        static Dictionary<string, string> _data;
        static string _loadedPath;

        static string ManifestPath
        {
            get
            {
                var iconsDir = Path.GetFullPath(IconBrowserSettings.IconsPath);
                return Path.Combine(iconsDir, ".icon_manifest.json");
            }
        }

        /// <summary>
        /// Gets the source library prefix for a given icon name.
        /// Returns null if unknown.
        /// </summary>
        public static string GetPrefix(string name)
        {
            EnsureLoaded();
            return _data.TryGetValue(name, out var p) ? p : null;
        }

        /// <summary>
        /// Records an icon's source library.
        /// </summary>
        public static void Set(string name, string prefix)
        {
            EnsureLoaded();
            _data[name] = prefix;
            Save();
        }

        /// <summary>
        /// Removes an icon from the manifest.
        /// </summary>
        public static void Remove(string name)
        {
            EnsureLoaded();
            if (_data.Remove(name))
                Save();
        }

        /// <summary>
        /// Returns all tracked icon-to-prefix mappings.
        /// </summary>
        public static IReadOnlyDictionary<string, string> GetAll()
        {
            EnsureLoaded();
            return _data;
        }

        /// <summary>
        /// Returns distinct library prefixes present in imported icons.
        /// </summary>
        public static HashSet<string> GetPrefixes()
        {
            EnsureLoaded();
            return new HashSet<string>(_data.Values);
        }

        /// <summary>
        /// Reassigns all icons with "unknown" prefix to the given prefix and saves.
        /// Returns the number of icons reassigned.
        /// </summary>
        public static int ReassignUnknowns(string newPrefix)
        {
            EnsureLoaded();
            int count = 0;

            // Find icon names that are NOT in the manifest yet (will show as "unknown")
            // We can't know them here, so this is called from IconDatabase which passes names.
            // Instead, fix entries already marked "unknown" in the data.
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

        /// <summary>
        /// Adds missing icons to the manifest with the given prefix.
        /// Returns the number of icons added.
        /// </summary>
        public static int AddMissing(IEnumerable<string> names, string prefix)
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

        /// <summary>
        /// Forces a reload from disk on next access.
        /// </summary>
        public static void Invalidate()
        {
            _data = null;
            _loadedPath = null;
        }

        static void EnsureLoaded()
        {
            var path = ManifestPath;
            if (_data != null && _loadedPath == path) return;

            _data = new Dictionary<string, string>();
            _loadedPath = path;

            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            ParseJson(json);
        }

        static void Save()
        {
            var path = ManifestPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, ToJson());
        }

        static string ToJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            bool first = true;
            foreach (var kv in _data)
            {
                if (!first) sb.AppendLine(",");
                sb.Append($"  \"{Escape(kv.Key)}\": \"{Escape(kv.Value)}\"");
                first = false;
            }
            sb.AppendLine();
            sb.Append('}');
            return sb.ToString();
        }

        static void ParseJson(string json)
        {
            json = json.Trim();
            if (json.Length < 2 || json[0] != '{') return;

            int i = 1;
            while (i < json.Length - 1)
            {
                SkipWs(json, ref i);
                if (i >= json.Length - 1 || json[i] == '}') break;
                if (json[i] == ',') { i++; continue; }

                var key = ReadStr(json, ref i);
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ':') i++;
                SkipWs(json, ref i);
                var val = ReadStr(json, ref i);

                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                    _data[key] = val;
            }
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static string ReadStr(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return "";
            i++;
            int start = i;
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\') i++;
                i++;
            }
            var result = s.Substring(start, i - start);
            if (i < s.Length) i++;
            return Unescape(result);
        }

        static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
