using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IconBrowser.Data;
using IconBrowser.Import;

namespace IconBrowser
{
    /// <summary>
    /// Coordinates icon import/delete operations.
    /// UI concerns (toast, grid refresh) are handled by callers via return values and events.
    /// </summary>
    public class IconOperationService
    {
        private readonly IconDatabase _db;
        private readonly IIconImporter _importer;

        public IconOperationService(IconDatabase db, IIconImporter importer)
        {
            _db = db;
            _importer = importer;
        }

        /// <summary>
        /// Imports a single icon.
        /// </summary>
        /// <returns>True if successful.</returns>
        public async Task<bool> ImportAsync(string prefix, string name)
        {
            var success = await _importer.ImportIconAsync(prefix, name);
            if (success)
                _db.MarkImported(name, prefix);
            return success;
        }

        /// <summary>
        /// Imports multiple icons with a progress bar.
        /// </summary>
        /// <returns>Number of successfully imported icons.</returns>
        public Task<int> BatchImportAsync(
            List<IconEntry> entries,
            Action<int, int> onProgress = null,
            Func<bool> isCancelled = null)
        {
            return RunBatchAsync(
                entries,
                e => !e.IsImported,
                async entry =>
                {
                    var success = await _importer.ImportIconAsync(entry.Prefix, entry.Name);
                    if (success)
                    {
                        entry.IsImported = true;
                        _db.MarkImported(entry.Name, entry.Prefix);
                    }
                    return success;
                },
                isCancelled,
                onProgress);
        }

        /// <summary>
        /// Deletes a single icon by name and prefix.
        /// </summary>
        /// <returns>True if successful.</returns>
        public bool Delete(string name, string prefix)
        {
            if (!_importer.DeleteIcon(name, prefix)) return false;
            _db.MarkDeleted(name);
            return true;
        }

        /// <summary>
        /// Deletes a single icon by asset path.
        /// </summary>
        /// <returns>True if successful.</returns>
        public bool DeleteByPath(string assetPath, string name)
        {
            if (!_importer.DeleteIconByPath(assetPath)) return false;
            _db.MarkDeleted(name);
            return true;
        }

        /// <summary>
        /// Deletes multiple icons with a progress bar.
        /// </summary>
        /// <returns>Number of successfully deleted icons.</returns>
        public int BatchDelete(
            List<IconEntry> entries,
            Action<int, int> onProgress = null,
            Func<bool> isCancelled = null)
        {
            return RunBatchAsync(
                entries,
                e => e.IsImported,
                entry =>
                {
                    bool success = !string.IsNullOrEmpty(entry.LocalAssetPath)
                        ? _importer.DeleteIconByPath(entry.LocalAssetPath)
                        : _importer.DeleteIcon(entry.Name, entry.Prefix);

                    if (success)
                    {
                        entry.IsImported = false;
                        _db.MarkDeleted(entry.Name);
                    }
                    return Task.FromResult(success);
                },
                isCancelled,
                onProgress).Result;
        }

        /// <summary>
        /// Shared batch-processing helper.
        /// Filters items, wraps the loop in BeginBatch/EndBatch, and reports progress.
        /// </summary>
        private async Task<int> RunBatchAsync<T>(
            List<T> items,
            Func<T, bool> filter,
            Func<T, Task<bool>> action,
            Func<bool> isCancelled,
            Action<int, int> onProgress)
        {
            var filtered = items.Where(filter).ToList();
            if (filtered.Count == 0) return 0;

            int count = 0;
            _db.BeginBatch();
            try
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    if (isCancelled?.Invoke() == true) break;
                    onProgress?.Invoke(i + 1, filtered.Count);

                    if (await action(filtered[i]))
                        count++;
                }
            }
            finally
            {
                _db.EndBatch();
            }

            return count;
        }
    }
}
