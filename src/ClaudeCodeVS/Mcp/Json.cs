using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// JSON helpers built on <see cref="JavaScriptSerializer"/>.
    ///
    /// Deliberately not Newtonsoft.Json: Visual Studio loads its own Newtonsoft into the same
    /// AppDomain, and shipping a second copy inside a VSIX is a classic source of
    /// assembly binding failures. JavaScriptSerializer ships with the .NET Framework itself,
    /// so the extension carries no serialization dependency at all.
    /// </summary>
    internal static class Json
    {
        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = 512
            };
        }

        public static string Serialize(object value)
        {
            return CreateSerializer().Serialize(value);
        }

        /// <summary>Parses a JSON document that is expected to be an object.</summary>
        public static Dictionary<string, object> ParseObject(string json)
        {
            return CreateSerializer().DeserializeObject(json) as Dictionary<string, object>;
        }

        public static Dictionary<string, object> GetObject(IDictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        public static string GetString(IDictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return null;
            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static int? GetInt(IDictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return null;
            if (value is int) return (int)value;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool? GetBool(IDictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return null;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed)
                ? (bool?)parsed
                : null;
        }

        /// <summary>
        /// Returns the raw value for <paramref name="key"/>. JSON-RPC ids may be a string or a
        /// number and must be echoed back with the original type, so callers keep them boxed.
        /// </summary>
        public static object GetRaw(IDictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value)) return null;
            return value;
        }

        public static bool HasKey(IDictionary<string, object> source, string key)
        {
            return source != null && source.ContainsKey(key);
        }

        /// <summary>Builds an ordered dictionary literal, the workhorse for response payloads.</summary>
        public static Dictionary<string, object> Obj(params object[] keyValuePairs)
        {
            if (keyValuePairs == null) return new Dictionary<string, object>();
            if (keyValuePairs.Length % 2 != 0)
            {
                throw new ArgumentException("Expected an even number of arguments.", "keyValuePairs");
            }

            var result = new Dictionary<string, object>(keyValuePairs.Length / 2, StringComparer.Ordinal);
            for (int i = 0; i < keyValuePairs.Length; i += 2)
            {
                result[Convert.ToString(keyValuePairs[i], CultureInfo.InvariantCulture)] = keyValuePairs[i + 1];
            }

            return result;
        }

        /// <summary>Wraps plain strings into the MCP <c>content</c> array shape.</summary>
        public static List<object> TextContent(params string[] parts)
        {
            var content = new List<object>(parts.Length);
            foreach (var part in parts)
            {
                content.Add(Obj("type", "text", "text", part ?? string.Empty));
            }

            return content;
        }

        public static IEnumerable AsEnumerable(object value)
        {
            return value as IEnumerable;
        }
    }
}
