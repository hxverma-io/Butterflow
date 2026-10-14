using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ButterFlowNative
{
    public class Program
    {
        // Win32 Constants
        private const int WH_MOUSE_LL = 14;
        private const int WH_KEYBOARD_LL = 13;

        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_HWHEEL = 0x1000;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        // Delegates
        private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClipCursor(ref RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClipCursor(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSetTimerResolution(uint DesiredResolution, bool SetResolution, out uint CurrentResolution);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string TaskName, ref int TaskIndex);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)]
            public uint type;
            [FieldOffset(4)]
            public MOUSEINPUT mi;
            [FieldOffset(4)]
            public KEYBDINPUT ki;
        }

        // State
        private static IntPtr _mouseHook = IntPtr.Zero;
        private static IntPtr _keyboardHook = IntPtr.Zero;
        private static LowLevelProc _mouseDelegate;
        private static LowLevelProc _keyboardDelegate;

        private static bool _remoteActive = false;
        private static int _lockX = 0;
        private static int _lockY = 0;
        private static POINT _lastPoint;
        private static HashSet<ushort> _activeDownKeys = new HashSet<ushort>();
        private static readonly object _stdoutLock = new object();

        // Screen edge bounds for detection
        private static int _screenWidth = 1920;
        private static int _screenHeight = 1080;
        private static int _edgeThreshold = 2; // pixels near edge
        private static bool _edgeSwitchEnabled = true;

        static void Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.InputEncoding = Encoding.UTF8;
            }
            catch { }

            _screenWidth = Screen.PrimaryScreen.Bounds.Width;
            _screenHeight = Screen.PrimaryScreen.Bounds.Height;

            // Start command listener thread
            Thread cmdThread = new Thread(CommandLoop) { IsBackground = true };
            cmdThread.Start();

            // Setup hooks on current message pump thread
            _mouseDelegate = MouseHookCallback;
            _keyboardDelegate = KeyboardHookCallback;

            using (System.Diagnostics.Process curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (System.Diagnostics.ProcessModule curModule = curProcess.MainModule)
            {
                IntPtr hModule = GetModuleHandle(curModule.ModuleName);
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseDelegate, hModule, 0);
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardDelegate, hModule, 0);
            }

            // Elevate to sub-millisecond Windows kernel timer and MMCSS Games scheduler
            TimeBeginPeriod(1);
            try { uint res; NtSetTimerResolution(5000, true, out res); } catch { }
            try { int taskIdx = 0; AvSetMmThreadCharacteristics("Games", ref taskIdx); } catch { }

            SendJson("{\"type\":\"READY\",\"screenWidth\":" + _screenWidth + ",\"screenHeight\":" + _screenHeight + "}");

            // Run Windows message loop to keep hooks alive
            Application.Run();

            // Cleanup
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            ClipCursor(IntPtr.Zero);
            TimeEndPeriod(1);
        }

        private static void SendJson(string json)
        {
            lock (_stdoutLock)
            {
                Console.WriteLine(json);
            }
        }

        private static void CommandLoop()
        {
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                try
                {
                    HandleCommand(line);
                }
                catch (Exception ex)
                {
                    SendJson("{\"type\":\"ERROR\",\"message\":\"" + Escape(ex.Message) + "\"}");
                }
            }
        }

        private static void HandleCommand(string json)
        {
            if (json.Contains("\"command\":\"SET_REMOTE\""))
            {
                bool active = json.Contains("\"active\":true");
                _remoteActive = active;
                if (active)
                {
                    // Center cursor inside wide 800x800 zone to eliminate edge gesture clamping
                    _lockX = _screenWidth / 2;
                    _lockY = _screenHeight / 2;
                    SetCursorPos(_lockX, _lockY);
                    _lastPoint.x = _lockX;
                    _lastPoint.y = _lockY;
                    RECT clip = new RECT { left = _lockX - 400, top = _lockY - 400, right = _lockX + 400, bottom = _lockY + 400 };
                    ClipCursor(ref clip);
                }
                else
                {
                    ClipCursor(IntPtr.Zero);
                    ResetLocalHeldKeys();
                }
                SendJson("{\"type\":\"REMOTE_STATE\",\"active\":" + (_remoteActive ? "true" : "false") + "}");
            }
            else if (json.Contains("\"command\":\"RESET_KEYS\""))
            {
                ResetLocalHeldKeys();
                SendJson("{\"type\":\"KEYS_RESET\"}");
            }
            else if (json.Contains("\"command\":\"SET_BOUNDS\""))
            {
                // Parse screen bounds
                int w = ParseInt(json, "width", _screenWidth);
                int h = ParseInt(json, "height", _screenHeight);
                int thresh = ParseInt(json, "threshold", _edgeThreshold);
                bool enable = !json.Contains("\"enable\":false");
                _screenWidth = w;
                _screenHeight = h;
                _edgeThreshold = thresh;
                _edgeSwitchEnabled = enable;
                SendJson("{\"type\":\"BOUNDS_SET\",\"width\":" + _screenWidth + ",\"height\":" + _screenHeight + "}");
            }
            else if (json.Contains("\"command\":\"GET_MONITORS\""))
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"type\":\"MONITORS\",\"monitors\":[");
                Screen[] screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"index\":" + i + ",\"x\":" + screens[i].Bounds.X + ",\"y\":" + screens[i].Bounds.Y +
                              ",\"width\":" + screens[i].Bounds.Width + ",\"height\":" + screens[i].Bounds.Height +
                              ",\"primary\":" + (screens[i].Primary ? "true" : "false") + "}");
                }
                sb.Append("]}");
                SendJson(sb.ToString());
            }
            else if (json.Contains("\"command\":\"INJECT_MOUSE\""))
            {
                InjectMouseFromJson(json);
            }
            else if (json.Contains("\"command\":\"INJECT_KEY\""))
            {
                InjectKeyFromJson(json);
            }
            else if (json.Contains("\"command\":\"CAPTURE_SCREEN\""))
            {
                CaptureScreenFrame(json);
            }
            else if (json.Contains("\"command\":\"SET_CURSOR_POS\""))
            {
                int x = ParseInt(json, "x", 0);
                int y = ParseInt(json, "y", 0);
                SetCursorPos(x, y);
            }
        }

        private static void InjectMouseFromJson(string json)
        {
            if (json.Contains("\"action\":\"move\""))
            {
                int dx = ParseInt(json, "dx", 0);
                int dy = ParseInt(json, "dy", 0);
                bool isAbsolute = json.Contains("\"absolute\":true");
                int x = ParseInt(json, "x", 0);
                int y = ParseInt(json, "y", 0);

                INPUT[] inputs = new INPUT[1];
                inputs[0].type = INPUT_MOUSE;

                if (isAbsolute)
                {
                    inputs[0].mi.dx = (int)((x * 65535) / _screenWidth);
                    inputs[0].mi.dy = (int)((y * 65535) / _screenHeight);
                    inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
                }
                else
                {
                    inputs[0].mi.dx = dx;
                    inputs[0].mi.dy = dy;
                    inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE;
                }

                SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            }
            else if (json.Contains("\"action\":\"button\""))
            {
                string btn = ParseString(json, "button");
                bool down = json.Contains("\"down\":true");

                uint flags = 0;
                uint data = 0;

                if (btn == "left") flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                else if (btn == "right") flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                else if (btn == "middle") flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;

                if (flags != 0)
                {
                    INPUT[] inputs = new INPUT[1];
                    inputs[0].type = INPUT_MOUSE;
                    inputs[0].mi.dwFlags = flags;
                    inputs[0].mi.mouseData = data;
                    SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
                }
            }
            else if (json.Contains("\"action\":\"wheel\""))
            {
                int delta = ParseInt(json, "delta", 0);
                bool horiz = json.Contains("\"horizontal\":true");

                INPUT[] inputs = new INPUT[1];
                inputs[0].type = INPUT_MOUSE;
                inputs[0].mi.dwFlags = horiz ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
                inputs[0].mi.mouseData = (uint)delta;
                SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            }
        }

        private static void InjectKeyFromJson(string json)
        {
            ushort vk = (ushort)ParseInt(json, "vk", 0);
            ushort scan = (ushort)ParseInt(json, "scan", 0);
            bool extended = json.Contains("\"extended\":true");
            bool down = json.Contains("\"down\":true");

            uint flags = KEYEVENTF_KEYDOWN;
            if (!down) flags |= KEYEVENTF_KEYUP;
            if (extended) flags |= KEYEVENTF_EXTENDEDKEY;

            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].ki.wVk = vk;
            inputs[0].ki.wScan = scan;
            inputs[0].ki.dwFlags = flags;

            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));

            if (down) _activeDownKeys.Add(vk);
            else _activeDownKeys.Remove(vk);
        }

        private static void ResetLocalHeldKeys()
        {
            if (_activeDownKeys.Count == 0) return;

            List<ushort> keysToRelease = new List<ushort>(_activeDownKeys);
            foreach (ushort vk in keysToRelease)
            {
                INPUT[] inputs = new INPUT[1];
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].ki.wVk = vk;
                inputs[0].ki.dwFlags = KEYEVENTF_KEYUP;
                SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            }
            _activeDownKeys.Clear();
        }

        private static void CaptureScreenFrame(string json)
        {
            int quality = ParseInt(json, "quality", 70);
            int maxW = ParseInt(json, "maxWidth", 1280);

            try
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                using (Bitmap bmp = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                    }

                    int targetW = bounds.Width;
                    int targetH = bounds.Height;
                    if (targetW > maxW)
                    {
                        targetH = (int)((float)targetH * maxW / targetW);
                        targetW = maxW;
                    }

                    using (Bitmap scaled = new Bitmap(bmp, new Size(targetW, targetH)))
                    using (MemoryStream ms = new MemoryStream())
                    {
                        ImageCodecInfo jpgEncoder = GetEncoder(ImageFormat.Jpeg);
                        System.Drawing.Imaging.Encoder myEncoder = System.Drawing.Imaging.Encoder.Quality;
                        EncoderParameters myEncoderParameters = new EncoderParameters(1);
                        myEncoderParameters.Param[0] = new EncoderParameter(myEncoder, (long)quality);

                        scaled.Save(ms, jpgEncoder, myEncoderParameters);
                        byte[] byteArr = ms.ToArray();
                        string b64 = Convert.ToBase64String(byteArr);

                        SendJson("{\"type\":\"SCREEN_FRAME\",\"width\":" + targetW + ",\"height\":" + targetH + ",\"data\":\"" + b64 + "\"}");
                    }
                }
            }
            catch (Exception ex)
            {
                SendJson("{\"type\":\"CAPTURE_ERROR\",\"message\":\"" + Escape(ex.Message) + "\"}");
            }
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid) return codec;
            }
            return null;
        }

        private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                MSLLHOOKSTRUCT hookStruct = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                int msg = (int)wParam;

                if (_remoteActive)
                {
                    // Suppress local movement and buttons; send to remote
                    if (msg == WM_MOUSEMOVE)
                    {
                        int dx = hookStruct.pt.x - _lastPoint.x;
                        int dy = hookStruct.pt.y - _lastPoint.y;
                        _lastPoint = hookStruct.pt;

                        if (dx != 0 || dy != 0)
                        {
                            SendJson("{\"type\":\"MOUSE_MOVE\",\"dx\":" + dx + ",\"dy\":" + dy + ",\"x\":" + hookStruct.pt.x + ",\"y\":" + hookStruct.pt.y + "}");
                            if (Math.Abs(hookStruct.pt.x - _lockX) > 250 || Math.Abs(hookStruct.pt.y - _lockY) > 250)
                            {
                                SetCursorPos(_lockX, _lockY);
                                _lastPoint.x = _lockX;
                                _lastPoint.y = _lockY;
                            }
                            return (IntPtr)1; // Block local movement
                        }
                    }
                    else if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ||
                             msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP ||
                             msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP)
                    {
                        string btn = "left";
                        bool down = (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN);
                        if (msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP) btn = "right";
                        else if (msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP) btn = "middle";

                        SendJson("{\"type\":\"MOUSE_BUTTON\",\"button\":\"" + btn + "\",\"down\":" + (down ? "true" : "false") + "}");
                        return (IntPtr)1; // Block local click
                    }
                    else if (msg == WM_MOUSEWHEEL)
                    {
                        short delta = (short)((hookStruct.mouseData >> 16) & 0xffff);
                        SendJson("{\"type\":\"MOUSE_WHEEL\",\"delta\":" + delta + ",\"horizontal\":false}");
                        return (IntPtr)1;
                    }
                    else if (msg == WM_MOUSEHWHEEL)
                    {
                        short delta = (short)((hookStruct.mouseData >> 16) & 0xffff);
                        SendJson("{\"type\":\"MOUSE_WHEEL\",\"delta\":" + delta + ",\"horizontal\":true}");
                        return (IntPtr)1;
                    }
                }
                else if (_edgeSwitchEnabled && msg == WM_MOUSEMOVE)
                {
                    // Detect edge crossing
                    string edge = null;
                    if (hookStruct.pt.x >= _screenWidth - _edgeThreshold) edge = "right";
                    else if (hookStruct.pt.x <= _edgeThreshold) edge = "left";
                    else if (hookStruct.pt.y <= _edgeThreshold) edge = "top";
                    else if (hookStruct.pt.y >= _screenHeight - _edgeThreshold) edge = "bottom";

                    if (edge != null)
                    {
                        SendJson("{\"type\":\"EDGE_HIT\",\"edge\":\"" + edge + "\",\"x\":" + hookStruct.pt.x + ",\"y\":" + hookStruct.pt.y + "}");
                    }
                }
            }

            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                KBDLLHOOKSTRUCT hookStruct = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                int msg = (int)wParam;
                bool isDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                bool isExtended = (hookStruct.flags & 1) != 0;

                // Emergency Breakout Key: Ctrl + Alt + Pause/Break (VK 0x13) or Ctrl + Alt + Escape (VK 0x1B) or Scroll Lock (VK 0x91)
                bool ctrlDown = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                bool altDown = (GetAsyncKeyState(0x12) & 0x8000) != 0;

                if (ctrlDown && altDown && (hookStruct.vkCode == 0x13 || hookStruct.vkCode == 0x1B))
                {
                    // Emergency release
                    _remoteActive = false;
                    ClipCursor(IntPtr.Zero);
                    ResetLocalHeldKeys();
                    SendJson("{\"type\":\"EMERGENCY_RELEASE\"}");
                    return (IntPtr)1;
                }

                if (_remoteActive)
                {
                    SendJson("{\"type\":\"KEY_EVENT\",\"vk\":" + hookStruct.vkCode + ",\"scan\":" + hookStruct.scanCode +
                             ",\"extended\":" + (isExtended ? "true" : "false") + ",\"down\":" + (isDown ? "true" : "false") + "}");
                    return (IntPtr)1; // Block local key injection
                }
            }

            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private static int ParseInt(string json, string key, int defaultVal)
        {
            try
            {
                string pattern = "\"" + key + "\":";
                int idx = json.IndexOf(pattern);
                if (idx < 0) return defaultVal;
                idx += pattern.Length;
                int end = idx;
                while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
                string val = json.Substring(idx, end - idx);
                return int.Parse(val);
            }
            catch { return defaultVal; }
        }

        private static string ParseString(string json, string key)
        {
            try
            {
                string pattern = "\"" + key + "\":\"";
                int idx = json.IndexOf(pattern);
                if (idx < 0) return "";
                idx += pattern.Length;
                int end = json.IndexOf("\"", idx);
                if (end < 0) return "";
                return json.Substring(idx, end - idx);
            }
            catch { return ""; }
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }
    }
}
