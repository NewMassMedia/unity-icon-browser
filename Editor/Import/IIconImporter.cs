using System.Threading.Tasks;

namespace IconBrowser.Import
{
    /// <summary>
    /// Abstraction for icon import/delete operations.
    /// Enables dependency injection and test mocking.
    /// </summary>
    public interface IIconImporter
    {
        Task<bool> ImportIconAsync(string prefix, string name);
        string ConvertForUnity(string svg);
        bool DeleteIcon(string name, string prefix);
        bool DeleteIconByPath(string assetPath);
    }
}
