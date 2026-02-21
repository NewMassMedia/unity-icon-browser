using UnityEngine;
using UnityEngine.UIElements;

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

        /// <summary>
        /// SVG body content from Iconify API (without outer svg tag).
        /// Used for preview rendering before import.
        /// </summary>
        public string SvgBody { get; set; }

        /// <summary>
        /// Cached preview texture for remote icons.
        /// </summary>
        public VectorImage PreviewAsset { get; set; }

        /// <summary>
        /// Full Iconify identifier (e.g. "lucide:check").
        /// </summary>
        public string FullId => $"{Prefix}:{Name}";

        /// <summary>
        /// Local asset path after import.
        /// </summary>
        public string ImportedAssetPath => $"{IconBrowserSettings.IconsPath}/{Name}.svg";

        /// <summary>
        /// Resources.Load path (e.g. "Icons/check").
        /// </summary>
        public string ResourcePath => $"Icons/{Name}";

        /// <summary>
        /// Code snippet for loading this icon.
        /// </summary>
        public string LoadSnippet => $"Resources.Load<VectorImage>(\"{ResourcePath}\")";
    }
}
