using System;
using System.Collections.Generic;
using System.Linq;
using IconBrowser.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser.UI
{
    /// <summary>
    /// Right-side detail panel showing selected icon preview, metadata, and actions.
    /// </summary>
    public class IconDetailPanel : VisualElement
    {
        readonly VisualElement _previewIcon;
        readonly Label _nameLabel;
        readonly Label _libraryLabel;
        readonly Label _tagsLabel;
        readonly Label _categoriesLabel;
        readonly VisualElement _variantContainer;
        readonly Label _variantHeader;
        readonly VisualElement _variantStrip;
        readonly Label _codeLabel;
        readonly Button _copyBtn;
        readonly Button _importBtn;
        readonly Button _deleteBtn;
        readonly VisualElement _content;
        readonly Label _placeholder;

        // Multi-selection UI
        readonly VisualElement _multiContent;
        readonly Label _multiCountLabel;
        readonly Button _multiBatchBtn;
        List<IconEntry> _currentMultiSelection;

        IconEntry _currentEntry;
        List<IconEntry> _currentVariants;
        bool _browseMode;

        public IconEntry CurrentEntry => _currentEntry;

        public event Action<IconEntry> OnImportClicked;
        public event Action<IconEntry> OnDeleteClicked;
        public event Action<IconEntry> OnVariantSelected;
        public event Action<List<IconEntry>> OnBatchImportClicked;
        public event Action<List<IconEntry>> OnBatchDeleteClicked;

        public IconDetailPanel()
        {
            AddToClassList("icon-detail");

            _placeholder = new Label("Select an icon to preview");
            _placeholder.AddToClassList("icon-detail__placeholder");
            hierarchy.Add(_placeholder);

            _content = new VisualElement();
            _content.AddToClassList("icon-detail__content");
            _content.style.display = DisplayStyle.None;
            hierarchy.Add(_content);

            // Multi-selection content
            _multiContent = new VisualElement();
            _multiContent.AddToClassList("icon-detail__content");
            _multiContent.style.display = DisplayStyle.None;
            hierarchy.Add(_multiContent);

            _multiCountLabel = new Label();
            _multiCountLabel.AddToClassList("icon-detail__name");
            _multiCountLabel.style.marginTop = 24;
            _multiContent.Add(_multiCountLabel);

            _multiBatchBtn = new Button(() =>
            {
                if (_currentMultiSelection == null || _currentMultiSelection.Count == 0) return;

                // Determine action based on button state
                if (_multiBatchBtn.ClassListContains("icon-detail__btn--delete"))
                    OnBatchDeleteClicked?.Invoke(_currentMultiSelection);
                else
                    OnBatchImportClicked?.Invoke(_currentMultiSelection);
            });
            _multiBatchBtn.AddToClassList("icon-detail__btn");
            _multiContent.Add(_multiBatchBtn);

            // Preview
            _previewIcon = new VisualElement();
            _previewIcon.AddToClassList("icon-detail__preview");
            _content.Add(_previewIcon);

            // Name
            _nameLabel = new Label();
            _nameLabel.AddToClassList("icon-detail__name");
            _content.Add(_nameLabel);

            // Library
            _libraryLabel = new Label();
            _libraryLabel.AddToClassList("icon-detail__meta");
            _content.Add(_libraryLabel);

            // Tags
            _tagsLabel = new Label();
            _tagsLabel.AddToClassList("icon-detail__tags");
            _content.Add(_tagsLabel);

            // Categories
            _categoriesLabel = new Label();
            _categoriesLabel.AddToClassList("icon-detail__tags");
            _content.Add(_categoriesLabel);

            // Variant selector
            _variantContainer = new VisualElement();
            _variantContainer.AddToClassList("icon-detail__variants");
            _variantContainer.style.display = DisplayStyle.None;
            _content.Add(_variantContainer);

            _variantHeader = new Label();
            _variantHeader.AddToClassList("icon-detail__variant-header");
            _variantContainer.Add(_variantHeader);

            _variantStrip = new VisualElement();
            _variantStrip.AddToClassList("icon-detail__variant-strip");
            _variantContainer.Add(_variantStrip);

            // Code snippet
            _codeLabel = new Label();
            _codeLabel.AddToClassList("icon-detail__code");
            _codeLabel.style.whiteSpace = WhiteSpace.Normal;
            _content.Add(_codeLabel);

            // Buttons
            var buttons = new VisualElement();
            buttons.AddToClassList("icon-detail__buttons");
            _content.Add(buttons);

            _copyBtn = new Button(OnCopy) { text = "Copy Code" };
            _copyBtn.AddToClassList("icon-detail__btn");
            buttons.Add(_copyBtn);

            _importBtn = new Button(OnImport) { text = "Import" };
            _importBtn.AddToClassList("icon-detail__btn");
            _importBtn.AddToClassList("icon-detail__btn--import");
            buttons.Add(_importBtn);

            _deleteBtn = new Button(OnDelete) { text = "Delete" };
            _deleteBtn.AddToClassList("icon-detail__btn");
            _deleteBtn.AddToClassList("icon-detail__btn--delete");
            buttons.Add(_deleteBtn);
        }

        /// <summary>
        /// Shows detail for the given icon entry, optionally with variant list.
        /// </summary>
        public void ShowEntry(IconEntry entry, List<IconEntry> variants = null, bool browseMode = false)
        {
            _currentEntry = entry;
            _currentVariants = variants;
            _browseMode = browseMode;
            _multiContent.style.display = DisplayStyle.None;

            if (entry == null)
            {
                _content.style.display = DisplayStyle.None;
                _placeholder.style.display = DisplayStyle.Flex;
                return;
            }

            _content.style.display = DisplayStyle.Flex;
            _placeholder.style.display = DisplayStyle.None;

            // Preview
            if (entry.LocalAsset != null)
                _previewIcon.style.backgroundImage = new StyleBackground(Background.FromVectorImage(entry.LocalAsset));
            else if (entry.PreviewSprite != null)
                _previewIcon.style.backgroundImage = new StyleBackground(Background.FromSprite(entry.PreviewSprite));
            else
                _previewIcon.style.backgroundImage = StyleKeyword.None;

            // Name
            _nameLabel.text = entry.Name;

            // Library
            if (!string.IsNullOrEmpty(entry.Prefix) && entry.Prefix != "unknown")
            {
                _libraryLabel.text = entry.Prefix;
                _libraryLabel.style.display = DisplayStyle.Flex;
            }
            else
            {
                _libraryLabel.style.display = DisplayStyle.None;
            }

            // Tags
            if (entry.Tags != null && entry.Tags.Length > 0)
            {
                _tagsLabel.text = "Tags: " + string.Join(", ", entry.Tags);
                _tagsLabel.style.display = DisplayStyle.Flex;
            }
            else
            {
                _tagsLabel.style.display = DisplayStyle.None;
            }

            // Categories
            if (entry.Categories != null && entry.Categories.Length > 0)
            {
                _categoriesLabel.text = string.Join(", ", entry.Categories);
                _categoriesLabel.style.display = DisplayStyle.Flex;
            }
            else
            {
                _categoriesLabel.style.display = DisplayStyle.None;
            }

            // Variant selector
            if (variants != null && variants.Count > 1)
            {
                _variantContainer.style.display = DisplayStyle.Flex;
                _variantHeader.text = $"Variants ({variants.Count})";
                BuildVariantStrip(entry, variants);
            }
            else
            {
                _variantContainer.style.display = DisplayStyle.None;
            }

            // Code & Copy — hidden in browse mode
            var showCode = !_browseMode && entry.IsImported;
            _codeLabel.text = entry.LoadSnippet;
            _codeLabel.style.display = showCode ? DisplayStyle.Flex : DisplayStyle.None;
            _copyBtn.style.display = showCode ? DisplayStyle.Flex : DisplayStyle.None;

            // Button visibility
            _importBtn.style.display = entry.IsImported ? DisplayStyle.None : DisplayStyle.Flex;
            _deleteBtn.style.display = entry.IsImported ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Shows a multi-selection summary with batch action button.
        /// </summary>
        public void ShowMultiSelection(List<IconEntry> entries)
        {
            _currentMultiSelection = entries;
            _currentEntry = null;
            _content.style.display = DisplayStyle.None;
            _placeholder.style.display = DisplayStyle.None;
            _multiContent.style.display = DisplayStyle.Flex;

            _multiCountLabel.text = $"{entries.Count} icons selected";

            int importedCount = entries.Count(e => e.IsImported);
            int notImportedCount = entries.Count - importedCount;

            if (importedCount == entries.Count)
            {
                // All imported — show Delete button
                _multiBatchBtn.text = $"Delete {entries.Count} Icons";
                _multiBatchBtn.EnableInClassList("icon-detail__btn--delete", true);
                _multiBatchBtn.EnableInClassList("icon-detail__btn--import", false);
            }
            else if (notImportedCount == entries.Count)
            {
                // None imported — show Import button
                _multiBatchBtn.text = $"Import {entries.Count} Icons";
                _multiBatchBtn.EnableInClassList("icon-detail__btn--import", true);
                _multiBatchBtn.EnableInClassList("icon-detail__btn--delete", false);
            }
            else
            {
                // Mixed — show Import for non-imported only
                _multiBatchBtn.text = $"Import {notImportedCount} Icons";
                _multiBatchBtn.EnableInClassList("icon-detail__btn--import", true);
                _multiBatchBtn.EnableInClassList("icon-detail__btn--delete", false);
            }
        }

        void BuildVariantStrip(IconEntry selected, List<IconEntry> variants)
        {
            _variantStrip.Clear();
            foreach (var variant in variants)
            {
                var btn = new VisualElement();
                btn.AddToClassList("icon-detail__variant-btn");
                if (variant == selected)
                    btn.AddToClassList("icon-detail__variant-btn--active");

                var icon = new VisualElement();
                icon.AddToClassList("icon-detail__variant-icon");
                if (variant.LocalAsset != null)
                    icon.style.backgroundImage = new StyleBackground(Background.FromVectorImage(variant.LocalAsset));
                else if (variant.PreviewSprite != null)
                    icon.style.backgroundImage = new StyleBackground(Background.FromSprite(variant.PreviewSprite));
                btn.Add(icon);

                var label = new Label(string.IsNullOrEmpty(variant.VariantLabel) ? "default" : variant.VariantLabel);
                label.AddToClassList("icon-detail__variant-label");
                btn.Add(label);

                var captured = variant;
                btn.RegisterCallback<ClickEvent>(_ =>
                {
                    if (captured != _currentEntry)
                        OnVariantSelected?.Invoke(captured);
                });

                _variantStrip.Add(btn);
            }
        }

        /// <summary>
        /// Clears the panel to placeholder state.
        /// </summary>
        public new void Clear()
        {
            _multiContent.style.display = DisplayStyle.None;
            ShowEntry(null);
        }

        void OnCopy()
        {
            if (_currentEntry == null) return;
            EditorGUIUtility.systemCopyBuffer = _currentEntry.LoadSnippet;
            Debug.Log($"[IconBrowser] Copied: {_currentEntry.LoadSnippet}");
        }

        void OnImport()
        {
            if (_currentEntry == null) return;
            OnImportClicked?.Invoke(_currentEntry);
        }

        void OnDelete()
        {
            if (_currentEntry == null) return;

            if (EditorUtility.DisplayDialog(
                "Delete Icon",
                $"Are you sure you want to delete '{_currentEntry.Name}.svg'?",
                "Delete", "Cancel"))
            {
                OnDeleteClicked?.Invoke(_currentEntry);
            }
        }
    }
}
