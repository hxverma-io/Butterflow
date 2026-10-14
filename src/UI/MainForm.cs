using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ButterFlow.Core;
using ButterFlow.Network;

namespace ButterFlow.UI
{
    public class MainForm : Form
    {
        // Core Services
        private DiscoveryService _discovery;
        private InputReceiver _receiver;
        private HostInputManager _hostManager;
        private ClipboardManager _clipboard;

        // Multi-Machine Mesh Senders
        private readonly Dictionary<ScreenEdge, InputSender> _senders = new Dictionary<ScreenEdge, InputSender>();
        private readonly Dictionary<ScreenEdge, string> _edgeIpMap = new Dictionary<ScreenEdge, string>();

        private DragGhostOverlay _ghostOverlay;
        private EdgeDropZoneBar _edgeDropBar;

        private AppMode _currentMode = AppMode.Host;
        private int _screenWidth;
        private int _screenHeight;
        private int _processPid;

        // Container Panel
        private Panel _mainPanel;

        // 1. macOS Window Chrome & Traffic Lights
        private Panel _titleBarPanel;
        private Rectangle _rectTrafficRed = new Rectangle(18, 16, 12, 12);
        private Rectangle _rectTrafficYellow = new Rectangle(36, 16, 12, 12);
        private Rectangle _rectTrafficGreen = new Rectangle(54, 16, 12, 12);
        private int _hoveredTraffic = 0; // 0=none, 1=red, 2=yellow, 3=green

        // 2. Hero Header
        private Panel _heroHeaderPanel;
        private Panel _statusPillPanel;
        private string _statusPillText = "CONNECTION READY • 0.72ms";

        // 3. Mode Selection Cards (Host vs Client)
        private Panel _cardHostMode;
        private Panel _cardClientMode;

        // 4. Local Network Discovery Section
        private Panel _cardDiscovery;
        private ListBox _listPeers;
        private Panel _emptyDiscoveryPanel;
        private Button _btnConnectSelected;
        private TextBox _txtManualIp;
        private Button _btnManualConnect;
        private int _selectedPeerIndex = -1;

        // 5. Live Connection Diagnostics
        private Panel _cardDiagnostics;
        private Label _lblDiagStatus;
        private Label _lblMetricPing;
        private Label _lblMetricPolling;
        private Label _lblMetricDrops;
        private Button _btnSwitchCursor;

        // 6. Preferences & Behavior
        private Panel _cardPreferences;
        private bool _clipboardToggleState = true;
        private Rectangle _rectToggleBounds = new Rectangle(24, 46, 42, 24);
        private TrackBar _sliderSpeed;
        private Label _lblFrictionValue;

        // 7. Activity Log (Terminal Box)
        private Panel _cardLog;
        private ListBox _listLog;
        private Button _btnTabAll;
        private Button _btnTabSockets;
        private Button _btnTabTelemetry;
        private Label _lblClearLog;
        private string _activeLogFilter = "ALL";

        // 8. Footer Bar
        private Panel _footerPanel;

        private NotifyIcon _trayIcon;

        // ========================================================
        // Apple macOS Light Professional Palette (From Reference UI)
        // ========================================================
        private static readonly Color ColWindowBg       = Color.FromArgb(249, 250, 251); // #F9FAFB Crisp Canvas
        private static readonly Color ColCardBg         = Color.FromArgb(255, 255, 255); // #FFFFFF Pure White
        private static readonly Color ColCardBorder     = Color.FromArgb(229, 231, 235); // #E5E7EB Hairline Border
        private static readonly Color ColCardBorderDark = Color.FromArgb(17, 24, 39);    // #111827 Active 2px Border
        private static readonly Color ColTextBlack      = Color.FromArgb(17, 24, 39);    // #111827 Deep Text
        private static readonly Color ColTextGray       = Color.FromArgb(107, 114, 128); // #6B7280 Muted Gray
        private static readonly Color ColTextLight      = Color.FromArgb(156, 163, 175); // #9CA3AF Subtle
        private static readonly Color ColBtnBlack       = Color.FromArgb(17, 24, 39);    // #111827 Black Action
        private static readonly Color ColBtnBlackHover  = Color.FromArgb(31, 41, 55);    // #1F2937
        private static readonly Color ColGreenDot       = Color.FromArgb(16, 185, 129);  // #10B981 Emerald
        private static readonly Color ColTerminalBg     = Color.FromArgb(8, 10, 15);     // Obsidian Terminal

        public MainForm()
        {
            NativeMethods.SetProcessDPIAware();
            _screenWidth = SystemInformation.VirtualScreen.Width;
            _screenHeight = SystemInformation.VirtualScreen.Height;
            _processPid = Process.GetCurrentProcess().Id;

            InitializeFormProperties();
            InitializeComponents();
            InitializeServices();
            LayoutResponsiveControls();
            UpdateModeCardsVisual();

            this.Resize += (s, e) => LayoutResponsiveControls();
        }

        private void InitializeFormProperties()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            this.Text = "ButterFlow — Zero-Latency Universal Input Control";
            this.Size = new Size(1180, 950);
            this.MinimumSize = new Size(1020, 860);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = ColWindowBg;
            this.ForeColor = ColTextBlack;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            this.Icon = SystemIcons.Application;
            this.AllowDrop = true;

            // System Tray
            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = SystemIcons.Application;
            _trayIcon.Text = "ButterFlow - Zero-Latency Input Mesh";
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.BringToFront();
            };

            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Open ButterFlow", (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; });
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Exit", (s, e) => { CleanupServices(); Application.Exit(); });
            _trayIcon.ContextMenu = trayMenu;

            this.FormClosing += (s, e) =>
            {
                CleanupServices();
                if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
            };

