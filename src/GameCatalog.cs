using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SAM.Modern
{
    /// <summary>Steam 全部游戏目录（对应原版 SAM 的 games.xml 列表）。</summary>
    internal static class GameCatalog
    {
        public const string RemoteUrl = "https://gib.me/sam/games.xml";

        private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

        private static readonly Regex GamePattern = new Regex(
            "<game(?:\\s+type=\"([^\"]*)\")?\\s*>(\\d+)</game>",
            RegexOptions.CultureInvariant);

        public sealed class CatalogEntry
        {
            public long Id;
            public string Type = "normal";
        }

        public static string CachePath
        {
            get
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "games.xml");
            }
        }

        /// <summary>读取目录：优先使用未过期缓存，否则下载；下载失败则退回旧缓存。</summary>
        public static List<CatalogEntry> Load(out string source)
        {
            string path = CachePath;
            string text = null;
            source = "cache";

            bool cached = false;
            try
            {
                if (File.Exists(path) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheMaxAge)
                {
                    text = File.ReadAllText(path, Encoding.UTF8);
                    cached = !string.IsNullOrEmpty(text);
                }
            }
            catch (Exception error) { Log.Write("games.xml cache read failed", error); }

            if (cached == false)
            {
                try
                {
                    text = Download();
                    source = "network";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, text, Encoding.UTF8);
                    }
                    catch (Exception error) { Log.Write("games.xml cache write failed", error); }
                }
                catch (Exception error)
                {
                    Log.Write("games.xml download failed", error);
                    source = "stale-cache";
                    try
                    {
                        if (File.Exists(path)) text = File.ReadAllText(path, Encoding.UTF8);
                    }
                    catch (Exception inner) { Log.Write("games.xml stale cache read failed", inner); }
                }
            }

            if (string.IsNullOrEmpty(text)) return null;

            var list = new List<CatalogEntry>();
            try
            {
                foreach (Match m in GamePattern.Matches(text))
                {
                    long id;
                    if (long.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) == false) continue;
                    if (id <= 0) continue;
                    var entry = new CatalogEntry();
                    entry.Id = id;
                    string type = m.Groups[1].Value;
                    entry.Type = string.IsNullOrEmpty(type) ? "normal" : type.Trim().ToLowerInvariant();
                    list.Add(entry);
                }
            }
            catch (Exception error) { Log.Write("games.xml parse failed", error); }

            Log.Write("game catalog loaded from " + source + ": " + list.Count + " entries");
            return list;
        }

        private static string Download()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "SAM.Modern");
                byte[] bytes = client.DownloadData(new Uri(RemoteUrl));
                return Encoding.UTF8.GetString(bytes);
            }
        }

        public static string TypeLabelKey(string type)
        {
            if (string.IsNullOrEmpty(type)) return "normal";
            switch (type)
            {
                case "normal":
                case "demo":
                case "mod":
                case "junk":
                    return type;
                default:
                    return "normal";
            }
        }
    }
}
