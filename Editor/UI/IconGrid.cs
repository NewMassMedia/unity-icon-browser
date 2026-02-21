using System;
using System.Collections.Generic;
using IconBrowser.Data;
using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser.UI
{
    /// <summary>
    /// Virtualized icon grid — only creates VisualElements for visible cells.
    /// Uses a spacer element for total scroll height and repositions cells on scroll.
    /// </summary>
    public class IconGrid : VisualElement
    {
        const int CELL_WIDTH = 76;
        const int CELL_HEIGHT = 84;

        readonly ScrollView _scrollView;
        readonly VisualElement _viewport; // sized to full content height
        readonly Label _emptyLabel;

        readonly List<VisualElement> _cellPool = new();
        readonly Dictionary<int, VisualElement> _activeCells = new(); // index -> cell

        List<IconEntry> _items = new();
        IconEntry _selectedEntry;
        int _selectedIndex = -1;
        int _columns = 1;
        int _visibleFirst = -1;
        int _visibleLast = -1;

        public event Action<IconEntry> OnIconSelected;
        public event Action<IconEntry> OnIconDoubleClicked;
        public event Action<int, int> OnVisibleRangeChanged;

        public IconGrid()
        {
            AddToClassList("icon-grid");
            style.flexGrow = 1;

            _scrollView = new ScrollView(ScrollViewMode.Vertical);
            _scrollView.AddToClassList("icon-grid__scroll");
            _scrollView.style.flexGrow = 1;
            hierarchy.Add(_scrollView);

            _viewport = new VisualElement();
            _viewport.AddToClassList("icon-grid__viewport");
            _viewport.style.position = Position.Relative;
            _scrollView.Add(_viewport);

            _emptyLabel = new Label("No icons found");
            _emptyLabel.AddToClassList("icon-grid__empty");
            _emptyLabel.style.display = DisplayStyle.None;
            hierarchy.Add(_emptyLabel);

            _scrollView.verticalScroller.valueChanged += _ => UpdateVisibleCells();
            _scrollView.RegisterCallback<GeometryChangedEvent>(_ => OnLayoutChanged());
        }

        public void SetItems(List<IconEntry> items)
        {
            _items = items ?? new List<IconEntry>();
            _selectedEntry = null;
            _selectedIndex = -1;
            _visibleFirst = -1;
            _visibleLast = -1;

            RecycleCells();

            if (_items.Count == 0)
            {
                _emptyLabel.style.display = DisplayStyle.Flex;
                _scrollView.style.display = DisplayStyle.None;
                return;
            }

            _emptyLabel.style.display = DisplayStyle.None;
            _scrollView.style.display = DisplayStyle.Flex;

            // Reset scroll
            _scrollView.scrollOffset = Vector2.zero;

            // Schedule layout after geometry resolves
            schedule.Execute(OnLayoutChanged);
        }

        public void RefreshPreviews()
        {
            foreach (var kv in _activeCells)
            {
                if (kv.Key < 0 || kv.Key >= _items.Count) continue;
                var entry = _items[kv.Key];
                var iconVe = kv.Value.Q<VisualElement>("cell-icon");
                if (iconVe == null) continue;

                var image = GetIconImage(entry);
                if (image != null)
                    iconVe.style.backgroundImage = new StyleBackground(Background.FromVectorImage(image));
            }
        }

        public void Select(IconEntry entry)
        {
            // Deselect previous
            if (_selectedIndex >= 0 && _activeCells.TryGetValue(_selectedIndex, out var prevCell))
                prevCell.RemoveFromClassList("icon-grid__cell--selected");

            _selectedEntry = entry;
            _selectedIndex = _items.IndexOf(entry);

            if (_selectedIndex >= 0 && _activeCells.TryGetValue(_selectedIndex, out var cell))
                cell.AddToClassList("icon-grid__cell--selected");
        }

        void OnLayoutChanged()
        {
            if (_items.Count == 0) return;

            var scrollWidth = _scrollView.contentViewport.resolvedStyle.width;
            if (float.IsNaN(scrollWidth) || scrollWidth <= 0) return;

            _columns = Mathf.Max(1, (int)(scrollWidth / CELL_WIDTH));
            int totalRows = Mathf.CeilToInt((float)_items.Count / _columns);
            float totalHeight = totalRows * CELL_HEIGHT;

            _viewport.style.height = totalHeight;
            _viewport.style.width = _columns * CELL_WIDTH;

            UpdateVisibleCells();
        }

        void UpdateVisibleCells()
        {
            if (_items.Count == 0 || _columns <= 0) return;

            var scrollY = _scrollView.scrollOffset.y;
            var viewH = _scrollView.contentViewport.resolvedStyle.height;
            if (float.IsNaN(viewH) || viewH <= 0) return;

            int firstRow = Mathf.Max(0, (int)(scrollY / CELL_HEIGHT) - 1);
            int lastRow = (int)((scrollY + viewH) / CELL_HEIGHT) + 1;

            int firstIndex = firstRow * _columns;
            int lastIndex = Mathf.Min(_items.Count - 1, (lastRow + 1) * _columns - 1);

            if (firstIndex == _visibleFirst && lastIndex == _visibleLast) return;

            // Recycle cells outside new range
            var toRecycle = new List<int>();
            foreach (var kv in _activeCells)
            {
                if (kv.Key < firstIndex || kv.Key > lastIndex)
                    toRecycle.Add(kv.Key);
            }
            foreach (var idx in toRecycle)
            {
                var cell = _activeCells[idx];
                cell.style.display = DisplayStyle.None;
                _cellPool.Add(cell);
                _activeCells.Remove(idx);
            }

            // Create/reuse cells for new visible range
            for (int i = firstIndex; i <= lastIndex; i++)
            {
                if (_activeCells.ContainsKey(i)) continue;

                var cell = GetOrCreateCell();
                BindCell(cell, i);
                _activeCells[i] = cell;
            }

            _visibleFirst = firstIndex;
            _visibleLast = lastIndex;

            OnVisibleRangeChanged?.Invoke(firstIndex, lastIndex);
        }

        VisualElement GetOrCreateCell()
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
            iconVe.style.width = 24;
            iconVe.style.height = 24;
            cell.Add(iconVe);

            var checkVe = new VisualElement { name = "cell-check" };
            checkVe.AddToClassList("icon-grid__cell-check");
            checkVe.style.display = DisplayStyle.None;
            cell.Add(checkVe);

            var label = new Label();
            label.AddToClassList("icon-grid__cell-label");
            cell.Add(label);

            cell.RegisterCallback<ClickEvent>(OnCellClick);

            _viewport.Add(cell);
            return cell;
        }

        void BindCell(VisualElement cell, int index)
        {
            var entry = _items[index];
            cell.userData = index; // store index, not entry — for click lookup

            // Position
            int row = index / _columns;
            int col = index % _columns;
            cell.style.left = col * CELL_WIDTH;
            cell.style.top = row * CELL_HEIGHT;

            // Icon
            var iconVe = cell.Q<VisualElement>("cell-icon");
            var image = GetIconImage(entry);
            if (image != null)
                iconVe.style.backgroundImage = new StyleBackground(Background.FromVectorImage(image));
            else
                iconVe.style.backgroundImage = StyleKeyword.None;

            // Check overlay
            var checkVe = cell.Q<VisualElement>("cell-check");
            checkVe.style.display = (entry.IsImported && entry.LocalAsset == null)
                ? DisplayStyle.Flex : DisplayStyle.None;

            // Label
            var label = cell.Q<Label>();
            label.text = TruncateName(entry.Name);
            label.tooltip = entry.Name;

            // Selection
            cell.EnableInClassList("icon-grid__cell--selected", index == _selectedIndex);
        }

        void OnCellClick(ClickEvent evt)
        {
            var cell = evt.currentTarget as VisualElement;
            if (cell?.userData is not int index || index < 0 || index >= _items.Count) return;

            var entry = _items[index];

            if (evt.clickCount == 2)
            {
                OnIconDoubleClicked?.Invoke(entry);
                return;
            }

            Select(entry);
            OnIconSelected?.Invoke(entry);
        }

        void RecycleCells()
        {
            foreach (var kv in _activeCells)
            {
                kv.Value.style.display = DisplayStyle.None;
                _cellPool.Add(kv.Value);
            }
            _activeCells.Clear();
        }

        static VectorImage GetIconImage(IconEntry entry)
        {
            if (entry.LocalAsset != null) return entry.LocalAsset;
            if (entry.PreviewAsset != null) return entry.PreviewAsset;
            return null;
        }

        static string TruncateName(string name)
        {
            return name.Length > 9 ? name.Substring(0, 8) + ".." : name;
        }
    }
}
