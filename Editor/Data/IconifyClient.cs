using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace IconBrowser.Data
{
    /// <summary>
    /// HTTP client wrapper for the Iconify API.
    /// Implements IIconifyClient for dependency injection.
    /// Static convenience methods delegate to <see cref="Default"/>.
    /// </summary>
    public class IconifyClient : IIconifyClient
    {
        const string BASE_URL = "https://api.iconify.design";
        const int MAX_RETRIES = 2;

        /// <summary>
        /// Shared default instance for backward compatibility.
        /// </summary>
        public static readonly IconifyClient Default = new();

        #region IIconifyClient (instance methods)

        public async Task<Dictionary<string, IconLibrary>> GetCollectionsAsync(CancellationToken ct = default)
        {
            var json = await FetchAsync($"{BASE_URL}/collections", ct);
            return ParseCollections(json);
        }

        public async Task<CollectionData> GetCollectionAsync(string prefix, CancellationToken ct = default)
        {
            var json = await FetchAsync($"{BASE_URL}/collection?prefix={prefix}&info=true&chars=false", ct);
            return ParseCollection(json, prefix);
        }

        public async Task<SearchResult> SearchAsync(string query, string prefix = "", int limit = 64, CancellationToken ct = default)
        {
            var url = $"{BASE_URL}/search?query={Uri.EscapeDataString(query)}&limit={limit}";
            if (!string.IsNullOrEmpty(prefix))
                url += $"&prefix={prefix}";
            var json = await FetchAsync(url, ct);
            return ParseSearchResult(json);
        }

        public async Task<Dictionary<string, string>> GetIconsBatchAsync(string prefix, string[] names, CancellationToken ct = default)
        {
            if (names.Length == 0) return new Dictionary<string, string>();

            var icons = string.Join(",", names);
            var json = await FetchAsync($"{BASE_URL}/{prefix}.json?icons={icons}", ct);
            return ParseIconsBatch(json, prefix);
        }

        public async Task<string> GetSvgAsync(string prefix, string name, CancellationToken ct = default)
        {
            return await FetchAsync($"{BASE_URL}/{prefix}/{name}.svg", ct);
        }

        #endregion

        #region Static convenience methods (backward compatibility)

        public static Task<Dictionary<string, IconLibrary>> GetCollectionsStaticAsync()
            => Default.GetCollectionsAsync();

        public static Task<CollectionData> GetCollectionStaticAsync(string prefix)
            => Default.GetCollectionAsync(prefix);

        public static Task<SearchResult> SearchStaticAsync(string query, string prefix = "", int limit = 64)
            => Default.SearchAsync(query, prefix, limit);

        public static Task<Dictionary<string, string>> GetIconsBatchStaticAsync(string prefix, string[] names)
            => Default.GetIconsBatchAsync(prefix, names);

        public static Task<string> GetSvgStaticAsync(string prefix, string name)
            => Default.GetSvgAsync(prefix, name);

        #endregion

        #region HTTP

        static async Task<string> FetchAsync(string url, CancellationToken ct = default)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var request = UnityWebRequest.Get(url);
                request.timeout = 15;
                var op = request.SendWebRequest();
                var tcs = new TaskCompletionSource<bool>();
                op.completed += _ => tcs.SetResult(true);

                using (ct.Register(() => { request.Abort(); tcs.TrySetCanceled(); }))
                {
                    await tcs.Task;
                }

                if (request.result == UnityWebRequest.Result.Success)
                    return request.downloadHandler.text;

                lastError = new Exception($"Iconify API error: {request.error} ({url})");
                request.Dispose();

                if (attempt < MAX_RETRIES)
                    await Task.Delay(500 * (attempt + 1), ct);
            }
            throw lastError;
        }

        #endregion

        #region API Response Parsing

        static Dictionary<string, IconLibrary> ParseCollections(string json)
        {
            var result = new Dictionary<string, IconLibrary>();
            var obj = ParseJsonObject(json);
            foreach (var kv in obj)
            {
                var lib = new IconLibrary { Prefix = kv.Key };
                var inner = ParseJsonObject(kv.Value);
                if (inner.TryGetValue("name", out var name)) lib.Name = UnquoteJson(name);
                if (inner.TryGetValue("total", out var total) && int.TryParse(total, out var totalVal))
                    lib.Total = totalVal;
                else
                    continue;
                if (inner.TryGetValue("author", out var author))
                {
                    var authorObj = ParseJsonObject(author);
                    if (authorObj.TryGetValue("name", out var authorName))
                        lib.Author = UnquoteJson(authorName);
                    if (authorObj.TryGetValue("url", out var authorUrl))
                        lib.AuthorUrl = UnquoteJson(authorUrl);
                }
                if (inner.TryGetValue("license", out var license))
                {
                    var licObj = ParseJsonObject(license);
                    if (licObj.TryGetValue("title", out var licTitle))
                        lib.License = UnquoteJson(licTitle);
                    if (licObj.TryGetValue("spdx", out var licSpdx))
                        lib.LicenseSpdx = UnquoteJson(licSpdx);
                }
                if (inner.TryGetValue("category", out var category))
                    lib.Category = UnquoteJson(category);
                if (inner.TryGetValue("palette", out var palette))
                    lib.Palette = palette == "true";
                result[kv.Key] = lib;
            }
            return result;
        }

        static CollectionData ParseCollection(string json, string prefix)
        {
            var data = new CollectionData { Prefix = prefix, IconNames = new List<string>() };
            var obj = ParseJsonObject(json);

            if (obj.TryGetValue("uncategorized", out var uncategorized))
            {
                data.IconNames.AddRange(ParseJsonStringArray(uncategorized));
            }

            if (obj.TryGetValue("categories", out var categories))
            {
                var catObj = ParseJsonObject(categories);
                foreach (var kv in catObj)
                {
                    var names = ParseJsonStringArray(kv.Value);
                    foreach (var n in names)
                    {
                        if (!data.IconNames.Contains(n))
                            data.IconNames.Add(n);
                    }
                }
                data.Categories = new Dictionary<string, List<string>>();
                foreach (var kv in catObj)
                    data.Categories[UnquoteJson(kv.Key)] = ParseJsonStringArray(kv.Value);
            }

            if (obj.TryGetValue("info", out var info))
            {
                var infoObj = ParseJsonObject(info);
                if (infoObj.TryGetValue("total", out var total) && int.TryParse(total, out var t))
                    data.Total = t;
            }

            return data;
        }

        static SearchResult ParseSearchResult(string json)
        {
            var result = new SearchResult { Icons = new List<string>() };
            var obj = ParseJsonObject(json);

            if (obj.TryGetValue("icons", out var icons))
                result.Icons = ParseJsonStringArray(icons);
            if (obj.TryGetValue("total", out var total) && int.TryParse(total, out var t))
                result.Total = t;

            return result;
        }

        static Dictionary<string, string> ParseIconsBatch(string json, string prefix)
        {
            var result = new Dictionary<string, string>();
            var root = ParseJsonObject(json);

            if (!root.TryGetValue("icons", out var iconsJson)) return result;

            int width = 24, height = 24;
            if (root.TryGetValue("width", out var w) && int.TryParse(w, out var wv)) width = wv;
            if (root.TryGetValue("height", out var h) && int.TryParse(h, out var hv)) height = hv;

            var icons = ParseJsonObject(iconsJson);
            foreach (var kv in icons)
            {
                var iconObj = ParseJsonObject(kv.Value);
                if (!iconObj.TryGetValue("body", out var body)) continue;

                var bodyStr = UnquoteJson(body);
                bodyStr = bodyStr.Replace("\\\"", "\"").Replace("\\\\", "\\");
                bodyStr = bodyStr.Replace("currentColor", "#FFFFFF");

                int iw = width, ih = height;
                if (iconObj.TryGetValue("width", out var iws) && int.TryParse(iws, out var iwv)) iw = iwv;
                if (iconObj.TryGetValue("height", out var ihs) && int.TryParse(ihs, out var ihv)) ih = ihv;

                var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{iw}\" height=\"{ih}\" " +
                          $"viewBox=\"0 0 {iw} {ih}\">" +
                          $"{bodyStr}</svg>";
                result[kv.Key] = svg;
            }
            return result;
        }

        #endregion

        #region JSON Delegation

        static Dictionary<string, string> ParseJsonObject(string json) => SimpleJsonParser.ParseJsonObject(json);
        static List<string> ParseJsonStringArray(string json) => SimpleJsonParser.ParseJsonStringArray(json);
        static string UnquoteJson(string s) => SimpleJsonParser.UnquoteJson(s);

        #endregion
    }

    public class CollectionData
    {
        public string Prefix;
        public List<string> IconNames;
        public Dictionary<string, List<string>> Categories;
        public int Total;
    }

    public class SearchResult
    {
        public List<string> Icons;
        public int Total;
    }
}
