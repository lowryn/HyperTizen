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
        private bool _useSecVideoCapture;
        private bool _t7ProbePending;
        private WebSocketClient _wsClient;

        // Lifecycle: the capture loop runs inside a single Task guarded by a
        // CancellationTokenSource. Stop() cancels and awaits; Start() spawns a
        // fresh one. _lifecycleLock serialises Start/Stop so a rapid off→on
        // sequence can't interleave two concurrent loops.
        private readonly SemaphoreSlim _lifecycleLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _loopCts;
        private Task _loopTask;

        // Per-send timeout. Must be long enough for a normal send over a healthy
        // TCP connection, short enough that a dead TCP connection is noticed
        // before the loop wedges on a single send.
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

        public HyperionClient()
        {
            _useSecVideoCapture = TryInitSecVideoCapture();

            if (_useSecVideoCapture)
                VideoCapture.InitCapture();

            // WebSocket connection is established lazily inside the capture
            // loop. Do not block OnCreate with SSDP or a connect attempt.
            // Only auto-start if the user has previously enabled it — this is
            // the persisted state that survives reboots, so on-boot launches
            // resume capture without needing the UI.
            if (App.Configuration.Enabled)
                _ = Start();
        }

        private bool TryInitSecVideoCapture()
        {
            try
            {
                if (SDK.SystemInfo.TizenVersionMajor < 8)
                {
                    // Tizen 7 and below: flat C API in libsec-video-capture.so.0.
                    // libvideoenhance has no rgb_measure entry points on Tizen 6.0 firmware.
                    // The native probe runs later, from the capture loop, so a native crash
                    // cannot take the service down before its control server is reachable.
                    _t7ProbePending = true;
                    Diag.Log("cap_mode: T7 capture probe deferred until capture is enabled");
                    Preference.Set("cap_mode", "libve");
                    return false;
                }
                SDK.SecVideoCaptureT8.Init();
                Diag.Log("cap_mode: secvideo (NV12 FlatBuffers TCP)");
                Preference.Set("cap_mode", "secvideo");
                return true;
            }
            catch (Exception ex)
            {
                Diag.Log("cap_mode: libve (fallback) — " + ex.Message);
                Preference.Set("cap_mode", "libve");
                return false;
            }
        }

        public bool IsRunning
        {
            get { return _loopTask != null && !_loopTask.IsCompleted; }
        }

        private bool ProbeT7VideoCapture()
        {
            string state = Preference.Contains("t7_probe") ? Preference.Get<string>("t7_probe") : "";
            if (state == "running" || state == "crashed")
            {
                Preference.Set("t7_probe", "crashed");
                Diag.Log("T7 capture probe skipped: the previous probe killed the service. Retry with /set?key=t7_probe&value=retry");
                return false;
            }

            try
            {
                VideoCapture.InitCapture();
                Preference.Set("t7_probe", "running");
                Diag.Log("T7 capture probe: calling secvideo_api_capture_screen...");
                int result = VideoCapture.ProbeCapture();
                Preference.Set("t7_probe", "ok");
                Diag.Log("T7 capture probe: secvideo_api_capture_screen returned " + result);
                return true;
            }
            catch (Exception ex)
            {
                Preference.Set("t7_probe", "failed");
                Diag.Log("T7 capture probe failed: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        public async Task Start()
        {
            await _lifecycleLock.WaitAsync();
            try
            {
                if (_loopTask != null && !_loopTask.IsCompleted)
                {
                    Diag.Log("Start: capture loop already running");
                    return;
                }

                _loopCts = new CancellationTokenSource();
                CancellationToken ct = _loopCts.Token;
                _loopTask = Task.Run(async () =>
                {
                    try
                    {
                        if (_t7ProbePending)
                        {
                            _t7ProbePending = false;
                            _useSecVideoCapture = ProbeT7VideoCapture();
                            Diag.Log(_useSecVideoCapture
                                ? "cap_mode: secvideo-t7 (NV12 FlatBuffers TCP)"
                                : "cap_mode: libve (T7 probe unusable)");
                            Preference.Set("cap_mode", _useSecVideoCapture ? "secvideo" : "libve");
                        }

                        if (_useSecVideoCapture)
                            await StartSecVideo(ct);
                        else
                            await StartLegacy(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // normal on Stop
                    }
                    catch (Exception ex)
                    {
                        Diag.Log("Capture loop crashed: " + ex.Message);
                    }
                    finally
                    {
                        Diag.Log("Capture loop exited");
                    }
                });
                Diag.Log("Start: capture loop spawned");
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task Stop()
        {
            CancellationTokenSource cts;
            Task task;
            await _lifecycleLock.WaitAsync();
            try
            {
                cts = _loopCts;
                task = _loopTask;
                _loopCts = null;
                _loopTask = null;
            }
            finally
            {
                _lifecycleLock.Release();
            }

            try { cts?.Cancel(); } catch { }

            if (task != null)
            {
                // Bounded wait — if the loop doesn't exit in time, abandon it
                // and move on. The network resources will be reclaimed below.
                var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
                if (completed != task)
                    Diag.Log("Stop: capture loop did not exit in 3s, abandoning");
            }

            try { cts?.Dispose(); } catch { }

            // Tear down whichever transport was in use so a subsequent Start()
            // starts from a clean slate.
            if (_useSecVideoCapture)
            {
                Networking.Disconnect();
            }
            else
            {
                try { _wsClient?.client?.Abort(); } catch { }
                _wsClient = null;
            }
        }

        // SecVideoCapture path: SSDP → TCP FlatBuffers → NV12 frames
        private async Task StartSecVideo(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
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
                            Diag.Log($"SSDP: using cached {ip}:{port}");
                        }

                        if (ip == null)
                        {
                            Diag.Log("SSDP: no HyperHDR found, retrying in 10s");
                            await Task.Delay(10000, ct);
                            continue;
                        }

                        Preference.Set("fbsServer", $"{ip}:{port}");
                        Networking.Connect(ip, port);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Diag.Log("Connection error: " + ex.Message);
                        await Task.Delay(5000, ct);
                        continue;
                    }
                }

                var frame = VideoCapture.CaptureFrame();
                if (frame.HasValue)
                    await Networking.SendFrameAsync(frame.Value.yData, frame.Value.uvData);
                else
                    await Task.Delay(200, ct);
            }

            Networking.Disconnect();
        }

        // Legacy libvideoenhance.so path: 8-point pixel sampling → PNG → WS JSON
        private int _legacyFrames;

        private async Task StartLegacy(CancellationToken ct)
        {
            // Retry GetCondition — the hardware may not be ready immediately.
            bool condOk = false;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                condOk = Capturer.GetCondition();
                if (condOk) break;
                Diag.Log($"StartLegacy: GetCondition() failed (attempt {attempt}/5), retrying in 2s");
                await Task.Delay(2000, ct);
            }
            if (!condOk)
            {
                Diag.Log("StartLegacy: GetCondition() failed after all retries, aborting");
                return;
            }

            var cond = Capturer.LastCondition;
            Diag.Log($"Condition: SCP={cond.ScreenCapturePoints}, SleepMS={cond.SleepMS}, W={cond.Width}, H={cond.Height}");
            Preference.Set("cond_scp",   cond.ScreenCapturePoints.ToString());
            Preference.Set("cond_sleep", cond.SleepMS.ToString());
            Preference.Set("cond_w",     cond.Width.ToString());
            Preference.Set("cond_h",     cond.Height.ToString());

            while (!ct.IsCancellationRequested)
            {
                // Ensure we have a live WebSocket. If not, resolve/rebuild/reconnect.
                if (_wsClient == null || _wsClient.client == null || _wsClient.client.State != WebSocketState.Open)
                {
                    try { _wsClient?.client?.Abort(); } catch { }
                    _wsClient = null;

                    string rpcServer = ResolveRpcServer();
                    if (rpcServer == null)
                    {
                        Diag.Log("StartLegacy: rpcServer unresolved, retrying in 10s");
                        await Task.Delay(10000, ct);
                        continue;
                    }

                    try
                    {
                        _wsClient = new WebSocketClient(rpcServer);
                        await _wsClient.ConnectAsync(ct);
                        Diag.Log("StartLegacy: WebSocket connected to " + rpcServer);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Diag.Log("StartLegacy: connect failed: " + ex.Message);
                        _wsClient = null;
                        await Task.Delay(5000, ct);
                        continue;
                    }
                }

                HyperTizen.Color[] colors = await Capturer.GetColors();
                string image = Capturer.ToImage(colors);
                if (++_legacyFrames % 50 == 1)
                    Diag.Log($"StartLegacy: frame {_legacyFrames} captured");

                try
                {
                    ImageCommand imgCmd  = new ImageCommand(image);
                    string message       = MiniJson.SerializeImageCommand(imgCmd);
                    byte[] buffer        = Encoding.UTF8.GetBytes(message);

                    using (var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        sendCts.CancelAfter(SendTimeout);
                        await _wsClient.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, sendCts.Token);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Send failure (timeout, broken connection, etc.) — drop the
                    // client so the next iteration reconnects.
                    Diag.Log("StartLegacy: send failed, will reconnect: " + ex.Message);
                    try { _wsClient?.client?.Abort(); } catch { }
                    _wsClient = null;
                    await Task.Delay(1000, ct);
                }
            }
        }

        // Returns a ws:// URL for HyperHDR — from stored preference, or
        // auto-discovered via SSDP. Called from the capture loop, so a slow
        // SSDP scan does not block OnCreate.
        private string ResolveRpcServer()
        {
            if (Preference.Contains("rpcServer"))
                return Preference.Get<string>("rpcServer");

            Diag.Log("rpcServer not set, attempting SSDP auto-discovery...");
            (string ip, _) = SsdpDiscovery.GetHyperIpAndPort();
            if (ip == null)
            {
                Diag.Log("SSDP: no HyperHDR found — configure rpcServer via UI");
                return null;
            }

            // Use HyperHDR's standard JSON/WebSocket port (19400) as default
            string url = $"ws://{ip}:19400/";
            Preference.Set("rpcServer", url);
            App.Configuration.RPCServer = url;
            Diag.Log($"SSDP auto-discovered rpcServer: {url}");
            return url;
        }

        public void UpdateURI(string uri)
        {
            if (_useSecVideoCapture) return; // not used in SecVideo path
            // Drop the current connection — the loop's reconnect path will
            // pick up the new URI from Preference on its next iteration.
            try { _wsClient?.client?.Abort(); } catch { }
            _wsClient = null;
        }
    }
}
