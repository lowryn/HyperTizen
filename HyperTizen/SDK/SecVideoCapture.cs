using System;
using System.Runtime.InteropServices;

namespace HyperTizen.SDK
{
    // Tizen 7 and below: flat C API
    public static unsafe class SecVideoCaptureT7
    {
        [DllImport("/usr/lib/libsec-video-capture.so.0", CallingConvention = CallingConvention.Cdecl, EntryPoint = "secvideo_api_capture_screen")]
        public static extern int CaptureScreen(int w, int h, ref SecVideoCapture.Info_t pInfo);
    }

    // Tizen 8+: C++ object with vtable — Samsung changed the API
    public static unsafe class SecVideoCaptureT8
    {
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate int CaptureScreenDelegate(IntPtr @this, int w, int h, ref SecVideoCapture.Info_t pInfo);

        public struct IVideoCapture { public IntPtr* vtable; }

        private static bool _initialized = false;
        private static IVideoCapture* _instance;
        private static CaptureScreenDelegate _captureScreen;

        [DllImport("/usr/lib/libvideo-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl, EntryPoint = "getInstance")]
        private static extern IVideoCapture* GetInstance();

        // Call once at startup — NOT per frame
        public static void Init()
        {
            if (_initialized) return;
            _instance = GetInstance();
            if (_instance == null)
                throw new Exception("SecVideoCaptureT8: getInstance() returned null");
            const int CaptureScreenVTableIndex = 3;
            IntPtr fp = _instance->vtable[CaptureScreenVTableIndex];
            _captureScreen = (CaptureScreenDelegate)Marshal.GetDelegateForFunctionPointer(fp, typeof(CaptureScreenDelegate));
            _initialized = true;
        }

        public static int CaptureScreen(int w, int h, ref SecVideoCapture.Info_t pInfo)
        {
            if (!_initialized)
                throw new InvalidOperationException("SecVideoCaptureT8 not initialized — call Init() first");
            return _captureScreen((IntPtr)_instance, w, h, ref pInfo);
        }
    }

    public static class SecVideoCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Info_t
        {
            public Int32 iGivenBufferSize1;  // Y plane buffer size
            public Int32 iGivenBufferSize2;  // UV plane buffer size
            public Int32 iWidth;
            public Int32 iHeight;
            public IntPtr pImageY;           // Y plane buffer pointer
            public IntPtr pImageUV;          // UV plane buffer pointer
            public Int32 iRetColorFormat;    // 0=YUV420, 1=YUV422, 2=YUV444
            public Int32 unknown2;
            public Int32 capture3DMode;      // 0=2D, 1=FRAMEPACKING, etc.
        }

        public static int CaptureScreen(int w, int h, ref Info_t pInfo)
        {
            if (SystemInfo.TizenVersionMajor >= 8)
                return SecVideoCaptureT8.CaptureScreen(w, h, ref pInfo);
            else
                return SecVideoCaptureT7.CaptureScreen(w, h, ref pInfo);
        }
    }
}
