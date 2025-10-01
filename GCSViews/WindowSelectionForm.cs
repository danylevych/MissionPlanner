using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.Utilities;

namespace MissionPlanner.GCSViews
{
    public partial class WindowSelectionForm : Form
    {
        public WindowCapture.WindowInfo SelectedWindow { get; private set; }
        private Timer _previewTimer;
        private WindowCapture _previewCapture;
        private DateTime _lastPreviewUpdate = DateTime.MinValue;
        private volatile bool _isUpdatingPreview = false;
        
        public WindowSelectionForm()
        {
            InitializeComponent();
            
            // Setup preview timer
            _previewTimer = new Timer();
            _previewTimer.Interval = 100; // 10 FPS for preview
            _previewTimer.Tick += PreviewTimer_Tick;
        }

        private void WindowSelectionForm_Load(object sender, EventArgs e)
        {
            RefreshWindowList();
        }

        private void RefreshWindowList()
        {
            listBoxWindows.Items.Clear();
            
            try
            {
                var windows = WindowCapture.GetVisibleWindows()
                    .Where(w => !string.IsNullOrEmpty(w.Title) && 
                               !w.Title.Equals("Program Manager") &&
                               !w.Title.Contains("Microsoft Text Input Application") &&
                               !w.Title.Contains("Window Selection") &&
                               !w.Title.Contains("MissionPlanner"))
                    .OrderBy(w => w.Title)
                    .ToList();
                
                foreach (var window in windows)
                {
                    listBoxWindows.Items.Add(window);
                }
                
                if (listBoxWindows.Items.Count > 0)
                {
                    listBoxWindows.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error getting window list: {ex.Message}", "Error", 
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnRefresh_Click(object sender, EventArgs e)
        {
            StopPreview();
            RefreshWindowList();
        }

        private void listBoxWindows_DoubleClick(object sender, EventArgs e)
        {
            if (listBoxWindows.SelectedItem != null)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        private void listBoxWindows_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listBoxWindows.SelectedItem is WindowCapture.WindowInfo selectedWindow)
            {
                StartPreview(selectedWindow);
            }
            else
            {
                StopPreview();
            }
        }

        private void StartPreview(WindowCapture.WindowInfo window)
        {
            try
            {
                StopPreview();
                
                _previewCapture = new WindowCapture();
                _previewCapture.FrameCaptured += OnPreviewFrameCaptured;
                _previewCapture.StartCapture(window.Handle, 200); // 5 FPS for preview
            }
            catch (Exception ex)
            {
                // Preview failed, but don't show error - just disable preview
                System.Diagnostics.Debug.WriteLine($"Preview failed: {ex.Message}");
            }
        }

        private void OnPreviewFrameCaptured(object sender, Bitmap frame)
        {
            try
            {
                // Skip update if still processing previous frame
                if (_isUpdatingPreview)
                {
                    frame?.Dispose();
                    return;
                }
                
                // Throttle updates for preview (max 5 FPS)
                var now = DateTime.UtcNow;
                if ((now - _lastPreviewUpdate).TotalMilliseconds < 200)
                {
                    frame?.Dispose();
                    return;
                }
                
                _lastPreviewUpdate = now;
                _isUpdatingPreview = true;
                
                if (pictureBoxPreview.InvokeRequired)
                {
                    pictureBoxPreview.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            UpdatePreviewImage(frame);
                        }
                        finally
                        {
                            _isUpdatingPreview = false;
                            frame?.Dispose();
                        }
                    }));
                }
                else
                {
                    try
                    {
                        UpdatePreviewImage(frame);
                    }
                    finally
                    {
                        _isUpdatingPreview = false;
                        frame?.Dispose();
                    }
                }
            }
            catch
            {
                _isUpdatingPreview = false;
                frame?.Dispose();
            }
        }

        private void UpdatePreviewImage(Bitmap frame)
        {
            try
            {
                var oldImage = pictureBoxPreview.Image;
                
                // Calculate scaling to maintain aspect ratio
                float scaleX = (float)pictureBoxPreview.Width / frame.Width;
                float scaleY = (float)pictureBoxPreview.Height / frame.Height;
                float scale = Math.Min(scaleX, scaleY);
                
                int newWidth = (int)(frame.Width * scale);
                int newHeight = (int)(frame.Height * scale);
                
                var scaledImage = new Bitmap(pictureBoxPreview.Width, pictureBoxPreview.Height);
                using (var g = Graphics.FromImage(scaledImage))
                {
                    // Set high quality rendering
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    
                    // Fill background
                    g.Clear(Color.Black);
                    
                    // Center the image
                    int x = (pictureBoxPreview.Width - newWidth) / 2;
                    int y = (pictureBoxPreview.Height - newHeight) / 2;
                    
                    g.DrawImage(frame, x, y, newWidth, newHeight);
                }
                
                pictureBoxPreview.Image = scaledImage;
                oldImage?.Dispose();
            }
            catch
            {
                // Ignore image update errors
            }
        }

        private void PreviewTimer_Tick(object sender, EventArgs e)
        {
            // This method is kept for compatibility but we now use direct frame capture events
        }

        private void StopPreview()
        {
            try
            {
                _previewTimer?.Stop();
                
                if (_previewCapture != null)
                {
                    _previewCapture.FrameCaptured -= OnPreviewFrameCaptured;
                    _previewCapture.StopCapture();
                    _previewCapture.Dispose();
                    _previewCapture = null;
                }
                
                var oldImage = pictureBoxPreview.Image;
                pictureBoxPreview.Image = null;
                oldImage?.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopPreview();
            
            if (DialogResult == DialogResult.OK && listBoxWindows.SelectedItem != null)
            {
                SelectedWindow = (WindowCapture.WindowInfo)listBoxWindows.SelectedItem;
            }
            
            base.OnFormClosing(e);
        }
    }
}