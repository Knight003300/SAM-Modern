using System;
using System.Collections.Generic;
using Microsoft.Web.WebView2.WinForms;

namespace SAM.Modern
{
    /// <summary>页面 ⇄ 后端的消息通道。见 modern/BRIDGE.md。</summary>
    internal sealed class Bridge
    {
        private readonly WebView2 _web;
        private readonly List<string> _queue = new List<string>();

        public Bridge(WebView2 web)
        {
            this._web = web;
        }

        public bool Ready
        {
            get
            {
                try { return this._web != null && this._web.CoreWebView2 != null; }
                catch (Exception) { return false; }
            }
        }

        public void Send(string json)
        {
            if (json == null) return;
            if (this.Ready == false)
            {
                if (this._queue.Count < 200) this._queue.Add(json);
                return;
            }
            try
            {
                this._web.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception error)
            {
                Log.Write("postMessage failed", error);
            }
        }

        public void Send(JsonBuilder builder)
        {
            this.Send(builder.ToString());
        }

        /// <summary>页面加载完成后补发积压消息。</summary>
        public void Flush()
        {
            if (this.Ready == false || this._queue.Count == 0) return;
            var items = new List<string>(this._queue);
            this._queue.Clear();
            foreach (string item in items)
            {
                try { this._web.CoreWebView2.PostWebMessageAsJson(item); }
                catch (Exception error) { Log.Write("flush failed", error); }
            }
        }

        public void Status(string text, bool busy)
        {
            var b = new JsonBuilder();
            b.BeginObject().Name("ev").Str("status").Name("text").Str(text).Name("busy").Bool(busy).EndObject();
            this.Send(b);
        }

        public void Toast(string kind, string title, string text)
        {
            var b = new JsonBuilder();
            b.BeginObject().Name("ev").Str("toast").Name("kind").Str(kind)
                .Name("title").Str(title).Name("text").Str(text).EndObject();
            this.Send(b);
        }

        public void Error(string title, string text)
        {
            var b = new JsonBuilder();
            b.BeginObject().Name("ev").Str("error").Name("title").Str(title).Name("text").Str(text).EndObject();
            this.Send(b);
        }
    }
}
