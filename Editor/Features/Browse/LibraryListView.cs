using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IconBrowser;
using UnityEngine.UIElements;
using UnityEngine;
using IconBrowser.Data;

namespace IconBrowser.UI
{
    /// <summary>
    /// Library list sidebar — featured libraries + "More" toggle.
    /// Reusable component for selecting an icon library from a categorized list.
    /// </summary>
    public class LibraryListView : VisualElement
    {
        public readonly struct LibraryPreviewItem
        {
            public readonly string Name;
            public readonly Sprite Sprite;

            public LibraryPreviewItem(string name, Sprite sprite)
            {
                Name = name;
                Sprite = sprite;
            }
        }

        private const int PREVIEW_ICON_COUNT = 3;
        private const float PREVIEW_OFFSET_X = 8f;
        private const float PREVIEW_OFFSET_Y = -2f;

        private readonly string[] _featuredPrefixes;
        private readonly ScrollView _scroll;
        private readonly VisualElement _featuredList;
        private readonly VisualElement _moreList;
        private readonly Button _moreToggle;
        private bool _isMoreExpanded;
        private VisualElement _activeItem;
        private VisualElement _hoveredItem;
        private string _hoveredPrefix;
        private int _hoverRequestVersion;

        private VisualElement _tooltip;
        private VisualElement _overlayRoot;
        private VisualElement _tooltipIconRow;
        private readonly List<VisualElement> _tooltipIconSlots = new();

        /// <summary>
        /// Fired when a library is selected. Parameter is the library prefix.
        /// </summary>
        public event Action<string> OnLibrarySelected = delegate { };
        public event Action<string> OnLibraryHovered = delegate { };

        /// <summary>
        /// Currently highlighted library prefix.
        /// </summary>
        public string ActivePrefix { get; private set; }

        /// <summary>
        /// Async provider for library preview icons (typically 3 samples).
        /// </summary>
        public Func<string, Task<IReadOnlyList<LibraryPreviewItem>>> ResolveLibraryPreviewsAsync { get; set; }

