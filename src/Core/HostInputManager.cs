using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ButterFlow.Engine;
using ButterFlow.Network;

namespace ButterFlow.Core
{
    public class HostInputManager : IDisposable
    {
        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private NativeMethods.HookProc _mouseProc;
        private NativeMethods.HookProc _keyboardProc;
        private RawInputCapture _rawInput;

        // Multi-Machine Mesh Senders (Right, Left, Top, Bottom)
        private readonly Dictionary<ScreenEdge, InputSender> _peers = new Dictionary<ScreenEdge, InputSender>();
        private InputSender _activeSender = null;
        private ScreenEdge _activeEdge = ScreenEdge.Right;
        private bool _isControllingClient;
        private bool _isActive;

        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }
        public float Sensitivity { get; set; } // 1.0f = normal, 1.2f = snappy butter
        public int PushBarrierThreshold { get; set; } // Edge push threshold for crossover

        private int _centerX;
        private int _centerY;
        private int _edgePushCount = 0;
        private int _returnPushCount = 0;
        private DateTime _controlEnterTime = DateTime.MinValue;
        private DateTime _lastReleaseTime = DateTime.MinValue;

        // Virtual coordinates tracked while controlling client (0.0 to 1.0)
        private float _virtualNormX;
        private float _virtualNormY;

        // Sub-pixel accumulator for 1000Hz butter-smooth continuous motion
        private float _subPixelX = 0f;
        private float _subPixelY = 0f;

        // Cross-Screen Drag & Drop tracking
        private bool _isDraggingCrossScreen = false;
        private string _draggedFilePath = null;

        public bool IsControllingClient { get { return _isControllingClient; } }
        public ScreenEdge ActiveEdge { get { return _activeEdge; } }
        public bool IsReverseDragActive { get; set; }

        public event Action<bool, ScreenEdge> OnControlStateChanged;
        public event Action OnIncomingReverseDragDrop;
        public event Action<int, int> OnReverseDragMoved;
        public event Action<string> OnLogMessage;

        public HostInputManager(InputSender defaultSender, int screenW, int screenH)
        {
            ScreenWidth = screenW;
            ScreenHeight = screenH;
            _centerX = screenW / 2;
            _centerY = screenH / 2;

            Sensitivity = 1.15f;
            PushBarrierThreshold = 1; // Instant snappy crossover

            if (defaultSender != null)
            {
                SetPeer(ScreenEdge.Right, defaultSender);
            }

            _mouseProc = MouseHookCallback;
            _keyboardProc = KeyboardHookCallback;
        }

        public void SetPeer(ScreenEdge edge, InputSender sender)
        {
            if (sender == null) return;
            _peers[edge] = sender;
            sender.OnSwitchBack += normY => HandleSwitchBack(edge, normY);
        }

        public void RemovePeer(ScreenEdge edge)
        {
            if (_peers.ContainsKey(edge))
            {
                if (_isControllingClient && _activeEdge == edge)
                {
                    ReleaseControlBackToHost(0.5f);
                }
                _peers.Remove(edge);
            }
        }

        public ScreenEdge ClientPosition
        {
            get { return _activeEdge; }
            set
            {
                if (_peers.ContainsKey(_activeEdge) && _activeEdge != value)
                {
                    InputSender s = _peers[_activeEdge];
                    _peers.Remove(_activeEdge);
                    _peers[value] = s;
                }
                _activeEdge = value;
            }
        }

        public void Start()
        {
            if (_isActive) return;
            _isActive = true;

            // 1. Force Windows NT Kernel to 0.5ms high-resolution timer!
            NativeMethods.TimeBeginPeriod(1);
            try
            {
                uint curRes;
                NativeMethods.NtSetTimerResolution(5000, true, out curRes); // 5000 * 100ns = 0.5ms!
            }
            catch { }

            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                IntPtr moduleHandle = NativeMethods.GetModuleHandle(curModule.ModuleName);
                _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
                _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
            }

            // 2. Initialize 1000Hz Raw Input Capture directly from mouse optical sensor (pure hardware feel)
            try
            {
                _rawInput = new RawInputCapture(null);
                _rawInput.OnMotionDelta += ProcessRawInput;
                _rawInput.Start();
            }
            catch (Exception ex)
            {
                Log("RawInput init warning: " + ex.Message);
            }

