using System;
using System.Collections.Generic;

namespace System.Windows.Navigation
{
    /// <summary>
    /// Shim for <c>System.Windows.Navigation.UriMapping</c>. A single URI-rewrite rule used
    /// by <see cref="UriMapper"/>: when an incoming URI matches the <see cref="Uri"/> pattern
    /// it is rewritten to <see cref="MappedUri"/>.
    ///
    /// Supports the two features WP apps rely on:
    ///  - <c>{placeholder}</c> tokens in path segments and query values, captured from the
    ///    incoming URI and substituted into the mapped URI, and
    ///  - pass-through of query parameters the pattern doesn't mention (appended to the result).
    /// </summary>
    public class UriMapping
    {
        public UriMapping() { }

        public UriMapping(Uri uri, Uri mappedUri)
        {
            Uri = uri;
            MappedUri = mappedUri;
        }

        /// <summary>The pattern an incoming URI is matched against.</summary>
        public Uri? Uri { get; set; }

        /// <summary>The URI produced when <see cref="Uri"/> matches.</summary>
        public Uri? MappedUri { get; set; }

        /// <summary>
        /// Returns the rewritten URI if <paramref name="uri"/> matches this rule, otherwise
        /// <c>null</c> so the caller can try the next rule.
        /// </summary>
        public Uri? MapUri(Uri uri)
        {
            if (Uri == null || MappedUri == null || uri == null)
                return null;

            Split(uri.OriginalString, out string inPath, out string inQuery);
            Split(Uri.OriginalString, out string patPath, out string patQuery);

            var captures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!MatchPath(patPath, inPath, captures))
                return null;

            var passThrough = new List<string>();
            if (!MatchQuery(patQuery, inQuery, captures, passThrough))
                return null;

            string result = Substitute(MappedUri.OriginalString, captures);
            if (passThrough.Count > 0)
                result += (result.Contains('?') ? "&" : "?") + string.Join("&", passThrough);

            return new Uri(result, UriKind.Relative);
        }

        private static void Split(string value, out string path, out string query)
        {
            int q = value.IndexOf('?');
            if (q < 0)
            {
                path = value;
                query = string.Empty;
            }
            else
            {
                path = value.Substring(0, q);
                query = value.Substring(q + 1);
            }
        }

        private static bool MatchPath(string patternPath, string inputPath,
            Dictionary<string, string> captures)
        {
            string[] pat = patternPath.TrimStart('/').Split('/');
            string[] inp = inputPath.TrimStart('/').Split('/');
            if (pat.Length != inp.Length)
                return false;

            for (int i = 0; i < pat.Length; i++)
            {
                string? key = TryGetPlaceholder(pat[i]);
                if (key != null)
                {
                    if (inp[i].Length == 0) return false;
                    captures[key] = inp[i];
                }
                else if (!string.Equals(pat[i], inp[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool MatchQuery(string patternQuery, string inputQuery,
            Dictionary<string, string> captures, List<string> passThrough)
        {
            var input = ParseQuery(inputQuery);
            var consumed = new bool[input.Count];

            foreach (var (patKey, patValue) in ParseQuery(patternQuery))
            {
                int idx = -1;
                for (int i = 0; i < input.Count; i++)
                {
                    if (!consumed[i] &&
                        string.Equals(input[i].Key, patKey, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx < 0) return false; // pattern requires a query key the input lacks

                consumed[idx] = true;
                string? key = TryGetPlaceholder(patValue);
                if (key != null)
                    captures[key] = input[idx].Value;
                else if (!string.Equals(patValue, input[idx].Value, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            for (int i = 0; i < input.Count; i++)
            {
                if (consumed[i]) continue;
                passThrough.Add(input[i].Value.Length == 0
                    ? input[i].Key
                    : input[i].Key + "=" + input[i].Value);
            }
            return true;
        }

        private static List<KeyValuePair<string, string>> ParseQuery(string query)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(query))
                return result;

            foreach (string part in query.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq < 0)
                    result.Add(new KeyValuePair<string, string>(part, string.Empty));
                else
                    result.Add(new KeyValuePair<string, string>(
                        part.Substring(0, eq), part.Substring(eq + 1)));
            }
            return result;
        }

        /// <summary>Returns the inner name of a <c>{name}</c> token, or null if not a token.</summary>
        private static string? TryGetPlaceholder(string segment)
        {
            if (segment.Length >= 2 && segment[0] == '{' && segment[segment.Length - 1] == '}')
                return segment.Substring(1, segment.Length - 2);
            return null;
        }

        private static string Substitute(string template, Dictionary<string, string> captures)
        {
            foreach (var kv in captures)
                template = template.Replace("{" + kv.Key + "}", kv.Value);
            return template;
        }
    }
}
