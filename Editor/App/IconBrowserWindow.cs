using System;
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
        [MenuItem("Window/Tools/Icon Browser")]
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
        private readonly SearchShellPolicy _searchShellPolicy = new();
        private VisualElement _tabBar;
        private Button _projectTabBtn;
        private Button _browseTabBtn;
        private Button _settingsTabBtn;
        private Label _statusLabel;

        private ProjectTab _projectTab;
        private BrowseTab _browseTab;
        private SettingsTab _settingsTab;
        private IIconBrowserSearchTarget _projectSearchTarget;
        private IIconBrowserSearchTarget _browseSearchTarget;
        private IIconBrowserSearchTarget _activeSearchTarget;
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
            _searchField.RegisterCallback<KeyUpEvent>(OnSearchKeyUp);
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
            _browseTab.OnGlobalSearchExitedViaLibraryClick -= OnBrowseGlobalSearchExitedViaLibraryClick;
            _browseTab.OnGlobalSearchExitedViaLibraryClick += OnBrowseGlobalSearchExitedViaLibraryClick;
            tabContent.Add(_browseTab);

            _settingsTab = new SettingsTab(_previewCache);
            _settingsTab.style.display = DisplayStyle.None;
            _settingsTab.OnImportPathChanged -= OnImportPathChanged;
            _settingsTab.OnImportPathChanged += OnImportPathChanged;
            tabContent.Add(_settingsTab);

            _projectSearchTarget = new SearchTargetDelegate(_projectTab.Search, SearchDispatchMode.Immediate);
            _browseSearchTarget = new SearchTargetDelegate(_browseTab.Search, SearchDispatchMode.Deferred);
            _activeSearchTarget = _searchShellPolicy.ResolveTargetForInputChanged(_activeTab, _projectSearchTarget, _browseSearchTarget);

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
            var previousTab = _activeTab;
            var previousSearchText = _searchField?.value ?? string.Empty;
            _activeTab = tab;

            _projectTabBtn.EnableInClassList("icon-browser__tab-btn--active", tab == 0);
            _browseTabBtn.EnableInClassList("icon-browser__tab-btn--active", tab == 1);
            _settingsTabBtn.EnableInClassList("icon-browser__tab-btn--active", tab == 2);

            _projectTab.style.display = tab == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _browseTab.style.display = tab == 1 ? DisplayStyle.Flex : DisplayStyle.None;
            _settingsTab.style.display = tab == 2 ? DisplayStyle.Flex : DisplayStyle.None;

            _searchField.style.display = _searchShellPolicy.IsSearchVisible(tab) ? DisplayStyle.Flex : DisplayStyle.None;
            _activeSearchTarget = _searchShellPolicy.ResolveTargetForInputChanged(tab, _projectSearchTarget, _browseSearchTarget);
            var shouldSyncSearchTargetOnTabSwitch = _searchShellPolicy.ShouldSyncSearchTargetOnTabSwitch(previousTab, tab);

            if (_searchShellPolicy.ShouldResetSearchTextOnTabSwitch(previousTab, tab))
            {
                if (_searchShellPolicy.ShouldClearSearchTextWithoutDispatch(tab))
                    _searchField.SetValueWithoutNotify(string.Empty);
                else
                    _searchField.value = string.Empty;
            }

            if (previousTab != tab &&
                tab == 1 &&
                _activeSearchTarget?.DispatchMode == SearchDispatchMode.Deferred &&
                string.IsNullOrWhiteSpace(previousSearchText) &&
                string.IsNullOrWhiteSpace(_searchField.value) &&
                !shouldSyncSearchTargetOnTabSwitch)
            {
                _activeSearchTarget.Search(string.Empty);
            }

            if (shouldSyncSearchTargetOnTabSwitch &&
                _searchShellPolicy.ShouldDispatchOnInputChanged(_activeSearchTarget, _searchField.value))
            {
                _activeSearchTarget.Search(_searchField.value);
            }

            _browseTab.SetTabActive(tab == 1);

            if (tab == 1)
            {
                _browseTab.SyncImportState();
                _browseTab.Initialize();
            }
        }

        private void OnSearchChanged(ChangeEvent<string> evt)
        {
            if (_searchShellPolicy.ShouldDispatchOnInputChanged(_activeSearchTarget, evt.newValue))
                _activeSearchTarget.Search(evt.newValue);
        }

        private void OnSearchKeyUp(KeyUpEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
                return;

            var query = _searchField.value;
            if (_searchShellPolicy.ShouldDispatchOnSubmit(_activeSearchTarget, query))
                _activeSearchTarget.Search(query);

            evt.StopPropagation();
        }

        private void OnBrowseGlobalSearchExitedViaLibraryClick()
        {
            _searchField?.SetValueWithoutNotify(string.Empty);
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
            if (_browseTab != null)
                _browseTab.OnGlobalSearchExitedViaLibraryClick -= OnBrowseGlobalSearchExitedViaLibraryClick;

            _projectTab?.Detach();
            _browseTab?.Detach();
            _previewCache?.Destroy();
        }

        private sealed class SearchTargetDelegate : IIconBrowserSearchTarget
        {
            private readonly Action<string> _search;

            public SearchDispatchMode DispatchMode { get; }

            public SearchTargetDelegate(Action<string> search, SearchDispatchMode dispatchMode)
            {
                _search = search;
                DispatchMode = dispatchMode;
            }

            public void Search(string query) => _search(query);
        }
    }
}
