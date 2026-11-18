using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SAM.Modern
{
    internal static class AppSwitchProbe
    {
        public static void Run(long first, long second)
        {
            var sb = new StringBuilder();

            var clock = System.Diagnostics.Stopwatch.StartNew();

            var c1 = new SAM.API.Client();
            try
            {
                long t0 = clock.ElapsedMilliseconds;
                c1.Initialize(first);
                sb.AppendLine("phase1 ok appid=" + c1.SteamUtils.GetAppId() + " initMs=" + (clock.ElapsedMilliseconds - t0));
                sb.AppendLine("phase1 steamId=" + c1.SteamUser.GetSteamId());
            }
            catch (Exception e) { sb.AppendLine("phase1 fail " + e.GetType().Name + ": " + e.Message); }
            try
            {
                long t0 = clock.ElapsedMilliseconds;
                c1.Dispose();
                sb.AppendLine("phase1 disposed ms=" + (clock.ElapsedMilliseconds - t0));
            }
            catch (Exception e) { sb.AppendLine("phase1 dispose fail " + e.Message); }

            Environment.SetEnvironmentVariable("SteamAppId", second.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("env SteamAppId=" + (Environment.GetEnvironmentVariable("SteamAppId") ?? "<null>"));

            var c2 = new SAM.API.Client();
            try
            {
                long t0 = clock.ElapsedMilliseconds;
                c2.Initialize(second);
                sb.AppendLine("phase2 ok appid=" + c2.SteamUtils.GetAppId() + " initMs=" + (clock.ElapsedMilliseconds - t0));
            }
            catch (Exception e) { sb.AppendLine("phase2 fail " + e.GetType().Name + ": " + e.Message); }

            try
            {
                int result = -1;
                bool got = false;
                var cb = c2.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsReceived>();
                cb.OnRun += delegate(SAM.API.Types.UserStatsReceived d) { result = d.Result; got = true; };
                c2.SteamUserStats.RequestUserStats(c2.SteamUser.GetSteamId());
                for (int i = 0; i < 400 && got == false; i++) { c2.RunCallbacks(false); Thread.Sleep(25); }
                sb.AppendLine("phase2 UserStatsReceived got=" + got + " result=" + result);

                int kills;
                bool okStat = c2.SteamUserStats.GetStatValue("total_kills", out kills);
                sb.AppendLine("phase2 total_kills ok=" + okStat + " v=" + kills);

                bool achieved;
                uint when;
                bool okAch = c2.SteamUserStats.GetAchievementAndUnlockTime("NEW_ACHIEVEMENT", out achieved, out when);
                sb.AppendLine("phase2 ach ok=" + okAch + " achieved=" + achieved);
            }
            catch (Exception e) { sb.AppendLine("phase2 stats fail " + e.GetType().Name + ": " + e.Message); }
            try { c2.Dispose(); } catch (Exception) { }

            // phase3：再切回游戏库（appId=0），验证同一进程内反复切换是否稳定。
            Environment.SetEnvironmentVariable("SteamAppId", null);
            var c3 = new SAM.API.Client();
            try
            {
                long t0 = clock.ElapsedMilliseconds;
                c3.Initialize(0);
                sb.AppendLine("phase3 ok appid=" + c3.SteamUtils.GetAppId() + " initMs=" + (clock.ElapsedMilliseconds - t0));
                sb.AppendLine("phase3 steamId=" + c3.SteamUser.GetSteamId());
                long t1 = clock.ElapsedMilliseconds;
                int owned = 0;
                for (uint id = 1; id <= 400; id++) { if (c3.SteamApps008.IsSubscribedApp(id)) owned++; }
                sb.AppendLine("phase3 subscribed probe ms=" + (clock.ElapsedMilliseconds - t1) + " owned=" + owned);
            }
            catch (Exception e) { sb.AppendLine("phase3 fail " + e.GetType().Name + ": " + e.Message); }
            try { c3.Dispose(); sb.AppendLine("phase3 disposed"); } catch (Exception) { }

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-switch.txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }
}
