using System;
using SAM.API;

namespace SAM.Modern
{
    /// <summary>Steam 会话封装：初始化、回调泵、基础查询。</summary>
    internal sealed class SteamHost : IDisposable
    {
        private readonly Client _client = new Client();

        public Client Client { get { return this._client; } }
        public bool Initialized { get; private set; }
        public long AppId { get; private set; }

        public void Initialize(long appId)
        {
            this.AppId = appId;

            // 返回游戏库时是「接力」出来的新进程：UseShellExecute=false 启动的子进程会继承父进程
            // 设过的 SteamAppId 环境变量，steamclient 会继续以那个游戏的身份建立管道，于是 Steam
            // 一直认为那个游戏还在运行（游戏库里的「运行中」不消失）。游戏库进程必须显式清掉它。
            if (appId <= 0)
            {
                Environment.SetEnvironmentVariable("SteamAppId", null);
            }

            Log.Write("steam init appId=" + appId
                + " env SteamAppId=" + (Environment.GetEnvironmentVariable("SteamAppId") ?? "<null>"));

            this._client.Initialize(appId);
            this.Initialized = true;

            uint reported = 0;
            try { reported = this._client.SteamUtils.GetAppId(); }
            catch (Exception) { }
            Log.Write("Steam client initialized for appId " + appId + " (steam reports appId " + reported + ")");
        }

        public void Pump()
        {
            try
            {
                this._client.RunCallbacks(false);
            }
            catch (Exception error)
            {
                Log.Write("RunCallbacks failed", error);
            }
        }

        public ulong SteamId
        {
            get
            {
                try { return this._client.SteamUser.GetSteamId(); }
                catch (Exception) { return 0; }
            }
        }

        public string GameLanguage
        {
            get
            {
                try { return this._client.SteamApps008.GetCurrentGameLanguage() ?? "english"; }
                catch (Exception) { return "english"; }
            }
        }

        public string GetAppData(uint appId, string key)
        {
            try { return this._client.SteamApps001.GetAppData(appId, key); }
            catch (Exception) { return null; }
        }

        public bool IsSubscribed(uint appId)
        {
            try { return this._client.SteamApps008.IsSubscribedApp(appId); }
            catch (Exception) { return false; }
        }

        public void Dispose()
        {
            try { this._client.Dispose(); }
            catch (Exception) { }
        }
    }
}
