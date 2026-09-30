using System;
using System.Runtime.InteropServices;
using Tizen.Applications;

namespace HyperTizen
{
    public static class VideoCapture
    {
        public const int Width  = 480;  // 3840 / 8
        public const int Height = 270;  // 2160 / 8

        private static readonly int YSize  = Width * Height;
        private static readonly int UVSize = (Width * Height) / 2;

        private const int InfoBytes = 256;
        private static IntPtr _pInfo;
        private static IntPtr _pImageY;
        private static IntPtr _pImageUV;
        private static byte[] _yData;
        private static byte[] _uvData;

        public static void InitCapture()
        {
            if (_pImageY != IntPtr.Zero) return;
            _pImageY  = Marshal.AllocHGlobal(YSize);
            _pImageUV = Marshal.AllocHGlobal(UVSize);
            _pInfo    = Marshal.AllocHGlobal(InfoBytes);
            _yData    = new byte[YSize];
            _uvData   = new byte[UVSize];
            Diag.Log($"VideoCapture: buffers allocated ({Width}x{Height} NV12)");
        }

        // Tizen 7 and below: native struct in an oversized zeroed buffer (Info_t layout at offsets 0..35).
        private static int CaptureRaw()
        {
            for (int i = 0; i < InfoBytes; i += 4) Marshal.WriteInt32(_pInfo, i, 0);
            Marshal.WriteInt32(_pInfo, 0, YSize);
            Marshal.WriteInt32(_pInfo, 4, UVSize);
            Marshal.WriteIntPtr(_pInfo, 16, _pImageY);
            Marshal.WriteIntPtr(_pInfo, 20, _pImageUV);
            return SDK.SecVideoCaptureT7.CaptureScreenRaw(Width, Height, _pInfo);
        }

        // Loads the native library step by step, logging each step to the persisted log,
        // then makes one capture call. Throws if the library or entry point is missing.
        public static int ProbeCapture()
        {
            const string lib = "/usr/lib/libsec-video-capture.so.0";
            Diag.Log("T7 probe: loading " + lib);
            IntPtr handle = NativeLibrary.Load(lib);
            Diag.Log("T7 probe: library loaded, resolving secvideo_api_capture_screen");
            IntPtr fn = NativeLibrary.GetExport(handle, "secvideo_api_capture_screen");
            Diag.Log("T7 probe: entry point resolved (" + fn + "), calling with a " + InfoBytes + "-byte zeroed struct");
            return CaptureRaw();
        }

        private static readonly object _captureLock = new object();

        // Returns captured frame data, or null if capture failed (DRM, scaler error, etc.)
        public static (byte[] yData, byte[] uvData)? CaptureFrame()
        {
            lock (_captureLock)
                return CaptureFrameCore();
        }

        // One fresh capture converted to a top-down 24-bit BMP, for checking the picture in a browser.
        public static byte[] SnapshotBmp()
        {
            byte[] y, uv;
            lock (_captureLock)
            {
                InitCapture();
                var frame = CaptureFrameCore();
                if (!frame.HasValue) return null;
                y = (byte[])frame.Value.yData.Clone();
                uv = (byte[])frame.Value.uvData.Clone();
            }

            int rowBytes = Width * 3;
            var bmp = new byte[54 + rowBytes * Height];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            PutInt(bmp, 2, bmp.Length);
            PutInt(bmp, 10, 54);
            PutInt(bmp, 14, 40);
            PutInt(bmp, 18, Width);
            PutInt(bmp, 22, -Height);
            bmp[26] = 1;
            bmp[28] = 24;
            PutInt(bmp, 34, rowBytes * Height);

            for (int row = 0; row < Height; row++)
            {
                for (int col = 0; col < Width; col++)
                {
                    int yy = y[row * Width + col] - 16;
                    int uvIndex = (row / 2) * Width + (col / 2) * 2;
                    int u = uv[uvIndex] - 128;
                    int v = uv[uvIndex + 1] - 128;
                    int c = 298 * yy;
                    int o = 54 + row * rowBytes + col * 3;
                    bmp[o]     = Clamp((c + 516 * u + 128) >> 8);
                    bmp[o + 1] = Clamp((c - 100 * u - 208 * v + 128) >> 8);
                    bmp[o + 2] = Clamp((c + 409 * v + 128) >> 8);
                }
            }
            return bmp;
        }

        private static void PutInt(byte[] b, int offset, int value)
        {
            b[offset] = (byte)value;
            b[offset + 1] = (byte)(value >> 8);
            b[offset + 2] = (byte)(value >> 16);
            b[offset + 3] = (byte)(value >> 24);
        }

        private static byte Clamp(int v)
        {
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        private static (byte[] yData, byte[] uvData)? CaptureFrameCore()
        {
            bool legacyApi = SDK.SystemInfo.TizenVersionMajor < 8;
            var info = new SDK.SecVideoCapture.Info_t
            {
                iGivenBufferSize1 = YSize,
                iGivenBufferSize2 = UVSize,
                pImageY           = _pImageY,
                pImageUV          = _pImageUV
            };

            int result = legacyApi
                ? CaptureRaw()
                : SDK.SecVideoCapture.CaptureScreen(Width, Height, ref info);

            if (result < 0)
            {
                switch (result)
                {
                    case -4:
                        Diag.Log("VideoCapture: DRM content (-4), skipping frame");
                        break;
                    case -2:
                        Diag.Log("VideoCapture: scaler failure (-2), try cold reboot if persistent");
                        break;
                    default:
                        Diag.Log($"VideoCapture: capture error {result}");
                        break;
                }
                return null;
            }

            IntPtr pY  = legacyApi ? Marshal.ReadIntPtr(_pInfo, 16) : info.pImageY;
            IntPtr pUV = legacyApi ? Marshal.ReadIntPtr(_pInfo, 20) : info.pImageUV;
            Marshal.Copy(pY,  _yData,  0, YSize);
            Marshal.Copy(pUV, _uvData, 0, UVSize);

            return (_yData, _uvData);
        }
    }
}
