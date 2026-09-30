using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HyperTizen.WebSocket.DataTypes;
using Rssdp;
using Tizen.Applications;
using static HyperTizen.WebSocket.DataTypes.SSDPScanResultEvent;

namespace HyperTizen.WebSocket
{
    public class WSServer
    {
        private HttpListener _httpListener;
        private List<string> usnList = new List<string>()
        {
            "urn:hyperion-project.org:device:basic:1",
            "urn:hyperhdr.eu:device:basic:1"
        };

        public WSServer(string uriPrefix)
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(uriPrefix);
        }

        public void Start()
        {
            _httpListener.Start();
        }

        public void Stop()
        {
            try { _httpListener.Close(); } catch { }
        }

        public async Task RunAsync()
        {
            while (true)
            {
                try
                {
                    var httpContext = await _httpListener.GetContextAsync();
                    if (httpContext.Request.IsWebSocketRequest)
                    {
                        var wsContext = await httpContext.AcceptWebSocketAsync(null);
                        _ = HandleWebSocketAsync(wsContext.WebSocket);
                    }
                    else if (httpContext.Request.Url.AbsolutePath == "/set")
                    {
                        string key = httpContext.Request.QueryString["key"];
                        string value = httpContext.Request.QueryString["value"];
                        string reply;
                        if (string.IsNullOrEmpty(key) || value == null)
                        {
                            httpContext.Response.StatusCode = 400;
                            reply = "usage: /set?key=enabled&value=true";
                        }
                        else
                        {
                            Diag.Log($"HTTP /set key={key} value={value}");
                            SetConfiguration(new SetConfigEvent { Event = Event.SetConfig, key = key, value = value });
                            reply = "ok";
                        }
                        byte[] okBody = Encoding.UTF8.GetBytes(reply);
                        httpContext.Response.ContentType = "text/plain; charset=utf-8";
                        httpContext.Response.ContentLength64 = okBody.Length;
                        httpContext.Response.OutputStream.Write(okBody, 0, okBody.Length);
                        httpContext.Response.Close();
                    }
                    else if (httpContext.Request.Url.AbsolutePath == "/frame.bmp")
                    {
                        byte[] bmp = null;
                        string frameError = "capture returned no frame (see /logs)";
                        try { bmp = VideoCapture.SnapshotBmp(); }
                        catch (Exception ex) { frameError = ex.GetType().Name + " " + ex.Message; }

                        if (bmp == null)
                        {
                            Diag.Log("GET /frame.bmp failed: " + frameError);
                            byte[] msg = Encoding.UTF8.GetBytes(frameError);
                            httpContext.Response.StatusCode = 503;
                            httpContext.Response.ContentType = "text/plain; charset=utf-8";
                            httpContext.Response.ContentLength64 = msg.Length;
                            httpContext.Response.OutputStream.Write(msg, 0, msg.Length);
                        }
                        else
                        {
                            httpContext.Response.ContentType = "image/bmp";
                            httpContext.Response.ContentLength64 = bmp.Length;
                            httpContext.Response.OutputStream.Write(bmp, 0, bmp.Length);
                        }
                        httpContext.Response.Close();
                    }
                    else if (httpContext.Request.Url.AbsolutePath == "/logs")
                    {
                        byte[] body = Encoding.UTF8.GetBytes(Diag.Dump());
                        httpContext.Response.ContentType = "text/plain; charset=utf-8";
                        httpContext.Response.ContentLength64 = body.Length;
                        httpContext.Response.OutputStream.Write(body, 0, body.Length);
                        httpContext.Response.Close();
                    }
                    else
                    {
                        httpContext.Response.StatusCode = 400;
                        httpContext.Response.Close();
                    }
                }
                catch (ObjectDisposedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Diag.Log("Control server request failed: " + ex.Message);
                }
            }
        }

