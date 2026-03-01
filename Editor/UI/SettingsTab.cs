using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using IconBrowser.Import;

namespace IconBrowser.UI
{
    /// <summary>
    /// Settings tab — import path, filter mode, sample count, and cache management.
    /// </summary>
    public class SettingsTab : VisualElement
    {
        private readonly SvgPreviewCache _previewCache;
        private readonly TextField _pathField;

        /// <summary>
        /// Fired when the import path is changed.
        /// </summary>
        public event Action OnImportPathChanged = delegate { };

        /// <summary>
        /// Fired when the preview cache is cleared.
        /// </summary>
        public event Action OnCacheCleared = delegate { };

        public SettingsTab(SvgPreviewCache previewCache)
        {
            _previewCache = previewCache;

            var root = new ScrollView(ScrollViewMode.Vertical);
            root.AddToClassList("settings-tab");
            Add(root);

            // --- Path Settings ---
            var pathSection = new VisualElement();
            pathSection.AddToClassList("settings-tab__section");
            root.Add(pathSection);

            var pathTitle = new Label("Path Settings");
            pathTitle.AddToClassList("settings-tab__section-title");
            pathSection.Add(pathTitle);

            var pathRow = new VisualElement();
            pathRow.AddToClassList("settings-tab__row");
            pathSection.Add(pathRow);

            var pathLabel = new Label("Import Path");
            pathLabel.AddToClassList("settings-tab__label");
            pathRow.Add(pathLabel);

            _pathField = new TextField();
            _pathField.value = IconBrowserSettings.IconsPath;
            _pathField.isReadOnly = true;
            _pathField.AddToClassList("settings-tab__path-field");
            pathRow.Add(_pathField);

            var changeBtn = new Button(ChangeImportPath) { text = "Change..." };
            changeBtn.AddToClassList("settings-tab__change-btn");
            pathRow.Add(changeBtn);

            // --- Import Settings ---
            var importSection = new VisualElement();
            importSection.AddToClassList("settings-tab__section");
            root.Add(importSection);

            var importTitle = new Label("Import Settings");
            importTitle.AddToClassList("settings-tab__section-title");
            importSection.Add(importTitle);

            // Filter Mode
            var filterRow = new VisualElement();
            filterRow.AddToClassList("settings-tab__row");
            importSection.Add(filterRow);

            var filterLabel = new Label("Filter Mode");
            filterLabel.AddToClassList("settings-tab__label");
            filterRow.Add(filterLabel);

            var filterChoices = new List<string> { "Point", "Bilinear", "Trilinear" };
            var filterField = new PopupField<string>(filterChoices, IconBrowserSettings.FilterMode);
            filterField.RegisterValueChangedCallback(evt =>
            {
                IconBrowserSettings.FilterMode = filterChoices.IndexOf(evt.newValue);
            });
            filterRow.Add(filterField);

            // Sample Count
            var sampleRow = new VisualElement();
            sampleRow.AddToClassList("settings-tab__row");
            importSection.Add(sampleRow);

            var sampleLabel = new Label("Sample Count");
            sampleLabel.AddToClassList("settings-tab__label");
            sampleRow.Add(sampleLabel);

            var sampleChoices = new List<string> { "1", "2", "4", "8" };
            var sampleIndex = sampleChoices.IndexOf(IconBrowserSettings.SampleCount.ToString());
            if (sampleIndex < 0) sampleIndex = 2; // default to "4"
            var sampleField = new PopupField<string>(sampleChoices, sampleIndex);
            sampleField.RegisterValueChangedCallback(evt =>
            {
                if (int.TryParse(evt.newValue, out var val))
                    IconBrowserSettings.SampleCount = val;
            });
            sampleRow.Add(sampleField);

            // --- Cache Settings ---
            var cacheSection = new VisualElement();
            cacheSection.AddToClassList("settings-tab__section");
            root.Add(cacheSection);

            var cacheTitle = new Label("Cache");
            cacheTitle.AddToClassList("settings-tab__section-title");
            cacheSection.Add(cacheTitle);

            var cacheRow = new VisualElement();
            cacheRow.AddToClassList("settings-tab__row");
            cacheSection.Add(cacheRow);

            var cacheLabel = new Label("Preview Atlas Cache");
            cacheLabel.AddToClassList("settings-tab__label");
            cacheRow.Add(cacheLabel);

            var openCacheBtn = new Button(() =>
            {
                var dir = IconAtlas.CacheDir;
                if (System.IO.Directory.Exists(dir))
                    EditorUtility.RevealInFinder(dir);
                else
                    EditorUtility.DisplayDialog("Cache Empty", "No cache folder exists yet.", "OK");
            }) { text = "Open Folder" };
            openCacheBtn.AddToClassList("settings-tab__change-btn");
            cacheRow.Add(openCacheBtn);

            var clearCacheBtn = new Button(() =>
            {
                if (!EditorUtility.DisplayDialog("Clear Cache",
                    "Delete all preview atlas cache files? Previews will be re-downloaded on next browse.",
                    "Clear", "Cancel"))
                    return;

                _previewCache?.Destroy();
                IconAtlas.ClearAllCaches();
                Debug.Log("[IconBrowser] Preview cache cleared.");
                OnCacheCleared?.Invoke();
            }) { text = "Clear Cache" };
            clearCacheBtn.AddToClassList("settings-tab__change-btn");
            cacheRow.Add(clearCacheBtn);
        }

        private void ChangeImportPath()
        {
            var currentPath = IconBrowserSettings.IconsPath;
            var newPath = EditorUtility.OpenFolderPanel("Select Icons Import Folder", currentPath, "");
            if (string.IsNullOrEmpty(newPath)) return;

            // Convert absolute path to Assets-relative path
            var dataPath = Application.dataPath;
            if (newPath.StartsWith(dataPath))
            {
                newPath = "Assets" + newPath.Substring(dataPath.Length);
                IconBrowserSettings.IconsPath = newPath;
                _pathField.value = newPath;
                Debug.Log($"[IconBrowser] Import path set to: {newPath}");
                OnImportPathChanged?.Invoke();
            }
            else
            {
                EditorUtility.DisplayDialog("Invalid Path",
                    "The selected folder must be inside the Assets directory.", "OK");
            }
        }
    }
}
