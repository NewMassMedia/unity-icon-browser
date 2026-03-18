using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using IconBrowser.Data;

namespace IconBrowser.UI
{
    /// <summary>
    /// Manages cell lifecycle (pooling, binding, recycling) and visible-range virtualization
    /// for the icon grid. Owns all VisualElement instances for cells and headers.
    /// </summary>
    internal sealed class IconGridVirtualizer
    {
        #region Variables

        private const int CELL_WIDTH = IconBrowserConstants.CELL_WIDTH;
        private const int CELL_HEIGHT = IconBrowserConstants.CELL_HEIGHT;

        private readonly VisualElement _viewport;
        private readonly ScrollView _scrollView;

        private readonly List<VisualElement> _cellPool = new();
        private readonly List<VisualElement> _headerPool = new();
        private readonly Dictionary<int, VisualElement> _activeCells = new();
        private readonly List<int> _recycleBuffer = new();

        private int _visibleFirst = -1;
        private int _visibleLast = -1;

        // Mutable state — updated by IconGrid before each UpdateVisible call.
        // Closures in GetOrCreateCell reference these fields to always get current values.
        private List<IconEntry> _items = new();
        private HashSet<int> _selectedIndices;
        private bool _showActions;

        #endregion Variables

        #region Events

        public event Action<IconEntry> OnQuickImportClicked;
        public event Action<IconEntry> OnQuickDeleteClicked;

        #endregion Events

        #region Properties

        public IReadOnlyDictionary<int, VisualElement> ActiveCells => _activeCells;

        #endregion Properties

        #region Constructor

        public IconGridVirtualizer(VisualElement viewport, ScrollView scrollView)
        {
            _viewport = viewport;
            _scrollView = scrollView;
        }

        #endregion Constructor

        #region Help Methods

        /// <summary>
        /// Sync current data state before calling UpdateVisible.
        /// </summary>
        public void SetState(List<IconEntry> items, HashSet<int> selectedIndices, bool showActions)
        {
            _items = items;
            _selectedIndices = selectedIndices;
            _showActions = showActions;
        }

        /// <summary>
        /// Update visible cells based on current scroll position and layout.
        /// Returns (firstDataIndex, lastDataIndex) of visible range, or (-1,-1) if nothing visible.
        /// </summary>
        public (int first, int last) UpdateVisible(IconGridLayout layout)
        {
            if (layout.Entries.Count > 0)
                return UpdateVisibleGrouped(layout);
            else
                return UpdateVisibleFlat(layout);
        }

        public void RecycleAll()
        {
            foreach (var kv in _activeCells)
            {
                kv.Value.style.display = DisplayStyle.None;

                if (kv.Value.ClassListContains("icon-grid__header"))
                    _headerPool.Add(kv.Value);
                else
                    _cellPool.Add(kv.Value);
            }
            _activeCells.Clear();
        }

        public void ResetVisibleRange()
        {
            _visibleFirst = -1;
            _visibleLast = -1;
        }

        /// <summary>
        /// Refresh icon previews and action button state for all active cells.
        /// </summary>
        public void RefreshPreviews(Func<int, int> getDataIndex)
        {
            foreach (var kv in _activeCells)
            {
                int dataIdx = getDataIndex(kv.Key);
                if (dataIdx < 0 || dataIdx >= _items.Count) continue;

                var entry = _items[dataIdx];
                var cell = kv.Value;

                var iconVe = cell.Q<VisualElement>("cell-icon");
                if (iconVe != null)
                    iconVe.style.backgroundImage = IconGrid.GetIconBackground(entry);

                var checkVe = cell.Q<VisualElement>("cell-check");
                if (checkVe != null)
                    checkVe.style.display = (entry.IsImported && entry.LocalAsset == null)
                        ? DisplayStyle.Flex : DisplayStyle.None;

                if (_showActions)
                {
                    bool selected = _selectedIndices.Contains(dataIdx);
                    if (selected && _selectedIndices.Count == 1)
                        ShowCellAction(cell, false);
                    else
                    {
                        var btn = cell.Q<Button>("cell-action-btn");
                        if (btn != null) btn.style.display = DisplayStyle.None;
                    }
                }
            }
        }

        /// <summary>
        /// Refresh selection visual state for all active cells.
        /// </summary>
        public void RefreshSelectionVisuals(Func<int, int> getDataIndex)
        {
            foreach (var kv in _activeCells)
            {
                int dataIdx = getDataIndex(kv.Key);
                var cell = kv.Value;
                bool selected = _selectedIndices.Contains(dataIdx);
                cell.EnableInClassList("icon-grid__cell--selected", selected);

                if (_showActions)
                {
                    var btn = cell.Q<Button>("cell-action-btn");
                    if (btn != null)
                    {
                        if (selected && _selectedIndices.Count == 1)
                            ShowCellAction(cell, false);
                        else
                            btn.style.display = DisplayStyle.None;
                    }
                }
            }
        }

        #endregion Help Methods

        #region Cell / Header creation

