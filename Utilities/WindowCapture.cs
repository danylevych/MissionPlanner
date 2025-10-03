using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks;

namespace MissionPlanner.Utilities
{
    public class WindowCapture : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        private const uint PW_CLIENTONLY = 0x1;
        private const uint PW_RENDERFULLCONTENT = 0x2;
        private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int SW_RESTORE = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public enum CaptureMethod
        {
            BitBlt,           // Standard BitBlt (fastest)
            PrintWindow,      // PrintWindow API (works with some protected windows)
            ScreenCapture     // Capture by screen coordinates (fallback)
        }

        public struct WindowInfo
        {
            public IntPtr Handle;
            public string Title;
            public string ProcessName;
            public bool IsMinimized;
            public bool HasDirectX;

            public override string ToString()
            {
                var status = IsMinimized ? " [Minimized]" : "";
                var dx = HasDirectX ? " [DirectX]" : "";
                return $"{Title} ({ProcessName}){status}{dx}";
            }
        }

        private IntPtr _targetWindow = IntPtr.Zero;
        private Timer _captureTimer;
        private bool _isCapturing = false;
        private readonly object _captureLock = new object();
        private DateTime _lastCaptureTime = DateTime.MinValue;
        private volatile bool _isProcessingFrame = false;
        private int _frameSkipCount = 0;
        private const int MAX_FRAME_SKIP = 3; // Skip frames if processing is too slow
        private CaptureMethod _captureMethod = CaptureMethod.BitBlt;
        

        public event EventHandler<Bitmap> FrameCaptured;

