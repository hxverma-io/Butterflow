using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ButterFlow.Engine;
using ButterFlow.UI;

namespace ButterFlow
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        private static void Main(string[] args)
        {
            // Check for Benchmark Mode CLI flag
            if (args != null && args.Length > 0 && (args[0] == "--benchmark" || args[0] == "-b" || args[0] == "/benchmark"))
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                Console.WriteLine();
                Console.WriteLine("===============================================================");
                Console.WriteLine("    BUTTERFLOW REAL-TIME INPUT ENGINE — 1000 Hz STRESS TEST    ");
                Console.WriteLine("===============================================================");
                Console.WriteLine("Running 60-Second 1000 Hz simulation with 3% packet loss and jitter...");
                Console.WriteLine();

                BenchmarkResult res = BenchmarkEngine.RunSimulation(60, 3, pct =>
                {
                    if (pct % 20 == 0) Console.Write(string.Format("[{0}%] ", pct));
                });

                Console.WriteLine("\n");
                Console.WriteLine(res.SummaryText);
                File.WriteAllText("benchmark_results.txt", res.SummaryText);
                Console.WriteLine("\nResults written to benchmark_results.txt");
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                File.WriteAllText("crash.log", "Unhandled: " + e.ExceptionObject);
            };
            Application.ThreadException += (s, e) =>
            {
                File.WriteAllText("crash.log", "ThreadException: " + e.Exception);
            };

            try
            {
                // Request 1ms kernel timer resolution for 1000Hz gaming-grade fluidity
                try { ButterFlow.Core.NativeMethods.TimeBeginPeriod(1); } catch { }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                File.WriteAllText("crash.log", ex.ToString());
            }
            finally
            {
                try { ButterFlow.Core.NativeMethods.TimeEndPeriod(1); } catch { }
            }
        }
    }
}
