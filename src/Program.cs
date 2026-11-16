using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SAM.API;

namespace SAM.Modern
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            Log.Init();
            Log.Write("start args=[" + string.Join(",", args) + "]");

            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs ev)
            {
                var error = ev.ExceptionObject as Exception;
                if (error != null) Log.Write("FATAL unhandled (terminating=" + ev.IsTerminating + ")", error);
                else Log.Write("FATAL unhandled (terminating=" + ev.IsTerminating + "): " + ev.ExceptionObject);
            };
            Application.ThreadException += delegate (object sender, System.Threading.ThreadExceptionEventArgs ev)
            {
                Log.Write("FATAL ui thread exception", ev.Exception);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            try { SetProcessDPIAware(); }
            catch (Exception) { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            long appId = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--dump-schema", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) SchemaDump.Write(args[++i]);
                    return;
                }
                if (string.Equals(arg, "--probe-catalog", StringComparison.OrdinalIgnoreCase))
                {
                    CatalogProbe.Run();
                    return;
                }
                if (string.Equals(arg, "--probe-switch", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 2 < args.Length)
                    {
                        AppSwitchProbe.Run(long.Parse(args[i + 1], CultureInfo.InvariantCulture),
                                           long.Parse(args[i + 2], CultureInfo.InvariantCulture));
                    }
                    return;
                }
                if (string.Equals(arg, "picker", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(arg, "--hidden", StringComparison.OrdinalIgnoreCase))
                {
                    WebShell.StartHidden = true;
                    continue;
                }
                if (string.Equals(arg, "--max", StringComparison.OrdinalIgnoreCase))
                {
                    WebShell.StartupMaximized = true;
                    continue;
                }
                if (string.Equals(arg, "--ready", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) WebShell.ReadyFile = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--bounds", StringComparison.OrdinalIgnoreCase))
                {
                    System.Drawing.Rectangle bounds;
                    if (i + 1 < args.Length && Relay.TryParseBounds(args[++i], out bounds))
                    {
                        WebShell.StartupBounds = bounds;
                    }
                    continue;
                }
                if (string.Equals(arg, "--auto-open", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) WebShell.AutoOpenAppId = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--shot", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) WebShell.ShotPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--script", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) WebShell.ShotScript = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--delay", StringComparison.OrdinalIgnoreCase))
                {
                    int delay;
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out delay))
                    {
                        WebShell.ShotDelayMs = delay;
                    }
                    continue;
                }
                long parsed;
                if (long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) == false || parsed <= 0)
                {
                    MessageBox.Show("无法从命令行参数解析应用 ID。", "SAM Modern",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                appId = parsed;
            }

            if (string.IsNullOrEmpty(WebShell.ShotPath) == false)
            {
                Log.Write("screenshot mode -> " + WebShell.ShotPath);
            }

            string installPath = Steam.GetInstallPath();
            if (string.IsNullOrEmpty(installPath) == false &&
                string.Equals(Path.GetFullPath(installPath), Path.GetFullPath(Application.StartupPath), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("本工具拒绝从 Steam 安装目录中运行。", "SAM Modern",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Log.Write("parsed appId=" + appId);
            var host = new SteamHost();
            try
            {
                host.Initialize(appId);
                Log.Write("steam host ready, appId=" + appId + " steamId=" + host.SteamId);
            }
            catch (ClientInitializeException error)
            {
                Log.Write("client init failed", error);
                string text;
                if (error.Failure == ClientInitializeFailure.ConnectToGlobalUser)
                {
                    text = "Steam 未运行。请先启动 Steam，然后重新打开本工具。" + Environment.NewLine + Environment.NewLine +
                           "如果你的游戏来自家庭共享，可能会因为共享账号正在游戏中而处于锁定状态。" + Environment.NewLine + Environment.NewLine +
                           "(" + error.Message + ")";
                }
                else
                {
                    text = "无法连接到 Steam：" + error.Failure + Environment.NewLine + error.Message;
                }
                MessageBox.Show(text, "SAM Modern", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            catch (DllNotFoundException error)
            {
                Log.Write("dll missing", error);
                MessageBox.Show("你触发了异常错误！" + Environment.NewLine + error.Message, "SAM Modern",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            catch (Exception error)
            {
                Log.Write("init failed", error);
                MessageBox.Show("初始化失败：" + Environment.NewLine + error.Message, "SAM Modern",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Log.Write("creating main form (appId=" + appId + ")");
            try
            {
                if (appId == 0)
                {
                    Application.Run(new PickerForm(host));
                }
                else
                {
                    Application.Run(new GameForm(host, appId));
                }
            }
            catch (Exception error)
            {
                Log.Write("FATAL form crash (appId=" + appId + ")", error);
                throw;
            }
            Log.Write("main form closed, exiting");
        }
    }
}
