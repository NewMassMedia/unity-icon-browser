using UnityEditor;

namespace IconBrowser
{
    /// <summary>
    /// EditorPrefs-based settings for the Icon Browser.
    /// </summary>
    static class IconBrowserSettings
    {
        const string PREF_ICONS_PATH = "IconBrowser_IconsPath";
        const string DEFAULT_PATH = "Assets/Resources/Icons";

        /// <summary>
        /// Target folder for imported icons.
        /// </summary>
        public static string IconsPath
        {
            get => EditorPrefs.GetString(PREF_ICONS_PATH, DEFAULT_PATH);
            set => EditorPrefs.SetString(PREF_ICONS_PATH, value);
        }
    }
}
