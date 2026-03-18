using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;

namespace IconBrowser
{
    /// <summary>
    /// Safe fire-and-forget helper that logs unobserved exceptions.
    /// </summary>
    internal static class AsyncHelper
    {
        /// <summary>
        /// Observes a fire-and-forget task, logging any exception to the console.
        /// </summary>
        public static void FireAndForget(Task task, [CallerMemberName] string caller = "")
        {
            task.ContinueWith(
                t => Debug.LogError($"[IconBrowser] {caller}: {t.Exception?.Flatten().InnerException}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
