using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SAM.Modern
{
    /// <summary>调试用：把成就架构 bin 的结构转储为文本，便于排查解析问题。</summary>
    internal static class SchemaDump
    {
        public static void Write(string appIdText)
        {
            long appId;
            if (long.TryParse(appIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out appId) == false)
            {
                Log.Write("dump: bad appId " + appIdText);
                return;
            }

            string path = SchemaLoader.SchemaPath(appId);
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema-" + appId + ".txt");
            Log.Write("dump: " + path + " -> " + output);

            var sb = new StringBuilder();
            var kv = SchemaKv.LoadAsBinary(path);
            if (kv == null)
            {
                sb.AppendLine("LOAD FAILED: " + path);
            }
            else
            {
                Dial(kv, 0, sb);
            }
            File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            Log.Write("dump written: " + output);
        }

        private static void Dial(SchemaKv node, int depth, StringBuilder sb)
        {
            sb.Append(new string(' ', depth * 2));
            sb.Append(node.Name);
            sb.Append("  [type=").Append(node.Type).Append(']');
            if (node.Value != null) sb.Append(" = ").Append(Convert.ToString(node.Value, CultureInfo.InvariantCulture));
            sb.Append(node.Valid ? "" : " (invalid)");
            sb.AppendLine();
            if (node.Children == null) return;
            foreach (var child in node.Children)
            {
                if (depth > 8) { sb.AppendLine(new string(' ', (depth + 1) * 2) + "..."); return; }
                Dial(child, depth + 1, sb);
            }
        }
    }
}