        private async Task HandleWebSocketAsync(System.Net.WebSockets.WebSocket webSocket)
        {
            Diag.Log("Control WS: client connected");
            try
            {
                var buffer = new byte[1024 * 4];
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                while (result.MessageType != WebSocketMessageType.Close)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Diag.Log("Control WS: received " + message);
                    await OnMessageAsync(webSocket, message);
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }

                Diag.Log("Control WS: client closed");
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Diag.Log("Control WS: handler failed: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        protected async Task OnMessageAsync(System.Net.WebSockets.WebSocket webSocket, string message)
        {
            var fields = MiniJson.ParseObject(message);
            Event eventType = MiniJson.ParseEvent(fields);

            switch (eventType)
            {
                case Event.ScanSSDP:
                    {
                        var devices = await ScanSSDPAsync();
                        string resultEvent = MiniJson.SsdpScanResult(devices);
                        await SendAsync(webSocket, resultEvent);
                        break;
                    }

                case Event.ReadConfig:
                    {
                        ReadConfigEvent readConfigEvent = new ReadConfigEvent { Event = Event.ReadConfig, key = MiniJson.Field(fields, "key") };
                        string result = await ReadConfigAsync(readConfigEvent);
                        await SendAsync(webSocket, result);
                        break;
                    }

                case Event.SetConfig:
                    {
                        SetConfigEvent setConfigEvent = new SetConfigEvent { Event = Event.SetConfig, key = MiniJson.Field(fields, "key"), value = MiniJson.Field(fields, "value") };
                        SetConfiguration(setConfigEvent);
                        break;
                    }
            }
        }

        private async Task<List<SSDPDevice>> ScanSSDPAsync()
        {
            var devices = new List<SSDPDevice>();
            using (var deviceLocator = new SsdpDeviceLocator())
            {
                var foundDevices = await deviceLocator.SearchAsync();
                foreach (var foundDevice in foundDevices)
                {
                    if (!usnList.Contains(foundDevice.NotificationType)) continue;

                    var fullDevice = await foundDevice.GetDeviceInfo();
                    Uri descLocation = foundDevice.DescriptionLocation;
                    devices.Add(new SSDPDevice(fullDevice.FriendlyName, descLocation.OriginalString.Replace(descLocation.PathAndQuery, "")));
                }
            }
            return devices;
        }

        private async Task<string> ReadConfigAsync(ReadConfigEvent readConfigEvent)
        {
            string result;
            string live = ReadLiveValue(readConfigEvent.key);
            if (live != null)
            {
                result = MiniJson.ReadConfigResult(false, readConfigEvent.key, live);
            }
            else if (!Preference.Contains(readConfigEvent.key))
            {
                result = MiniJson.ReadConfigResult(true, readConfigEvent.key, "Key doesn't exist.");
            }
            else
            {
                string value = Preference.Get<string>(readConfigEvent.key);
                result = MiniJson.ReadConfigResult(false, readConfigEvent.key, value);
            }
            return result;
        }

        private async Task SendAsync(System.Net.WebSockets.WebSocket webSocket, string message)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        // Runtime state that is not stored in Preference; null when the key is not a live one.
        private static string ReadLiveValue(string key)
        {
            switch (key)
            {
                case "connected":
                    return Networking.IsConnected ? "true" : "false";
                case "capturing":
                    return App.client != null && App.client.IsRunning ? "true" : "false";
                case "logs":
                    return Diag.Tail(25);
                default:
                    return null;
            }
        }

        void SetConfiguration(SetConfigEvent setConfigEvent)
        {
            switch (setConfigEvent.key)
            {
                case "fbsServer":
                    {
                        Preference.Set(setConfigEvent.key, setConfigEvent.value);
                        try { Networking.Disconnect(); } catch (Exception ex) { Diag.Log("fbsServer change: disconnect failed: " + ex.Message); }
                        return;
                    }
                case "rpcServer":
                    {
                        App.Configuration.RPCServer = setConfigEvent.value;
                        // Persist first so the reconnect path picks up the new URI
                        Preference.Set(setConfigEvent.key, setConfigEvent.value);
                        App.client.UpdateURI(setConfigEvent.value);
                        return;
                    }
                case "enabled":
                    {
                        bool value = bool.Parse(setConfigEvent.value);
                        bool previous = App.Configuration.Enabled;
                        App.Configuration.Enabled = value;
                        Preference.Set(setConfigEvent.key, setConfigEvent.value);
                        if (value && !previous)
                            _ = App.client.Start();
                        else if (!value && previous)
                            _ = App.client.Stop();
                        return;
                    }
            }

            Preference.Set(setConfigEvent.key, setConfigEvent.value);
        }
    }

    public static class WebSocketServer
    {
        private static readonly string[] Prefixes =
        {
            "http://*:8086/",
            "http://+:8086/",
        };

        // Keeps trying: the network stack may not be ready right after boot,
        // and a prefix can be refused on some firmware versions.
        public static async Task StartServerAsync()
        {
            while (true)
            {
                foreach (string prefix in Prefixes)
                {
                    WSServer server = null;
                    try
                    {
                        server = new WSServer(prefix);
                        server.Start();
                        Diag.Log("Control server listening on " + prefix);
                        await server.RunAsync();
                    }
                    catch (Exception ex)
                    {
                        Diag.Log("Control server on " + prefix + " failed: " + ex.GetType().Name + " " + ex.Message);
                        server?.Stop();
                    }
                }

                await Task.Delay(5000);
            }
        }
    }

    public class WebSocketClient
    {
        private string uri;
        public ClientWebSocket client;
        private byte errorTimes = 0;

        public WebSocketClient(string uri)
        {
            this.uri = uri;
            client = new ClientWebSocket();
            client.Options.KeepAliveInterval = TimeSpan.Zero; // prevent unsolicited pong frames
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            // Bound the connect attempt so a black-hole server can't hang us
            // forever when called from the capture loop's reconnect path.
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(new Uri(uri), connectCts.Token);
            }
            _ = ReceiveMessagesAsync();
        }

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[1024 * 4];
            while (client.State == WebSocketState.Open)
            {
                var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                }
                else
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    OnMessage(message);
                }
            }
        }

        private void OnMessage(string message)
        {

        }
    }
}