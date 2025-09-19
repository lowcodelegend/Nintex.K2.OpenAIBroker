
namespace Nintex.K2
{
    using System.Text.Json;
    using System.Text.RegularExpressions;

    public static class JsonSnip
    {
        /// <summary>
        /// Extract the first complete top-level JSON object from a string.
        /// - Ignores braces inside double-quoted strings and handles escapes.
        /// - Optionally strips Harmony tokens (<|...|>) and code fences.
        /// - If mustParse=true, validates with System.Text.Json before returning.
        /// Returns the JSON substring (including the outer braces) or null if not found.
        /// </summary>
        public static string ExtractFirstJsonObject(string raw, bool stripNoise = true, bool mustParse = false)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string text = raw;

            if (stripNoise)
            {
                // Remove Harmony/meta tokens like <|channel|>, <|message|>, etc.
                text = Regex.Replace(text, @"<\|[^>|]+?\|>", "");
                // Strip code-fence markers (keep inner content)
                text = text.Replace("```json", "").Replace("```", "");
            }

            int start = -1, depth = 0;
            bool inString = false, escaped = false;

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];

                if (start >= 0)
                {
                    if (inString)
                    {
                        if (escaped) { escaped = false; }
                        else if (ch == '\\') { escaped = true; }
                        else if (ch == '"') { inString = false; }
                    }
                    else
                    {
                        if (ch == '"') { inString = true; }
                        else if (ch == '{') { depth++; }
                        else if (ch == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                string slice = text.Substring(start, i - start + 1);
                                if (mustParse)
                                {
                                    try { var _ = JsonDocument.Parse(slice); }
                                    catch { return null; }
                                }
                                return slice;
                            }
                        }
                    }
                }
                else
                {
                    if (ch == '{')
                    {
                        start = i;
                        depth = 1;
                    }
                }
            }
            return null;
        }
    }
}
