using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;

[assembly: InternalsVisibleTo("IconBrowser.Editor.Tests")]

namespace IconBrowser.Data
{
    /// <summary>
    /// Represents a single icon, either local (imported) or remote (from Iconify API).
    /// </summary>
    public class IconEntry
    {
        public string Name { get; set; }
        public string Prefix { get; set; }
        public string[] Tags { get; set; }
        public string[] Categories { get; set; }
        public bool IsImported { get; set; }
        public VectorImage LocalAsset { get; set; }
        public string LocalAssetPath { get; set; }

        /// <summary>
        /// Number of variant icons in this group (for grid badge display).
        /// </summary>
        public int VariantCount { get; set; }

        /// <summary>
        /// Variant label extracted from suffix (e.g. "bold", "fill", "outline").
        /// </summary>
        public string VariantLabel { get; set; }

        /// <summary>
        /// Cached preview sprite for remote icons (references atlas directly).
        /// </summary>
        public Sprite PreviewSprite { get; set; }

        /// <summary>
        /// Full Iconify identifier (e.g. "lucide:check").
        /// </summary>
        public string FullId => $"{Prefix}:{Name}";

        /// <summary>
        /// Local asset path after import.
        /// </summary>
        public string ImportedAssetPath => $"{IconBrowserSettings.IconsPath}/{Prefix}/{Name}.svg";

        /// <summary>
        /// Resources.Load path (e.g. "Icons/lucide/check").
        /// </summary>
        public string ResourcePath => $"Icons/{Prefix}/{Name}";

        /// <summary>
        /// Code snippet for loading this icon.
        /// </summary>
        public string LoadSnippet => $"Resources.Load<VectorImage>(\"{ResourcePath}\")";
    }

    /// <summary>
    /// Groups icon entries by variant suffix per library.
    /// Each library has its own known suffix set; libraries without variants are not grouped.
    /// </summary>
    public static class VariantGrouper
    {
        /// <summary>
        /// Per-library variant suffix definitions (longest-first within each list for correct parsing).
        /// Libraries not listed here have no variant grouping.
        /// </summary>
        private static readonly Dictionary<string, string[]> LIBRARY_SUFFIXES = new()
        {
            // Phosphor — 6 weights (regular = no suffix)
            ["ph"] = new[] { "-duotone", "-light", "-bold", "-thin", "-fill" },
            // Heroicons v2 — outline default
            ["heroicons"] = new[] { "-20-solid", "-16-solid", "-solid" },
            // Material Symbols — compound suffixes before simple ones
            ["material-symbols"] = new[] { "-outline-rounded", "-outline-sharp", "-outline", "-rounded", "-sharp" },
            // Tabler
            ["tabler"] = new[] { "-filled" },
            // MDI (Material Design Icons)
            ["mdi"] = new[] { "-outline" },
            // Bootstrap Icons
            ["bi"] = new[] { "-fill" },
            // Remix Icon — always suffixed, no base form
            ["ri"] = new[] { "-line", "-fill" },
            // Iconoir
            ["iconoir"] = new[] { "-solid" },
            // Carbon
            ["carbon"] = new[] { "-filled" },
        };
        // NOTE: "lucide" is intentionally absent — single-style library, no variants.

        /// <summary>
        /// Display order for variant tabs (user-friendly ordering).
        /// Separate from LIBRARY_SUFFIXES which is ordered for parsing.
        /// </summary>
        private static readonly Dictionary<string, string[]> LIBRARY_DISPLAY_ORDER = new()
        {
            ["ph"] = new[] { "-thin", "-light", "-bold", "-fill", "-duotone" },
            ["heroicons"] = new[] { "-solid", "-20-solid", "-16-solid" },
            ["material-symbols"] = new[] { "-outline", "-rounded", "-sharp", "-outline-rounded", "-outline-sharp" },
            ["ri"] = new[] { "-line", "-fill" },
        };

