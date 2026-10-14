using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using ButterFlow.Core;

namespace ButterFlow.UI
{
    public class DragGhostOverlay : Form
    {
        private string _fileName = string.Empty;
        private string _fileSizeText = string.Empty;
        private Bitmap _fileBitmap = null;
        private bool _isDropped = false;
        private string _targetLocationText = string.Empty;
        private System.Windows.Forms.Timer _fadeTimer;
        private double _opacityTarget = 0.94;

        public DragGhostOverlay()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.Size = new Size(360, 58);
            this.DoubleBuffered = true;
            this.BackColor = Color.FromArgb(15, 23, 42); // Sleek Slate 900
            this.Opacity = _opacityTarget;

            _fadeTimer = new System.Windows.Forms.Timer();
            _fadeTimer.Interval = 25;
            _fadeTimer.Tick += (s, e) =>
            {
                if (this.Opacity > 0.05)
                {
                    this.Opacity -= 0.08;
                }
                else
                {
                    _fadeTimer.Stop();
                    this.Hide();
                }
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT (clicks pass straight through!)
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }

        public void ShowDrag(string fileName, long fileSize, int curX, int curY)
        {
            _fadeTimer.Stop();
            _fileName = fileName;
            _isDropped = false;
            _targetLocationText = string.Empty;

            if (_fileBitmap != null)
            {
                try { _fileBitmap.Dispose(); } catch { }
                _fileBitmap = null;
            }

            try
            {
                _fileBitmap = ShellHelper.GetFileIconBitmap(fileName);
            }
            catch
            {
                _fileBitmap = null;
            }

            if (fileSize > 1024 * 1024)
            {
                _fileSizeText = string.Format("{0:F1} MB", (double)fileSize / (1024.0 * 1024.0));
            }
            else if (fileSize > 1024)
            {
                _fileSizeText = string.Format("{0} KB", fileSize / 1024);
            }
            else
            {
                _fileSizeText = string.Format("{0} B", fileSize);
            }

            this.Opacity = _opacityTarget;
            UpdatePosition(curX, curY);
            if (!this.Visible) this.Show();
            this.Invalidate();
        }

        public void UpdatePosition(int curX, int curY)
        {
            int targetX = curX + 18;
            int targetY = curY + 22;

            int screenW = Screen.PrimaryScreen.Bounds.Width;
            int screenH = Screen.PrimaryScreen.Bounds.Height;

            if (targetX + this.Width > screenW)
            {
                targetX = curX - this.Width - 10;
            }
            if (targetY + this.Height > screenH)
            {
                targetY = curY - this.Height - 10;
            }

            this.Location = new Point(targetX, targetY);
        }

        public void TriggerDrop(string targetLocation = null)
        {
            _isDropped = true;
            _targetLocationText = !string.IsNullOrEmpty(targetLocation) ? targetLocation : "Desktop";
            this.Invalidate();

            // Smooth fadeout after 750ms
            System.Threading.ThreadPool.QueueUserWorkItem(state =>
            {
                System.Threading.Thread.Sleep(750);
                if (!this.IsDisposed)
                {
                    try { this.BeginInvoke((Action)(() => _fadeTimer.Start())); } catch { }
                }
            });
        }

        public void CancelDrag()
        {
            _fadeTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int r = 16;
            Rectangle rect = new Rectangle(1, 1, this.Width - 3, this.Height - 3);
            using (GraphicsPath path = GetRoundedRect(rect, r))
            {
                // Background
                Color bg = _isDropped ? Color.FromArgb(6, 78, 59) : Color.FromArgb(15, 23, 42);
                using (SolidBrush b = new SolidBrush(bg))
                {
                    g.FillPath(b, path);
                }

                // Border
                Color borderColor = _isDropped ? Color.FromArgb(16, 185, 129) : Color.FromArgb(59, 130, 246);
                using (Pen p = new Pen(borderColor, 1.8f))
                {
                    g.DrawPath(p, path);
                }
            }

            if (_isDropped)
            {
                using (Font iconFont = new Font("Segoe UI", 12f, FontStyle.Bold))
                using (Font titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold))
                using (Font subFont = new Font("Segoe UI", 8.25f, FontStyle.Regular))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                using (SolidBrush subBrush = new SolidBrush(Color.FromArgb(167, 243, 208)))
                {
                    g.DrawString("✅", iconFont, Brushes.White, new PointF(14, 16));

                    string dropMsg = "Dropped at " + _targetLocationText + "!";
                    if (dropMsg.Length > 28) dropMsg = dropMsg.Substring(0, 25) + "...";
                    g.DrawString(dropMsg, titleFont, textBrush, new PointF(46, 11));
                    g.DrawString("Saved & Copied to Clipboard (Ctrl+V)", subFont, subBrush, new PointF(47, 32));
                }
            }
            else
            {
                // 1. Draw Real 32x32 Windows File Icon
                if (_fileBitmap != null)
                {
                    try
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(_fileBitmap, new Rectangle(14, 13, 32, 32));
                    }
                    catch
                    {
                        using (Font iconFont = new Font("Segoe UI", 14f))
                        {
                            g.DrawString("📄", iconFont, Brushes.White, new PointF(12, 13));
                        }
                    }
                }
                else
                {
                    using (Font iconFont = new Font("Segoe UI", 14f))
                    {
                        g.DrawString("📄", iconFont, Brushes.White, new PointF(12, 13));
                    }
                }

                using (Font titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold))
                using (Font subFont = new Font("Segoe UI", 8.25f, FontStyle.Regular))
                using (SolidBrush whiteBrush = new SolidBrush(Color.White))
                using (SolidBrush subBrush = new SolidBrush(Color.FromArgb(148, 163, 184)))
                {
                    string displayTitle = _fileName;
                    if (displayTitle.Length > 26) displayTitle = displayTitle.Substring(0, 23) + "...";
                    g.DrawString(displayTitle, titleFont, whiteBrush, new PointF(54, 11));

                    string prompt = string.Format("{0} • Drop to place file here", _fileSizeText);
                    g.DrawString(prompt, subFont, subBrush, new PointF(55, 32));
                }
            }
        }

        private static GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            Size size = new Size(diameter, diameter);
            Rectangle arc = new Rectangle(bounds.Location, size);
            GraphicsPath path = new GraphicsPath();

            if (radius == 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);

            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_fileBitmap != null) { try { _fileBitmap.Dispose(); } catch { } _fileBitmap = null; }
                if (_fadeTimer != null) { try { _fadeTimer.Dispose(); } catch { } _fadeTimer = null; }
            }
            base.Dispose(disposing);
        }
    }
}
