using System;
using System.Threading.Tasks;
using UnityEditor;

namespace IconBrowser.UI
{
    /// <summary>
    /// Shared helper for wrapping batch operations with EditorUtility progress bar display.
    /// Encapsulates the cancelable-progress + try/finally ClearProgressBar pattern.
    /// </summary>
    internal static class EditorProgressHelper
    {
        /// <summary>
        /// Wraps an async batch operation with a cancelable progress bar.
        /// The progress bar is automatically cleared when the operation completes or is cancelled.
        /// </summary>
        /// <param name="title">Progress bar window title.</param>
        /// <param name="messageFormat">
        /// Format string for the progress message. Use {0} for current index and {1} for total count.
        /// Example: "Importing... ({0}/{1})"
        /// </param>
        /// <param name="operation">
        /// The async batch operation to run. Receives onProgress and isCancelled callbacks.
        /// </param>
        /// <param name="externalCancelCheck">
        /// Optional additional cancellation check (e.g. CancellationToken).
        /// </param>
        public static async Task<int> RunWithProgressAsync(
            string title,
            string messageFormat,
            Func<Action<int, int>, Func<bool>, Task<int>> operation,
            Func<bool> externalCancelCheck = null)
        {
            bool wasCancelled = false;
            try
            {
                return await operation(
                    (cur, total) =>
                    {
                        wasCancelled = EditorUtility.DisplayCancelableProgressBar(
                            title,
                            string.Format(messageFormat, cur, total),
                            (float)cur / total);
                    },
                    () => wasCancelled || (externalCancelCheck?.Invoke() ?? false));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Wraps a synchronous batch operation with a cancelable progress bar.
        /// The progress bar is automatically cleared when the operation completes or is cancelled.
        /// </summary>
        public static int RunWithProgress(
            string title,
            string messageFormat,
            Func<Action<int, int>, Func<bool>, int> operation)
        {
            bool wasCancelled = false;
            try
            {
                return operation(
                    (cur, total) =>
                    {
                        wasCancelled = EditorUtility.DisplayCancelableProgressBar(
                            title,
                            string.Format(messageFormat, cur, total),
                            (float)cur / total);
                    },
                    () => wasCancelled);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