        private VisualElement GetOrCreateCell()
        {
            if (_cellPool.Count > 0)
            {
                var recycled = _cellPool[_cellPool.Count - 1];
                _cellPool.RemoveAt(_cellPool.Count - 1);
                recycled.style.display = DisplayStyle.Flex;
                return recycled;
            }

            var cell = new VisualElement();
            cell.AddToClassList("icon-grid__cell");
            cell.style.position = Position.Absolute;
            cell.style.width = CELL_WIDTH;
            cell.style.height = CELL_HEIGHT;

            var iconVe = new VisualElement { name = "cell-icon" };
            iconVe.AddToClassList("icon-grid__cell-icon");
            iconVe.style.width = IconBrowserConstants.ICON_DISPLAY_SIZE;
            iconVe.style.height = IconBrowserConstants.ICON_DISPLAY_SIZE;
            cell.Add(iconVe);

            var checkVe = new VisualElement { name = "cell-check" };
            checkVe.AddToClassList("icon-grid__cell-check");
            checkVe.style.display = DisplayStyle.None;
            cell.Add(checkVe);

            var badge = new Label { name = "cell-badge" };
            badge.AddToClassList("icon-grid__cell-badge");
            badge.style.display = DisplayStyle.None;
            cell.Add(badge);

            var label = new Label();
            label.AddToClassList("icon-grid__cell-label");
            cell.Add(label);

            var actionBtn = new Button { name = "cell-action-btn" };
            actionBtn.AddToClassList("icon-grid__cell-action-btn");
            actionBtn.style.display = DisplayStyle.None;
            actionBtn.clicked += () =>
            {
                if (cell.userData is not int idx) return;
                if (idx < 0 || idx >= _items.Count) return;
                var item = _items[idx];
                if (item.IsImported)
                    OnQuickDeleteClicked?.Invoke(item);
                else
                    OnQuickImportClicked?.Invoke(item);
            };
            cell.Add(actionBtn);

            cell.RegisterCallback<MouseEnterEvent>(_ => ShowCellAction(cell, true));
            cell.RegisterCallback<MouseLeaveEvent>(_ => ShowCellAction(cell, false));

            _viewport.Add(cell);
            return cell;
        }

        private VisualElement GetOrCreateHeader()
        {
            if (_headerPool.Count > 0)
            {
                var recycled = _headerPool[_headerPool.Count - 1];
                _headerPool.RemoveAt(_headerPool.Count - 1);
                recycled.style.display = DisplayStyle.Flex;
                return recycled;
            }

            var header = new VisualElement();
            header.AddToClassList("icon-grid__header");
            header.style.position = Position.Absolute;

            var label = new Label();
            header.Add(label);

            _viewport.Add(header);
            return header;
        }

        #endregion Cell / Header creation

        #region Bind Methods

        private void BindCellFlat(VisualElement cell, int index, int columns)
        {
            var entry = _items[index];
            cell.userData = index;

            int row = index / columns;
            int col = index % columns;
            cell.style.left = col * CELL_WIDTH;
            cell.style.top = row * CELL_HEIGHT;

            BindCellContent(cell, entry, index);
        }

        private void BindCellGrouped(VisualElement cell, IconGridLayout.LayoutEntry le)
        {
            cell.userData = le.DataIndex;
            cell.style.left = le.Left;
            cell.style.top = le.Top;

            BindCellContent(cell, _items[le.DataIndex], le.DataIndex);
        }

        private void BindCellContent(VisualElement cell, IconEntry entry, int dataIndex)
        {
            cell.userData = dataIndex;

            var iconVe = cell.Q<VisualElement>("cell-icon");
            iconVe.style.backgroundImage = IconGrid.GetIconBackground(entry);

            var checkVe = cell.Q<VisualElement>("cell-check");
            checkVe.style.display = (entry.IsImported && entry.LocalAsset == null)
                ? DisplayStyle.Flex : DisplayStyle.None;

            var badge = cell.Q<Label>("cell-badge");
            if (entry.VariantCount > 1)
            {
                badge.text = entry.VariantCount.ToString();
                badge.style.display = DisplayStyle.Flex;
            }
            else
            {
                badge.style.display = DisplayStyle.None;
            }

            var label = cell.Q<Label>(className: "icon-grid__cell-label");
            label.text = IconGrid.TruncateName(entry.Name);
            label.tooltip = entry.Name;

            bool selected = _selectedIndices.Contains(dataIndex);
            cell.EnableInClassList("icon-grid__cell--selected", selected);

            var actionBtn = cell.Q<Button>("cell-action-btn");
            if (actionBtn != null)
                actionBtn.style.display = DisplayStyle.None;
            if (_showActions && selected && _selectedIndices.Count <= 1)
                ShowCellAction(cell, false);
        }

        private static void BindHeader(VisualElement header, IconGridLayout.LayoutEntry le)
        {
            header.style.left = le.Left;
            header.style.top = le.Top;
            header.style.width = le.Width;
            header.style.height = le.Height;
            header.Q<Label>().text = le.HeaderText;
        }

        #endregion Bind Methods

        #region Virtualization

