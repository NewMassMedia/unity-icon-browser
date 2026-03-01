using System.Collections.Generic;
using UnityEngine;

namespace IconBrowser.UI
{
    /// <summary>
    /// Pure layout calculator for the icon grid.
    /// Computes cell positions for flat (simple grid) and grouped (alphabet headers) modes.
    /// Stateless with respect to UI — no VisualElement references.
    /// </summary>
    internal sealed class IconGridLayout
    {
        #region Variables

        private const int CELL_WIDTH = IconBrowserConstants.CELL_WIDTH;
        private const int CELL_HEIGHT = IconBrowserConstants.CELL_HEIGHT;
        private const int HEADER_HEIGHT = IconBrowserConstants.HEADER_HEIGHT;

        private readonly List<LayoutEntry> _entries = new();
        private readonly HashSet<int> _hitTestBuffer = new();

        private int _columns;
        private bool _isGrouped;
        private int _itemCount;

        #endregion Variables

        #region Properties

        public IReadOnlyList<LayoutEntry> Entries => _entries;
        public int Columns => _columns;
        public float TotalHeight { get; private set; }
        public float TotalWidth { get; private set; }

        #endregion Properties

        #region Structs

        public struct LayoutEntry
        {
            public bool IsHeader;
            public int DataIndex;   // index into items (-1 for headers)
            public string HeaderText;
            public float Left, Top, Width, Height;
        }

        #endregion Structs

        #region Help Methods

        /// <summary>
        /// Compute layout for the given items and viewport width.
        /// </summary>
        public void Compute(int itemCount, bool isGrouped, float viewportWidth, List<Data.IconEntry> items)
        {
            _itemCount = itemCount;
            _isGrouped = isGrouped;
            _columns = Mathf.Max(1, (int)(viewportWidth / CELL_WIDTH));

            if (_isGrouped)
                ComputeGrouped(items);
            else
                ComputeFlat();
        }

        /// <summary>
        /// Returns the data index at the given content-space position, or -1 if none.
        /// Used by DragSelectionHandler for single-point hit testing.
        /// </summary>
        public int DataIndexAtContent(float contentX, float contentY)
        {
            if (_isGrouped)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    var le = _entries[i];
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
                return (idx >= 0 && idx < _itemCount) ? idx : -1;
            }
        }

        /// <summary>
        /// Returns data indices whose cells overlap the given content-space rectangle.
        /// Used by DragSelectionHandler for rectangle selection.
        /// </summary>
        public HashSet<int> HitTestRect(Rect dragRect)
        {
            _hitTestBuffer.Clear();

            if (_isGrouped)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    var le = _entries[i];
                    if (le.IsHeader) continue;

                    var cellRect = new Rect(le.Left, le.Top, le.Width, le.Height);
                    if (cellRect.Overlaps(dragRect))
                        _hitTestBuffer.Add(le.DataIndex);
                }
            }
            else
            {
                if (_columns <= 0) return _hitTestBuffer;

                int firstRow = Mathf.Max(0, (int)(dragRect.y / CELL_HEIGHT));
                int lastRow = (int)((dragRect.y + dragRect.height) / CELL_HEIGHT);
                int firstCol = Mathf.Max(0, (int)(dragRect.x / CELL_WIDTH));
                int lastCol = Mathf.Min(_columns - 1, (int)((dragRect.x + dragRect.width) / CELL_WIDTH));

                for (int row = firstRow; row <= lastRow; row++)
                {
                    for (int col = firstCol; col <= lastCol; col++)
                    {
                        int idx = row * _columns + col;
                        if (idx >= 0 && idx < _itemCount)
                        {
                            var cellRect = new Rect(col * CELL_WIDTH, row * CELL_HEIGHT, CELL_WIDTH, CELL_HEIGHT);
                            if (cellRect.Overlaps(dragRect))
                                _hitTestBuffer.Add(idx);
                        }
                    }
                }
            }

            return _hitTestBuffer;
        }

        private void ComputeFlat()
        {
            _entries.Clear();
            int totalRows = Mathf.CeilToInt((float)_itemCount / _columns);
            TotalHeight = totalRows * CELL_HEIGHT;
            TotalWidth = _columns * CELL_WIDTH;
        }

        private void ComputeGrouped(List<Data.IconEntry> items)
        {
            _entries.Clear();
            float y = 0;
            char currentChar = '\0';
            int col = 0;

            for (int i = 0; i < items.Count; i++)
            {
                if (string.IsNullOrEmpty(items[i].Name)) continue;

                char firstChar = char.ToUpper(items[i].Name[0]);

                if (firstChar != currentChar)
                {
                    if (col > 0) { y += CELL_HEIGHT; col = 0; }

                    _entries.Add(new LayoutEntry
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

                _entries.Add(new LayoutEntry
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

            TotalHeight = y;
            TotalWidth = _columns * CELL_WIDTH;
        }

        #endregion Help Methods
    }
}
