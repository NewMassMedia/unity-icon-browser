namespace IconBrowser.UI
{
    internal sealed class SearchSession
    {
        public string DraftQuery { get; private set; } = string.Empty;
        public string CommittedQuery { get; private set; } = string.Empty;
        public bool IsGlobalSearchMode { get; private set; }
        public string LastBrowsePrefix { get; private set; }
        public int RequestVersion { get; private set; }

        public SearchSession(string initialBrowsePrefix)
        {
            LastBrowsePrefix = NormalizePrefix(initialBrowsePrefix);
        }

        public void UpdateDraftQuery(string query)
        {
            DraftQuery = query ?? string.Empty;
        }

        public bool TryCommitGlobalSearch(string currentBrowsePrefix)
        {
            var normalizedQuery = NormalizeQuery(DraftQuery);
            if (normalizedQuery.Length == 0)
                return false;

            LastBrowsePrefix = NormalizePrefix(currentBrowsePrefix, LastBrowsePrefix);
            DraftQuery = normalizedQuery;
            CommittedQuery = normalizedQuery;
            IsGlobalSearchMode = true;
            RequestVersion++;
            return true;
        }

        public int BeginBrowseRequest(string browsePrefix)
        {
            LastBrowsePrefix = NormalizePrefix(browsePrefix, LastBrowsePrefix);
            DraftQuery = string.Empty;
            CommittedQuery = string.Empty;
            IsGlobalSearchMode = false;
            RequestVersion++;
            return RequestVersion;
        }

        public bool TryReturnToBrowse(string browsePrefix)
        {
            var hadSearchState = IsGlobalSearchMode
                || !string.IsNullOrWhiteSpace(CommittedQuery)
                || !string.IsNullOrWhiteSpace(DraftQuery);

            LastBrowsePrefix = NormalizePrefix(browsePrefix, LastBrowsePrefix);
            DraftQuery = string.Empty;
            CommittedQuery = string.Empty;
            IsGlobalSearchMode = false;

            if (!hadSearchState)
                return false;

            RequestVersion++;
            return true;
        }

        public bool IsCurrentRequest(int requestVersion)
        {
            return requestVersion == RequestVersion;
        }

        private static string NormalizeQuery(string query)
        {
            return (query ?? string.Empty).Trim();
        }

        private static string NormalizePrefix(string prefix, string fallback = "")
        {
            return string.IsNullOrWhiteSpace(prefix) ? (fallback ?? string.Empty) : prefix;
        }
    }
}