            // Drag and drop onto form
            this.DragEnter += HandleFormDragEnter;
            this.DragDrop += HandleFormDragDrop;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Subtle outer window border
            using (Pen p = new Pen(Color.FromArgb(209, 213, 219), 1f))
            {
                g.DrawRectangle(p, 0, 0, this.Width - 1, this.Height - 1);
            }
        }

        private void InitializeComponents()
        {
            // Main scrolling container
            _mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = ColWindowBg,
                AllowDrop = true
            };
            _mainPanel.DragEnter += HandleFormDragEnter;
            _mainPanel.DragDrop += HandleFormDragDrop;
            this.Controls.Add(_mainPanel);

            // ========================================================
            // 1. macOS WINDOW CHROME (Top Titlebar with Traffic Lights)
            // ========================================================
            _titleBarPanel = new Panel
            {
                Height = 44,
                BackColor = ColCardBg,
                Dock = DockStyle.Top
            };
            _titleBarPanel.Paint += PaintTitleBar;
            _titleBarPanel.MouseDown += HandleWindowDragMouseDown;
            _titleBarPanel.MouseMove += (s, e) =>
            {
                int prev = _hoveredTraffic;
                if (_rectTrafficRed.Contains(e.Location)) _hoveredTraffic = 1;
                else if (_rectTrafficYellow.Contains(e.Location)) _hoveredTraffic = 2;
                else if (_rectTrafficGreen.Contains(e.Location)) _hoveredTraffic = 3;
                else _hoveredTraffic = 0;

                if (prev != _hoveredTraffic) _titleBarPanel.Invalidate();
            };
            _titleBarPanel.MouseLeave += (s, e) =>
            {
                if (_hoveredTraffic != 0) { _hoveredTraffic = 0; _titleBarPanel.Invalidate(); }
            };
            _titleBarPanel.MouseClick += (s, e) =>
            {
                if (_rectTrafficRed.Contains(e.Location)) this.Close();
                else if (_rectTrafficYellow.Contains(e.Location)) this.WindowState = FormWindowState.Minimized;
                else if (_rectTrafficGreen.Contains(e.Location)) this.WindowState = (this.WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized;
            };

            this.Controls.Add(_titleBarPanel); // Stays fixed on top
            _mainPanel.BringToFront();

            // ========================================================
            // 2. HERO HEADER SECTION
            // ========================================================
            _heroHeaderPanel = new Panel
            {
                Height = 56,
                BackColor = Color.Transparent
            };
            _heroHeaderPanel.Paint += PaintHeroHeader;

            _statusPillPanel = new Panel
            {
                Size = new Size(230, 32),
                BackColor = ColCardBg,
                Cursor = Cursors.Default
            };
            _statusPillPanel.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                Rectangle r = new Rectangle(0, 0, _statusPillPanel.Width - 1, _statusPillPanel.Height - 1);
                using (GraphicsPath path = GetRoundedRect(r, 16))
                using (SolidBrush bg = new SolidBrush(ColCardBg))
                using (Pen p = new Pen(ColCardBorder, 1.2f))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(p, path);
                }

                // Green dot
                using (SolidBrush b = new SolidBrush(ColGreenDot))
                {
                    g.FillEllipse(b, 14, 12, 8, 8);
                }

                // Text
                using (Font f = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                using (SolidBrush tb = new SolidBrush(ColTextBlack))
                {
                    g.DrawString(_statusPillText, f, tb, new PointF(28, 8));
                }
            };
            _heroHeaderPanel.Controls.Add(_statusPillPanel);
            _mainPanel.Controls.Add(_heroHeaderPanel);

            // ========================================================
            // 3. MODE SELECTOR CARDS (HOST vs CLIENT)
            // ========================================================
            _cardHostMode = new Panel { Cursor = Cursors.Hand, BackColor = ColCardBg };
            _cardHostMode.Paint += (s, e) => PaintModeCard(e.Graphics, _cardHostMode, true);
            _cardHostMode.Click += (s, e) => SetMode(AppMode.Host);

            _cardClientMode = new Panel { Cursor = Cursors.Hand, BackColor = ColCardBg };
            _cardClientMode.Paint += (s, e) => PaintModeCard(e.Graphics, _cardClientMode, false);
            _cardClientMode.Click += (s, e) => SetMode(AppMode.Client);

            _mainPanel.Controls.AddRange(new Control[] { _cardHostMode, _cardClientMode });

            // ========================================================
            // 4. LOCAL NETWORK DISCOVERY CARD
            // ========================================================
            _cardDiscovery = CreateMacCard();

            _listPeers = new ListBox
            {
                Location = new Point(24, 76),
                BackColor = ColCardBg,
                ForeColor = ColTextBlack,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.5f),
                ItemHeight = 62,
                DrawMode = DrawMode.OwnerDrawFixed
            };
            _listPeers.DrawItem += DrawPeerListItem;
            _listPeers.SelectedIndexChanged += (s, e) =>
            {
                _selectedPeerIndex = _listPeers.SelectedIndex;
                _listPeers.Invalidate();
            };

            _emptyDiscoveryPanel = new Panel { Location = new Point(24, 76), BackColor = Color.FromArgb(249, 250, 251) };
            _emptyDiscoveryPanel.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, _emptyDiscoveryPanel.Width - 1, _emptyDiscoveryPanel.Height - 1);
                using (GraphicsPath path = GetRoundedRect(r, 10))
                using (Pen p = new Pen(ColCardBorder, 1))
                {
                    g.DrawPath(p, path);
                }
            };
            Label lblEmptyIcon = new Label { Text = "🔍", Font = new Font("Segoe UI", 16), Location = new Point(24, 24), Size = new Size(32, 32), TextAlign = ContentAlignment.MiddleCenter };
            Label lblEmptyTitle = new Label { Text = "Broadcasting & Scanning for nearby computers...", Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = ColTextBlack, Location = new Point(66, 20), AutoSize = true };
            Label lblEmptySub = new Label { Text = "Ensure ButterFlow is open on your other PC (both on the same local Wi-Fi or subnet)", Font = new Font("Segoe UI", 8.5f), ForeColor = ColTextGray, Location = new Point(66, 44), AutoSize = true };
            _emptyDiscoveryPanel.Controls.AddRange(new Control[] { lblEmptyIcon, lblEmptyTitle, lblEmptySub });

            _btnConnectSelected = CreateDarkButton("🔗  Connect Selected Peer", 0, 76, 280, 38);
            _btnConnectSelected.Click += (s, e) => ConnectSelectedPeer();

            _txtManualIp = new TextBox
            {
                Text = "192.168.1.",
                Font = new Font("Segoe UI", 10f),
                BackColor = ColCardBg,
                ForeColor = ColTextBlack,
                BorderStyle = BorderStyle.FixedSingle
            };

            _btnManualConnect = CreateLightBorderButton("Connect", 0, 0, 90, 32);
            _btnManualConnect.Click += (s, e) => ConnectManualIp(_txtManualIp.Text.Trim(), ScreenEdge.Right);

            _cardDiscovery.Paint += PaintDiscoveryHeader;
            _cardDiscovery.Controls.AddRange(new Control[] { _listPeers, _emptyDiscoveryPanel, _btnConnectSelected, _txtManualIp, _btnManualConnect });
            _mainPanel.Controls.Add(_cardDiscovery);

            // ========================================================
            // 5. LIVE CONNECTION DIAGNOSTICS CARD
            // ========================================================
            _cardDiagnostics = CreateMacCard();
            _cardDiagnostics.Paint += PaintDiagnosticsCard;

            _lblDiagStatus = new Label
            {
                Text = "Status: Host Active  •  Ready to transfer mouse seamlessly",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = ColTextBlack,
                Location = new Point(44, 46),
                AutoSize = true
            };

            Label lblShortcuts = new Label
            {
                Text = "Shortcuts:   Ctrl + Alt + S  (Switch)   |   Ctrl + Alt + Esc  (Snap Back)",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = ColTextGray,
                Location = new Point(24, 76),
                AutoSize = true
            };

            _lblMetricPing = new Label { Text = "0.72 ms", Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = ColTextBlack, AutoSize = true };
            _lblMetricPolling = new Label { Text = "1000 Hz", Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = ColTextBlack, AutoSize = true };
            _lblMetricDrops = new Label { Text = "0.00%", Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = ColTextBlack, AutoSize = true };

            _btnSwitchCursor = CreateDarkButton("⚡  Switch Cursor (Ctrl+Alt+S)", 0, 42, 230, 38);
            _btnSwitchCursor.Click += (s, e) =>
            {
                if (_currentMode == AppMode.Host && _hostManager != null)
                {
                    _hostManager.ToggleControl();
                }
                else
                {
                    MessageBox.Show("Switch Cursor works on the HOST PC.", "ButterFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            _cardDiagnostics.Controls.AddRange(new Control[] { _lblDiagStatus, lblShortcuts, _lblMetricPing, _lblMetricPolling, _lblMetricDrops, _btnSwitchCursor });
            _mainPanel.Controls.Add(_cardDiagnostics);

            // ========================================================
            // 6. PREFERENCES & BEHAVIOR CARD
            // ========================================================
            _cardPreferences = CreateMacCard();
            _cardPreferences.Paint += PaintPreferencesCard;
            _cardPreferences.MouseClick += (s, e) =>
            {
                if (_rectToggleBounds.Contains(e.Location))
                {
                    _clipboardToggleState = !_clipboardToggleState;
                    if (_clipboard != null) _clipboard.IsEnabled = _clipboardToggleState;
                    _cardPreferences.Invalidate();
                }
            };

            _sliderSpeed = new TrackBar
            {
                Minimum = 8,
                Maximum = 24,
                Value = 12,
                TickStyle = TickStyle.None,
                BackColor = ColCardBg,
                Width = 180,
                Height = 30
            };
            _sliderSpeed.Scroll += (s, e) =>
            {
                float mult = _sliderSpeed.Value / 10.0f;
                if (_hostManager != null) _hostManager.Sensitivity = mult;
                _lblFrictionValue.Text = string.Format("Smooth ({0:F1}x)", mult);
            };

            _lblFrictionValue = new Label
            {
                Text = "Smooth (1.2x)",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = ColTextBlack,
                AutoSize = true,
                BackColor = Color.FromArgb(243, 244, 246)
            };

            _cardPreferences.Controls.AddRange(new Control[] { _sliderSpeed, _lblFrictionValue });
            _mainPanel.Controls.Add(_cardPreferences);

            // ========================================================
            // 7. ACTIVITY LOG (TERMINAL IN LIGHT CARD)
            // ========================================================
            _cardLog = CreateMacCard();
            _cardLog.Paint += PaintLogHeader;

            _btnTabAll = CreateFilterTabButton("All Logs", true);
            _btnTabSockets = CreateFilterTabButton("Sockets", false);
            _btnTabTelemetry = CreateFilterTabButton("Telemetry", false);

            _btnTabAll.Click += (s, e) => SwitchLogTab("ALL", _btnTabAll);
            _btnTabSockets.Click += (s, e) => SwitchLogTab("SOCKETS", _btnTabSockets);
            _btnTabTelemetry.Click += (s, e) => SwitchLogTab("TELEMETRY", _btnTabTelemetry);

            _lblClearLog = new Label
            {
                Text = "Clear Log",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = ColTextGray,
                Cursor = Cursors.Hand,
                AutoSize = true
            };
            _lblClearLog.Click += (s, e) => _listLog.Items.Clear();

            Panel terminalBox = new Panel
            {
                Location = new Point(24, 60),
                BackColor = ColTerminalBg
            };
            terminalBox.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, terminalBox.Width - 1, terminalBox.Height - 1);
                using (GraphicsPath path = GetRoundedRect(r, 10))
                using (Pen p = new Pen(Color.FromArgb(30, 41, 59), 1))
                {
                    g.DrawPath(p, path);
                }
            };

            _listLog = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = ColTerminalBg,
                ForeColor = Color.FromArgb(226, 232, 240),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 22
            };
            _listLog.DrawItem += DrawLogTerminalItem;
            terminalBox.Controls.Add(_listLog);

            _cardLog.Controls.AddRange(new Control[] { _btnTabAll, _btnTabSockets, _btnTabTelemetry, _lblClearLog, terminalBox });
            _mainPanel.Controls.Add(_cardLog);

            // ========================================================
            // 8. FOOTER: SYSTEM METRICS & ATTRIBUTION
            // ========================================================
            _footerPanel = new Panel { Height = 46, BackColor = Color.Transparent };
            _footerPanel.Paint += PaintFooter;
            _mainPanel.Controls.Add(_footerPanel);
        }

        // ========================================================
        // Custom Renderers for Reference UI Look & Feel
        // ========================================================
        private void PaintTitleBar(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 1. Traffic Lights
            Color redCol = (_hoveredTraffic == 1) ? Color.FromArgb(255, 95, 86) : Color.FromArgb(239, 68, 68);
            using (SolidBrush b = new SolidBrush(redCol)) g.FillEllipse(b, _rectTrafficRed);

            Color yellowCol = (_hoveredTraffic == 2) ? Color.FromArgb(254, 188, 46) : Color.FromArgb(245, 158, 11);
            using (SolidBrush b = new SolidBrush(yellowCol)) g.FillEllipse(b, _rectTrafficYellow);

            Color greenCol = (_hoveredTraffic == 3) ? Color.FromArgb(39, 201, 63) : Color.FromArgb(16, 185, 129);
            using (SolidBrush b = new SolidBrush(greenCol)) g.FillEllipse(b, _rectTrafficGreen);

            // 2. Title next to traffic lights
            using (Font f = new Font("Segoe UI", 9f, FontStyle.Regular))
            using (SolidBrush tb = new SolidBrush(ColTextGray))
            {
                g.DrawString("⚡  ButterFlow  —  Zero-Latency Universal Input Control", f, tb, new PointF(80, 13));
            }

            // 3. Top-right daemon indicator
            string daemonText = string.Format("● daemon: pid {0}", _processPid);
            using (Font f = new Font("Segoe UI", 8.5f, FontStyle.Regular))
            using (SolidBrush gb = new SolidBrush(ColGreenDot))
            {
                SizeF sz = g.MeasureString(daemonText, f);
                g.DrawString(daemonText, f, gb, new PointF(_titleBarPanel.Width - sz.Width - 28, 14));
            }

            // Bottom hairline border
            using (Pen p = new Pen(ColCardBorder, 1))
            {
                g.DrawLine(p, 0, _titleBarPanel.Height - 1, _titleBarPanel.Width, _titleBarPanel.Height - 1);
            }
        }

        private void PaintHeroHeader(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Black Squircle Logo
            Rectangle logoRect = new Rectangle(0, 4, 44, 44);
            using (GraphicsPath path = GetRoundedRect(logoRect, 10))
            using (SolidBrush b = new SolidBrush(ColBtnBlack))
            {
                g.FillPath(b, path);
            }
            using (Font f = new Font("Segoe UI", 16f, FontStyle.Bold))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("⚡", f, Brushes.White, logoRect, sf);
            }

            // Title: ButterFlow
            using (Font tf = new Font("Segoe UI", 16f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(ColTextBlack))
            {
                g.DrawString("ButterFlow", tf, tb, new PointF(54, 4));
            }

            // Badge: v2.4 Pro
            Rectangle badgeR = new Rectangle(186, 12, 58, 20);
            using (GraphicsPath bp = GetRoundedRect(badgeR, 6))
            using (SolidBrush bb = new SolidBrush(Color.FromArgb(243, 244, 246)))
            using (Pen p = new Pen(ColCardBorder, 1))
            {
                g.FillPath(bb, bp);
                g.DrawPath(p, bp);
            }
            using (Font bf = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            using (SolidBrush btb = new SolidBrush(ColTextGray))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("v2.4 Pro", bf, btb, badgeR, sf);
            }

            // Subtitle
            using (Font sf = new Font("Segoe UI", 9f, FontStyle.Regular))
            using (SolidBrush sb = new SolidBrush(ColTextGray))
            {
                g.DrawString("High-Precision Zero-Latency Universal Input Control", sf, sb, new PointF(56, 32));
            }
        }

        private void PaintModeCard(Graphics g, Panel card, bool isHost)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool isSelected = (isHost && _currentMode == AppMode.Host) || (!isHost && _currentMode == AppMode.Client);
            Rectangle r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);

            // Card background & border
            using (GraphicsPath path = GetRoundedRect(r, 12))
            using (SolidBrush bg = new SolidBrush(ColCardBg))
            using (Pen p = new Pen(isSelected ? ColCardBorderDark : ColCardBorder, isSelected ? 2.2f : 1.2f))
            {
                g.FillPath(bg, path);
                g.DrawPath(p, path);
            }

            // Left Squircle Icon
            Rectangle iconR = new Rectangle(20, (card.Height - 44) / 2, 44, 44);
            using (GraphicsPath ip = GetRoundedRect(iconR, 10))
            using (SolidBrush ib = new SolidBrush(isSelected ? ColBtnBlack : Color.FromArgb(243, 244, 246)))
            {
                g.FillPath(ib, ip);
            }
            using (Font f = new Font("Segoe UI", 16f))
            using (SolidBrush tb = new SolidBrush(isSelected ? Color.White : ColTextGray))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(isHost ? "🖥️" : "💻", f, tb, iconR, sf);
            }

            // Title & Pill
            string title = isHost ? "HOST MODE" : "CLIENT MODE";
            using (Font tf = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(ColTextBlack))
            {
                g.DrawString(title, tf, tb, new PointF(76, 20));
            }

            // Tag Pill
            string tag = isHost ? "MASTER CONTROLLER" : "RECEIVER";
            Rectangle tagR = new Rectangle(isHost ? 174 : 188, 22, isHost ? 122 : 68, 19);
            using (GraphicsPath tp = GetRoundedRect(tagR, 5))
            using (SolidBrush tb = new SolidBrush(isHost ? ColBtnBlack : Color.FromArgb(243, 244, 246)))
            {
                g.FillPath(tb, tp);
            }
            using (Font tf = new Font("Segoe UI", 7.25f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(isHost ? Color.White : ColTextGray))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(tag, tf, tb, tagR, sf);
            }

            // Subtext
            string sub = isHost ? "This PC transmits cursor & keyboard seamlessly" : "This PC is controlled by the Host machine";
            using (Font sf = new Font("Segoe UI", 8.75f, FontStyle.Regular))
            using (SolidBrush sb = new SolidBrush(ColTextGray))
            {
                g.DrawString(sub, sf, sb, new PointF(76, 44));
            }

            // Right Radio Circle
            Rectangle radioR = new Rectangle(card.Width - 44, (card.Height - 22) / 2, 22, 22);
            if (isSelected)
            {
                using (Pen p = new Pen(ColBtnBlack, 2.2f))
                {
                    g.DrawEllipse(p, radioR);
                }
                Rectangle inner = new Rectangle(radioR.X + 5, radioR.Y + 5, 12, 12);
                using (SolidBrush b = new SolidBrush(ColBtnBlack))
                {
                    g.FillEllipse(b, inner);
                }
            }
            else
            {
                using (Pen p = new Pen(ColCardBorder, 1.8f))
                {
                    g.DrawEllipse(p, radioR);
                }
            }
        }

        private void PaintDiscoveryHeader(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Icon: Share
            using (Font f = new Font("Segoe UI", 12f))
            using (SolidBrush tb = new SolidBrush(ColTextBlack))
            {
                g.DrawString("🔗", f, tb, new PointF(24, 18));
            }

            // Title
            using (Font tf = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(ColTextBlack))
            {
                g.DrawString("Local Network Discovery", tf, tb, new PointF(52, 18));
            }

            using (Font mf = new Font("Segoe UI", 9f, FontStyle.Regular))
            using (SolidBrush mb = new SolidBrush(ColTextGray))
            {
                g.DrawString("(Zero-Config mDNS)", mf, mb, new PointF(238, 20));
            }

            // Subtitle
            using (Font sf = new Font("Segoe UI", 8.75f, FontStyle.Regular))
            using (SolidBrush sb = new SolidBrush(ColTextGray))
            {
                g.DrawString("Auto-detected peers on local subnet 192.168.1.0/24", sf, sb, new PointF(52, 40));
            }

            // Right scanning pill
            Rectangle scanR = new Rectangle(_cardDiscovery.Width - 190, 20, 166, 26);
            using (GraphicsPath sp = GetRoundedRect(scanR, 13))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(243, 244, 246)))
            using (Pen p = new Pen(ColCardBorder, 1))
            {
                g.FillPath(sb, sp);
                g.DrawPath(p, sp);
            }
            using (SolidBrush gb = new SolidBrush(ColGreenDot))
            {
                g.FillEllipse(gb, scanR.X + 14, scanR.Y + 9, 8, 8);
            }
            using (Font sf = new Font("Segoe UI", 8.25f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(ColTextGray))
            {
                g.DrawString("Scanning every 3s", sf, tb, new PointF(scanR.X + 28, scanR.Y + 5));
            }

            // Right Action Labels
            int rightColX = _listPeers.Right + 24;
            using (Font af = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (SolidBrush ab = new SolidBrush(ColTextBlack))
            {
                g.DrawString("Action on Selected Peer", af, ab, new PointF(rightColX, 76));
                g.DrawString("Manual Direct Connect", af, ab, new PointF(rightColX, 132));
            }
        }

        private void DrawPeerListItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _listPeers.Items.Count) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            PeerListItem item = _listPeers.Items[e.Index] as PeerListItem;
            if (item == null || item.Peer == null) return;

            bool isSelected = (e.Index == _selectedPeerIndex);
            Rectangle r = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 3, e.Bounds.Width - 4, e.Bounds.Height - 6);

            // Item card background
            using (GraphicsPath path = GetRoundedRect(r, 10))
            using (SolidBrush bg = new SolidBrush(ColCardBg))
            using (Pen p = new Pen(isSelected ? ColCardBorderDark : ColCardBorder, isSelected ? 2f : 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(p, path);
            }

            // Left icon squircle
            Rectangle iconR = new Rectangle(r.X + 12, r.Y + 11, 36, 36);
            using (GraphicsPath ip = GetRoundedRect(iconR, 8))
            using (SolidBrush ib = new SolidBrush(Color.FromArgb(243, 244, 246)))
            {
                g.FillPath(ib, ip);
            }
            using (Font f = new Font("Segoe UI", 12f))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("💻", f, Brushes.Black, iconR, sf);
            }

            // Device Name
            using (Font nf = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(ColTextBlack))
            {
                g.DrawString(item.Peer.MachineName, nf, tb, new PointF(r.X + 56, r.Y + 11));
            }

            // Status Tag Pill
            bool isConnected = item.Peer.Mode == AppMode.Client;
            Rectangle tagR = new Rectangle(r.X + 220, r.Y + 12, isConnected ? 74 : 52, 18);
            using (GraphicsPath tp = GetRoundedRect(tagR, 4))
            using (SolidBrush tb = new SolidBrush(isConnected ? Color.FromArgb(209, 250, 229) : Color.FromArgb(243, 244, 246)))
            {
                g.FillPath(tb, tp);
            }
            using (Font tf = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(isConnected ? Color.FromArgb(6, 95, 70) : ColTextGray))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(isConnected ? "Connected" : "Idle", tf, tb, tagR, sf);
            }

            // Subtext: IP & Port
            string sub = string.Format("{0} • Port 42043 • Zero-Latency Mesh", item.Peer.IpAddress);
            using (Font sf = new Font("Segoe UI", 8.25f, FontStyle.Regular))
            using (SolidBrush sb = new SolidBrush(ColTextGray))
            {
                g.DrawString(sub, sf, sb, new PointF(r.X + 56, r.Y + 33));
            }

            // Right Select Pill
            Rectangle btnR = new Rectangle(r.Right - 84, r.Y + 16, 72, 26);
            using (GraphicsPath bp = GetRoundedRect(btnR, 6))
            using (SolidBrush bb = new SolidBrush(isSelected ? ColBtnBlack : Color.FromArgb(243, 244, 246)))
            using (Pen p = new Pen(isSelected ? ColBtnBlack : ColCardBorder, 1))
            {
                g.FillPath(bb, bp);
                g.DrawPath(p, bp);
            }
            using (Font bf = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (SolidBrush btb = new SolidBrush(isSelected ? Color.White : ColTextBlack))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(isSelected ? "Selected" : "Select", bf, btb, btnR, sf);
            }
        }

        private void PaintDiagnosticsCard(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Title
            using (Font tf = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(ColTextBlack))
            {
                g.DrawString("📈  Live Connection Diagnostics", tf, tb, new PointF(24, 18));
            }

            // Status Green Dot
            using (SolidBrush gb = new SolidBrush(ColGreenDot))
            {
                g.FillEllipse(gb, 26, 50, 9, 9);
            }

            // Vertical separator before metrics
            int sepX = _btnSwitchCursor.Left - 260;
            using (Pen sp = new Pen(ColCardBorder, 1))
            {
                g.DrawLine(sp, sepX, 36, sepX, 86);
            }

            // Metric Titles
            using (Font hf = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            using (SolidBrush hb = new SolidBrush(ColTextGray))
            {
                g.DrawString("PING (RTT)", hf, hb, new PointF(sepX + 24, 38));
                g.DrawString("POLLING", hf, hb, new PointF(sepX + 110, 38));
                g.DrawString("PACKET DROPS", hf, hb, new PointF(sepX + 180, 38));
            }

            // Green dot next to polling
            using (SolidBrush gb = new SolidBrush(ColGreenDot))
            {
                g.FillEllipse(gb, sepX + 162, 60, 6, 6);
            }
        }

        private void PaintPreferencesCard(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Title
            using (Font tf = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(ColTextBlack))
            {
                g.DrawString("⚙️  Preferences & Behavior", tf, tb, new PointF(24, 18));
            }

            // iOS Toggle Switch (Left)
            using (GraphicsPath tp = GetRoundedRect(_rectToggleBounds, 12))
            using (SolidBrush tb = new SolidBrush(_clipboardToggleState ? ColBtnBlack : Color.FromArgb(209, 213, 219)))
            {
                g.FillPath(tb, tp);
            }
            int knobX = _clipboardToggleState ? _rectToggleBounds.Right - 22 : _rectToggleBounds.Left + 2;
            Rectangle knobR = new Rectangle(knobX, _rectToggleBounds.Top + 2, 20, 20);
            using (SolidBrush kb = new SolidBrush(Color.White))
            {
                g.FillEllipse(kb, knobR);
            }

            // Toggle Label
            using (Font lf = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (SolidBrush lfb = new SolidBrush(ColTextBlack))
            {
                g.DrawString("Sync Shared Clipboard", lf, lfb, new PointF(76, 42));
            }
            using (Font sf = new Font("Segoe UI", 8.25f, FontStyle.Regular))
            using (SolidBrush sb = new SolidBrush(ColTextGray))
            {
                g.DrawString("Seamless copy/paste across devices (Text, images & HTML)", sf, sb, new PointF(76, 62));
            }

            // Vertical divider
            int divX = _cardPreferences.Width / 2 + 30;
            using (Pen p = new Pen(ColCardBorder, 1))
            {
                g.DrawLine(p, divX, 36, divX, 86);
            }

            // Right slider labels
            using (Font lf = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (SolidBrush lfb = new SolidBrush(ColTextBlack))
            {
                g.DrawString("Cursor Velocity & Sensitivity:", lf, lfb, new PointF(divX + 24, 40));
            }
            using (Font sf = new Font("Segoe UI", 8.25f, FontStyle.Regular))
            using (SolidBrush sb = new SolidBrush(ColTextGray))
            {
                g.DrawString("Prevent accidental boundary leaping", sf, sb, new PointF(divX + 24, 62));
            }
        }

        private void PaintLogHeader(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Title
            using (Font tf = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(ColTextBlack))
            {
                g.DrawString("📋  Activity Log", tf, tb, new PointF(24, 18));
            }
        }

        private void DrawLogTerminalItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _listLog.Items.Count) return;

            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            string line = _listLog.Items[e.Index].ToString();
            Rectangle r = e.Bounds;

            using (SolidBrush bg = new SolidBrush(ColTerminalBg))
            {
                g.FillRectangle(bg, r);
            }

            // Colorize by log tag
            Color textColor = Color.FromArgb(226, 232, 240); // default off-white
            if (line.Contains("[READY]") || line.Contains("✅") || line.Contains("Placed")) textColor = Color.FromArgb(52, 211, 153); // Emerald
            else if (line.Contains("[mDNS]") || line.Contains("[DISC]")) textColor = Color.FromArgb(56, 189, 248); // Sky blue
            else if (line.Contains("[INIT]") || line.Contains("🚀")) textColor = Color.FromArgb(250, 204, 21); // Amber
            else if (line.Contains("failed") || line.Contains("error")) textColor = Color.FromArgb(248, 113, 113); // Rose

            using (Font f = new Font("Consolas", 8.75f))
            using (SolidBrush tb = new SolidBrush(textColor))
            {
                g.DrawString(line, f, tb, new PointF(r.X + 8, r.Y + 2));
            }
        }

        private void PaintFooter(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (Font f = new Font("Segoe UI", 8.25f, FontStyle.Regular))
            using (SolidBrush tb = new SolidBrush(ColTextLight))
            {
                string leftText = string.Format("● Daemon Active (PID {0})   |   UDP Broadcast: Enabled   |   Jitter: <0.08ms", _processPid);
                g.DrawString(leftText, f, tb, new PointF(24, 14));

                string rightText = "ButterFlow Engine  •  AES-256-GCM  •  Himanshu Verma (@hxverma-io)";
                SizeF sz = g.MeasureString(rightText, f);
                g.DrawString(rightText, f, tb, new PointF(_footerPanel.Width - sz.Width - 24, 14));
            }
        }

        // ========================================================
        // Component Factories
        // ========================================================
        private Panel CreateMacCard()
        {
            Panel card = new Panel { BackColor = ColCardBg };
            card.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                using (GraphicsPath path = GetRoundedRect(r, 12))
                using (Pen p = new Pen(ColCardBorder, 1.2f))
                {
                    g.DrawPath(p, path);
                }
            };
            return card;
        }

        private Button CreateDarkButton(string text, int x, int y, int w, int h)
        {
            Button btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = ColBtnBlack,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                Rectangle r = new Rectangle(0, 0, btn.Width, btn.Height);
                using (GraphicsPath path = GetRoundedRect(r, 8))
                using (SolidBrush b = new SolidBrush(ColBtnBlack))
                {
                    g.FillPath(b, path);
                }
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString(btn.Text, btn.Font, Brushes.White, r, sf);
                }
            };
            return btn;
        }

        private Button CreateLightBorderButton(string text, int x, int y, int w, int h)
        {
            Button btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = ColCardBg,
                ForeColor = ColTextBlack,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.75f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                Rectangle r = new Rectangle(0, 0, btn.Width - 1, btn.Height - 1);
                using (GraphicsPath path = GetRoundedRect(r, 6))
                using (SolidBrush b = new SolidBrush(ColCardBg))
                using (Pen p = new Pen(ColCardBorder, 1))
                {
                    g.FillPath(b, path);
                    g.DrawPath(p, path);
                }
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (SolidBrush tb = new SolidBrush(ColTextBlack))
                {
                    g.DrawString(btn.Text, btn.Font, tb, r, sf);
                }
            };
            return btn;
        }

        private Button CreateFilterTabButton(string text, bool isActive)
        {
            Button btn = new Button
            {
                Text = text,
                Size = new Size(76, 26),
                BackColor = isActive ? ColBtnBlack : Color.FromArgb(243, 244, 246),
                ForeColor = isActive ? Color.White : ColTextGray,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                Rectangle r = new Rectangle(0, 0, btn.Width - 1, btn.Height - 1);
                using (GraphicsPath path = GetRoundedRect(r, 6))
                using (SolidBrush b = new SolidBrush(btn.BackColor))
                {
                    g.FillPath(b, path);
                }
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (SolidBrush tb = new SolidBrush(btn.ForeColor))
                {
                    g.DrawString(btn.Text, btn.Font, tb, r, sf);
                }
            };
            return btn;
        }

        private void SwitchLogTab(string filter, Button activeBtn)
        {
            _activeLogFilter = filter;
            _btnTabAll.BackColor = Color.FromArgb(243, 244, 246);
            _btnTabAll.ForeColor = ColTextGray;
            _btnTabSockets.BackColor = Color.FromArgb(243, 244, 246);
            _btnTabSockets.ForeColor = ColTextGray;
            _btnTabTelemetry.BackColor = Color.FromArgb(243, 244, 246);
            _btnTabTelemetry.ForeColor = ColTextGray;

            activeBtn.BackColor = ColBtnBlack;
            activeBtn.ForeColor = Color.White;

            _btnTabAll.Invalidate();
            _btnTabSockets.Invalidate();
            _btnTabTelemetry.Invalidate();
        }

        public static GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
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

        // ========================================================
        // Window Dragging & Resizing Support
        // ========================================================
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private void HandleWindowDragMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.X > 75)
            {
                ReleaseCapture();
                SendMessage(this.Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTCLIENT = 1;
            const int HTLEFT = 10;
            const int HTRIGHT = 11;
            const int HTTOP = 12;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOM = 15;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT)
                {
                    Point pt = this.PointToClient(new Point(m.LParam.ToInt32()));
                    int border = 8;
                    if (pt.X <= border && pt.Y <= border) m.Result = (IntPtr)HTTOPLEFT;
                    else if (pt.X >= this.ClientSize.Width - border && pt.Y <= border) m.Result = (IntPtr)HTTOPRIGHT;
                    else if (pt.X <= border && pt.Y >= this.ClientSize.Height - border) m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (pt.X >= this.ClientSize.Width - border && pt.Y >= this.ClientSize.Height - border) m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (pt.X <= border) m.Result = (IntPtr)HTLEFT;
                    else if (pt.X >= this.ClientSize.Width - border) m.Result = (IntPtr)HTRIGHT;
                    else if (pt.Y <= border) m.Result = (IntPtr)HTTOP;
                    else if (pt.Y >= this.ClientSize.Height - border) m.Result = (IntPtr)HTBOTTOM;
                }
                return;
            }

            if (m.Msg == NativeMethods.WM_INPUT)
            {
                if (_currentMode == AppMode.Host && _hostManager != null && _hostManager.IsControllingClient)
                {
                    try
                    {
                        uint dwSize = 0;
                        uint headerSize = (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTHEADER));
                        NativeMethods.GetRawInputData(m.LParam, NativeMethods.RID_INPUT, IntPtr.Zero, ref dwSize, headerSize);
                        if (dwSize > 0)
                        {
                            IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
                            try
                            {
                                if (NativeMethods.GetRawInputData(m.LParam, NativeMethods.RID_INPUT, buffer, ref dwSize, headerSize) == dwSize)
                                {
                                    uint dwType = (uint)Marshal.ReadInt32(buffer, 0);
                                    if (dwType == NativeMethods.RIM_TYPEMOUSE)
                                    {
                                        IntPtr rawMousePtr = (IntPtr)(buffer.ToInt64() + headerSize);
                                        int deltaX = Marshal.ReadInt32(rawMousePtr, 12);
                                        int deltaY = Marshal.ReadInt32(rawMousePtr, 16);
                                        if (deltaX != 0 || deltaY != 0)
                                        {
                                            _hostManager.ProcessRawInput(deltaX, deltaY);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(buffer);
                            }
                        }
                    }
                    catch { }
                }
            }
            base.WndProc(ref m);
        }

        // ========================================================
        // Responsive Layout Calculation
        // ========================================================
        private void LayoutResponsiveControls()
        {
            int totalWidth = _mainPanel.ClientSize.Width - 56;
            if (totalWidth > 1140) totalWidth = 1140;
            if (totalWidth < 960) totalWidth = 960;

            int startX = Math.Max(28, (_mainPanel.ClientSize.Width - totalWidth) / 2);
            int curY = 16;

            // 1. Hero Header
            _heroHeaderPanel.Location = new Point(startX, curY);
            _heroHeaderPanel.Width = totalWidth;
            _statusPillPanel.Location = new Point(totalWidth - _statusPillPanel.Width, 12);
            curY += _heroHeaderPanel.Height + 16;

            // 2. Mode Selector Cards
            int modeCardW = (totalWidth - 16) / 2;
            int modeCardH = 82;
            _cardHostMode.Location = new Point(startX, curY);
            _cardHostMode.Size = new Size(modeCardW, modeCardH);

            _cardClientMode.Location = new Point(startX + modeCardW + 16, curY);
            _cardClientMode.Size = new Size(modeCardW, modeCardH);
            curY += modeCardH + 16;

            // 3. Local Network Discovery Card
            int discCardH = 200;
            _cardDiscovery.Location = new Point(startX, curY);
            _cardDiscovery.Size = new Size(totalWidth, discCardH);

            int rightColW = 280;
            int rightColX = totalWidth - rightColW - 24;
            int leftListW = rightColX - 44;

            _listPeers.Size = new Size(leftListW, 108);
            _emptyDiscoveryPanel.Size = new Size(leftListW, 108);

            _btnConnectSelected.Location = new Point(rightColX, 98);
            _btnConnectSelected.Size = new Size(rightColW, 36);

            int txtIpW = rightColW - 100;
            _txtManualIp.Location = new Point(rightColX, 154);
            _txtManualIp.Size = new Size(txtIpW, 30);
            _btnManualConnect.Location = new Point(rightColX + txtIpW + 8, 153);
            _btnManualConnect.Size = new Size(rightColW - (txtIpW + 8), 32);

            curY += discCardH + 16;

            // 4. Live Connection Diagnostics Card
            int diagCardH = 115;
            _cardDiagnostics.Location = new Point(startX, curY);
            _cardDiagnostics.Size = new Size(totalWidth, diagCardH);

            _btnSwitchCursor.Location = new Point(totalWidth - 230 - 24, 40);
            int sepX = _btnSwitchCursor.Left - 260;
            _lblMetricPing.Location = new Point(sepX + 24, 56);
            _lblMetricPolling.Location = new Point(sepX + 110, 56);
            _lblMetricDrops.Location = new Point(sepX + 180, 56);

            curY += diagCardH + 16;

            // 5. Preferences & Behavior Card
            int prefCardH = 110;
            _cardPreferences.Location = new Point(startX, curY);
            _cardPreferences.Size = new Size(totalWidth, prefCardH);

            int divX = _cardPreferences.Width / 2 + 30;
            _sliderSpeed.Location = new Point(divX + 24, 60);
            _lblFrictionValue.Location = new Point(_sliderSpeed.Right + 12, 65);

            curY += prefCardH + 16;

            // 6. Activity Log Card
            int logCardH = 210;
            _cardLog.Location = new Point(startX, curY);
            _cardLog.Size = new Size(totalWidth, logCardH);

            _btnTabAll.Location = new Point(140, 16);
            _btnTabSockets.Location = new Point(222, 16);
            _btnTabTelemetry.Location = new Point(304, 16);
            _lblClearLog.Location = new Point(totalWidth - 84, 20);

            Control terminalBox = null;
            foreach (Control c in _cardLog.Controls) if (c is Panel) terminalBox = c;
            if (terminalBox != null)
            {
                terminalBox.Size = new Size(totalWidth - 48, 134);
            }

            curY += logCardH + 16;

            // 7. Footer
            _footerPanel.Location = new Point(startX, curY);
            _footerPanel.Width = totalWidth;
        }

        // ========================================================
        // Services & Mesh Logic
        // ========================================================
        private void InitializeServices()
        {
            // 1. Discovery
            _discovery = new DiscoveryService(_currentMode, _screenWidth, _screenHeight);
            _discovery.OnPeerFound += peer => SafeInvoke(() =>
            {
                RefreshPeerList();
                LogMessage(string.Format("[DISC] Discovered peer: {0} ({1})", peer.MachineName, peer.IpAddress));
            });
            _discovery.OnPeerRemoved += ip => SafeInvoke(() =>
            {
                RefreshPeerList();
            });
            _discovery.Start();

            // 2. Clipboard (Multi-format: Text, Screenshots, Files)
            _clipboard = new ClipboardManager();
            _clipboard.OnClipboardTextCopied += text =>
            {
                if (!_clipboardToggleState) return;
                if (_currentMode == AppMode.Host)
                {
                    foreach (var s in _senders.Values)
                    {
                        if (s != null && s.IsConnected) s.SendClipboard(text);
                    }
                }
                else if (_currentMode == AppMode.Client && _receiver != null && _receiver.IsConnected)
                {
                    _receiver.SendClipboardToHost(text);
                }
            };

            _clipboard.OnClipboardImageCopied += pngBytes =>
            {
                if (!_clipboardToggleState) return;
                if (_currentMode == AppMode.Host)
                {
                    foreach (var s in _senders.Values)
                    {
                        if (s != null && s.IsConnected) s.SendImageClipboard(pngBytes);
                    }
                    LogMessage("🖼️ Screenshot synced to connected Mesh screens! (Ctrl+V)");
                }
                else if (_currentMode == AppMode.Client && _receiver != null && _receiver.IsConnected)
                {
                    _receiver.SendImageClipboardToHost(pngBytes);
                    LogMessage("🖼️ Screenshot sent to Host! (Ctrl+V)");
                }
            };

            _clipboard.OnFileDropCopied += filePath =>
            {
                SendFileToConnectedPeers(filePath);
            };

            // Default: Host Mode
            SetMode(AppMode.Host);
        }

        private void RefreshPeerList()
        {
            _listPeers.Items.Clear();
            if (_discovery == null) return;

            var peers = _discovery.GetActivePeers();
            if (peers.Count == 0)
            {
                _emptyDiscoveryPanel.Visible = true;
                _listPeers.Visible = false;
                _selectedPeerIndex = -1;
            }
            else
            {
                _emptyDiscoveryPanel.Visible = false;
                _listPeers.Visible = true;
                foreach (var peer in peers)
                {
                    _listPeers.Items.Add(new PeerListItem { Display = peer.MachineName, Peer = peer });
                }
                if (_listPeers.Items.Count > 0 && _selectedPeerIndex < 0)
                {
                    _selectedPeerIndex = 0;
                    _listPeers.SelectedIndex = 0;
                }
            }
            _listPeers.Invalidate();
        }

        private class PeerListItem
        {
            public string Display { get; set; }
            public PeerInfo Peer { get; set; }
            public override string ToString() { return Display; }
        }

        private void SetMode(AppMode mode)
        {
            _currentMode = mode;
            if (_discovery != null) _discovery.CurrentMode = mode;
            UpdateModeCardsVisual();

            if (mode == AppMode.Host)
            {
                if (_receiver == null) StartClientMode();
                StartHostMode();
            }
            else
            {
                StopHostMode();
                StartClientMode();
            }
        }

        private void UpdateModeCardsVisual()
        {
            if (_cardHostMode != null) _cardHostMode.Invalidate();
            if (_cardClientMode != null) _cardClientMode.Invalidate();
            if (_lblDiagStatus != null)
            {
                _lblDiagStatus.Text = (_currentMode == AppMode.Host)
                    ? "Status: Host Active  •  Ready to transfer mouse seamlessly"
                    : "Status: Client Active  •  Listening for Host connection";
            }
        }

        private void StartHostMode()
        {
            if (_hostManager == null)
            {
                _hostManager = new HostInputManager(null, _screenWidth, _screenHeight);
                _hostManager.PushBarrierThreshold = 1; // Instant snappy crossover
                _hostManager.Sensitivity = _sliderSpeed.Value / 10.0f;
                _hostManager.OnControlStateChanged += (isControlling, edge) => SafeInvoke(() =>
                {
                    if (isControlling)
                    {
                        UpdateStatus(string.Format("Active: Controlling {0} Screen ({1})", edge, _edgeIpMap.ContainsKey(edge) ? _edgeIpMap[edge] : ""));
                    }
                    else
                    {
                        UpdateStatus("Host Active  •  Ready to transfer mouse seamlessly");
                    }
                });
                _hostManager.OnIncomingReverseDragDrop += () => SafeInvoke(() =>
                {
                    Point curPos = Cursor.Position;
                    string targetFolder = ShellHelper.GetDropTargetFolder(curPos.X, curPos.Y);
                    foreach (var s in _senders.Values)
                    {
                        if (s != null) s.FinalizeReverseDrop(targetFolder);
                    }
                    string displayTarget = Path.GetFileName(targetFolder);
                    if (string.IsNullOrEmpty(displayTarget) || targetFolder.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), StringComparison.OrdinalIgnoreCase))
                    {
                        displayTarget = "Desktop";
                    }

                    if (_ghostOverlay != null && !_ghostOverlay.IsDisposed)
                    {
                        _ghostOverlay.TriggerDrop(displayTarget);
                    }
                    LogMessage("[DROP] Reverse dragged file placed directly into: " + displayTarget);
                });

                _hostManager.OnReverseDragMoved += (x, y) => SafeInvoke(() =>
                {
                    if (_ghostOverlay != null && !_ghostOverlay.IsDisposed && _ghostOverlay.Visible)
                    {
                        _ghostOverlay.UpdatePosition(x, y);
                    }
                });

                _hostManager.OnLogMessage += msg => SafeInvoke(() => LogMessage(msg));
                _hostManager.Start();
            }

            InitializeEdgeDropBar(ScreenEdge.Right);
            UpdateStatus("Host Active  •  Ready to transfer mouse seamlessly");
            LogMessage("[INIT] Switched to Host Mode (Master Controller)");
        }

        private void StopHostMode()
        {
            if (_hostManager != null)
            {
                _hostManager.Stop();
                _hostManager = null;
            }

            foreach (var s in _senders.Values)
            {
                if (s != null) s.Disconnect();
            }
            _senders.Clear();
            _edgeIpMap.Clear();
            if (_edgeDropBar != null) { try { _edgeDropBar.Dispose(); } catch { } _edgeDropBar = null; }
        }

        private void StartClientMode()
        {
            if (_receiver == null)
            {
                _receiver = new InputReceiver(_screenWidth, _screenHeight);
                _receiver.HostPositionRelativeToClient = ScreenEdge.Left;
                _receiver.OnConnectionStatusChanged += connected => SafeInvoke(() =>
                {
                    if (connected)
                    {
                        UpdateStatus("Connected: Controlled by Host");
                        _statusPillText = "CONNECTED TO HOST • 0.65ms";
                    }
                    else
                    {
                        UpdateStatus("Client Mode - Listening for Host connection");
                        _statusPillText = "CLIENT LISTENING • 42043";
                    }
                    _statusPillPanel.Invalidate();
                });

                _receiver.OnClipboardReceived += text => SafeInvoke(() =>
                {
                    if (_clipboard != null && _clipboardToggleState) _clipboard.SetRemoteClipboardText(text);
                });

                _receiver.OnImageClipboardReceived += pngBytes => SafeInvoke(() =>
                {
                    if (_clipboard != null && _clipboardToggleState) _clipboard.SetRemoteClipboardImage(pngBytes);
                    LogMessage("🖼️ Screenshot received from Host! Press Ctrl+V to paste.");
                });

                _receiver.OnDragStarted += (fileName, fileSize) => SafeInvoke(() =>
                {
                    Point curPos = Cursor.Position;
                    if (_ghostOverlay == null || _ghostOverlay.IsDisposed) _ghostOverlay = new DragGhostOverlay();
                    _ghostOverlay.ShowDrag(fileName, fileSize, curPos.X, curPos.Y);
                    LogMessage("🚀 Cross-Screen Drag incoming: " + fileName);
                });

                _receiver.OnDragMoved += (normX, normY) => SafeInvoke(() =>
                {
                    Point curPos = Cursor.Position;
                    if (_ghostOverlay != null && !_ghostOverlay.IsDisposed && _ghostOverlay.Visible)
                    {
                        _ghostOverlay.UpdatePosition(curPos.X, curPos.Y);
                    }
                });

                _receiver.OnDragDropped += (dropX, dropY) => SafeInvoke(() =>
                {
                    Point curPos = Cursor.Position;
                    string targetFolder = ShellHelper.GetDropTargetFolder(curPos.X, curPos.Y);
                    _receiver.FinalizeDragDrop(targetFolder);

                    string displayTarget = Path.GetFileName(targetFolder);
                    if (string.IsNullOrEmpty(displayTarget) || targetFolder.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), StringComparison.OrdinalIgnoreCase))
                    {
                        displayTarget = "Desktop";
                    }

                    if (_ghostOverlay != null && !_ghostOverlay.IsDisposed)
                    {
                        _ghostOverlay.TriggerDrop(displayTarget);
                    }
                    LogMessage("📂 Dragged file dropped! Saved directly to: " + displayTarget);
                });

                _receiver.OnDragCancelled += () => SafeInvoke(() =>
                {
                    if (_ghostOverlay != null && !_ghostOverlay.IsDisposed)
                    {
                        _ghostOverlay.CancelDrag();
                    }
                    LogMessage("Cross-screen drag cancelled");
                });

                _receiver.OnFileReceived += (name, path) => HandleFileReceived(name, path);
                _receiver.OnLogMessage += msg => SafeInvoke(() => LogMessage(msg));
                _receiver.Start();
            }

            InitializeEdgeDropBar(ScreenEdge.Left);
            UpdateStatus("Client Mode Active • Listening on ports 42043/42044");
            LogMessage("[INIT] Switched to Client Mode (Receiver). Ports open.");
        }

        private void StopClientMode()
        {
            if (_receiver != null)
            {
                _receiver.Stop();
                _receiver = null;
            }
        }

        private void ConnectSelectedPeer()
        {
            if (_selectedPeerIndex < 0 || _selectedPeerIndex >= _listPeers.Items.Count)
            {
                MessageBox.Show("Please select a discovered machine from the list.", "ButterFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            PeerListItem selected = _listPeers.Items[_selectedPeerIndex] as PeerListItem;
            if (selected == null || selected.Peer == null) return;

            PeerInfo peer = selected.Peer;
            if (peer.Mode == AppMode.Host)
            {
                MessageBox.Show(
                    string.Format("'{0}' is in HOST MODE!\n\nSwitch '{0}' to CLIENT MODE so it can join the Mesh.", peer.MachineName),
                    "Switch Target to Client Mode",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            ConnectManualIp(peer.IpAddress, ScreenEdge.Right);
        }

        private void ConnectManualIp(string ip, ScreenEdge edge)
        {
            if (string.IsNullOrEmpty(ip) || ip == "192.168.1.")
            {
                MessageBox.Show("Please enter a valid IP address.", "ButterFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_currentMode != AppMode.Host)
            {
                MessageBox.Show("Switch to 'Host Mode' to connect to client screens.", "ButterFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            UpdateStatus(string.Format("Connecting to {0}...", ip));
            LogMessage(string.Format("[mDNS] Initiating zero-latency handshake with {0}...", ip));

            InputSender sender = new InputSender();
            sender.OnLatencyMeasured += ms => SafeInvoke(() =>
            {
                _lblMetricPing.Text = string.Format("{0:F2} ms", ms);
                _statusPillText = string.Format("CONNECTION READY • {0:F2}ms", ms);
                _statusPillPanel.Invalidate();
            });

            sender.OnRemoteClipboard += text => SafeInvoke(() =>
            {
                if (_clipboard != null && _clipboardToggleState) _clipboard.SetRemoteClipboardText(text);
            });

            sender.OnRemoteImageClipboard += pngBytes => SafeInvoke(() =>
            {
                if (_clipboard != null && _clipboardToggleState) _clipboard.SetRemoteClipboardImage(pngBytes);
                LogMessage("🖼️ Screenshot received from " + edge + " screen! Press Ctrl+V.");
            });

            sender.OnFileReceived += (name, path) => HandleFileReceived(name, path);

            sender.OnRemoteDragStarted += (fileName, fileSize) => SafeInvoke(() =>
            {
                Point curPos = Cursor.Position;
                sender.IsReverseDragActive = true;
                if (_ghostOverlay == null || _ghostOverlay.IsDisposed) _ghostOverlay = new DragGhostOverlay();
                _ghostOverlay.ShowDrag(fileName, fileSize, curPos.X, curPos.Y);
                bool isStillHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
                if (_hostManager != null) _hostManager.IsReverseDragActive = isStillHeld;
                if (!isStillHeld)
                {
                    string targetFolder = ShellHelper.GetDropTargetFolder(curPos.X, curPos.Y);
                    sender.FinalizeReverseDrop(targetFolder);
                    string displayTarget = Path.GetFileName(targetFolder);
                    if (string.IsNullOrEmpty(displayTarget) || targetFolder.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), StringComparison.OrdinalIgnoreCase))
                    {
                        displayTarget = "Desktop";
                    }
                    _ghostOverlay.TriggerDrop(displayTarget);
                }
                LogMessage(string.Format("🚀 Incoming Reverse Drag: {0}", fileName));
            });

            sender.OnDisconnected += () => SafeInvoke(() =>
            {
                DisconnectEdge(edge);
                LogMessage(string.Format("[DISC] Screen {0} ({1}) disconnected.", edge, ip));
            });

            bool success = sender.Connect(ip);
            if (success)
            {
                if (_senders.ContainsKey(edge))
                {
                    _senders[edge].Disconnect();
                }
                _senders[edge] = sender;
                _edgeIpMap[edge] = ip;

                if (_hostManager != null)
                {
                    _hostManager.SetPeer(edge, sender);
                }

                UpdateStatus(string.Format("Host Active  •  Connected to {0}", ip));
                LogMessage(string.Format("[READY] Zero-latency socket handshake synchronized (Peer: {0})", ip));
                _statusPillText = string.Format("MESH ACTIVE ({0} PCs)", _senders.Count + 1);
                _statusPillPanel.Invalidate();
            }
            else
            {
                sender.Disconnect();
                UpdateStatus(string.Format("Connection to {0} failed. Check IP & Firewall.", ip));
                LogMessage("[ERR] Connection failed to " + ip);
            }
        }

        private void DisconnectEdge(ScreenEdge edge)
        {
            if (_senders.ContainsKey(edge))
            {
                _senders[edge].Disconnect();
                _senders.Remove(edge);
            }
            if (_edgeIpMap.ContainsKey(edge))
            {
                _edgeIpMap.Remove(edge);
            }
            if (_hostManager != null)
            {
                _hostManager.RemovePeer(edge);
            }
            _statusPillText = _senders.Count > 0 ? string.Format("MESH ACTIVE ({0} PCs)", _senders.Count + 1) : "CONNECTION READY • 0.72ms";
            _statusPillPanel.Invalidate();
        }

        // ========================================================
        // File Drag & Drop Handling
        // ========================================================
        private void HandleFormDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void HandleFormDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    foreach (string file in files)
                    {
                        SendFileToConnectedPeers(file);
                    }
                }
            }
        }

        private void SendFileToConnectedPeers(string filePath)
        {
            if (!File.Exists(filePath)) return;
            string fileName = Path.GetFileName(filePath);
            LogMessage("📤 Starting file beam: " + fileName);

            if (_currentMode == AppMode.Host)
            {
                bool sentAny = false;
                foreach (var kvp in _senders)
                {
                    if (kvp.Value != null && kvp.Value.IsConnected)
                    {
                        sentAny = true;
                        kvp.Value.SendFile(filePath, pct => SafeInvoke(() =>
                        {
                            UpdateStatus(string.Format("Beaming {0} ({1}%)", fileName, pct));
                        }));
                    }
                }
                if (!sentAny)
                {
                    MessageBox.Show("No client screen is currently connected to receive the file.\nConnect a client PC first!", "ButterFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            else if (_currentMode == AppMode.Client)
            {
                if (_receiver != null && _receiver.IsConnected)
                {
                    _receiver.SendFileToHost(filePath, pct => SafeInvoke(() =>
                    {
                        UpdateStatus(string.Format("Beaming {0} to Host ({1}%)", fileName, pct));
                    }));
                }
                else
                {
                    MessageBox.Show("Client is not connected to a Host PC.", "ButterFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void HandleFileReceived(string fileName, string savedPath)
        {
            SafeInvoke(() =>
            {
                string folderName = Path.GetFileName(Path.GetDirectoryName(savedPath));
                if (string.IsNullOrEmpty(folderName) || Path.GetDirectoryName(savedPath).Equals(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), StringComparison.OrdinalIgnoreCase))
                {
                    folderName = "Desktop";
                }
                UpdateStatus(string.Format("✅ Received: {0} (Placed at {1})", fileName, folderName));
                LogMessage(string.Format("📂 File placed at: {0} -> Copied to Clipboard!", savedPath));
                if (_clipboard != null && _clipboardToggleState)
                {
                    _clipboard.SetRemoteClipboardFile(savedPath);
                }
                if (_trayIcon != null)
                {
                    _trayIcon.ShowBalloonTip(3000, "ButterFlow File Dropped", fileName + " placed at " + folderName + "!", ToolTipIcon.Info);
                }
            });
        }

        private void UpdateStatus(string text)
        {
            if (_lblDiagStatus != null) _lblDiagStatus.Text = "Status: " + text;
        }

        private void LogMessage(string msg)
        {
            string line = string.Format("[{0:HH:mm:ss}]  {1}", DateTime.Now, msg);
            _listLog.Items.Insert(0, line);
            if (_listLog.Items.Count > 100) _listLog.Items.RemoveAt(100);
        }

        private void SafeInvoke(Action act)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            try { this.BeginInvoke(act); } catch { }
        }

        private void RegisterRawInput()
        {
            try
            {
                NativeMethods.RAWINPUTDEVICE[] rid = new NativeMethods.RAWINPUTDEVICE[1];
                rid[0].usUsagePage = 0x01; // Generic Desktop Controls
                rid[0].usUsage = 0x02;     // Mouse
                rid[0].dwFlags = NativeMethods.RIDEV_INPUTSINK; // Capture globally even in background
                rid[0].hwndTarget = this.Handle;

                bool success = NativeMethods.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICE)));
                if (success)
                {
                    LogMessage("[INIT] 1000Hz Optical Sensor RawInput engine active");
                }
            }
            catch { }
        }

        private void InitializeEdgeDropBar(ScreenEdge edge)
        {
            try
            {
                if (_edgeDropBar != null && !_edgeDropBar.IsDisposed)
                {
                    _edgeDropBar.UpdateEdge(edge);
                }
                else
                {
                    _edgeDropBar = new EdgeDropZoneBar(edge);
                    _edgeDropBar.OnFilesDropped += files => SafeInvoke(new Action(() =>
                    {
                        if (files != null)
                        {
                            foreach (string f in files)
                            {
                                SendFileToConnectedPeers(f);
                            }
                        }
                    }));
                    _edgeDropBar.Show();
                }
            }
            catch { }
        }

        private void CleanupServices()
        {
            StopHostMode();
            StopClientMode();
            if (_edgeDropBar != null) { try { _edgeDropBar.Dispose(); } catch { } _edgeDropBar = null; }
            if (_ghostOverlay != null) { try { _ghostOverlay.Dispose(); } catch { } _ghostOverlay = null; }
            if (_discovery != null) { _discovery.Dispose(); _discovery = null; }
            if (_clipboard != null) { _clipboard.Dispose(); _clipboard = null; }
        }
    }
}