        /// <summary>
        /// Parses variant suffix from an icon name using the library's known suffixes.
        /// Returns (baseName, variantLabel). If no match, variantLabel is empty.
        /// </summary>
        public static (string baseName, string variantLabel) ParseVariant(string name, string prefix = "")
        {
            if (!string.IsNullOrEmpty(prefix) && LIBRARY_SUFFIXES.TryGetValue(prefix, out var suffixes))
            {
                foreach (var suffix in suffixes)
                {
                    if (name.EndsWith(suffix))
                        return (name.Substring(0, name.Length - suffix.Length), suffix.Substring(1));
                }
            }
            return (name, "");
        }

        /// <summary>
        /// Overload without prefix — tries all known suffixes (for Project tab mixed-library use).
        /// </summary>
        public static (string baseName, string variantLabel) ParseVariantAny(string name)
        {
            foreach (var kv in LIBRARY_SUFFIXES)
            {
                foreach (var suffix in kv.Value)
                {
                    if (name.EndsWith(suffix))
                        return (name.Substring(0, name.Length - suffix.Length), suffix.Substring(1));
                }
            }
            return (name, "");
        }

        /// <summary>
        /// Returns true if the given library prefix has variant definitions.
        /// </summary>
        public static bool HasVariants(string prefix) => LIBRARY_SUFFIXES.ContainsKey(prefix);

        /// <summary>
        /// Returns suffix strings in display order for variant tabs.
        /// Falls back to parsing order if no display order is defined.
        /// </summary>
        public static string[] GetSuffixes(string prefix)
        {
            if (LIBRARY_DISPLAY_ORDER.TryGetValue(prefix, out var display))
                return display;
            return LIBRARY_SUFFIXES.TryGetValue(prefix, out var s) ? s : System.Array.Empty<string>();
        }

        /// <summary>
        /// Groups entries by base name using library-specific suffixes.
        /// Returns representative entries and a variant map.
        /// Only groups with 2+ variants appear in the variant map.
        /// </summary>
        public static (List<IconEntry> representatives, Dictionary<string, List<IconEntry>> variantMap)
            GroupEntries(List<IconEntry> entries, string prefix = "")
        {
            // If no variant definitions for this library, return all entries as-is
            if (!string.IsNullOrEmpty(prefix) && !LIBRARY_SUFFIXES.ContainsKey(prefix))
            {
                return (new List<IconEntry>(entries), new Dictionary<string, List<IconEntry>>());
            }

            // Group by base name
            var groups = new Dictionary<string, List<IconEntry>>();
            foreach (var entry in entries)
            {
                var p = !string.IsNullOrEmpty(prefix) ? prefix : entry.Prefix;
                var (baseName, _) = ParseVariant(entry.Name, p);
                if (!groups.TryGetValue(baseName, out var list))
                {
                    list = new List<IconEntry>();
                    groups[baseName] = list;
                }
                list.Add(entry);
            }

            var representatives = new List<IconEntry>();
            var variantMap = new Dictionary<string, List<IconEntry>>();

            // For Remix Icon (ri), the representative should be the -line variant
            bool preferLine = prefix == "ri";

            foreach (var kv in groups)
            {
                var baseName = kv.Key;
                var group = kv.Value;

                // Pick representative
                IconEntry rep;
                if (preferLine)
                    rep = group.FirstOrDefault(e => e.Name.EndsWith("-line")) ?? group[0];
                else
                    rep = group.FirstOrDefault(e => e.Name == baseName)
                       ?? group.OrderBy(e => e.Name.Length).First();

                representatives.Add(rep);

                if (group.Count > 1)
                    variantMap[baseName] = group;
            }

            // Preserve original ordering based on first appearance
            var orderMap = new Dictionary<IconEntry, int>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (!orderMap.ContainsKey(entries[i]))
                    orderMap[entries[i]] = i;
            }
            representatives.Sort((a, b) => orderMap[a].CompareTo(orderMap[b]));

            return (representatives, variantMap);
        }
    }
}
