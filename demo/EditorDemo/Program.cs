using System;
using System.Windows;

namespace EditorDemo
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new MainWindow());
        }
    }
}
