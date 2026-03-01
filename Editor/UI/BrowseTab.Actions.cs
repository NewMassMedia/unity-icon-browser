using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using IconBrowser.Data;
using IconBrowser.Import;

namespace IconBrowser.UI
{
    /// <summary>
    /// BrowseTab partial — import/delete operations, variant selection, and preview loading.
    /// </summary>
    internal partial class BrowseTab
    {
        // ──────────────────────────────────────────
        //  Import operations
        // ──────────────────────────────────────────

        private void OnImport(IconEntry entry)
        {
            AsyncHelper.FireAndForget(OnImportAsync(entry));
        }

        private async Task OnImportAsync(IconEntry entry)
        {
            if (_cts.Token.IsCancellationRequested) return;
            EditorUtility.DisplayProgressBar("Importing Icon", $"Importing {entry.Name}...", 0.5f);
            try
            {
                var success = await _ops.ImportAsync(entry.Prefix, entry.Name);
                if (_cts.Token.IsCancellationRequested) return;
                if (success)
                {
                    entry.IsImported = true;
                    _detail.ShowEntry(entry, FindVariants(entry.Name), browseMode: true);
                    _grid.RefreshPreviews();
                    OnIconImported?.Invoke();
                    _toast?.ShowInfo($"Imported {entry.Name}");
                }
                else
                {
                    _toast?.ShowError($"Failed to import {entry.Name}");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void OnBatchImport(List<IconEntry> entries)
        {
            AsyncHelper.FireAndForget(OnBatchImportAsync(entries));
        }

        private async Task OnBatchImportAsync(List<IconEntry> entries)
        {
            var token = _cts.Token;
            int count = await EditorProgressHelper.RunWithProgressAsync(
                "Importing Icons",
                "Importing... ({0}/{1})",
                (onProgress, isCancelled) => _ops.BatchImportAsync(entries, onProgress, isCancelled),
                () => token.IsCancellationRequested);

            _grid.RefreshPreviews();
            OnIconImported?.Invoke();
            if (count > 0)
                _toast?.ShowInfo($"Imported {count} icon(s)");
        }

        // ──────────────────────────────────────────
        //  Delete operations
        // ──────────────────────────────────────────

        private void OnQuickDelete(IconEntry entry)
        {
            if (_ops.Delete(entry.Name, entry.Prefix))
            {
                entry.IsImported = false;
                _detail.ShowEntry(entry, FindVariants(entry.Name), browseMode: true);
                _grid.RefreshPreviews();
                OnIconImported?.Invoke();
                _toast?.ShowError($"Deleted {entry.Name}");
            }
        }

        private void OnBatchDelete(List<IconEntry> entries)
        {
            var toDelete = entries.Where(e => e.IsImported).ToList();
            if (toDelete.Count == 0) return;

            if (!EditorUtility.DisplayDialog(
                "Delete Icons",
                $"Are you sure you want to delete {toDelete.Count} icon(s)?",
                "Delete", "Cancel"))
                return;

            int deletedCount = _ops.BatchDelete(entries);
            if (deletedCount > 0)
            {
                _grid.RefreshPreviews();
                OnIconImported?.Invoke();
                _toast?.ShowError($"Deleted {deletedCount} icon(s)");
            }
        }

        // ──────────────────────────────────────────
        //  Variant selection
        // ──────────────────────────────────────────

        private void OnVariantSelected(IconEntry variant)
        {
            AsyncHelper.FireAndForget(OnVariantSelectedAsync(variant));
        }

        private async Task OnVariantSelectedAsync(IconEntry variant)
        {
            var variants = FindVariants(variant.Name);

            if (variant.PreviewSprite == null && variant.LocalAsset == null)
            {
                var cached = _previewCache.GetPreview(variant.Prefix, variant.Name);
                if (cached != null)
                {
                    variant.PreviewSprite = cached;
                }
                else
                {
                    await _previewCache.LoadPreviewBatchAsync(_dc.CurrentPrefix, new List<string> { variant.Name }, () =>
                    {
                        var preview = _previewCache.GetPreview(variant.Prefix, variant.Name);
                        if (preview != null) variant.PreviewSprite = preview;
                        _detail.ShowEntry(variant, variants, browseMode: true);
                    });
                    return;
                }
            }

            _detail.ShowEntry(variant, variants, browseMode: true);
        }

        // ──────────────────────────────────────────
        //  Preview loading / visible range
        // ──────────────────────────────────────────

        private async Task PreloadVariantPreviewsAsync(List<IconEntry> variants)
        {
            var toLoad = new List<string>();
            foreach (var v in variants)
            {
                if (v.PreviewSprite != null || v.LocalAsset != null) continue;
                var cached = _previewCache.GetPreview(v.Prefix, v.Name);
                if (cached != null)
                {
                    v.PreviewSprite = cached;
                    continue;
                }
                toLoad.Add(v.Name);
            }
            if (toLoad.Count == 0) return;

            await _previewCache.LoadPreviewBatchAsync(_dc.CurrentPrefix, toLoad, () =>
            {
                foreach (var v in variants)
                {
                    var p = _previewCache.GetPreview(v.Prefix, v.Name);
                    if (p != null) v.PreviewSprite = p;
                }
                // Re-show detail with loaded previews
                if (_detail != null && variants.Count > 0)
                {
                    var current = variants.FirstOrDefault(v => v.PreviewSprite != null) ?? variants[0];
                    _detail.ShowEntry(_detail.CurrentEntry, variants, browseMode: true);
                }
            });
        }

        /// <summary>
        /// Debounced scroll handler — waits before loading previews.
        /// </summary>
        private void OnVisibleRangeChangedDebounced(int first, int last)
        {
            _scrollDebounceHandle?.Pause();
            if (_shouldImmediateLoad)
            {
                _shouldImmediateLoad = false;
                OnVisibleRangeChanged(first, last);
            }
            else
            {
                _scrollDebounceHandle = schedule.Execute(() => OnVisibleRangeChanged(first, last)).StartingIn(IconBrowserConstants.SCROLL_DEBOUNCE_MS);
            }
        }

        private void OnVisibleRangeChanged(int first, int last)
        {
            if (_cts.Token.IsCancellationRequested) return;
            var groupedEntries = _dc.GroupedEntries;
            if (groupedEntries.Count == 0) return;

            // Prefetch margin: load 2 extra rows above and below the visible range
            int margin = _grid.Columns * 2;
            first = Mathf.Clamp(first - margin, 0, groupedEntries.Count - 1);
            last = Mathf.Clamp(last + margin, 0, groupedEntries.Count - 1);

            // Collect names that need previews — include variant siblings
            var nameSet = new HashSet<string>();
            var allVariantEntries = new List<IconEntry>();
            for (int i = first; i <= last; i++)
            {
                var entry = groupedEntries[i];
                // Bug fix: only skip imported icons that already have a local asset loaded
                if (entry.IsImported && entry.LocalAsset != null) continue;
                var cached = _previewCache.GetPreview(entry.Prefix, entry.Name);
                if (cached != null)
                {
                    entry.PreviewSprite = cached;
                }
                else
                {
                    nameSet.Add(entry.Name);
                }

                // Also include all variant siblings for this group
                var (baseName, _) = VariantGrouper.ParseVariant(entry.Name, _dc.CurrentPrefix);
                if (_dc.VariantMap.TryGetValue(baseName, out var variants))
                {
                    foreach (var v in variants)
                    {
                        allVariantEntries.Add(v);
                        if (v.PreviewSprite != null || (v.IsImported && v.LocalAsset != null)) continue;
                        var vc = _previewCache.GetPreview(v.Prefix, v.Name);
                        if (vc != null) { v.PreviewSprite = vc; continue; }
                        nameSet.Add(v.Name);
                    }
                }
            }

            // Refresh cells that just got their preview from cache
            _grid.RefreshPreviews();

            if (nameSet.Count == 0) return;

            var names = new List<string>(nameSet);

            // Load previews asynchronously
            AsyncHelper.FireAndForget(_previewCache.LoadPreviewBatchAsync(_dc.CurrentPrefix, names, () =>
            {
                // Update representative entries
                for (int i = first; i <= last && i < groupedEntries.Count; i++)
                {
                    var entry = groupedEntries[i];
                    var preview = _previewCache.GetPreview(entry.Prefix, entry.Name);
                    if (preview != null) entry.PreviewSprite = preview;
                }
                // Update variant entries
                foreach (var v in allVariantEntries)
                {
                    var preview = _previewCache.GetPreview(v.Prefix, v.Name);
                    if (preview != null) v.PreviewSprite = preview;
                }
                _grid.RefreshPreviews();
            }));
        }
    }
}
