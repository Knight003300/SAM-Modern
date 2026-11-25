using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace SAM.Modern
{
    /// <summary>诊断：统计 Steam 目录条目、本账号拥有数量与耗时。</summary>
    internal static class CatalogProbe
    {
        /// <summary>诊断：把 librarycache 里出现过、但扫描结果里没有的 appid 逐个问 Steam 为什么没有。</summary>
        public static void RunFamily(string steamPath)
        {
            var sb = new StringBuilder();
            var client = new SAM.API.Client();
            try
            {
                client.Initialize(0);
            }
            catch (Exception e)
            {
                sb.AppendLine("client init failed " + e.GetType().Name + ": " + e.Message);
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-family.txt"), sb.ToString(), Encoding.UTF8);
                return;
            }
            client.RunCallbacks(false);
            ulong self = 0;
            try { self = client.SteamUser.GetSteamId(); } catch (Exception) { }
            sb.AppendLine("self steamId=" + self + " steamPath=" + (steamPath ?? "<null>"));

            // 已扫描到的 appid（owned.txt 每行 appId\ttype\tname）
            var scanned = new HashSet<long>();
            string ownedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "owned.txt");
            if (File.Exists(ownedPath))
            {
                foreach (string line in File.ReadAllLines(ownedPath))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('\t');
                    long id;
                    if (long.TryParse(parts[0], out id)) scanned.Add(id);
                }
            }
            sb.AppendLine("scanned=" + scanned.Count);

            var candidates = new SortedSet<long>();
            // 候选来源 1：显式给出的 id 列表（便于人工核对某个游戏为什么没扫到）
            string listPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-family-ids.txt");
            if (File.Exists(listPath))
            {
                foreach (string line in File.ReadAllLines(listPath))
                {
                    long id;
                    if (long.TryParse(line.Trim(), out id) && id > 0 && scanned.Contains(id) == false) candidates.Add(id);
                }
            }
            // 候选来源 2：Steam 的 librarycache（本账号见过的 app 缓存）
            string libCache = Path.Combine(steamPath, "appcache", "librarycache");
            if (Directory.Exists(libCache))
            {
                foreach (string file in Directory.GetFiles(libCache))
                {
                    long id;
                    if (long.TryParse(Path.GetFileName(file), out id) && id > 0 && scanned.Contains(id) == false) candidates.Add(id);
                }
            }
            // 候选来源 3：localconfig.vdf 的 apps 段（玩过/配置过的 app）
            foreach (long id in ReadLocalConfigApps(steamPath))
            {
                if (scanned.Contains(id) == false) candidates.Add(id);
            }
            // 已安装但没扫描到的也加进来
            sb.AppendLine("candidates=" + candidates.Count);

            int sub = 0, fam = 0, free = 0, inst = 0, named = 0, notInLib = 0;
            var watch = Stopwatch.StartNew();
            foreach (long id in candidates)
            {
                uint appId = (uint)id;
                bool isSub = SafeBool(delegate { return client.SteamApps008.IsSubscribedApp(appId); });
                bool isFam = SafeBool(delegate { return client.SteamApps008.IsSubscribedFromFamilySharing(appId); });
                bool isFree = SafeBool(delegate { return client.SteamApps008.IsSubscribedFromFreeWeekend(appId); });
                bool isInst = SafeBool(delegate { return client.SteamApps008.IsAppInstalled(appId); });

                string name = null;
                try { name = client.SteamApps001.GetAppData(appId, "name"); } catch (Exception) { }
                if (isSub) sub++;
                if (isFam) fam++;
                if (isFree) free++;
                if (isInst) inst++;
                if (string.IsNullOrEmpty(name) == false) named++;
                if (isSub == false && isFam == false && isFree == false && isInst == false) notInLib++;
                sb.AppendLine("lib " + id
                    + " sub=" + (isSub ? "1" : "0")
                    + " fam=" + (isFam ? "1" : "0")
                    + " free=" + (isFree ? "1" : "0")
                    + " inst=" + (isInst ? "1" : "0")

                    + " name=" + (name ?? "<null>"));
            }
            sb.AppendLine("summary candidates=" + candidates.Count + " sub=" + sub + " fam=" + fam + " free=" + free + " inst=" + inst + " named=" + named + " notInLibrary=" + notInLib + " ms=" + watch.ElapsedMilliseconds);

            try { client.Dispose(); } catch (Exception) { }
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-family.txt"), sb.ToString(), Encoding.UTF8);
        }

        /// <summary>读 userdata/*/config/localconfig.vdf 里 Software/Valve/Steam/apps 的 appid 集合。</summary>
        private static List<long> ReadLocalConfigApps(string steamPath)
        {
            var result = new List<long>();
            string userdata = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(userdata) == false) return result;
            foreach (string dir in Directory.GetDirectories(userdata))
            {
                string cfg = Path.Combine(dir, "config", "localconfig.vdf");
                if (File.Exists(cfg) == false) continue;
                try
                {
                    string text = File.ReadAllText(cfg, Encoding.UTF8);
                    int idx = text.IndexOf("\"apps\"", StringComparison.Ordinal);
                    if (idx < 0) continue;
                    // 只取 apps { 之后到下一个同级 } 之间的 "appid" { ... }
                    int depth = 0;
                    int start = -1;
                    for (int i = idx; i < text.Length; i++)
                    {
                        char c = text[i];
                        if (c == '{') { depth++; if (depth == 1) start = i + 1; }
                        else if (c == '}')
                        {
                            depth--;
                            if (depth == 0) break;
                        }
                    }
                    if (start < 0) continue;
                    int end = -1;
                    depth = 1;
                    for (int i = start; i < text.Length; i++)
                    {
                        char c = text[i];
                        if (c == '{') depth++;
                        else if (c == '}')
                        {
                            depth--;
                            if (depth == 0) { end = i; break; }
                        }
                    }
                    if (end < 0) continue;
                    string block = text.Substring(start, end - start);
                    var rx = new System.Text.RegularExpressions.Regex("\"(\\d{1,10})\"\\s*\\{");
                    foreach (System.Text.RegularExpressions.Match m in rx.Matches(block))
                    {
                        long id;
                        if (long.TryParse(m.Groups[1].Value, out id) && id > 0) result.Add(id);
                    }
                }
                catch (Exception) { }
            }
            return result;
        }

        private static bool SafeBool(Func<bool> call)
        {
            try { return call(); } catch (Exception) { return false; }
        }

        public static void Run()
        {
            var sb = new StringBuilder();
            var watch = Stopwatch.StartNew();

            string source;
            List<GameCatalog.CatalogEntry> catalog = GameCatalog.Load(out source);
            sb.AppendLine("catalog source=" + source + " count=" + (catalog == null ? -1 : catalog.Count) + " ms=" + watch.ElapsedMilliseconds);

            var client = new SAM.API.Client();
            try
            {
                client.Initialize(0);
                sb.AppendLine("client initialized appid=" + client.SteamUtils.GetAppId());
            }
            catch (Exception e)
            {
                sb.AppendLine("client init failed " + e.GetType().Name + ": " + e.Message);
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-catalog.txt"), sb.ToString(), Encoding.UTF8);
                return;
            }

            client.RunCallbacks(false);

            if (catalog != null)
            {
                watch.Restart();
                var owned = new List<GameCatalog.CatalogEntry>();
                foreach (var entry in catalog)
                {
                    bool ok;
                    try { ok = client.SteamApps008.IsSubscribedApp((uint)entry.Id); }
                    catch (Exception) { ok = false; }
                    if (ok) owned.Add(entry);
                }
                sb.AppendLine("subscribed=" + owned.Count + " checkMs=" + watch.ElapsedMilliseconds);

                watch.Restart();
                int named = 0;
                var missing = new StringBuilder();
                foreach (var entry in owned)
                {
                    string name = null;
                    try { name = client.SteamApps001.GetAppData((uint)entry.Id, "name"); }
                    catch (Exception) { }
                    if (string.IsNullOrEmpty(name) == false) named++;
                    else if (missing.Length < 600) missing.Append(entry.Id).Append(',');
                }
                sb.AppendLine("named=" + named + " nameMs=" + watch.ElapsedMilliseconds);
                sb.AppendLine("missingNames=" + missing);

                foreach (var entry in owned)
                {
                    string name = null;
                    try { name = client.SteamApps001.GetAppData((uint)entry.Id, "name"); } catch (Exception) { }
                    sb.AppendLine("owned " + entry.Id + " [" + entry.Type + "] " + (name ?? "<null>"));
                }
            }

            try { client.Dispose(); } catch (Exception) { }
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-catalog.txt"), sb.ToString(), Encoding.UTF8);
        }
    }
}
