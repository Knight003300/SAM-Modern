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
            this._client.Initialize(appId);
            this.Initialized = true;
            Log.Write("Steam client initialized for appId " + appId);
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
