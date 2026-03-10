using Newtonsoft.Json;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tizen.Applications;
using HyperTizen.WebSocket.DataTypes;

namespace HyperTizen.WebSocket
{
    internal class HyperionClient
    {
        private readonly bool _useSecVideoCapture;
        private WebSocketClient _wsClient;

        public HyperionClient()
        {
            _useSecVideoCapture = TryInitSecVideoCapture();

            if (_useSecVideoCapture)
            {
                VideoCapture.InitCapture();
                Task.Run(() => Start());
            }
            else
            {
                string rpcServer = ResolveRpcServer();
                if (rpcServer == null) return;
                _wsClient = new WebSocketClient(rpcServer);
                Task.Run(() => _wsClient.ConnectAsync());
                Task.Run(() => Start());
            }
        }

        // Returns a ws:// URL for HyperHDR — from stored preference, or auto-discovered via SSDP.
        private string ResolveRpcServer()
        {
            if (Preference.Contains("rpcServer"))
                return Preference.Get<string>("rpcServer");

            Tizen.Log.Debug("HyperTizen", "rpcServer not set, attempting SSDP auto-discovery...");
            (string ip, _) = SsdpDiscovery.GetHyperIpAndPort();
            if (ip == null)
            {
                Tizen.Log.Debug("HyperTizen", "SSDP: no HyperHDR found — configure rpcServer via UI");
                return null;
            }

            // Use HyperHDR's standard JSON/WebSocket port (19400) as default
            string url = $"ws://{ip}:19400/";
            Preference.Set("rpcServer", url);
            App.Configuration.RPCServer = url;
            Tizen.Log.Debug("HyperTizen", $"SSDP auto-discovered rpcServer: {url}");
            return url;
        }

        private bool TryInitSecVideoCapture()
        {
            try
            {
                if (SDK.SystemInfo.TizenVersionMajor < 8)
                {
                    Tizen.Log.Debug("HyperTizen", "cap_mode: libve (Tizen < 8)");
                    Preference.Set("cap_mode", "libve");
                    return false;
                }
                SDK.SecVideoCaptureT8.Init();
                Tizen.Log.Debug("HyperTizen", "cap_mode: secvideo (NV12 FlatBuffers TCP)");
                Preference.Set("cap_mode", "secvideo");
                return true;
            }
            catch (Exception ex)
            {
                Tizen.Log.Debug("HyperTizen", "cap_mode: libve (fallback) — " + ex.Message);
                Preference.Set("cap_mode", "libve");
                return false;
            }
        }

        public async Task Start(bool shouldStart = false)
        {
            if (_useSecVideoCapture)
                await StartSecVideo(shouldStart);
            else
                await StartLegacy(shouldStart);
        }

        // SecVideoCapture path: SSDP → TCP FlatBuffers → NV12 frames
        private async Task StartSecVideo(bool shouldStart = false)
        {
            while (App.Configuration.Enabled || shouldStart)
            {
                if (!Networking.IsConnected)
                {
                    try
                    {
                        (string ip, int port) = SsdpDiscovery.GetHyperIpAndPort();

                        // Fall back to cached address if SSDP finds nothing
                        if (ip == null && Preference.Contains("fbsServer"))
                        {
                            var parts = Preference.Get<string>("fbsServer").Split(':');
                            ip   = parts[0];
                            port = int.Parse(parts[1]);
                            Tizen.Log.Debug("HyperTizen", $"SSDP: using cached {ip}:{port}");
                        }

                        if (ip == null)
                        {
                            Tizen.Log.Debug("HyperTizen", "SSDP: no HyperHDR found, retrying in 10s");
                            await Task.Delay(10000);
                            continue;
                        }

                        Preference.Set("fbsServer", $"{ip}:{port}");
                        Networking.Connect(ip, port);
                    }
                    catch (Exception ex)
                    {
                        Tizen.Log.Debug("HyperTizen", "Connection error: " + ex.Message);
                        await Task.Delay(5000);
                        continue;
                    }
                }

                var frame = VideoCapture.CaptureFrame();
                if (frame.HasValue)
                    await Networking.SendFrameAsync(frame.Value.yData, frame.Value.uvData);

                if (App.Configuration.Enabled && shouldStart) shouldStart = false;
                else if (!App.Configuration.Enabled && shouldStart) App.Configuration.Enabled = true;
            }

            Networking.Disconnect();
        }

        // Legacy libvideoenhance.so path: existing 8-point pixel sampling → PNG → WebSocket JSON
        private async Task StartLegacy(bool shouldStart = false)
        {
            if (_wsClient == null || !Capturer.GetCondition()) return;

            var cond = Capturer.LastCondition;
            Tizen.Log.Debug("HyperTizen", $"Condition: SCP={cond.ScreenCapturePoints}, SleepMS={cond.SleepMS}, W={cond.Width}, H={cond.Height}");
            Preference.Set("cond_scp",   cond.ScreenCapturePoints.ToString());
            Preference.Set("cond_sleep", cond.SleepMS.ToString());
            Preference.Set("cond_w",     cond.Width.ToString());
            Preference.Set("cond_h",     cond.Height.ToString());

            while (App.Configuration.Enabled || shouldStart)
            {
                HyperTizen.Color[] colors = await Capturer.GetColors();
                string image = Capturer.ToImage(colors);

                if (_wsClient?.client?.State == WebSocketState.Open)
                {
                    ImageCommand imgCmd  = new ImageCommand(image);
                    string message       = JsonConvert.SerializeObject(imgCmd);
                    byte[] buffer        = Encoding.UTF8.GetBytes(message);
                    await _wsClient.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }

                if (App.Configuration.Enabled && shouldStart) shouldStart = false;
                else if (!App.Configuration.Enabled && shouldStart) App.Configuration.Enabled = true;
            }
        }

        public void UpdateURI(string uri)
        {
            if (_useSecVideoCapture) return; // not used in SecVideo path
            if (_wsClient?.client?.State == WebSocketState.Open)
                _wsClient.client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            _wsClient = new WebSocketClient(uri);
            Task.Run(() => _wsClient.ConnectAsync());
        }

        public async Task Stop()
        {
            App.Configuration.Enabled = false;

            if (_useSecVideoCapture)
            {
                Networking.Disconnect();
                return;
            }

            // Legacy: send one final frame before stopping
            if (_wsClient?.client?.State != WebSocketState.Open) return;
            HyperTizen.Color[] colors = await Capturer.GetColors();
            string image = Capturer.ToImage(colors);
            ImageCommand imgCmd = new ImageCommand(image);
            string msg = JsonConvert.SerializeObject(imgCmd);
            byte[] buf = Encoding.UTF8.GetBytes(msg);
            await _wsClient.client.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
