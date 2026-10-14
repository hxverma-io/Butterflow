using System;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ButterFlow.Core
{
    public class ClipboardManager : IDisposable
    {
        private System.Threading.Timer _pollTimer;
        private string _lastText = string.Empty;
        private int _lastImageHash = 0;
        private string _lastFilePath = string.Empty;
        private bool _isUpdatingInternally;
        private bool _isEnabled;

        public event Action<string> OnClipboardTextCopied;
        public event Action<byte[]> OnClipboardImageCopied;
        public event Action<string> OnFileDropCopied;

        public bool IsEnabled
        {
            get { return _isEnabled; }
            set
            {
                _isEnabled = value;
                if (_isEnabled) Start();
                else Stop();
            }
        }

        public ClipboardManager()
        {
            _isEnabled = true;
        }

        public void Start()
        {
            if (_pollTimer == null)
            {
                _pollTimer = new System.Threading.Timer(CheckClipboard, null, 500, 200);
            }
        }

        public void Stop()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Dispose();
                _pollTimer = null;
            }
        }

        private string _lastFileSignature = string.Empty;

        private void CheckClipboard(object state)
        {
            if (!_isEnabled || _isUpdatingInternally) return;

            try
            {
                // Must be executed on STA thread for Windows Forms Clipboard access
                Thread staThread = new Thread(() =>
                {
                    try
                    {
                        // 1. File Copy / Cut Check in Windows Explorer (PRIORITY 1)
                        if (Clipboard.ContainsFileDropList())
                        {
                            StringCollection files = Clipboard.GetFileDropList();
                            if (files != null && files.Count > 0)
                            {
                                string firstFile = files[0];
                                string sig = string.Empty;
                                foreach (string f in files) sig += f + ";";
                                if (File.Exists(firstFile)) sig += File.GetLastWriteTime(firstFile).Ticks;

                                if (sig != _lastFileSignature)
                                {
                                    _lastFileSignature = sig;
                                    foreach (string f in files)
                                    {
                                        if (!string.IsNullOrEmpty(f) && File.Exists(f))
                                        {
                                            if (OnFileDropCopied != null)
                                            {
                                                OnFileDropCopied(f);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        // 2. Image / Screenshot Clipboard Check (Win+Shift+S)
                        else if (Clipboard.ContainsImage())
                        {
                            Image img = Clipboard.GetImage();
                            if (img != null)
                            {
                                using (MemoryStream ms = new MemoryStream())
                                {
                                    img.Save(ms, ImageFormat.Png);
                                    byte[] pngBytes = ms.ToArray();
                                    int hash = pngBytes.Length ^ (pngBytes.Length > 20 ? BitConverter.ToInt32(pngBytes, 10) : 0);
                                    if (hash != _lastImageHash)
                                    {
                                        _lastImageHash = hash;
                                        if (OnClipboardImageCopied != null)
                                        {
                                            OnClipboardImageCopied(pngBytes);
                                        }
                                    }
                                }
                            }
                        }
                        // 3. Text Clipboard Check
                        else if (Clipboard.ContainsText())
                        {
                            string currentText = Clipboard.GetText();
                            if (!string.IsNullOrEmpty(currentText) && currentText != _lastText)
                            {
                                _lastText = currentText;
                                if (OnClipboardTextCopied != null)
                                {
                                    OnClipboardTextCopied(currentText);
                                }
                            }
                        }
                    }
                    catch { }
                });
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();
                staThread.Join(200);
            }
            catch { }
        }

        public void SetRemoteClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text) || text == _lastText) return;

            _isUpdatingInternally = true;
            _lastText = text;

            try
            {
                Thread staThread = new Thread(() =>
                {
                    try
                    {
                        Clipboard.SetText(text);
                    }
                    catch { }
                    finally
                    {
                        _isUpdatingInternally = false;
                    }
                });
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();
                staThread.Join(300);
            }
            catch
            {
                _isUpdatingInternally = false;
            }
        }

        public void SetRemoteClipboardImage(byte[] pngBytes)
        {
            if (pngBytes == null || pngBytes.Length == 0) return;

            int hash = pngBytes.Length ^ (pngBytes.Length > 20 ? BitConverter.ToInt32(pngBytes, 10) : 0);
            if (hash == _lastImageHash) return;

            _isUpdatingInternally = true;
            _lastImageHash = hash;

            try
            {
                Thread staThread = new Thread(() =>
                {
                    try
                    {
                        using (MemoryStream ms = new MemoryStream(pngBytes))
                        {
                            Image img = Image.FromStream(ms);
                            Clipboard.SetImage(img);
                        }
                    }
                    catch { }
                    finally
                    {
                        _isUpdatingInternally = false;
                    }
                });
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();
                staThread.Join(500);
            }
            catch
            {
                _isUpdatingInternally = false;
            }
        }

        public void SetRemoteClipboardFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            _isUpdatingInternally = true;
            _lastFilePath = filePath;

            try
            {
                Thread staThread = new Thread(() =>
                {
                    try
                    {
                        StringCollection sc = new StringCollection();
                        sc.Add(filePath);
                        Clipboard.SetFileDropList(sc);
                    }
                    catch { }
                    finally
                    {
                        _isUpdatingInternally = false;
                    }
                });
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();
                staThread.Join(300);
            }
            catch
            {
                _isUpdatingInternally = false;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
