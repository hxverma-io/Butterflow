using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class RawInputTest : Form {
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll")]
    public static extern bool RegisterRawInputDevices([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    public RawInputTest() {
        RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[1];
        rid[0].usUsagePage = 0x01; // HID_USAGE_PAGE_GENERIC
        rid[0].usUsage = 0x02;     // HID_USAGE_GENERIC_MOUSE
        rid[0].dwFlags = 0x00000100; // RIDEV_INPUTSINK
        rid[0].hwndTarget = this.Handle;

        bool success = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        Console.WriteLine("RegisterRawInputDevices success: " + success);
    }

    public static void Main() {
        RawInputTest t = new RawInputTest();
    }
}
