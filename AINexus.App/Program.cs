using System;
using System.Diagnostics;
using System.Windows;

namespace AINexus.App
{
    public class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application();
            app.Run(new MainWindow());
        }
    }
}
