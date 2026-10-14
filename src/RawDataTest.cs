using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class RawInputDataTest : Form {
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll")]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    public RawInputDataTest() {
        RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[1];
        rid[0].usUsagePage = 0x01; // Generic desktop
        rid[0].usUsage = 0x02;     // Mouse
        rid[0].dwFlags = 0x00000100; // RIDEV_INPUTSINK
        rid[0].hwndTarget = this.Handle;
        RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x00FF) { // WM_INPUT
            uint size = 0;
            GetRawInputData(m.LParam, 0x10000003, IntPtr.Zero, ref size, (uint)(IntPtr.Size == 8 ? 24 : 16));
            if (size > 0) {
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                GetRawInputData(m.LParam, 0x10000003, buffer, ref size, (uint)(IntPtr.Size == 8 ? 24 : 16));
                int headerSize = IntPtr.Size == 8 ? 24 : 16;
                // In RAWINPUTMOUSE: usFlags:2, union:2, ulButtons:4, ulRawButtons:4, lLastX:4, lLastY:4
                int lastX = Marshal.ReadInt32(buffer, headerSize + 12);
                int lastY = Marshal.ReadInt32(buffer, headerSize + 16);
                Marshal.FreeHGlobal(buffer);
                Console.WriteLine("Raw Delta: " + lastX + ", " + lastY);
                Application.Exit();
            }
        }
        base.WndProc(ref m);
    }

    public static void Main() {
        Console.WriteLine("Pointer Size: " + IntPtr.Size);
    }
}
