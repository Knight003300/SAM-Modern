using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SAM.Modern
{
    /// <summary>游戏选择界面：扫描本地 Steam 库，列出可管理的游戏。</summary>
    internal sealed class PickerForm : WebShell
    {
        private sealed class GameEntry
        {
            public long Id;
            public string Name;
            public string Type = "normal";
        }

        private static readonly Regex NamePattern = new Regex("\"name\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        private static readonly Regex PathPattern = new Regex("\"path\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);

        private readonly SteamHost _host;
        private readonly Timer _pumpTimer;
        private bool _scanning;

        public PickerForm(SteamHost host)
            : base("SAM Modern  ·  游戏库")
        {
            this._host = host;

            this._pumpTimer = new Timer();
            this._pumpTimer.Interval = 500;
            this._pumpTimer.Tick += delegate { this._host.Pump(); };
            this._pumpTimer.Start();
        }

        protected override void OnPageReady()
        {
            var b = new JsonBuilder();
            b.BeginObject()
                .Name("ev").Str("ready")
                .Name("mode").Str("picker")
                .Name("appId").Num(0)
                .Name("gameName").Str(string.Empty)
                .Name("steamId").Num((long)this._host.SteamId)
                .Name("language").Str(this._host.GameLanguage)
                .EndObject();
            this.Channel.Send(b);

            this.Scan();

            if (string.IsNullOrEmpty(AutoOpenAppId) == false)
            {
                long autoId;
                if (long.TryParse(AutoOpenAppId, NumberStyles.Integer, CultureInfo.InvariantCulture, out autoId) && autoId > 0)
                {
                    Log.Write("auto-open: relaying to " + autoId);
                    this.RelayTo(autoId);
                }
            }
        }

        private void Scan()
        {
            if (this._scanning) return;
            this._scanning = true;
            this.Channel.Status("正在扫描本地 Steam 游戏库…", true);

            Task.Run(delegate
            {
                List<GameEntry> games = null;
                string failure = null;
                try { games = this.EnumerateGames(); }
                catch (Exception error) { failure = error.Message; Log.Write("scan failed", error); }

                try
                {
                    this.BeginInvoke((Action)delegate
                    {
                        this._scanning = false;
                        if (games == null)
                        {
                            this.Channel.Status("扫描失败。", false);
                            this.Channel.Error("扫描失败", "无法读取本地游戏库：" + failure);
                            return;
                        }
                        this.Publish(games);
                    });
                }
                catch (Exception) { }
            });
        }

        /// <summary>后台线程安全地把进度写到状态栏。</summary>
        private void ReportStatus(string text)
        {
            try
            {
                this.BeginInvoke((Action)delegate { this.Channel.Status(text, true); });
            }
            catch (Exception) { }
        }

        private void Publish(List<GameEntry> games)
        {
            var b = new JsonBuilder();
            b.BeginObject().Name("ev").Str("games").Name("games").BeginArray();
            foreach (var game in games)
            {
                b.BeginObject()
                    .Name("id").Num(game.Id)
                    .Name("name").Str(game.Name)
                    .Name("type").Str(game.Type);
                WriteCapsules(b, game.Id);
                b.EndObject();
            }
            b.EndArray().Name("total").Num(games.Count).EndObject();
            this.Channel.Send(b);

            this.Channel.Status("已载入 " + games.Count + " 个游戏。", false);
        }

        /// <summary>按 Steam 官方目录（games.xml）核对订阅状态，列出账号拥有的全部游戏。</summary>
        private List<GameEntry> EnumerateGames()
        {
            string source;
            List<GameCatalog.CatalogEntry> catalog = GameCatalog.Load(out source);
            this.ReportStatus("已读取游戏目录（" + (catalog == null ? 0 : catalog.Count) + " 项，" +
                (string.Equals(source, "network", StringComparison.Ordinal) ? "在线" : "本地缓存") + "），正在核对拥有的游戏…");

            var result = new List<GameEntry>();
            var seen = new HashSet<long>();

            if (catalog != null)
            {
                foreach (var entry in catalog)
                {
                    if (entry.Id <= 0) continue;
                    if (seen.Contains(entry.Id)) continue;
                    if (this._host.IsSubscribed((uint)entry.Id) == false) continue;

                    string name = this._host.GetAppData((uint)entry.Id, "name");
                    if (string.IsNullOrEmpty(name)) continue;

                    seen.Add(entry.Id);
                    result.Add(new GameEntry
                    {
                        Id = entry.Id,
                        Name = name,
                        Type = GameCatalog.TypeLabelKey(entry.Type),
                    });
                }
                Log.Write("catalog scan: " + result.Count + " owned of " + catalog.Count);
            }

            // 本地已安装但目录里缺失的（极少见，比如未收录的新游戏），用清单补齐。
            var supplemental = new List<long>();
            foreach (var entry in this.EnumerateInstalled())
            {
                if (seen.Contains(entry.Id)) continue;
                if (this._host.IsSubscribed((uint)entry.Id) == false) continue;
                seen.Add(entry.Id);
                result.Add(entry);
                supplemental.Add(entry.Id);
            }
            if (supplemental.Count > 0)
            {
                Log.Write("catalog missing, added from installs: " + string.Join(",", supplemental.ConvertAll(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()));
            }

            if (seen.Contains(480) == false && this._host.IsSubscribed(480))
            {
                result.Add(new GameEntry { Id = 480, Name = "Spacewar（测试用）", Type = "junk" });
            }

            result.Sort(delegate (GameEntry a, GameEntry b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            return result;
        }

        /// <summary>扫描本地 steamapps 清单，拿到已安装的游戏（作为目录的补充）。</summary>
        private List<GameEntry> EnumerateInstalled()
        {
            var result = new List<GameEntry>();

            string install = SAM.API.Steam.GetInstallPath();
            if (string.IsNullOrEmpty(install)) return result;

            var roots = new List<string>();
            string steamApps = Path.Combine(install, "steamapps");
            roots.Add(steamApps);

            string libraryFile = Path.Combine(steamApps, "libraryfolders.vdf");
            try
            {
                if (File.Exists(libraryFile))
                {
                    string text = File.ReadAllText(libraryFile);
                    foreach (Match m in PathPattern.Matches(text))
                    {
                        string path = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (string.IsNullOrEmpty(path)) continue;
                        string candidate = Path.Combine(path, "steamapps");
                        if (roots.Contains(candidate) == false) roots.Add(candidate);
                    }
                }
            }
            catch (Exception error) { Log.Write("libraryfolders parse failed", error); }

            foreach (string root in roots)
            {
                if (Directory.Exists(root) == false) continue;
                string[] manifests;
                try { manifests = Directory.GetFiles(root, "appmanifest_*.acf"); }
                catch (Exception error) { Log.Write("manifest scan failed: " + root, error); continue; }

                foreach (string manifest in manifests)
                {
                    string fileName = Path.GetFileNameWithoutExtension(manifest);
                    long appId;
                    if (long.TryParse(fileName.Substring("appmanifest_".Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out appId) == false) continue;

                    string name = null;
                    try
                    {
                        string text = File.ReadAllText(manifest);
                        var m = NamePattern.Match(text);
                        if (m.Success) name = m.Groups[1].Value;
                    }
                    catch (Exception error) { Log.Write("manifest read failed: " + manifest, error); }

                    if (string.IsNullOrEmpty(name)) name = this._host.GetAppData((uint)appId, "name");
                    if (string.IsNullOrEmpty(name)) continue;

                    result.Add(new GameEntry { Id = appId, Name = name });
                }
            }

            return result;
        }

        protected override void OnCommand(string cmd, Dictionary<string, object> message)
        {
            switch (cmd)
            {
                case "picker/reload":
                    this.Scan();
                    return;
                case "game/open":
                {
                    long appId = (long)JsonParser.GetNumber(message, "id", 0);
                    if (appId <= 0)
                    {
                        this.Channel.Toast("warn", "无效的游戏 ID", "请输入正确的 AppID。");
                        return;
                    }

                    this.RelayTo(appId);
                    return;
                }
            }
            base.OnCommand(cmd, message);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { this._pumpTimer.Stop(); this._pumpTimer.Dispose(); }
            catch (Exception) { }
            try { this._host.Dispose(); }
            catch (Exception) { }
            base.OnFormClosed(e);
        }
    }
}
