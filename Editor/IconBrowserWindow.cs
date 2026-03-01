using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using IconBrowser.Data;
using IconBrowser.Import;
using IconBrowser.UI;

namespace IconBrowser
{
    /// <summary>
    /// Icon Browser editor window — search, browse, preview, and import icons from Iconify.
    /// </summary>
    public class IconBrowserWindow : EditorWindow
    {
        [MenuItem("Window/Icon Browser")]
        public static void Open()
        {
            var window = GetWindow<IconBrowserWindow>();
            window.titleContent = new GUIContent("Icon Browser");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        private IconDatabase _db;
        private SvgPreviewCache _previewCache;

        private ToolbarSearchField _searchField;
        private VisualElement _tabBar;
        private Button _projectTabBtn;
        private Button _browseTabBtn;
        private Button _settingsTabBtn;
        private Label _statusLabel;

        private ProjectTab _projectTab;
        private BrowseTab _browseTab;
        private SettingsTab _settingsTab;
        private ToastNotification _toast;

        private int _activeTab; // 0 = Project, 1 = Browse, 2 = Settings
        private List<IconLibrary> _sortedLibraries = new();

        private void CreateGUI()
        {
            _db = new IconDatabase();
            _previewCache = new SvgPreviewCache();

            // Load stylesheet
            var uss = FindStyleSheet();
            if (uss != null)
                rootVisualElement.styleSheets.Add(uss);

            rootVisualElement.AddToClassList("icon-browser");

            _toast = new ToastNotification();

            BuildTabs();
            BuildStatusBar();

            rootVisualElement.Add(_toast);

            // Initialize
            _projectTab.Initialize();
            LoadLibrariesAsync();
        }

        private void BuildTabs()
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
            _projectTab.SetToast(_toast);
            tabContent.Add(_projectTab);

            _browseTab = new BrowseTab(_db, _previewCache);
            _browseTab.style.display = DisplayStyle.None;
            _browseTab.SetToast(_toast);
            _browseTab.OnIconImported -= OnIconImported;
            _browseTab.OnIconImported += OnIconImported;
            tabContent.Add(_browseTab);

            _settingsTab = new SettingsTab(_previewCache);
            _settingsTab.style.display = DisplayStyle.None;
            _settingsTab.OnImportPathChanged -= OnImportPathChanged;
            _settingsTab.OnImportPathChanged += OnImportPathChanged;
            tabContent.Add(_settingsTab);

            UpdateProjectTabLabel();
        }

        private void BuildStatusBar()
        {
            _statusLabel = new Label();
            _statusLabel.AddToClassList("icon-browser__status");
            rootVisualElement.Add(_statusLabel);
            UpdateStatusBar();
        }

        private void SwitchTab(int tab)
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
                _browseTab.Initialize();
            }
        }

        private void OnSearchChanged(ChangeEvent<string> evt)
        {
            if (_activeTab == 0)
                _projectTab.Search(evt.newValue);
            else
                _browseTab.Search(evt.newValue);
        }

        private void LoadLibrariesAsync()
        {
            AsyncHelper.FireAndForget(LoadLibrariesInternalAsync());
        }

        private async Task LoadLibrariesInternalAsync()
        {
            try
            {
                await _db.LoadLibrariesAsync();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[IconBrowser] Failed to load libraries: {e}");
                _toast?.ShowError("Failed to load icon libraries. Check your network.");
                return;
            }
            if (!_db.LibrariesLoaded) return;

            _sortedLibraries = _db.GetSortedLibraries();

            _browseTab.SetLibraries(_sortedLibraries);

            UpdateStatusBar();
        }

        private void UpdateProjectTabLabel()
        {
            _projectTabBtn.text = $"Project ({_db.LocalCount})";
        }

        private void UpdateStatusBar()
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

        private static StyleSheet FindStyleSheet()
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

        private void OnIconImported()
        {
            _projectTab.Initialize();
            UpdateStatusBar();
        }

        private void OnImportPathChanged()
        {
            _projectTab.Initialize();
            UpdateStatusBar();
        }

        private void OnDestroy()
        {
            _projectTab?.Detach();
            _browseTab?.Detach();
            _previewCache?.Destroy();
        }
    }
}
