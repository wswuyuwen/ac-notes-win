using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;

namespace EditorDemo
{
    /// <summary>
    /// WebView2 + Tiptap 集成验证 demo：
    /// 1) WebView2 在 AllowsTransparency 分层窗口中的合成兼容性（已知风险：子 HWND 黑块）
    /// 2) Tiptap WYSIWYG 真实效果（粗体/删除线/任务勾选框/标题，无 Markdown 标记）
    /// </summary>
    public sealed class MainWindow : Window
    {
        private const double WinWidth = 540;
        private const double WinHeight = 480;

        private const int SM_CXSCREEN = 0;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        public MainWindow()
        {
            Title = "EditorDemo";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = System.Windows.Media.Brushes.Transparent;
            Width = WinWidth;
            Height = WinHeight;
            AllowsTransparency = true; // 复现主应用的分层窗口环境

            int screenW = GetSystemMetrics(SM_CXSCREEN);
            double wpfWidth = SystemParameters.PrimaryScreenWidth;
            double scale = wpfWidth > 0 ? screenW / wpfWidth : 1.0;
            Left = (wpfWidth - WinWidth) / 2.0;
            Top = 0;

            // 深色面板（模拟主应用面板）
            var panel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0B, 0x0F)),
                CornerRadius = new CornerRadius(18),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
            };

            // 编辑器区（不透明深色矩形，规避 WebView2 透明合成；视觉与面板一体）
            var editorHost = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x15)),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(18, 18, 18, 18),
            };

            var webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x10, 0x11, 0x15) };
            editorHost.Child = webView;
            panel.Child = editorHost;

            Content = panel;

            Loaded += async (_, _) =>
            {
                var htmlPath = Path.Combine(AppContext.BaseDirectory, "editor.html");
                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Navigate("file:///" + htmlPath.Replace('\\', '/'));
            };
        }
    }
}
