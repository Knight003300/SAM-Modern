using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace SAM.Modern
{
    /// <summary>
    /// 进程接力：Steam 的 steamclient 模块在进程内只能绑定一个 AppID，
    /// 所以切换游戏必须换进程。这里负责把窗口状态编码成子进程参数，
    /// 让新窗口沿用旧窗口的位置尺寸，做到视觉上的「同一个窗口在切换」。
    /// </summary>
    internal static class Relay
    {
        public static string BuildArguments(long appId, string readyFile, Rectangle bounds, bool maximized, int parentPid)
        {
            var sb = new StringBuilder();
            if (appId > 0)
            {
                sb.Append(appId.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("picker");
            }

            sb.Append(" --hidden");
            sb.Append(" --ready \"").Append(readyFile ?? string.Empty).Append('"');

            if (bounds.IsEmpty == false)
            {
                sb.Append(" --bounds ")
                  .Append(bounds.X.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(bounds.Y.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(bounds.Width.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(bounds.Height.ToString(CultureInfo.InvariantCulture));
            }

            if (maximized) sb.Append(" --max");
            if (parentPid > 0)
            {
                sb.Append(" --parent-pid ").Append(parentPid.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 父子进程的「唤回」握手文件：子窗口想回游戏库时写这个文件，父窗口轮询到后把自己显示出来。
        /// 路径按父进程 PID 命名，双方都能算出来，不需要额外传参。
        /// 目录规则与 ready 文件一致：程序目录下的 relay 子目录（%TEMP% 对子进程可能不可写）。
        /// </summary>
        public static string ResumeFilePath(int parentPid)
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
            return Path.Combine(folder, "sam-modern-resume-" + parentPid.ToString(CultureInfo.InvariantCulture) + ".txt");
        }

        /// <summary>解析 "--bounds 10,20,1280,840" 这类参数。</summary>
        public static bool TryParseBounds(string text, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 4) return false;
            int x, y, w, h;
            if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) == false) return false;
            if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y) == false) return false;
            if (int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out w) == false) return false;
            if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out h) == false) return false;
            if (w <= 0 || h <= 0) return false;
            bounds = new Rectangle(x, y, w, h);
            return true;
        }
    }
}
