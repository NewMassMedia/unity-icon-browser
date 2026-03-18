using System.Collections.Generic;

namespace IconBrowser.Data
{
    /// <summary>
    /// Minimal JSON parser for Iconify API responses.
    /// Handles objects, string arrays, and primitive values without external dependencies.
    /// Consolidated from duplicate parsers in IconifyClient, IconManifest, and IconAtlas.
    /// </summary>
    internal static class SimpleJsonParser
    {
        /// <summary>
        /// Parses a JSON object into key-value pairs (values are raw JSON strings).
        /// Handles nested objects/arrays by tracking brace/bracket depth.
        /// </summary>
        public static Dictionary<string, string> ParseJsonObject(string json)
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

        /// <summary>
        /// Parses a JSON array of strings (e.g. ["a", "b", "c"]).
        /// Non-string values are skipped.
        /// </summary>
        public static List<string> ParseJsonStringArray(string json)
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

        /// <summary>
        /// Reads a JSON quoted string starting at position i.
        /// Handles backslash escapes. Advances i past the closing quote.
        /// </summary>
        public static string ReadJsonString(string json, ref int i)
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

        /// <summary>
        /// Reads any JSON value (string, object, array, number, bool, null) as a raw string.
        /// Advances i past the value.
        /// </summary>
        public static string ReadJsonValue(string json, ref int i)
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

        /// <summary>
        /// Skips whitespace characters starting at position i.
        /// </summary>
        public static void SkipWhitespace(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        }

        /// <summary>
        /// Removes surrounding quotes from a JSON string value.
        /// </summary>
        public static string UnquoteJson(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }

        /// <summary>
        /// Unescapes JSON backslash sequences in a string.
        /// </summary>
        public static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        /// <summary>
        /// Escapes a string for safe JSON embedding (backslash and double-quote).
        /// </summary>
        public static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