        public LibraryListView(string[] featuredPrefixes)
        {
            _featuredPrefixes = featuredPrefixes;
            style.height = Length.Percent(100);
            style.flexShrink = 0f;

            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            _scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _scroll.style.flexGrow = 1f;
            _scroll.AddToClassList("library-list");
            Add(_scroll);

            _featuredList = new VisualElement();
            _featuredList.AddToClassList("library-list__section");
            _scroll.Add(_featuredList);

            _moreToggle = new Button(ToggleMore);
            _moreToggle.AddToClassList("library-list__more-toggle");
            _moreToggle.text = "More Libraries \u25b6"; // ▶
            _scroll.Add(_moreToggle);

            _moreList = new VisualElement();
            _moreList.AddToClassList("library-list__section");
            _moreList.style.display = DisplayStyle.None;
            _scroll.Add(_moreList);

            _tooltip = new VisualElement();
            _tooltip.AddToClassList("library-list__preview-tooltip");
            _tooltip.pickingMode = PickingMode.Ignore;
            _tooltip.style.position = Position.Absolute;
            _tooltip.style.display = DisplayStyle.None;

            _tooltipIconRow = new VisualElement();
            _tooltipIconRow.AddToClassList("library-list__preview-icons");
            _tooltip.Add(_tooltipIconRow);

            _tooltipIconSlots.Clear();
            for (int i = 0; i < PREVIEW_ICON_COUNT; i++)
            {
                var iconSlot = new VisualElement();
                iconSlot.AddToClassList("library-list__preview-icon");
                iconSlot.AddToClassList("library-list__preview-icon--empty");
                _tooltipIconRow.Add(iconSlot);
                _tooltipIconSlots.Add(iconSlot);
            }

            RegisterCallback<AttachToPanelEvent>(_ => EnsureTooltipHost());
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                HideTooltip();
                _tooltip?.RemoveFromHierarchy();
                _overlayRoot = null;
            });
        }

        /// <summary>
        /// Populates the library list from loaded library data.
        /// </summary>
        public void SetLibraries(List<IconLibrary> libraries)
        {
            _hoveredItem = null;
            _hoveredPrefix = null;
            _hoverRequestVersion++;
            HideTooltip();

            _featuredList.Clear();
            _moreList.Clear();

            var featuredSet = new HashSet<string>(_featuredPrefixes);

            // Featured libraries — in the curated order
            foreach (var prefix in _featuredPrefixes)
            {
                var lib = libraries.FirstOrDefault(l => l.Prefix == prefix);
                if (lib != null)
                    _featuredList.Add(CreateLibraryItem(lib));
            }

            // More libraries — everything else, sorted by name
            var rest = libraries
                .Where(l => !featuredSet.Contains(l.Prefix))
                .OrderBy(l => l.Name)
                .ToList();

            foreach (var lib in rest)
                _moreList.Add(CreateLibraryItem(lib));

            _isMoreExpanded = true;
            _moreList.style.display = DisplayStyle.Flex;
            _moreToggle.text = $"More Libraries ({rest.Count}) \u25bc";
            _moreToggle.style.display = rest.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Highlights the given library prefix in the list.
        /// </summary>
        public void HighlightLibrary(string prefix)
        {
            _activeItem?.RemoveFromClassList("library-list__item--active");

            _activeItem = FindLibraryItem(_featuredList, prefix)
                       ?? FindLibraryItem(_moreList, prefix);
            _activeItem?.AddToClassList("library-list__item--active");
            ActivePrefix = prefix;
        }

        private VisualElement CreateLibraryItem(IconLibrary lib)
        {
            var item = new Button(() => OnLibrarySelected?.Invoke(lib.Prefix));
            item.AddToClassList("library-list__item");
            item.userData = lib.Prefix;

            var label = new Label(lib.Name);
            label.AddToClassList("library-list__item-name");
            item.Add(label);

            var count = new Label(lib.Total.ToString("N0"));
            count.AddToClassList("library-list__item-count");
            item.Add(count);

            item.RegisterCallback<MouseEnterEvent>(_ => OnItemHoverEnter(item, lib));
            item.RegisterCallback<MouseMoveEvent>(_ => PositionTooltip(item));
            item.RegisterCallback<MouseLeaveEvent>(_ => OnItemHoverLeave(item));

            return item;
        }

        private static VisualElement FindLibraryItem(VisualElement container, string prefix)
        {
            foreach (var child in container.Children())
            {
                if (child.userData is string p && p == prefix)
                    return child;
            }
            return null;
        }

        private void ToggleMore()
        {
            _isMoreExpanded = !_isMoreExpanded;
            _moreList.style.display = _isMoreExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            _moreToggle.text = _isMoreExpanded
                ? $"More Libraries ({_moreList.childCount}) \u25bc"  // ▼
                : $"More Libraries ({_moreList.childCount}) \u25b6"; // ▶
        }

        private void OnItemHoverEnter(VisualElement item, IconLibrary lib)
        {
            OnLibraryHovered?.Invoke(lib.Prefix);

            _hoveredItem = item;
            _hoveredPrefix = lib.Prefix;
            _hoverRequestVersion++;

            EnsureTooltipHost();
            if (_tooltip.parent == null)
                return;

            ClearTooltipIcons();
            _tooltip.style.display = DisplayStyle.Flex;
            _tooltip.BringToFront();
            PositionTooltip(item);

            if (ResolveLibraryPreviewsAsync == null)
                return;

            AsyncHelper.FireAndForget(LoadTooltipPreviewAsync(lib.Prefix, _hoverRequestVersion));
        }

        private void OnItemHoverLeave(VisualElement item)
        {
            if (_hoveredItem != item) return;

            _hoveredItem = null;
            _hoveredPrefix = null;
            _hoverRequestVersion++;
            HideTooltip();
        }

        private async Task LoadTooltipPreviewAsync(string prefix, int requestVersion)
        {
            var previews = await ResolveLibraryPreviewsAsync(prefix);

            if (_tooltip == null) return;
            if (requestVersion != _hoverRequestVersion) return;
            if (_hoveredPrefix != prefix) return;

            if (previews == null || previews.Count == 0) return;

            for (int i = 0; i < PREVIEW_ICON_COUNT; i++)
            {
                var slot = _tooltipIconSlots[i];
                if (i < previews.Count && previews[i].Sprite != null)
                {
                    slot.style.backgroundImage = new StyleBackground(Background.FromSprite(previews[i].Sprite));
                    slot.EnableInClassList("library-list__preview-icon--empty", false);
                    slot.tooltip = string.Empty;
                }
                else
                {
                    slot.style.backgroundImage = StyleKeyword.None;
                    slot.EnableInClassList("library-list__preview-icon--empty", true);
                    slot.tooltip = string.Empty;
                }
            }

        }

        private void PositionTooltip(VisualElement item)
        {
            if (_tooltip == null) return;
            var root = _overlayRoot ?? FindOverlayRoot();
            if (root == null) return;

            var rootBounds = root.worldBound;
            var itemBounds = item.worldBound;

            var tooltipWidth = _tooltip.resolvedStyle.width;
            if (float.IsNaN(tooltipWidth) || tooltipWidth <= 0f) tooltipWidth = 126f;

            var tooltipHeight = _tooltip.resolvedStyle.height;
            if (float.IsNaN(tooltipHeight) || tooltipHeight <= 0f) tooltipHeight = 52f;

            float left = itemBounds.xMax - rootBounds.xMin + PREVIEW_OFFSET_X;
            float top = itemBounds.yMin - rootBounds.yMin + PREVIEW_OFFSET_Y;

            var rightOverflow = left + tooltipWidth - rootBounds.width;
            if (rightOverflow > 0f)
                left = itemBounds.xMin - rootBounds.xMin - tooltipWidth - PREVIEW_OFFSET_X;

            left = Mathf.Clamp(left, 0f, Mathf.Max(0f, rootBounds.width - tooltipWidth - 4f));
            top = Mathf.Clamp(top, 0f, Mathf.Max(0f, rootBounds.height - tooltipHeight - 4f));

            _tooltip.style.left = left;
            _tooltip.style.top = top;
        }

        private void EnsureTooltipHost()
        {
            if (_tooltip == null) return;

            var root = FindOverlayRoot();
            if (root == null)
                return;

            if (_overlayRoot == root && _tooltip.parent == root)
                return;

            _tooltip.RemoveFromHierarchy();
            _overlayRoot = root;
            root.Add(_tooltip);
            _tooltip.BringToFront();
        }

        private VisualElement FindOverlayRoot()
        {
            var current = parent;
            while (current != null)
            {
                if (current.ClassListContains("icon-tab__body"))
                    return current;
                current = current.parent;
            }

            return parent;
        }

        private void HideTooltip()
        {
            if (_tooltip == null) return;
            _tooltip.style.display = DisplayStyle.None;
        }

        private void ClearTooltipIcons()
        {
            foreach (var slot in _tooltipIconSlots)
            {
                slot.style.backgroundImage = StyleKeyword.None;
                slot.EnableInClassList("library-list__preview-icon--empty", true);
                slot.tooltip = string.Empty;
            }
        }

    }
}
