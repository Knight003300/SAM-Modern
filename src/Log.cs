using System;
using System.IO;
using System.Text;

namespace SAM.Modern
{
    internal static class Log
    {
        private static readonly object Lock = new object();
        private static string _Path;

        public static string Directory
        {
            get
            {
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return Path.GetDirectoryName(exe);
            }
        }

        public static void Init()
        {
            try
            {
                _Path = Path.Combine(Directory, "sam-modern.log");
                if (File.Exists(_Path) && new FileInfo(_Path).Length > 2 * 1024 * 1024)
                {
                    File.Delete(_Path);
                }
            }
            catch (Exception) { }
        }

        public static void Write(string message)
        {
            try
            {
                if (_Path == null) return;
                lock (Lock)
                {
                    File.AppendAllText(_Path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception) { }
        }

        public static void Write(string message, Exception error)
        {
            Write(message + " :: " + error.GetType().Name + ": " + error.Message + Environment.NewLine + error.StackTrace);
        }
    }
}
