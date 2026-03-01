namespace IconBrowser
{
    /// <summary>
    /// Shared constants for the Icon Browser.
    /// </summary>
    internal static class IconBrowserConstants
    {
        // Grid cell dimensions
        public const int CELL_WIDTH = 76;
        public const int CELL_HEIGHT = 84;
        public const int HEADER_HEIGHT = 28;

        // Icon rendering
        public const int ICON_SIZE = 48;

        // Atlas
        public const int ATLAS_SIZE = 2048;
        public const int MAX_BATCH_SIZE = 100;
        public const int MAX_ATLAS_COUNT = 16; // ~64MB limit for LRU eviction

        // Debounce timings (ms)
        public const int SEARCH_DEBOUNCE_MS = 300;
        public const int SCROLL_DEBOUNCE_MS = 100;

        // Drag selection
        public const float DRAG_THRESHOLD = 4f;

        // UI label truncation
        public const int TRUNCATE_LENGTH = 9;

        // HTTP
        public const int MAX_RETRIES = 2;
        public const int REQUEST_TIMEOUT_SECONDS = 15;
    }
}
