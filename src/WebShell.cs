using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SAM.Modern
{
    /// <summary>无边框外壳：自绘 8px 边框（可拖动缩放）+ WebView2 宿主。</summary>
    internal class WebShell : Form
    {
        private const int WmNcHitTest = 0x0084;
        private const int WmNcLButtonDown = 0x00A1;
        private const int HtCaption = 0x0002;
        private const int HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
        private const int ResizeBorder = 6;
        private const int WmGetMinMaxInfo = 0x0024;
        private const int WmNcCalcSize = 0x0083;
        private const int MonitorDefaultToNearest = 0x00000002;
        private const int WsThickFrame = 0x00040000;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeOld = 19;
        private const int DwmwaBorderColor = 34;
        private const int WmEraseBkgnd = 0x0014;
        private const int WmNcPaint = 0x0085;
        private const int WmNcActivate = 0x0086;
        private const int CornerRadius = 10;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        /// <summary>WM_GETMINMAXINFO 的参数：系统按「整块显示器」算最大化尺寸，无边框窗会盖住任务栏，所以要自己改。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        protected readonly WebView2 Web = new WebView2();
        protected readonly Bridge Bridge;
        private readonly string _uiDirectory;
        private CoreWebView2Environment _environment;
        private bool _webReady;
        private ScheduleDispatcher _scheduler;

        public WebShell(string title)
        {
            this.Text = title;
            this.FormBorderStyle = FormBorderStyle.None;

            // 尺寸不能写死：高 DPI 小屏（例如 1366x768 @150% → 工作区只有约 911x485 逻辑像素）上，
            // 写死的 920x620 最小尺寸比整个工作区还大，窗口永远放不下，用户看到的就是「放不大」。
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Size minimum = new Size(Math.Min(920, work.Width), Math.Min(620, work.Height));
            Size preferred = new Size(Math.Min(1280, work.Width), Math.Min(840, work.Height));
            this.MinimumSize = minimum;
            this.Size = preferred;
            this.MaximizedBounds = work;
            if (StartupBounds.IsEmpty == false && StartupBounds.Width >= 320 && StartupBounds.Height >= 240)
            {
                this.StartPosition = FormStartPosition.Manual;
                this.Bounds = StartupBounds;
            }
            else
            {
                this.StartPosition = FormStartPosition.CenterScreen;
            }
            this.BackColor = Color.FromArgb(12, 10, 22);
            this.DoubleBuffered = true;
            this.Padding = new Padding(8);
            this.KeyPreview = true;

            this._uiDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");
            this._explicitBounds = StartupBounds.IsEmpty == false && StartupBounds.Width >= 320 && StartupBounds.Height >= 240;

            this.Web.Dock = DockStyle.Fill;
            this.Web.DefaultBackgroundColor = Color.FromArgb(12, 10, 22);
            this.Controls.Add(this.Web);

            this.Bridge = new Bridge(this.Web);
            this.Bridge.Status("正在启动…", true);
            // 定时解锁的任务调度器：游戏库和游戏窗口各有一个，靠 .claim 独占文件互斥，
            // 所以用户同时开着两个窗口也只会有一个进程真正去拉起 worker。
            this._scheduler = new ScheduleDispatcher(this.Bridge);

            if (StartHidden)
            {
                // 不能靠 SetVisibleCore 抑制显示：那样句柄不会创建，OnLoad 不会执行，WebView2 也就永远初始化不了。
                // 改为把窗口挪到屏幕外：照常显示/加载，就绪后再挪回目标位置。
                // 注意：不要在这里把 ShowInTaskbar 设成 false 再在亮窗口时改回 true ——
                // WinForms 切换 ShowInTaskbar 会重建窗口句柄，实测吃掉 150ms 以上，
                // 正好卡在「页面已画好、等窗口亮起」的关键路径上。
                // 窗口在屏幕外不可见，任务栏按钮提前出现不到一秒，同 exe 会与旧窗口合并，用户无感。
                this.StartPosition = FormStartPosition.Manual;
                this.Location = OffScreenLocation;
            }
        }

        protected Bridge Channel { get { return this.Bridge; } }

        private static readonly Point OffScreenLocation = new Point(-32000, -32000);

        /// <summary>把窗口从屏幕外挪到目标位置并激活（供页面就绪后调用）。</summary>
        protected void RevealWindow()
        {
            if (this._revealed) return;
            this._revealed = true;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var trace = new System.Text.StringBuilder();
            Action<string> mark = delegate (string name) { trace.Append(name).Append('=').Append(clock.ElapsedMilliseconds).Append(' '); };

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Size targetSize = this._explicitBounds ? StartupBounds.Size : new Size(1280, 840);
            if (targetSize.Width < 320 || targetSize.Height < 240) targetSize = new Size(1280, 840);
            // 目标尺寸不能超过工作区：显式 bounds 也可能是上一个窗口在更大的屏幕上留下的。
            targetSize = new Size(
                Math.Min(targetSize.Width, area.Width),
                Math.Min(targetSize.Height, area.Height));

            Point targetLocation;
            if (this._explicitBounds)
            {
                targetLocation = StartupBounds.Location;
            }
            else
            {
                targetLocation = new Point(
                    area.Left + Math.Max(0, (area.Width - targetSize.Width) / 2),
                    area.Top + Math.Max(0, (area.Height - targetSize.Height) / 2));
            }

            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = new Rectangle(targetLocation, targetSize);
            mark("bounds");

            if (this.Visible == false) this.Show();
            mark("show");
            if (StartupMaximized) this.WindowState = FormWindowState.Maximized;

            this.Activate();
            mark("activate");
            this.BringToFront();
            mark("front");
            this.ApplyRoundedRegion();
            mark("round");
            Log.Write("reveal timing: " + trace.ToString().TrimEnd());
        }

        /// <summary>调试用：页面就绪后把 WebView2 渲染结果截图到该路径（为空则不截图）。</summary>
        public static string ShotPath;

        /// <summary>调试用：截图前等待的毫秒数。</summary>
        public static int ShotDelayMs = 3000;

        /// <summary>调试用：截图前注入执行的脚本（用于切页签等验收场景）。</summary>
        public static string ShotScript;

        /// <summary>由父窗口传入的窗口位置与尺寸（空表示居中）。</summary>
        public static Rectangle StartupBounds = Rectangle.Empty;

        /// <summary>由父窗口传入：启动后是否最大化。</summary>
        public static bool StartupMaximized;

        /// <summary>页面就绪后写入该文件，父窗口据此隐藏自己（为空则不写）。</summary>
        public static string ReadyFile;

        /// <summary>启动时先不显示窗口，等页面真正就绪后再显示（避免白屏闪烁）。</summary>
        public static bool StartHidden;

        /// <summary>调试用：游戏库界面就绪后自动打开指定 AppID（为空则不自动打开）。</summary>
        public static string AutoOpenAppId;

        private bool _nativeRounded;
        private bool _revealed;
        private readonly bool _explicitBounds;

        protected override async void OnLoad(EventArgs e)
        {
            Log.Write("WebShell.OnLoad enter");
            base.OnLoad(e);

            if (StartupMaximized && StartHidden == false)
            {
                try { this.WindowState = FormWindowState.Maximized; }
                catch (Exception error) { Log.Write("maximize failed", error); }
            }

            Log.Write("WebView2 init start (hidden=" + StartHidden + ", readyFile=" + (ReadyFile ?? "<none>") + ")");

            var initClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var dataFolder = ResolveDataFolder();
                long folderMs = initClock.ElapsedMilliseconds;

                this._environment = await CoreWebView2Environment.CreateAsync(null, dataFolder, null);
                long envMs = initClock.ElapsedMilliseconds;
                await this.Web.EnsureCoreWebView2Async(this._environment);
                long coreMs = initClock.ElapsedMilliseconds;

                var core = this.Web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;
                core.Settings.IsSwipeNavigationEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.SetVirtualHostNameToFolderMapping("sam.ui", this._uiDirectory, CoreWebView2HostResourceAccessKind.Allow);

                string cacheRoot = Art.LocalCacheRoot;
                if (string.IsNullOrEmpty(cacheRoot) == false)
                {
                    try
                    {
                        core.SetVirtualHostNameToFolderMapping("steam.cache", cacheRoot, CoreWebView2HostResourceAccessKind.Allow);
                        Log.Write("library cache host: " + cacheRoot);
                    }
                    catch (Exception error)
                    {
                        Log.Write("library cache host failed: " + cacheRoot, error);
                    }
                }

                core.WebMessageReceived += this.OnWebMessage;
                core.NavigationCompleted += this.OnNavigationCompleted;
                core.NewWindowRequested += (s, args) => { args.Handled = true; };

                await core.AddScriptToExecuteOnDocumentCreatedAsync(UiDiagnosticsScript);
                long scriptMs = initClock.ElapsedMilliseconds;
                core.Navigate("https://sam.ui/index.html");
                Log.Write("WebView2 init timing: folder=" + folderMs + " env=" + (envMs - folderMs) +
                    " core=" + (coreMs - envMs) + " script=" + (scriptMs - coreMs) +
                    " navigate=" + (initClock.ElapsedMilliseconds - scriptMs));
                Log.Write("WebView2 navigated to virtual host (" + this._uiDirectory + ")");
            }
            catch (Exception error)
            {
                Log.Write("WebView2 init failed", error);
                MessageBox.Show(this,
                    "界面引擎（WebView2）初始化失败：" + Environment.NewLine + error.Message +
                    Environment.NewLine + Environment.NewLine +
                    "请安装 Microsoft Edge WebView2 运行时后重试。",
                    "SAM Modern", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
        }

        /// <summary>注入到页面：把 JS 异常与 console.error 回传给宿主记录日志。</summary>
        private const string UiDiagnosticsScript = @"
(function () {
  function report(kind, message, source, stack) {
    try {
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({ cmd: 'ui/error', kind: String(kind), message: String(message), source: String(source || ''), stack: String(stack || '') });
      }
    } catch (e) { }
  }
  window.addEventListener('error', function (e) {
    if (e && e.target && e.target !== window && e.target.tagName && !e.message) return;
    report('error', e && e.message, e && (e.filename + ':' + e.lineno + ':' + e.colno), e && e.error && e.error.stack);
  }, true);
  window.addEventListener('unhandledrejection', function (e) {
    var r = e && e.reason;
    report('rejection', r && (r.message || r), '', r && r.stack);
  });
  var originalError = console.error;
  console.error = function () {
    var parts = Array.prototype.slice.call(arguments).map(function (a) {
      try { return typeof a === 'object' ? JSON.stringify(a) : String(a); } catch (x) { return String(a); }
    });
    report('console', parts.join(' '), '', '');
    if (originalError) originalError.apply(console, arguments);
  };
  window.addEventListener('load', function () {
    try {
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({ cmd: 'ui/ready' });
      }
    } catch (e) { }
  });
})();
";

        /// <summary>把封面候选写进 JSON：页面逐个尝试，个别游戏没有 header.jpg 也能显示。</summary>
        protected static void WriteCapsules(JsonBuilder b, long appId)
        {
            List<string> candidates = Art.CapsuleCandidates(appId);
            b.Name("capsule").Str(candidates.Count > 0 ? candidates[0] : null);
            b.Name("capsules").BeginArray();
            foreach (string url in candidates) b.Str(url);
            b.EndArray();
        }

        /// <summary>启动另一个进程承载新界面（appId 为 0 表示游戏库），本窗口在新窗口就绪后关闭。</summary>
        protected void RelayTo(long appId)
        {
            // 切换进行中时忽略重复点击，避免一次点击叠出多个隐藏子进程。
            if (this._relayTimer != null && this._relayTimer.Enabled)
            {
                Log.Write("relay ignored: switch already in progress");
                return;
            }

            // 回游戏库时，如果本窗口是被游戏库窗口接力起来的，就直接把那个窗口唤回来：
            // 它的句柄、WebView2、已经渲染好的列表都还在，比新起一个进程快一个数量级。
            if (appId <= 0 && this.TryResumeParent())
            {
                return;
            }

            // %TEMP% / %LOCALAPPDATA% 在本机对被调起的进程不可写，握手文件必须放在程序目录下。
            string ready = ReadyFilePath();

            Rectangle bounds = this.WindowState == FormWindowState.Maximized
                ? this.RestoreBounds
                : this.Bounds;
            bool maximized = this.WindowState == FormWindowState.Maximized;
            if (bounds.Width < 320 || bounds.Height < 240) bounds = Rectangle.Empty;

            // 游戏库窗口去开游戏时把自己的 PID 传给子进程：子进程返回时据此唤回本窗口。
            int parentPid = (appId > 0 && this.KeepAliveOnRelay)
                ? System.Diagnostics.Process.GetCurrentProcess().Id
                : 0;

            try
            {
                var info = new System.Diagnostics.ProcessStartInfo();
                info.FileName = Application.ExecutablePath;
                info.UseShellExecute = false;
                // 接力子进程是控制台程序：不抑制的话每次切换都会在桌面上闪一个黑窗口。
                info.CreateNoWindow = true;
                info.Arguments = Relay.BuildArguments(appId, ready, bounds, maximized, parentPid);
                this._relayChild = System.Diagnostics.Process.Start(info);
                Log.Write("relay launch appId=" + appId + " pid=" + (this._relayChild == null ? 0 : this._relayChild.Id) + " parent=" + parentPid + " args=" + info.Arguments);
            }
            catch (Exception error)
            {
                Log.Write("relay launch failed", error);
                this.Channel.Error("无法打开", "无法启动新的界面进程：" + error.Message);
                return;
            }

            this.Channel.Status(appId > 0 ? "正在载入游戏数据…" : "正在返回游戏库…", true);
            this.StartRelayWatch(ready);
        }

        private Timer _relayTimer;
        private DateTime _relayDeadline;
        private System.Diagnostics.Process _relayChild;
        private string _relayReadyFile;

        /// <summary>
        /// 接力后本窗口是否继续活着（游戏库窗口隐藏待命，等子窗口唤回）。
        /// 只有游戏库窗口会返回 true：它的状态全在内存里，唤回比重建便宜得多。
        /// </summary>
        protected virtual bool KeepAliveOnRelay { get { return false; } }

        /// <summary>由父窗口通过 --parent-pid 传入的游戏库进程 PID（0 表示没有父窗口）。</summary>
        public static int ParentPid;

        private Timer _parentWatchTimer;
        private bool _standby;
        private System.Diagnostics.Process _standbyChild;

        private Timer _resumeTimer;
        private DateTime _resumeDeadline;
        private string _resumeFilePath;
        private string _resumeAckPath;
        private bool _resumeTried;
        private bool _handedOffToParent;

        /// <summary>父子进程握手文件路径：位于程序目录下的 relay 子目录（保证双方都可读写）。</summary>
        private static string ReadyFilePath()
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "relay");
            try
            {
                Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception error)
            {
                Log.Write("relay folder unusable: " + folder, error);
                folder = AppDomain.CurrentDomain.BaseDirectory;
            }
            return Path.Combine(folder, "sam-modern-relay-" + Guid.NewGuid().ToString("N") + ".ready");
        }

        private void StartRelayWatch(string readyFile)
        {
            this._relayReadyFile = readyFile;
            this._relayDeadline = DateTime.UtcNow.AddSeconds(25);
            if (this._relayTimer == null)
            {
                this._relayTimer = new Timer();
                this._relayTimer.Interval = 15;
                // Timer 只建一次，路径必须从字段读，否则第二次接力会去轮询上一轮的文件。
                this._relayTimer.Tick += delegate { this.PollRelay(this._relayReadyFile); };
            }
            this._relayTimer.Start();
        }

        private void PollRelay(string readyFile)
        {
            bool ready = false;
            try { ready = File.Exists(readyFile); }
            catch (Exception) { }

            if (ready)
            {
                this._relayTimer.Stop();
                try { File.Delete(readyFile); }
                catch (Exception) { }
                this._standbyChild = this._relayChild;
                this._relayChild = null;
                if (this.KeepAliveOnRelay)
                {
                    this.EnterStandby();
                    return;
                }
                Log.Write("relay ready, closing old window");
                this.Close();
                return;
            }

            if (this._relayChild != null)
            {
                bool exited = false;
                try { exited = this._relayChild.HasExited; }
                catch (Exception) { exited = true; }

                if (exited)
                {
                    // 新窗口还没报到就没了（初始化失败 / 用户提前关掉）：别干等 25 秒。
                    this._relayTimer.Stop();
                    this._relayChild = null;
                    Log.Write("relay child exited before ready");
                    this.Channel.Status("切换失败。", false);
                    this.Channel.Toast("error", "切换失败", "新窗口在就绪前退出了，已保留当前界面。");
                    return;
                }
            }

            if (DateTime.UtcNow > this._relayDeadline)
            {
                this._relayTimer.Stop();
                if (this._relayChild != null)
                {
                    try { if (this._relayChild.HasExited == false) this._relayChild.Kill(); }
                    catch (Exception) { }
                    this._relayChild = null;
                }
                Log.Write("relay timeout, killed child");
                this.Channel.Status("切换失败。", false);
                this.Channel.Toast("error", "切换失败", "新窗口未能在 25 秒内就绪，已保留当前界面。");
            }
        }

        /// <summary>
        /// 接力成功后本窗口进入待命：藏起来但进程活着，等游戏窗口要回游戏库时再被唤回。
        /// 隐藏而不是关闭，是因为重建一次窗口（进程启动 + Steam 初始化 + WebView2 + 页面渲染）要 870ms 量级，
        /// 而把已有窗口 Show 出来只要几十毫秒。
        /// </summary>
        private void EnterStandby()
        {
            this._standby = true;
            Log.Write("standby: hiding picker window, child pid=" + (this._standbyChild == null ? 0 : this._standbyChild.Id));

            try { this.Hide(); }
            catch (Exception error) { Log.Write("standby hide failed", error); }

            if (this._parentWatchTimer == null)
            {
                this._parentWatchTimer = new Timer();
                this._parentWatchTimer.Interval = 20;
                this._parentWatchTimer.Tick += delegate { this.PollStandby(); };
            }
            this._parentWatchTimer.Start();
        }

        /// <summary>待命期间轮询两件事：子窗口要求唤回（正常），子窗口已经退出（异常，得自己回来）。</summary>
        private void PollStandby()
        {
            if (this._standby == false)
            {
                this._parentWatchTimer.Stop();
                return;
            }

            string resume = Relay.ResumeFilePath(System.Diagnostics.Process.GetCurrentProcess().Id);
            bool requested = false;
            try { requested = File.Exists(resume); }
            catch (Exception) { }

            if (requested)
            {
                Rectangle bounds = Rectangle.Empty;
                bool maximized = false;
                bool quit = false;
                try
                {
                    string text = File.ReadAllText(resume);
                    quit = string.Equals(text.Trim(), "quit", StringComparison.OrdinalIgnoreCase);
                    if (quit == false) ParseResumeRequest(text, out bounds, out maximized);
                    File.Delete(resume);
                }
                catch (Exception error)
                {
                    Log.Write("resume read failed", error);
                }

                if (quit)
                {
                    // 用户在游戏窗口点了关闭：整个程序一起退出，别把游戏库窗口单独冒出来。
                    Log.Write("standby: child closed, exiting");
                    this._standby = false;
                    this._standbyChild = null;
                    this._parentWatchTimer.Stop();
                    this.Close();
                    return;
                }

                Log.Write("standby: resumed by child, bounds=" + bounds + " max=" + maximized);
                this._standbyChild = null;
                this.ExitStandby(bounds, maximized);

                try
                {
                    string ack = resume + ".ack";
                    File.WriteAllText(ack, "ok");
                }
                catch (Exception error)
                {
                    Log.Write("resume ack failed", error);
                }
                return;
            }

            if (this._standbyChild != null)
            {
                bool exited = false;
                try { exited = this._standbyChild.HasExited; }
                catch (Exception) { exited = true; }

                if (exited)
                {
                    // 子窗口没走唤回流程就没了（崩溃或被任务管理器结束）：自己出来，别让用户面对空桌面。
                    Log.Write("standby: child exited without resume, showing picker again");
                    this._standbyChild = null;
                    this.ExitStandby(Rectangle.Empty, false);
                }
            }
        }

        /// <summary>把待命的窗口重新摆出来。</summary>
        private void ExitStandby(Rectangle bounds, bool maximized)
        {
            this._standby = false;
            if (this._parentWatchTimer != null) this._parentWatchTimer.Stop();

            try
            {
                if (bounds.IsEmpty == false && bounds.Width >= 320 && bounds.Height >= 240)
                {
                    // 上一个窗口记下的位置可能来自更大的屏幕：夹回当前工作区，别让窗口跑到屏幕外。
                    Rectangle work = Screen.FromRectangle(bounds).WorkingArea;
                    this.StartPosition = FormStartPosition.Manual;
                    this.Bounds = new Rectangle(
                        bounds.X,
                        bounds.Y,
                        Math.Min(bounds.Width, work.Width),
                        Math.Min(bounds.Height, work.Height));
                }
                this.Show();
                if (maximized) this.WindowState = FormWindowState.Maximized;
                this.Activate();
                this.BringToFront();
                this.ApplyRoundedRegion();
                this.OnResumed();
            }
            catch (Exception error)
            {
                Log.Write("standby exit failed", error);
            }
        }

        /// <summary>窗口从待命状态被唤回后调用：子类可以在这里恢复界面上的状态文案。</summary>
        protected virtual void OnResumed() { }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                if (this._scheduler != null)
                {
                    this._scheduler.Dispose();
                    this._scheduler = null;
                }
            }
            catch (Exception error)
            {
                Log.Write("schedule: dispose failed", error);
            }

            // 用户直接关掉了游戏窗口（不是返回游戏库）：让待命的游戏库窗口一起退出。
            // 否则游戏库窗口会因为「子进程没了」的兜底逻辑自己冒出来，用户会觉得关不掉。
            if (ParentPid > 0 && this._handedOffToParent == false)
            {
                try
                {
                    File.WriteAllText(Relay.ResumeFilePath(ParentPid), "quit");
                    Log.Write("quit parent pid=" + ParentPid);
                }
                catch (Exception error)
                {
                    Log.Write("quit parent failed", error);
                }
            }
            base.OnFormClosed(e);
        }

        private static void ParseResumeRequest(string text, out Rectangle bounds, out bool maximized)
        {
            bounds = Rectangle.Empty;
            maximized = false;
            if (string.IsNullOrEmpty(text)) return;

            string[] parts = text.Trim().Split('|');
            if (parts.Length > 1 && string.Equals(parts[1], "max", StringComparison.OrdinalIgnoreCase)) maximized = true;

            Rectangle parsed;
            if (Relay.TryParseBounds(parts[0], out parsed)) bounds = parsed;
        }

        /// <summary>
        /// 本窗口是被游戏库窗口接力起来的：写唤回文件，让那个窗口自己回来，然后关掉自己。
        /// 返回 true 表示已经交给父窗口，调用方不要再启动新进程。
        /// </summary>
        private bool TryResumeParent()
        {
            if (ParentPid <= 0 || this._resumeTried) return false;
            this._resumeTried = true;

            string path = Relay.ResumeFilePath(ParentPid);
            Rectangle bounds = this.WindowState == FormWindowState.Maximized ? this.RestoreBounds : this.Bounds;
            bool maximized = this.WindowState == FormWindowState.Maximized;
            if (bounds.Width < 320 || bounds.Height < 240) bounds = Rectangle.Empty;

            string text = bounds.IsEmpty
                ? "0,0,0,0" + (maximized ? "|max" : string.Empty)
                : bounds.X + "," + bounds.Y + "," + bounds.Width + "," + bounds.Height + (maximized ? "|max" : string.Empty);

            try
            {
                File.WriteAllText(path, text);
                Log.Write("resume parent pid=" + ParentPid + " request=" + text);
            }
            catch (Exception error)
            {
                Log.Write("resume request failed", error);
                return false;
            }

            this._handedOffToParent = true;
            this.Channel.Status("正在返回游戏库…", true);
            this._resumeFilePath = path;
            this._resumeAckPath = path + ".ack";
            this._resumeDeadline = DateTime.UtcNow.AddSeconds(8);

            if (this._resumeTimer == null)
            {
                this._resumeTimer = new Timer();
                this._resumeTimer.Interval = 15;
                this._resumeTimer.Tick += delegate { this.PollResumeAck(); };
            }
            this._resumeTimer.Start();
            return true;
        }

        private void PollResumeAck()
        {
            bool acked = false;
            try { acked = File.Exists(this._resumeAckPath); }
            catch (Exception) { }

            if (acked)
            {
                this._resumeTimer.Stop();
                try { File.Delete(this._resumeAckPath); } catch (Exception) { }
                Log.Write("parent resumed, closing game window");
                this.Close();
                return;
            }

            if (DateTime.UtcNow > this._resumeDeadline)
            {
                this._resumeTimer.Stop();
                Log.Write("parent resume timeout, falling back to new picker process");
                try { File.Delete(this._resumeFilePath); } catch (Exception) { }
                // 父窗口叫不醒：让它自己退场，免得两个游戏库窗口同时存在。
                try { File.WriteAllText(Relay.ResumeFilePath(ParentPid), "quit"); } catch (Exception) { }
                // 父窗口没响应（被杀 / 卡死）：退回原来的做法，起一个新的游戏库进程。
                this.RelayTo(0);
            }
        }

        private bool _readyAnnounced;

        /// <summary>
        /// 显示窗口并通知父窗口退出。幂等：多次调用只有第一次生效。
        /// 时机在页面 load 之后、C# 侧推数据之前 —— 页面此时已经有骨架屏，
        /// 先把窗口摆上来、让旧窗口退场，数据随后补进去，比等数据齐了再显示快得多。
        /// </summary>
        private void MarkReady()
        {
            if (this._readyAnnounced) return;
            this._readyAnnounced = true;

            this.RevealWindow();

            if (string.IsNullOrEmpty(ReadyFile)) return;
            try
            {
                // 先写临时文件再改名：父进程靠 File.Exists 判断，改名保证它看到的一定是完整文件。
                string temp = ReadyFile + ".tmp";
                File.WriteAllText(temp, "ready");
                if (File.Exists(ReadyFile)) File.Delete(ReadyFile);
                File.Move(temp, ReadyFile);
                Log.Write("ready file written: " + ReadyFile);
            }
            catch (Exception error)
            {
                Log.Write("ready file failed", error);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int preference = DwmwcpRound;
                this._nativeRounded = DwmSetWindowAttribute(this.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int)) == 0;
            }
            catch (Exception) { this._nativeRounded = false; }

            // 窗口是自绘的无边框窗，但 DWM 仍然会在窗口外沿画一圈系统边框（WS_THICKFRAME 带来的）。
            // 系统处于深色模式时，没声明深色支持的应用拿到的是浅色边框：窗口从屏幕外挪回来、
            // 页面首帧还没送到的那几百毫秒里，用户看到的就是四边一圈白框（实测 #F2F5FC，宽 8px）。
            // 声明深色 + 把边框色压到界面底色，这一圈就再也看不见了。
            try
            {
                int dark = 1;
                int hrDark = DwmSetWindowAttribute(this.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
                if (hrDark != 0)
                {
                    hrDark = DwmSetWindowAttribute(this.Handle, DwmwaUseImmersiveDarkModeOld, ref dark, sizeof(int));
                }
                int border = BorderColorRef;
                int hrBorder = DwmSetWindowAttribute(this.Handle, DwmwaBorderColor, ref border, sizeof(int));
                Log.Write("dwm attrs: dark=" + hrDark + " border=" + hrBorder + " nativeRound=" + this._nativeRounded);
            }
            catch (Exception) { }

            this.ApplyRoundedRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            this.ApplyRoundedRegion();
            this.SyncNormalBounds();
            this.BroadcastWindowState();
        }

        private Rectangle _normalBounds;
        private bool _wasMaximized;
        private bool _restoringBounds;
        private System.Windows.Forms.Timer _restoreTimer;
        private Rectangle _restoreTarget;

        /// <summary>
        /// 无边框窗（WS_THICKFRAME + 客户区吃满整个窗口）有个副作用：系统按「最大化矩形减去边框」推算还原尺寸，
        /// 而这里的边框是 0，于是每还原一次窗口就长大一圈（实测 1920x1020 → 1936x1036 → 1952x1052）。
        /// 自己记住最大化前的矩形，还原时按它摆回去。
        /// </summary>
        private void SyncNormalBounds()
        {
            if (this._restoringBounds) return;
            bool maximized = this.WindowState == FormWindowState.Maximized;
            if (maximized)
            {
                this._wasMaximized = true;
                return;
            }

            if (this._wasMaximized)
            {
                this._wasMaximized = false;
                if (this._normalBounds.IsEmpty == false) this.StartRestoreNormalBounds(this._normalBounds);
                return;
            }

            if (this._restoreTimer != null) return;
            if (this._revealed && this.Visible)
            {
                this._normalBounds = this.Bounds;
            }
        }

        /// <summary>
        /// 系统自己会把「还原矩形」再改一次（它按最大化矩形减边框推算，边框是 0 于是越还原越大），
        /// 单次赋值会被它随后覆盖。这里用短命计时器连按几帧，直到窗口真的停在记录值上。
        /// </summary>
        private void StartRestoreNormalBounds(Rectangle target)
        {
            this._restoreTarget = target;
            if (this._restoreTimer != null) return;
            var timer = new System.Windows.Forms.Timer();
            this._restoreTimer = timer;
            timer.Interval = 16;
            int ticks = 0;
            timer.Tick += delegate
            {
                ticks++;
                if (ticks > 40 || this.IsDisposed || this.WindowState != FormWindowState.Normal)
                {
                    timer.Stop();
                    timer.Dispose();
                    this._restoreTimer = null;
                    return;
                }
                if (this.Bounds == this._restoreTarget) return;
                try
                {
                    this._restoringBounds = true;
                    this.Bounds = this._restoreTarget;
                }
                finally { this._restoringBounds = false; }
            };
            timer.Start();
        }

        /// <summary>把最大化状态告诉页面：标题栏的「最大化 / 还原」图标靠它切换。</summary>
        private void BroadcastWindowState()
        {
            try
            {
                if (this._webReady == false) return;
                bool maximized = this.WindowState == FormWindowState.Maximized;
                if (maximized == this._lastMaximizedReport) return;
                this._lastMaximizedReport = maximized;
                Log.Write("window state maximized=" + maximized + " bounds=" + this.Bounds + " client=" + this.ClientSize +
                    " work=" + Screen.FromHandle(this.Handle).WorkingArea);
                this.Bridge.Send("{\"ev\":\"window\",\"maximized\":" + (maximized ? "true" : "false") + "}");
            }
            catch (Exception) { }
        }

        private bool _lastMaximizedReport;

        /// <summary>Windows 11 交给 DWM 圆角；旧系统用窗口区域裁出同样的圆角。</summary>
        private void ApplyRoundedRegion()
        {
            if (this._nativeRounded) return;
            try
            {
                if (this.IsHandleCreated == false) return;
                if (this.WindowState != FormWindowState.Normal)
                {
                    SetWindowRgn(this.Handle, IntPtr.Zero, true);
                    return;
                }

                int width = this.Width;
                int height = this.Height;
                if (width <= 0 || height <= 0) return;

                IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, CornerRadius * 2, CornerRadius * 2);
                if (region == IntPtr.Zero) return;
                SetWindowRgn(this.Handle, region, true);
            }
            catch (Exception) { }
        }

        /// <summary>WebView2 需要可写的用户数据目录；依次尝试多个候选位置。</summary>
        private static string ResolveDataFolder()
        {
            var candidates = new List<string>();
            try
            {
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SAM.Modern", "WebView2"));
            }
            catch (Exception) { }
            try { candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2Data")); }
            catch (Exception) { }
            try { candidates.Add(Path.Combine(Path.GetTempPath(), "SAM.Modern.WebView2")); }
            catch (Exception) { }

            foreach (string candidate in candidates)
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    string probe = Path.Combine(candidate, ".write-probe");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    Log.Write("WebView2 data folder: " + candidate);
                    return candidate;
                }
                catch (Exception error)
                {
                    Log.Write("WebView2 data folder unusable: " + candidate + " (" + error.GetType().Name + ")");
                }
            }

            throw new IOException("找不到可写的 WebView2 用户数据目录。");
        }

        private bool _pageReadyDone;
        private bool _paintedSeen;
        private System.Windows.Forms.Timer _paintTimer;

        /// <summary>
        /// 页面可以接收消息了：推积压消息 → 让子类推首屏数据。
        /// 幂等，两个信号源谁先到谁负责：
        ///   - 页面自己 postMessage 的 ui/ready（最早，比 NavigationCompleted 早 100ms 量级）
        ///   - CoreWebView2.NavigationCompleted（兜底，ui/ready 万一没发出来）
        /// 这里不亮窗口：亮窗口要等页面把首屏真正画出来（ui/painted），
        /// 否则新窗口会先露出 boot() 的空骨架，切换时看到的就是「库骨架屏一闪」。
        /// </summary>
        private void CompletePageReady()
        {
            if (this._pageReadyDone) return;
            this._pageReadyDone = true;
            this._webReady = true;

            this.Bridge.Flush();
            this.SendAppInfo();
            this.OnPageReady();
            try
            {
                this._scheduler.Start();
                this._scheduler.Publish();
            }
            catch (Exception error)
            {
                Log.Write("schedule: start failed", error);
            }
            this._lastMaximizedReport = false;
            this.BroadcastWindowState();
            this.StartPaintWatch();
        }

        /// <summary>
        /// 等页面回 ui/painted 再亮窗口；最多等 PaintTimeoutMs 毫秒，
        /// 免得脚本出错时窗口永远停在屏幕外。
        /// </summary>
        private void StartPaintWatch()
        {
            if (this._paintedSeen || this._paintTimer != null) return;
            this._paintTimer = new System.Windows.Forms.Timer();
            this._paintTimer.Interval = PaintTimeoutMs;
            this._paintTimer.Tick += delegate
            {
                this._paintTimer.Stop();
                this._paintTimer.Dispose();
                this._paintTimer = null;
                if (this._paintedSeen) return;
                Log.Write("ui/painted timeout, revealing anyway");
                this.MarkReady();
            };
            this._paintTimer.Start();
        }

        private const int PaintTimeoutMs = 1200;

        private void OnPainted()
        {
            this._paintedSeen = true;
            if (this._paintTimer != null)
            {
                this._paintTimer.Stop();
                this._paintTimer.Dispose();
                this._paintTimer = null;
            }
            this.MarkReady();
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess == false)
            {
                Log.Write("navigation failed: " + e.WebErrorStatus);
                return;
            }

            this.CompletePageReady();

            if (string.IsNullOrEmpty(ShotPath) == false)
            {
                this.CapturePreviewLater(ShotPath, ShotDelayMs);
            }
        }

        private async void CapturePreviewLater(string path, int delayMs)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(delayMs);

                if (string.IsNullOrEmpty(ShotScript) == false)
                {
                    try
                    {
                        await this.Web.CoreWebView2.ExecuteScriptAsync(ShotScript);
                        Log.Write("screenshot script executed");
                        await System.Threading.Tasks.Task.Delay(1200);
                    }
                    catch (Exception error)
                    {
                        Log.Write("screenshot script failed", error);
                    }
                }
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    await this.Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                }
                Log.Write("screenshot written: " + path);
            }
            catch (Exception error)
            {
                Log.Write("screenshot failed", error);
            }
        }

        protected virtual void OnPageReady()
        {
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json;
            try { json = e.WebMessageAsJson; }
            catch (Exception) { return; }

            Dictionary<string, object> message;
            try { message = JsonParser.ParseObject(json); }
            catch (Exception error)
            {
                Log.Write("bad message: " + json, error);
                return;
            }
            if (message == null) return;

            string cmd = JsonParser.GetString(message, "cmd");
            if (string.IsNullOrEmpty(cmd)) return;

            if (string.Equals(cmd, "ui/error", StringComparison.Ordinal))
            {
                Log.Write("ui " + JsonParser.GetString(message, "kind") + ": " +
                    JsonParser.GetString(message, "message") + " @ " + JsonParser.GetString(message, "source"));
                string stack = JsonParser.GetString(message, "stack");
                if (string.IsNullOrEmpty(stack) == false) Log.Write(stack);
                return;
            }
            if (string.Equals(cmd, "ui/ready", StringComparison.Ordinal))
            {
                Log.Write("ui reported ready");
                // 页面 load 事件比 NavigationCompleted 早 100ms 量级，是更好的就绪信号。
                this.CompletePageReady();
                return;
            }
            if (string.Equals(cmd, "ui/painted", StringComparison.Ordinal))
            {
                Log.Write("ui reported painted (layout=" + JsonParser.GetNumber(message, "layoutMs") +
                    "ms image=" + JsonParser.GetNumber(message, "imageMs") + "ms)");
                this.OnPainted();
                return;
            }

            if (this.HandleWindowCommand(cmd)) return;

            try
            {
                this.OnCommand(cmd, message);
            }
            catch (Exception error)
            {
                Log.Write("command failed: " + cmd, error);
                this.Bridge.Error("操作失败", error.Message);
            }
        }

        private bool HandleWindowCommand(string cmd)
        {
            switch (cmd)
            {
                case "win/minimize":
                    this.WindowState = FormWindowState.Minimized;
                    return true;
                case "win/maximize":
                    this.WindowState = this.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized;
                    return true;
                case "win/close":
                    this.Close();
                    return true;
                case "win/drag":
                    ReleaseCapture();
                    SendMessage(this.Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
                    return true;
                case "ui/error":
                    return true;
                case "ui/ready":
                    return true;
                case "win/state":
                    this.Bridge.Send("{\"ev\":\"window\",\"maximized\":" +
                        (this.WindowState == FormWindowState.Maximized ? "true" : "false") + "}");
                    return true;
            }
            return false;
        }

        protected virtual void OnCommand(string cmd, Dictionary<string, object> message)
        {
            // 定时解锁的建/删任务放在基类：游戏库窗口和游戏窗口都能用（子类末尾都会调 base.OnCommand）。
            if (string.Equals(cmd, "schedule/create", StringComparison.Ordinal))
            {
                this.CreateSchedule(message);
                return;
            }
            if (string.Equals(cmd, "schedule/createBatch", StringComparison.Ordinal))
            {
                this.CreateScheduleBatch(message);
                return;
            }
            if (string.Equals(cmd, "schedule/cancel", StringComparison.Ordinal))
            {
                this.CancelSchedule(message);
                return;
            }
            if (string.Equals(cmd, "schedule/run", StringComparison.Ordinal))
            {
                this.RunScheduleNow(message);
                return;
            }
            if (string.Equals(cmd, "schedule/pool", StringComparison.Ordinal))
            {
                this.SendSchedulePool(message);
                return;
            }
            if (string.Equals(cmd, "app/checkUpdate", StringComparison.Ordinal))
            {
                this.CheckUpdate();
                return;
            }
            if (string.Equals(cmd, "app/openUrl", StringComparison.Ordinal))
            {
                this.OpenUrlCommand(message);
                return;
            }
            Log.Write("unhandled command: " + cmd);
        }

        /// <summary>
        /// 把版本号、下载页地址告诉页面。版本号只有 AppInfo.Version 这一份，
        /// 页面上的「关于」直接显示它，不在 JS 里再写一遍。
        /// </summary>
        private void SendAppInfo()
        {
            var b = new JsonBuilder();
            b.BeginObject()
                .Name("ev").Str("appInfo")
                .Name("version").Str(AppInfo.Version)
                .Name("releasesUrl").Str(AppInfo.ReleasesUrl)
                .EndObject();
            this.Channel.Send(b);
        }

        /// <summary>
        /// 检查更新：HTTP 要好几秒，放后台线程，拿到结果再回 UI 线程发出去。
        /// 失败只算「检查失败」，不弹错误框。
        /// </summary>
        private void CheckUpdate()
        {
            this.Bridge.Status("正在检查更新…", true);
            System.Threading.Tasks.Task.Run(delegate
            {
                AppInfo.UpdateResult result = null;
                try
                {
                    result = AppInfo.CheckUpdate();
                }
                catch (Exception error)
                {
                    Log.Write("update: unexpected failure", error);
                }
                try
                {
                    this.BeginInvoke((Action)delegate
                    {
                        this.Bridge.Status(string.Empty, false);
                        this.SendUpdateResult(result);
                    });
                }
                catch (Exception)
                {
                    // 窗口已经关了，没人收这个结果，丢掉即可。
                }
            });
        }

        private void SendUpdateResult(AppInfo.UpdateResult result)
        {
            var b = new JsonBuilder();
            b.BeginObject()
                .Name("ev").Str("updateResult")
                .Name("current").Str(AppInfo.Version)
                .Name("ok").Bool(result != null && result.Ok);
            if (result == null || result.Ok == false)
            {
                string failure = result != null ? result.Failure : null;
                if (string.IsNullOrEmpty(failure)) failure = "检查更新失败，请稍后再试。";
                b.Name("failure").Str(failure);
            }
            else
            {
                b.Name("tag").Str(result.Tag)
                    .Name("name").Str(result.Name)
                    .Name("url").Str(result.Url)
                    .Name("body").Str(result.Body)
                    .Name("newer").Bool(IsNewerVersion(result.Tag, AppInfo.Version));
            }
            b.EndObject();
            this.Channel.Send(b);
        }

        private void OpenUrlCommand(Dictionary<string, object> message)
        {
            string url = JsonParser.GetString(message, "url");
            try
            {
                if (AppInfo.OpenUrl(url) == false)
                {
                    this.Bridge.Toast("warn", "打不开这个链接", "只支持 http / https 地址。");
                    return;
                }
                Log.Write("app: opened url " + url);
            }
            catch (Exception error)
            {
                Log.Write("app: open url failed " + url, error);
                this.Bridge.Toast("error", "打不开这个链接", "系统没能启动浏览器，可以手动打开：" + url);
            }
        }

        /// <summary>比较 "v1.9.10" 与 "1.9.9" 这类版本号；远程解析不出来就当没有新版。</summary>
        private static bool IsNewerVersion(string remote, string current)
        {
            int[] left = ParseVersion(remote);
            if (left == null) return false;
            int[] right = ParseVersion(current);
            if (right == null) return true;
            int count = Math.Max(left.Length, right.Length);
            for (int i = 0; i < count; i++)
            {
                int a = i < left.Length ? left[i] : 0;
                int c = i < right.Length ? right[i] : 0;
                if (a != c) return a > c;
            }
            return false;
        }

        private static int[] ParseVersion(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string value = text.Trim();
            if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V')) value = value.Substring(1);
            string[] parts = value.Split('.');
            var numbers = new List<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                int number;
                if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out number) == false) return null;
                numbers.Add(number);
            }
            return numbers.Count == 0 ? null : numbers.ToArray();
        }

        /// <summary>
        /// 把一条前端消息翻译成 ScheduleJob 并夹紧数值；不落盘。quiet 为真时不弹提示，
        /// 供批量建任务复用（一次选上百个游戏，否则会弹上百条 toast）。
        /// </summary>
        private ScheduleJob BuildSchedule(Dictionary<string, object> message, bool quiet)
        {
            long appId = (long)JsonParser.GetNumber(message, "appId", 0);
            if (appId <= 0)
            {
                if (quiet == false) this.Channel.Toast("warn", "无法定时", "没有有效的游戏 ID。");
                return null;
            }
            long at = (long)JsonParser.GetNumber(message, "at", 0);
            long now = ScheduleStore.Now();
            if (at <= now + 2)
            {
                if (quiet == false) this.Channel.Toast("warn", "无法定时", "目标时间太近或已经过去了，请换一个更晚的时刻。");
                return null;
            }
            if (at > now + 366L * 24 * 3600)
            {
                if (quiet == false) this.Channel.Toast("warn", "无法定时", "目标时间太远了，最多提前一年。");
                return null;
            }

            var job = new ScheduleJob();
            job.Id = ScheduleStore.NewId();
            job.AppId = appId;
            job.GameName = JsonParser.GetString(message, "gameName");
            if (string.IsNullOrEmpty(job.GameName)) job.GameName = "App " + appId;
            job.At = at;
            job.MinGap = (int)JsonParser.GetNumber(message, "minGap", 15);
            job.MaxGap = (int)JsonParser.GetNumber(message, "maxGap", 45);
            job.Count = (int)JsonParser.GetNumber(message, "count", 0);
            job.RareOnly = JsonParser.GetBool(message, "rareOnly", false);
            job.RarePct = JsonParser.GetNumber(message, "rarePct", 5.0);
            job.Order = (int)Math.Round(JsonParser.GetNumber(message, "order", 0));
            job.Jitter = (int)JsonParser.GetNumber(message, "jitter", 0);
            job.Language = JsonParser.GetString(message, "language");
            if (string.IsNullOrEmpty(job.Language)) job.Language = "english";
            job.CreatedAt = now;

            // 夹紧，防止前端传进离谱值让 worker 睡上好几天。
            // 间隔允许 0：前端「立刻解锁」传的就是 minGap = maxGap = 0，worker 会连着写完不等待。
            if (job.MinGap < 0) job.MinGap = 0;
            if (job.MaxGap < job.MinGap) job.MaxGap = job.MinGap;
            if (job.MaxGap > 7 * 24 * 3600) job.MaxGap = 7 * 24 * 3600;
            if (job.MinGap > job.MaxGap) job.MinGap = job.MaxGap;
            if (job.Count < 0) job.Count = 0;
            if (job.RarePct < 0) job.RarePct = 0;
            if (job.RarePct > 100) job.RarePct = 100;
            if (job.Jitter < 0) job.Jitter = 0;
            if (job.Jitter > 3600) job.Jitter = 3600;
            if (job.Order < 0) job.Order = 0;
            if (job.Order > 2) job.Order = 2;

            object rawList;
            if (message.TryGetValue("achievements", out rawList))
            {
                var items = rawList as List<object>;
                if (items != null)
                {
                    foreach (object item in items)
                    {
                        string id = item as string;
                        if (string.IsNullOrEmpty(id) == false && job.Achievements.Contains(id) == false)
                        {
                            job.Achievements.Add(id);
                        }
                    }
                }
            }

            return job;
        }

        /// <summary>
        /// 建一条定时任务并落盘。worker 进程在到点前由调度器拉起，本进程不需要保持这个游戏窗口打开。
        /// 但程序本身必须还在运行（调度器活在窗口进程里），界面文案要写明这一点。
        /// </summary>
        private void CreateSchedule(Dictionary<string, object> message)
        {
            ScheduleJob job = this.BuildSchedule(message, false);
            if (job == null) return;

            if (ScheduleStore.SaveJob(job) == false)
            {
                this.Channel.Toast("error", "无法定时", "任务文件写入失败，请检查程序目录是否可写。");
                return;
            }
            Log.Write("schedule: created " + job.Id + " appId=" + job.AppId + " at=" + job.At +
                      " count=" + job.Count + " explicit=" + job.Achievements.Count + " order=" + job.Order);
            this.Channel.Toast("success", "已排入定时解锁",
                "《" + job.GameName + "》将在 " + FormatScheduleTime(job.At) + " 开始解锁。");
            if (this._scheduler != null) this._scheduler.Publish();
        }

        /// <summary>
        /// 批量建定时任务：多个游戏在同一分钟开始解锁。每个游戏一个独立 worker 进程，
        /// 因为 Steam 客户端在一个进程里只能绑定一个 appId（换了会报 appID mismatch）。
        /// 参数为公共的 at/minGap/maxGap/jitter，games 是 [{appId,gameName,count,rareOnly,rarePct}]。
        /// </summary>
        private void CreateScheduleBatch(Dictionary<string, object> message)
        {
            long at = (long)JsonParser.GetNumber(message, "at", 0);
            object rawGames;
            var games = new List<object>();
            if (message.TryGetValue("games", out rawGames))
            {
                var items = rawGames as List<object>;
                if (items != null) games = items;
            }
            if (games.Count == 0)
            {
                this.Channel.Toast("warn", "无法定时", "没有选中任何游戏。");
                return;
            }

            int created = 0;
            int failed = 0;
            long now = ScheduleStore.Now();
            for (int i = 0; i < games.Count; i++)
            {
                var one = games[i] as Dictionary<string, object>;
                if (one == null)
                {
                    failed++;
                    continue;
                }
                // 每项都带上公共字段，BuildSchedule 只认一个扁平对象。
                one["at"] = (double)at;
                if (one.ContainsKey("minGap") == false) one["minGap"] = JsonParser.GetNumber(message, "minGap", 15);
                if (one.ContainsKey("maxGap") == false) one["maxGap"] = JsonParser.GetNumber(message, "maxGap", 45);
                if (one.ContainsKey("jitter") == false) one["jitter"] = JsonParser.GetNumber(message, "jitter", 0);
                if (one.ContainsKey("order") == false) one["order"] = JsonParser.GetNumber(message, "order", 0);
                if (one.ContainsKey("language") == false) one["language"] = JsonParser.GetString(message, "language");

                ScheduleJob job = this.BuildSchedule(one, true);
                if (job == null)
                {
                    failed++;
                    continue;
                }
                if (ScheduleStore.SaveJob(job))
                {
                    created++;
                }
                else
                {
                    failed++;
                }
            }

            Log.Write("schedule: batch created=" + created + " failed=" + failed + " at=" + at +
                      " total=" + games.Count);
            if (created > 0)
            {
                string when = FormatScheduleTime(at);
                this.Channel.Toast("success", "已排入定时解锁",
                    created + " 个游戏将在 " + when + " 开始解锁。" +
                    (failed > 0 ? "另有 " + failed + " 个没有排上。" : ""));
            }
            else
            {
                this.Channel.Toast("error", "无法定时", "一个任务都没有排上，请检查程序目录是否可写。");
            }
            if (this._scheduler != null) this._scheduler.Publish();
        }

        private void CancelSchedule(Dictionary<string, object> message)
        {
            string id = JsonParser.GetString(message, "id");
            if (string.IsNullOrEmpty(id))
            {
                this.Channel.Toast("warn", "无法取消", "没有指定要取消的任务。");
                return;
            }
            ScheduleStore.DeleteJob(id);
            Log.Write("schedule: cancelled " + id);
            this.Channel.Toast("success", "已取消定时", "这条定时解锁任务已删除。");
            if (this._scheduler != null) this._scheduler.Publish();
        }

        /// <summary>
        /// 「立刻开始」：把任务的时刻改成现在，调度器下一拍就会拉起 worker。
        /// 任务已经开始（认领文件还新鲜）时不动它，避免起第二个进程重复解锁。
        /// </summary>
        private void RunScheduleNow(Dictionary<string, object> message)
        {
            string id = JsonParser.GetString(message, "id");
            if (string.IsNullOrEmpty(id))
            {
                this.Channel.Toast("warn", "无法开始", "没有指定要开始的任务。");
                return;
            }
            var job = ScheduleStore.LoadJob(ScheduleStore.JobPath(id));
            if (job == null)
            {
                this.Channel.Toast("warn", "任务不在了", "这条定时任务已经结束或被取消了。");
                if (this._scheduler != null) this._scheduler.Publish();
                return;
            }
            if (this._scheduler != null && this._scheduler.IsClaimFresh(id))
            {
                this.Channel.Toast("info", "正在解锁", "这条任务已经开始了，不用再点一次。");
                return;
            }
            // 上一次尝试留下的认领 / 结果 / 进度都清掉，否则调度器会拿旧结果直接收尾。
            ScheduleStore.Delete(ScheduleStore.ClaimPath(id));
            ScheduleStore.Delete(ScheduleStore.ResultPath(id));
            ScheduleStore.Delete(ScheduleStore.ProgressPath(id));
            ScheduleStore.Delete(ScheduleStore.DonePath(id));
            job.At = ScheduleStore.Now();
            if (ScheduleStore.SaveJob(job) == false)
            {
                this.Channel.Toast("error", "无法开始", "任务文件写入失败，请重试。");
                return;
            }
            Log.Write("schedule: run now " + id);
            this.Channel.Toast("success", "已开始解锁", "《" + job.GameName + "》正在解锁，进度可以在任务列表里看。");
            if (this._scheduler != null) this._scheduler.Publish();
        }

        /// <summary>
        /// 按 appId 从本地缓存里读出这个游戏的成就清单，供「定时批量解锁」为每个游戏单独挑成就。
        /// 只读 cache/game-&lt;appId&gt;.json（以前打开过这个游戏才有），不连 Steam，
        /// 所以游戏库窗口里可以同步跑，不用等 stats 回调。
        /// </summary>
        private void SendSchedulePool(Dictionary<string, object> message)
        {
            long appId = (long)JsonParser.GetNumber(message, "appId", 0);
            var b = new JsonBuilder();
            b.BeginObject().Name("ev").Str("schedulePool").Name("appId").Num(appId);

            var items = new List<object>();
            bool cached = false;
            if (appId > 0)
            {
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache",
                        "game-" + appId.ToString(CultureInfo.InvariantCulture) + ".json");
                    if (File.Exists(path))
                    {
                        var root = JsonParser.ParseObject(File.ReadAllText(path));
                        if (root != null && (long)JsonParser.GetNumber(root, "appId", -1) == appId)
                        {
                            object raw;
                            if (root.TryGetValue("achievements", out raw))
                            {
                                var list = raw as List<object>;
                                if (list != null)
                                {
                                    cached = true;
                                    items = list;
                                }
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    Log.Write("schedule: pool read failed appId=" + appId, error);
                }
            }

            b.Name("cached").Bool(cached);
            b.Name("achievements").BeginArray();
            int listed = 0;
            foreach (object item in items)
            {
                var def = item as Dictionary<string, object>;
                if (def == null) continue;
                // 已经解锁的、受保护的成就没法（也不需要）再定时解锁，直接不列出来。
                if (JsonParser.GetBool(def, "achieved", false)) continue;
                if (JsonParser.GetBool(def, "protect", false)) continue;
                b.BeginObject()
                    .Name("id").Str(JsonParser.GetString(def, "id"))
                    .Name("name").Str(JsonParser.GetString(def, "name"))
                    .Name("rarity").Num(JsonParser.GetNumber(def, "rarity", -1.0))
                    .EndObject();
                listed++;
            }
            b.EndArray();
            b.EndObject();

            Log.Write("schedule: pool appId=" + appId + " cached=" + cached + " listed=" + listed);
            this.Channel.Send(b);
        }

        /// <summary>把 unix 秒按本机时区格式化成「MM-dd HH:mm」，只用于提示文案。</summary>
        private static string FormatScheduleTime(long unixSeconds)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
                    .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return unixSeconds.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// 无边框窗默认没有 WS_THICKFRAME，Windows 就不认它是「可缩放/可贴边」的窗口：
        /// 拖到屏幕顶部不会最大化、Win+方向键也不生效。补回该样式位（边框由我们自己绘制，不会露出来）。
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= WsThickFrame;
                return cp;
            }
        }

        /// <summary>
        /// 无边框窗最大化时，系统按整块显示器算尺寸（含任务栏区域），会把底栏压到任务栏底下。
        /// 这里按「工作区」给出最大化尺寸与位置。
        /// </summary>
        private void ApplyMaximizedBounds(ref Message m)
        {
            try
            {
                IntPtr monitor = MonitorFromWindow(this.Handle, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return;

                var info = new MonitorInfo();
                info.Size = Marshal.SizeOf(typeof(MonitorInfo));
                if (GetMonitorInfo(monitor, ref info) == false) return;

                var mmi = (MinMaxInfo)Marshal.PtrToStructure(m.LParam, typeof(MinMaxInfo));
                NativeRect work = info.Work;
                mmi.MaxPosition.X = work.Left - info.Monitor.Left;
                mmi.MaxPosition.Y = work.Top - info.Monitor.Top;
                mmi.MaxSize.X = work.Right - work.Left;
                mmi.MaxSize.Y = work.Bottom - work.Top;
                Marshal.StructureToPtr(mmi, m.LParam, false);
                m.Result = IntPtr.Zero;
            }
            catch (Exception error)
            {
                Log.Write("getminmaxinfo failed", error);
            }
        }

        /// <summary>DWMWA_BORDER_COLOR 用的是 COLORREF（0x00BBGGRR），和界面 --line 对齐。</summary>
        private static int BorderColorRef { get { return 0x00262626; } }

        protected override void WndProc(ref Message m)
        {
            // 兜底：任何情况下都别让系统用 COLOR_WINDOW（浅色）擦背景，
            // 一律用界面底色填，避免首帧出现浅色闪块。
            if (m.Msg == WmEraseBkgnd)
            {
                try
                {
                    using (var graphics = Graphics.FromHdc(m.WParam))
                    {
                        using (var brush = new SolidBrush(this.BackColor))
                        {
                            graphics.FillRectangle(brush, 0, 0, this.ClientSize.Width, this.ClientSize.Height);
                        }
                    }
                }
                catch (Exception) { }
                m.Result = (IntPtr)1;
                return;
            }

            if (m.Msg == WmGetMinMaxInfo)
            {
                this.ApplyMaximizedBounds(ref m);
                return;
            }

            // 为了恢复贴边/缩放能力加了 WS_THICKFRAME，但系统会因此画出一圈非客户区边框
            // （无边框窗里就是一圈没画过的浅灰，非常难看）。这里把客户区直接当成整个窗口：
            // 既保留系统的缩放/贴边判定，又不让边框露出来。
            if (m.Msg == WmNcCalcSize)
            {
                if (m.WParam != IntPtr.Zero)
                {
                    // wParam=TRUE：系统已经算好目标矩形（最大化时就是工作区），原样交回即可。
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
                return;
            }

            // WS_THICKFRAME 是恢复贴边/缩放能力的代价：系统会照常在非客户区画一圈缩放边框
            // （8px = SM_CXSIZEFRAME 4 + SM_CXPADDEDBORDER 4）。WM_NCCALCSIZE 只改布局矩形，
            // 挡不住 WM_NCPAINT 的绘制，于是这圈浅色边框会从 Form 的 8px Padding 环里露出来，
            // 看起来就是切换窗口时四边闪一圈白框。这里直接接管非客户区绘制：不画。
            if (m.Msg == WmNcPaint)
            {
                m.Result = IntPtr.Zero;
                return;
            }

            if (m.Msg == WmNcActivate)
            {
                // 激活/失活时系统会顺带重画 NC 边框，返回 1 表示“我自己处理了”，别再画。
                m.Result = (IntPtr)1;
                return;
            }

            if (m.Msg == WmNcHitTest && this.WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1)
                {
                    var screen = new Point(((int)m.LParam) & 0xFFFF, ((int)m.LParam) >> 16);
                    var p = this.PointToClient(screen);
                    int b = ResizeBorder;
                    bool left = p.X <= b, right = p.X >= this.ClientSize.Width - b;
                    bool top = p.Y <= b, bottom = p.Y >= this.ClientSize.Height - b;
                    if (top && left) m.Result = (IntPtr)HtTopLeft;
                    else if (top && right) m.Result = (IntPtr)HtTopRight;
                    else if (bottom && left) m.Result = (IntPtr)HtBottomLeft;
                    else if (bottom && right) m.Result = (IntPtr)HtBottomRight;
                    else if (left) m.Result = (IntPtr)HtLeft;
                    else if (right) m.Result = (IntPtr)HtRight;
                    else if (top) m.Result = (IntPtr)HtTop;
                    else if (bottom) m.Result = (IntPtr)HtBottom;
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            int width = this.ClientSize.Width;
            int height = this.ClientSize.Height;
            if (width < 4 || height < 4) return;

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int radius = CornerRadius;
            int d = radius * 2;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(width - d - 1, 0, d, d, 270, 90);
                path.AddArc(width - d - 1, height - d - 1, d, d, 0, 90);
                path.AddArc(0, height - d - 1, d, d, 90, 90);
                path.CloseFigure();
                using (var pen = new Pen(Color.FromArgb(76, 70, 120)))
                {
                    e.Graphics.DrawPath(pen, path);
                }
            }
        }
    }
}