        public static List<WindowInfo> GetVisibleWindows()
        {
            var windows = new List<WindowInfo>();
            
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                int length = GetWindowTextLength(hWnd);
                if (length == 0) return true;

                var builder = new StringBuilder(length + 1);
                GetWindowText(hWnd, builder, builder.Capacity);
                var title = builder.ToString();

                if (string.IsNullOrWhiteSpace(title)) return true;

                try
                {
                    uint processId;
                    GetWindowThreadProcessId(hWnd, out processId);
                    var process = Process.GetProcessById((int)processId);
                    
                    bool isMinimized = IsIconic(hWnd);
                    bool hasDirectX = HasDirectXContent(process.ProcessName);
                    
                    windows.Add(new WindowInfo
                    {
                        Handle = hWnd,
                        Title = title,
                        ProcessName = process.ProcessName,
                        IsMinimized = isMinimized,
                        HasDirectX = hasDirectX
                    });
                }
                catch
                {
                    // Ignore processes we can't access
                }

                return true;
            }, IntPtr.Zero);

            return windows;
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr hdc, int iStretchMode);

        [DllImport("gdi32.dll")]
        private static extern bool SetBrushOrgEx(IntPtr hdc, int nXOrg, int nYOrg, IntPtr lppt);

        private const int STRETCH_HALFTONE = 4;

        public void StartCapture(IntPtr windowHandle, int intervalMs = 33, CaptureMethod? method = null) // ~30 FPS
        {
            lock (_captureLock)
            {
                StopCapture();
                
                _targetWindow = windowHandle;
                _captureMethod = method ?? DetectBestCaptureMethod(windowHandle);
                _isCapturing = true;
                
                System.Diagnostics.Debug.WriteLine($"Starting capture with method: {_captureMethod}");
                _captureTimer = new Timer(CaptureFrame, null, 0, intervalMs);
            }
        }

        public void StopCapture()
        {
            lock (_captureLock)
            {
                _isCapturing = false;
                _captureTimer?.Dispose();
                _captureTimer = null;
                _targetWindow = IntPtr.Zero;
            }
        }

        private void CaptureFrame(object state)
        {
            if (!_isCapturing || _targetWindow == IntPtr.Zero) return;
            
            // Skip frame if still processing previous one
            if (_isProcessingFrame)
            {
                _frameSkipCount++;
                if (_frameSkipCount < MAX_FRAME_SKIP)
                    return;
                // Force processing if we've skipped too many frames
            }
            
            // Throttle to prevent excessive CPU usage
            var now = DateTime.UtcNow;
            if ((now - _lastCaptureTime).TotalMilliseconds < 25) // Max 40 FPS
                return;
                
            _lastCaptureTime = now;
            _frameSkipCount = 0;
            _isProcessingFrame = true;

            try
            {
                // Run capture on background thread to avoid UI blocking
                Task.Run(() =>
                {
                    try
                    {
                        var bitmap = CaptureWindow(_targetWindow);
                        if (bitmap != null && _isCapturing)
                        {
                            FrameCaptured?.Invoke(this, bitmap);
                        }
                        else
                        {
                            bitmap?.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Window capture error: {ex.Message}");
                    }
                    finally
                    {
                        _isProcessingFrame = false;
                    }
                });
            }
            catch (Exception ex)
            {
                _isProcessingFrame = false;
                System.Diagnostics.Debug.WriteLine($"Window capture error: {ex.Message}");
            }
        }

        private Bitmap CaptureWindow(IntPtr windowHandle)
        {
            try
            {
                // Try different capture methods
                switch (_captureMethod)
                {
                    case CaptureMethod.PrintWindow:
                        return CaptureWindowPrintWindow(windowHandle);
                    case CaptureMethod.ScreenCapture:
                        return CaptureWindowByScreen(windowHandle);
                    case CaptureMethod.BitBlt:
                    default:
                        return CaptureWindowBitBlt(windowHandle);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Capture failed with {_captureMethod}, trying fallback: {ex.Message}");
                
                // Try fallback methods
                if (_captureMethod != CaptureMethod.PrintWindow)
                {
                    try { return CaptureWindowPrintWindow(windowHandle); } catch { }
                }
                
                if (_captureMethod != CaptureMethod.ScreenCapture)
                {
                    try { return CaptureWindowByScreen(windowHandle); } catch { }
                }
                
                return null;
            }
        }

        private Bitmap CaptureWindowBitBlt(IntPtr windowHandle)
        {
            if (!GetWindowRect(windowHandle, out RECT rect))
                return null;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
                return null;

            IntPtr hdcSrc = GetWindowDC(windowHandle);
            if (hdcSrc == IntPtr.Zero)
                return null;

            IntPtr hdcDest = CreateCompatibleDC(hdcSrc);
            IntPtr hBitmap = CreateCompatibleBitmap(hdcSrc, width, height);
            IntPtr hOld = SelectObject(hdcDest, hBitmap);

            SetStretchBltMode(hdcDest, STRETCH_HALFTONE);
            SetBrushOrgEx(hdcDest, 0, 0, IntPtr.Zero);

            const uint SRCCOPY = 0x00CC0020;
            BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, SRCCOPY);

            SelectObject(hdcDest, hOld);
            DeleteDC(hdcDest);
            ReleaseDC(windowHandle, hdcSrc);

            Bitmap bitmap = Image.FromHbitmap(hBitmap);
            DeleteObject(hBitmap);

            return bitmap;
        }

        private Bitmap CaptureWindowPrintWindow(IntPtr windowHandle)
        {
            if (!GetWindowRect(windowHandle, out RECT rect))
                return null;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
                return null;

            // Restore window if minimized
            if (IsIconic(windowHandle))
            {
                ShowWindow(windowHandle, SW_RESTORE);
                Thread.Sleep(100); // Give time for window to restore
            }

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr hdc = graphics.GetHdc();
                
                // Try PrintWindow with different flags
                bool success = PrintWindow(windowHandle, hdc, PW_RENDERFULLCONTENT);
                if (!success)
                {
                    success = PrintWindow(windowHandle, hdc, PW_CLIENTONLY);
                }
                
                graphics.ReleaseHdc(hdc);
                
                if (!success)
                {
                    bitmap.Dispose();
                    return null;
                }
            }

            return bitmap;
        }

        private Bitmap CaptureWindowByScreen(IntPtr windowHandle)
        {
            if (!GetWindowRect(windowHandle, out RECT rect))
                return null;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
                return null;

            // Capture by screen coordinates
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
            }

            return bitmap;
        }

        public bool IsCapturing => _isCapturing;
        public IntPtr TargetWindow => _targetWindow;
        public CaptureMethod CurrentCaptureMethod => _captureMethod;

        private CaptureMethod DetectBestCaptureMethod(IntPtr windowHandle)
        {
            try
            {
                // Get process info
                uint processId;
                GetWindowThreadProcessId(windowHandle, out processId);
                var process = Process.GetProcessById((int)processId);
                
                // Check if it's a DirectX/OpenGL application
                string processName = process.ProcessName.ToLower();
                if (HasDirectXContent(processName))
                {
                    return CaptureMethod.ScreenCapture; // DirectX apps often need screen capture
                }
                
                // Check if window is minimized
                if (IsIconic(windowHandle))
                {
                    return CaptureMethod.PrintWindow; // PrintWindow can capture minimized windows
                }
                
                // Test BitBlt first (fastest)
                var testBitmap = CaptureWindowBitBlt(windowHandle);
                if (testBitmap != null && !IsBitmapBlank(testBitmap))
                {
                    testBitmap.Dispose();
                    return CaptureMethod.BitBlt;
                }
                testBitmap?.Dispose();
                
                // Try PrintWindow
                testBitmap = CaptureWindowPrintWindow(windowHandle);
                if (testBitmap != null && !IsBitmapBlank(testBitmap))
                {
                    testBitmap.Dispose();
                    return CaptureMethod.PrintWindow;
                }
                testBitmap?.Dispose();
                
                // Fallback to screen capture
                return CaptureMethod.ScreenCapture;
            }
            catch
            {
                return CaptureMethod.BitBlt; // Default fallback
            }
        }

        private static bool HasDirectXContent(string processName)
        {
            string[] directxApps = {
                "chrome", "firefox", "edge", "opera", // Browsers with hardware acceleration
                "vlc", "mpc-hc", "potplayer", // Video players
                "obs", "obs64", // Screen recording
                "game", "unity", "unreal", // Game engines
                "steam", "origin", "epic", // Game launchers
                "discord", // Discord with hardware acceleration
                "blender", "maya", "3dsmax" // 3D software
            };
            
            return directxApps.Any(app => processName.ToLower().Contains(app));
        }

        private bool IsBitmapBlank(Bitmap bitmap)
        {
            if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0)
                return true;
                
            // Sample a few pixels to check if image is blank/black
            int sampleCount = Math.Min(100, bitmap.Width * bitmap.Height);
            int nonBlackPixels = 0;
            
            for (int i = 0; i < sampleCount; i++)
            {
                int x = (i % 10) * (bitmap.Width / 10);
                int y = (i / 10) * (bitmap.Height / 10);
                
                if (x < bitmap.Width && y < bitmap.Height)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.R > 10 || pixel.G > 10 || pixel.B > 10) // Not completely black
                    {
                        nonBlackPixels++;
                    }
                }
            }
            
            return nonBlackPixels < (sampleCount * 0.1); // Less than 10% non-black pixels = likely blank
        }

        public void Dispose()
        {
            StopCapture();
        }
    }
}