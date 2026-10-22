using System;
using System.Diagnostics;
using System.Windows;

namespace AINexus.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) => await ScanHardwareAsync();
        }

        private async System.Threading.Tasks.Task ScanHardwareAsync()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string psScript = "(Get-CimInstance Win32_Processor).Name + '|' + [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)";
                    var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{psScript}\"")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        string output = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit();
                        Dispatcher.Invoke(() => {
                            var parts = output.Split('|');
                            if (parts.Length == 2)
                            {
                                this.Title = $"Nexus Portal - {parts[0].Trim()} | {parts[1].Trim()} GB RAM";
                            }
                        });
                    }
                }
                catch { }
            });
        }
    }
}
