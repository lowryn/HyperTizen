using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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

        public async Task StartAsync()
        {
            _httpListener.Start();
            while (true)
            {
                var httpContext = await _httpListener.GetContextAsync();
                if (httpContext.Request.IsWebSocketRequest)
                {
                    var wsContext = await httpContext.AcceptWebSocketAsync(null);
                    _ = HandleWebSocketAsync(wsContext.WebSocket);
                }
                else
                {
                    httpContext.Response.StatusCode = 400;
                    httpContext.Response.Close();
                }
            }
        }

        private async Task HandleWebSocketAsync(System.Net.WebSockets.WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            while (result.MessageType != WebSocketMessageType.Close)
            {
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await OnMessageAsync(webSocket, message);
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }

            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
        }

        protected async Task OnMessageAsync(System.Net.WebSockets.WebSocket webSocket, string message)
        {
            BasicEvent data = JsonConvert.DeserializeObject<BasicEvent>(message);

            switch (data.Event)
            {
                case Event.ScanSSDP:
                    {
                        var devices = await ScanSSDPAsync();
                        string resultEvent = JsonConvert.SerializeObject(new SSDPScanResultEvent(devices));
                        await SendAsync(webSocket, resultEvent);
                        break;
                    }

                case Event.ReadConfig:
                    {
                        ReadConfigEvent readConfigEvent = JsonConvert.DeserializeObject<ReadConfigEvent>(message);
                        string result = await ReadConfigAsync(readConfigEvent);
                        await SendAsync(webSocket, result);
                        break;
                    }

                case Event.SetConfig:
                    {
                        SetConfigEvent setConfigEvent = JsonConvert.DeserializeObject<SetConfigEvent>(message);
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
            if (!Preference.Contains(readConfigEvent.key))
            {
                result = JsonConvert.SerializeObject(new ReadConfigResultEvent(true, readConfigEvent.key, "Key doesn't exist."));
            }
            else
            {
                string value = Preference.Get<string>(readConfigEvent.key);
                result = JsonConvert.SerializeObject(new ReadConfigResultEvent(false, readConfigEvent.key, value));
            }
            return result;
        }

        private async Task SendAsync(System.Net.WebSockets.WebSocket webSocket, string message)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        void SetConfiguration(SetConfigEvent setConfigEvent)
        {
            switch (setConfigEvent.key)
            {
                case "rpcServer":
                    {
                        App.Configuration.RPCServer = setConfigEvent.value;
                        App.client.UpdateURI(setConfigEvent.value);
                        break;
                    }
                case "enabled":
                    {
                        bool value = bool.Parse(setConfigEvent.value);
                        if (!App.Configuration.Enabled && value)
                        {
                            App.Configuration.Enabled = value;
                            Task.Run(() => App.client.Start(value));
                        }
                        else App.Configuration.Enabled = value;
                        break;
                    }
            }

            Preference.Set(setConfigEvent.key, setConfigEvent.value);
        }
    }

    public static class WebSocketServer
    {
        public static async Task StartServerAsync()
        {
            var wsServer = new WSServer("http://+:8086/");
            await wsServer.StartAsync();
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

        public async Task ConnectAsync()
        {
            await client.ConnectAsync(new Uri(uri), CancellationToken.None);
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