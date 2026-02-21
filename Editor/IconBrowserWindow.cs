using System.Collections.Generic;
using System.Linq;
using IconBrowser.Data;
using IconBrowser.Import;
using IconBrowser.UI;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser
{
    /// <summary>
    /// Icon Browser editor window — search, browse, preview, and import icons from Iconify.
    /// </summary>
    public class IconBrowserWindow : EditorWindow
    {
        [MenuItem("Tools/Icon Browser")]
        public static void Open()
        {
            var window = GetWindow<IconBrowserWindow>();
            window.titleContent = new GUIContent("Icon Browser");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        IconDatabase _db;
        SvgPreviewCache _previewCache;

        ToolbarSearchField _searchField;
        PopupField<string> _libraryDropdown;
        PopupField<string> _categoryDropdown;
        VisualElement _tabBar;
        Button _projectTabBtn;
        Button _browseTabBtn;
        Label _statusLabel;

        ProjectTab _projectTab;
        BrowseTab _browseTab;

        int _activeTab; // 0 = Project, 1 = Browse
        List<IconLibrary> _sortedLibraries = new();

        void CreateGUI()
        {
            _db = new IconDatabase();
            _previewCache = new SvgPreviewCache();

            // Load stylesheet from package path
            var packagePath = GetPackagePath();
            if (!string.IsNullOrEmpty(packagePath))
            {
                var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>($"{packagePath}/Editor/IconBrowserWindow.uss");
                if (uss != null)
                    rootVisualElement.styleSheets.Add(uss);
            }

            rootVisualElement.AddToClassList("icon-browser");

            BuildToolbar();
            BuildTabs();
            BuildStatusBar();

            // Initialize
            _projectTab.Initialize();
            LoadLibrariesAsync();
        }

        void BuildToolbar()
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("icon-browser__toolbar");
            rootVisualElement.Add(toolbar);

            _searchField = new ToolbarSearchField();
            _searchField.AddToClassList("icon-browser__search");
            _searchField.RegisterValueChangedCallback(OnSearchChanged);
            toolbar.Add(_searchField);

            // Library dropdown — starts with "Lucide" placeholder until libraries load
            _libraryDropdown = new PopupField<string>(
                new List<string> { "Lucide" }, 0);
            _libraryDropdown.AddToClassList("icon-browser__library-dropdown");
            _libraryDropdown.RegisterValueChangedCallback(OnLibraryChanged);
            toolbar.Add(_libraryDropdown);

            // Category dropdown — hidden until categories are loaded
            _categoryDropdown = new PopupField<string>(
                new List<string> { "All" }, 0);
            _categoryDropdown.AddToClassList("icon-browser__category-dropdown");
            _categoryDropdown.style.display = DisplayStyle.None;
            _categoryDropdown.RegisterValueChangedCallback(OnCategoryChanged);
            toolbar.Add(_categoryDropdown);

            // Settings button
            var settingsBtn = new Button(ShowSettings) { text = "\u2699" };
            settingsBtn.AddToClassList("icon-browser__settings-btn");
            toolbar.Add(settingsBtn);
        }

        void BuildTabs()
        {
            // Tab bar
            _tabBar = new VisualElement();
            _tabBar.AddToClassList("icon-browser__tab-bar");
            rootVisualElement.Add(_tabBar);

            _projectTabBtn = new Button(() => SwitchTab(0));
            _projectTabBtn.AddToClassList("icon-browser__tab-btn");
            _projectTabBtn.AddToClassList("icon-browser__tab-btn--active");
            _tabBar.Add(_projectTabBtn);

            _browseTabBtn = new Button(() => SwitchTab(1)) { text = "Browse" };
            _browseTabBtn.AddToClassList("icon-browser__tab-btn");
            _tabBar.Add(_browseTabBtn);

            // Tab content
            var tabContent = new VisualElement();
            tabContent.AddToClassList("icon-browser__tab-content");
            rootVisualElement.Add(tabContent);

            _projectTab = new ProjectTab(_db);
            tabContent.Add(_projectTab);

            _browseTab = new BrowseTab(_db, _previewCache);
            _browseTab.style.display = DisplayStyle.None;
            _browseTab.OnIconImported += () =>
            {
                _projectTab.Initialize();
                UpdateStatusBar();
            };
            _browseTab.OnCategoriesLoaded += OnCategoriesLoaded;
            tabContent.Add(_browseTab);

            UpdateProjectTabLabel();
        }

        void BuildStatusBar()
        {
            _statusLabel = new Label();
            _statusLabel.AddToClassList("icon-browser__status");
            rootVisualElement.Add(_statusLabel);
            UpdateStatusBar();
        }

        void SwitchTab(int tab)
        {
            _activeTab = tab;

            _projectTabBtn.EnableInClassList("icon-browser__tab-btn--active", tab == 0);
            _browseTabBtn.EnableInClassList("icon-browser__tab-btn--active", tab == 1);

            _projectTab.style.display = tab == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _browseTab.style.display = tab == 1 ? DisplayStyle.Flex : DisplayStyle.None;

            _libraryDropdown.style.display = tab == 1 ? DisplayStyle.Flex : DisplayStyle.None;
            _categoryDropdown.style.display = tab == 1 ? DisplayStyle.Flex : DisplayStyle.None;

            // Clear search when switching tabs
            _searchField.value = "";

            if (tab == 1 && _browseTab.CurrentPrefix == "lucide")
                _browseTab.Initialize();
        }

        void OnSearchChanged(ChangeEvent<string> evt)
        {
            if (_activeTab == 0)
                _projectTab.Search(evt.newValue);
            else
                _browseTab.Search(evt.newValue);
        }

        void OnLibraryChanged(ChangeEvent<string> evt)
        {
            if (_sortedLibraries.Count == 0) return;

            var lib = _sortedLibraries.FirstOrDefault(l => l.DisplayName == evt.newValue);
            if (lib != null)
            {
                // Reset category when switching libraries
                _categoryDropdown.SetValueWithoutNotify("All");
                _browseTab.SetLibrary(lib.Prefix);
                UpdateStatusBar();
            }
        }

        void OnCategoryChanged(ChangeEvent<string> evt)
        {
            var category = evt.newValue == "All" ? "" : evt.newValue;
            _browseTab.SetCategory(category);
        }

        void OnCategoriesLoaded(List<string> categories)
        {
            if (categories == null || categories.Count == 0)
            {
                _categoryDropdown.style.display = DisplayStyle.None;
                return;
            }

            var choices = new List<string> { "All" };
            choices.AddRange(categories.OrderBy(c => c));
            _categoryDropdown.choices = choices;
            _categoryDropdown.SetValueWithoutNotify("All");

            if (_activeTab == 1)
                _categoryDropdown.style.display = DisplayStyle.Flex;
        }

        async void LoadLibrariesAsync()
        {
            await _db.LoadLibrariesAsync();
            if (!_db.LibrariesLoaded) return;

            _sortedLibraries = _db.GetSortedLibraries();
            var displayNames = _sortedLibraries.Select(l => l.DisplayName).ToList();

            // Replace the dropdown with loaded data
            _libraryDropdown.choices = displayNames;
            _libraryDropdown.SetValueWithoutNotify(displayNames[0]); // Lucide is first

            UpdateStatusBar();
        }

        void UpdateProjectTabLabel()
        {
            _projectTabBtn.text = $"Project ({_db.LocalCount})";
        }

        void UpdateStatusBar()
        {
            UpdateProjectTabLabel();

            if (_sortedLibraries.Count > 0)
            {
                var current = _sortedLibraries.FirstOrDefault(l => l.Prefix == _browseTab.CurrentPrefix);
                var totalStr = current != null ? $"{current.Total:N0} icons in {current.Name}" : "";
                _statusLabel.text = $"{totalStr} \u00b7 {_db.LocalCount} imported";
            }
            else
            {
                _statusLabel.text = $"{_db.LocalCount} imported icons";
            }
        }

        void ShowSettings()
        {
            var currentPath = IconBrowserSettings.IconsPath;
            var newPath = EditorUtility.OpenFolderPanel("Select Icons Import Folder", currentPath, "");
            if (string.IsNullOrEmpty(newPath)) return;

            // Convert absolute path to Assets-relative path
            var dataPath = Application.dataPath;
            if (newPath.StartsWith(dataPath))
            {
                newPath = "Assets" + newPath.Substring(dataPath.Length);
                IconBrowserSettings.IconsPath = newPath;
                _projectTab.Initialize();
                UpdateStatusBar();
                Debug.Log($"[IconBrowser] Import path set to: {newPath}");
            }
            else
            {
                EditorUtility.DisplayDialog("Invalid Path",
                    "The selected folder must be inside the Assets directory.", "OK");
            }
        }

        /// <summary>
        /// Finds the package path for this package, supporting both local and embedded installations.
        /// </summary>
        static string GetPackagePath()
        {
            // Try package path first
            var guids = AssetDatabase.FindAssets("IconBrowserWindow t:StyleSheet");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("IconBrowserWindow.uss"))
                {
                    // Return the directory containing the USS
                    return System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
                }
            }

            // Fallback: search by package name
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(IconBrowserWindow).Assembly);
            if (packageInfo != null)
                return packageInfo.assetPath;

            return null;
        }

        void OnDestroy()
        {
            _previewCache?.CleanupTempAssets();
        }
    }
}
