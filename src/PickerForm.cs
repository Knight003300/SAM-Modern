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

        /// <summary>
        /// 游戏库窗口在被接力后不退出，而是隐藏待命：游戏窗口点「返回」时直接把它唤回来。
        /// 它身上有已经扫描好的游戏列表和热着的 WebView2，唤醒只要几十毫秒，重建却要 870ms。
        /// </summary>
        protected override bool KeepAliveOnRelay { get { return true; } }

        private int _lastPublished;

        /// <summary>被唤回时把状态栏从「正在返回游戏库…」改回正常文案，否则用户会看到一个永远在加载的提示。</summary>
        protected override void OnResumed()
        {
            Log.Write("picker resumed, restoring status (" + this._lastPublished + " games)");
            this.Channel.Status("已载入 " + this._lastPublished + " 个游戏。", false);
        }
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
            // 上次的列表直接塞进 ready 事件里一起发：页面只收一条消息就能画出完整列表。
            // 拆成两条时第一条 ready 会让页面先渲染骨架屏，第二条才填内容，中间那几帧就是切换闪烁。
            var cached = LoadOwnedCache();

            var b = new JsonBuilder();
            b.BeginObject()
                .Name("ev").Str("ready")
                .Name("mode").Str("picker")
                .Name("appId").Num(0)
                .Name("gameName").Str(string.Empty)
                .Name("steamId").Num((long)this._host.SteamId)
                .Name("language").Str(this._host.GameLanguage);
            if (cached.Count > 0)
            {
                b.Name("cachedGames");
                WriteGameArray(b, cached);
            }
            b.EndObject();
            this.Channel.Send(b);
            if (cached.Count > 0)
            {
                Log.Write("ready with cached games: " + cached.Count);
                this.Channel.Status("已显示上次的 " + cached.Count + " 个游戏，正在刷新…", true);
            }

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

                if (games != null) SaveOwnedCache(games);

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
                        this.Publish(games, false);
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

        private static void WriteGameArray(JsonBuilder b, List<GameEntry> games)
        {
            b.BeginArray();
            foreach (var game in games)
            {
                b.BeginObject()
                    .Name("id").Num(game.Id)
                    .Name("name").Str(game.Name)
                    .Name("type").Str(game.Type);
                WriteCapsules(b, game.Id);
                b.EndObject();
            }
            b.EndArray();
        }

        private void Publish(List<GameEntry> games, bool cached)
        {
            var b = new JsonBuilder();
            b.BeginObject().Name("ev").Str("games").Name("cached").Bool(cached).Name("games");
            WriteGameArray(b, games);
            b.Name("total").Num(games.Count).EndObject();
            this.Channel.Send(b);
            this._lastPublished = games.Count;
            Log.Write("publish games cached=" + cached + " count=" + games.Count);

            if (cached)
            {
                this.Channel.Status("已显示上次的 " + games.Count + " 个游戏，正在刷新…", true);
            }
            else
            {
                this.Channel.Status("已载入 " + games.Count + " 个游戏。", false);
            }
        }

        /// <summary>已拥有游戏的磁盘缓存：让返回游戏库时能立刻出列表，不必等整轮目录核对。</summary>
        private static string OwnedCachePath()
        {
            try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "owned.txt"); }
            catch (Exception) { return null; }
        }

        private static List<GameEntry> LoadOwnedCache()
        {
            var result = new List<GameEntry>();
            string path = OwnedCachePath();
            if (string.IsNullOrEmpty(path)) return result;
            try
            {
                if (File.Exists(path) == false) return result;
                foreach (string line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3) continue;

                    long id;
                    if (long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id) == false) continue;
                    if (id <= 0) continue;
                    if (string.IsNullOrEmpty(parts[2])) continue;

                    result.Add(new GameEntry
                    {
                        Id = id,
                        Name = parts[2],
                        Type = string.IsNullOrEmpty(parts[1]) ? "normal" : parts[1],
                    });
                }
                Log.Write("owned cache loaded: " + result.Count);
            }
            catch (Exception error)
            {
                Log.Write("owned cache load failed", error);
            }
            return result;
        }

        private static void SaveOwnedCache(List<GameEntry> games)
        {
            string path = OwnedCachePath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder) == false) Directory.CreateDirectory(folder);

                var lines = new List<string>(games.Count);
                foreach (var game in games)
                {
                    string name = (game.Name ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                    lines.Add(game.Id.ToString(CultureInfo.InvariantCulture) + "\t" + game.Type + "\t" + name);
                }

                string temp = path + ".tmp";
                File.WriteAllLines(temp, lines.ToArray());
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                Log.Write("owned cache saved: " + games.Count);
            }
            catch (Exception error)
            {
                Log.Write("owned cache save failed", error);
            }
        }

        /// <summary>后台线程安全地把扫描进度发给页面。</summary>
        private void SendScanProgress(int found)
        {
            try
            {
                this.BeginInvoke((Action)delegate
                {
                    var b = new JsonBuilder();
                    b.BeginObject().Name("ev").Str("gamesProgress").Name("scanning").Num(found).EndObject();
                    this.Channel.Send(b);
                });
            }
            catch (Exception) { }
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
                int scanned = 0;
                foreach (var entry in catalog)
                {
                    scanned++;
                    if (scanned % 1000 == 0) this.SendScanProgress(result.Count);
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
