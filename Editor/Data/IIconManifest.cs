using System.Collections.Generic;

namespace IconBrowser.Data
{
    /// <summary>
    /// Abstraction for icon-to-library mapping (manifest) persistence.
    /// Enables dependency injection and test mocking.
    /// </summary>
    public interface IIconManifest
    {
        string GetPrefix(string name);
        void Set(string name, string prefix);
        void Remove(string name);
        IReadOnlyDictionary<string, string> GetAll();
        HashSet<string> GetPrefixes();
        int ReassignUnknowns(string newPrefix);
        int AddMissing(IEnumerable<string> names, string prefix);
        void Invalidate();
    }
}
