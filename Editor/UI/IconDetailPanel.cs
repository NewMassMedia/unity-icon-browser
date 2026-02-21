using System;
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
        readonly Label _tagsLabel;
        readonly Label _categoriesLabel;
        readonly Label _codeLabel;
        readonly Button _copyBtn;
        readonly Button _importBtn;
        readonly Button _deleteBtn;
        readonly VisualElement _content;
        readonly Label _placeholder;

        IconEntry _currentEntry;

        public event Action<IconEntry> OnImportClicked;
        public event Action<IconEntry> OnDeleteClicked;

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

            // Preview
            _previewIcon = new VisualElement();
            _previewIcon.AddToClassList("icon-detail__preview");
            _content.Add(_previewIcon);

            // Name
            _nameLabel = new Label();
            _nameLabel.AddToClassList("icon-detail__name");
            _content.Add(_nameLabel);

            // Tags
            _tagsLabel = new Label();
            _tagsLabel.AddToClassList("icon-detail__tags");
            _content.Add(_tagsLabel);

            // Categories
            _categoriesLabel = new Label();
            _categoriesLabel.AddToClassList("icon-detail__tags");
            _content.Add(_categoriesLabel);

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
        /// Shows detail for the given icon entry.
        /// </summary>
        public void ShowEntry(IconEntry entry)
        {
            _currentEntry = entry;

            if (entry == null)
            {
                _content.style.display = DisplayStyle.None;
                _placeholder.style.display = DisplayStyle.Flex;
                return;
            }

            _content.style.display = DisplayStyle.Flex;
            _placeholder.style.display = DisplayStyle.None;

            // Preview
            var image = entry.LocalAsset ?? entry.PreviewAsset;
            if (image != null)
                _previewIcon.style.backgroundImage = new StyleBackground(Background.FromVectorImage(image));
            else
                _previewIcon.style.backgroundImage = StyleKeyword.None;

            // Name
            _nameLabel.text = entry.Name;

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

            // Code
            _codeLabel.text = entry.LoadSnippet;

            // Button visibility
            _importBtn.style.display = entry.IsImported ? DisplayStyle.None : DisplayStyle.Flex;
            _deleteBtn.style.display = entry.IsImported ? DisplayStyle.Flex : DisplayStyle.None;
            _copyBtn.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Clears the panel to placeholder state.
        /// </summary>
        public new void Clear()
        {
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
