using System;
using System.Diagnostics;
using System.Windows;

namespace AINexus.App
{
    public class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (ToolArchiveInspector.IsWorker(args)) return ToolArchiveInspector.RunWorkerAsync().GetAwaiter().GetResult();
            var app = new Application();
            return app.Run(new MainWindow());
        }
    }
}
