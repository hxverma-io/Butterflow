using System;
using System.Drawing;
using System.Windows.Forms;

public class TestForm : Form {
    public TestForm() {
        this.Text = "ButterFlow Test";
        this.Size = new Size(400, 300);
        Label lbl = new Label() {
            Text = "WinForms is working!",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 12, FontStyle.Bold)
        };
        this.Controls.Add(lbl);
    }

    [STAThread]
    public static void Main() {
        Console.WriteLine("Virtual Screen: " + SystemInformation.VirtualScreen.Width + "x" + SystemInformation.VirtualScreen.Height);
    }
}
