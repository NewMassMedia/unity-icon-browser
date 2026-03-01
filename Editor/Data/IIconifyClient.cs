using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace IconBrowser.Data
{
    /// <summary>
    /// Abstraction for the Iconify REST API client.
    /// Enables dependency injection and test mocking.
    /// </summary>
    public interface IIconifyClient
    {
        Task<Dictionary<string, IconLibrary>> GetCollectionsAsync(CancellationToken ct = default);
        Task<CollectionData> GetCollectionAsync(string prefix, CancellationToken ct = default);
        Task<SearchResult> SearchAsync(string query, string prefix = "", int limit = 64, CancellationToken ct = default);
        Task<Dictionary<string, string>> GetIconsBatchAsync(string prefix, string[] names, CancellationToken ct = default);
        Task<string> GetSvgAsync(string prefix, string name, CancellationToken ct = default);
    }
}
