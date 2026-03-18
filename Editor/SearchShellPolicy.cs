namespace IconBrowser
{
    internal sealed class SearchShellPolicy
    {
        private const int ProjectTab = 0;
        private const int SettingsTab = 2;

        public bool IsSearchVisible(int activeTab) => activeTab != SettingsTab;

        public bool ShouldResetSearchTextOnTabSwitch(int previousTab, int nextTab) => previousTab != nextTab;

        public bool ShouldClearSearchTextWithoutDispatch(int activeTab) => !IsSearchVisible(activeTab);

        public bool ShouldSyncSearchTargetOnTabSwitch(int previousTab, int nextTab)
        {
            return !IsSearchVisible(previousTab) && IsSearchVisible(nextTab);
        }

        public IIconBrowserSearchTarget ResolveTargetForInputChanged(
            int activeTab,
            IIconBrowserSearchTarget projectTarget,
            IIconBrowserSearchTarget browseTarget)
        {
            if (activeTab == SettingsTab)
                return null;

            return activeTab == ProjectTab ? projectTarget : browseTarget;
        }

        public bool ShouldDispatchOnInputChanged(IIconBrowserSearchTarget target, string query)
        {
            if (target == null)
                return false;

            switch (target.DispatchMode)
            {
                case SearchDispatchMode.Immediate:
                    return true;
                case SearchDispatchMode.Deferred:
                    return string.IsNullOrWhiteSpace(query);
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(target), target.DispatchMode, "Unknown search dispatch mode.");
            }
        }

        public bool ShouldDispatchOnSubmit(IIconBrowserSearchTarget target, string query)
        {
            if (target == null)
                return false;

            switch (target.DispatchMode)
            {
                case SearchDispatchMode.Immediate:
                    return false;
                case SearchDispatchMode.Deferred:
                    return !string.IsNullOrWhiteSpace(query);
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(target), target.DispatchMode, "Unknown search dispatch mode.");
            }
        }
    }
}
