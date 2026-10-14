using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ButterFlow.Core;

namespace ButterFlow.Engine
{
    public sealed class RawInputCapture : IDisposable
    {
        private Thread _captureThread;
        private volatile bool _isRunning;
        private IntPtr _hwnd = IntPtr.Zero;
        private readonly LockFreeRingBuffer _ringBuffer;
        private uint _sequenceCounter = 0;
        private long _qpcFrequency;
        private IntPtr _pData = IntPtr.Zero;
        private const int RAWINPUT_BUFFER_SIZE = 512;

        public event Action<int, int> OnMotionDelta;
        public bool IsRunning { get { return _isRunning; } }

        public RawInputCapture(LockFreeRingBuffer ringBuffer = null)
        {
            _ringBuffer = ringBuffer;
            _pData = Marshal.AllocHGlobal(RAWINPUT_BUFFER_SIZE);
            NativeMethods.QueryPerformanceFrequency(out _qpcFrequency);
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            AutoResetEvent readySignal = new AutoResetEvent(false);

            _captureThread = new Thread(() => RunMessageLoop(readySignal))
            {
                IsBackground = true,
                Name = "RawInputCaptureThread",
                Priority = ThreadPriority.Highest
            };
            _captureThread.Start();

            readySignal.WaitOne(2000);
        }

        private void RunMessageLoop(AutoResetEvent readySignal)
        {
            // 1. Elevate thread to Multimedia Class Scheduler Service (MMCSS)
            try
            {
                int taskIndex = 0;
                NativeMethods.AvSetMmThreadCharacteristics("Games", ref taskIndex);
            }
            catch { }

            // 2. Set sub-millisecond timer resolution
            NativeMethods.TimeBeginPeriod(1);

            // 3. Create hidden message window
            MessageWindow window = new MessageWindow(this);
            _hwnd = window.Handle;

            // 4. Register Raw Input Devices
            RegisterDevices(_hwnd);

            readySignal.Set();

            // 5. Standard high-speed Win32 message pump
            Application.Run();
        }

        private void RegisterDevices(IntPtr hwnd)
        {
            NativeMethods.RAWINPUTDEVICE[] rid = new NativeMethods.RAWINPUTDEVICE[2];

            // Mouse
            rid[0].usUsagePage = 0x01; // Generic desktop controls
            rid[0].usUsage = 0x02;     // Mouse
            rid[0].dwFlags = NativeMethods.RIDEV_INPUTSINK;
            rid[0].hwndTarget = hwnd;

            // Keyboard
            rid[1].usUsagePage = 0x01;
            rid[1].usUsage = 0x06;     // Keyboard
            rid[1].dwFlags = NativeMethods.RIDEV_INPUTSINK;
            rid[1].hwndTarget = hwnd;

            bool success = NativeMethods.RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICE)));
            if (!success)
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine("RawInput Registration failed with error: " + err);
            }
        }

        internal void HandleRawInput(IntPtr hRawInput)
        {
            uint dwSize = (uint)RAWINPUT_BUFFER_SIZE;
            if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, _pData, ref dwSize, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTHEADER))) == unchecked((uint)-1) || dwSize == 0)
            {
                return;
            }

            NativeMethods.RAWINPUT raw = (NativeMethods.RAWINPUT)Marshal.PtrToStructure(_pData, typeof(NativeMethods.RAWINPUT));

            long nowQpc;
            NativeMethods.QueryPerformanceCounter(out nowQpc);

            if (raw.header.dwType == NativeMethods.RIM_TYPEMOUSE)
            {
                int dx = raw.mouse.lLastX;
                int dy = raw.mouse.lLastY;
                short wheel = 0;

                if ((raw.mouse.usButtonFlags & NativeMethods.RI_MOUSE_WHEEL) != 0)
                {
                    wheel = (short)raw.mouse.usButtonData;
                }

                byte buttonState = 0;
                if ((raw.mouse.usButtonFlags & NativeMethods.RI_MOUSE_LEFT_BUTTON_DOWN) != 0) buttonState |= 1;
                if ((raw.mouse.usButtonFlags & NativeMethods.RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) buttonState |= 2;
                if ((raw.mouse.usButtonFlags & NativeMethods.RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) buttonState |= 4;
                if ((raw.mouse.usButtonFlags & NativeMethods.RI_MOUSE_BUTTON_4_DOWN) != 0) buttonState |= 8;
                if ((raw.mouse.usButtonFlags & NativeMethods.RI_MOUSE_BUTTON_5_DOWN) != 0) buttonState |= 16;

                if (dx != 0 || dy != 0 || wheel != 0 || raw.mouse.usButtonFlags != 0)
                {
                    if (_ringBuffer != null)
                    {
                        RawInputItem item = new RawInputItem
                        {
                            Sequence = ++_sequenceCounter,
                            QpcTimestamp = nowQpc,
                            DeltaX = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, dx)),
                            DeltaY = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, dy)),
                            WheelDelta = wheel,
                            ButtonsState = buttonState,
                            EventType = 0
                        };
                        _ringBuffer.TryEnqueue(ref item);
                    }

                    if (OnMotionDelta != null && (dx != 0 || dy != 0))
                    {
                        OnMotionDelta(dx, dy);
                    }
                }
            }
            else if (raw.header.dwType == NativeMethods.RIM_TYPEKEYBOARD)
            {
                if (_ringBuffer != null)
                {
                    ushort vk = raw.keyboard.VKey;
                    ushort scan = raw.keyboard.MakeCode;
                    uint flags = raw.keyboard.Flags;
                    bool isDown = (flags & NativeMethods.RI_KEY_BREAK) == 0;

                    RawInputItem item = new RawInputItem
                    {
                        Sequence = ++_sequenceCounter,
                        QpcTimestamp = nowQpc,
                        EventType = 2, // Key
                        KeyVk = vk,
                        KeyScan = scan,
                        KeyFlags = flags,
                        KeyIsDown = isDown
                    };

                    _ringBuffer.TryEnqueue(ref item);
                }
            }
        }

        public void Stop()
        {
            _isRunning = false;
            NativeMethods.TimeEndPeriod(1);

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.PostMessage(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _hwnd = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Stop();
            if (_pData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pData);
                _pData = IntPtr.Zero;
            }
        }

        private sealed class MessageWindow : Form
        {
            private readonly RawInputCapture _parent;

            public MessageWindow(RawInputCapture parent)
            {
                _parent = parent;
                this.FormBorderStyle = FormBorderStyle.None;
                this.ShowInTaskbar = false;
                this.StartPosition = FormStartPosition.Manual;
                this.Location = new System.Drawing.Point(-2000, -2000);
                this.Size = new System.Drawing.Size(1, 1);
                this.CreateControl();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == NativeMethods.WM_INPUT)
                {
                    _parent.HandleRawInput(m.LParam);
                }
                base.WndProc(ref m);
            }
        }
    }
}
