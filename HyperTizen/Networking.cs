using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Google.FlatBuffers;
using hyperhdrnet;

namespace HyperTizen
{
    public static class Networking
    {
        private static TcpClient _client;
        private static NetworkStream _stream;

        public static bool IsConnected => _client != null && _client.Connected && _stream != null;

        public static void Connect(string ip, int port)
        {
            Disconnect();
            _client = new TcpClient(ip, port);
            _stream = _client.GetStream();
            SendRegister();
            // Background task to drain HyperHDR's reply messages
            Task.Run(() => DrainRepliesAsync());
            Tizen.Log.Debug("HyperTizen", $"Networking: connected to {ip}:{port} (FlatBuffers TCP)");
        }

        public static void Disconnect()
        {
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        public static async Task SendFrameAsync(byte[] yData, byte[] uvData)
        {
            if (!IsConnected) return;
            try
            {
                byte[] msg = BuildImageMessage(yData, uvData);
                await WriteMessageAsync(msg);
            }
            catch (Exception ex)
            {
                Tizen.Log.Debug("HyperTizen", "Networking.SendFrameAsync error: " + ex.Message);
                Disconnect();
            }
        }

        private static void SendRegister()
        {
            byte[] msg = BuildRegisterMessage();
            WriteMessage(msg);
            Tizen.Log.Debug("HyperTizen", "Networking: register sent");
        }

        private static async Task DrainRepliesAsync()
        {
            var buffer = new byte[1024];
            while (IsConnected)
            {
                try
                {
                    int read = await _stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read == 0) { Disconnect(); break; }
                    // Optionally parse: Reply.GetRootAsReply(new ByteBuffer(buffer, 4))
                }
                catch { Disconnect(); break; }
            }
        }

        // Write a length-prefixed message synchronously (for register at connect time)
        private static void WriteMessage(byte[] msg)
        {
            byte[] header = LengthHeader(msg.Length);
            _stream.Write(header, 0, 4);
            _stream.Write(msg, 0, msg.Length);
            _stream.Flush();
        }

        // Write a length-prefixed message asynchronously (for each frame)
        private static async Task WriteMessageAsync(byte[] msg)
        {
            byte[] header = LengthHeader(msg.Length);
            await _stream.WriteAsync(header, 0, 4);
            await _stream.WriteAsync(msg, 0, msg.Length);
            await _stream.FlushAsync();
        }

        private static byte[] LengthHeader(int length)
        {
            return new byte[]
            {
                (byte)(length >> 24),
                (byte)(length >> 16),
                (byte)(length >> 8),
                (byte)(length)
            };
        }

        private static byte[] BuildRegisterMessage()
        {
            var builder = new FlatBufferBuilder(256);
            var origin  = builder.CreateString("HyperTizen");
            Register.StartRegister(builder);
            Register.AddOrigin(builder, origin);
            Register.AddPriority(builder, 123);
            var reg = Register.EndRegister(builder);
            Request.StartRequest(builder);
            Request.AddCommandType(builder, Command.Register);
            Request.AddCommand(builder, reg.Value);
            var req = Request.EndRequest(builder);
            Request.FinishRequestBuffer(builder, req);
            return builder.SizedByteArray();
        }

        private static byte[] BuildImageMessage(byte[] yData, byte[] uvData)
        {
            var builder = new FlatBufferBuilder(yData.Length + uvData.Length + 256);

            // Use VectorBlock for efficiency (adds the whole array at once)
            var yVec  = NV12Image.CreateDataYVectorBlock(builder, yData);
            var uvVec = NV12Image.CreateDataUvVectorBlock(builder, uvData);

            NV12Image.StartNV12Image(builder);
            NV12Image.AddDataY(builder, yVec);
            NV12Image.AddDataUv(builder, uvVec);
            NV12Image.AddWidth(builder, VideoCapture.Width);
            NV12Image.AddHeight(builder, VideoCapture.Height);
            NV12Image.AddStrideY(builder, VideoCapture.Width);
            NV12Image.AddStrideUv(builder, VideoCapture.Width);
            var nv12 = NV12Image.EndNV12Image(builder);

            Image.StartImage(builder);
            Image.AddDataType(builder, ImageType.NV12Image);
            Image.AddData(builder, nv12.Value);
            Image.AddDuration(builder, -1);
            var img = Image.EndImage(builder);

            Request.StartRequest(builder);
            Request.AddCommandType(builder, Command.Image);
            Request.AddCommand(builder, img.Value);
            var req = Request.EndRequest(builder);
            Request.FinishRequestBuffer(builder, req);

            return builder.SizedByteArray();
        }
    }
}
