using IconBrowser.Data;
using IconBrowser.Import;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser.UI
{
    /// <summary>
    /// Project tab — shows locally imported icons with search, grid, and detail panel.
    /// </summary>
    public class ProjectTab : VisualElement
    {
        readonly IconDatabase _db;
        readonly IconGrid _grid;
        readonly IconDetailPanel _detail;

        string _searchQuery = "";

        public ProjectTab(IconDatabase db)
        {
            _db = db;
            AddToClassList("icon-tab");

            var body = new VisualElement();
            body.AddToClassList("icon-tab__body");
            Add(body);

            _grid = new IconGrid();
            body.Add(_grid);

            _detail = new IconDetailPanel();
            body.Add(_detail);

            _grid.OnIconSelected += OnSelected;
            _grid.OnIconDoubleClicked += OnDoubleClicked;
            _detail.OnDeleteClicked += OnDelete;

            _db.OnLocalIconsChanged += Refresh;
        }

        /// <summary>
        /// Initializes the tab — scans local icons and builds the grid.
        /// </summary>
        public void Initialize()
        {
            _db.ScanLocalIcons();
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

        void Refresh()
        {
            var icons = _db.SearchLocal(_searchQuery);
            _grid.SetItems(icons);
            _detail.Clear();
        }

        void OnSelected(IconEntry entry)
        {
            _detail.ShowEntry(entry);
        }

        void OnDoubleClicked(IconEntry entry)
        {
            // Copy load snippet on double-click
            EditorGUIUtility.systemCopyBuffer = entry.LoadSnippet;
            Debug.Log($"[IconBrowser] Copied: {entry.LoadSnippet}");
        }

        void OnDelete(IconEntry entry)
        {
            if (IconImporter.DeleteIcon(entry.Name))
            {
                _db.MarkDeleted(entry.Name);
                _detail.Clear();
            }
        }
    }
}
