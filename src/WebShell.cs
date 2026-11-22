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
            this.OnPageReady();
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
            Log.Write("unhandled command: " + cmd);
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

        protected override void WndProc(ref Message m)
        {
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
