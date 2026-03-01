using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using IconBrowser.Data;
using IconBrowser.Import;

namespace IconBrowser.UI
{
    /// <summary>
    /// Project tab — shows locally imported icons with search, library filter, grid, and detail panel.
    /// </summary>
    public class ProjectTab : VisualElement
    {
        private readonly IconDatabase _db;
        private readonly IconOperationService _ops;
        private readonly IconGrid _grid;
        private readonly IconDetailPanel _detail;
        private readonly VisualElement _libraryBar;
        private readonly Button _fixUnknownBtn;
        private readonly Button _refreshBtn;
        private ToastNotification _toast;

        private string _searchQuery = "";
        private string _prefixFilter = "";

        public ProjectTab(IconDatabase db)
        {
            _db = db;
            _ops = new IconOperationService(db, IconImporter.Default);
            AddToClassList("icon-tab");

            // Library filter bar (pill buttons)
            _libraryBar = new VisualElement();
            _libraryBar.AddToClassList("project-tab__filter-bar");
            Add(_libraryBar);

            _fixUnknownBtn = new Button(OnFixUnknown) { text = "Fix Unknown \u2192 lucide" };
            _fixUnknownBtn.AddToClassList("project-tab__fix-btn");
            _fixUnknownBtn.style.display = DisplayStyle.None;

            _refreshBtn = new Button(OnRefresh) { text = "\u21BB" };
            _refreshBtn.AddToClassList("project-tab__refresh-btn");
            _refreshBtn.tooltip = "Rescan local icons";

            var body = new VisualElement();
            body.AddToClassList("icon-tab__body");
            Add(body);

            _grid = new IconGrid { GroupByAlpha = true };
            body.Add(_grid);

            _detail = new IconDetailPanel();
            body.Add(_detail);

            _grid.ShowActionButtons = true;
            _grid.OnIconSelected -= OnSelected;
            _grid.OnIconSelected += OnSelected;
            _grid.OnSelectionChanged -= OnGridSelectionChanged;
            _grid.OnSelectionChanged += OnGridSelectionChanged;
            _grid.OnQuickDeleteClicked -= OnDelete;
            _grid.OnQuickDeleteClicked += OnDelete;
            _detail.OnDeleteClicked -= OnDelete;
            _detail.OnDeleteClicked += OnDelete;
            _detail.OnBatchDeleteClicked -= OnBatchDelete;
            _detail.OnBatchDeleteClicked += OnBatchDelete;

            _db.OnLocalIconsChanged -= Refresh;
            _db.OnLocalIconsChanged += Refresh;
        }

        internal void SetToast(ToastNotification toast) => _toast = toast;

        /// <summary>
        /// Initializes the tab — scans local icons and builds the grid.
        /// </summary>
        public void Initialize()
        {
            _db.ScanLocalIcons();
            RefreshLibraryFilter();
            Refresh();
        }

        /// <summary>
        /// Filters icons by the given search query.
        /// </summary>
        public void Search(string query)
        {
            _searchQuery = query;
            Refresh();
        }

        private void OnLibraryFilterClicked(string prefix)
        {
            _prefixFilter = prefix;

            foreach (var child in _libraryBar.Children())
            {
                if (child is Button btn)
                    btn.EnableInClassList("browse-tab__variant-tab--active",
                        (prefix == "" && btn.text == "All") || btn.userData is string p && p == prefix);
            }

            Refresh();
        }

        private void RefreshLibraryFilter()
        {
            var prefixes = _db.GetLocalPrefixes();

            _libraryBar.Clear();

            // "All" tab
            var allBtn = new Button(() => OnLibraryFilterClicked("")) { text = "All" };
            allBtn.userData = "";
            allBtn.AddToClassList("browse-tab__variant-tab");
            allBtn.EnableInClassList("browse-tab__variant-tab--active", _prefixFilter == "");
            _libraryBar.Add(allBtn);

            // Per-library tabs
            foreach (var prefix in prefixes)
            {
                var captured = prefix;
                var btn = new Button(() => OnLibraryFilterClicked(captured)) { text = prefix };
                btn.userData = prefix;
                btn.AddToClassList("browse-tab__variant-tab");
                btn.EnableInClassList("browse-tab__variant-tab--active", _prefixFilter == prefix);
                _libraryBar.Add(btn);
            }

            // "Fix Unknown" button at the end
            _fixUnknownBtn.style.display = prefixes.Contains("unknown")
                ? DisplayStyle.Flex : DisplayStyle.None;
            _libraryBar.Add(_fixUnknownBtn);
            _libraryBar.Add(_refreshBtn);
        }

        private void OnFixUnknown()
        {
            int count = _db.ReassignUnknownIcons("lucide");
            if (count > 0)
            {
                Debug.Log($"[IconBrowser] Reassigned {count} unknown icons to 'lucide'");
                RefreshLibraryFilter();
                Refresh();
            }
        }

        private void OnRefresh()
        {
            _db.ScanLocalIcons();
            RefreshLibraryFilter();
            Refresh();
        }

        private void Refresh()
        {
            var icons = _db.SearchLocal(_searchQuery, _prefixFilter);
            _grid.SetItems(icons);
            _detail.Clear();
        }

        private void OnSelected(IconEntry entry)
        {
            _detail.ShowEntry(entry);
        }

        private void OnGridSelectionChanged(List<IconEntry> entries)
        {
            _detail.HandleSelectionChanged(entries, OnSelected);
        }

        private void OnBatchDelete(List<IconEntry> entries)
        {
            var toDelete = entries.Where(e => e.IsImported).ToList();
            if (toDelete.Count == 0) return;

            if (!EditorUtility.DisplayDialog(
                "Delete Icons",
                $"Are you sure you want to delete {toDelete.Count} icons?",
                "Delete", "Cancel"))
                return;

            int deletedCount = EditorProgressHelper.RunWithProgress(
                "Deleting Icons",
                "Deleting... ({0}/{1})",
                (onProgress, isCancelled) => _ops.BatchDelete(entries, onProgress, isCancelled));

            _grid.ClearSelection();
            RefreshLibraryFilter();
            if (deletedCount > 0)
                _toast?.ShowError($"Deleted {deletedCount} icon(s)");
        }

        private void OnDelete(IconEntry entry)
        {
            bool deleted = !string.IsNullOrEmpty(entry.LocalAssetPath)
                ? _ops.DeleteByPath(entry.LocalAssetPath, entry.Name)
                : _ops.Delete(entry.Name, entry.Prefix);

            if (deleted)
            {
                _detail.Clear();
                RefreshLibraryFilter();
                Refresh();
                _toast?.ShowError($"Deleted {entry.Name}");
            }
        }

        public void Detach()
        {
            _grid.OnIconSelected -= OnSelected;
            _grid.OnSelectionChanged -= OnGridSelectionChanged;
            _grid.OnQuickDeleteClicked -= OnDelete;
            _detail.OnDeleteClicked -= OnDelete;
            _detail.OnBatchDeleteClicked -= OnBatchDelete;
            _db.OnLocalIconsChanged -= Refresh;
        }
    }
}
