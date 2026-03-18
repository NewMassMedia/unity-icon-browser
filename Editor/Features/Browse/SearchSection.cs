using System;
using System.Collections.Generic;
using IconBrowser.Data;

namespace IconBrowser.UI
{
    internal sealed class SearchSection
    {
        public string Prefix { get; }
        public string DisplayName { get; }
        public IReadOnlyList<IconEntry> Entries { get; }

        public SearchSection(string prefix, string displayName, IReadOnlyList<IconEntry> entries)
        {
            Prefix = prefix ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Prefix : displayName;
            Entries = entries ?? Array.Empty<IconEntry>();
        }
    }
}
