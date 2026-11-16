using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SAM.Modern
{
    /// <summary>美术资源定位：优先使用 Steam 本地缓存，缺失时给出一串 CDN 候选地址。</summary>
    internal static class Art
    {
        /// <summary>Steam 本地封面缓存目录（appcache\librarycache），不存在则返回 null。</summary>
        public static string LocalCacheRoot
        {
            get
            {
                try
                {
                    string install = SAM.API.Steam.GetInstallPath();
                    if (string.IsNullOrEmpty(install)) return null;
                    string root = Path.Combine(Path.Combine(install, "appcache"), "librarycache");
                    return Directory.Exists(root) ? root : null;
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>本地缓存里可以当封面的文件名，按优先顺序排列（横版在前，竖版垫后）。</summary>
        private static readonly string[] LocalNames =
        {
            "header_schinese.jpg", "header.jpg",
            "library_capsule_schinese.jpg", "library_capsule.jpg",
            "library_hero_schinese.jpg", "library_hero.jpg",
            "library_600x900_schinese.jpg", "library_600x900.jpg"
        };

        /// <summary>
        /// 封面候选地址：本地缓存里所有存在的文件（按优先级全部列出），再补齐 CDN 回落地址。
        /// 页面按顺序逐个尝试，任何一个命中都能显示封面。
        /// </summary>
        public static List<string> CapsuleCandidates(long appId)
        {
            var list = new List<string>();
            string id = appId.ToString(CultureInfo.InvariantCulture);
            string root = LocalCacheRoot;

            if (string.IsNullOrEmpty(root) == false)
            {
                string dir = Path.Combine(root, id);
                if (Directory.Exists(dir))
                {
                    foreach (string name in LocalNames)
                    {
                        if (File.Exists(Path.Combine(dir, name)) == false) continue;
                        string url = "https://steam.cache/" + id + "/" + name;
                        if (list.Contains(url) == false) list.Add(url);
                    }
                }
            }

            string[] remote =
            {
                "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/" + id + "/header.jpg",
                "https://cdn.cloudflare.steamstatic.com/steam/apps/" + id + "/header.jpg",
                "https://cdn.cloudflare.steamstatic.com/steam/apps/" + id + "/capsule_616x353.jpg",
                "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/" + id + "/library_hero.jpg",
                "https://cdn.cloudflare.steamstatic.com/steam/apps/" + id + "/library_hero.jpg",
                "https://cdn.cloudflare.steamstatic.com/steam/apps/" + id + "/library_600x900.jpg"
            };
            foreach (string url in remote)
            {
                if (list.Contains(url) == false) list.Add(url);
            }

            return list;
        }

        /// <summary>封面主地址（候选列表的第一项）。</summary>
        public static string CapsuleUrl(long appId)
        {
            var list = CapsuleCandidates(appId);
            return list.Count > 0 ? list[0] : null;
        }
    }
}
