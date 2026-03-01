using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser.UI
{
    /// <summary>
    /// Reusable drag-to-select handler for any VisualElement grid.
    /// Handles pointer capture, threshold, rectangle display,
    /// Ctrl/Shift/Click/DoubleClick modifiers.
    /// </summary>
    public class DragSelectionHandler
    {
        private const float DRAG_THRESHOLD = IconBrowserConstants.DRAG_THRESHOLD;

        private readonly VisualElement _target;
        private readonly ScrollView _scrollView;
        private readonly Func<float, float, int> _hitTest;
        private readonly VisualElement _selectionRect;

        private readonly HashSet<int> _selectedIndices = new();
        private readonly HashSet<int> _preDragSnapshot = new();

        private bool _isDragging;
        private bool _isDragThresholdMet;
        private Vector2 _dragStart, _dragCurrent;
        private int _dragPointerId = -1;
        private int _pointerDownDataIndex = -1;
        private int _pointerDownClickCount;
        private EventModifiers _pointerDownModifiers;
        private int _lastClickedIndex = -1;

        /// <summary>
        /// Set of currently selected data indices.
        /// </summary>
        public HashSet<int> SelectedIndices => _selectedIndices;

        /// <summary>
        /// Index of the last item clicked (for Shift+Click range).
        /// </summary>
        public int LastClickedIndex
        {
            get => _lastClickedIndex;
            set => _lastClickedIndex = value;
        }

        /// <summary>
        /// Fired when a single item is clicked.
        /// </summary>
        public event Action<int> OnItemClicked = delegate { };

        /// <summary>
        /// Fired when a single item is double-clicked.
        /// </summary>
        public event Action<int> OnItemDoubleClicked = delegate { };

        /// <summary>
        /// Fired after any selection change (click, drag, clear).
        /// </summary>
        public event Action OnSelectionChanged = delegate { };

        /// <summary>
        /// Delegate for computing the set of data indices inside a content-space rectangle.
        /// Used during drag selection to determine which items overlap the rectangle.
        /// </summary>
        public Func<Rect, HashSet<int>> HitTestRect { get; set; }

        /// <param name="target">VisualElement to register pointer events on (contentViewport).</param>
        /// <param name="scrollView">ScrollView for scroll offset reference.</param>
        /// <param name="hitTest">Maps content-space (x, y) → data index (-1 if none).</param>
        /// <param name="selectionRect">VisualElement to display the drag rectangle.</param>
        public DragSelectionHandler(
            VisualElement target,
            ScrollView scrollView,
            Func<float, float, int> hitTest,
            VisualElement selectionRect)
        {
            _target = target;
            _scrollView = scrollView;
            _hitTest = hitTest;
            _selectionRect = selectionRect;

            _target.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _target.RegisterCallback<PointerUpEvent>(OnPointerUp);
            _target.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
        }

        /// <summary>
        /// Clears the current selection and fires OnSelectionChanged.
        /// </summary>
        public void ClearSelection()
        {
            _selectedIndices.Clear();
            _lastClickedIndex = -1;
            OnSelectionChanged?.Invoke();
        }

        /// <summary>
        /// Selects a single item by data index and fires OnSelectionChanged.
        /// </summary>
        public void SelectSingle(int dataIndex)
        {
            _selectedIndices.Clear();
            if (dataIndex >= 0)
            {
                _selectedIndices.Add(dataIndex);
                _lastClickedIndex = dataIndex;
            }
            OnSelectionChanged?.Invoke();
        }

        /// <summary>
        /// Unregisters all pointer events.
        /// </summary>
        public void Detach()
        {
            _target.UnregisterCallback<PointerDownEvent>(OnPointerDown);
            _target.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
            _target.UnregisterCallback<PointerUpEvent>(OnPointerUp);
            _target.UnregisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;

            _isDragging = true;
            _isDragThresholdMet = false;
            _dragPointerId = evt.pointerId;

            var viewportPos = _target.WorldToLocal(evt.position);
            _dragStart = viewportPos;
            _dragCurrent = viewportPos;

            var contentPos = viewportPos + new Vector2(0, _scrollView.scrollOffset.y);
            _pointerDownDataIndex = _hitTest(contentPos.x, contentPos.y);
            _pointerDownClickCount = evt.clickCount;
            _pointerDownModifiers = evt.modifiers;

            bool isCtrl = (evt.modifiers & EventModifiers.Control) != 0
                       || (evt.modifiers & EventModifiers.Command) != 0;

            _preDragSnapshot.Clear();
            if (isCtrl)
            {
                foreach (var idx in _selectedIndices)
                    _preDragSnapshot.Add(idx);
            }
            else if (_pointerDownDataIndex < 0)
            {
                _selectedIndices.Clear();
                OnSelectionChanged?.Invoke();
            }

            _target.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_isDragging || evt.pointerId != _dragPointerId) return;

            var viewportPos = _target.WorldToLocal(evt.position);
            _dragCurrent = viewportPos;

            var delta = _dragCurrent - _dragStart;
            if (!_isDragThresholdMet)
            {
                if (Mathf.Abs(delta.x) < DRAG_THRESHOLD && Mathf.Abs(delta.y) < DRAG_THRESHOLD)
                    return;
                _isDragThresholdMet = true;
            }

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

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!_isDragging || evt.pointerId != _dragPointerId) return;

            _isDragging = false;
            _selectionRect.style.display = DisplayStyle.None;
            _target.ReleasePointer(evt.pointerId);
            _dragPointerId = -1;

            if (!_isDragThresholdMet)
            {
                int dataIndex = _pointerDownDataIndex;
                if (dataIndex >= 0)
                {
                    if (_pointerDownClickCount >= 2)
                    {
                        OnItemDoubleClicked?.Invoke(dataIndex);
                        evt.StopPropagation();
                        return;
                    }

                    bool isCtrl = (_pointerDownModifiers & EventModifiers.Control) != 0
                               || (_pointerDownModifiers & EventModifiers.Command) != 0;
                    bool isShift = (_pointerDownModifiers & EventModifiers.Shift) != 0;

                    if (isCtrl)
                    {
                        if (_selectedIndices.Contains(dataIndex))
                            _selectedIndices.Remove(dataIndex);
                        else
                            _selectedIndices.Add(dataIndex);
                        _lastClickedIndex = dataIndex;
                    }
                    else if (isShift && _lastClickedIndex >= 0)
                    {
                        int min = Mathf.Min(_lastClickedIndex, dataIndex);
                        int max = Mathf.Max(_lastClickedIndex, dataIndex);
                        _selectedIndices.Clear();
                        for (int i = min; i <= max; i++)
                            _selectedIndices.Add(i);
                    }
                    else
                    {
                        _selectedIndices.Clear();
                        _selectedIndices.Add(dataIndex);
                        _lastClickedIndex = dataIndex;
                    }

                    OnItemClicked?.Invoke(dataIndex);
                }
            }

            OnSelectionChanged?.Invoke();
            evt.StopPropagation();
        }

        private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            if (!_isDragging) return;
            _isDragging = false;
            _selectionRect.style.display = DisplayStyle.None;
            _dragPointerId = -1;
            OnSelectionChanged?.Invoke();
        }

        private void UpdateDragSelection()
        {
            var scrollOffset = _scrollView.scrollOffset;
            float left = Mathf.Min(_dragStart.x, _dragCurrent.x);
            float top = Mathf.Min(_dragStart.y, _dragCurrent.y) + scrollOffset.y;
            float right = Mathf.Max(_dragStart.x, _dragCurrent.x);
            float bottom = Mathf.Max(_dragStart.y, _dragCurrent.y) + scrollOffset.y;

            var dragRect = new Rect(left, top, right - left, bottom - top);

            _selectedIndices.Clear();
            foreach (var idx in _preDragSnapshot)
                _selectedIndices.Add(idx);

            if (HitTestRect != null)
            {
                var hits = HitTestRect(dragRect);
                foreach (var idx in hits)
                    _selectedIndices.Add(idx);
            }

            OnSelectionChanged?.Invoke();
        }
    }
}
