using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Tizen.Applications.Notifications;
using Tizen.System;

namespace HyperTizen
{
    public static class Capturer
    {
        private static Condition _condition;

        private static bool IsTizen7OrHigher
        {
            get
            {
                string version;
                Information.TryGetValue("http://tizen.org/feature/platform.version", out version);
                if (int.Parse(version.Split('.')[0]) >= 7)
                {
                    return true;
                } else
                {
                    return false;
                }
            }
        }

        // 8 static points — all captured every frame.
        // 4 batches × 20ms = ~10fps, all 8 zones update together.
        private static readonly CapturePoint[][] _pointSets = {
            new CapturePoint[] {
                new CapturePoint(0.21,  0.05),   // [0] top-left
                new CapturePoint(0.7,   0.05),   // [1] top-right
                new CapturePoint(0.95,  0.275),  // [2] right-top
                new CapturePoint(0.95,  0.8),    // [3] right-bottom
                new CapturePoint(0.65,  0.95),   // [4] bottom-right
                new CapturePoint(0.35,  0.95),   // [5] bottom-left
                new CapturePoint(0.05,  0.2),    // [6] left-top
                new CapturePoint(0.05,  0.725),  // [7] left-bottom
            }
        };
        private static int _setIndex = 0;
        private static readonly Color[] _blended = new Color[8];
        
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cs_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition(out Condition unknown);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cs_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition(int i, int x, int y);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cs_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel(int i, out Color color);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition7(out Condition unknown);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ve_set_rgb_measure_position")]
        private static extern int MeasurePosition7(int i, int x, int y);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel7(int i, out Color color);
        
        public static Condition LastCondition => _condition;

        public static bool GetCondition()
        {
            int res = -1;
            try
            {
                if (!IsTizen7OrHigher)
                {
                    res = MeasureCondition(out _condition);
                } else
                {
                    res = MeasureCondition7(out _condition);
                }
            } catch
            {
                Notification notification = new Notification
                {
                    Title = "HyperTizen",
                    Content = "Your TV does not support the required functions for HyperTizen.",
                    Count = 1
                };

                NotificationManager.Post(notification);
            }
            if (res < 0)
            {
                return false;
            } else
            {
                return true;
            }
        }

        public static async Task<Color[]> GetColors()
        {
            CapturePoint[] pts = _pointSets[_setIndex];
            int offset = _setIndex * 4;

            int i = 0;
            while (i < pts.Length)
            {
                if (_condition.ScreenCapturePoints == 0) break;

                int batchSize = Math.Min(_condition.ScreenCapturePoints, pts.Length - i);
                int batchStart = i;

                for (int j = 0; j < batchSize; j++)
                {
                    int x = (int)(pts[i].X * (double)_condition.Width) - _condition.PixelDensityX / 2;
                    int y = (int)(pts[i].Y * (double)_condition.Height) - _condition.PixelDensityY / 2;
                    x = (x >= _condition.Width - _condition.PixelDensityX) ? _condition.Width - (_condition.PixelDensityX + 1) : x;
                    y = (y >= _condition.Height - _condition.PixelDensityY) ? (_condition.Height - _condition.PixelDensityY + 1) : y;

                    int _ = IsTizen7OrHigher ? MeasurePosition7(j, x, y) : MeasurePosition(j, x, y);
                    i++;
                }

                if (_condition.SleepMS > 0) await Task.Delay(_condition.SleepMS);

                int k = 0, retries = 0;
                while (k < batchSize)
                {
                    Color color;
                    int res = IsTizen7OrHigher ? MeasurePixel7(k, out color) : MeasurePixel(k, out color);

                    if (res >= 0 && color.R <= 1023 && color.G <= 1023 && color.B <= 1023)
                    {
                        _blended[offset + batchStart + k] = color;
                        k++;
                        retries = 0;
                    }
                    else if (++retries >= 20) { k++; retries = 0; }
                }
            }

            _setIndex = 0; // single set, no rotation
            return (Color[])_blended.Clone();
        }

        public static string ToImage(Color[] colors)
        {
            // [0]=top-left [1]=top-right [2]=right-top [3]=right-bottom [4]=bottom-right [5]=bottom-left [6]=left-top [7]=left-bottom
            using (var image = new SKBitmap(64, 48))
            using (var canvas = new SKCanvas(image))
            {
                canvas.Clear(SKColors.Black);
                // Top strip (rows 0-3): 2 zones
                canvas.DrawRect(SKRect.Create(0,  0, 32, 4), new SKPaint { Color = ClampColor(colors[0]) });
                canvas.DrawRect(SKRect.Create(32, 0, 32, 4), new SKPaint { Color = ClampColor(colors[1]) });
                // Right strip (cols 61-63): 2 zones
                canvas.DrawRect(SKRect.Create(61,  0, 3, 24), new SKPaint { Color = ClampColor(colors[2]) });
                canvas.DrawRect(SKRect.Create(61, 24, 3, 24), new SKPaint { Color = ClampColor(colors[3]) });
                // Bottom strip (rows 44-47): 2 zones
                canvas.DrawRect(SKRect.Create(32, 44, 32, 4), new SKPaint { Color = ClampColor(colors[4]) });
                canvas.DrawRect(SKRect.Create(0,  44, 32, 4), new SKPaint { Color = ClampColor(colors[5]) });
                // Left strip (cols 0-2): 2 zones
                canvas.DrawRect(SKRect.Create(0,  0, 3, 24), new SKPaint { Color = ClampColor(colors[6]) });
                canvas.DrawRect(SKRect.Create(0, 24, 3, 24), new SKPaint { Color = ClampColor(colors[7]) });

                using (var memoryStream = new MemoryStream())
                {
                    using (var data = SKImage.FromBitmap(image).Encode(SKEncodedImageFormat.Png, 100))
                        data.SaveTo(memoryStream);
                    return Convert.ToBase64String(memoryStream.ToArray());
                }
            }
        }

        static SKColor ClampColor(Color color)
        {
            byte r = (byte)(Math.Clamp(color.R, 0, 1023) * 255 / 1023);
            byte g = (byte)(Math.Clamp(color.G, 0, 1023) * 255 / 1023);
            byte b = (byte)(Math.Clamp(color.B, 0, 1023) * 255 / 1023);
            return new SKColor(r, g, b);
        }
    }

    public struct Color
    {
        public int R;
        public int G;
        public int B;
    }

    public struct Condition
    {
        public int ScreenCapturePoints;

        public int PixelDensityX;

        public int PixelDensityY;

        public int SleepMS;

        public int Width;

        public int Height;
    }

    public struct CapturePoint
    {
        public CapturePoint(double x, double y) {
            this.X = x;
            this.Y = y;
        }

        public double X;
        public double Y;
    }
}
