using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

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

        private static int _refreshing;

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

        /// <summary>读取目录：缓存存在就立即使用（过期时后台刷新），不存在则同步下载；失败返回 null，绝不抛给上层。</summary>
        public static List<CatalogEntry> Load(out string source)
        {
            string path = CachePath;
            string text = null;
            source = "cache";

            bool stale = false;
            try
            {
                if (File.Exists(path))
                {
                    text = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrEmpty(text) == false)
                    {
                        stale = DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= CacheMaxAge;
                        if (stale) source = "stale-cache";
                    }
                }
            }
            catch (Exception error)
            {
                Log.Write("games.xml cache read failed", error);
                text = null;
            }

            // 旧缓存先用起来，刷新交给后台线程，避免 UI 线程被网络卡住。
            if (stale) StartBackgroundRefresh(path);

            if (string.IsNullOrEmpty(text))
            {
                try
                {
                    text = Download();
                    if (string.IsNullOrEmpty(text) == false)
                    {
                        source = "network";
                        WriteCache(path, text);
                    }
                }
                catch (Exception error)
                {
                    Log.Write("game catalog download failed", error);
                    return null;
                }
            }

            if (string.IsNullOrEmpty(text)) return null;

            List<CatalogEntry> list = Parse(text);
            Log.Write("game catalog loaded from " + source + ": " + list.Count + " entries");
            return list;
        }

        private static List<CatalogEntry> Parse(string text)
        {
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
            return list;
        }

        private static string Download()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            var request = (HttpWebRequest)WebRequest.Create(RemoteUrl);
            request.UserAgent = "SAM.Modern";
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            request.KeepAlive = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteCache(string path, string text)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory) == false) Directory.CreateDirectory(directory);
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception error) { Log.Write("games.xml cache write failed", error); }
        }

        /// <summary>后台刷新过期缓存；同一时间只跑一个刷新任务，且不触碰 UI/Channel。</summary>
        private static void StartBackgroundRefresh(string path)
        {
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;

            try
            {
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    try
                    {
                        string text = Download();
                        if (string.IsNullOrEmpty(text))
                        {
                            Log.Write("game catalog background refresh failed: empty response");
                            return;
                        }

                        List<CatalogEntry> list = Parse(text);
                        WriteCache(path, text);
                        Log.Write("game catalog cache refreshed: " + list.Count + " entries");
                    }
                    catch (Exception error) { Log.Write("game catalog background refresh failed", error); }
                    finally { Interlocked.Exchange(ref _refreshing, 0); }
                });
            }
            catch (Exception error)
            {
                Log.Write("game catalog background refresh queue failed", error);
                Interlocked.Exchange(ref _refreshing, 0);
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