            Log("Host Input Engine active. [0.5ms Kernel Timer Active • 1000Hz Raw Input Ready]");
        }

        public void Stop()
        {
            _isActive = false;
            ReleaseControlBackToHost(0.5f);

            NativeMethods.TimeEndPeriod(1);

            if (_rawInput != null)
            {
                try { _rawInput.Dispose(); } catch { }
                _rawInput = null;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            if (_keyboardHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            Log("Host Input Hooks uninstalled");
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isActive)
            {
                NativeMethods.MSLLHOOKSTRUCT hookStruct = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));

                // Discard synthetic repositioning events injected by SetCursorPos
                if ((hookStruct.flags & 1) != 0)
                {
                    return (IntPtr)1;
                }

                int msg = wParam.ToInt32();

                if (!_isControllingClient)
                {
                    // User is working on Host screen
                    if (msg == NativeMethods.WM_MOUSEMOVE)
                    {
                        if (IsReverseDragActive && OnReverseDragMoved != null)
                        {
                            OnReverseDragMoved(hookStruct.pt.X, hookStruct.pt.Y);
                        }
                        CheckMultiEdgeCrossover(hookStruct.pt.X, hookStruct.pt.Y);
                    }
                    else if (msg == NativeMethods.WM_LBUTTONDOWN)
                    {
                        // Clean: no heavy COM interop on normal host clicks
                    }
                    else if (msg == NativeMethods.WM_LBUTTONUP)
                    {
                        if (IsReverseDragActive)
                        {
                            IsReverseDragActive = false;
                            Log("📂 Reverse Dragged file dropped onto Host screen!");
                            if (OnIncomingReverseDragDrop != null)
                            {
                                OnIncomingReverseDragDrop();
                            }
                        }
                    }
                }
                else
                {
                    // User is actively controlling a Client screen in the Mesh!
                    if (_activeSender != null && _activeSender.IsConnected)
                    {
                        switch (msg)
                        {
                            case NativeMethods.WM_MOUSEMOVE:
                                return (IntPtr)1; // Suppress locally on Host in <0.5us! 1000Hz RawInput streams the deltas directly!

                            case NativeMethods.WM_LBUTTONDOWN:
                                _activeSender.SendMouseButton(MouseButtonType.Left, ButtonState.Down);
                                return (IntPtr)1;
                            case NativeMethods.WM_LBUTTONUP:
                                if (_isDraggingCrossScreen)
                                {
                                    _isDraggingCrossScreen = false;
                                    if (_activeSender != null)
                                    {
                                        _activeSender.SendDragDrop(_virtualNormX, _virtualNormY);
                                        Log("📂 Cross-Screen File Dropped onto " + _activeEdge + " screen!");
                                    }
                                }
                                _activeSender.SendMouseButton(MouseButtonType.Left, ButtonState.Up);
                                return (IntPtr)1;

                            case NativeMethods.WM_RBUTTONDOWN:
                                _activeSender.SendMouseButton(MouseButtonType.Right, ButtonState.Down);
                                return (IntPtr)1;
                            case NativeMethods.WM_RBUTTONUP:
                                _activeSender.SendMouseButton(MouseButtonType.Right, ButtonState.Up);
                                return (IntPtr)1;

                            case NativeMethods.WM_MBUTTONDOWN:
                                _activeSender.SendMouseButton(MouseButtonType.Middle, ButtonState.Down);
                                return (IntPtr)1;
                            case NativeMethods.WM_MBUTTONUP:
                                _activeSender.SendMouseButton(MouseButtonType.Middle, ButtonState.Up);
                                return (IntPtr)1;

                            case NativeMethods.WM_MOUSEWHEEL:
                                short wheelDelta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                                _activeSender.SendMouseWheel(wheelDelta);
                                return (IntPtr)1;
                        }
                    }
                    else
                    {
                        // Active client disconnected unexpectedly
                        ReleaseControlBackToHost(0.5f);
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isActive)
            {
                NativeMethods.KBDLLHOOKSTRUCT kb = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                int msg = wParam.ToInt32();
                bool isDown = (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN);

                bool isCtrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
                bool isAlt = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;

                // 1. Hotkey: Emergency Escape (Ctrl + Alt + Escape)
                if (kb.vkCode == NativeMethods.VK_ESCAPE && isDown && isCtrl && isAlt)
                {
                    Log("Emergency Escape triggered!");
                    ReleaseControlBackToHost(0.5f);
                    return (IntPtr)1;
                }

                // 2. Hotkey: Toggle Switch Control (Ctrl + Alt + S or F12)
                if (((kb.vkCode == 'S' && isCtrl && isAlt) || kb.vkCode == (uint)Keys.F12) && isDown)
                {
                    ToggleControl();
                    return (IntPtr)1;
                }

                // If currently controlling client, transmit keys and suppress locally
                if (_isControllingClient && _activeSender != null && _activeSender.IsConnected)
                {
                    _activeSender.SendKeyEvent((ushort)kb.vkCode, (ushort)kb.scanCode, kb.flags, isDown);
                    return (IntPtr)1; // Suppress on Host
                }
            }

            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        public void ToggleControl()
        {
            if (_isControllingClient)
            {
                ReleaseControlBackToHost(0.5f);
            }
            else
            {
                // Try switching to the primary peer or first connected peer
                foreach (var kvp in _peers)
                {
                    if (kvp.Value != null && kvp.Value.IsConnected)
                    {
                        SwitchControlToClient(kvp.Key, ScreenWidth / 2, ScreenHeight / 2);
                        return;
                    }
                }
                Log("Cannot switch: No connected client machine in Mesh");
            }
        }

        private void CheckMultiEdgeCrossover(int x, int y)
        {
            if (_isControllingClient) return;

            // Debounce: Prevent instant re-crossing within 500ms of releasing back to host
            if ((DateTime.Now - _lastReleaseTime).TotalMilliseconds < 500)
            {
                return;
            }

            ScreenEdge? targetEdge = null;

            // 1. Check Right Screen
            if (x >= ScreenWidth - 2 && _peers.ContainsKey(ScreenEdge.Right) && _peers[ScreenEdge.Right].IsConnected)
            {
                targetEdge = ScreenEdge.Right;
            }
            // 2. Check Left Screen
            else if (x <= 1 && _peers.ContainsKey(ScreenEdge.Left) && _peers[ScreenEdge.Left].IsConnected)
            {
                targetEdge = ScreenEdge.Left;
            }
            // 3. Check Top Screen
            else if (y <= 1 && _peers.ContainsKey(ScreenEdge.Top) && _peers[ScreenEdge.Top].IsConnected)
            {
                targetEdge = ScreenEdge.Top;
            }
            // 4. Check Bottom Screen
            else if (y >= ScreenHeight - 2 && _peers.ContainsKey(ScreenEdge.Bottom) && _peers[ScreenEdge.Bottom].IsConnected)
            {
                targetEdge = ScreenEdge.Bottom;
            }

            if (targetEdge.HasValue)
            {
                _edgePushCount++;
                int threshold = PushBarrierThreshold > 0 ? PushBarrierThreshold : 1;
                if (_edgePushCount >= threshold)
                {
                    _edgePushCount = 0;
                    SwitchControlToClient(targetEdge.Value, x, y);
                }
            }
            else
            {
                _edgePushCount = 0;
            }
        }

        private void SwitchControlToClient(ScreenEdge edge, int enterX, int enterY)
        {
            if (_isControllingClient || !_peers.ContainsKey(edge) || !_peers[edge].IsConnected) return;

            _activeEdge = edge;
            _activeSender = _peers[edge];
            _isControllingClient = true;
            _controlEnterTime = DateTime.Now;
            _returnPushCount = 0;

            // Set initial position safely inside client screen
            _virtualNormY = (float)enterY / (float)ScreenHeight;
            if (_virtualNormY < 0.05f) _virtualNormY = 0.05f;
            if (_virtualNormY > 0.95f) _virtualNormY = 0.95f;

            switch (edge)
            {
                case ScreenEdge.Right:
                    _virtualNormX = 0.03f; // Enter from left edge of right PC
                    break;
                case ScreenEdge.Left:
                    _virtualNormX = 0.97f; // Enter from right edge of left PC
                    break;
                case ScreenEdge.Top:
                    _virtualNormX = (float)enterX / (float)ScreenWidth;
                    _virtualNormY = 0.97f; // Enter from bottom edge of top PC
                    break;
                case ScreenEdge.Bottom:
                    _virtualNormX = (float)enterX / (float)ScreenWidth;
                    _virtualNormY = 0.03f; // Enter from top edge of bottom PC
                    break;
            }

            // Dynamic 800x800 Wide Centered Boundary:
            // Fast flicking and rapid mouse sweeps NEVER hit monitor edges, taskbars, or OS gesture zones!
            try
            {
                _centerX = ScreenWidth / 2;
                _centerY = ScreenHeight / 2;
                NativeMethods.SetCursorPos(_centerX, _centerY);
                NativeMethods.RECT clipRect = new NativeMethods.RECT(_centerX - 400, _centerY - 400, _centerX + 400, _centerY + 400);
                NativeMethods.ClipCursor(ref clipRect);
            }
            catch { }

            _subPixelX = 0f;
            _subPixelY = 0f;

            // Send initial entry position (delta = 0 signals initial placement)
            _activeSender.SendMouseMove(_virtualNormX, _virtualNormY, 0, 0);

            // Cross-Screen Drag & Drop Detection: Check if user is holding Left Click while crossing edge
            bool isLButtonHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
            if (isLButtonHeld)
            {
                // Query dragged file asynchronously in background so screen crossover has 0.0ms delay!
                ThreadPool.QueueUserWorkItem(s =>
                {
                    try
                    {
                        var files = ShellHelper.GetSelectedExplorerFiles();
                        if (files != null && files.Count > 0 && _isControllingClient)
                        {
                            string draggedPath = files[0];
                            if (System.IO.File.Exists(draggedPath))
                            {
                                _isDraggingCrossScreen = true;
                                _draggedFilePath = draggedPath;
                                System.IO.FileInfo fi = new System.IO.FileInfo(draggedPath);

                                Log(string.Format("🚀 Cross-Screen Drag: {0} ({1:F1} MB) -> Beaming to {2}!", fi.Name, fi.Length / (1024.0 * 1024.0), edge));
                                if (_activeSender != null && _activeSender.IsConnected)
                                {
                                    _activeSender.SendDragStart(fi.Name, fi.Length);
                                    _activeSender.SendFile(draggedPath, null);
                                }
                            }
                        }
                    }
                    catch { }
                });
            }

            Log(string.Format("⚡ Control switched to {0} Machine ({1})!", edge, _activeSender.TargetIp));
            if (OnControlStateChanged != null) OnControlStateChanged(true, edge);
        }

        public void ProcessRawInput(int deltaX, int deltaY)
        {
            if (!_isControllingClient || !_isActive || _activeSender == null || !_activeSender.IsConnected) return;
            if (deltaX == 0 && deltaY == 0) return;

            // Dynamic center-keeping: ensure host cursor remains safely centered inside the 800x800 zone
            NativeMethods.POINT curPt;
            if (NativeMethods.GetCursorPos(out curPt))
            {
                if (Math.Abs(curPt.X - _centerX) > 300 || Math.Abs(curPt.Y - _centerY) > 300)
                {
                    NativeMethods.SetCursorPos(_centerX, _centerY);
                }
            }

            // Direct 1000Hz optical sensor ballistics (pure hardware 1:1 feel)
            float dist = (float)Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            float factor = Sensitivity;
            if (dist <= 2.2f)
            {
                factor = 1.0f; // Pure 1:1 hardware precision for desktop UI & text
            }
            else if (dist < 7.0f)
            {
                float t = (dist - 2.2f) / 4.8f;
                factor = 1.0f + (Sensitivity - 1.0f) * (t * t * (3f - 2f * t)); // Smooth Hermite curve
            }

            float totalX = (deltaX * factor) + _subPixelX;
            float totalY = (deltaY * factor) + _subPixelY;

            int sendDeltaX = (int)totalX;
            int sendDeltaY = (int)totalY;

            _subPixelX = totalX - sendDeltaX;
            _subPixelY = totalY - sendDeltaY;

            if (sendDeltaX == 0 && sendDeltaY == 0) return;

            _virtualNormX += (float)sendDeltaX / (float)ScreenWidth;
            _virtualNormY += (float)sendDeltaY / (float)ScreenHeight;
            _virtualNormX = Math.Max(0.0f, Math.Min(1.0f, _virtualNormX));
            _virtualNormY = Math.Max(0.0f, Math.Min(1.0f, _virtualNormY));

            _activeSender.SendMouseMove(_virtualNormX, _virtualNormY, sendDeltaX, sendDeltaY);

            if (_isDraggingCrossScreen)
            {
                _activeSender.SendDragMotion(_virtualNormX, _virtualNormY);
            }

            CheckReturnToHostEdge(sendDeltaX, sendDeltaY);
        }

        private void CheckReturnToHostEdge(int deltaX, int deltaY)
        {
            // Debounce: Must be actively on client for at least 600ms before allowing return
            if ((DateTime.Now - _controlEnterTime).TotalMilliseconds < 600)
            {
                return;
            }

            bool pushingTowardsHost = false;

            switch (_activeEdge)
            {
                case ScreenEdge.Right:
                    // Client is on Right, Host is on Left. Cursor at left boundary & pushing left!
                    if (_virtualNormX <= 0.003f && deltaX < 0) pushingTowardsHost = true;
                    break;

                case ScreenEdge.Left:
                    // Client is on Left, Host is on Right. Cursor at right boundary & pushing right!
                    if (_virtualNormX >= 0.997f && deltaX > 0) pushingTowardsHost = true;
                    break;

                case ScreenEdge.Top:
                    // Client is Above, Host is Below. Cursor at bottom boundary & pushing down!
                    if (_virtualNormY >= 0.997f && deltaY > 0) pushingTowardsHost = true;
                    break;

                case ScreenEdge.Bottom:
                    // Client is Below, Host is Above. Cursor at top boundary & pushing up!
                    if (_virtualNormY <= 0.003f && deltaY < 0) pushingTowardsHost = true;
                    break;
            }

            if (pushingTowardsHost)
            {
                _returnPushCount++;
                // Must actively push into border for 5 consecutive frames to trigger return
                if (_returnPushCount >= 5)
                {
                    _returnPushCount = 0;

                    // Reverse Drag & Drop: Check if user is holding physical Left Mouse Button on Host!
                    bool isLButtonHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
                    if (isLButtonHeld)
                    {
                        IsReverseDragActive = true;
                        Log("🚀 Reverse Drag detected: Client -> Host! Requesting file from Client...");
                        if (_activeSender != null && _activeSender.IsConnected)
                        {
                            _activeSender.SendRequestReverseDrag();
                        }
                    }
                    else
                    {
                        // Normal release back to Host: ensure client button is released
                        if (_activeSender != null && _activeSender.IsConnected)
                        {
                            _activeSender.SendMouseButton(MouseButtonType.Left, ButtonState.Up);
                        }
                    }

                    ReleaseControlBackToHost(_virtualNormY);
                }
            }
            else
            {
                _returnPushCount = 0;
            }
        }

        private void HandleSwitchBack(ScreenEdge edge, float returnNormY)
        {
            if (_isControllingClient && _activeEdge == edge)
            {
                ReleaseControlBackToHost(returnNormY);
            }
        }

        public void ReleaseControlBackToHost(float returnNormY)
        {
            if (!_isControllingClient) return;
            _isControllingClient = false;
            _lastReleaseTime = DateTime.Now;

            if (_isDraggingCrossScreen)
            {
                _isDraggingCrossScreen = false;
                if (_activeSender != null)
                {
                    _activeSender.SendDragCancel();
                }
            }

            try { NativeMethods.ClipCursor(IntPtr.Zero); } catch { }

            int restoreX = _centerX;
            int restoreY = (int)(returnNormY * ScreenHeight);
            if (restoreY < 30) restoreY = 30;
            if (restoreY > ScreenHeight - 30) restoreY = ScreenHeight - 30;

            switch (_activeEdge)
            {
                case ScreenEdge.Right:
                    restoreX = ScreenWidth - 60;
                    break;
                case ScreenEdge.Left:
                    restoreX = 60;
                    break;
                case ScreenEdge.Top:
                    restoreX = ScreenWidth / 2;
                    restoreY = 60;
                    break;
                case ScreenEdge.Bottom:
                    restoreX = ScreenWidth / 2;
                    restoreY = ScreenHeight - 60;
                    break;
            }

            NativeMethods.SetCursorPos(restoreX, restoreY);

            Log("🏠 Control returned to Host");
            if (OnControlStateChanged != null) OnControlStateChanged(false, _activeEdge);
        }

        private void Log(string msg)
        {
            if (OnLogMessage != null) OnLogMessage(msg);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
