
namespace Nintex.K2
{
    using System.Collections.Generic;
    using System;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Linq;

    public static class JsonSnip
    {
        /// <summary>
        /// Extract the first complete top-level JSON value (object OR array) from a string.
        /// - Ignores bracket/brace characters inside double-quoted strings (handles escapes).
        /// - Strips Harmony-style tokens (<|...|>) and code fences by default.
        /// - If mustParse=true, validates with System.Text.Json before returning.
        /// Returns the JSON substring (including outer [] or {}) or null if not found.
        /// </summary>
        public static string ExtractFirstJsonValue(string raw, bool stripNoise = true, bool mustParse = false)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string text = raw;

            if (stripNoise)
            {
                // Remove Harmony/meta tokens like <|channel|>, <|message|>, etc.
                text = Regex.Replace(text, @"<\|[^>|]+?\|>", "");
                // Strip code-fence markers (keep inner content)
                text = text.Replace("```json", "")
                           .Replace("```JSON", "")
                           .Replace("```", "");
            }

            int start = -1;
            bool inString = false, escaped = false;
            var stack = new Stack<char>(); // tracks '{' and '[' nesting

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
                        continue;
                    }

                    if (ch == '"') { inString = true; continue; }

                    if (ch == '{' || ch == '[')
                    {
                        stack.Push(ch);
                    }
                    else if (ch == '}' || ch == ']')
                    {
                        if (stack.Count == 0) return null; // malformed
                        char open = stack.Peek();
                        bool match = (open == '{' && ch == '}') || (open == '[' && ch == ']');
                        if (!match) return null; // malformed (mismatched)
                        stack.Pop();

                        if (stack.Count == 0)
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
                    // other chars ignored
                }
                else
                {
                    // Not yet started: look for the first top-level '{' OR '['
                    if (!inString)
                    {
                        if (ch == '"') { inString = true; }
                        else if (ch == '{' || ch == '[')
                        {
                            start = i;
                            stack.Push(ch);
                        }
                    }
                    else
                    {
                        // We’re in a stray string before any JSON begins
                        if (escaped) { escaped = false; }
                        else if (ch == '\\') { escaped = true; }
                        else if (ch == '"') { inString = false; }
                    }
                }
            }

            return null; // not found or unbalanced
        }
    }
}
