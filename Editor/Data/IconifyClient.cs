using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace IconBrowser.Data
{
    /// <summary>
    /// HTTP client wrapper for the Iconify API.
    /// </summary>
    public static class IconifyClient
    {
        const string BASE_URL = "https://api.iconify.design";

        /// <summary>
        /// Fetches all available icon collections (200+ libraries).
        /// Returns a dictionary of prefix -> collection info JSON.
        /// </summary>
        public static async Task<Dictionary<string, IconLibrary>> GetCollectionsAsync()
        {
            var json = await FetchAsync($"{BASE_URL}/collections");
            return ParseCollections(json);
        }

        /// <summary>
        /// Fetches all icon names in a specific collection.
        /// </summary>
        public static async Task<CollectionData> GetCollectionAsync(string prefix)
        {
            var json = await FetchAsync($"{BASE_URL}/collection?prefix={prefix}&info=true&chars=false");
            return ParseCollection(json, prefix);
        }

        /// <summary>
        /// Searches icons across a specific prefix (or all if prefix is empty).
        /// </summary>
        public static async Task<SearchResult> SearchAsync(string query, string prefix = "", int limit = 64)
        {
            var url = $"{BASE_URL}/search?query={Uri.EscapeDataString(query)}&limit={limit}";
            if (!string.IsNullOrEmpty(prefix))
                url += $"&prefix={prefix}";
            var json = await FetchAsync(url);
            return ParseSearchResult(json);
        }

        /// <summary>
        /// Fetches SVG bodies for multiple icons in one request.
        /// </summary>
        public static async Task<Dictionary<string, string>> GetIconsBatchAsync(string prefix, string[] names)
        {
            if (names.Length == 0) return new Dictionary<string, string>();

            var icons = string.Join(",", names);
            var json = await FetchAsync($"{BASE_URL}/{prefix}.json?icons={icons}");
            return ParseIconsBatch(json, prefix);
        }

        /// <summary>
        /// Fetches a single icon as a complete SVG string.
        /// </summary>
        public static async Task<string> GetSvgAsync(string prefix, string name)
        {
            return await FetchAsync($"{BASE_URL}/{prefix}/{name}.svg");
        }

        const int MAX_RETRIES = 2;

        static async Task<string> FetchAsync(string url)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                var request = UnityWebRequest.Get(url);
                request.timeout = 15;
                var op = request.SendWebRequest();
                var tcs = new TaskCompletionSource<bool>();
                op.completed += _ => tcs.SetResult(true);
                await tcs.Task;

                if (request.result == UnityWebRequest.Result.Success)
                    return request.downloadHandler.text;

                lastError = new Exception($"Iconify API error: {request.error} ({url})");
                request.Dispose();

                if (attempt < MAX_RETRIES)
                    await Task.Delay(500 * (attempt + 1));
            }
            throw lastError;
        }

        #region JSON Parsing (manual — avoids Newtonsoft dependency)

        static Dictionary<string, IconLibrary> ParseCollections(string json)
        {
            var result = new Dictionary<string, IconLibrary>();
            // Parse top-level object: { "prefix": { "name": "...", "total": N, ... }, ... }
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

            // "uncategorized" is a flat array of icon names
            if (obj.TryGetValue("uncategorized", out var uncategorized))
            {
                data.IconNames.AddRange(ParseJsonStringArray(uncategorized));
            }

            // "categories" is { "Category": ["icon1", "icon2"], ... }
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
                // Unescape JSON string
                bodyStr = bodyStr.Replace("\\\"", "\"").Replace("\\\\", "\\");
                // Unity SVG parser does not support currentColor — replace with white
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

        #region Minimal JSON Parser

        /// <summary>
        /// Parses a JSON object into key-value pairs (values are raw JSON strings).
        /// Handles nested objects/arrays by tracking brace/bracket depth.
        /// </summary>
        static Dictionary<string, string> ParseJsonObject(string json)
        {
            var result = new Dictionary<string, string>();
            json = json.Trim();
            if (json.Length < 2 || json[0] != '{') return result;

            int i = 1;
            while (i < json.Length - 1)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length - 1 || json[i] == '}') break;
                if (json[i] == ',') { i++; continue; }

                var key = ReadJsonString(json, ref i);
                SkipWhitespace(json, ref i);
                if (i < json.Length && json[i] == ':') i++;
                SkipWhitespace(json, ref i);
                var value = ReadJsonValue(json, ref i);
                result[key] = value;
            }
            return result;
        }

        static List<string> ParseJsonStringArray(string json)
        {
            var result = new List<string>();
            json = json.Trim();
            if (json.Length < 2 || json[0] != '[') return result;

            int i = 1;
            while (i < json.Length - 1)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length - 1 || json[i] == ']') break;
                if (json[i] == ',') { i++; continue; }

                if (json[i] == '"')
                {
                    result.Add(ReadJsonString(json, ref i));
                }
                else
                {
                    ReadJsonValue(json, ref i); // skip non-string values
                }
            }
            return result;
        }

        static void SkipWhitespace(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        }

        static string ReadJsonString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"') return "";
            i++; // skip opening quote
            int start = i;
            while (i < json.Length)
            {
                if (json[i] == '\\') { i += 2; continue; }
                if (json[i] == '"') break;
                i++;
            }
            var result = json.Substring(start, i - start);
            if (i < json.Length) i++; // skip closing quote
            return result;
        }

        static string ReadJsonValue(string json, ref int i)
        {
            if (i >= json.Length) return "";

            if (json[i] == '"')
            {
                int start = i;
                ReadJsonString(json, ref i);
                return json.Substring(start, i - start);
            }

            if (json[i] == '{' || json[i] == '[')
            {
                char open = json[i], close = open == '{' ? '}' : ']';
                int depth = 1, start = i;
                i++;
                bool inString = false;
                while (i < json.Length && depth > 0)
                {
                    if (json[i] == '\\' && inString) { i += 2; continue; }
                    if (json[i] == '"') inString = !inString;
                    if (!inString)
                    {
                        if (json[i] == open) depth++;
                        else if (json[i] == close) depth--;
                    }
                    i++;
                }
                return json.Substring(start, i - start);
            }

            // number, bool, null
            int vstart = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']' && !char.IsWhiteSpace(json[i]))
                i++;
            return json.Substring(vstart, i - vstart);
        }

        static string UnquoteJson(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }

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
