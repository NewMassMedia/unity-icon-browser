using UnityEditor;

namespace IconBrowser
{
    /// <summary>
    /// EditorPrefs-based settings for the Icon Browser.
    /// </summary>
    static class IconBrowserSettings
    {
        const string PREF_ICONS_PATH = "IconBrowser_IconsPath";
        const string DEFAULT_PATH = "Assets/CoreUI/Runtime/Resources/Icons";

        /// <summary>
        /// Target folder for imported icons.
        /// </summary>
        public static string IconsPath
        {
            get => EditorPrefs.GetString(PREF_ICONS_PATH, DEFAULT_PATH);
            set => EditorPrefs.SetString(PREF_ICONS_PATH, value);
        }

        const string PREF_FILTER_MODE = "IconBrowser_FilterMode";
        const string PREF_SAMPLE_COUNT = "IconBrowser_SampleCount";

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
    }
}
