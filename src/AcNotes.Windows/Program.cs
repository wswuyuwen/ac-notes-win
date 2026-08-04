using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace AcNotes.Windows
{
    internal static class Program
    {
        private static Mutex? _singleInstanceMutex;

        [STAThread]
        private static void Main(string[] args)
        {
            // 单实例互斥：防止常驻+验证实例并发读写同一份 notes.json/注册表（已实测并发会互相覆盖/崩溃）
            _singleInstanceMutex = new Mutex(true, @"Local\AcNotes.Windows", out bool createdNew);
            if (!createdNew)
            {
                return; // 已有实例在运行，直接退出
            }

            // WinExe 无控制台，把 stdout 重定向到文件便于诊断
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AcNotes");
                Directory.CreateDirectory(logDir);
                Console.SetOut(new StreamWriter(Path.Combine(logDir, "console.log"), true) { AutoFlush = true });
                // 未处理异常兜底：写 crash.log 便于排查（曾因选区越界静默崩溃无日志）
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                {
                    try
                    {
                        File.AppendAllText(
                            Path.Combine(logDir, "crash.log"),
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.ExceptionObject}\n\n");
                    }
                    catch { }
                };
            }
            catch { }
            bool selfTest = Array.Exists(args, a => a == "--selftest");
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow(selfTest);
            app.Run(window);
        }
    }
}
