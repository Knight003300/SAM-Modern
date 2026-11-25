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

            /// <summary>
            /// Steam 是否认为当前账号「拥有」这个 app（IsSubscribedApp）。
            /// 目录里扫出来的必然是 true；从安装清单或本机记录补出来的可能是 false ——
            /// 家庭共享、免费周末结束、已退款、或账号切换后的残留。界面会据此打一个「未在库中」标记。
            /// </summary>
            public bool Subscribed = true;
        }

        private static readonly Regex NamePattern = new Regex("\"name\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        private static readonly Regex PathPattern = new Regex("\"path\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);

        /// <summary>
        /// Steam 自己的界面组件，不是游戏：它们会出现在 localconfig / 库缓存里，
        /// 但 Steam 库从不把它们当游戏显示，列进来只会让列表变脏。
        /// </summary>
        private static readonly HashSet<long> NonGameApps = new HashSet<long> { 7, 760, 241100, 2371090 };

        /// <summary>localconfig.vdf 里 apps 段的条目：<c>"123456" { ... }</c>。</summary>
        private static readonly Regex LocalConfigAppPattern = new Regex(
            "\"(\\d{1,10})\"\\s*\\{", RegexOptions.CultureInvariant);

        /// <summary>
        /// 名字像工具/服务器/权利的条目。它们在 Steam 库里属于「软件/工具」，不是游戏，
        /// 但没有成就也没有封面，混在正式版里很吵 —— 归到「其他」分类而不是丢掉，用户仍然能点开。
        /// 只做后缀匹配，避免误伤名字里恰好带这些词的正式游戏。
        /// </summary>
        private static readonly string[] ToolSuffixes =
        {
            " Dedicated Server",
            " Authoring Tools",
            " SDK",
            " - Entitlement",
            "Linux client",
            " Steamworks Common Redistributables",
        };

        private static bool LooksLikeTool(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string suffix in ToolSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

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
                // 只有「本机有痕迹但 Steam 说没拥有」的才带这个字段，正常游戏省掉一半报文体积。
                if (game.Subscribed == false) b.Name("unowned").Bool(true);
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
                        Subscribed = parts.Length < 4 || parts[3] != "0",
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
                    lines.Add(game.Id.ToString(CultureInfo.InvariantCulture) + "\t" + game.Type + "\t" + name +
                        (game.Subscribed ? string.Empty : "\t0"));
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

        /// <summary>
        /// 本地证据：Steam 自己在磁盘上留下的「这台机器装过 / 跑过这个 app」的痕迹。
        /// 只靠 games.xml + IsSubscribedApp 会漏两类游戏：
        /// ① 目录里没有收录、但账号确实拥有的（如 Don't Starve Together、Soundpad）；
        /// ② 目录里有、但当前账号 IsSubscribedApp 为 false 的（家庭共享、免费周末结束、已退款）。
        /// 这两类都能在本地痕迹里找到，所以扫描时把它们当作补充来源。
        /// </summary>
        private sealed class LocalEvidence
        {
            /// <summary>本机任何地方出现过的 appId（库缓存、userdata、localconfig）。</summary>
            public readonly HashSet<long> Ids = new HashSet<long>();

            /// <summary>强证据：本机装过或跑过（安装清单、成就架构文件）。</summary>
            public readonly HashSet<long> Strong = new HashSet<long>();
        }

        /// <summary>Steam 库里所有可能的 steamapps 目录（主库 + libraryfolders.vdf 里登记的）。</summary>
        private static List<string> LibraryRoots(string install)
        {
            var roots = new List<string>();
            if (string.IsNullOrEmpty(install)) return roots;

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

            return roots;
        }

        /// <summary>收集本地痕迹。全部是纯文件读取，不依赖 Steam 接口，所以很快也不会失败。</summary>
        private static LocalEvidence EnumerateEvidence(string install, List<string> roots)
        {
            var evidence = new LocalEvidence();
            if (string.IsNullOrEmpty(install)) return evidence;

            // 强证据 1：成就架构文件。Steam 只在游戏真正启动过之后才会写这个文件。
            try
            {
                string stats = Path.Combine(install, "appcache", "stats");
                if (Directory.Exists(stats))
                {
                    foreach (string file in Directory.GetFiles(stats, "UserGameStatsSchema_*.bin"))
                    {
                        long id;
                        if (long.TryParse(Path.GetFileNameWithoutExtension(file).Substring("UserGameStatsSchema_".Length),
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0)
                        {
                            evidence.Strong.Add(id);
                            evidence.Ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception error) { Log.Write("evidence stats scan failed", error); }

            // 强证据 2：安装清单（已安装）。
            foreach (string root in roots)
            {
                try
                {
                    if (Directory.Exists(root) == false) continue;
                    foreach (string manifest in Directory.GetFiles(root, "appmanifest_*.acf"))
                    {
                        long id;
                        if (long.TryParse(Path.GetFileNameWithoutExtension(manifest).Substring("appmanifest_".Length),
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0)
                        {
                            evidence.Strong.Add(id);
                            evidence.Ids.Add(id);
                        }
                    }
                }
                catch (Exception error) { Log.Write("evidence manifest scan failed: " + root, error); }
            }

            // 弱证据：全机库缓存（Steam 为见过的 app 存过封面/信息就会留下一个以 appId 命名的文件）。
            try
            {
                string libraryCache = Path.Combine(install, "appcache", "librarycache");
                if (Directory.Exists(libraryCache))
                {
                    foreach (string file in Directory.GetFiles(libraryCache))
                    {
                        long id;
                        if (long.TryParse(Path.GetFileName(file), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0)
                        {
                            evidence.Ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception error) { Log.Write("evidence librarycache scan failed", error); }

            // 弱证据：每个账号的 userdata（localconfig 的 apps 段 + 每游戏成就缓存）。
            try
            {
                string userdata = Path.Combine(install, "userdata");
                if (Directory.Exists(userdata))
                {
                    foreach (string account in Directory.GetDirectories(userdata))
                    {
                        string config = Path.Combine(account, "config");

                        string localConfig = Path.Combine(config, "localconfig.vdf");
                        if (File.Exists(localConfig))
                        {
                            try
                            {
                                foreach (long id in ReadLocalConfigApps(File.ReadAllText(localConfig)))
                                {
                                    if (id > 0) evidence.Ids.Add(id);
                                }
                            }
                            catch (Exception error) { Log.Write("evidence localconfig parse failed: " + localConfig, error); }
                        }

                        string cache = Path.Combine(config, "librarycache");
                        if (Directory.Exists(cache))
                        {
                            foreach (string file in Directory.GetFiles(cache, "*.json"))
                            {
                                long id;
                                if (long.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out id) && id > 0)
                                {
                                    evidence.Ids.Add(id);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception error) { Log.Write("evidence userdata scan failed", error); }

            Log.Write("local evidence: ids=" + evidence.Ids.Count + " strong=" + evidence.Strong.Count);
            return evidence;
        }

        /// <summary>从 localconfig.vdf 的 apps 段取 appId 集合。</summary>
        private static List<long> ReadLocalConfigApps(string text)
        {
            var result = new List<long>();
            if (string.IsNullOrEmpty(text)) return result;

            int idx = text.IndexOf("\"apps\"", StringComparison.Ordinal);
            if (idx < 0) return result;

            int depth = 0;
            int start = -1;
            int end = -1;
            for (int i = idx; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{')
                {
                    depth++;
                    if (depth == 1 && start < 0) start = i;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0) { end = i; break; }
                }
            }
            if (start < 0 || end <= start) return result;

            foreach (Match m in LocalConfigAppPattern.Matches(text.Substring(start, end - start + 1)))
            {
                long id;
                if (long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0)
                {
                    if (result.Contains(id) == false) result.Add(id);
                }
            }
            return result;
        }

        /// <summary>按 Steam 官方目录（games.xml）核对订阅状态，再补上本地痕迹里发现的其他游戏。</summary>
        private List<GameEntry> EnumerateGames()
        {
            string source;
            List<GameCatalog.CatalogEntry> catalog = GameCatalog.Load(out source);
            this.ReportStatus("已读取游戏目录（" + (catalog == null ? 0 : catalog.Count) + " 项，" +
                (string.Equals(source, "network", StringComparison.Ordinal) ? "在线" : "本地缓存") + "），正在核对拥有的游戏…");

            var result = new List<GameEntry>();
            var seen = new HashSet<long>();

            // 目录里出现过的 appId → 类型（demo / mod / junk），补扫时沿用同一个分类。
            var catalogTypes = new Dictionary<long, string>();

            if (catalog != null)
            {
                int scanned = 0;
                foreach (var entry in catalog)
                {
                    scanned++;
                    if (scanned % 1000 == 0) this.SendScanProgress(result.Count);
                    if (entry.Id <= 0) continue;
                    // 分类先全量记下来：补扫出来的 appId 也要沿用目录里的 demo / mod 标记。
                    if (catalogTypes.ContainsKey(entry.Id) == false)
                    {
                        catalogTypes[entry.Id] = GameCatalog.TypeLabelKey(entry.Type);
                    }
                    if (seen.Contains(entry.Id)) continue;
                    if (this._host.IsSubscribed((uint)entry.Id) == false) continue;

                    string name = this._host.GetAppData((uint)entry.Id, "name");
                    if (string.IsNullOrEmpty(name)) continue;

                    seen.Add(entry.Id);
                    result.Add(new GameEntry
                    {
                        Id = entry.Id,
                        Name = name,
                        Type = catalogTypes[entry.Id],
                    });
                }
                Log.Write("catalog scan: " + result.Count + " owned of " + catalog.Count);
            }

            string install = SAM.API.Steam.GetInstallPath();
            List<string> roots = LibraryRoots(install);

            // 本地已安装但目录里缺失的（极少见，比如未收录的新游戏），用清单补齐。
            var supplemental = new List<long>();
            foreach (var entry in this.EnumerateInstalled(roots))
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

            // 第三轮补扫：本地痕迹里还有、但前面两轮都没进来的。
            // 判据是「Steam 说拥有」或「本机确实装过/跑过」，两者有一个就收；
            // 只有弱痕迹（库缓存里见过封面）的不收，否则列表会被别人的游戏塞满。
            var evidence = EnumerateEvidence(install, roots);
            var evidenceAdded = new List<long>();
            var evidenceUnsubscribed = new List<long>();
            foreach (long id in evidence.Ids)
            {
                if (id <= 0 || seen.Contains(id)) continue;
                if (NonGameApps.Contains(id)) continue;

                bool strong = evidence.Strong.Contains(id);
                bool subscribed = this._host.IsSubscribed((uint)id);
                if (subscribed == false && strong == false) continue;

                string name = this._host.GetAppData((uint)id, "name");
                if (string.IsNullOrEmpty(name)) name = "App " + id.ToString(CultureInfo.InvariantCulture);

                string type;
                if (catalogTypes.TryGetValue(id, out type) == false)
                {
                    type = LooksLikeTool(name) ? "junk" : "normal";
                }

                seen.Add(id);
                result.Add(new GameEntry
                {
                    Id = id,
                    Name = name,
                    Type = type,
                    Subscribed = subscribed,
                });
                evidenceAdded.Add(id);
                if (subscribed == false) evidenceUnsubscribed.Add(id);
            }
            if (evidenceAdded.Count > 0)
            {
                Log.Write("evidence added: " + string.Join(",", evidenceAdded.ConvertAll(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()));
            }
            if (evidenceUnsubscribed.Count > 0)
            {
                Log.Write("evidence added but not subscribed: " + string.Join(",", evidenceUnsubscribed.ConvertAll(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()));
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
        private List<GameEntry> EnumerateInstalled(List<string> roots)
        {
            var result = new List<GameEntry>();

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
