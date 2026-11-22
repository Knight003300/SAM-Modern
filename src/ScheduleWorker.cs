using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using SAM.API;

namespace SAM.Modern
{
    /// <summary>
    /// 定时解锁的执行体：由调度器以 --run-job 拉起，不建任何窗口。
    /// 等到目标时刻后用当前真实时间逐个 SetAchievement + StoreStats —— Steam 只认真实时间。
    /// </summary>
    internal static class ScheduleWorker
    {
        public static void Run(string jobPath)
        {
            Log.Write("schedule worker: start job=" + jobPath);

            var job = ScheduleStore.LoadJob(jobPath);
            if (job == null)
            {
                Log.Write("schedule worker: cannot read job");
                return;
            }

            int jitter = 0;
            if (job.Jitter > 0) jitter = new Random().Next(0, job.Jitter + 1);
            long target = job.At + jitter;
            long targetMs = target * 1000L;
            Log.Write("schedule worker: id=" + job.Id + " appId=" + job.AppId + " target=" + target
                + " jitter=" + jitter + " now=" + ScheduleStore.Now() + " nowMs=" + ScheduleStore.NowMs());

            ScheduleStore.WriteProgress(job.Id, 0, 0, "", target);

            // 先把 Steam 连接、统计数据、候选成就全部准备好，再等到目标那一秒动手。
            // 这样「到点」指的就是第一个成就真正写进 Steam 的时刻，而不是进程被拉起的时刻。
            var host = new SteamHost();
            bool statsReady = false;
            try
            {
                host.Initialize(job.AppId);
                var callback = host.Client.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsReceived>();
                callback.OnRun += delegate (SAM.API.Types.UserStatsReceived data)
                {
                    if (data.Result == 1) statsReady = true;
                };

                try { host.Client.SteamUserStats.RequestGlobalAchievementPercentages((uint)job.AppId); }
                catch (Exception error) { Log.Write("schedule worker: RequestGlobalAchievementPercentages failed", error); }
                host.Client.SteamUserStats.RequestUserStats(host.SteamId);
            }
            catch (Exception error)
            {
                Log.Write("schedule worker: steam init failed", error);
                ScheduleStore.WriteResult(job.Id, false, 0, 0, 0, "无法连接 Steam：" + error.Message);
                try { host.Dispose(); } catch (Exception) { }
                return;
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            while (statsReady == false && DateTime.UtcNow < deadline)
            {
                host.Pump();
                Thread.Sleep(50);
            }

            if (statsReady == false)
            {
                Log.Write("schedule worker: stats not received");
                ScheduleStore.WriteResult(job.Id, false, 0, 0, 0, "等待 Steam 统计数据超时。");
                try { host.Dispose(); } catch (Exception) { }
                return;
            }

            var ids = BuildTargets(host, job);
            if (ids.Count == 0)
            {
                ScheduleStore.WriteResult(job.Id, false, 0, 0, 0, "没有可解锁的成就。");
                try { host.Dispose(); } catch (Exception) { }
                return;
            }

            // 等到目标时刻（毫秒级对齐）：最后 10ms 才细睡，避免长时间空转。
            long lastBeat = ScheduleStore.NowMs();
            while (true)
            {
                long nowMs = ScheduleStore.NowMs();
                long remain = targetMs - nowMs;
                if (remain <= 0) break;
                // 心跳：每 10 秒刷新一次认领文件，调度器据此区分「worker 还在预热」和「worker 挂了」。
                if (nowMs - lastBeat >= 10000)
                {
                    lastBeat = nowMs;
                    ScheduleStore.TouchClaim(job.Id);
                }
                Thread.Sleep((int)Math.Min(remain, 10));
            }
            Log.Write("schedule worker: reached target nowMs=" + ScheduleStore.NowMs() + " driftMs=" +
                      (ScheduleStore.NowMs() - targetMs));

            Log.Write("schedule worker: unlocking " + ids.Count + " achievements");
            var api = host.Client.SteamUserStats;
            var random = new Random();
            int done = 0;
            int failed = 0;

            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                {
                    int gap = random.Next(job.MinGap, job.MaxGap + 1);
                    ScheduleStore.WriteProgress(job.Id, done, ids.Count, ids[i], target);
                    long sleepMs = gap * 1000L;
                    long slept = 0;
                    while (slept < sleepMs)
                    {
                        int slice = (int)Math.Min(10000, sleepMs - slept);
                        Thread.Sleep(slice);
                        slept += slice;
                        ScheduleStore.TouchClaim(job.Id);
                    }
                }

                bool ok;
                try { ok = api.SetAchievement(ids[i], true); }
                catch (Exception error) { Log.Write("schedule worker: SetAchievement failed " + ids[i], error); ok = false; }

                if (ok)
                {
                    try { api.StoreStats(); }
                    catch (Exception error) { Log.Write("schedule worker: StoreStats failed " + ids[i], error); }
                    done++;
                }
                else
                {
                    failed++;
                }

                Log.Write("schedule worker: " + (i + 1) + "/" + ids.Count + " id=" + ids[i] + " ok=" + ok);
                ScheduleStore.WriteProgress(job.Id, done, ids.Count, ids[i], target);
            }

            string message = failed > 0 ? ("其中 " + failed + " 个没有解锁成功。") : "";
            ScheduleStore.WriteResult(job.Id, done > 0, done, failed, ids.Count, message);
            Log.Write("schedule worker: finished done=" + done + " failed=" + failed);
            try { host.Dispose(); } catch (Exception) { }
        }

        private static List<string> BuildTargets(SteamHost host, ScheduleJob job)
        {
            var ids = new List<string>();
            if (job.Achievements.Count > 0)
            {
                ids.AddRange(job.Achievements);
                return ids;
            }

            List<AchievementDef> definitions;
            List<StatDefinition> stats;
            if (SchemaLoader.Load(job.AppId, job.Language, out definitions, out stats) == false)
            {
                Log.Write("schedule worker: schema load failed appId=" + job.AppId);
                return ids;
            }

            var api = host.Client.SteamUserStats;
            var candidates = new List<string>();
            foreach (var def in definitions)
            {
                if ((def.Permission & 3) != 0) continue;
                bool current = false;
                try { if (api.GetAchievement(def.Id, out current) == false) continue; }
                catch (Exception) { continue; }
                if (current) continue;

                if (job.RareOnly)
                {
                    float percent;
                    bool rare = false;
                    try { rare = api.GetAchievementAchievedPercent(def.Id, out percent) && percent > 0f && percent < job.RarePct; }
                    catch (Exception) { }
                    if (rare == false) continue;
                }

                candidates.Add(def.Id);
            }

            var random = new Random();
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                string temp = candidates[i];
                candidates[i] = candidates[j];
                candidates[j] = temp;
            }

            int take = job.Count > 0 ? Math.Min(job.Count, candidates.Count) : candidates.Count;
            for (int i = 0; i < take; i++) ids.Add(candidates[i]);
            return ids;
        }
    }
}
