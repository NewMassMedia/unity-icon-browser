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
        VisualElement _tabBar;
        Button _projectTabBtn;
        Button _browseTabBtn;
        Button _settingsTabBtn;
        Label _statusLabel;

        ProjectTab _projectTab;
        BrowseTab _browseTab;
        VisualElement _settingsTab;

        TextField _pathField;

        int _activeTab; // 0 = Project, 1 = Browse, 2 = Settings
        List<IconLibrary> _sortedLibraries = new();

        void CreateGUI()
        {
            _db = new IconDatabase();
            _previewCache = new SvgPreviewCache();

            // Load stylesheet
            var uss = FindStyleSheet();
            if (uss != null)
                rootVisualElement.styleSheets.Add(uss);

            rootVisualElement.AddToClassList("icon-browser");

            BuildTabs();
            BuildStatusBar();

            // Initialize
            _projectTab.Initialize();
            LoadLibrariesAsync();
        }

        void BuildTabs()
        {
            // Tab bar — tabs on left, search on right
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

            _settingsTabBtn = new Button(() => SwitchTab(2)) { text = "Settings" };
            _settingsTabBtn.AddToClassList("icon-browser__tab-btn");
            _tabBar.Add(_settingsTabBtn);

            // Spacer pushes remaining items to the right
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            _tabBar.Add(spacer);

            // Search field
            _searchField = new ToolbarSearchField();
            _searchField.AddToClassList("icon-browser__search");
            _searchField.RegisterValueChangedCallback(OnSearchChanged);
            _tabBar.Add(_searchField);

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
            tabContent.Add(_browseTab);

            _settingsTab = BuildSettingsTab();
            _settingsTab.style.display = DisplayStyle.None;
            tabContent.Add(_settingsTab);

            UpdateProjectTabLabel();
        }

        VisualElement BuildSettingsTab()
        {
            var root = new ScrollView(ScrollViewMode.Vertical);
            root.AddToClassList("settings-tab");

            // --- Path Settings ---
            var pathSection = new VisualElement();
            pathSection.AddToClassList("settings-tab__section");
            root.Add(pathSection);

            var pathTitle = new Label("Path Settings");
            pathTitle.AddToClassList("settings-tab__section-title");
            pathSection.Add(pathTitle);

            var pathRow = new VisualElement();
            pathRow.AddToClassList("settings-tab__row");
            pathSection.Add(pathRow);

            var pathLabel = new Label("Import Path");
            pathLabel.AddToClassList("settings-tab__label");
            pathRow.Add(pathLabel);

            _pathField = new TextField();
            _pathField.value = IconBrowserSettings.IconsPath;
            _pathField.isReadOnly = true;
            _pathField.AddToClassList("settings-tab__path-field");
            pathRow.Add(_pathField);

            var changeBtn = new Button(() => ChangeImportPath()) { text = "Change..." };
            changeBtn.AddToClassList("settings-tab__change-btn");
            pathRow.Add(changeBtn);

            // --- Import Settings ---
            var importSection = new VisualElement();
            importSection.AddToClassList("settings-tab__section");
            root.Add(importSection);

            var importTitle = new Label("Import Settings");
            importTitle.AddToClassList("settings-tab__section-title");
            importSection.Add(importTitle);

            // Filter Mode
            var filterRow = new VisualElement();
            filterRow.AddToClassList("settings-tab__row");
            importSection.Add(filterRow);

            var filterLabel = new Label("Filter Mode");
            filterLabel.AddToClassList("settings-tab__label");
            filterRow.Add(filterLabel);

            var filterChoices = new List<string> { "Point", "Bilinear", "Trilinear" };
            var filterField = new PopupField<string>(filterChoices, IconBrowserSettings.FilterMode);
            filterField.RegisterValueChangedCallback(evt =>
            {
                IconBrowserSettings.FilterMode = filterChoices.IndexOf(evt.newValue);
            });
            filterRow.Add(filterField);

            // Sample Count
            var sampleRow = new VisualElement();
            sampleRow.AddToClassList("settings-tab__row");
            importSection.Add(sampleRow);

            var sampleLabel = new Label("Sample Count");
            sampleLabel.AddToClassList("settings-tab__label");
            sampleRow.Add(sampleLabel);

            var sampleChoices = new List<string> { "1", "2", "4", "8" };
            var sampleIndex = sampleChoices.IndexOf(IconBrowserSettings.SampleCount.ToString());
            if (sampleIndex < 0) sampleIndex = 2; // default to "4"
            var sampleField = new PopupField<string>(sampleChoices, sampleIndex);
            sampleField.RegisterValueChangedCallback(evt =>
            {
                if (int.TryParse(evt.newValue, out var val))
                    IconBrowserSettings.SampleCount = val;
            });
            sampleRow.Add(sampleField);

            return root;
        }

        void ChangeImportPath()
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
                _pathField.value = newPath;
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
            _settingsTabBtn.EnableInClassList("icon-browser__tab-btn--active", tab == 2);

            _projectTab.style.display = tab == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _browseTab.style.display = tab == 1 ? DisplayStyle.Flex : DisplayStyle.None;
            _settingsTab.style.display = tab == 2 ? DisplayStyle.Flex : DisplayStyle.None;

            // Hide search field on Settings tab
            _searchField.style.display = tab == 2 ? DisplayStyle.None : DisplayStyle.Flex;

            // Clear search when switching tabs
            _searchField.value = "";

            if (tab == 1)
            {
                _browseTab.SyncImportState();
                if (_browseTab.CurrentPrefix == "lucide")
                    _browseTab.Initialize();
            }
        }

        void OnSearchChanged(ChangeEvent<string> evt)
        {
            if (_activeTab == 0)
                _projectTab.Search(evt.newValue);
            else
                _browseTab.Search(evt.newValue);
        }

        async void LoadLibrariesAsync()
        {
            await _db.LoadLibrariesAsync();
            if (!_db.LibrariesLoaded) return;

            _sortedLibraries = _db.GetSortedLibraries();

            // Pass libraries to BrowseTab's sidebar list
            _browseTab.SetLibraries(_sortedLibraries);

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

        static StyleSheet FindStyleSheet()
        {
            var guids = AssetDatabase.FindAssets("IconBrowserWindow t:StyleSheet");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("IconBrowserWindow.uss"))
                    return AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            }
            return null;
        }

        void OnDestroy()
        {
            _previewCache?.Destroy();
        }
    }
}
