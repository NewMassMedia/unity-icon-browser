using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using IconBrowser.Data;

namespace IconBrowser.UI
{
    /// <summary>
    /// Virtualized icon grid with optional alphabetical grouping.
    /// Coordinates <see cref="IconGridLayout"/> (pure layout math) and
    /// <see cref="IconGridVirtualizer"/> (cell lifecycle / virtualization).
    /// </summary>
    public sealed class IconGrid : VisualElement
    {
        #region Variables

        private readonly ScrollView _scrollView;
        private readonly VisualElement _viewport;
        private readonly Label _emptyLabel;
        private readonly VisualElement _selectionRect;

        private List<IconEntry> _items = new();
        private List<IconEntry> _selectedEntriesCache;
        private bool _isSelectedEntriesDirty = true;
        private bool _isGrouped;

        private DragSelectionHandler _dragHandler;
        private readonly IconGridLayout _layout = new();
        private readonly IconGridVirtualizer _virtualizer;

        #endregion Variables

        #region Events

        public event Action<IconEntry> OnIconSelected = delegate { };
        public event Action<IconEntry> OnIconDoubleClicked = delegate { };
        public event Action<int, int> OnVisibleRangeChanged = delegate { };
        public event Action<List<IconEntry>> OnSelectionChanged = delegate { };
        public event Action<IconEntry> OnQuickImportClicked = delegate { };
        public event Action<IconEntry> OnQuickDeleteClicked = delegate { };

        #endregion Events

        #region Properties

        /// <summary>
        /// When true, cells show an inline action button (Import / Delete) on hover/select.
        /// Only shown for single selection.
        /// </summary>
        public bool ShowActionButtons { get; set; }

        public int Columns => _layout.Columns;

        private HashSet<int> SelectedIndices => _dragHandler?.SelectedIndices ?? EMPTY_INDICES;
        private static readonly HashSet<int> EMPTY_INDICES = new();

        public List<IconEntry> SelectedEntries
        {
            get
            {
                if (_isSelectedEntriesDirty)
                {
                    _selectedEntriesCache = SelectedIndices
                        .Where(i => i >= 0 && i < _items.Count).Select(i => _items[i]).ToList();
                    _isSelectedEntriesDirty = false;
                }
                return _selectedEntriesCache;
            }
        }

        /// <summary>
        /// When true, items are sorted by name and grouped under alphabet headers.
        /// </summary>
        public bool GroupByAlpha
        {
            get => _isGrouped;
            set => _isGrouped = value;
        }

        #endregion Properties

        #region Constructor

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

            _selectionRect = new VisualElement();
            _selectionRect.AddToClassList("icon-grid__selection-rect");
            _selectionRect.style.position = Position.Absolute;
            _selectionRect.pickingMode = PickingMode.Ignore;
            _selectionRect.style.display = DisplayStyle.None;

            // Create virtualizer
            _virtualizer = new IconGridVirtualizer(_viewport, _scrollView);
            _virtualizer.OnQuickImportClicked += entry => OnQuickImportClicked?.Invoke(entry);
            _virtualizer.OnQuickDeleteClicked += entry => OnQuickDeleteClicked?.Invoke(entry);

