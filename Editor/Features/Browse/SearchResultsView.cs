using System;
using System.Collections.Generic;
using IconBrowser;
using IconBrowser.Data;
using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser.UI
{
    internal sealed class SearchResultsView : VisualElement
    {
        private readonly ScrollView _scrollView;
        private readonly VisualElement _content;
        private readonly Label _emptyLabel;
        private readonly List<CellBinding> _cellBindings = new();
        private IconEntry _selectedEntry;

        public event Action<IconEntry> OnIconSelected = delegate { };
        public event Action<IconEntry> OnQuickImportClicked = delegate { };
        public event Action<IconEntry> OnQuickDeleteClicked = delegate { };

        public SearchResultsView()
        {
            AddToClassList("icon-grid");
            style.flexGrow = 1f;

            _scrollView = new ScrollView(ScrollViewMode.Vertical);
            _scrollView.AddToClassList("icon-grid__scroll");
            _scrollView.style.flexGrow = 1f;
            hierarchy.Add(_scrollView);

            _content = new VisualElement();
            _content.style.flexDirection = FlexDirection.Column;
            _content.style.paddingBottom = 12f;
            _scrollView.Add(_content);

            _emptyLabel = new Label("No icons found");
            _emptyLabel.AddToClassList("icon-grid__empty");
            _emptyLabel.style.display = DisplayStyle.None;
            hierarchy.Add(_emptyLabel);
        }

        public void SetSections(IReadOnlyList<SearchSection> sections)
        {
            var hadVisibleEntries = _content.childCount > 0;
            var previousSelection = _selectedEntry;
            _selectedEntry = null;
            _cellBindings.Clear();
            _content.Clear();

            var hasEntries = false;
            if (sections != null)
            {
                foreach (var section in sections)
                {
                    if (section == null || section.Entries == null || section.Entries.Count == 0)
                        continue;

                    hasEntries = true;
                    _content.Add(CreateSection(section));
                }
            }

            if (previousSelection != null)
            {
                foreach (var binding in _cellBindings)
                {
                    if (!ReferenceEquals(binding.Entry, previousSelection))
                        continue;

                    _selectedEntry = binding.Entry;
                    break;
                }
            }

            _emptyLabel.style.display = hasEntries ? DisplayStyle.None : DisplayStyle.Flex;
            _scrollView.style.display = hasEntries ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasEntries && !hadVisibleEntries)
                _scrollView.scrollOffset = Vector2.zero;

            RefreshItems();
        }

        public void ClearSelection()
        {
            _selectedEntry = null;
            RefreshItems();
        }

        public void RefreshItems()
        {
            foreach (var binding in _cellBindings)
                BindCell(binding);
        }

        private VisualElement CreateSection(SearchSection section)
        {
            var sectionRoot = new VisualElement();
            sectionRoot.style.flexDirection = FlexDirection.Column;
            sectionRoot.style.marginBottom = 16f;

            var header = new VisualElement();
            header.AddToClassList("icon-grid__header");
            header.style.position = Position.Relative;
            header.style.height = IconBrowserConstants.HEADER_HEIGHT;
            header.style.width = Length.Percent(100);
            sectionRoot.Add(header);

            var headerLabel = new Label(section.DisplayName);
            header.Add(headerLabel);

            var cells = new VisualElement();
            cells.style.flexDirection = FlexDirection.Row;
            cells.style.flexWrap = Wrap.Wrap;
            cells.style.alignContent = Align.FlexStart;
            sectionRoot.Add(cells);

            foreach (var entry in section.Entries)
            {
                if (entry == null)
                    continue;

                var cell = CreateCell(entry);
                cells.Add(cell.Root);
                _cellBindings.Add(cell);
            }

            return sectionRoot;
        }

        private CellBinding CreateCell(IconEntry entry)
        {
            var root = new VisualElement();
            root.AddToClassList("icon-grid__cell");
            root.style.position = Position.Relative;
            root.style.width = IconBrowserConstants.CELL_WIDTH;
            root.style.height = IconBrowserConstants.CELL_HEIGHT;
            root.style.marginBottom = 4f;
            root.userData = entry;
            root.AddManipulator(new Clickable(() => HandleCellClicked(entry)));

            var icon = new VisualElement { name = "cell-icon" };
            icon.AddToClassList("icon-grid__cell-icon");
            icon.style.width = IconBrowserConstants.ICON_DISPLAY_SIZE;
            icon.style.height = IconBrowserConstants.ICON_DISPLAY_SIZE;
            root.Add(icon);

            var check = new VisualElement { name = "cell-check" };
            check.AddToClassList("icon-grid__cell-check");
            root.Add(check);

            var badge = new Label { name = "cell-badge" };
            badge.AddToClassList("icon-grid__cell-badge");
            root.Add(badge);

            var label = new Label();
            label.AddToClassList("icon-grid__cell-label");
            root.Add(label);

            var actionButton = new Button(() =>
            {
                if (entry.IsImported)
                    OnQuickDeleteClicked(entry);
                else
                    OnQuickImportClicked(entry);
            })
            {
                name = "cell-action-btn"
            };
            actionButton.AddToClassList("icon-grid__cell-action-btn");
            root.Add(actionButton);

            return new CellBinding(entry, root, icon, check, badge, label, actionButton);
        }

        private void HandleCellClicked(IconEntry entry)
        {
            _selectedEntry = entry;
            OnIconSelected(entry);
            RefreshItems();
        }

        private void BindCell(CellBinding binding)
        {
            binding.Icon.style.backgroundImage = IconGrid.GetIconBackground(binding.Entry);
            binding.Check.style.display = binding.Entry.IsImported && binding.Entry.LocalAsset == null
                ? DisplayStyle.Flex
                : DisplayStyle.None;

            if (binding.Entry.VariantCount > 1)
            {
                binding.Badge.text = binding.Entry.VariantCount.ToString();
                binding.Badge.style.display = DisplayStyle.Flex;
            }
            else
            {
                binding.Badge.style.display = DisplayStyle.None;
            }

            binding.Label.text = IconGrid.TruncateName(binding.Entry.Name);
            binding.Label.tooltip = binding.Entry.Name;

            var isSelected = ReferenceEquals(binding.Entry, _selectedEntry);
            binding.Root.EnableInClassList("icon-grid__cell--selected", isSelected);

            binding.ActionButton.text = binding.Entry.IsImported ? "Delete" : "Import";
            binding.ActionButton.EnableInClassList("icon-grid__cell-action-btn--delete", binding.Entry.IsImported);
            binding.ActionButton.EnableInClassList("icon-grid__cell-action-btn--import", !binding.Entry.IsImported);
            binding.ActionButton.style.display = isSelected ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private sealed class CellBinding
        {
            public CellBinding(
                IconEntry entry,
                VisualElement root,
                VisualElement icon,
                VisualElement check,
                Label badge,
                Label label,
                Button actionButton)
            {
                Entry = entry;
                Root = root;
                Icon = icon;
                Check = check;
                Badge = badge;
                Label = label;
                ActionButton = actionButton;
            }

            public IconEntry Entry { get; }
            public VisualElement Root { get; }
            public VisualElement Icon { get; }
            public VisualElement Check { get; }
            public Label Badge { get; }
            public Label Label { get; }
            public Button ActionButton { get; }
        }
    }
}
