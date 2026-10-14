using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using ButterFlow.Core;

namespace ButterFlow.UI
{
    public class EdgeDropZoneBar : Form
    {
        private ScreenEdge _edge;
        private bool _isHoveredWithDrag = false;
        private bool _isSuccessFlash = false;
        private System.Windows.Forms.Timer _flashTimer;

        public event Action<string[]> OnFilesDropped;

        public EdgeDropZoneBar(ScreenEdge edge)
        {
            _edge = edge;

            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.AllowDrop = true;
            this.DoubleBuffered = true;
            this.BackColor = Color.FromArgb(15, 23, 42); // Slate 900
            this.Opacity = 0.25;

            _flashTimer = new System.Windows.Forms.Timer();
            _flashTimer.Interval = 1200;
            _flashTimer.Tick += (s, e) =>
            {
                _flashTimer.Stop();
                _isSuccessFlash = false;
                CollapseBar();
            };

            this.DragEnter += HandleDragEnter;
            this.DragOver += HandleDragOver;
            this.DragLeave += HandleDragLeave;
            this.DragDrop += HandleDragDrop;

            PositionOnEdge();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }

        public void UpdateEdge(ScreenEdge edge)
        {
            _edge = edge;
            PositionOnEdge();
        }

        public void PositionOnEdge()
        {
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            int normalThickness = 8;

            switch (_edge)
            {
                case ScreenEdge.Right:
                    this.Location = new Point(screen.Right - normalThickness, screen.Top + 60);
                    this.Size = new Size(normalThickness, screen.Height - 120);
                    break;
                case ScreenEdge.Left:
                    this.Location = new Point(screen.Left, screen.Top + 60);
                    this.Size = new Size(normalThickness, screen.Height - 120);
                    break;
                case ScreenEdge.Top:
                    this.Location = new Point(screen.Left + 60, screen.Top);
                    this.Size = new Size(screen.Width - 120, normalThickness);
                    break;
                case ScreenEdge.Bottom:
                    this.Location = new Point(screen.Left + 60, screen.Bottom - normalThickness);
                    this.Size = new Size(screen.Width - 120, normalThickness);
                    break;
            }

            this.Opacity = 0.25;
            this.Invalidate();
        }

        private void ExpandBar()
        {
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            int expandedThickness = 65;

            switch (_edge)
            {
                case ScreenEdge.Right:
                    this.Location = new Point(screen.Right - expandedThickness, screen.Top + 40);
                    this.Size = new Size(expandedThickness, screen.Height - 80);
                    break;
                case ScreenEdge.Left:
                    this.Location = new Point(screen.Left, screen.Top + 40);
                    this.Size = new Size(expandedThickness, screen.Height - 80);
                    break;
                case ScreenEdge.Top:
                    this.Location = new Point(screen.Left + 40, screen.Top);
                    this.Size = new Size(screen.Width - 80, expandedThickness);
                    break;
                case ScreenEdge.Bottom:
                    this.Location = new Point(screen.Left + 40, screen.Bottom - expandedThickness);
                    this.Size = new Size(screen.Width - 80, expandedThickness);
                    break;
            }

            this.Opacity = 0.92;
            this.Invalidate();
        }

        private void CollapseBar()
        {
            _isHoveredWithDrag = false;
            PositionOnEdge();
        }

        private void HandleDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                _isHoveredWithDrag = true;
                ExpandBar();
            }
        }

        private void HandleDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void HandleDragLeave(object sender, EventArgs e)
        {
            if (!_isSuccessFlash)
            {
                CollapseBar();
            }
        }

        private void HandleDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    _isSuccessFlash = true;
                    this.Invalidate();
                    _flashTimer.Start();

                    if (OnFilesDropped != null)
                    {
                        OnFilesDropped(files);
                    }
                }
                else
                {
                    CollapseBar();
                }
            }
            else
            {
                CollapseBar();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            int r = Math.Min(this.Width, this.Height) / 2;
            if (r > 12) r = 12;

            using (GraphicsPath path = MainForm.GetRoundedRect(rect, r))
            {
                if (_isSuccessFlash)
                {
                    // Green Success Glow
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(16, 185, 129)))
                    using (Pen p = new Pen(Color.FromArgb(52, 211, 153), 1.5f))
                    {
                        g.FillPath(b, path);
                        g.DrawPath(p, path);
                    }
                    using (Font f = new Font("Segoe UI", 9.5f, FontStyle.Bold))
                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        g.DrawString("✅ Beamed!", f, Brushes.White, this.ClientRectangle, sf);
                    }
                }
                else if (_isHoveredWithDrag)
                {
                    // Active Drag Cyan/Blue Glow
                    using (LinearGradientBrush lgb = new LinearGradientBrush(this.ClientRectangle, Color.FromArgb(37, 99, 235), Color.FromArgb(14, 165, 233), LinearGradientMode.Vertical))
                    using (Pen p = new Pen(Color.FromArgb(56, 189, 248), 1.8f))
                    {
                        g.FillPath(lgb, path);
                        g.DrawPath(p, path);
                    }
                    using (Font f = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        string prompt = (_edge == ScreenEdge.Right || _edge == ScreenEdge.Left) ? "⚡\n\nD\nR\nO\nP\n\nT\nO\n\nB\nE\nA\nM" : "⚡ Drop File to Beam across PCs";
                        g.DrawString(prompt, f, Brushes.White, this.ClientRectangle, sf);
                    }
                }
                else
                {
                    // Idle discreet edge indicator
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(37, 99, 235)))
                    {
                        g.FillPath(b, path);
                    }
                }
            }
        }
    }
}
