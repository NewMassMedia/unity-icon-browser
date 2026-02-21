namespace IconBrowser.Data
{
    /// <summary>
    /// Metadata for an icon library (collection) from Iconify API.
    /// </summary>
    public class IconLibrary
    {
        public string Prefix { get; set; }
        public string Name { get; set; }
        public int Total { get; set; }
        public string Author { get; set; }
        public string AuthorUrl { get; set; }
        public string License { get; set; }
        public string LicenseSpdx { get; set; }
        public string Category { get; set; }
        public bool Palette { get; set; }

        public string DisplayName => $"{Name} ({Total})";

        public override string ToString() => DisplayName;
    }
}
