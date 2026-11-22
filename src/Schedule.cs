using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SAM.Modern
{
    /// <summary>一条定时解锁任务：在指定时刻由隐藏的 worker 进程去逐个解锁。</summary>
    internal sealed class ScheduleJob
    {
        public string Id;
        public long AppId;
        public string GameName;
        public long At;              // unix 秒：第一个成就的解锁时刻
        public int MinGap = 15;      // 多个成就之间的随机间隔下限
        public int MaxGap = 45;
        public int Count;            // 0 = 全部剩余
        public bool RareOnly;
        public double RarePct = 5.0;
        public string Language = "english";
        public int Jitter;           // 0..N 秒随机错开，多个游戏同一分钟时用
        public List<string> Achievements = new List<string>();  // 显式指定时优先
        public long CreatedAt;
    }

    /// <summary>任务文件读写。全部落在 &lt;exe 目录&gt;/schedule 下，多个进程共享。</summary>
    internal static class ScheduleStore
    {
        public static string Root
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schedule"); }
        }

        public static void Ensure()
        {
            try { if (Directory.Exists(Root) == false) Directory.CreateDirectory(Root); }
            catch (Exception error) { Log.Write("schedule: cannot create dir", error); }
        }

        public static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>毫秒级当前时间：worker 等到精确的某一秒要用，秒级精度会抖 ±1s。</summary>
        public static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        public static string JobPath(string id) { return Path.Combine(Root, "job-" + id + ".json"); }
        public static string ClaimPath(string id) { return Path.Combine(Root, "job-" + id + ".claim"); }
        public static string ProgressPath(string id) { return Path.Combine(Root, "job-" + id + ".progress"); }
        public static string ResultPath(string id) { return Path.Combine(Root, "job-" + id + ".result"); }

        public static string NewId()
        {
            return Now().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        public static string Serialize(ScheduleJob job)
        {
            var b = new JsonBuilder();
            b.BeginObject()
                .Name("id").Str(job.Id)
                .Name("appId").Num(job.AppId)
                .Name("gameName").Str(job.GameName)
                .Name("at").Num(job.At)
                .Name("minGap").Num(job.MinGap)
                .Name("maxGap").Num(job.MaxGap)
                .Name("count").Num(job.Count)
                .Name("rareOnly").Bool(job.RareOnly)
                .Name("rarePct").Num(job.RarePct)
                .Name("language").Str(job.Language)
                .Name("jitter").Num(job.Jitter)
                .Name("createdAt").Num(job.CreatedAt)
                .Name("achievements").Str(Join(job.Achievements))
                .EndObject();
            return b.ToString();
        }

        private static string Join(List<string> ids)
        {
            if (ids == null || ids.Count == 0) return "";
            return string.Join("\n", ids.ToArray());
        }

        public static ScheduleJob Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var o = JsonParser.ParseObject(json);
            if (o == null) return null;
            var job = new ScheduleJob();
            job.Id = JsonParser.GetString(o, "id");
            job.AppId = (long)JsonParser.GetNumber(o, "appId", 0);
            job.GameName = JsonParser.GetString(o, "gameName");
            job.At = (long)JsonParser.GetNumber(o, "at", 0);
            job.MinGap = (int)JsonParser.GetNumber(o, "minGap", 15);
            job.MaxGap = (int)JsonParser.GetNumber(o, "maxGap", 45);
            job.Count = (int)JsonParser.GetNumber(o, "count", 0);
            job.RareOnly = JsonParser.GetBool(o, "rareOnly", false);
            job.RarePct = JsonParser.GetNumber(o, "rarePct", 5.0);
            job.Language = JsonParser.GetString(o, "language");
            if (string.IsNullOrEmpty(job.Language)) job.Language = "english";
            job.Jitter = (int)JsonParser.GetNumber(o, "jitter", 0);
            job.CreatedAt = (long)JsonParser.GetNumber(o, "createdAt", 0);
            string packed = JsonParser.GetString(o, "achievements");
            if (string.IsNullOrEmpty(packed) == false)
            {
                foreach (string line in packed.Split('\n'))
                {
                    string id = line.Trim();
                    if (id.Length > 0) job.Achievements.Add(id);
                }
            }
            // 允许 0：「立刻解锁」的任务就是 minGap = maxGap = 0。
            if (job.MinGap < 0) job.MinGap = 0;
            if (job.MaxGap < job.MinGap) job.MaxGap = job.MinGap;
            return job;
        }

        public static bool SaveJob(ScheduleJob job)
        {
            try
            {
                Ensure();
                string temp = JobPath(job.Id) + ".tmp";
                File.WriteAllText(temp, Serialize(job), new UTF8Encoding(false));
                if (File.Exists(JobPath(job.Id))) File.Delete(JobPath(job.Id));
                File.Move(temp, JobPath(job.Id));
                return true;
            }
            catch (Exception error)
            {
                Log.Write("schedule: save job failed", error);
                return false;
            }
        }

        public static ScheduleJob LoadJob(string path)
        {
            try { return Parse(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception error) { Log.Write("schedule: load job failed " + path, error); return null; }
        }

        public static List<ScheduleJob> LoadAll()
        {
            var list = new List<ScheduleJob>();
            try
            {
                if (Directory.Exists(Root) == false) return list;
                foreach (string path in Directory.GetFiles(Root, "job-*.json"))
                {
                    if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    var job = LoadJob(path);
                    if (job != null) list.Add(job);
                }
            }
            catch (Exception error) { Log.Write("schedule: list jobs failed", error); }
            list.Sort(delegate (ScheduleJob a, ScheduleJob b) { return a.At.CompareTo(b.At); });
            return list;
        }

        public static void DeleteJob(string id)
        {
            Delete(JobPath(id));
            Delete(ClaimPath(id));
            Delete(ProgressPath(id));
            Delete(ResultPath(id));
        }

        public static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception error) { Log.Write("schedule: delete failed " + path, error); }
        }

        /// <summary>清掉没有对应任务文件的残留（结果 / 进度 / 认领）。</summary>
        public static void CleanupOrphans()
        {
            try
            {
                if (Directory.Exists(Root) == false) return;
                foreach (string path in Directory.GetFiles(Root, "job-*.*"))
                {
                    string name = Path.GetFileName(path);
                    int dot = name.LastIndexOf('.');
                    if (dot <= 0) continue;
                    string id = name.Substring(4, dot - 4);
                    if (File.Exists(JobPath(id))) continue;
                    if (string.Equals(Path.GetExtension(name), ".json", StringComparison.OrdinalIgnoreCase)) continue;
                    Delete(path);
                }
            }
            catch (Exception error) { Log.Write("schedule: cleanup failed", error); }
        }

        public static void WriteProgress(string id, int done, int total, string current, long at)
        {
            try
            {
                var b = new JsonBuilder();
                b.BeginObject()
                    .Name("done").Num(done)
                    .Name("total").Num(total)
                    .Name("current").Str(current)
                    .Name("at").Num(at)
                    .Name("stamp").Num(Now())
                    .EndObject();
                File.WriteAllText(ProgressPath(id), b.ToString(), new UTF8Encoding(false));
            }
            catch (Exception error) { Log.Write("schedule: write progress failed", error); }
        }

        public static string ReadProgress(string id)
        {
            try
            {
                string path = ProgressPath(id);
                if (File.Exists(path) == false) return null;
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception) { return null; }
        }

        public static void WriteResult(string id, bool ok, int done, int failed, int total, string message)
        {
            try
            {
                var b = new JsonBuilder();
                b.BeginObject()
                    .Name("ok").Bool(ok)
                    .Name("done").Num(done)
                    .Name("failed").Num(failed)
                    .Name("total").Num(total)
                    .Name("message").Str(message)
                    .Name("finishedAt").Num(Now())
                    .EndObject();
                File.WriteAllText(ResultPath(id), b.ToString(), new UTF8Encoding(false));
            }
            catch (Exception error) { Log.Write("schedule: write result failed", error); }
        }

        public static Dictionary<string, object> ReadResult(string id)
        {
            try
            {
                string path = ResultPath(id);
                if (File.Exists(path) == false) return null;
                return JsonParser.ParseObject(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// 调度器：每个窗口（游戏库 / 游戏页）都跑一份，谁先认领谁负责起 worker 进程。
    /// 认领用「独占创建 .claim 文件」实现，多个进程同时跑也不会重复解锁。
    /// </summary>
    internal sealed class ScheduleDispatcher : IDisposable
    {
        private readonly Bridge _bridge;
        private readonly Timer _timer;
        private readonly Dictionary<string, long> _running = new Dictionary<string, long>(StringComparer.Ordinal);
        private string _lastPublished;

        private const int LeadSeconds = 40;      // 提前多久把 worker 拉起来（worker 先连 Steam 备好数据，再等到点）
        private const int LateGraceSeconds = 3600;
        private const int ClaimStaleSeconds = 180;

        public ScheduleDispatcher(Bridge bridge)
        {
            this._bridge = bridge;
            this._timer = new Timer();
            this._timer.Interval = 1000;
            this._timer.Tick += this.OnTick;
        }

        public void Start()
        {
            ScheduleStore.Ensure();
            ScheduleStore.CleanupOrphans();
            try { this._timer.Start(); }
            catch (Exception error) { Log.Write("schedule: start failed", error); }
        }

        public void Dispose()
        {
            try { this._timer.Stop(); this._timer.Dispose(); }
            catch (Exception) { }
        }

        public void Publish()
        {
            this._lastPublished = null;
            this.PublishJobs(true);
        }

        private void OnTick(object sender, EventArgs e)
        {
            try { this.Tick(); }
            catch (Exception error) { Log.Write("schedule: tick failed", error); }
        }

        private void Tick()
        {
            long now = ScheduleStore.Now();
            var jobs = ScheduleStore.LoadAll();
            bool changed = false;

            foreach (var job in jobs)
            {
                if (this._running.ContainsKey(job.Id))
                {
                    var result = ScheduleStore.ReadResult(job.Id);
                    if (result != null)
                    {
                        this.Finish(job, result);
                        changed = true;
                    }
                    else if (now > job.At + ClaimStaleSeconds + 120 && this.IsStaleClaim(job.Id))
                    {
                        ScheduleStore.WriteResult(job.Id, false, 0, 0, 0, "解锁进程没有响应。");
                        changed = true;
                    }
                    continue;
                }

                if (now > job.At + LateGraceSeconds)
                {
                    Log.Write("schedule: job expired " + job.Id);
                    ScheduleStore.DeleteJob(job.Id);
                    this.Send("scheduleMissed", job, null);
                    changed = true;
                    continue;
                }

                if (now < job.At - LeadSeconds) continue;

                if (ScheduleStore.ReadResult(job.Id) != null) continue;   // 别的进程已经跑完

                if (this.TryClaim(job.Id) == false) continue;             // 别的进程负责

                if (this.Launch(job) == false)
                {
                    ScheduleStore.Delete(ScheduleStore.ClaimPath(job.Id));
                    continue;
                }
                this._running[job.Id] = now;
                this.Send("scheduleStarted", job, null);
                changed = true;
            }

            foreach (string id in new List<string>(this._running.Keys))
            {
                bool exists = false;
                foreach (var job in jobs) { if (job.Id == id) { exists = true; break; } }
                if (exists == false) this._running.Remove(id);
            }

            if (changed) this.PublishJobs(true);
            else this.PublishJobs(false);
        }

        private bool IsStaleClaim(string id)
        {
            try
            {
                string path = ScheduleStore.ClaimPath(id);
                if (File.Exists(path) == false) return true;
                return (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds > ClaimStaleSeconds;
            }
            catch (Exception) { return true; }
        }

        private bool TryClaim(string id)
        {
            string path = ScheduleStore.ClaimPath(id);
            try
            {
                if (File.Exists(path))
                {
                    if (this.IsStaleClaim(id) == false) return false;
                    ScheduleStore.Delete(path);
                }
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                    stream.Write(bytes, 0, bytes.Length);
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (Exception error) { Log.Write("schedule: claim failed", error); return false; }
        }

        private bool Launch(ScheduleJob job)
        {
            try
            {
                var info = new ProcessStartInfo();
                info.FileName = Application.ExecutablePath;
                info.Arguments = "--run-job \"" + ScheduleStore.JobPath(job.Id) + "\"";
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var child = Process.Start(info);
                Log.Write("schedule: launched worker pid=" + (child == null ? 0 : child.Id) + " job=" + job.Id
                    + " appId=" + job.AppId + " at=" + job.At);
                return child != null;
            }
            catch (Exception error)
            {
                Log.Write("schedule: launch failed", error);
                ScheduleStore.WriteResult(job.Id, false, 0, 0, 0, "无法启动解锁进程：" + error.Message);
                return false;
            }
        }

        private void Finish(ScheduleJob job, Dictionary<string, object> result)
        {
            bool ok = JsonParser.GetBool(result, "ok", false);
            int done = (int)JsonParser.GetNumber(result, "done", 0);
            int total = (int)JsonParser.GetNumber(result, "total", 0);
            string message = JsonParser.GetString(result, "message");
            Log.Write("schedule: finished job=" + job.Id + " ok=" + ok + " done=" + done + "/" + total);
            this.Send("scheduleDone", job, result);
            if (ok)
            {
                this._bridge.Toast("success", "定时解锁完成",
                    "《" + job.GameName + "》已解锁 " + done + " 个成就。");
            }
            else
            {
                this._bridge.Toast("error", "定时解锁失败",
                    "《" + job.GameName + "》" + (string.IsNullOrEmpty(message) ? "解锁没有成功。" : message));
            }
            this._running.Remove(job.Id);
            ScheduleStore.DeleteJob(job.Id);
        }

        private void Send(string ev, ScheduleJob job, Dictionary<string, object> result)
        {
            var b = new JsonBuilder();
            b.BeginObject().Name("ev").Str(ev);
            this.WriteJob(b, job, result);
            b.EndObject();
            this._bridge.Send(b);
        }

        private void PublishJobs(bool force)
        {
            var jobs = ScheduleStore.LoadAll();
            var b = new JsonBuilder();
            b.BeginObject().Name("ev").Str("scheduleList").Name("jobs").BeginArray();
            var ids = new StringBuilder();
            foreach (var job in jobs)
            {
                ids.Append(job.Id).Append(';');
                b.BeginObject();
                this.WriteJobFields(b, job);
                string progress = ScheduleStore.ReadProgress(job.Id);
                if (string.IsNullOrEmpty(progress) == false)
                {
                    var o = JsonParser.ParseObject(progress);
                    if (o != null)
                    {
                        b.Name("done").Num((long)JsonParser.GetNumber(o, "done", 0));
                        b.Name("total").Num((long)JsonParser.GetNumber(o, "total", 0));
                    }
                }
                b.EndObject();
            }
            b.EndArray().EndObject();
            string signature = ids.ToString();
            if (force == false && signature == this._lastPublished) return;
            this._lastPublished = signature;
            this._bridge.Send(b);
        }

        private void WriteJob(JsonBuilder b, ScheduleJob job, Dictionary<string, object> result)
        {
            b.Name("job").BeginObject();
            this.WriteJobFields(b, job);
            b.EndObject();
            if (result != null)
            {
                b.Name("result").BeginObject()
                    .Name("ok").Bool(JsonParser.GetBool(result, "ok", false))
                    .Name("done").Num((long)JsonParser.GetNumber(result, "done", 0))
                    .Name("total").Num((long)JsonParser.GetNumber(result, "total", 0))
                    .Name("message").Str(JsonParser.GetString(result, "message"))
                    .EndObject();
            }
        }

        private void WriteJobFields(JsonBuilder b, ScheduleJob job)
        {
            b.Name("id").Str(job.Id)
                .Name("appId").Num(job.AppId)
                .Name("gameName").Str(job.GameName)
                .Name("at").Num(job.At)
                .Name("count").Num(job.Count)
                .Name("minGap").Num(job.MinGap)
                .Name("maxGap").Num(job.MaxGap)
                .Name("rareOnly").Bool(job.RareOnly)
                .Name("rarePct").Num(job.RarePct);
            // 点名解锁时前端要知道这条任务指定了哪些成就（列表里显示「指定 N 个」）
            if (job.Achievements != null && job.Achievements.Count > 0)
            {
                b.Name("achievements").BeginArray();
                foreach (string id in job.Achievements) b.Str(id);
                b.EndArray();
            }
        }
    }
}
