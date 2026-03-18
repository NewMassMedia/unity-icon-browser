using System;
using System.Collections.Generic;
using System.Linq;
using IconBrowser.Data;

namespace IconBrowser.UI
{
    internal static class SearchSectionBuilder
    {
        public static List<SearchSection> Build(
            IEnumerable<IconEntry> entries,
            IReadOnlyList<string> sidebarPrefixOrder,
            IReadOnlyDictionary<string, string> prefixDisplayNames = null)
        {
            if (entries == null)
                throw new ArgumentNullException(nameof(entries));

            sidebarPrefixOrder ??= Array.Empty<string>();

            var orderLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < sidebarPrefixOrder.Count; index++)
            {
                var prefix = sidebarPrefixOrder[index];
                if (!string.IsNullOrWhiteSpace(prefix) && !orderLookup.ContainsKey(prefix))
                    orderLookup[prefix] = index;
            }

            return entries
                .GroupBy(entry => entry?.Prefix ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SearchSection(
                    group.Key,
                    ResolveDisplayName(group.Key, prefixDisplayNames),
                    group.Where(entry => entry != null).ToList()))
                .OrderBy(section => ResolveOrder(section.Prefix, orderLookup))
                .ThenBy(section => section.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ResolveDisplayName(string prefix, IReadOnlyDictionary<string, string> prefixDisplayNames)
        {
            if (prefixDisplayNames != null && prefixDisplayNames.TryGetValue(prefix, out var displayName) && !string.IsNullOrWhiteSpace(displayName))
                return displayName;

            return prefix ?? string.Empty;
        }

        private static int ResolveOrder(string prefix, IReadOnlyDictionary<string, int> orderLookup)
        {
            return orderLookup.TryGetValue(prefix ?? string.Empty, out var order) ? order : int.MaxValue;
        }
    }
}
