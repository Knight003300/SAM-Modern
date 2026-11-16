using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SAM.Modern
{
    /// <summary>诊断：统计 Steam 目录条目、本账号拥有数量与耗时。</summary>
    internal static class CatalogProbe
    {
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
