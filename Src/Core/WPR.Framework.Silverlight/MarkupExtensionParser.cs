using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Parses XAML markup extension syntax: <c>{TypeName arg1, name1=val1, name2=val2}</c>.
    /// Returns a structured representation; resolution to a CLR object is the caller's job.
    /// Quoted values, escapes, and nested extensions are not supported in 1.5.
    /// </summary>
    internal static class MarkupExtensionParser
    {
        public class ParseResult
        {
            public string TypeName { get; set; } = "";
            public List<string> PositionalArgs { get; } = new();
            public Dictionary<string, string> NamedArgs { get; } = new(StringComparer.Ordinal);
        }

        public static bool IsMarkupExtension(string raw)
        {
            string s = raw?.Trim() ?? "";
            return s.Length >= 2 && s[0] == '{' && s[s.Length - 1] == '}'
                // Escape sequence: {} prefix means a literal {.
                && !(s.Length >= 2 && s[0] == '{' && s[1] == '}');
        }

        public static ParseResult Parse(string raw)
        {
            string s = raw.Trim();
            if (!IsMarkupExtension(s))
                throw new XamlParseException($"Not a markup extension: '{raw}'");

            // Strip outer braces.
            string body = s.Substring(1, s.Length - 2).Trim();
            int firstSpace = body.IndexOf(' ');
            string typeName = firstSpace < 0 ? body : body.Substring(0, firstSpace);
            string rest = firstSpace < 0 ? "" : body.Substring(firstSpace + 1).Trim();

            var result = new ParseResult { TypeName = typeName };
            if (rest.Length == 0) return result;

            // Split on top-level commas. (No nesting support in 1.5.)
            foreach (string part in SplitTopLevelCommas(rest))
            {
                string token = part.Trim();
                if (token.Length == 0) continue;
                int eq = token.IndexOf('=');
                if (eq < 0)
                {
                    result.PositionalArgs.Add(token);
                }
                else
                {
                    string name = token.Substring(0, eq).Trim();
                    string value = token.Substring(eq + 1).Trim();
                    result.NamedArgs[name] = value;
                }
            }
            return result;
        }

        private static IEnumerable<string> SplitTopLevelCommas(string s)
        {
            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return s.Substring(start, i - start);
                    start = i + 1;
                }
            }
            yield return s.Substring(start);
        }
    }
}
