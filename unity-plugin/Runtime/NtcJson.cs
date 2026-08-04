// NtcJson.cs
// Minimal, dependency-free JSON parser used to read the NTC container's JSON descriptor chunk.
// Deliberately avoids UnityEngine and any third-party package so that it can be compiled and
// unit-tested completely outside of Unity (e.g. with plain csc.exe) and also dropped unchanged
// into a Unity project (Editor or Runtime assembly).
//
// Supported subset of JSON is sufficient for the NTC "TextureSetFile" schema:
// objects, arrays, strings, numbers (as double), true/false/null.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ntc.Packing
{
    /// <summary>
    /// Tiny recursive-descent JSON parser. Produces a plain object graph:
    /// Dictionary&lt;string, object&gt; for objects, List&lt;object&gt; for arrays,
    /// string, double, bool or null for scalars.
    /// </summary>
    public static class NtcJson
    {
        public static object Parse(string json)
        {
            int index = 0;
            object result = ParseValue(json, ref index);
            SkipWhitespace(json, ref index);
            return result;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't':
                    Expect(s, ref i, "true");
                    return true;
                case 'f':
                    Expect(s, ref i, "false");
                    return false;
                case 'n':
                    Expect(s, ref i, "null");
                    return null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (s[i] == '}') { i++; return dict; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (s[i] != ':') throw new FormatException("Expected ':' at position " + i);
                i++;
                object value = ParseValue(s, ref i);
                dict[key] = value;

                SkipWhitespace(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new FormatException("Expected ',' or '}' at position " + i);
            }
            return dict;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (s[i] == ']') { i++; return list; }

            while (true)
            {
                object value = ParseValue(s, ref i);
                list.Add(value);

                SkipWhitespace(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new FormatException("Expected ',' or ']' at position " + i);
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected '\"' at position " + i);
            i++;
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i];
                if (c == '\\')
                {
                    i++;
                    char esc = s[i];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            string hex = s.Substring(i + 1, 4);
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default:
                            throw new FormatException("Unknown escape sequence \\" + esc);
                    }
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            i++; // closing quote
            return sb.ToString();
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
                i++;
            string token = s.Substring(start, i - start);
            return double.Parse(token, CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
                throw new FormatException("Expected literal '" + literal + "' at position " + i);
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n'))
                i++;
        }

        // ---- Convenience accessors for the object graph produced above ----

        public static Dictionary<string, object> AsObject(object v)
        {
            return v as Dictionary<string, object>;
        }

        public static List<object> AsArray(object v)
        {
            return v as List<object>;
        }

        public static string GetString(Dictionary<string, object> obj, string key, string defaultValue)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v is string) return (string)v;
            return defaultValue;
        }

        public static int GetInt(Dictionary<string, object> obj, string key, int defaultValue)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v is double) return (int)(double)v;
            return defaultValue;
        }

        public static long GetLong(Dictionary<string, object> obj, string key, long defaultValue)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v is double) return (long)(double)v;
            return defaultValue;
        }

        public static float GetFloat(Dictionary<string, object> obj, string key, float defaultValue)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v is double) return (float)(double)v;
            return defaultValue;
        }

        public static bool GetBool(Dictionary<string, object> obj, string key, bool defaultValue)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v is bool) return (bool)v;
            return defaultValue;
        }

        public static List<object> GetArray(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v)) return v as List<object>;
            return null;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v)) return v as Dictionary<string, object>;
            return null;
        }
    }
}
