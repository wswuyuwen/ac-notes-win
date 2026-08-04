using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AcNotes.Windows
{
    /// <summary>
    /// 紧凑胶囊窗口（双窗口架构：紧凑态载体，2026-07-31 圆角抗锯齿演进）。
    /// 分层窗口（AllowsTransparency=true）：WPF GPU 抗锯齿绘制胶囊圆角，
    /// 替代 SetWindowRgn 二值区域裁剪的锯齿边缘（GDI region 无法抗锯齿）。
    /// 本窗口不承载 WebView2 → 无"分层窗口下 WebView2 键盘输入失效"的限制。
    /// 透明像素区域命中测试自动失败 → 胶囊外点击穿透（等效原区域裁剪行为）。
    /// </summary>
    internal sealed class CapsuleWindow : Window
    {
        private const double WidthDips = 100;     // 胶囊宽度（130 → 100，用户指定）
        private const double HeightDips = 42;     // 胶囊高度（58 → 42：图标+文字单行并排）
        private const double CornerRadiusValue = 21; // 42 高 → 上下各 21 = 两端半圆（macOS 原版胶囊）

        /// <summary>花纹卡片背景（UI 库 animal-island-ui .pattern-default 语义：大圆点 28px 网格 1.5px
        /// + 小圆点 14px 网格偏移 7px 1px；底色/点色随主题：sea=seaBlue 蓝底纹 / tree=treeGreen 绿底纹）</summary>
        private static Brush BuildPatternBrush(System.Windows.Media.Color baseColor, System.Windows.Media.Color dotColor)
        {
            var bigDot = new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb(0x26, dotColor.R, dotColor.G, dotColor.B)), null, // 点 0.15
                new EllipseGeometry(new Point(0, 0), 1.5, 1.5));
            var bigTile = new DrawingBrush(bigDot)
            {
                TileMode = TileMode.Tile, Stretch = Stretch.None,
                Viewport = new Rect(0, 0, 28, 28), ViewportUnits = BrushMappingMode.Absolute,
            };
            var smallDot = new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb(0x1A, dotColor.R, dotColor.G, dotColor.B)), null, // 点 0.10
                new EllipseGeometry(new Point(0, 0), 1, 1));
            var smallTile = new DrawingBrush(smallDot)
            {
                TileMode = TileMode.Tile, Stretch = Stretch.None,
                Viewport = new Rect(7, 7, 14, 14), ViewportUnits = BrushMappingMode.Absolute, // 偏移 7px
            };
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(baseColor), null,
                new RectangleGeometry(new Rect(0, 0, 1, 1)))); // 底色
            drawing.Children.Add(new GeometryDrawing(bigTile, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
            drawing.Children.Add(new GeometryDrawing(smallTile, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
            drawing.Freeze();
            return new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        }

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);

        public CapsuleWindow()
        {
            Title = "AcNotesCapsule";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true; // 分层窗口：抗锯齿圆角；本窗口无 WebView2，无输入限制
            Background = Brushes.Transparent;
            Width = WidthDips;
            Height = HeightDips;
            Left = (SystemParameters.PrimaryScreenWidth - WidthDips) / 2.0; // 顶部中央，与激活区常量区域一致
            Top = 0;

            _capsule = new Border
            {
                // 花纹卡片风格，底色随主题（初始 sea 蓝底纹；tree 时 SetTheme 切换绿底纹）
                Background = BuildPatternBrush(
                    Color.FromRgb(0x98, 0xD2, 0xE3),       // sea: seaBlue 底
                    Color.FromRgb(0x5E, 0x9B, 0xB0)),      // 深蓝点
                CornerRadius = new CornerRadius(CornerRadiusValue),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xC4, 0xA8)), // 花纹卡片描边 #d4c4a8
                BorderThickness = new Thickness(1.5),
            };
            // 内容：图标 +「随手记」同一排，中间间隙 6px，整体居中（用户 2026-08-04）
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon-design.png");
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (System.IO.File.Exists(iconPath))
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(iconPath, UriKind.Absolute);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                stack.Children.Add(new Image
                {
                    Source = bmp,
                    Width = 24,  // 98:72 原比 → 高 18
                    Height = 18,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0), // 与文字间隙 6px
                });
            }
            // 「随手记」：ZCOOL KuaiLe 品牌字体、棕 #725d42（对齐标题栏）
            var zcoolFont = new FontFamily(new Uri(AppContext.BaseDirectory),
                "./Assets/ZCOOLKuaiLe-Regular.ttf#ZCOOL KuaiLe");
            stack.Children.Add(new TextBlock
            {
                Text = "随手记",
                FontFamily = zcoolFont,
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromRgb(0x72, 0x5D, 0x42)), // brown #725d42
                VerticalAlignment = VerticalAlignment.Center,
            });
            _capsule.Child = stack;
            Content = _capsule;

            // Show 完成后补 TOOLWINDOW（与主窗口一致；SourceInitialized 阶段 WPF 可能重置样式，Loaded 后补最稳）
            Loaded += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                Console.WriteLine($"[capsule] Loaded: hwnd=0x{hwnd.ToInt64():X}");
                EnsureToolWindow();
            };
        }

        /// <summary>主题切换：胶囊花纹底 sea=seaBlue 蓝底纹 / tree=treeGreen 绿底纹（用户 2026-08-04）</summary>
        public void SetTheme(bool treeTheme)
        {
            if (_capsule == null) return;
            _capsule.Background = treeTheme
                ? BuildPatternBrush(
                    Color.FromRgb(0x62, 0xB9, 0x8B),       // tree: treeGreen 底
                    Color.FromRgb(0x3E, 0x8A, 0x62))       // 深绿点
                : BuildPatternBrush(
                    Color.FromRgb(0x98, 0xD2, 0xE3),       // sea: seaBlue 底
                    Color.FromRgb(0x5E, 0x9B, 0xB0));      // 深蓝点
        }

        private Border? _capsule;

        /// <summary>无边框窗口 ShowInTaskbar=false 不自动加 WS_EX_TOOLWINDOW（Alt+Tab 会露出），手动补位</summary>
        private void EnsureToolWindow()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) == 0)
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        }

        /// <summary>双窗口显隐：只改显示状态，不动尺寸/位置/Z 序（乱跳机制不回归）</summary>
        public void SetVisible(bool visible)
        {
            if (visible && !IsVisible) Show();
            else if (!visible && IsVisible) Hide();
        }

        public long ExStyle
        {
            get
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                return hwnd == IntPtr.Zero ? 0 : GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            }
        }
    }
}
