using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
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

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public struct WindowInfo
        {
            public IntPtr Handle;
            public string Title;
            public string ProcessName;

            public override string ToString()
            {
                return $"{Title} ({ProcessName})";
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
                    
                    windows.Add(new WindowInfo
                    {
                        Handle = hWnd,
                        Title = title,
                        ProcessName = process.ProcessName
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

        public void StartCapture(IntPtr windowHandle, int intervalMs = 33) // ~30 FPS
        {
            lock (_captureLock)
            {
                StopCapture();
                
                _targetWindow = windowHandle;
                _isCapturing = true;
                
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
            if (!GetWindowRect(windowHandle, out RECT rect))
                return null;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
                return null;

            // Get DPI scaling factor
            float dpiX, dpiY;
            using (var g = Graphics.FromHwnd(windowHandle))
            {
                dpiX = g.DpiX / 96.0f;
                dpiY = g.DpiY / 96.0f;
            }

            IntPtr hdcSrc = GetWindowDC(windowHandle);
            if (hdcSrc == IntPtr.Zero)
                return null;

            IntPtr hdcDest = CreateCompatibleDC(hdcSrc);
            IntPtr hBitmap = CreateCompatibleBitmap(hdcSrc, width, height);
            IntPtr hOld = SelectObject(hdcDest, hBitmap);

            // Set stretch mode for better quality
            SetStretchBltMode(hdcDest, STRETCH_HALFTONE);
            SetBrushOrgEx(hdcDest, 0, 0, IntPtr.Zero);

            const uint SRCCOPY = 0x00CC0020;
            BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, SRCCOPY);

            SelectObject(hdcDest, hOld);
            DeleteDC(hdcDest);
            ReleaseDC(windowHandle, hdcSrc);

            Bitmap bitmap = Image.FromHbitmap(hBitmap);
            DeleteObject(hBitmap);

            // Set resolution for proper scaling
            bitmap.SetResolution(96 * dpiX, 96 * dpiY);

            return bitmap;
        }

        public bool IsCapturing => _isCapturing;
        public IntPtr TargetWindow => _targetWindow;

        public void Dispose()
        {
            StopCapture();
        }
    }
}