        private (int, int) UpdateVisibleFlat(IconGridLayout layout)
        {
            if (_items.Count == 0 || layout.Columns <= 0) return (-1, -1);

            var scrollY = _scrollView.scrollOffset.y;
            var viewH = _scrollView.contentViewport.resolvedStyle.height;
            if (float.IsNaN(viewH) || viewH <= 0) return (-1, -1);

            int firstRow = Mathf.Max(0, (int)(scrollY / CELL_HEIGHT) - 1);
            int lastRow = (int)((scrollY + viewH) / CELL_HEIGHT) + 1;

            int firstIndex = firstRow * layout.Columns;
            int lastIndex = Mathf.Min(_items.Count - 1, (lastRow + 1) * layout.Columns - 1);

            if (firstIndex == _visibleFirst && lastIndex == _visibleLast) return (firstIndex, lastIndex);

            RecycleOutOfRange(firstIndex, lastIndex);

            for (int i = firstIndex; i <= lastIndex; i++)
            {
                if (_activeCells.ContainsKey(i)) continue;
                var cell = GetOrCreateCell();
                BindCellFlat(cell, i, layout.Columns);
                _activeCells[i] = cell;
            }

            _visibleFirst = firstIndex;
            _visibleLast = lastIndex;
            return (firstIndex, lastIndex);
        }

        private (int, int) UpdateVisibleGrouped(IconGridLayout layout)
        {
            var entries = layout.Entries;
            if (entries.Count == 0 || layout.Columns <= 0) return (-1, -1);

            var scrollY = _scrollView.scrollOffset.y;
            var viewH = _scrollView.contentViewport.resolvedStyle.height;
            if (float.IsNaN(viewH) || viewH <= 0) return (-1, -1);

            int firstVisible = -1, lastVisible = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                float bottom = e.Top + e.Height;
                if (bottom >= scrollY && e.Top <= scrollY + viewH)
                {
                    if (firstVisible == -1) firstVisible = i;
                    lastVisible = i;
                }
                else if (firstVisible != -1)
                {
                    break;
                }
            }

            if (firstVisible == -1) return (-1, -1);

            firstVisible = Mathf.Max(0, firstVisible - layout.Columns);
            lastVisible = Mathf.Min(entries.Count - 1, lastVisible + layout.Columns);

            if (firstVisible == _visibleFirst && lastVisible == _visibleLast)
                return (_visibleFirst, _visibleLast);

            RecycleOutOfRange(firstVisible, lastVisible);

            for (int i = firstVisible; i <= lastVisible; i++)
            {
                if (_activeCells.ContainsKey(i)) continue;

                var entry = entries[i];
                if (entry.IsHeader)
                {
                    var header = GetOrCreateHeader();
                    BindHeader(header, entry);
                    _activeCells[i] = header;
                }
                else
                {
                    var cell = GetOrCreateCell();
                    BindCellGrouped(cell, entry);
                    _activeCells[i] = cell;
                }
            }

            _visibleFirst = firstVisible;
            _visibleLast = lastVisible;

            int minData = int.MaxValue, maxData = int.MinValue;
            for (int i = firstVisible; i <= lastVisible; i++)
            {
                if (!entries[i].IsHeader)
                {
                    int d = entries[i].DataIndex;
                    if (d < minData) minData = d;
                    if (d > maxData) maxData = d;
                }
            }
            return minData != int.MaxValue ? (minData, maxData) : (-1, -1);
        }

        private void RecycleOutOfRange(int first, int last)
        {
            _recycleBuffer.Clear();
            foreach (var kv in _activeCells)
            {
                if (kv.Key < first || kv.Key > last)
                    _recycleBuffer.Add(kv.Key);
            }
            foreach (var idx in _recycleBuffer)
            {
                var el = _activeCells[idx];
                el.style.display = DisplayStyle.None;

                if (el.ClassListContains("icon-grid__header"))
                    _headerPool.Add(el);
                else
                    _cellPool.Add(el);

                _activeCells.Remove(idx);
            }
        }

        #endregion Virtualization

        #region Cell Action

        private void ShowCellAction(VisualElement cell, bool hovered)
        {
            if (!_showActions) return;
            var btn = cell.Q<Button>("cell-action-btn");
            if (btn == null) return;

            if (cell.userData is not int idx)
            {
                btn.style.display = DisplayStyle.None;
                return;
            }
            if (idx < 0 || idx >= _items.Count)
            {
                btn.style.display = DisplayStyle.None;
                return;
            }

            bool singleSel = _selectedIndices.Count <= 1;
            bool selected = _selectedIndices.Contains(idx);
            bool show = singleSel && (hovered || selected);

            if (show)
            {
                var entry = _items[idx];
                btn.text = entry.IsImported ? "Delete" : "Import";
                btn.EnableInClassList("icon-grid__cell-action-btn--delete", entry.IsImported);
                btn.EnableInClassList("icon-grid__cell-action-btn--import", !entry.IsImported);
                btn.style.display = DisplayStyle.Flex;
            }
            else
            {
                btn.style.display = DisplayStyle.None;
            }
        }

        #endregion Cell Action
    }
}
