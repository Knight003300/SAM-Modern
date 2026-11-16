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

        protected readonly WebView2 Web = new WebView2();
        protected readonly Bridge Bridge;
        private readonly string _uiDirectory;
        private CoreWebView2Environment _environment;
        private bool _webReady;

        public WebShell(string title)
        {
            this.Text = title;
            this.FormBorderStyle = FormBorderStyle.None;
            this.MinimumSize = new Size(920, 620);
            this.Size = new Size(1280, 840);
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
                this.ShowInTaskbar = false;
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

            this.ShowInTaskbar = true;
            Size targetSize = this._explicitBounds ? StartupBounds.Size : new Size(1280, 840);
            if (targetSize.Width < 320 || targetSize.Height < 240) targetSize = new Size(1280, 840);

            Point targetLocation;
            if (this._explicitBounds)
            {
                targetLocation = StartupBounds.Location;
            }
            else
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                targetLocation = new Point(
                    area.Left + Math.Max(0, (area.Width - targetSize.Width) / 2),
                    area.Top + Math.Max(0, (area.Height - targetSize.Height) / 2));
            }

            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = new Rectangle(targetLocation, targetSize);

            if (this.Visible == false) this.Show();
            if (StartupMaximized) this.WindowState = FormWindowState.Maximized;

            this.Activate();
            this.BringToFront();
            this.ApplyRoundedRegion();
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

            try
            {
                var dataFolder = ResolveDataFolder();

                this._environment = await CoreWebView2Environment.CreateAsync(null, dataFolder, null);
                await this.Web.EnsureCoreWebView2Async(this._environment);

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
                core.Navigate("https://sam.ui/index.html");
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

            // %TEMP% / %LOCALAPPDATA% 在本机对被调起的进程不可写，握手文件必须放在程序目录下。
            string ready = ReadyFilePath();

            Rectangle bounds = this.WindowState == FormWindowState.Maximized
                ? this.RestoreBounds
                : this.Bounds;
            bool maximized = this.WindowState == FormWindowState.Maximized;
            if (bounds.Width < 320 || bounds.Height < 240) bounds = Rectangle.Empty;

            try
            {
                var info = new System.Diagnostics.ProcessStartInfo();
                info.FileName = Application.ExecutablePath;
                info.UseShellExecute = false;
                info.Arguments = Relay.BuildArguments(appId, ready, bounds, maximized);
                this._relayChild = System.Diagnostics.Process.Start(info);
                Log.Write("relay launch appId=" + appId + " pid=" + (this._relayChild == null ? 0 : this._relayChild.Id) + " args=" + info.Arguments);
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
            this._relayDeadline = DateTime.UtcNow.AddSeconds(25);
            if (this._relayTimer == null)
            {
                this._relayTimer = new Timer();
                this._relayTimer.Interval = 60;
                this._relayTimer.Tick += delegate { this.PollRelay(readyFile); };
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
                Log.Write("relay ready, closing old window");
                this._relayChild = null;
                this.Close();
                return;
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

        /// <summary>页面就绪后写文件通知父窗口（用于父子窗口无缝切换）。</summary>
        private void WriteReadyFile()
        {
            if (string.IsNullOrEmpty(ReadyFile)) return;
            this.RevealWindow();
            try
            {
                File.WriteAllText(ReadyFile, "ready");
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
        }

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

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess == false)
            {
                Log.Write("navigation failed: " + e.WebErrorStatus);
                return;
            }

            this._webReady = true;
            await this.Web.CoreWebView2.ExecuteScriptAsync(
                "if (window.SAM && window.SAM.handleEvent) { window.SAM.ready = true; }");
            this.Bridge.Flush();
            this.OnPageReady();
            this.WriteReadyFile();

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

        protected override void WndProc(ref Message m)
        {
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
