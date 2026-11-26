using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace SAM.Modern
{
    /// <summary>
    /// 程序自身的版本号与「检查更新」。版本号只有这一份：页面从 appInfo 事件里拿，
    /// 不在 JS 里再写一遍，免得发版时忘改一处。
    /// </summary>
    internal static class AppInfo
    {
        public const string Version = "1.9.10";
        public const string ReleasesUrl = "https://github.com/Knight003300/SAM-Modern/releases/latest";
        private const string LatestApi = "https://api.github.com/repos/Knight003300/SAM-Modern/releases/latest";

        public sealed class UpdateResult
        {
            public bool Ok;
            public string Failure;
            public string Tag;
            public string Name;
            public string Url;
            public string Body;
        }

        /// <summary>
        /// 问 GitHub 要最新 release。这段在后台线程跑（会阻塞几秒），结果由调用方回到 UI 线程再发。
        /// 没网、被墙、被限流都只算「检查失败」，不影响程序其它功能。
        /// </summary>
        public static UpdateResult CheckUpdate()
        {
            var result = new UpdateResult();
            try
            {
                string text = Download(LatestApi);
                var root = JsonParser.ParseObject(text);
                if (root == null)
                {
                    result.Failure = "更新服务器返回的内容看不懂。";
                    return result;
                }
                result.Tag = Trim(JsonParser.GetString(root, "tag_name"));
                result.Name = Trim(JsonParser.GetString(root, "name"));
                result.Url = Trim(JsonParser.GetString(root, "html_url"));
                result.Body = Trim(JsonParser.GetString(root, "body"));
                if (string.IsNullOrEmpty(result.Tag))
                {
                    result.Failure = "更新服务器没有给出最新版本号。";
                    return result;
                }
                if (string.IsNullOrEmpty(result.Url)) result.Url = ReleasesUrl;
                result.Ok = true;
                Log.Write("update: latest tag=" + result.Tag);
                return result;
            }
            catch (Exception error)
            {
                Log.Write("update: check failed", error);
                result.Failure = Describe(error);
                return result;
            }
        }

        /// <summary>用系统默认浏览器打开链接。只放行 http/https，避免把任意字符串丢给 shell。</summary>
        public static bool OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            bool web = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            if (web == false) return false;
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }

        private static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Trim();
        }

        private static string Describe(Exception error)
        {
            var web = error as WebException;
            if (web != null)
            {
                var response = web.Response as HttpWebResponse;
                if (response != null)
                {
                    int code = (int)response.StatusCode;
                    if (code == 403 || code == 429) return "请求太频繁了，过几分钟再试。";
                    if (code == 404) return "更新地址失效了，请到项目主页手动查看。";
                    return "更新服务器返回错误（HTTP " + code.ToString(CultureInfo.InvariantCulture) + "）。";
                }
                return "连不上更新服务器，请检查网络后重试。";
            }
            if (error is WebException || error is IOException) return "连不上更新服务器，请检查网络后重试。";
            return "检查更新时出错：" + error.Message;
        }

        private static string Download(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            var request = (HttpWebRequest)WebRequest.Create(url);
            // GitHub API 必须带 UA，否则直接 403。
            request.UserAgent = "SAM.Modern/" + Version;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            request.KeepAlive = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
