using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SAM.Modern
{
    /// <summary>单个游戏的成就 / 统计管理界面。</summary>
    internal sealed class GameForm : WebShell
    {
        private readonly SteamHost _host;
        private readonly long _appId;
        private readonly Timer _pumpTimer;
        private readonly SAM.API.Callbacks.UserStatsReceived _statsCallback;
        private readonly List<AchievementDef> _definitions = new List<AchievementDef>();
        private readonly List<StatDefinition> _statDefinitions = new List<StatDefinition>();
        private readonly Dictionary<string, AchievementDef> _definitionById =
            new Dictionary<string, AchievementDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, StatDefinition> _statById =
            new Dictionary<string, StatDefinition>(StringComparer.Ordinal);

        private string _gameName;
        private readonly string _language;
        private bool _schemaLoaded;
        private bool _requesting;

        // 成就架构在窗口创建阶段就后台预读，等 Steam 回调回来时通常已经好了，
        // 省掉「统计数据已到、还在解析 schema」的那几百毫秒。
        private readonly Task<SchemaLoader.Preloaded> _schemaPreload;

        // 全服解锁百分比（稀有度）：成就 id -> 0-100 的百分比
        private readonly Dictionary<string, float> _rarity = new Dictionary<string, float>(StringComparer.Ordinal);

        // 随机解锁（按随机间隔逐个解锁，Steam 才会为每个成就记录不同的真实时间）
        private readonly Timer _unlockTimer;
        private readonly List<string> _unlockQueue = new List<string>();
        private readonly Random _unlockRandom = new Random();
        private int _unlockIndex;
        private int _unlockMinGap = 15;
        private int _unlockMaxGap = 45;
        private bool _unlockRunning;

        private const int MaxUnlockGap = 7 * 24 * 60 * 60;

        /// <summary>稀有成就的默认判定线：全服解锁率低于 5%。</summary>
        private const double DefaultRarePercent = 5.0;

        public GameForm(SteamHost host, long appId)
            : base("SAM Modern  ·  成就管理")
        {
            Log.Write("GameForm ctor begin appId=" + appId);
            this._host = host;
            this._appId = appId;
            this._language = host.GameLanguage;
            Log.Write("GameForm language=" + this._language);
            this._gameName = host.GetAppData((uint)appId, "name");
            if (string.IsNullOrEmpty(this._gameName)) this._gameName = "App " + appId;
            Log.Write("GameForm name=" + this._gameName);

            this._statsCallback = host.Client.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsReceived>();
            this._statsCallback.OnRun += this.OnUserStatsReceived;
            Log.Write("GameForm callback registered");

            this._pumpTimer = new Timer();
            this._pumpTimer.Interval = 250;
            this._pumpTimer.Tick += delegate { this._host.Pump(); };
            this._pumpTimer.Start();

            this._unlockTimer = new Timer();
            this._unlockTimer.Tick += this.OnUnlockTick;

            string language = this._language;
            long appIdForSchema = appId;
            this._schemaPreload = Task.Run(delegate { return SchemaLoader.Preload(appIdForSchema, language); });

            Log.Write("GameForm ctor end");
        }

        protected override void OnPageReady()
        {
            // 上次的快照直接塞进 ready 事件里一起发：页面只收一条消息就能画出完整界面。
            // 拆成两条消息时，第一条 ready 会让页面先渲染「0 个成就 / 转圈」的空骨架，
            // 第二条才把内容填进去，中间那一两帧就是切换时看到的闪烁。
            string cachedJson = this.LoadGameCacheJson();

            var b = new JsonBuilder();
            b.BeginObject()
                .Name("ev").Str("ready")
                .Name("mode").Str("game")
                .Name("appId").Num(this._appId)
                .Name("gameName").Str(this._gameName)
                .Name("steamId").Num((long)this._host.SteamId)
                .Name("language").Str(this._language);
            if (string.IsNullOrEmpty(cachedJson) == false) b.Name("cachedGame").Raw(cachedJson);
            b.EndObject();
            this.Channel.Send(b);
            if (string.IsNullOrEmpty(cachedJson) == false)
            {
                Log.Write("ready with cached game: " + cachedJson.Length + " bytes");
            }

            // 随后再向 Steam 要新数据，回来时整表覆盖。
            this.Retrieve();
        }

        private void Retrieve()
        {
            if (this._requesting) return;
            this._requesting = true;
            this.Channel.Status("正在请求 Steam 统计数据…", true);
            // 先让 Steam 去取全服成就百分比，等统计数据回来时百分比通常已经就绪
            try
            {
                this._host.Client.SteamUserStats.RequestGlobalAchievementPercentages((uint)this._appId);
            }
            catch (Exception error)
            {
                Log.Write("RequestGlobalAchievementPercentages failed", error);
            }

            try
            {
                this._host.Client.SteamUserStats.RequestUserStats(this._host.SteamId);
            }
            catch (Exception error)
            {
                Log.Write("RequestUserStats failed", error);
                this._requesting = false;
                this.Channel.Error("请求失败", "无法向 Steam 请求统计数据：" + error.Message);
            }
        }

        private void OnUserStatsReceived(SAM.API.Types.UserStatsReceived data)
        {
            this._requesting = false;

            if (data.Result != 1)
            {
                string text = data.Result == 2
                    ? "发生了一般性错误 —— 这通常意味着你并不拥有该游戏。"
                    : "错误代码 " + data.Result.ToString(CultureInfo.InvariantCulture);
                Log.Write("UserStatsReceived result=" + data.Result);
                this.Channel.Status("获取统计数据失败。", false);
                this.Channel.Error("获取统计数据失败", "无法获取该游戏的统计数据。" + Environment.NewLine + text);
                return;
            }

            if (this._schemaLoaded == false)
            {
                SchemaLoader.Preloaded preloaded = null;
                try
                {
                    if (this._schemaPreload != null) preloaded = this._schemaPreload.Result;
                }
                catch (Exception error)
                {
                    Log.Write("schema preload failed", error);
                }

                List<AchievementDef> achievements = preloaded == null ? null : preloaded.Achievements;
                List<StatDefinition> stats = preloaded == null ? null : preloaded.Stats;
                bool ok = preloaded != null && preloaded.Ok;
                if (ok == false)
                {
                    ok = SchemaLoader.Load(this._appId, this._language, out achievements, out stats);
                }
                if (ok == false)
                {
                    this.Channel.Status("读取成就架构失败。", false);
                    this.Channel.Error("读取成就架构失败",
                        "找不到或无法解析该游戏的成就架构文件。" + Environment.NewLine +
                        "请先在 Steam 中启动一次该游戏，然后重新尝试。" + Environment.NewLine + Environment.NewLine +
                        SchemaLoader.SchemaPath(this._appId));
                    return;
                }

                this._definitions.Clear();
                this._definitions.AddRange(achievements);
                this._statDefinitions.Clear();
                this._statDefinitions.AddRange(stats);
                this._definitionById.Clear();
                foreach (var def in this._definitions) this._definitionById[def.Id] = def;
                this._statById.Clear();
                foreach (var def in this._statDefinitions) this._statById[def.Id] = def;
                this._schemaLoaded = true;
            }

            this.LoadRarity();
            this.PushGame();
        }

        /// <summary>读取每个成就的全服解锁百分比（Steam 的 GetAchievementAchievedPercent）。</summary>
        private void LoadRarity()
        {
            this._rarity.Clear();
            var api = this._host.Client.SteamUserStats;
            int ok = 0;
            foreach (var def in this._definitions)
            {
                float percent;
                try
                {
                    if (api.GetAchievementAchievedPercent(def.Id, out percent) && percent > 0f)
                    {
                        this._rarity[def.Id] = percent;
                        ok++;
                    }
                }
                catch (Exception error)
                {
                    Log.Write("GetAchievementAchievedPercent failed: " + def.Id, error);
                }
            }
            Log.Write("rarity loaded: " + ok + "/" + this._definitions.Count);
        }

        /// <summary>判断某个成就是否稀有（全服解锁率低于阈值）。读不到百分比的一律不算稀有。</summary>
        private bool IsRare(string achievementId, double rarePct)
        {
            float percent;
            if (this._rarity.TryGetValue(achievementId, out percent) == false)
            {
                float live;
                try
                {
                    if (this._host.Client.SteamUserStats.GetAchievementAchievedPercent(achievementId, out live) == false)
                    {
                        return false;
                    }
                }
                catch (Exception error)
                {
                    Log.Write("GetAchievementAchievedPercent failed: " + achievementId, error);
                    return false;
                }
                this._rarity[achievementId] = live;
                percent = live;
            }
            return percent > 0f && percent < rarePct;
        }

        private static string FormatPercent(double value)
        {
            if (value >= 10) return value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            return value.ToString("0.##", CultureInfo.InvariantCulture) + "%";
        }

        private void PushGame()
        {
            var api = this._host.Client.SteamUserStats;
            var b = new JsonBuilder();
            b.BeginObject().Name("ev").Str("game").Name("appId").Num(this._appId).Name("name").Str(this._gameName);
            WriteCapsules(b, this._appId);

            int unlocked = 0;
            var achievements = new List<string>();
            foreach (var def in this._definitions)
            {
                bool achieved = false;
                uint unlockTime = 0;
                try { api.GetAchievementAndUnlockTime(def.Id, out achieved, out unlockTime); }
                catch (Exception error) { Log.Write("GetAchievementAndUnlockTime failed: " + def.Id, error); }
                if (achieved) unlocked++;

                string normal = this.IconUrl(def.IconNormal);
                string locked = this.IconUrl(def.IconLocked);
                if (locked == null) locked = normal;

                float percent;
                double rarity = this._rarity.TryGetValue(def.Id, out percent) ? percent : -1.0;

                var item = new JsonBuilder();
                item.BeginObject()
                    .Name("id").Str(def.Id)
                    .Name("name").Str(def.Name)
                    .Name("desc").Str(def.Description ?? string.Empty)
                    .Name("icon").Str(normal)
                    .Name("iconLocked").Str(locked)
                    .Name("achieved").Bool(achieved)
                    .Name("unlockTime").Num(achieved && unlockTime > 0 ? (long)unlockTime : 0)
                    .Name("hidden").Bool(def.IsHidden)
                    .Name("protect").Bool((def.Permission & 3) != 0)
                    .Name("rarity").Num(rarity)
                    .EndObject();
                achievements.Add(item.ToString());
            }

            var stats = new List<string>();
            foreach (var def in this._statDefinitions)
            {
                double value = 0;
                if (def.IsFloat)
                {
                    float f;
                    try { if (api.GetStatValue(def.Id, out f)) value = f; }
                    catch (Exception error) { Log.Write("GetStatValue(float) failed: " + def.Id, error); }
                }
                else
                {
                    int i;
                    try { if (api.GetStatValue(def.Id, out i)) value = i; }
                    catch (Exception error) { Log.Write("GetStatValue(int) failed: " + def.Id, error); }
                }

                var item = new JsonBuilder();
                item.BeginObject()
                    .Name("id").Str(def.Id)
                    .Name("name").Str(def.Name)
                    .Name("value").Num(value)
                    .Name("isFloat").Bool(def.IsFloat)
                    .Name("min").Num(def.IsFloat ? 0 : (long)def.MinValue)
                    .Name("max").Num(def.IsFloat ? 0 : (long)def.MaxValue)
                    .Name("incrementOnly").Bool(def.IncrementOnly)
                    .Name("protect").Bool(def.IsProtected)
                    .EndObject();
                stats.Add(item.ToString());
            }

            b.Name("achievements").BeginArray();
            foreach (var item in achievements) b.Raw(item);
            b.EndArray();

            b.Name("stats").BeginArray();
            foreach (var item in stats) b.Raw(item);
            b.EndArray();

            b.Name("summary").BeginObject()
                .Name("total").Num(this._definitions.Count)
                .Name("unlocked").Num(unlocked)
                .EndObject()
                .EndObject();

            string json = b.ToString();
            this.Channel.Send(json);
            this.SaveGameCache(json);
            this.Channel.Status("已载入 " + this._definitions.Count + " 个成就、"
                + this._statDefinitions.Count + " 项统计。", false);
        }

        /// <summary>每个游戏的成就 / 统计快照文件（按 appId 分文件）。</summary>
        private static string GameCachePath(long appId)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache",
                "game-" + appId.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        private void SaveGameCache(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                string path = GameCachePath(this._appId);
                string directory = Path.GetDirectoryName(path);
                if (Directory.Exists(directory) == false) Directory.CreateDirectory(directory);
                // 先写临时文件再改名：另一个进程可能正在读，改名保证它读到的永远是完整文件。
                string temp = path + ".tmp";
                File.WriteAllText(temp, json, Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                Log.Write("game cache saved: " + json.Length + " bytes");
            }
            catch (Exception error)
            {
                Log.Write("game cache save failed", error);
            }
        }

        /// <summary>读取上次成功推送的游戏数据快照；没有或不可用时返回 null。</summary>
        private string LoadGameCacheJson()
        {
            try
            {
                string path = GameCachePath(this._appId);
                if (File.Exists(path) == false) return null;
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(json) || json.Length < 2 || json[0] != '{') return null;

                // 文件名已按 appId 区分，这里再校验一次，避免陈旧文件串味。
                var probe = JsonParser.ParseObject(json);
                if (probe == null) return null;
                if ((long)JsonParser.GetNumber(probe, "appId", -1) != this._appId) return null;
                return json;
            }
            catch (Exception error)
            {
                Log.Write("game cache load failed", error);
                return null;
            }
        }

        private string IconUrl(string icon)
        {
            if (string.IsNullOrEmpty(icon)) return null;
            return "https://cdn.steamstatic.com/steamcommunity/public/images/apps/"
                + this._appId.ToString(CultureInfo.InvariantCulture) + "/" + icon;
        }

        protected override void OnCommand(string cmd, Dictionary<string, object> message)
        {
            switch (cmd)
            {
                case "game/back":
                    this.RelayTo(0);
                    return;
                case "game/refresh":
                    this.Retrieve();
                    return;
                case "game/store":
                    this.Store(message);
                    return;
                case "game/reset":
                    this.Reset(JsonParser.GetBool(message, "resetAchievements", false));
                    return;
                case "game/unlockAll":
                    this.BulkApply(true);
                    return;
                case "game/lockAll":
                    this.BulkApply(false);
                    return;
                case "game/invertAll":
                    this.BulkInvert();
                    return;
                case "game/unlockRandom":
                    this.StartRandomUnlock(message);
                    return;
                case "game/unlockRandom/cancel":
                    this.CancelRandomUnlock(true);
                    return;
            }
            base.OnCommand(cmd, message);
        }

        private void BulkApply(bool achieve)
        {
            int changed = 0;
            var api = this._host.Client.SteamUserStats;
            foreach (var def in this._definitions)
            {
                if ((def.Permission & 3) != 0) continue;
                bool current = false;
                try { if (api.GetAchievement(def.Id, out current) == false) continue; }
                catch (Exception error) { Log.Write("GetAchievement failed: " + def.Id, error); continue; }
                if (current == achieve) continue;
                try { api.SetAchievement(def.Id, achieve); changed++; }
                catch (Exception error) { Log.Write("SetAchievement failed: " + def.Id, error); }
            }

            if (changed == 0)
            {
                this.Channel.Toast("info", "没有需要修改的成就", "所有可修改的成就已经是目标状态。");
                this.PushGame();
                return;
            }

            this.CommitStore(changed, 0);
        }

        private void BulkInvert()
        {
            int changed = 0;
            var api = this._host.Client.SteamUserStats;
            foreach (var def in this._definitions)
            {
                if ((def.Permission & 3) != 0) continue;
                bool current = false;
                try { if (api.GetAchievement(def.Id, out current) == false) continue; }
                catch (Exception error) { Log.Write("GetAchievement failed: " + def.Id, error); continue; }
                try { api.SetAchievement(def.Id, current == false); changed++; }
                catch (Exception error) { Log.Write("SetAchievement failed: " + def.Id, error); }
            }

            if (changed == 0)
            {
                this.Channel.Toast("info", "没有可以反选的成就", "该游戏没有可修改的成就。");
                this.PushGame();
                return;
            }

            this.CommitStore(changed, 0);
        }

        /* ---------------------------------------------------------------------
           随机解锁：Steam 不提供写入历史解锁时间的接口，唯一能让成就拥有
           「不同解锁时间」的办法是按真实时间逐个解锁 —— 每解锁一个就保存一次，
           Steam 便以当前真实时间分别记录它们。
           --------------------------------------------------------------------- */

        private void StartRandomUnlock(Dictionary<string, object> message)
        {
            if (this._unlockRunning)
            {
                this.Channel.Toast("info", "已经有一轮随机解锁在进行",
                    "请等待当前这一轮结束，或先点击「停止」。");
                return;
            }

            if (this._schemaLoaded == false)
            {
                this.Channel.Toast("warn", "数据还没准备好", "请等待成就数据加载完成后再试。");
                return;
            }

            int count = (int)Math.Round(JsonParser.GetNumber(message, "count", 0));
            int minGap = (int)Math.Round(JsonParser.GetNumber(message, "minGap", 15));
            int maxGap = (int)Math.Round(JsonParser.GetNumber(message, "maxGap", 45));
            int delay = (int)Math.Round(JsonParser.GetNumber(message, "delay", 5));
            bool rareOnly = JsonParser.GetBool(message, "rareOnly", false);
            double rarePct = JsonParser.GetNumber(message, "rarePct", DefaultRarePercent);
            // 解锁顺序：0 = 随机（默认），1 = 稀有度低→高（常见先），2 = 稀有度高→低（稀有先）
            int order = (int)Math.Round(JsonParser.GetNumber(message, "order", 0));
            if (order < 0) order = 0;
            if (order > 2) order = 2;

            // 自定义间隔可以按「天」描述，所以上限放宽到 7 天
            if (minGap < 1) minGap = 1;
            if (minGap > MaxUnlockGap) minGap = MaxUnlockGap;
            if (maxGap < minGap) maxGap = minGap;
            if (maxGap > MaxUnlockGap) maxGap = MaxUnlockGap;
            if (delay < 0) delay = 0;
            if (delay > 3600) delay = 3600;
            if (rarePct <= 0 || rarePct > 100) rarePct = DefaultRarePercent;

            // 前端可以点名要解锁哪些成就（「指定成就」模式）。点名时按给定顺序执行，
            // 既不按数量截断，也不做稀有度过滤 —— 用户已经自己挑好了。
            var explicitIds = new List<string>();
            object rawExplicit;
            if (message.TryGetValue("achievements", out rawExplicit))
            {
                var items = rawExplicit as List<object>;
                if (items != null)
                {
                    foreach (object item in items)
                    {
                        string id = item as string;
                        if (string.IsNullOrEmpty(id) == false && explicitIds.Contains(id) == false)
                        {
                            explicitIds.Add(id);
                        }
                    }
                }
            }

            var api = this._host.Client.SteamUserStats;
            var candidates = new List<string>();
            if (explicitIds.Count > 0)
            {
                foreach (string id in explicitIds)
                {
                    AchievementDef def;
                    if (this._definitionById.TryGetValue(id, out def) == false) continue;
                    if ((def.Permission & 3) != 0) continue;
                    bool current = false;
                    try { if (api.GetAchievement(id, out current) == false) continue; }
                    catch (Exception error) { Log.Write("GetAchievement failed: " + id, error); continue; }
                    if (current) continue;
                    candidates.Add(id);
                }
                if (candidates.Count == 0)
                {
                    this.Channel.Toast("info", "这些成就都不能解锁",
                        "选中的成就都已经解锁了，或者属于受保护的成就。");
                    return;
                }
            }
            else
            {
                foreach (var def in this._definitions)
                {
                    if ((def.Permission & 3) != 0) continue;
                    if (rareOnly && this.IsRare(def.Id, rarePct) == false) continue;
                    bool current = false;
                    try { if (api.GetAchievement(def.Id, out current) == false) continue; }
                    catch (Exception error) { Log.Write("GetAchievement failed: " + def.Id, error); continue; }
                    if (current) continue;
                    candidates.Add(def.Id);
                }

                if (candidates.Count == 0)
                {
                    if (rareOnly)
                    {
                        this.Channel.Toast("info", "没有可解锁的稀有成就",
                            "稀有成就（全服解锁率低于 " + FormatPercent(rarePct) + "）都已经解锁了，"
                            + "或者这个游戏没有稀有成就。");
                    }
                    else
                    {
                        this.Channel.Toast("info", "没有可解锁的成就", "该游戏所有可修改的成就都已经解锁了。");
                    }
                    return;
                }

                if (order == 0)
                {
                    // 顺序也打乱，看起来才像「随机」
                    for (int i = candidates.Count - 1; i > 0; i--)
                    {
                        int j = this._unlockRandom.Next(i + 1);
                        string tmp = candidates[i];
                        candidates[i] = candidates[j];
                        candidates[j] = tmp;
                    }
                }
                else
                {
                    // 按全服解锁率排：order=1 低→高（常见先、越稀有越靠后），order=2 反过来。
                    // 读不到百分比的排到最后 —— 否则它们会莫名其妙抢在最前面。
                    var percents = new Dictionary<string, float>(StringComparer.Ordinal);
                    foreach (string id in candidates)
                    {
                        float percent = -1f;
                        try
                        {
                            if (api.GetAchievementAchievedPercent(id, out percent) == false) percent = -1f;
                        }
                        catch (Exception error)
                        {
                            Log.Write("GetAchievementAchievedPercent failed: " + id, error);
                            percent = -1f;
                        }
                        percents[id] = percent;
                    }
                    // order=1「常见 → 稀有」：全服解锁率高的（常见）先解锁，百分比要从大到小；
                    // order=2「稀有 → 常见」反过来。别被「稀有度」这三个字带偏。
                    bool ascending = order == 2;
                    candidates.Sort(delegate(string a, string b)
                    {
                        float pa, pb;
                        if (percents.TryGetValue(a, out pa) == false) pa = -1f;
                        if (percents.TryGetValue(b, out pb) == false) pb = -1f;
                        bool ka = pa > 0f;
                        bool kb = pb > 0f;
                        if (ka != kb) return ka ? -1 : 1;
                        if (ka == false) return 0;
                        return ascending ? pa.CompareTo(pb) : pb.CompareTo(pa);
                    });
                }
                if (count > 0 && count < candidates.Count)
                {
                    candidates.RemoveRange(count, candidates.Count - count);
                }
            }

            this._unlockQueue.Clear();
            this._unlockQueue.AddRange(candidates);
            this._unlockIndex = 0;
            this._unlockMinGap = minGap;
            this._unlockMaxGap = maxGap;
            this._unlockRunning = true;

            Log.Write("random unlock start: " + this._unlockQueue.Count + " achievements"
                + (explicitIds.Count > 0 ? " (explicit " + explicitIds.Count + ")" : "")
                + ", gap " + minGap + "-" + maxGap + "s, first delay " + delay + "s"
                + (rareOnly ? ", rare only <" + FormatPercent(rarePct) : "")
                + (order == 0 ? "" : (order == 1 ? ", order common-first" : ", order rare-first")));

            this.SendUnlockRun(delay, null, 0);
            string orderText = order == 0 ? ""
                : (order == 1 ? "（按稀有度从常见到稀有排序）" : "（按稀有度从稀到常见排序）");
            this.Channel.Status((explicitIds.Count > 0 ? "指定解锁已开始：共 " : "随机解锁已开始：共 ")
                + this._unlockQueue.Count
                + (explicitIds.Count > 0 ? " 个指定成就" : (rareOnly ? " 个稀有成就" : " 个成就")) + orderText + "，间隔 "
                + FormatGap(minGap) + " – " + FormatGap(maxGap) + "，"
                + (delay > 0 ? "约 " + FormatGap(delay) + "后解锁第一个。" : "正在解锁第一个…"), true);

            this.ScheduleUnlock(delay);
        }

        private static string FormatGap(int seconds)
        {
            if (seconds < 60) return seconds + " 秒";
            if (seconds < 3600)
            {
                int m = seconds / 60;
                int rs = seconds % 60;
                return rs > 0 ? m + " 分 " + rs + " 秒" : m + " 分钟";
            }
            if (seconds < 86400)
            {
                int h = seconds / 3600;
                int rm = (seconds % 3600) / 60;
                return rm > 0 ? h + " 小时 " + rm + " 分" : h + " 小时";
            }
            int d = seconds / 86400;
            int rh = (seconds % 86400) / 3600;
            return rh > 0 ? d + " 天 " + rh + " 小时" : d + " 天";
        }

        private void ScheduleUnlock(int seconds)
        {
            this._unlockTimer.Stop();
            if (seconds < 1) seconds = 1;
            this._unlockTimer.Interval = seconds * 1000;
            this._unlockTimer.Start();
        }

        private void OnUnlockTick(object sender, EventArgs e)
        {
            this._unlockTimer.Stop();
            if (this._unlockRunning == false) return;

            if (this._unlockIndex >= this._unlockQueue.Count)
            {
                this.FinishRandomUnlock();
                return;
            }

            string id = this._unlockQueue[this._unlockIndex];
            var api = this._host.Client.SteamUserStats;

            bool ok;
            try { ok = api.SetAchievement(id, true); }
            catch (Exception error) { Log.Write("random unlock SetAchievement failed: " + id, error); ok = false; }

            if (ok)
            {
                // 每一个都单独保存一次，Steam 才会分别记下当前时间
                try { api.StoreStats(); }
                catch (Exception error) { Log.Write("random unlock StoreStats failed: " + id, error); }
            }

            this._unlockIndex++;

            uint unlockTime = 0;
            if (ok)
            {
                bool achieved = false;
                try { api.GetAchievementAndUnlockTime(id, out achieved, out unlockTime); }
                catch (Exception error) { Log.Write("random unlock read back failed: " + id, error); }
                if (achieved == false) unlockTime = 0;
            }

            bool finished = this._unlockIndex >= this._unlockQueue.Count;
            int next = finished ? 0 : this._unlockRandom.Next(this._unlockMinGap, this._unlockMaxGap + 1);

            Log.Write("random unlock " + this._unlockIndex + "/" + this._unlockQueue.Count
                + " id=" + id + " ok=" + ok + " unlockTime=" + unlockTime + " next=" + next + "s");

            this.SendUnlockRun(next, id, unlockTime);

            if (finished)
            {
                this.FinishRandomUnlock();
                return;
            }

            this.Channel.Status("随机解锁进行中：" + this._unlockIndex + " / " + this._unlockQueue.Count
                + "，下一个约 " + FormatGap(next) + "后解锁。", true);
            this.ScheduleUnlock(next);
        }

        private void SendUnlockRun(int nextSeconds, string achievementId, uint unlockTime)
        {
            var b = new JsonBuilder();
            b.BeginObject()
                .Name("ev").Str("unlockRun")
                .Name("active").Bool(true)
                .Name("done").Num(this._unlockIndex)
                .Name("total").Num(this._unlockQueue.Count)
                .Name("next").Num(nextSeconds);
            if (achievementId != null)
            {
                b.Name("id").Str(achievementId);
                b.Name("unlockTime").Num((long)unlockTime);
            }
            b.EndObject();
            this.Channel.Send(b);
        }

        private void FinishRandomUnlock()
        {
            this._unlockTimer.Stop();
            bool wasRunning = this._unlockRunning;
            int done = this._unlockIndex;
            int total = this._unlockQueue.Count;
            this._unlockRunning = false;
            this._unlockQueue.Clear();
            this._unlockIndex = 0;

            var b = new JsonBuilder();
            b.BeginObject()
                .Name("ev").Str("unlockRun")
                .Name("active").Bool(false)
                .Name("finished").Bool(true)
                .Name("done").Num(done)
                .Name("total").Num(total)
                .EndObject();
            this.Channel.Send(b);

            if (wasRunning)
            {
                Log.Write("random unlock finished: " + done + " achievements");
                this.Channel.Toast("success", "随机解锁完成",
                    "已按随机间隔解锁 " + done + " 个成就，每个成就的解锁时间都不相同。");
            }

            this.PushGame();
        }

        private void CancelRandomUnlock(bool notify)
        {
            if (this._unlockRunning == false) return;

            this._unlockTimer.Stop();
            this._unlockRunning = false;
            int done = this._unlockIndex;
            int total = this._unlockQueue.Count;
            this._unlockQueue.Clear();
            this._unlockIndex = 0;

            var b = new JsonBuilder();
            b.BeginObject()
                .Name("ev").Str("unlockRun")
                .Name("active").Bool(false)
                .Name("cancelled").Bool(true)
                .Name("done").Num(done)
                .Name("total").Num(total)
                .EndObject();
            this.Channel.Send(b);

            Log.Write("random unlock cancelled at " + done + "/" + total);

            if (notify)
            {
                this.Channel.Toast("info", "已停止随机解锁",
                    "本次共解锁 " + done + " 个成就，其余成就保持原状。");
            }

            this.PushGame();
        }

        private void Store(Dictionary<string, object> message)
        {
            var achievementChanges = JsonParser.GetObject(message, "achievements");
            var statChanges = JsonParser.GetObject(message, "stats");

            var api = this._host.Client.SteamUserStats;
            int achievements = 0;
            int stats = 0;

            if (achievementChanges != null)
            {
                foreach (var pair in achievementChanges)
                {
                    AchievementDef def;
                    if (this._definitionById.TryGetValue(pair.Key, out def) == false) continue;

                    bool target = pair.Value is bool ? (bool)pair.Value
                        : (pair.Value is double ? (double)pair.Value != 0
                        : string.Equals(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase));

                    bool current = false;
                    try { api.GetAchievement(def.Id, out current); }
                    catch (Exception error) { Log.Write("GetAchievement failed: " + def.Id, error); continue; }
                    if (current == target) continue;

                    if ((def.Permission & 3) != 0)
                    {
                        this.Channel.Toast("error", "受保护的成就",
                            "“" + def.Name + "”是受保护的成就，无法用本工具修改。");
                        continue;
                    }

                    try
                    {
                        if (api.SetAchievement(def.Id, target)) achievements++;
                        else this.Channel.Toast("warn", "修改失败", "无法设置成就“" + def.Name + "”的状态。");
                    }
                    catch (Exception error)
                    {
                        Log.Write("SetAchievement failed: " + def.Id, error);
                        this.Channel.Toast("warn", "修改失败", "设置成就“" + def.Name + "”时出错：" + error.Message);
                    }
                }
            }

            if (statChanges != null)
            {
                foreach (var pair in statChanges)
                {
                    StatDefinition def;
                    if (this._statById.TryGetValue(pair.Key, out def) == false) continue;

                    double target = 0;
                    if (pair.Value is double) target = (double)pair.Value;
                    else if (pair.Value is string)
                    {
                        double parsed;
                        if (double.TryParse((string)pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) == false) continue;
                        target = parsed;
                    }
                    else if (pair.Value is bool) target = ((bool)pair.Value) ? 1 : 0;

                    if (def.IsProtected)
                    {
                        this.Channel.Toast("error", "受保护的统计",
                            "“" + def.Name + "”是受保护的统计项，无法用本工具修改。");
                        continue;
                    }

                    try
                    {
                        bool ok;
                        if (def.IsFloat) ok = api.SetStatValue(def.Id, (float)target);
                        else ok = api.SetStatValue(def.Id, (int)Math.Round(target));

                        if (ok) stats++;
                        else this.Channel.Toast("warn", "修改失败", "无法设置统计“" + def.Name + "”的值。");
                    }
                    catch (Exception error)
                    {
                        Log.Write("SetStatValue failed: " + def.Id, error);
                        this.Channel.Toast("warn", "修改失败", "设置统计“" + def.Name + "”时出错：" + error.Message);
                    }
                }
            }

            if (achievements == 0 && stats == 0)
            {
                this.Channel.Toast("info", "没有改动", "没有检测到需要保存的修改。");
                this.PushGame();
                return;
            }

            this.CommitStore(achievements, stats);
        }

        private void CommitStore(int achievements, int stats)
        {
            this.Channel.Status("正在写入 Steam…", true);
            bool ok;
            try { ok = this._host.Client.SteamUserStats.StoreStats(); }
            catch (Exception error)
            {
                Log.Write("StoreStats failed", error);
                this.Channel.Status("写入失败。", false);
                this.Channel.Error("写入失败", "向 Steam 写入数据时出错：" + error.Message);
                return;
            }

            if (ok == false)
            {
                this.Channel.Status("写入失败。", false);
                this.Channel.Error("写入失败", "Steam 拒绝了本次数据写入，请稍后重试。");
                return;
            }

            var b = new JsonBuilder();
            b.BeginObject().Name("ev").Str("stored")
                .Name("achievements").Num(achievements)
                .Name("stats").Num(stats)
                .EndObject();
            this.Channel.Send(b);

            this.Channel.Toast("success", "已保存",
                "已写入 " + achievements + " 个成就、 " + stats + " 项统计。");
            this.Retrieve();
        }

        private void Reset(bool resetAchievements)
        {
            this.Channel.Status("正在重置…", true);
            try
            {
                this._host.Client.SteamUserStats.ResetAllStats(resetAchievements);
            }
            catch (Exception error)
            {
                Log.Write("ResetAllStats failed", error);
                this.Channel.Status("重置失败。", false);
                this.Channel.Error("重置失败", "重置统计时出错：" + error.Message);
                return;
            }

            this.Channel.Toast("success", "已重置",
                resetAchievements ? "所有成就与统计已重置。" : "所有统计数据已重置（成就保持不变）。");
            this.Retrieve();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { this._unlockTimer.Stop(); this._unlockTimer.Dispose(); }
            catch (Exception) { }
            try { this._pumpTimer.Stop(); this._pumpTimer.Dispose(); }
            catch (Exception) { }
            try { this._host.Dispose(); }
            catch (Exception) { }
            base.OnFormClosed(e);
        }
    }
}
