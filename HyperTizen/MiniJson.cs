using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HyperTizen.WebSocket.DataTypes;
using static HyperTizen.WebSocket.DataTypes.SSDPScanResultEvent;

namespace HyperTizen
{
    // Minimal JSON reader/writer for the flat control messages. Avoids Newtonsoft.Json,
    // whose assembly fails to load on Tizen 6.0 firmware.
    public static class MiniJson
    {
        public static Dictionary<string, string> ParseObject(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            SkipWs(json, ref i);
            if (Peek(json, i) != '{') throw new FormatException("JSON object expected");
            i++;
            SkipWs(json, ref i);
            if (Peek(json, i) == '}') return result;

            while (true)
            {
                SkipWs(json, ref i);
                string key = ReadString(json, ref i);
                SkipWs(json, ref i);
                if (Peek(json, i) != ':') throw new FormatException("':' expected");
                i++;
                SkipWs(json, ref i);
                result[key] = ReadValue(json, ref i);
                SkipWs(json, ref i);
                char c = Peek(json, i);
                i++;
                if (c == ',') continue;
                if (c == '}') break;
                throw new FormatException("',' or '}' expected");
            }
            return result;
        }

        public static Event ParseEvent(Dictionary<string, string> fields)
        {
            if (!fields.TryGetValue("Event", out string raw) || raw == null)
                throw new FormatException("Event missing");
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                return (Event)number;
            return (Event)Enum.Parse(typeof(Event), raw, true);
        }

        public static string Field(Dictionary<string, string> fields, string name)
        {
            return fields.TryGetValue(name, out string value) ? value : null;
        }

        public static string ReadConfigResult(bool error, string key, string value)
        {
            return "{\"Event\":" + (int)Event.ReadConfigResult
                + ",\"error\":" + (error ? "true" : "false")
                + ",\"key\":" + Quote(key)
                + ",\"value\":" + Quote(value) + "}";
        }

        public static string SsdpScanResult(List<SSDPDevice> devices)
        {
            var sb = new StringBuilder();
            sb.Append("{\"Event\":").Append((int)Event.SSDPScanResult).Append(",\"devices\":[");
            for (int n = 0; n < devices.Count; n++)
            {
                if (n > 0) sb.Append(',');
                sb.Append("{\"FriendlyName\":").Append(Quote(devices[n].FriendlyName))
                  .Append(",\"UrlBase\":").Append(Quote(devices[n].UrlBase)).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string SerializeImageCommand(ImageCommand c)
        {
            return "{\"command\":" + Quote(c.command)
                + ",\"imagedata\":" + Quote(c.imagedata)
                + ",\"imagewidth\":" + c.imagewidth.ToString(CultureInfo.InvariantCulture)
                + ",\"imageheight\":" + c.imageheight.ToString(CultureInfo.InvariantCulture)
                + ",\"name\":" + Quote(c.name)
                + ",\"format\":" + Quote(c.format)
                + ",\"priority\":" + c.priority.ToString(CultureInfo.InvariantCulture)
                + ",\"duration\":" + c.duration.ToString(CultureInfo.InvariantCulture)
                + ",\"origin\":" + Quote(c.origin) + "}";
        }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static char Peek(string s, int i)
        {
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            return s[i];
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static string ReadString(string s, ref int i)
        {
            if (Peek(s, i) != '"') throw new FormatException("'\"' expected");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = Peek(s, i++);
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                char e = Peek(s, i++);
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
        }

        // Strings are returned unescaped, numbers/booleans as raw text, null and nested values as null.
        private static string ReadValue(string s, ref int i)
        {
            char c = Peek(s, i);
            if (c == '"') return ReadString(s, ref i);

            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (true)
                {
                    char d = Peek(s, i);
                    if (d == '"') { ReadString(s, ref i); continue; }
                    i++;
                    if (d == '{' || d == '[') depth++;
                    else if (d == '}' || d == ']') { depth--; if (depth == 0) return null; }
                }
            }

            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && !char.IsWhiteSpace(s[i])) i++;
            string raw = s.Substring(start, i - start);
            return raw == "null" ? null : raw;
        }
    }
}
