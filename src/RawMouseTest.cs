using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class RawInputMouseForm : Form {
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll")]
    public static extern bool RegisterRawInputDevices([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    private int _count = 0;

    public RawInputMouseForm() {
        RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[1];
        rid[0].usUsagePage = 0x01; // Generic Desktop
        rid[0].usUsage = 0x02;     // Mouse
        rid[0].dwFlags = 0x00000100; // RIDEV_INPUTSINK (Receives input even when in background!)
        rid[0].hwndTarget = this.Handle;

        bool reg = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        Console.WriteLine("Registered Raw Input: " + reg);
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x00FF) { // WM_INPUT
            uint size = 0;
            int headerSize = IntPtr.Size == 8 ? 24 : 16;
            GetRawInputData(m.LParam, 0x10000003, IntPtr.Zero, ref size, (uint)headerSize);
            if (size > 0) {
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try {
                    if (GetRawInputData(m.LParam, 0x10000003, buffer, ref size, (uint)headerSize) == size) {
                        // In RAWINPUT (64-bit):
                        // header: 24 bytes
                        // RAWINPUTMOUSE:
                        // usFlags: 2 bytes (offset 24)
                        // ulButtons: 4 bytes (offset 28)
                        // ulRawButtons: 4 bytes (offset 32)
                        // lLastX: 4 bytes (offset 36)
                        // lLastY: 4 bytes (offset 40)
                        int lastX = Marshal.ReadInt32(buffer, headerSize + 12);
                        int lastY = Marshal.ReadInt32(buffer, headerSize + 16);

                        if (lastX != 0 || lastY != 0) {
                            _count++;
                            if (_count % 20 == 0) {
                                Console.WriteLine("Delta #" + _count + ": dX=" + lastX + ", dY=" + lastY);
                            }
                            if (_count >= 60) {
                                Application.Exit();
                            }
                        }
                    }
                } finally {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        base.WndProc(ref m);
    }

    public static void Main() {
        Application.Run(new RawInputMouseForm());
    }
}
