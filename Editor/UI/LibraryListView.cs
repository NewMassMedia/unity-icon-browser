using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;
using IconBrowser.Data;

namespace IconBrowser.UI
{
    /// <summary>
    /// Library list sidebar — featured libraries + "More" toggle.
    /// Reusable component for selecting an icon library from a categorized list.
    /// </summary>
    public class LibraryListView : VisualElement
    {
        private readonly string[] _featuredPrefixes;
        private readonly ScrollView _scroll;
        private readonly VisualElement _featuredList;
        private readonly VisualElement _moreList;
        private readonly Button _moreToggle;
        private bool _isMoreExpanded;
        private VisualElement _activeItem;

        /// <summary>
        /// Fired when a library is selected. Parameter is the library prefix.
        /// </summary>
        public event Action<string> OnLibrarySelected = delegate { };

        /// <summary>
        /// Currently highlighted library prefix.
        /// </summary>
        public string ActivePrefix { get; private set; }

        public LibraryListView(string[] featuredPrefixes)
        {
            _featuredPrefixes = featuredPrefixes;

            _scroll = new ScrollView(ScrollViewMode.Vertical);
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
        }

        /// <summary>
        /// Populates the library list from loaded library data.
        /// </summary>
        public void SetLibraries(List<IconLibrary> libraries)
        {
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

            _moreToggle.text = $"More Libraries ({rest.Count}) \u25b6";
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
    }
}
