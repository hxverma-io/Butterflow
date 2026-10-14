using System;
using System.Windows.Forms;

public class ScreenTest {
    public static void Main() {
        foreach (var s in Screen.AllScreens) {
            Console.WriteLine("Screen: " + s.DeviceName + " Bounds: " + s.Bounds + " WorkingArea: " + s.WorkingArea);
        }
        Console.WriteLine("VirtualScreen: " + SystemInformation.VirtualScreen);
    }
}
