namespace IconBrowser
{
    internal enum SearchDispatchMode
    {
        Immediate,
        Deferred
    }

    internal interface IIconBrowserSearchTarget
    {
        SearchDispatchMode DispatchMode { get; }
        void Search(string query);
    }
}
