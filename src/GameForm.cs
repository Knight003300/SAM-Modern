using System;
using System.Collections.Generic;
using System.Globalization;
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

        // 随机解锁（按随机间隔逐个解锁，Steam 才会为每个成就记录不同的真实时间）
        private readonly Timer _unlockTimer;
        private readonly List<string> _unlockQueue = new List<string>();
        private readonly Random _unlockRandom = new Random();
        private int _unlockIndex;
        private int _unlockMinGap = 15;
        private int _unlockMaxGap = 45;
        private bool _unlockRunning;

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

            Log.Write("GameForm ctor end");
        }

        protected override void OnPageReady()
        {
            var b = new JsonBuilder();
            b.BeginObject()
                .Name("ev").Str("ready")
                .Name("mode").Str("game")
                .Name("appId").Num(this._appId)
                .Name("gameName").Str(this._gameName)
                .Name("steamId").Num((long)this._host.SteamId)
                .Name("language").Str(this._language)
                .EndObject();
            this.Channel.Send(b);

            this.Retrieve();
        }

        private void Retrieve()
        {
            if (this._requesting) return;
            this._requesting = true;
            this.Channel.Status("正在请求 Steam 统计数据…", true);
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
                List<AchievementDef> achievements;
                List<StatDefinition> stats;
                bool ok = SchemaLoader.Load(this._appId, this._language, out achievements, out stats);
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

            this.PushGame();
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

            this.Channel.Send(b);
            this.Channel.Status("已获取 " + this._definitions.Count + " 个成就、"
                + this._statDefinitions.Count + " 项统计。", false);
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

            if (minGap < 1) minGap = 1;
            if (minGap > 3600) minGap = 3600;
            if (maxGap < minGap) maxGap = minGap;
            if (maxGap > 3600) maxGap = 3600;
            if (delay < 0) delay = 0;
            if (delay > 3600) delay = 3600;

            var api = this._host.Client.SteamUserStats;
            var candidates = new List<string>();
            foreach (var def in this._definitions)
            {
                if ((def.Permission & 3) != 0) continue;
                bool current = false;
                try { if (api.GetAchievement(def.Id, out current) == false) continue; }
                catch (Exception error) { Log.Write("GetAchievement failed: " + def.Id, error); continue; }
                if (current) continue;
                candidates.Add(def.Id);
            }

            if (candidates.Count == 0)
            {
                this.Channel.Toast("info", "没有可解锁的成就", "该游戏所有可修改的成就都已经解锁了。");
                return;
            }

            // 顺序也打乱，看起来才像「随机」
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = this._unlockRandom.Next(i + 1);
                string tmp = candidates[i];
                candidates[i] = candidates[j];
                candidates[j] = tmp;
            }
            if (count > 0 && count < candidates.Count)
            {
                candidates.RemoveRange(count, candidates.Count - count);
            }

            this._unlockQueue.Clear();
            this._unlockQueue.AddRange(candidates);
            this._unlockIndex = 0;
            this._unlockMinGap = minGap;
            this._unlockMaxGap = maxGap;
            this._unlockRunning = true;

            Log.Write("random unlock start: " + this._unlockQueue.Count + " achievements, gap "
                + minGap + "-" + maxGap + "s, first delay " + delay + "s");

            this.SendUnlockRun(delay, null, 0);
            this.Channel.Status("随机解锁已开始：共 " + this._unlockQueue.Count + " 个成就，"
                + (delay > 0 ? "约 " + delay + " 秒后解锁第一个。" : "正在解锁第一个…"), true);

            this.ScheduleUnlock(delay);
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
                + "，下一个约 " + next + " 秒后解锁。", true);
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
