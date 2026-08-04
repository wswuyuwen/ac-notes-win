using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AcNotes.Windows
{
    /// <summary>
    /// lucide 图标集（lucide.dev，ISC 许可）的 WPF 复刻：24×24 viewBox 线条图标，
    /// 以 Path.Stroke 渲染（stroke-width 2、圆头/圆角连接，等价 lucide stroke 属性），
    /// 颜色绑定父按钮 Foreground，任意 DPI 矢量清晰。对齐官网 demo
    /// （oil-oil.github.io/NotchNotes 面板即用 lucide 图标渲染）。
    /// </summary>
    internal static class LucideIcon
    {
        /// <summary>创建 lucide 线条图标（Path），颜色自动跟随父按钮 Foreground</summary>
        public static Path Create(string name, double size = 16, double strokeThickness = 1.5)
        {
            var path = new Path
            {
                Data = Geometry.Parse(Paths[name]),
                StrokeThickness = strokeThickness, // 默认 1.5：细线条（用户反馈 2 过粗；对齐 lucide 14px 视觉比例）
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
            path.SetBinding(Shape.StrokeProperty, new Binding("Foreground")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
            });
            return path;
        }

        private static readonly Dictionary<string, string> Paths = new()
        {
            ["minus"] = "M5 12h14",
            ["x"] = "M18 6L6 18 M6 6l12 12",
            ["plus"] = "M12 5v14 M5 12h14",
            ["trash-2"] = "M10 11v6 M14 11v6 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M3 6h18 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2",
            ["settings"] = "M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915 M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0",
            ["bold"] = "M6 12h9a4 4 0 0 1 0 8H7a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h7a4 4 0 0 1 0 8",
            ["italic"] = "M19 4h-9 M14 20H5 M15 4L9 20",
            ["strikethrough"] = "M16 4H9a3 3 0 0 0-2.83 4 M14 12a4 4 0 0 1 0 8H6 M4 12h16",
            ["code-2"] = "M18 16L22 12L18 8 M6 8L2 12L6 16 M14.5 4L9.5 20",
            ["link"] = "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71",
            ["quote"] = "M16 3a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2 1 1 0 0 1 1 1v1a2 2 0 0 1-2 2 1 1 0 0 0-1 1v2a1 1 0 0 0 1 1 6 6 0 0 0 6-6V5a2 2 0 0 0-2-2z M5 3a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2 1 1 0 0 1 1 1v1a2 2 0 0 1-2 2 1 1 0 0 0-1 1v2a1 1 0 0 0 1 1 6 6 0 0 0 6-6V5a2 2 0 0 0-2-2z",
            ["list"] = "M3 5h.01 M3 12h.01 M3 19h.01 M8 5h13 M8 12h13 M8 19h13",
            ["list-ordered"] = "M11 5h10 M11 12h10 M11 19h10 M4 4h1v5 M4 9h2 M6.5 20H3.4c0-1 2.6-1.925 2.6-3.5a1.5 1.5 0 0 0-2.6-1.02",
            ["list-checks"] = "M13 5h8 M13 12h8 M13 19h8 M3 17L5 19L9 15 M3 7L5 9L9 5",
            ["clock"] = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0 M12 6v6l4 2",
        };
    }
}
