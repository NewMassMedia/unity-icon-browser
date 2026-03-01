using System;
using System.Collections.Generic;
using System.Linq;
using IconBrowser.Data;
using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser.UI
{
    /// <summary>
    /// Virtualized icon grid with optional alphabetical grouping.
    /// Flat mode: simple grid (Browse tab). Grouped mode: alphabet headers (Project tab).
    /// </summary>
    public class IconGrid : VisualElement
    {
        const int CELL_WIDTH = IconBrowserConstants.CELL_WIDTH;
        const int CELL_HEIGHT = IconBrowserConstants.CELL_HEIGHT;
        const int HEADER_HEIGHT = IconBrowserConstants.HEADER_HEIGHT;
        const float DRAG_THRESHOLD = IconBrowserConstants.DRAG_THRESHOLD;

        readonly ScrollView _scrollView;
        readonly VisualElement _viewport;
        readonly Label _emptyLabel;

        readonly List<VisualElement> _cellPool = new();
        readonly List<VisualElement> _headerPool = new();
        readonly Dictionary<int, VisualElement> _activeCells = new(); // layout index -> element

        List<IconEntry> _items = new();
        readonly HashSet<int> _selectedDataIndices = new();
        int _lastClickedDataIndex = -1;
        int _columns = 1;
        public int Columns => _columns;
        int _visibleFirst = -1;
        int _visibleLast = -1;

        // Drag selection
        VisualElement _selectionRect;
        bool _isDragging;
        bool _dragThresholdMet;
        Vector2 _dragStart, _dragCurrent;
        HashSet<int> _preDragSnapshot = new();
        int _dragPointerId = -1;
        int _pointerDownDataIndex = -1;
        int _pointerDownClickCount;
        EventModifiers _pointerDownModifiers;

        // Grouped layout
        bool _grouped;
        readonly List<LayoutEntry> _layout = new();

        struct LayoutEntry
        {
            public bool IsHeader;
            public int DataIndex;   // index into _items (-1 for headers)
            public string HeaderText;
            public float Left, Top, Width, Height;
        }

        public event Action<IconEntry> OnIconSelected;
        public event Action<IconEntry> OnIconDoubleClicked;
        public event Action<int, int> OnVisibleRangeChanged;
        public event Action<List<IconEntry>> OnSelectionChanged;
        public event Action<IconEntry> OnQuickImportClicked;
        public event Action<IconEntry> OnQuickDeleteClicked;

        /// <summary>
        /// When true, cells show an inline action button (Import / Delete) on hover/select.
        /// Only shown for single selection.
        /// </summary>
        public bool ShowActionButtons { get; set; }

        public List<IconEntry> SelectedEntries => _selectedDataIndices
            .Where(i => i >= 0 && i < _items.Count).Select(i => _items[i]).ToList();

        /// <summary>
        /// When true, items are sorted by name and grouped under alphabet headers.
        /// </summary>
        public bool GroupByAlpha
        {
            get => _grouped;
            set => _grouped = value;
        }

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

            // Drag selection rectangle (overlaid on contentViewport)
            _selectionRect = new VisualElement();
            _selectionRect.AddToClassList("icon-grid__selection-rect");
            _selectionRect.style.position = Position.Absolute;
            _selectionRect.pickingMode = PickingMode.Ignore;
            _selectionRect.style.display = DisplayStyle.None;

            // Register drag events and add selection rect to contentViewport once geometry is ready
            _scrollView.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var vp = _scrollView.contentViewport;
                if (_selectionRect.parent == null)
                {
                    vp.Add(_selectionRect);
                    // Register pointer events on contentViewport so capture works correctly
                    vp.RegisterCallback<PointerDownEvent>(OnDragPointerDown);
                    vp.RegisterCallback<PointerMoveEvent>(OnDragPointerMove);
                    vp.RegisterCallback<PointerUpEvent>(OnDragPointerUp);
                }
            });

            _scrollView.verticalScroller.valueChanged += _ => UpdateVisible();
            _scrollView.RegisterCallback<GeometryChangedEvent>(_ => OnLayoutChanged());

            focusable = true;
            RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape && _selectedDataIndices.Count > 0)
                {
                    ClearSelection();
                    evt.StopPropagation();
                }
            });
        }

        public void SetItems(List<IconEntry> items)
        {
            _items = items ?? new List<IconEntry>();
            _selectedDataIndices.Clear();
            _lastClickedDataIndex = -1;
            _visibleFirst = -1;
            _visibleLast = -1;
            _layout.Clear();

            RecycleCells();

            if (_items.Count == 0)
            {
                _emptyLabel.style.display = DisplayStyle.Flex;
                _scrollView.style.display = DisplayStyle.None;
                return;
            }

            _emptyLabel.style.display = DisplayStyle.None;
            _scrollView.style.display = DisplayStyle.Flex;
            _scrollView.scrollOffset = Vector2.zero;

            if (_grouped)
                _items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            schedule.Execute(OnLayoutChanged);
        }

        public void RefreshPreviews()
        {
            foreach (var kv in _activeCells)
            {
                int dataIdx = GetDataIndex(kv.Key);
                if (dataIdx < 0 || dataIdx >= _items.Count) continue;

                var entry = _items[dataIdx];
                var cell = kv.Value;

                var iconVe = cell.Q<VisualElement>("cell-icon");
                if (iconVe != null)
                    iconVe.style.backgroundImage = GetIconBackground(entry);

                // Refresh imported check mark
                var checkVe = cell.Q<VisualElement>("cell-check");
                if (checkVe != null)
                    checkVe.style.display = (entry.IsImported && entry.LocalAsset == null)
                        ? DisplayStyle.Flex : DisplayStyle.None;

                // Refresh action button state
                if (ShowActionButtons)
                {
                    bool selected = _selectedDataIndices.Contains(dataIdx);
                    if (selected && _selectedDataIndices.Count == 1)
                        ShowCellAction(cell, false);
                    else
                    {
                        var btn = cell.Q<Button>("cell-action-btn");
                        if (btn != null) btn.style.display = DisplayStyle.None;
                    }
                }
            }
        }

        public void Select(IconEntry entry)
        {
            _selectedDataIndices.Clear();
            int idx = _items.IndexOf(entry);
            if (idx >= 0)
            {
                _selectedDataIndices.Add(idx);
                _lastClickedDataIndex = idx;
            }
            RefreshSelectionVisuals();
            FireSelectionChanged();
        }

        public void ClearSelection()
        {
            _selectedDataIndices.Clear();
            _lastClickedDataIndex = -1;
            RefreshSelectionVisuals();
            FireSelectionChanged();
        }

        void RefreshSelectionVisuals()
        {
            foreach (var kv in _activeCells)
            {
                int dataIdx = GetDataIndex(kv.Key);
                var cell = kv.Value;
                bool selected = _selectedDataIndices.Contains(dataIdx);
                cell.EnableInClassList("icon-grid__cell--selected", selected);

                // Update action button visibility for selection changes
                if (ShowActionButtons)
                {
                    var btn = cell.Q<Button>("cell-action-btn");
                    if (btn != null)
                    {
                        if (selected && _selectedDataIndices.Count == 1)
                            ShowCellAction(cell, false);
                        else
                            btn.style.display = DisplayStyle.None;
                    }
                }
            }
        }

        void FireSelectionChanged()
        {
            OnSelectionChanged?.Invoke(SelectedEntries);
        }

        int GetDataIndex(int layoutKey)
        {
            if (!_grouped) return layoutKey;
            if (layoutKey >= 0 && layoutKey < _layout.Count && !_layout[layoutKey].IsHeader)
                return _layout[layoutKey].DataIndex;
            return -1;
        }

        int DataIndexAtContentPosition(float contentX, float contentY)
        {
            if (_grouped)
            {
                for (int i = 0; i < _layout.Count; i++)
                {
                    var le = _layout[i];
                    if (le.IsHeader) continue;
                    if (contentX >= le.Left && contentX < le.Left + le.Width &&
                        contentY >= le.Top && contentY < le.Top + le.Height)
                        return le.DataIndex;
                }
                return -1;
            }
            else
            {
                if (_columns <= 0) return -1;
                int row = (int)(contentY / CELL_HEIGHT);
                int col = (int)(contentX / CELL_WIDTH);
                if (col < 0 || col >= _columns || row < 0) return -1;
                int idx = row * _columns + col;
                return (idx >= 0 && idx < _items.Count) ? idx : -1;
            }
        }

        #region Layout

        void OnLayoutChanged()
        {
            if (_items.Count == 0) return;

            var scrollWidth = _scrollView.contentViewport.resolvedStyle.width;
            if (float.IsNaN(scrollWidth) || scrollWidth <= 0) return;

            int newColumns = Mathf.Max(1, (int)(scrollWidth / CELL_WIDTH));
            bool columnsChanged = newColumns != _columns;
            _columns = newColumns;

            if (_grouped)
                ComputeGroupedLayout();
            else
                ComputeFlatLayout();

            // When columns change, recycle all cells so they get repositioned
            if (columnsChanged)
            {
                RecycleCells();
                _visibleFirst = -1;
                _visibleLast = -1;
            }

            UpdateVisible();
        }

        void ComputeFlatLayout()
        {
            int totalRows = Mathf.CeilToInt((float)_items.Count / _columns);
            _viewport.style.height = totalRows * CELL_HEIGHT;
            _viewport.style.width = _columns * CELL_WIDTH;
        }

        void ComputeGroupedLayout()
        {
            _layout.Clear();
            float y = 0;
            char currentChar = '\0';
            int col = 0;

            for (int i = 0; i < _items.Count; i++)
            {
                if (string.IsNullOrEmpty(_items[i].Name)) continue;

                char firstChar = char.ToUpper(_items[i].Name[0]);

                if (firstChar != currentChar)
                {
                    // Finish previous row
                    if (col > 0) { y += CELL_HEIGHT; col = 0; }

                    _layout.Add(new LayoutEntry
                    {
                        IsHeader = true,
                        DataIndex = -1,
                        HeaderText = firstChar.ToString(),
                        Left = 0, Top = y,
                        Width = _columns * CELL_WIDTH,
                        Height = HEADER_HEIGHT
                    });
                    y += HEADER_HEIGHT;
                    currentChar = firstChar;
                }

                _layout.Add(new LayoutEntry
                {
                    IsHeader = false,
                    DataIndex = i,
                    Left = col * CELL_WIDTH,
                    Top = y,
                    Width = CELL_WIDTH,
                    Height = CELL_HEIGHT
                });

                col++;
                if (col >= _columns) { col = 0; y += CELL_HEIGHT; }
            }

            if (col > 0) y += CELL_HEIGHT;

            _viewport.style.height = y;
            _viewport.style.width = _columns * CELL_WIDTH;
        }

        #endregion

        #region Virtualization

        void UpdateVisible()
        {
            if (_grouped)
                UpdateVisibleGrouped();
            else
                UpdateVisibleFlat();
        }

        void UpdateVisibleFlat()
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

            RecycleOutOfRange(firstIndex, lastIndex);

            for (int i = firstIndex; i <= lastIndex; i++)
            {
                if (_activeCells.ContainsKey(i)) continue;
                var cell = GetOrCreateCell();
                BindCellFlat(cell, i);
                _activeCells[i] = cell;
            }

            _visibleFirst = firstIndex;
            _visibleLast = lastIndex;
            OnVisibleRangeChanged?.Invoke(firstIndex, lastIndex);
        }

        void UpdateVisibleGrouped()
        {
            if (_layout.Count == 0 || _columns <= 0) return;

            var scrollY = _scrollView.scrollOffset.y;
            var viewH = _scrollView.contentViewport.resolvedStyle.height;
            if (float.IsNaN(viewH) || viewH <= 0) return;

            // Find visible layout entries
            int firstVisible = -1, lastVisible = -1;
            for (int i = 0; i < _layout.Count; i++)
            {
                var e = _layout[i];
                float bottom = e.Top + e.Height;
                if (bottom >= scrollY && e.Top <= scrollY + viewH)
                {
                    if (firstVisible == -1) firstVisible = i;
                    lastVisible = i;
                }
                else if (firstVisible != -1)
                {
                    break; // sorted by position
                }
            }

            if (firstVisible == -1) return;

            // Buffer
            firstVisible = Mathf.Max(0, firstVisible - _columns);
            lastVisible = Mathf.Min(_layout.Count - 1, lastVisible + _columns);

            if (firstVisible == _visibleFirst && lastVisible == _visibleLast) return;

            RecycleOutOfRange(firstVisible, lastVisible);

            for (int i = firstVisible; i <= lastVisible; i++)
            {
                if (_activeCells.ContainsKey(i)) continue;

                var entry = _layout[i];
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

            // Fire visible range with data indices
            int minData = int.MaxValue, maxData = int.MinValue;
            for (int i = firstVisible; i <= lastVisible; i++)
            {
                if (!_layout[i].IsHeader)
                {
                    int d = _layout[i].DataIndex;
                    if (d < minData) minData = d;
                    if (d > maxData) maxData = d;
                }
            }
            if (minData != int.MaxValue)
                OnVisibleRangeChanged?.Invoke(minData, maxData);
        }

        void RecycleOutOfRange(int first, int last)
        {
            var toRecycle = new List<int>();
            foreach (var kv in _activeCells)
            {
                if (kv.Key < first || kv.Key > last)
                    toRecycle.Add(kv.Key);
            }
            foreach (var idx in toRecycle)
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

        #endregion

        #region Cell / Header creation

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
                var idx = (int)cell.userData;
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

            cell.RegisterCallback<ClickEvent>(OnCellClick);

            _viewport.Add(cell);
            return cell;
        }


        void ShowCellAction(VisualElement cell, bool hovered)
        {
            if (!ShowActionButtons) return;
            var btn = cell.Q<Button>("cell-action-btn");
            if (btn == null) return;

            if (cell.userData == null || !(cell.userData is int))
            {
                btn.style.display = DisplayStyle.None;
                return;
            }
            int idx = (int)cell.userData;
            if (idx < 0 || idx >= _items.Count)
            {
                btn.style.display = DisplayStyle.None;
                return;
            }

            bool singleSel = _selectedDataIndices.Count <= 1;
            bool selected = _selectedDataIndices.Contains(idx);
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

        VisualElement GetOrCreateHeader()
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

        void BindCellFlat(VisualElement cell, int index)
        {
            var entry = _items[index];
            cell.userData = index;

            int row = index / _columns;
            int col = index % _columns;
            cell.style.left = col * CELL_WIDTH;
            cell.style.top = row * CELL_HEIGHT;

            BindCellContent(cell, entry, index);
        }

        void BindCellGrouped(VisualElement cell, LayoutEntry le)
        {
            cell.userData = le.DataIndex;
            cell.style.left = le.Left;
            cell.style.top = le.Top;

            BindCellContent(cell, _items[le.DataIndex], le.DataIndex);
        }

        void BindCellContent(VisualElement cell, IconEntry entry, int dataIndex)
        {
            cell.userData = dataIndex;

            var iconVe = cell.Q<VisualElement>("cell-icon");
            iconVe.style.backgroundImage = GetIconBackground(entry);

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
            label.text = TruncateName(entry.Name);
            label.tooltip = entry.Name;

            bool selected = _selectedDataIndices.Contains(dataIndex);
            cell.EnableInClassList("icon-grid__cell--selected", selected);

            // Action button — show if selected (single)
            var actionBtn = cell.Q<Button>("cell-action-btn");
            if (actionBtn != null)
                actionBtn.style.display = DisplayStyle.None;
            if (ShowActionButtons && selected && _selectedDataIndices.Count <= 1)
                ShowCellAction(cell, false);
        }

        void BindHeader(VisualElement header, LayoutEntry le)
        {
            header.style.left = le.Left;
            header.style.top = le.Top;
            header.style.width = le.Width;
            header.style.height = le.Height;
            header.Q<Label>().text = le.HeaderText;
        }

        #endregion

        #region Click handling

        void OnCellClick(ClickEvent evt)
        {
            // Click handling is now done via PointerDown/Up on contentViewport.
            // This handler is kept as a fallback but pointer capture prevents it from firing.
        }

        #endregion

        #region Drag selection

        void OnDragPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;

            _isDragging = true;
            _dragThresholdMet = false;
            _dragPointerId = evt.pointerId;

            // Store position relative to the contentViewport
            var viewportPos = _scrollView.contentViewport.WorldToLocal(evt.position);
            _dragStart = viewportPos;
            _dragCurrent = viewportPos;

            // Determine which cell (if any) is under the pointer
            var contentPos = viewportPos + new Vector2(0, _scrollView.scrollOffset.y);
            _pointerDownDataIndex = DataIndexAtContentPosition(contentPos.x, contentPos.y);
            _pointerDownClickCount = evt.clickCount;
            _pointerDownModifiers = evt.modifiers;

            bool isCtrl = (evt.modifiers & EventModifiers.Control) != 0
                       || (evt.modifiers & EventModifiers.Command) != 0;

            // Snapshot current selection for Ctrl+Drag
            _preDragSnapshot.Clear();
            if (isCtrl)
            {
                foreach (var idx in _selectedDataIndices)
                    _preDragSnapshot.Add(idx);
            }
            else if (_pointerDownDataIndex < 0)
            {
                // Empty area, non-Ctrl: clear immediately for visual feedback
                _selectedDataIndices.Clear();
                RefreshSelectionVisuals();
            }

            _scrollView.contentViewport.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        void OnDragPointerMove(PointerMoveEvent evt)
        {
            if (!_isDragging || evt.pointerId != _dragPointerId) return;

            var viewportPos = _scrollView.contentViewport.WorldToLocal(evt.position);
            _dragCurrent = viewportPos;

            var delta = _dragCurrent - _dragStart;
            if (!_dragThresholdMet)
            {
                if (Mathf.Abs(delta.x) < DRAG_THRESHOLD && Mathf.Abs(delta.y) < DRAG_THRESHOLD)
                    return;
                _dragThresholdMet = true;
            }

            // Update selection rectangle visuals (in viewport space)
            float left = Mathf.Min(_dragStart.x, _dragCurrent.x);
            float top = Mathf.Min(_dragStart.y, _dragCurrent.y);
            float width = Mathf.Abs(_dragCurrent.x - _dragStart.x);
            float height = Mathf.Abs(_dragCurrent.y - _dragStart.y);

            _selectionRect.style.display = DisplayStyle.Flex;
            _selectionRect.style.left = left;
            _selectionRect.style.top = top;
            _selectionRect.style.width = width;
            _selectionRect.style.height = height;

            UpdateDragSelection();
            evt.StopPropagation();
        }

        void OnDragPointerUp(PointerUpEvent evt)
        {
            if (!_isDragging || evt.pointerId != _dragPointerId) return;

            _isDragging = false;
            _selectionRect.style.display = DisplayStyle.None;
            _scrollView.contentViewport.ReleasePointer(evt.pointerId);
            _dragPointerId = -1;

            if (!_dragThresholdMet)
            {
                // No drag occurred — treat as a click
                int dataIndex = _pointerDownDataIndex;
                if (dataIndex >= 0 && dataIndex < _items.Count)
                {
                    var entry = _items[dataIndex];

                    // Double-click
                    if (_pointerDownClickCount >= 2)
                    {
                        OnIconDoubleClicked?.Invoke(entry);
                        evt.StopPropagation();
                        return;
                    }

                    bool isCtrl = (_pointerDownModifiers & EventModifiers.Control) != 0
                               || (_pointerDownModifiers & EventModifiers.Command) != 0;
                    bool isShift = (_pointerDownModifiers & EventModifiers.Shift) != 0;

                    if (isCtrl)
                    {
                        if (_selectedDataIndices.Contains(dataIndex))
                            _selectedDataIndices.Remove(dataIndex);
                        else
                            _selectedDataIndices.Add(dataIndex);
                        _lastClickedDataIndex = dataIndex;
                    }
                    else if (isShift && _lastClickedDataIndex >= 0)
                    {
                        int min = Mathf.Min(_lastClickedDataIndex, dataIndex);
                        int max = Mathf.Max(_lastClickedDataIndex, dataIndex);
                        _selectedDataIndices.Clear();
                        for (int i = min; i <= max; i++)
                            _selectedDataIndices.Add(i);
                    }
                    else
                    {
                        _selectedDataIndices.Clear();
                        _selectedDataIndices.Add(dataIndex);
                        _lastClickedDataIndex = dataIndex;
                    }

                    RefreshSelectionVisuals();
                    OnIconSelected?.Invoke(entry);
                }
                // else: empty area click — selection already cleared in PointerDown
            }

            FireSelectionChanged();
            evt.StopPropagation();
        }

        void UpdateDragSelection()
        {
            // Convert viewport coordinates to content coordinates (add scroll offset)
            var scrollOffset = _scrollView.scrollOffset;
            float left = Mathf.Min(_dragStart.x, _dragCurrent.x);
            float top = Mathf.Min(_dragStart.y, _dragCurrent.y) + scrollOffset.y;
            float right = Mathf.Max(_dragStart.x, _dragCurrent.x);
            float bottom = Mathf.Max(_dragStart.y, _dragCurrent.y) + scrollOffset.y;

            var dragRect = new Rect(left, top, right - left, bottom - top);

            // Start from pre-drag snapshot (Ctrl+Drag preserves prior selection)
            _selectedDataIndices.Clear();
            foreach (var idx in _preDragSnapshot)
                _selectedDataIndices.Add(idx);

            if (_grouped)
            {
                // Grouped mode: iterate layout entries and check overlap
                for (int i = 0; i < _layout.Count; i++)
                {
                    var le = _layout[i];
                    if (le.IsHeader) continue;

                    var cellRect = new Rect(le.Left, le.Top, le.Width, le.Height);
                    if (cellRect.Overlaps(dragRect))
                        _selectedDataIndices.Add(le.DataIndex);
                }
            }
            else
            {
                // Flat mode: compute indices from grid positions
                if (_columns <= 0) return;

                int firstRow = Mathf.Max(0, (int)(top / CELL_HEIGHT));
                int lastRow = (int)(bottom / CELL_HEIGHT);
                int firstCol = Mathf.Max(0, (int)(left / CELL_WIDTH));
                int lastCol = Mathf.Min(_columns - 1, (int)(right / CELL_WIDTH));

                for (int row = firstRow; row <= lastRow; row++)
                {
                    for (int col = firstCol; col <= lastCol; col++)
                    {
                        int idx = row * _columns + col;
                        if (idx >= 0 && idx < _items.Count)
                        {
                            // Check actual cell rect overlap
                            var cellRect = new Rect(col * CELL_WIDTH, row * CELL_HEIGHT, CELL_WIDTH, CELL_HEIGHT);
                            if (cellRect.Overlaps(dragRect))
                                _selectedDataIndices.Add(idx);
                        }
                    }
                }
            }

            RefreshSelectionVisuals();
        }

        #endregion

        void RecycleCells()
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

        static StyleBackground GetIconBackground(IconEntry entry)
        {
            if (entry.LocalAsset != null)
                return new StyleBackground(Background.FromVectorImage(entry.LocalAsset));
            if (entry.PreviewSprite != null)
                return new StyleBackground(Background.FromSprite(entry.PreviewSprite));
            return StyleKeyword.None;
        }

        internal static string TruncateName(string name)
        {
            return name.Length > IconBrowserConstants.TRUNCATE_LENGTH
                ? name.Substring(0, IconBrowserConstants.TRUNCATE_LENGTH - 1) + ".."
                : name;
        }
    }
}