            // Initialize drag handler once contentViewport geometry is ready
            _scrollView.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var vp = _scrollView.contentViewport;
                if (_selectionRect.parent == null)
                {
                    vp.Add(_selectionRect);
                    _dragHandler = new DragSelectionHandler(
                        vp, _scrollView, _layout.DataIndexAtContent, _selectionRect);
                    _dragHandler.HitTestRect = _layout.HitTestRect;
                    _dragHandler.OnItemClicked -= OnDragItemClicked;
                    _dragHandler.OnItemClicked += OnDragItemClicked;
                    _dragHandler.OnItemDoubleClicked -= OnDragItemDoubleClicked;
                    _dragHandler.OnItemDoubleClicked += OnDragItemDoubleClicked;
                    _dragHandler.OnSelectionChanged -= OnDragSelectionChanged;
                    _dragHandler.OnSelectionChanged += OnDragSelectionChanged;
                }
            });

            _scrollView.verticalScroller.valueChanged += _ => UpdateVisible();
            _scrollView.RegisterCallback<GeometryChangedEvent>(_ => OnLayoutChanged());

            focusable = true;
            RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape && SelectedIndices.Count > 0)
                {
                    ClearSelection();
                    evt.StopPropagation();
                }
            });
        }

        #endregion Constructor

        #region Unity Methods

        public void SetItems(List<IconEntry> items)
        {
            _items = items ?? new List<IconEntry>();
            if (_dragHandler != null)
            {
                _dragHandler.SelectedIndices.Clear();
                _dragHandler.LastClickedIndex = -1;
            }
            InvalidateSelectionCache();

            _virtualizer.ResetVisibleRange();
            _virtualizer.RecycleAll();

            if (_items.Count == 0)
            {
                _emptyLabel.style.display = DisplayStyle.Flex;
                _scrollView.style.display = DisplayStyle.None;
                return;
            }

            _emptyLabel.style.display = DisplayStyle.None;
            _scrollView.style.display = DisplayStyle.Flex;
            _scrollView.scrollOffset = Vector2.zero;

            if (_isGrouped)
                _items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            schedule.Execute(OnLayoutChanged);
        }

        public void RefreshPreviews()
        {
            SyncVirtualizerState();
            _virtualizer.RefreshPreviews(GetDataIndex);
        }

        public void Select(IconEntry entry)
        {
            int idx = _items.IndexOf(entry);
            if (_dragHandler != null)
                _dragHandler.SelectSingle(idx);
            SyncVirtualizerState();
            _virtualizer.RefreshSelectionVisuals(GetDataIndex);
            FireSelectionChanged();
        }

        public void ClearSelection()
        {
            if (_dragHandler != null)
                _dragHandler.ClearSelection();
            SyncVirtualizerState();
            _virtualizer.RefreshSelectionVisuals(GetDataIndex);
            FireSelectionChanged();
        }

        #endregion Unity Methods

        #region Help Methods

        internal static StyleBackground GetIconBackground(IconEntry entry)
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

        private void OnLayoutChanged()
        {
            if (_items.Count == 0) return;

            var scrollWidth = _scrollView.contentViewport.resolvedStyle.width;
            if (float.IsNaN(scrollWidth) || scrollWidth <= 0) return;

            int oldColumns = _layout.Columns;
            _layout.Compute(_items.Count, _isGrouped, scrollWidth, _items);

            _viewport.style.height = _layout.TotalHeight;
            _viewport.style.width = _layout.TotalWidth;

            if (_layout.Columns != oldColumns)
            {
                _virtualizer.RecycleAll();
                _virtualizer.ResetVisibleRange();
            }

            UpdateVisible();
        }

        private void UpdateVisible()
        {
            SyncVirtualizerState();
            var (first, last) = _virtualizer.UpdateVisible(_layout);
            if (first >= 0)
                OnVisibleRangeChanged?.Invoke(first, last);
        }

        private void SyncVirtualizerState()
        {
            _virtualizer.SetState(_items, SelectedIndices, ShowActionButtons);
        }

        private int GetDataIndex(int layoutKey)
        {
            if (!_isGrouped) return layoutKey;
            var entries = _layout.Entries;
            if (layoutKey >= 0 && layoutKey < entries.Count && !entries[layoutKey].IsHeader)
                return entries[layoutKey].DataIndex;
            return -1;
        }

        private void InvalidateSelectionCache() => _isSelectedEntriesDirty = true;

        private void FireSelectionChanged()
        {
            InvalidateSelectionCache();
            OnSelectionChanged?.Invoke(SelectedEntries);
        }

        private void OnDragItemClicked(int dataIndex)
        {
            if (dataIndex >= 0 && dataIndex < _items.Count)
            {
                SyncVirtualizerState();
                _virtualizer.RefreshSelectionVisuals(GetDataIndex);
                OnIconSelected?.Invoke(_items[dataIndex]);
            }
        }

        private void OnDragItemDoubleClicked(int dataIndex)
        {
            if (dataIndex >= 0 && dataIndex < _items.Count)
                OnIconDoubleClicked?.Invoke(_items[dataIndex]);
        }

        private void OnDragSelectionChanged()
        {
            SyncVirtualizerState();
            _virtualizer.RefreshSelectionVisuals(GetDataIndex);
            FireSelectionChanged();
        }

        #endregion Help Methods
    }
}
