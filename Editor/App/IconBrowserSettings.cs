using UnityEditor;
using UnityEngine;

namespace IconBrowser
{
    /// <summary>
    /// EditorPrefs-based settings for the Icon Browser.
    /// Keys are scoped per project via Application.dataPath hash to avoid
    /// cross-project contamination on first install.
    /// </summary>
    internal static class IconBrowserSettings
    {
        private const string DEFAULT_PATH = "Assets/Resources/Icon";

        private static string ProjectKey(string key) =>
            $"{key}_{Application.dataPath.GetHashCode()}";

        private static string PREF_ICONS_PATH => ProjectKey("IconBrowser_IconsPath");
        private static string PREF_FILTER_MODE => ProjectKey("IconBrowser_FilterMode");
        private static string PREF_SAMPLE_COUNT => ProjectKey("IconBrowser_SampleCount");
        private static string PREF_VERBOSE_CACHE_LOGS => ProjectKey("IconBrowser_VerboseCacheLogs");

        /// <summary>
        /// Target folder for imported icons.
        /// </summary>
        public static string IconsPath
        {
            get => EditorPrefs.GetString(PREF_ICONS_PATH, DEFAULT_PATH);
            set => EditorPrefs.SetString(PREF_ICONS_PATH, value);
        }

        /// <summary>
        /// Texture filter mode: 0 = Point, 1 = Bilinear, 2 = Trilinear.
        /// </summary>
        public static int FilterMode
        {
            get => EditorPrefs.GetInt(PREF_FILTER_MODE, 1);
            set => EditorPrefs.SetInt(PREF_FILTER_MODE, value);
        }

        /// <summary>
        /// MSAA sample count for SVG import: 1, 2, 4, or 8.
        /// </summary>
        public static int SampleCount
        {
            get => EditorPrefs.GetInt(PREF_SAMPLE_COUNT, 4);
            set => EditorPrefs.SetInt(PREF_SAMPLE_COUNT, value);
        }

        /// <summary>
        /// Enables verbose cache/eviction logs for diagnostics.
        /// </summary>
        public static bool VerboseCacheLogs
        {
            get => EditorPrefs.GetBool(PREF_VERBOSE_CACHE_LOGS, false);
            set => EditorPrefs.SetBool(PREF_VERBOSE_CACHE_LOGS, value);
        }

        /// <summary>
        /// Removes persisted settings so the defaults are used again.
        /// </summary>
        public static void ResetToDefaults()
        {
            EditorPrefs.DeleteKey(PREF_ICONS_PATH);
            EditorPrefs.DeleteKey(PREF_FILTER_MODE);
            EditorPrefs.DeleteKey(PREF_SAMPLE_COUNT);
            EditorPrefs.DeleteKey(PREF_VERBOSE_CACHE_LOGS);
        }
    }
}
