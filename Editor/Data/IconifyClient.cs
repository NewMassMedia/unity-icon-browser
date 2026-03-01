using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using IconBrowser.Import;

namespace IconBrowser.Data
{
    /// <summary>
    /// HTTP client wrapper for the Iconify API.
    /// Implements IIconifyClient for dependency injection.
    /// Use <see cref="Default"/> for the shared singleton instance.
    /// </summary>
    public class IconifyClient : IIconifyClient
    {
        private const string BASE_URL = "https://api.iconify.design";
        private const int MAX_RETRIES = IconBrowserConstants.MAX_RETRIES;

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

        #endregion IIconifyClient (instance methods)

        #region HTTP

        private static async Task<string> FetchAsync(string url, CancellationToken ct = default)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var request = UnityWebRequest.Get(url);
                request.timeout = IconBrowserConstants.REQUEST_TIMEOUT_SECONDS;
                var op = request.SendWebRequest();
                var tcs = new TaskCompletionSource<bool>();
                op.completed += _ => tcs.TrySetResult(true);

                using (ct.Register(() => { request.Abort(); tcs.TrySetCanceled(); }))
                {
                    await tcs.Task;
                }

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var text = request.downloadHandler.text;
                    request.Dispose();
                    return text;
                }

                lastError = new Exception($"Iconify API error: {request.error} ({url})");
                request.Dispose();

                if (attempt < MAX_RETRIES)
                    await Task.Delay(500 * (attempt + 1), ct);
            }
            throw lastError;
        }

        #endregion HTTP

        #region API Response Parsing

        private static Dictionary<string, IconLibrary> ParseCollections(string json)
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

        private static CollectionData ParseCollection(string json, string prefix)
        {
            var data = new CollectionData { Prefix = prefix, IconNames = new List<string>() };
            var obj = ParseJsonObject(json);
            var seen = new HashSet<string>();

            if (obj.TryGetValue("uncategorized", out var uncategorized))
            {
                foreach (var n in ParseJsonStringArray(uncategorized))
                {
                    if (seen.Add(n))
                        data.IconNames.Add(n);
                }
            }

            if (obj.TryGetValue("categories", out var categories))
            {
                var catObj = ParseJsonObject(categories);
                foreach (var kv in catObj)
                {
                    var names = ParseJsonStringArray(kv.Value);
                    foreach (var n in names)
                    {
                        if (seen.Add(n))
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

        private static SearchResult ParseSearchResult(string json)
        {
            var result = new SearchResult { Icons = new List<string>() };
            var obj = ParseJsonObject(json);

            if (obj.TryGetValue("icons", out var icons))
                result.Icons = ParseJsonStringArray(icons);
            if (obj.TryGetValue("total", out var total) && int.TryParse(total, out var t))
                result.Total = t;

            return result;
        }

        private static Dictionary<string, string> ParseIconsBatch(string json, string prefix)
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
                bodyStr = IconImporter.NormalizeSvgColors(bodyStr);

                int iw = width, ih = height;
                if (iconObj.TryGetValue("width", out var iws) && int.TryParse(iws, out var iwv)) iw = iwv;
                if (iconObj.TryGetValue("height", out var ihs) && int.TryParse(ihs, out var ihv)) ih = ihv;

                var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{iw}\" height=\"{ih}\" viewBox=\"0 0 {iw} {ih}\">{bodyStr}</svg>";
                result[kv.Key] = svg;
            }
            return result;
        }

        #endregion API Response Parsing

        #region JSON Delegation

        private static Dictionary<string, string> ParseJsonObject(string json) => SimpleJsonParser.ParseJsonObject(json);
        private static List<string> ParseJsonStringArray(string json) => SimpleJsonParser.ParseJsonStringArray(json);
        private static string UnquoteJson(string s) => SimpleJsonParser.UnquoteJson(s);

        #endregion JSON Delegation
    }

    public class CollectionData
    {
        public string Prefix { get; set; }
        public List<string> IconNames { get; set; }
        public Dictionary<string, List<string>> Categories { get; set; }
        public int Total { get; set; }
    }

    public class SearchResult
    {
        public List<string> Icons { get; set; }
        public int Total { get; set; }
    }
}
