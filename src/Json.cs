using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SAM.Modern
{
    internal static class Json
    {
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    }

    internal sealed class JsonBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder(8192);
        private readonly Stack<int> _counts = new Stack<int>();
        private string _pending;

        public override string ToString() { return this._sb.ToString(); }

        private void Separate()
        {
            if (this._counts.Count == 0) return;
            int n = this._counts.Pop();
            if (n > 0) this._sb.Append(',');
            this._counts.Push(n + 1);
        }

        private void Before()
        {
            if (this._pending != null)
            {
                this.Separate();
                this._sb.Append('"').Append(Json.Escape(this._pending)).Append("\":");
                this._pending = null;
            }
            else
            {
                this.Separate();
            }
        }

        public JsonBuilder Name(string name) { this._pending = name; return this; }
        public JsonBuilder BeginObject() { this.Before(); this._sb.Append('{'); this._counts.Push(0); return this; }
        public JsonBuilder EndObject() { if (this._counts.Count > 0) this._counts.Pop(); this._sb.Append('}'); return this; }
        public JsonBuilder BeginArray() { this.Before(); this._sb.Append('['); this._counts.Push(0); return this; }
        public JsonBuilder EndArray() { if (this._counts.Count > 0) this._counts.Pop(); this._sb.Append(']'); return this; }

        public JsonBuilder Str(string v)
        {
            this.Before();
            if (v == null) this._sb.Append("null");
            else this._sb.Append('"').Append(Json.Escape(v)).Append('"');
            return this;
        }

        public JsonBuilder Num(long v) { this.Before(); this._sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }

        public JsonBuilder Num(double v)
        {
            this.Before();
            if (double.IsNaN(v) || double.IsInfinity(v)) { this._sb.Append('0'); return this; }
            this._sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonBuilder Bool(bool v) { this.Before(); this._sb.Append(v ? "true" : "false"); return this; }
        public JsonBuilder Null() { this.Before(); this._sb.Append("null"); return this; }
        public JsonBuilder Raw(string v) { this.Before(); this._sb.Append(v ?? "null"); return this; }
    }

    internal static class JsonParser
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int index = 0;
            object value = ParseValue(text, ref index);
            return value;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            return Parse(text) as Dictionary<string, object>;
        }

        public static string GetString(Dictionary<string, object> o, string key)
        {
            if (o == null) return null;
            object v;
            if (o.TryGetValue(key, out v) == false || v == null) return null;
            return v as string;
        }

        public static bool GetBool(Dictionary<string, object> o, string key, bool fallback = false)
        {
            if (o == null) return fallback;
            object v;
            if (o.TryGetValue(key, out v) == false || v == null) return fallback;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            if (v is string) return ((string)v) == "true";
            return fallback;
        }

        public static double GetNumber(Dictionary<string, object> o, string key, double fallback = 0)
        {
            if (o == null) return fallback;
            object v;
            if (o.TryGetValue(key, out v) == false || v == null) return fallback;
            if (v is double) return (double)v;
            if (v is string) { double d; return double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback; }
            return fallback;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> o, string key)
        {
            if (o == null) return null;
            object v;
            if (o.TryGetValue(key, out v) == false || v == null) return null;
            return v as Dictionary<string, object>;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char c = text[index];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') index++;
                else break;
            }
        }

        private static object ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length) return null;
            char c = text[index];
            switch (c)
            {
                case '{': return ParseObjectInternal(text, ref index);
                case '[': return ParseArray(text, ref index);
                case '"': return ParseString(text, ref index);
                case 't': index += 4; return true;
                case 'f': index += 5; return false;
                case 'n': index += 4; return null;
                default: return ParseNumber(text, ref index);
            }
        }

        private static Dictionary<string, object> ParseObjectInternal(string text, ref int index)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            index++; // {
            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length) return result;
                if (text[index] == '}') { index++; return result; }
                if (text[index] == ',') { index++; continue; }
                if (text[index] != '"') { index++; continue; }
                string key = ParseString(text, ref index);
                SkipWhitespace(text, ref index);
                if (index < text.Length && text[index] == ':') index++;
                object value = ParseValue(text, ref index);
                result[key] = value;
            }
        }

        private static List<object> ParseArray(string text, ref int index)
        {
            var result = new List<object>();
            index++; // [
            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length) return result;
                if (text[index] == ']') { index++; return result; }
                if (text[index] == ',') { index++; continue; }
                result.Add(ParseValue(text, ref index));
            }
        }

        private static string ParseString(string text, ref int index)
        {
            var sb = new StringBuilder();
            index++; // opening quote
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (index >= text.Length) break;
                char e = text[index++];
                switch (e)
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
                        if (index + 4 <= text.Length)
                        {
                            int code;
                            if (int.TryParse(text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                            }
                            index += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private static object ParseNumber(string text, ref int index)
        {
            int start = index;
            while (index < text.Length)
            {
                char c = text[index];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') index++;
                else break;
            }
            double d;
            if (double.TryParse(text.Substring(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }
    }
}
