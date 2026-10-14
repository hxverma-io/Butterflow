using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ButterFlow.Installer
{
    public class SetupForm : Form
    {
        private ProgressBar _progressBar;
        private Label _lblStatus;
        private Label _lblTitle;
        private Label _lblSubtitle;
        private Button _btnInstall;
        private Button _btnCancel;
        private CheckBox _chkDesktopShortcut;
        private CheckBox _chkAutoStart;
        private CheckBox _chkLaunchNow;
        private Panel _headerPanel;

        private readonly string _installDir;

        public SetupForm()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _installDir = Path.Combine(localAppData, "ButterFlow");

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "ButterFlow Setup — Ultra-Smooth KVM";
            this.Size = new Size(520, 420);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = true;
            this.BackColor = Color.FromArgb(249, 250, 251);
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // Header Panel
            _headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 85,
                BackColor = Color.FromArgb(17, 24, 39) // Deep Midnight
            };

            _lblTitle = new Label
            {
                Text = "ButterFlow Setup",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                Location = new Point(24, 16),
                AutoSize = true
            };

            _lblSubtitle = new Label
            {
                Text = "Install the ultra-smooth, sub-millisecond software KVM",
                ForeColor = Color.FromArgb(156, 163, 175),
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Location = new Point(25, 48),
                AutoSize = true
            };

            _headerPanel.Controls.Add(_lblTitle);
            _headerPanel.Controls.Add(_lblSubtitle);

            // Body options
            Label lblTarget = new Label
            {
                Text = "Installation Directory:",
                Location = new Point(24, 105),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(55, 65, 81)
            };

            TextBox txtTarget = new TextBox
            {
                Text = _installDir,
                ReadOnly = true,
                Location = new Point(24, 128),
                Width = 456,
                BackColor = Color.White
            };

            _chkDesktopShortcut = new CheckBox
            {
                Text = "Create Desktop Shortcut",
                Checked = true,
                Location = new Point(24, 168),
                AutoSize = true,
                ForeColor = Color.FromArgb(31, 41, 55)
            };

            _chkAutoStart = new CheckBox
            {
                Text = "Start ButterFlow automatically when Windows starts",
                Checked = false,
                Location = new Point(24, 196),
                AutoSize = true,
                ForeColor = Color.FromArgb(31, 41, 55)
            };

            _chkLaunchNow = new CheckBox
            {
                Text = "Launch ButterFlow immediately after installation",
                Checked = true,
                Location = new Point(24, 224),
                AutoSize = true,
                ForeColor = Color.FromArgb(31, 41, 55)
            };

            _progressBar = new ProgressBar
            {
                Location = new Point(24, 264),
                Width = 456,
                Height = 18,
                Style = ProgressBarStyle.Continuous,
                Visible = false
            };

            _lblStatus = new Label
            {
                Text = "Ready to install ButterFlow on your system.",
                Location = new Point(24, 288),
                Width = 456,
                Height = 36,
                ForeColor = Color.FromArgb(107, 114, 128)
            };

            // Buttons
            _btnInstall = new Button
            {
                Text = "Install Now",
                Location = new Point(264, 328),
                Width = 110,
                Height = 36,
                BackColor = Color.FromArgb(17, 24, 39),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnInstall.FlatAppearance.BorderSize = 0;
            _btnInstall.Click += (s, e) => PerformInstallation();

            _btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(384, 328),
                Width = 96,
                Height = 36,
                BackColor = Color.FromArgb(229, 231, 235),
                ForeColor = Color.FromArgb(55, 65, 81),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnCancel.FlatAppearance.BorderSize = 0;
            _btnCancel.Click += (s, e) => this.Close();

            this.Controls.Add(_headerPanel);
            this.Controls.Add(lblTarget);
            this.Controls.Add(txtTarget);
            this.Controls.Add(_chkDesktopShortcut);
            this.Controls.Add(_chkAutoStart);
            this.Controls.Add(_chkLaunchNow);
            this.Controls.Add(_progressBar);
            this.Controls.Add(_lblStatus);
            this.Controls.Add(_btnInstall);
            this.Controls.Add(_btnCancel);
        }

        private void PerformInstallation()
        {
            _btnInstall.Enabled = false;
            _btnCancel.Enabled = false;
            _progressBar.Visible = true;
            _progressBar.Value = 10;
            _lblStatus.Text = "Creating installation directory...";

            try
            {
                if (!Directory.Exists(_installDir))
                {
                    Directory.CreateDirectory(_installDir);
                }

                _progressBar.Value = 30;
                _lblStatus.Text = "Extracting ButterFlow executable...";

                string exeTarget = Path.Combine(_installDir, "ButterFlow.exe");
                ExtractPayload(exeTarget);

                _progressBar.Value = 60;
                _lblStatus.Text = "Configuring Windows Firewall rules...";
                ConfigureFirewall(exeTarget);

                _progressBar.Value = 80;
                _lblStatus.Text = "Creating shortcuts and system registration...";

                // Start Menu shortcut
                string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ButterFlow");
                if (!Directory.Exists(startMenuDir)) Directory.CreateDirectory(startMenuDir);
                CreateShortcut(Path.Combine(startMenuDir, "ButterFlow.lnk"), exeTarget);

                // Desktop shortcut
                if (_chkDesktopShortcut.Checked)
                {
                    string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    CreateShortcut(Path.Combine(desktopDir, "ButterFlow.lnk"), exeTarget);
                }

                // Autostart registry
                if (_chkAutoStart.Checked)
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (key != null) key.SetValue("ButterFlow", "\"" + exeTarget + "\"");
                    }
                }

                // Register Uninstaller
                RegisterUninstall(exeTarget);

                _progressBar.Value = 100;
                _lblStatus.Text = "Installation completed successfully!";
                _lblStatus.ForeColor = Color.FromArgb(16, 185, 129);

                _btnCancel.Text = "Finish";
                _btnCancel.Enabled = true;

                if (_chkLaunchNow.Checked)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exeTarget,
                        WorkingDirectory = _installDir
                    });
                }

                _btnCancel.Click += (s, e) => this.Close();
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Error during setup: " + ex.Message;
                _lblStatus.ForeColor = Color.Red;
                _btnCancel.Enabled = true;
            }
        }

        private void ExtractPayload(string destinationPath)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            // Try to extract from embedded resource
            using (Stream resStream = asm.GetManifestResourceStream("ButterFlow.exe"))
            {
                if (resStream != null)
                {
                    using (FileStream fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write))
                    {
                        resStream.CopyTo(fs);
                    }
                    return;
                }
            }

            // Fallback: look in same folder as installer
            string localPayload = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ButterFlow.exe");
            if (File.Exists(localPayload))
            {
                File.Copy(localPayload, destinationPath, true);
                return;
            }

            throw new FileNotFoundException("Could not find ButterFlow.exe payload to install.");
        }

        private void ConfigureFirewall(string exePath)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = string.Format("advfirewall firewall add rule name=\"ButterFlow\" dir=in action=allow program=\"{0}\" enable=yes profile=any", exePath),
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process p = Process.Start(psi);
                if (p != null) p.WaitForExit(3000);
            }
            catch { }
        }

        private void CreateShortcut(string shortcutPath, string targetPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    shortcut.TargetPath = targetPath;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                    shortcut.Description = "ButterFlow — Ultra-Smooth Software KVM";
                    shortcut.Save();
                }
            }
            catch { }
        }

        private void RegisterUninstall(string exePath)
        {
            try
            {
                string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ButterFlow";
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    if (key != null)
                    {
                        key.SetValue("DisplayName", "ButterFlow — Ultra-Smooth Software KVM");
                        key.SetValue("DisplayIcon", exePath);
                        key.SetValue("DisplayVersion", "1.0.0");
                        key.SetValue("Publisher", "ButterFlow Core Engineering Team");
                        key.SetValue("InstallLocation", _installDir);
                        key.SetValue("UninstallString", "cmd.exe /c rd /s /q \"" + _installDir + "\"");
                    }
                }
            }
            catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }
}
