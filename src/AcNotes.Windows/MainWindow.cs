using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace AcNotes.Windows
{
    /// <summary>
    /// 主窗口：展开态面板（520×480 无边框置顶，DWM 抗锯齿圆角 + WebView2 编辑器）。
    /// 双窗口架构：紧凑态由 CapsuleWindow（独立分层窗口，抗锯齿胶囊圆角）承载，
    /// 形态切换 = 两窗口互斥显隐 + 内容透明度淡入；窗口尺寸永不变化（乱跳机制不回归）。
    /// 全局鼠标钩子 hover 触发（常量物理区域判定，与窗口状态解耦）。
    /// </summary>
    internal sealed class MainWindow : Window
    {
        // ---- 尺寸参数（面板宽 520 对齐官网 oil-oil.github.io/NotchNotes demo；
        // 高 480 恢复 macOS NotchGeometry 原版高度（官网 demo 360 受容器限制偏矮）；
        // 紧凑胶囊 170×32（用户 2026-08-04 调小：原 210×36 过宽））----
        private const double CompactWidth = 100;  // 胶囊宽度（130 → 100，用户指定）
        private const double CompactHeight = 42;  // 胶囊高度（58 → 42：图标+文字单行并排，激活区同步）
        private const double ExpandedWidth = 520;
        private const double ExpandedHeight = 480;
        // 顶部平直（macOS TopAttachedRoundedShape 语义：面板顶部贴屏幕边缘无圆角）。
        // DWM 圆角固定 8px 作用于四角 → 窗口上移 8px，顶部圆角弧线藏到屏幕外，屏幕内即平直顶边。
        private const double TopHideCorner = -8;

        // ---- 动画参数（保留：内容淡入区间 / 收起防抖）----
        private const double CollapseDelay = 0.22;
        private const double FadeInStart = 0.42;
        private const double FadeInEnd = 0.76;
        private const double StayMargin = 10;
        // 页脚高度：sea 图宽高比 1440:186 固定值（对齐 macOS footerHeight = 面板宽 × 186/1440）
        private const double FooterHeight = ExpandedWidth * 186.0 / 1440.0;
        // 内容底部留白 = 页脚高 + 间距（对齐 macOS contentBottomPadding = footerHeight + 13）
        private const double FooterGap = 13;
        // 顶部标题栏高度（对齐 macOS TitleBarView .frame(height: 60)）
        private const double TitleBarHeight = 60;

        private static readonly string LogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcNotes", "poc.log");

        /// <summary>
        /// 主面板背景：panel-bg.jpg 平铺 + 奶油渐变遮罩（对齐 macOS ACAssets.panelBackground）。
        /// 图片按原始像素平铺（与面板 520×480 相比大图裁左上），上层盖 #F8F8F0 渐变
        /// （0.70 → 0.55 → 0.85 透明度，顶→底）保证内容可读。
        /// ⚠️ 屏幕 DPI 缩放补偿（用户 2026-08-04 反馈背景图橘子比 macOS 大）：WPF 平铺单元按 DIP 计算，
        /// 125% 缩放下物理放大 1.25 倍；tile 尺寸取 像素÷dpiScale，物理 1:1 还原原图像素
        /// （对齐 macOS 非 Retina 屏 resizable(.tile) 原大平铺）。
        /// 资源缺失时回退原深色 #050506，不抛异常。
        /// </summary>
        private static Brush LoadPanelBackgroundBrush(double dpiScale)
        {
            var bgPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "panel-bg.jpg");
            if (System.IO.File.Exists(bgPath))
            {
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(bgPath, UriKind.Absolute);
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    // 平铺单元 DIP 尺寸 = 像素 ÷ dpiScale（物理 1:1 原图）
                    double tileW = bitmap.PixelWidth / dpiScale;
                    double tileH = bitmap.PixelHeight / dpiScale;

                    // 奶油渐变遮罩：顶 0.70 → 中 0.55 → 底 0.85（对齐 macOS LinearGradient）
                    var mask = new LinearGradientBrush(
                        System.Windows.Media.Color.FromArgb(0xB3, 0xF8, 0xF8, 0xF0),
                        System.Windows.Media.Color.FromArgb(0xD9, 0xF8, 0xF8, 0xF0),
                        90.0); // 0xB3=0.70, 0x8C=0.55, 0xD9=0.85 用三档渐变模拟
                    mask.GradientStops.Clear();
                    mask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0xB3, 0xF8, 0xF8, 0xF0), 0.0));
                    mask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x8C, 0xF8, 0xF8, 0xF0), 0.5));
                    mask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0xD9, 0xF8, 0xF8, 0xF0), 1.0));
                    mask.Freeze();

                    // 组合：图 + 遮罩（DrawingBrush 内两层，遮罩在上）
                    var drawing = new DrawingGroup();
                    drawing.Children.Add(new ImageDrawing(bitmap, new Rect(0, 0, tileW, tileH)));
                    drawing.Children.Add(new GeometryDrawing
                    {
                        Brush = mask,
                        Geometry = new RectangleGeometry(new Rect(0, 0, tileW, tileH)),
                    });
                    drawing.Freeze();

                    return new DrawingBrush(drawing)
                    {
                        TileMode = TileMode.Tile,
                        Stretch = Stretch.None,
                        Viewport = new Rect(0, 0, tileW, tileH),
                        ViewportUnits = BrushMappingMode.Absolute,
                    };
                }
                catch
                {
                    // 图片损坏等：回退深色，不让面板白屏
                }
            }
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x05, 0x06));
        }

        /// <summary>加载 Assets 下图片资源（缺失返回 null，不抛异常）</summary>
        private static System.Windows.Media.Imaging.BitmapImage? LoadAssetImage(string fileName)
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (!System.IO.File.Exists(path)) return null;
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 构建底部页脚：footer-sea/tree.png（随主题）贴面板底部与左右，无间距；
        /// 高度 = 面板宽 × 186/1440，scaledToFill 等比填充裁切（对齐 macOS drawer 页脚），
        /// 只裁底部两圆角对齐 DWM 8px；不拦截点击。资源缺失返回 null（不渲染页脚）。
        /// </summary>
        private UIElement? BuildFooter()
        {
            var bitmap = LoadAssetImage(_treeTheme ? "footer-tree.png" : "footer-sea.png");
            if (bitmap == null) return null;

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.UniformToFill, // scaledToFill：等比铺满目标区域，溢出裁切
                IsHitTestVisible = false,        // 页脚纯装饰，不拦截点击
            };

            var border = new Border
            {
                Child = image,
                Height = FooterHeight,
                CornerRadius = new CornerRadius(0, 0, 8, 8), // 底部两圆角对齐面板 DWM 8px
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            return border;
        }

        /// <summary>
        /// 顶部标题栏（对齐 macOS TitleBarView）：居中标题"动森随手记"（ZCOOL KuaiLe 28px 主题色）、
        /// 左侧 logo（animal_icon.png fit 等比）、右侧时间卡（星期/月日/时间，冒号每秒闪烁）。
        /// 三层覆盖布局：ZStack 等价物 = Grid 内三个子元素按 Z 序叠加。
        /// </summary>
        private Grid BuildTitleBar()
        {
            var titleBar = new Grid { Height = TitleBarHeight };

            // ZCOOL KuaiLe 字体（Assets 内嵌 ttf，# 后为 family 名）
            var zcoolFont = new FontFamily(new Uri(AppContext.BaseDirectory),
                "./Assets/ZCOOLKuaiLe-Regular.ttf#ZCOOL KuaiLe");

            // 主题强调色（seaBlue #98D2E3 ↔ treeGreen #62B98B，随 ThemeButton 切换）
            var accentBrush = new SolidColorBrush(ThemeAccentColor);
            accentBrush.Freeze();

            // ---- 居中：标题（对齐 macOS .custom(ZCOOL, size: 28pt)；用户指定字号 30）----
            _titleBlock = new TextBlock
            {
                Text = "随手记",
                FontFamily = zcoolFont,
                FontSize = 30,
                Foreground = accentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            titleBar.Children.Add(_titleBlock);

            // ---- 左侧：logo（fit 等比，高度 = 时间卡高 + 10；hover 放大 1.12 + 上浮 3px，
            //      对齐 macOS scaleEffect(1.12) + offset(y: -3) + spring(0.3, 0.6)）----
            var logoBitmap = LoadAssetImage("animal_icon.png");
            if (logoBitmap != null)
            {
                var logo = new Image
                {
                    Source = logoBitmap,
                    Stretch = Stretch.Uniform,
                    Height = TimeCardHeight + 10,
                    Margin = new Thickness(2, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = _handCursor ?? Cursors.Hand,
                };

                // hover 动效：Scale 1.0→1.12 + Y 平移 0→-3（spring 等效 = 0.3s ease-out）
                var scale = new ScaleTransform(1.0, 1.0);
                var translate = new TranslateTransform(0, 0);
                var transformGroup = new TransformGroup();
                transformGroup.Children.Add(scale);
                transformGroup.Children.Add(translate);
                logo.RenderTransform = transformGroup;
                logo.RenderTransformOrigin = new Point(0.5, 0.5);

                var hoverIn = new DoubleAnimation(1.12, TimeSpan.FromSeconds(0.3))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var hoverOut = new DoubleAnimation(1.0, TimeSpan.FromSeconds(0.3))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var riseIn = new DoubleAnimation(-3, TimeSpan.FromSeconds(0.3))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var riseOut = new DoubleAnimation(0, TimeSpan.FromSeconds(0.3))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

                logo.MouseEnter += (_, _) =>
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, hoverIn);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, hoverIn);
                    translate.BeginAnimation(TranslateTransform.YProperty, riseIn);
                };
                logo.MouseLeave += (_, _) =>
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, hoverOut);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, hoverOut);
                    translate.BeginAnimation(TranslateTransform.YProperty, riseOut);
                };

                titleBar.Children.Add(logo);
            }

            // ---- 右侧：时间卡（白色渐变圆角 10 卡片，对齐 macOS AnimalTimeView）----
            var timeCard = BuildTimeCard();
            timeCard.HorizontalAlignment = HorizontalAlignment.Right;
            timeCard.VerticalAlignment = VerticalAlignment.Center;
            titleBar.Children.Add(timeCard);

            return titleBar;
        }

        /// <summary>
        /// 时间卡（对齐 macOS AnimalTimeView）：白色渐变圆角 10 卡片 + 1.5px #D4CFC3 描边。
        /// 上下三行垂直堆叠（VStack spacing:1）：星期（black 绿 #6FBA2C）/ 月日（heavy 棕 #8B7355）/
        /// 时间 HH:mm（black 棕 #8B7355）拆三段 HH/冒号/mm，冒号每秒闪烁（0.5s opacity 过渡）。
        /// 字号按 macOS 视觉等比取小值（用户反馈换算放大后过大，回退 7/9/17）。
        /// </summary>
        private Border BuildTimeCard()
        {
            // 卡片底色：白 → #F8F8F0 垂直渐变（对齐 macOS LinearGradient [.white, #F8F8F0]）
            var cardBg = new LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF),
                System.Windows.Media.Color.FromRgb(0xF8, 0xF8, 0xF0),
                90.0);
            cardBg.Freeze();

            var green = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6F, 0xBA, 0x2C)); // 星期绿 (0.435,0.729,0.173)
            green.Freeze();
            var brown = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x73, 0x55)); // 月日/时间棕 (0.545,0.451,0.333)
            brown.Freeze();

            // 日期区：垂直堆叠（星期在上、月日在下，对齐 macOS VStack(spacing:1)）
            var dateStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            _timeWeekdayBlock = new TextBlock
            {
                FontSize = 7,
                FontWeight = FontWeights.Black,
                Foreground = green,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _timeMonthDayBlock = new TextBlock
            {
                FontSize = 9,
                FontWeight = FontWeights.Heavy,
                Foreground = brown,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0), // VStack spacing:1
            };
            dateStack.Children.Add(_timeWeekdayBlock);
            dateStack.Children.Add(_timeMonthDayBlock);

            // 时间区：HH : mm 水平三段（对齐 macOS HStack(spacing:0) 的 prefix/colon/suffix，
            // 冒号独立块控制闪烁；若整串+冒号会重复显示两个冒号）
            var timeStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _timeHourBlock = new TextBlock
            {
                FontSize = 17,
                FontWeight = FontWeights.Black,
                Foreground = brown,
            };
            _timeColonBlock = new TextBlock
            {
                Text = ":",
                FontSize = 17,
                FontWeight = FontWeights.Black,
                Foreground = brown,
                Margin = new Thickness(0, -2, 0, 0), // 冒号微上浮对齐 macOS .offset(y: -1.5)
            };
            _timeMinuteBlock = new TextBlock
            {
                FontSize = 17,
                FontWeight = FontWeights.Black,
                Foreground = brown,
            };
            timeStack.Children.Add(_timeHourBlock);
            timeStack.Children.Add(_timeColonBlock);
            timeStack.Children.Add(_timeMinuteBlock);

            // 整体垂直堆叠（VStack spacing:1）
            var inner = new StackPanel { Orientation = Orientation.Vertical };
            dateStack.Margin = new Thickness(0, 0, 0, 1); // 日期区与时间区间距
            inner.Children.Add(dateStack);
            inner.Children.Add(timeStack);

            var card = new Border
            {
                Child = inner,
                Padding = new Thickness(12, 5, 12, 5),
                CornerRadius = new CornerRadius(10),
                Background = cardBg,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD4, 0xCF, 0xC3)),
                BorderThickness = new Thickness(1.5),
            };

            RefreshClock(); // 初始填充
            _clockTimer.Tick += (_, _) =>
            {
                RefreshClock();
                // 冒号闪烁：每秒翻转，0.5s opacity 过渡（对齐 macOS .animation(.easeInOut(0.5))）
                _colonVisible = !_colonVisible;
                if (_timeColonBlock != null)
                {
                    _timeColonBlock.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(_colonVisible ? 1.0 : 0.0, TimeSpan.FromSeconds(0.5))
                        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
                }
            };
            _clockTimer.Start();

            return card;
        }

        /// <summary>刷新时间卡内容（星期 en_US 全大写 / MMM d / HH:mm 拆三段，对齐 macOS DateFormatter 格式）</summary>
        private void RefreshClock()
        {
            var now = DateTime.Now;
            var enUS = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            if (_timeWeekdayBlock != null)
                _timeWeekdayBlock.Text = now.ToString("dddd", enUS).ToUpperInvariant();
            if (_timeMonthDayBlock != null)
                _timeMonthDayBlock.Text = now.ToString("MMM d", enUS);
            if (_timeHourBlock != null)
                _timeHourBlock.Text = now.ToString("HH").PadLeft(2, '0'); // 小时两位
            if (_timeMinuteBlock != null)
                _timeMinuteBlock.Text = now.ToString("mm").PadLeft(2, '0'); // 分钟两位
        }

        /// <summary>时间卡高度（对齐 macOS AnimalTimeView：日期 7/9px + 时间 17px + padding 5×2 + spacing）</summary>
        private static double TimeCardHeight => 50;

        // ---- 窗口与动画状态 ----
        private readonly Grid _expandedContent = new();
        private readonly DispatcherTimer _collapseTimer = new();
        // ---- 删除标签二次确认弹窗（对齐 macOS ConfirmDeleteModal：深色遮罩 + 有机形状卡片 + 打字机 + 红删除按钮）----
        private Grid? _modalOverlay;           // 弹窗覆盖层（root 最上层；WebView2 弹窗期间需隐藏避免 airspace 遮挡）
        private bool _modalOpen;               // 弹窗打开中 → 禁止自动收起
        private DispatcherTimer? _typeTimer;   // 打字机逐字（typeSpeed 30ms）
        private int _typedCount;
        private long _typeTickMs;              // 性能取证：打字机 tick 累计耗时
        private int _typeTickCount;
        private TextBlock? _modalTitleBlock;
        private TextBlock? _modalBodyBlock;
        // 标题栏时钟（对齐 macOS AnimalTimeView：每秒刷新 + 冒号闪烁）
        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private TextBlock? _timeColonBlock;
        private TextBlock? _timeWeekdayBlock;
        private TextBlock? _timeMonthDayBlock;
        private TextBlock? _timeHourBlock;
        private TextBlock? _timeMinuteBlock;
        private bool _colonVisible = true;
        private bool _isExpanded;
        // 双窗口显隐状态（true=主窗口显示 / false=胶囊窗口显示）。初始 = true：
        // 窗口创建时主窗口即显示（app.Run 显示主窗口、胶囊未 Show），
        // 否则首次 SetProgress(0) 与初始 false 相同会跳过切换（胶囊永不显示、主窗口永不隐藏）
        private bool _windowExpanded = true;
        private readonly CapsuleWindow _capsule = new(); // 双窗口：紧凑态载体（独立分层窗口，抗锯齿胶囊圆角）

        // ---- 业务组件 ----
        private readonly NoteStore _store = new();
        private readonly MarkdownTiptapEditor _editor = new();
        private readonly StackPanel _tabsPanel = new() { Orientation = Orientation.Horizontal }; // 标签条（横向流式，超宽走 ScrollViewer 横滑）
        private ScrollViewer? _tabsScroll; // 标签横向滚动容器（新建后定位到新标签）
        // 鼠标左键拖拽平移标签条（2026-08-05 用户反馈"不能通过鼠标拖动滑动"；PanningMode 仅支持
        // 触摸/触控板，鼠标拖拽需手动实现——移动 >5px 判定拖拽，轻点正常触发标签选中）
        private bool _tabsDragging;
        private double _tabsDragStartX;
        private double _tabsDragOffset;
        // ⚠️ 水平滚轮（WM_MOUSEHWHEEL）：WPF 的 PreviewMouseWheel 只响应垂直滚轮（WM_MOUSEWHEEL），
        // 触控板双指横滑 / 鼠标水平滚轮发 WM_MOUSEHWHEEL（0x020E），WPF 无托管事件 → 标签区横滑无反应
        // （用户实测"超过标签组宽度后不能滑动"）。需 HwndSource hook 捕获后转 ScrollToHorizontalOffset。
        private const int WM_MOUSEHWHEEL = 0x020E;
        private System.Windows.Interop.HwndSource? _hwndSource;
        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEHWHEEL && _tabsScroll != null && _isExpanded)
            {
                // wParam 高 16 位 = 滚动量（正 = 右滑看后面，负 = 左滑看前面）；低 16 位忽略
                int delta = (short)((uint)wParam >> 16);
                _tabsScroll.ScrollToHorizontalOffset(_tabsScroll.HorizontalOffset + delta);
                handled = true;
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }
        private Button? _minusButton; // pager 固定减号（移除当前标签，不随标签滚动消失）
        private Button? _plusButton;  // pager 固定加号（新建标签）
        private NotifyIcon? _trayIcon;
        // ---- 主题切换（对齐 macOS ThemeButton：sea ↔ tree，页脚图 + accent 强调色）----
        private bool _treeTheme;              // false=sea（默认）/ true=tree
        private Grid? _root;                  // 根容器（切换页脚时替换用）
        private UIElement? _footerElement;    // 当前页脚元素（root 里替换用）
        private TextBlock? _titleBlock;       // 标题栏主标题（accent 色随主题切换）

        /// <summary>当前主题强调色（seaBlue #98D2E3 ↔ treeGreen #62B98B，对齐 ACTheme）</summary>
        private System.Windows.Media.Color ThemeAccentColor =>
            _treeTheme
                ? System.Windows.Media.Color.FromRgb(0x62, 0xB9, 0x8B) // treeGreen
                : System.Windows.Media.Color.FromRgb(0x98, 0xD2, 0xE3); // seaBlue

        private GlobalMouseHook? _hook;
        private GlobalKeyboardHook? _keyHook; // 全局 Esc 监听（面板展开后焦点在 WebView2，窗口 KeyDown 收不到）
        /// <summary>自定义手型光标（cursor-hand.cur；加载失败回退系统手型）——所有可点击元素统一使用，
        /// 避免 Cursors.Hand（系统手型）覆盖窗口自定义光标（2026-08-04 用户反馈工具栏图标光标不一致）</summary>
        private static readonly System.Windows.Input.Cursor? _handCursor = LoadHandCursor();

        private static System.Windows.Input.Cursor? LoadHandCursor()
        {
            try
            {
                var curPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "cursor-hand.cur");
                return System.IO.File.Exists(curPath) ? new System.Windows.Input.Cursor(curPath) : null;
            }
            catch { return null; }
        }
        private readonly bool _selfTest;
        private bool _hookInstalled;
        private bool _loadingTab; // 切 tab 加载中，避免回写
        private bool _clickTrigger; // 触发模式：false=hover（默认）/ true=click（设置菜单切换）
        private bool _pinOpen;      // 点击展开固定：托盘/菜单展开后不因鼠标位置自动收起（用户 2026-08-04 修复）
        private DateTime _pinOpenAt; // 点击展开时刻（抑制点托盘那次的 MouseUp，防展开瞬间被点击外部收起）
        private Button? _settingsButton; // 设置菜单锚点（纯代码构建无 Name 注册，用字段引用）

        // ---- Win32 ----
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOPMOST = 0x00000008;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTAPI { public int x; public int y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINTAPI lpPoint);

        // ---- DWM 圆角（Windows 11）：抗锯齿窗口边缘，替代 SetWindowRgn 二值区域裁剪（GDI 区域圆角必然锯齿）。
        // DWMWCP_ROUND 半径固定 8px（DWM 内置，不可配置）；只作用于窗口矩形四角，
        // 故紧凑胶囊形态改由独立分层窗口 CapsuleWindow 承载（见 CapsuleWindow.cs）。
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

        public MainWindow(bool selfTest)
        {
            _selfTest = selfTest;
            Log("=== AcNotes.Windows start selftest=" + selfTest + " ===");

            Title = "随手记";
            // 面板手型光标：cursor-hand.png → .cur（Windows 强制光标格式；热点 3,0 对齐 macOS hotSpot）
            if (_handCursor != null)
                Cursor = _handCursor;
            // 应用图标（icon-design.ico）：窗口/任务栏/Alt+Tab 兜底（用户 2026-08-04 要求）
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon-design.ico");
            if (System.IO.File.Exists(icoPath))
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(icoPath, UriKind.Absolute));
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;   // 对应 WS_EX_TOOLWINDOW（坑：无边框下需手动补位，见 ReportWindowStyle）
            Topmost = true;
            // 关键：不用 AllowsTransparency（分层窗口）——WebView2 在分层窗口下无法获得键盘输入
            // （官方 known issue）。用 WindowChrome 圆角 + 不透明窗口 + 窗口尺寸动画（紧凑=小窗，展开=大窗）。
            // 面板背景：panel-bg.jpg 平铺 + 奶油渐变遮罩（动森风；资源缺失回退 #050506）
            Background = LoadPanelBackgroundBrush(_dpiScale);
            Width = ExpandedWidth;  // 初始展开尺寸：WebView2 页面脚本需要非极小窗口才会执行（实测）
            Height = ExpandedHeight;
            AllowsTransparency = false;
            // 注：暂移除 WindowChrome 定位 WebView2 模块不执行的根因（疑似 WindowChrome 合成影响）

            // 双窗口架构（2026-07-31 圆角抗锯齿演进，替代 SetWindowRgn 区域裁剪）：
            //  - 主窗口：常驻 520×480 展开尺寸 @ 顶部中央（Top=-8 藏顶部圆角 → 屏幕内平直顶边）。
            //    WebView2 需要大/可见窗口初始化（屏幕外/小窗口实测不加载页面脚本）；
            //    展开态 DWM 圆角（抗锯齿，固定 8px）
            //  - 胶囊窗口（CapsuleWindow）：210×36 独立分层窗口，GPU 抗锯齿绘制胶囊圆角
            //  - 形态切换 = 两窗口互斥显隐（Show/Hide），窗口尺寸永不变化 → 无 HWND 抖动 → hover 判定稳定
            //  - 分层窗口透明像素命中测试自动失败 → 胶囊外点击穿透（低打扰）
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            double wpfWidth = SystemParameters.PrimaryScreenWidth;
            _dpiScale = wpfWidth > 0 ? screenW / wpfWidth : 1.0;
            Left = (wpfWidth - ExpandedWidth) / 2.0;
            Top = TopHideCorner;
            Log($"Screen={screenW}x{screenH} WPF-width={wpfWidth:F0} DPI-scale={_dpiScale:F2} -> 主窗口常驻 520×480(Top={TopHideCorner} 藏顶圆角) + 胶囊分层窗口 210×36，双窗口显隐切换");

            BuildUi();
            BuildTray();

            _collapseTimer.Interval = TimeSpan.FromSeconds(CollapseDelay);
            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer.Stop();
                if (_isExpanded && !_modalOpen && !IsPointerInStayRegion()) Collapse(); // 弹窗打开中不收起
            };

            _hook = new GlobalMouseHook();
            _hook.MouseMoved += OnMouseMoved;
            _hook.LeftButtonUp += OnGlobalMouseUp;
            // 钩子延迟到 OnLoaded 初始化完成后再安装：初始化期窗口保持展开尺寸（WebView2
            // 页面脚本需要大/可见窗口），过早安装会让顶部鼠标误触发 Expand，与 SetProgress(0) 打架。

            Loaded += OnLoaded;
            Closed += (_, _) => { _hook?.Dispose(); _keyHook?.Dispose(); _trayIcon?.Dispose(); _capsule.Close(); };
        }

        // ================= UI 构建 =================
        private void BuildUi()
        {
            panel = new Border
            {
                // 面板背景：panel-bg.jpg 平铺 + 奶油渐变遮罩（动森风；资源缺失回退 #050506）
                Background = LoadPanelBackgroundBrush(_dpiScale),
                // 8px 对齐 DWM 圆角（DWMWCP_ROUND 固定 8px）：窗口边缘由 DWM 抗锯齿裁切，
                // 内容圆角与其一致，无双重圆角残留
                CornerRadius = new CornerRadius(8),
                // 面板外框描边 = 当前主题强调色（对齐 macOS TopAttachedRoundedShape.stroke(accentColor, 1.5)，
                // sea 蓝 ↔ tree 绿随 ThemeButton 切换；1.5px 对齐蓝本 lineWidth，太细则被 DWM 阴影淹没）
                BorderBrush = new SolidColorBrush(ThemeAccentColor),
                BorderThickness = new Thickness(1.5),
                ClipToBounds = true,
            };

            var root = new Grid();
            _root = root;
            root.Children.Add(panel);
            // 底部页脚（动森 sea 主题）：贴面板底部与左右，内容层之上（macOS ZStack 底层语义）
            _footerElement = BuildFooter();
            if (_footerElement != null) root.Children.Add(_footerElement);
            root.Children.Add(_expandedContent);

            BuildExpandedContent();
            _editor.SetVisible(false); // 紧凑态隐藏（WebView2 仅展开态显示）

            // 删除确认弹窗覆盖层：root 最后一个子元素 = Z 序最上层（macOS ZStack 顶层语义）
            _modalOverlay = BuildDeleteConfirmModal();
            root.Children.Add(_modalOverlay);

            // 双窗口：主窗口只承载展开面板；紧凑胶囊由 CapsuleWindow（分层窗口）承载
            Content = root;
        }

        private void BuildExpandedContent()
        {
            // 面板 padding 对齐官网 demo：顶部 42（top-tools 区域）、左右 18、底部 18。
            // 外层必须用 Grid（不能用 StackPanel）：StackPanel 垂直给子元素无限高度，
            // editorCard 的 star 行会塌陷为 0（编辑器不可见）；Grid 有窗口有限高度约束，star 行正常分配
            var stack = new Grid { Margin = new Thickness(18, 14, 18, FooterHeight + FooterGap) };
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // titleBar（顶部标题栏 60px）
            stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 大卡片（标签+编辑器，对齐 macOS Tabs 容器）

            // ---- 顶部标题栏（logo + 标题 + 时间，对齐 macOS TitleBarView）----
            var titleBar = BuildTitleBar();
            Grid.SetRow(titleBar, 0);
            stack.Children.Add(titleBar);

            // ---- 大卡片内部（标签区 + 分隔线 + 编辑区，对齐 macOS VStack(spacing:0)）----
            var cardInner = new Grid();
            cardInner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标签区（TabPagerControl）
            cardInner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 分隔线 1px
            cardInner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 编辑区

            // ---- 标签区操作按钮（先创建，供下方 topTools 布局引用）----
            _minusButton = MakeToolButton(LucideIcon.Create("minus", 13), "移除标签", () =>
            {
                if (_store.Tabs.Count <= 1) return;
                // 二次确认（对齐 macOS TabPagerControl minus → ConfirmDeleteModal）
                ShowDeleteConfirmModal();
            });
            _plusButton = MakeToolButton(LucideIcon.Create("plus", 13), "新建笔记", async () =>
            {
                await SaveEditorStateAsync();
                _store.AddTab();
                await LoadTabAsync(_store.ActiveTabId);
                RefreshTabs(scrollToEnd: true); // 新建标签在末尾 → 滚动到末尾可见
                await _editor.FocusAsync();
            });

            // ---- top-tools（标签区，大卡片内 Row0）：三列 = 左 minus + 中标签横滑区 + 右 plus
            // 对齐 macOS TabPagerControl HStack [minus][ScrollView][plus]——加减天然在标签页两边 -->
            var topTools = new Grid();
            topTools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 左：minus（移除当前标签）
            topTools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 中：标签横滑区
            topTools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 右：plus（新建标签）
            Grid.SetRow(topTools, 0);

            // 左：minus 固定贴左缘（对齐 macOS TabPagerControl 首元素）
            _minusButton.Margin = new Thickness(10, 0, 0, 0); // 对齐 pager 左 padding 10
            Grid.SetColumn(_minusButton, 0);
            topTools.Children.Add(_minusButton);

            // 中：标签横滑区（仅标签，操作按钮分列两侧）
            // ⚠️ tabsPanel 右侧留白 6px：选中标签叶子右扩 6px 露头。若不加留白，
            // ScrollToRightEnd 滚到 Extent 右缘时叶子露头部分超出 Extent 被 ScrollViewer
            // 视口裁掉（离屏对照实测：留白 6px → 叶子 n=57 完整；仅 ScrollViewer.Padding
            // 方案 → 叶子 n=50 顶部/右缘被裁）。Padding 只缩 Viewport 不改变 Extent 右缘。
            _tabsPanel.Margin = new Thickness(0, 0, 6, 0);
            _tabsScroll = new ScrollViewer
            {
                Content = _tabsPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = System.Windows.Controls.PanningMode.HorizontalOnly,
            };
            // ⚠️ 鼠标滚轮横滑：PanningMode 仅支持触控板/触屏，鼠标滚轮默认垂直滚动（此容器无垂直滚动）
            // 会无反应。macOS ScrollView(.horizontal) 滚轮可横滑 → 拦截滚轮事件转水平偏移。
            _tabsScroll.PreviewMouseWheel += (s, e) =>
            {
                var scroller = (ScrollViewer)s!;
                scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
                e.Handled = true;
            };
            // ⚠️ 鼠标左键拖拽平移（2026-08-05 用户反馈"只能触控板滑、鼠标不能拖动"）：
            // PanningMode 仅支持触摸/触控板；拖拽平移 = 按下记录起点 → 移动 >5px 判定拖拽
            //（捕获鼠标平移，拦截标签 Button 误触）→ 松开结束；轻点 <5px 不拖拽，正常选中标签
            _tabsScroll.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _tabsDragStartX = e.GetPosition(_tabsScroll).X;
                _tabsDragOffset = _tabsScroll.HorizontalOffset;
                _tabsDragging = false;
            };
            _tabsScroll.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
                double dx = e.GetPosition(_tabsScroll).X - _tabsDragStartX;
                if (!_tabsDragging && Math.Abs(dx) > 5)
                {
                    _tabsDragging = true;
                    _tabsScroll.CaptureMouse();
                }
                if (_tabsDragging)
                {
                    _tabsScroll.ScrollToHorizontalOffset(_tabsDragOffset - dx);
                    e.Handled = true; // 拖拽中拦截，防标签 Button 误触
                }
            };
            _tabsScroll.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (_tabsDragging)
                {
                    _tabsDragging = false;
                    _tabsScroll.ReleaseMouseCapture();
                    e.Handled = true; // 拖拽过的松开不算点击
                }
            };

            var pager = new Border
            {
                // 动森卡片内标签区：无独立底/圆角（融入大卡片 #f8f8f0），对齐 macOS TabPagerControl padding(10,8)
                Background = System.Windows.Media.Brushes.Transparent,
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(10, 8, 6, 8),
                Child = _tabsScroll,
            };
            Grid.SetColumn(pager, 1); // 中列：标签横滑区（左 minus / 右 plus）
            topTools.Children.Add(pager);

            // 右：plus 固定贴右缘（对齐 macOS TabPagerControl 末元素）；删除/设置已移到底部工具栏
            _plusButton.Margin = new Thickness(6, 0, 10, 0);
            Grid.SetColumn(_plusButton, 2);
            topTools.Children.Add(_plusButton);
            cardInner.Children.Add(topTools);

            // ---- Tiptap 编辑器（WebView2 宿主；占位符/选区由 Tiptap 内置，内容存储为 HTML）----
            _editor.ContentChanged += async () =>
            {
                if (_loadingTab) return;
                _store.UpdateText(await _editor.GetHtmlAsync());
            };
            _editor.SelectionChanged += async () =>
            {
                if (_loadingTab) return;
                var (f, t) = await _editor.GetSelectionAsync();
                _store.UpdateSelection(_store.ActiveTabId, f, t);
            };

            // ---- editor-card（动森：背景 cardInset #faf8f2 对齐 macOS 编辑区；内嵌 note-body 编辑器 + toolbar-row 底部工具栏）----
            var editorCard = new Grid();
            editorCard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            editorCard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

            var editorHost = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xF8, 0xF2)), // cardInset #faf8f2（对齐 macOS 编辑区浅底）
                // ⚠️ Padding 2px 水平内缩 WebView2：WebView2 是独立子 HWND，airspace 渲染在 WPF 内容之上，
                // 若矩形贴卡片左右边缘会把居中描边 Path 盖住（实测右侧描边消失）。
                // Padding 只内缩 Child（WebView2），背景 cardInset 保持满宽（在描边之下无冲突）。
                Padding = new Thickness(2, 0, 2, 0),
                Child = _editor.View,
            };
            Grid.SetRow(editorHost, 0);
            editorCard.Children.Add(editorHost);

            // 底部工具栏行（对齐 macOS MarkdownShortcutToolbar：34px、浅色底、顶部分隔线 sand 0.35）
            var toolbarRow = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF0)), // cream #f8f8f0
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xC4, 0xB8, 0x9E)), // sand #c4b89e @ 35%
                BorderThickness = new Thickness(0, 1, 0, 0),
            };
            var mdToolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
            (MarkdownCommand command, string iconName, string help)[] items =
            {
                (MarkdownCommand.Bold, "bold", "加粗"),
                (MarkdownCommand.Italic, "italic", "斜体"),
                (MarkdownCommand.Strikethrough, "strikethrough", "删除线"),
                (MarkdownCommand.InlineCode, "code-2", "行内代码"),
                (MarkdownCommand.Link, "link", "链接"),
                (MarkdownCommand.Quote, "quote", "引用"),
                (MarkdownCommand.UnorderedList, "list", "无序列表"),
                (MarkdownCommand.OrderedList, "list-ordered", "有序列表"),
                (MarkdownCommand.TodoList, "list-checks", "任务列表"),
                (MarkdownCommand.Timestamp, "clock", "插入时间戳"),
            };
            foreach (var (cmd, iconName, help) in items)
            {
                // 对齐 macOS MarkdownToolbarButtonStyle：图标 brown 0.62，hover 0.85 / pressed 0.45。
                // ⚠️ 不能设本地 Background/Foreground——WPF 本地值优先级高于 Style Trigger，
                // hover/pressed 效果会被本地值压制（实测图标 hover 无变化）。默认色由 ToolbarButtonStyle 提供。
                var button = new Button
                {
                    Content = LucideIcon.Create(iconName, 14),
                    Width = 24,
                    Height = 22,
                    Margin = new Thickness(0, 0, 6, 0),
                    BorderThickness = new Thickness(0),
                    Cursor = _handCursor ?? Cursors.Hand,
                    ToolTip = help,
                };
                button.Click += async (_, _) => await _editor.ApplyCommandAsync(cmd);
                button.Style = ToolbarButtonStyle;
                mdToolbar.Children.Add(button);
            }

            // 删除/设置（对齐 macOS ClearNoteButton/SettingsMenu：独立图标按钮，与 markdown 命令同组但语义是笔记级操作）
            // ⚠️ 间距统一 6px：原 Margin(6,0,2,0) 左 6 与 markdown 组右 6 叠加成 12px，视觉间距过大（用户 2026-08-04 反馈）
            var clearButton = MakeToolButton(LucideIcon.Create("trash-2"), "清空笔记", () => _ = _editor.ClearAsync());
            clearButton.Margin = new Thickness(0, 0, 6, 0);
            mdToolbar.Children.Add(clearButton);
            _settingsButton = MakeToolButton(LucideIcon.Create("settings"), "设置", OpenSettingsMenu);
            _settingsButton.Margin = new Thickness(0, 0, 6, 0);
            mdToolbar.Children.Add(_settingsButton);

            // 右侧固定组（对齐 macOS HStack + Spacer：ChatLinkButton + ThemeButton 贴工具栏右缘）
            // Chat 链接：UI 库 icon-chat.svg 原色，点击打开项目 GitHub（对齐 macOS NSWorkspace.open）
            var rightGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
            rightGroup.Children.Add(MakeIconButton("icon-chat.png", "打开 GitHub 项目", () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/wswuyuwen")
                    { UseShellExecute = true });
                }
                catch (Exception ex) { Log("ChatLink open failed: " + ex.Message); }
            }));
            // 主题切换：UI 库 item-001.png，sea ↔ tree（页脚图 + accent 强调色）
            rightGroup.Children.Add(MakeIconButton("item-001.png", "更换主题", ToggleTheme));

            // 三列布局：左 Auto（markdown+删除+设置）/ 中 Star（弹性空隙 = macOS Spacer）/ 右 Auto（chat+theme）
            var toolbarGrid = new Grid();
            toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(mdToolbar, 0);
            toolbarGrid.Children.Add(mdToolbar);
            Grid.SetColumn(rightGroup, 2);
            toolbarGrid.Children.Add(rightGroup);
            toolbarRow.Child = toolbarGrid;
            Grid.SetRow(toolbarRow, 1);
            editorCard.Children.Add(toolbarRow);

            // 分隔线：标签区与编辑区 1px（对齐 macOS Rectangle fill tabBorder #e8e2d6）
            var separator = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE2, 0xD6)),
                Height = 1,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(separator, 1);
            cardInner.Children.Add(separator);

            Grid.SetRow(editorCard, 2);
            cardInner.Children.Add(editorCard);

            // ---- 大卡片容器（对齐 macOS Tabs 容器：#f8f8f0 底/圆角24/2px #e8e2d6 边框，底部垫 3D 阴影）----
            // 3D 阴影 = 硬边实心色块 #bdaea0 向下偏移 5px（macOS buttonShadow offset y:5，非投影效果）。
            // ⚠️ 不能用 DropShadowEffect：①语义是半透明模糊投影 ≠ 硬边色块 ②同一 Border 设置
            // Clip 圆角裁剪时，阴影偏移出元素边界部分会被 Clip 裁掉（离屏渲染实测完全不可见）。
            // WPF 等价双层结构：底层全尺寸阴影 Border（实心色块，圆角 24）+ 上层卡片 Border
            // Margin(0,0,0,5) 底部缩进 5px 露出阴影条。
            var editorCardOuter = new Grid();
            editorCardOuter.Margin = new Thickness(0, 10, 0, 0); // 标题栏与卡片间距（对齐 macOS VStack spacing 8 + 余量）

            // 底层：阴影色块（实心 #bdaea0，圆角 24，填满容器；卡片缩进后底部露出 5px 硬边色条）
            var shadowCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xBD, 0xAE, 0xA0)), // buttonShadow #bdaea0
                CornerRadius = new CornerRadius(24),
                IsHitTestVisible = false, // 纯装饰，不拦截点击
            };
            editorCardOuter.Children.Add(shadowCard);

            // 中层：卡片主体（cream 底、圆角 24；边框改外层 overlay 居中描边，BorderBrush 内缩语义不符）
            var editorCardShell = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF0)), // cream #f8f8f0
                CornerRadius = new CornerRadius(24),
                Margin = new Thickness(0, 0, 0, 5), // 底部缩进 5px，露出底层阴影色块
                Child = cardInner,
            };
            // 关键：ClipToBounds 是矩形裁剪（LayoutClip），不裁圆角——子元素（编辑器宿主不透明矩形背景）
            // 会把卡片圆角"填平"。macOS 用 clipShape(圆角24) 是圆角几何裁剪，WPF 对应必须用
            // Clip = RectangleGeometry(圆角)，且随尺寸变化更新（SizeChanged 里重设）。
            System.Windows.Shapes.Path? cardStroke = null;
            editorCardShell.SizeChanged += (_, _) =>
            {
                // 圆角几何裁剪：让子内容（编辑区矩形背景）按圆角裁切（对齐 macOS clipShape 24）
                editorCardShell.Clip = new RectangleGeometry(
                    new Rect(0, 0, editorCardShell.ActualWidth, editorCardShell.ActualHeight), 24, 24);
                // 同步居中描边几何（沿卡片外缘，不含阴影区）
                if (cardStroke != null)
                {
                    cardStroke.Data = new RectangleGeometry(
                        new Rect(0, 0, editorCardShell.ActualWidth, editorCardShell.ActualHeight), 24, 24);
                }
            };
            editorCardOuter.Children.Add(editorCardShell);

            // 顶层：居中描边（macOS stroke 居中于路径线，线宽 2 内外各摊 1px、外扩可见；
            // WPF Border 边框全铺在元素内侧不匹配，改用 Path 描边还原居中语义，外扩 1px 不被裁剪）
            // ⚠️ 固定灰 tabBorder #e8e2d6（macOS 蓝本同款）；不随主题——用户 2026-08-04 澄清：
            // "面板边框"指悬浮面板窗口轮廓，非内容编辑器卡片边框（勿再误改）
            cardStroke = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0xE2, 0xD6)), // tabBorder #e8e2d6
                StrokeThickness = 2,
                Data = new RectangleGeometry(new Rect(0, 0, 0, 0), 24, 24), // SizeChanged 里按实际尺寸更新
                IsHitTestVisible = false, // 纯装饰，不拦截点击
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Stretch = Stretch.None, // Data 按绝对坐标绘制，不被拉伸
            };
            editorCardOuter.Children.Add(cardStroke);

            Grid.SetRow(editorCardOuter, 1);
            stack.Children.Add(editorCardOuter);

            _expandedContent.Children.Add(stack);
            RefreshTabs();
        }

        /// <summary>标签区/工具区操作按钮：28×28、圆角 7、图标 brown 0.62；hover 背景 sand 0.18 + 图标 0.85、pressed 0.28/0.45（对齐 macOS RoundedHoverButtonBody）
        /// ⚠️ 不设本地 Background/Foreground：WPF 本地值优先级高于 Style Trigger，会压制 hover/pressed（同 mdToolbar 按钮）</summary>
        private Button MakeToolButton(UIElement icon, string help, Action onClick)
        {
            var button = new Button
            {
                Content = icon,
                Width = 28,  // 对齐 macOS TabIconButtonStyle .frame(28×28)
                Height = 28,
                BorderThickness = new Thickness(0),
                Cursor = _handCursor ?? Cursors.Hand,
                ToolTip = help,
            };
            button.Click += (_, _) => onClick();
            button.Style = ToolbarButtonStyle;
            return button;
        }

        // ================= 删除标签二次确认弹窗（macOS ConfirmDeleteModal 移植）=================
        // 严格对齐蓝本：深色遮罩 rgba(0,0,0,.75) fade-in 0.25s + 有机形状裁切卡片 #f7f3df 380×218
        // + 标题「删除标签」22px bold brown + 打字机正文（30ms/字）+ 右对齐硬边 3D 阴影按钮（取消奶油/删除红）

        /// <summary>构建弹窗覆盖层（root 最上层；初始隐藏）</summary>
        private Grid BuildDeleteConfirmModal()
        {
            var overlay = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(0xBF, 0x00, 0x00, 0x00)), // 深色遮罩 rgba(0,0,0,.75)
                Visibility = Visibility.Collapsed,
            };
            overlay.MouseLeftButtonDown += (_, _) => CloseDeleteConfirmModal(); // 点击遮罩 = 取消（macOS onTapGesture）

            // 卡片 380×218 居中，有机形状用 Path 矢量填充绘制（而非 Clip 裁剪）：
            // PERF 实测：Clip(复杂贝塞尔路径)+Opacity 动画 = 33fps 掉帧 45%；加 BitmapCache 反而 16fps（缓存生成/同步
            // 开销拖死 UI 线程）。Path 填充 = GPU tessellate 一次性缓存，动画期间仅 alpha 混合，零裁剪成本。
            var card = new Grid
            {
                Width = 380,
                Height = 218,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            card.Children.Add(new System.Windows.Shapes.Path
            {
                Data = BuildAnimalModalShape(), // 有机形状（380×218 绝对坐标）
                Fill = new SolidColorBrush(Color.FromRgb(0xF7, 0xF3, 0xDF)), // modalCardBg #f7f3df
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });
            // 形状内点击不冒泡到遮罩（macOS：仅遮罩点击取消；形状 Path 命中后由 card 截断冒泡）
            card.MouseLeftButtonDown += (_, e) => e.Handled = true;

            // 内部 padding：top 26 / h 30 / bottom 22（macOS .padding(.top,26).padding(.horizontal,30).padding(.bottom,22)）
            var inner = new Grid { Margin = new Thickness(30, 26, 30, 22) };
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // header
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // footer

            // ---- header：标题 + 右上圆形关闭（macOS：22px bold brown + 30×30 xmark）----
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _modalTitleBlock = new TextBlock
            {
                Text = "删除标签",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x72, 0x5D, 0x42)), // brown #725d42
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_modalTitleBlock, 0);
            header.Children.Add(_modalTitleBlock);
            var closeButton = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15), // 圆形
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = _handCursor ?? Cursors.Hand,
                Child = LucideIcon.Create("x", 13),
                ToolTip = "关闭",
            };
            // hover 圆底（UI 库 .animal-close:hover 背景 #725d421a）
            closeButton.MouseEnter += (_, _) => closeButton.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x72, 0x5D, 0x42));
            closeButton.MouseLeave += (_, _) => closeButton.Background = System.Windows.Media.Brushes.Transparent;
            closeButton.MouseLeftButtonDown += (_, _) => CloseDeleteConfirmModal();
            Grid.SetColumn(closeButton, 1);
            header.Children.Add(closeButton);
            header.Margin = new Thickness(0, 0, 0, 12); // padding bottom 12
            Grid.SetRow(header, 0);
            inner.Children.Add(header);

            // ---- body：打字机正文（macOS：16px semibold #8a7b66，lineSpacing 4 → 行高 22）----
            _modalBodyBlock = new TextBlock
            {
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x7B, 0x66)), // modalBodyText #8a7b66
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 18), // padding bottom 18
            };
            Grid.SetRow(_modalBodyBlock, 1);
            inner.Children.Add(_modalBodyBlock);

            // ---- footer：右对齐 gap 12（取消奶油胶囊 / 删除红色实底，均硬边 3D 阴影 offset y5）----
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancelButton = MakeModalButton(
                "取消",
                new SolidColorBrush(Color.FromRgb(0xF7, 0xF3, 0xDF)), // creamWarm 底
                new SolidColorBrush(Color.FromRgb(0xBD, 0xAE, 0xA0)), // buttonShadow #bdaea0
                new SolidColorBrush(Color.FromRgb(0x72, 0x5D, 0x42)), // brown 文字
                22,
                new SolidColorBrush(Color.FromArgb(0x4D, 0x72, 0x5D, 0x42)), // brown 30% 描边
                CloseDeleteConfirmModal);
            cancelButton.Margin = new Thickness(0, 0, 12, 0); // HStack spacing 12
            footer.Children.Add(cancelButton);
            footer.Children.Add(MakeModalButton(
                "删除",
                new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B)), // dangerRed #e5534b
                new SolidColorBrush(Color.FromRgb(0xC9, 0x44, 0x44)), // dangerRedShadow #c94444
                System.Windows.Media.Brushes.White,
                24,
                null,
                ConfirmDeleteActiveTab));
            Grid.SetRow(footer, 2);
            inner.Children.Add(footer);

            card.Children.Add(inner);
            overlay.Children.Add(card);
            return overlay;
        }

        /// <summary>胶囊按钮：36 高 + 硬边 3D 阴影色块（offset y 5，零模糊，对齐 macOS ZStack shadow.offset(y:5)）</summary>
        private static Grid MakeModalButton(string text, Brush fill, Brush shadowColor, Brush textBrush, double padH, Brush? border, Action onClick)
        {
            // 双层 Grid：底层阴影色块全尺寸 + 按钮本体底部缩 5 露出阴影（技能定稿模式，勿用 DropShadowEffect）
            var wrap = new Grid { Height = 41 }; // 36 按钮 + 5 阴影
            var shadow = new Border { Background = shadowColor, CornerRadius = new CornerRadius(18) };
            wrap.Children.Add(shadow);
            var btn = new Border
            {
                Background = fill,
                CornerRadius = new CornerRadius(18),
                Height = 36,
                Margin = new Thickness(0, 0, 0, 5), // 底部缩进 5 → 露出阴影色块
                Padding = new Thickness(padH, 0, padH, 0),
                Cursor = _handCursor ?? Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = textBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            };
            if (border != null)
            {
                btn.BorderBrush = border;
                btn.BorderThickness = new Thickness(2); // 取消按钮 brown 30% 描边 2px
            }
            btn.MouseLeftButtonDown += (_, _) => onClick();
            wrap.Children.Add(btn);
            return wrap;
        }

        /// <summary>显示删除确认弹窗（减号触发）：隐藏 WebView2 防 airspace 遮挡 + 禁止自动收起 + 打字机。
        /// 无淡入动画（用户 2026-08-04 拍板）：PERF 实测任何动画实现（Clip/BitmapCache/Path）均掉帧
        /// （4~11帧/335~362ms），瞬时显示彻底消除动画帧率问题。</summary>
        private void ShowDeleteConfirmModal()
        {
            if (_modalOpen || _modalOverlay == null) return;
            _modalOpen = true;
            _collapseTimer.Stop(); // 弹窗期间禁止自动收起（macOS isModalPresented=true 同语义）

            var swHide = System.Diagnostics.Stopwatch.StartNew();
            _editor.SetVisible(false); // WebView2 子 HWND 会盖住 WPF 弹窗 → 先隐藏（macOS 无此问题）
            swHide.Stop();
            Log($"PERF modal open SetVisible(false)={swHide.ElapsedMilliseconds}ms");

            _typedCount = 0;
            _modalBodyBlock!.Text = "";
            _modalOverlay.BeginAnimation(UIElement.OpacityProperty, null); // 清残留动画
            _modalOverlay.Opacity = 1; // 瞬时显示，无淡入
            _modalOverlay.Visibility = Visibility.Visible;

            // 打字机正文（整句连贯，超宽自动换行）
            StartTypewriter($"确定要删除「{_store.Title(_store.ActiveTabId)}」这个标签吗？删除后无法恢复。");
            Log("DELETE CONFIRM modal shown");
        }

        /// <summary>关闭弹窗（取消路径）：恢复编辑器可见性 + 允许自动收起</summary>
        private void CloseDeleteConfirmModal()
        {
            if (!_modalOpen) return;
            _typeTimer?.Stop();
            _modalOpen = false;
            if (_modalOverlay != null)
            {
                var swClose = System.Diagnostics.Stopwatch.StartNew();
                _modalOverlay.Visibility = Visibility.Collapsed;
                _modalOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                swClose.Stop();
                Log($"PERF modal close Collapsed={swClose.ElapsedMilliseconds}ms");
            }
            // 错峰（第一性原理：WebView2 显隐是浏览器进程重量级重绘，不能与遮罩消失同帧叠加）：
            // 先让画面稳定（遮罩已消失），延迟 ~120ms 再恢复编辑器，浏览器重绘发生在用户感知之外
            bool editorWasVisible = _editorVisible;
            _ = RestoreEditorDelayedAsync(editorWasVisible);
            Log("DELETE CONFIRM modal closed (cancel)");
        }

        /// <summary>延迟恢复 WebView2 可见性（错峰：避开遮罩消失帧，让浏览器重绘发生在画面稳定之后）</summary>
        private async Task RestoreEditorDelayedAsync(bool visible)
        {
            await Task.Delay(120);
            await Dispatcher.InvokeAsync(() =>
            {
                // 竞态防护：若延迟期间面板收起/展开（SetProgress 已改 _editorVisible），以 SetProgress 为准，跳过恢复
                if (_editorVisible == visible)
                {
                    var swShow = System.Diagnostics.Stopwatch.StartNew();
                    _editor.SetVisible(visible);
                    swShow.Stop();
                    Log($"PERF modal close SetVisible(true)={swShow.ElapsedMilliseconds}ms");
                }
            });
        }

        /// <summary>确认删除当前标签（macOS onConfirm：removeActiveTab + spring）</summary>
        private async void ConfirmDeleteActiveTab()
        {
            CloseDeleteConfirmModal();
            if (_store.Tabs.Count <= 1) return;
            await SaveEditorStateAsync();
            _store.RemoveTab(_store.ActiveTabId);
            await LoadTabAsync(_store.ActiveTabId);
            RefreshTabs();
            Log("DELETE CONFIRM confirmed, tab removed");
        }

        /// <summary>打字机逐字显示（macOS typeSpeed 30ms/字）</summary>
        private void StartTypewriter(string full)
        {
            _typeTimer?.Stop();
            _typedCount = 0;
            _typeTickMs = 0;
            _typeTickCount = 0;
            _modalBodyBlock!.Text = "";
            _typeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _typeTimer.Tick += (_, _) =>
            {
                var swTick = System.Diagnostics.Stopwatch.StartNew();
                _typedCount++;
                _modalBodyBlock!.Text = full[..Math.Min(_typedCount, full.Length)];
                if (_typedCount >= full.Length)
                {
                    _typeTimer?.Stop();
                    Log($"PERF typewriter avgTick={_typeTickMs / Math.Max(1, _typeTickCount)}ms x{_typeTickCount}");
                }
                swTick.Stop();
                _typeTickMs += swTick.ElapsedMilliseconds;
                _typeTickCount++;
            };
            _typeTimer.Start();
        }

        /// <summary>有机形状裁切（macOS AnimalModalShape：SVG clipPath 归一化坐标 × 380×218）</summary>
        private static Geometry BuildAnimalModalShape()
        {
            const double w = 380, h = 218;
            Point P(double x, double y) => new(x * w, y * h);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(P(0.501, 0.005), true, true);
                ctx.LineTo(P(0.523, 0.005), true, false);
                ctx.LineTo(P(0.549, 0.006), true, false);
                ctx.BezierTo(P(0.704, 0.010), P(0.796, 0.017), P(0.825, 0.027), true, false);
                ctx.LineTo(P(0.827, 0.028), true, false);
                ctx.BezierTo(P(0.872, 0.045), P(0.939, 0.044), P(0.978, 0.170), true, false);
                ctx.BezierTo(P(1.000, 0.254), P(1.000, 0.365), P(0.990, 0.505), true, false);
                ctx.LineTo(P(0.988, 0.513), true, false);
                ctx.BezierTo(P(0.979, 0.558), P(0.971, 0.598), P(0.965, 0.633), true, false);
                ctx.BezierTo(P(0.956, 0.689), P(0.979, 0.770), P(0.964, 0.865), true, false);
                ctx.BezierTo(P(0.953, 0.928), P(0.921, 0.966), P(0.869, 0.979), true, false);
                ctx.BezierTo(P(0.821, 0.986), P(0.773, 0.992), P(0.726, 0.995), true, false);
                ctx.LineTo(P(0.712, 0.996), true, false);
                ctx.LineTo(P(0.694, 0.997), true, false);
                ctx.BezierTo(P(0.648, 1.000), P(0.586, 1.000), P(0.507, 1.000), true, false);
                ctx.LineTo(P(0.501, 1.000), true, false);
                ctx.LineTo(P(0.464, 1.000), true, false);
                ctx.BezierTo(P(0.385, 1.000), P(0.325, 0.998), P(0.283, 0.995), true, false);
                ctx.BezierTo(P(0.234, 0.992), P(0.184, 0.987), P(0.133, 0.979), true, false);
                ctx.BezierTo(P(0.081, 0.966), P(0.050, 0.928), P(0.039, 0.865), true, false);
                ctx.BezierTo(P(0.023, 0.770), P(0.047, 0.689), P(0.037, 0.633), true, false);
                ctx.BezierTo(P(0.031, 0.595), P(0.023, 0.552), P(0.013, 0.505), true, false);
                ctx.BezierTo(P(-0.006, 0.365), P(-0.002, 0.254), P(0.024, 0.170), true, false);
                ctx.BezierTo(P(0.064, 0.045), P(0.130, 0.045), P(0.174, 0.028), true, false);
                ctx.BezierTo(P(0.204, 0.017), P(0.303, 0.009), P(0.474, 0.005), true, false);
                ctx.LineTo(P(0.501, 0.005), true, false);
            }
            geo.Freeze();
            return geo;
        }

        /// <summary>图片图标按钮（对齐 macOS ChatLinkButton/ThemeButton：28×28 + hover 圆角底 buttonHover=sand 20%）</summary>
        private static Border MakeIconButton(string assetName, string help, Action onClick)
        {
            var img = LoadAssetImage(assetName);
            var border = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7), // macOS RoundedRectangle(continuous) 7
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = _handCursor ?? Cursors.Hand,
                ToolTip = help,
                Child = img == null ? null : new Image
                {
                    Source = img,
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.Uniform, // macOS aspectRatio(.fit) 等比
                },
            };
            // hover 圆底（macOS buttonHover = sand.opacity(0.20) → #c4b89e @ 33）
            var hoverBg = new SolidColorBrush(Color.FromArgb(0x33, 0xC4, 0xB8, 0x9E));
            hoverBg.Freeze();
            border.MouseEnter += (_, _) => border.Background = hoverBg;
            border.MouseLeave += (_, _) => border.Background = System.Windows.Media.Brushes.Transparent;
            border.MouseLeftButtonDown += (_, _) => onClick();
            return border;
        }

        /// <summary>主题切换（sea ↔ tree）：页脚图 + 标题 accent 色 + 标签选中背景（对齐 macOS ThemeButton）</summary>
        private void ToggleTheme()
        {
            _treeTheme = !_treeTheme;
            Log("Theme -> " + (_treeTheme ? "tree" : "sea"));
            // 0. 胶囊花纹底随主题（sea 蓝底纹 / tree 绿底纹）
            _capsule.SetTheme(_treeTheme);

            // 1. 页脚图（footer-sea.png ↔ footer-tree.png）：root 里原位替换（panel 之后、内容层之前）
            if (_root != null && _footerElement != null)
            {
                _root.Children.Remove(_footerElement);
                _footerElement = BuildFooter();
                if (_footerElement != null) _root.Children.Insert(1, _footerElement);
            }
            // 2. 标题 accent 色（seaBlue ↔ treeGreen）
            if (_titleBlock != null)
            {
                var accentBrush = new SolidColorBrush(ThemeAccentColor);
                accentBrush.Freeze();
                _titleBlock.Foreground = accentBrush;
            }
            // 3. 面板外框描边（对齐 macOS TopAttachedRoundedShape.stroke(accentColor)）
            if (panel != null)
            {
                var borderBrush = new SolidColorBrush(ThemeAccentColor);
                borderBrush.Freeze();
                panel.BorderBrush = borderBrush;
            }
            // 4. 标签选中背景（TabPillButtonStyle 读 ThemeAccentColor，重建标签生效）
            RefreshTabs();
        }

        /// <summary>设置菜单（当前：触发模式切换；更多设置项待 M4）</summary>
        private void OpenSettingsMenu()
        {
            var menu = new ContextMenu();
            var trigger = new WpfMenuItem
            {
                Header = _clickTrigger ? "触发方式：点击" : "触发方式：悬停",
            };
            trigger.Click += (_, _) =>
            {
                _clickTrigger = !_clickTrigger;
                Log("Trigger mode -> " + (_clickTrigger ? "click" : "hover"));
            };
            menu.Items.Add(trigger);
            menu.Items.Add(new WpfMenuItem { Header = "更多设置即将推出", IsEnabled = false });
            if (_settingsButton != null)
            {
                menu.PlacementTarget = _settingsButton;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            }
            menu.IsOpen = true;
        }

        private static Style? _toolbarButtonStyle;

        private static Style ToolbarButtonStyle => _toolbarButtonStyle ??= CreateToolbarButtonStyle();

        /// <summary>
        /// 自定义模板去除 WPF Button 默认 chrome（ButtonChrome 忽略 Background/BorderThickness，
        /// hover 动画在透明分层窗口下触发全量重绘导致卡顿）。模板 = 圆角 Border + ContentPresenter，
        /// hover/pressed 只改纯色背景 alpha，对应 macOS RoundedHoverButtonBody。
        /// </summary>
        private static Style CreateToolbarButtonStyle()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            // ⚠️ 圆角不能走附加属性绑定（SetBinding "(Type.Prop)" 字符串路径在
            // FrameworkElementFactory 模板中实测不生效，Border.CornerRadius 恒为 0 → 直角矩形）。
            // 工具按钮统一圆角 7，模板直设。
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            var style = new Style(typeof(Button))
            {
                Setters =
                {
                    new Setter(Button.TemplateProperty, template),
                    new Setter(Button.BackgroundProperty, System.Windows.Media.Brushes.Transparent),
                    new Setter(Button.ForegroundProperty, new SolidColorBrush(Color.FromArgb(0x9E, 0x72, 0x5D, 0x42))), // brown 0.62 默认
                    new Setter(Button.BorderThicknessProperty, new Thickness(0)),
                    new Setter(Button.FocusableProperty, false), // 点击不抢键盘焦点，避免光标/渲染抖动
                },
            };
            // hover：背景 sand 0.18 + 图标 brown 0.85（对齐 macOS RoundedHoverButtonBody hoverOpacity 0.18 / hoverForegroundOpacity 0.85）
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0xC4, 0xB8, 0x9E)))); // sand @18%
            hover.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD9, 0x72, 0x5D, 0x42)))); // brown @85%
            // pressed：背景 sand 0.28 + 图标 brown 0.45（对齐 macOS pressedOpacity 0.28 / pressedForegroundOpacity 0.45）
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xC4, 0xB8, 0x9E)))); // sand @28%
            pressed.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x73, 0x72, 0x5D, 0x42)))); // brown @45%
            style.Triggers.Add(hover);
            style.Triggers.Add(pressed);
            return style;
        }

        // ---- 标签页（pager：固定 minus | 横滑标签区 | 固定 plus）----
        private void RefreshTabs(bool scrollToEnd = false)
        {
            _tabsPanel.Children.Clear();

            foreach (var tab in _store.Tabs)
            {
                var isSelected = tab.Id == _store.ActiveTabId;
                // 圆点：恒棕 #794f27，未选中=空心圆（对齐 macOS circle）/ 选中=实心圆（circle.fill），
                // 与文字间距恒定 5（对齐 macOS HStack spacing:5，选中/未选中一致）
                var dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = isSelected
                        ? (System.Windows.Media.Brush)new SolidColorBrush(Color.FromRgb(0x79, 0x4F, 0x27))
                        : System.Windows.Media.Brushes.Transparent,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x79, 0x4F, 0x27)),
                    StrokeThickness = isSelected ? 0 : 1.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0), // 间距恒定 5（对齐 macOS spacing:5）
                };
                var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(dot);
                // 标题：默认字体；选中白色 / 未选棕 #794f27（对齐 macOS tabActiveText/tabText）
                content.Children.Add(new TextBlock
                {
                    Text = _store.Title(tab.Id),
                    FontSize = 12,
                    FontWeight = isSelected ? FontWeights.Bold : FontWeights.Medium,
                    Foreground = new SolidColorBrush(Color.FromRgb(isSelected ? (byte)0xFF : (byte)0x79,
                        isSelected ? (byte)0xFF : (byte)0x4F, isSelected ? (byte)0xFF : (byte)0x27)),
                    MaxWidth = 104,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                var chip = new Button
                {
                    Content = content,
                    Height = 26,
                    Padding = new Thickness(10, 0, 10, 0),
                    // 注意：不能设本地 Background——WPF 本地值优先级高于 style setter，
                    // 会导致选中 sea 蓝背景被覆盖（已踩坑）。背景完全由 TabPillButtonStyle 控制。
                    BorderThickness = new Thickness(0),
                    Cursor = _handCursor ?? Cursors.Hand,
                    Tag = tab.Id,
                    ToolTip = _store.FullTitle(tab.Id),
                    // 对齐 macOS TabPillButtonStyle：hover 薄荷绿 10% / 选中 sea 蓝
                    Style = TabPillButtonStyle(isSelected),
                };

                // 选中态装饰：白色 0.14 描边 + 底部阴影 + LeafBadge 叶子（右上角）
                if (isSelected)
                {
                    // 描边直接用 chip 的 BorderBrush/BorderThickness（style 同源，不再额外叠 Border）
                    chip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)); // 白 0.14
                    chip.BorderThickness = new Thickness(1);
                    // 底部阴影（棕 0.08、y:3、radius 0.5）
                    chip.Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Color.FromRgb(0x3D, 0x34, 0x28),
                        Opacity = 0.08,
                        BlurRadius = 0.5,
                        ShadowDepth = 3,
                        Direction = 270,
                    };

                    // 叶子徽标：独立 Grid 包裹（只包叶子，chip 尺寸不受影响）
                    var leaf = LoadAssetImage("leaf.png");
                    FrameworkElement leafImg;
                    if (leaf != null)
                    {
                        leafImg = new Image { Source = leaf, Width = 18, Height = 18 };
                    }
                    else
                    {
                        leafImg = new TextBlock { Text = "🍃", FontSize = 13 };
                    }
                    leafImg.IsHitTestVisible = false;
                    leafImg.HorizontalAlignment = HorizontalAlignment.Right;
                    leafImg.VerticalAlignment = VerticalAlignment.Top;
                    // 叶子右移 6px（露头骑角）：Margin 0 时叶子贴 chip 右缘会遮住文字右端
                    // （离屏实测：文字右缘 x67，叶子绿 x51..61 → 重叠 8px）。
                    // 负 Margin right -6 → 叶子右缘超出 chip 右缘 6px，绿色内容移到文字右侧
                    // （实测叶子绿 x70..80 vs 文字右缘 67，3px 间隙不遮）。
                    // 防裁剪：ScrollViewer 加 Padding right 8（见 _tabsScroll 构建处），
                    // 叶子露头 6px 在标签滚动到最右时仍落在视口内不被裁。
                    leafImg.Margin = new Thickness(0, 0, -6, 0);

                    var shell = new Grid
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    shell.Children.Add(chip);
                    shell.Children.Add(leafImg);
                    _tabsPanel.Children.Add(shell);
                }
                else
                {
                    _tabsPanel.Children.Add(chip);
                }

                chip.Click += async (s, _) =>
                {
                    await SaveEditorStateAsync();
                    var id = (Guid)((Button)s!).Tag;
                    _store.SelectTab(id);
                    await LoadTabAsync(id);
                    RefreshTabs();
                };
                chip.ContextMenu = BuildTabMenu(tab.Id, _store.Tabs.Count);
            }

            // 减号可用性随标签数更新（按钮固定于 pager 左端，不随滚动消失）
            if (_minusButton != null) _minusButton.IsEnabled = _store.Tabs.Count > 1;

            // 仅新建标签时滚动到末尾（新标签在末尾，需可见）。
            // ⚠️ 不能无条件 ScrollToRightEnd：点击切换/删除/恢复都会调 RefreshTabs，
            // 用户滑动到前面点第一个标签会被强制拉回末尾（实测反馈"点第一个又跳回最后"）。
            // 点击切换时被点击标签必然在视口内，保持当前视口即可（对齐 macOS scrollTo 选中标签语义）。
            if (_tabsScroll != null && scrollToEnd)
                _tabsScroll.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => _tabsScroll!.ScrollToRightEnd()));
        }

        /// <summary>
        /// 标签胶囊样式（对齐 macOS TabPillButtonStyle）：默认透明底 hover 薄荷绿 10%，
        /// 选中 = 当前主题强调色（seaBlue ↔ treeGreen）。背景由模板内 Border 绘制（含圆角绑定），
        /// hover 触发背景色切换。实例方法：读 ThemeAccentColor（随 ThemeButton 切换）。
        /// </summary>
        private Style TabPillButtonStyle(bool isSelected)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            // ⚠️ 圆角不能走附加属性绑定（同 ToolbarButtonStyle：SetBinding 附加属性字符串路径
            // 在 FrameworkElementFactory 模板中实测不生效）。标签胶囊统一圆角 13（两端半圆）直设。
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            var style = new Style(typeof(Button))
            {
                Setters =
                {
                    new Setter(Button.TemplateProperty, template),
                    new Setter(Button.BackgroundProperty, System.Windows.Media.Brushes.Transparent),
                    new Setter(Button.BorderThicknessProperty, new Thickness(0)),
                    new Setter(Button.FocusableProperty, false),
                },
            };
            // 未选中：hover 薄荷绿 0.10（对齐 macOS green.opacity(0.10)）
            if (!isSelected)
            {
                var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
                hover.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0x19, 0xC8, 0xB9)))); // green #19c8b9 @10%
                style.Triggers.Add(hover);
            }
            else
            {
                // 选中背景 = 当前主题强调色（seaBlue ↔ treeGreen，随 ThemeButton 切换，RefreshTabs 重建生效）
                style.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(ThemeAccentColor)));
            }
            return style;
        }

        private ContextMenu BuildTabMenu(Guid id, int tabCount)
        {
            var menu = new ContextMenu();
            var delete = new WpfMenuItem { Header = "删除这条笔记", Tag = id };
            delete.IsEnabled = tabCount > 1;
            delete.Click += async (s, _) =>
            {
                await SaveEditorStateAsync();
                _store.RemoveTab((Guid)((WpfMenuItem)s!).Tag);
                await LoadTabAsync(_store.ActiveTabId);
                RefreshTabs();
            };
            menu.Items.Add(delete);

            if (_store.CanRestoreDeletedNote)
            {
                var restore = new WpfMenuItem { Header = "恢复已删除笔记" };
                restore.Click += async (_, _) =>
                {
                    _store.RestoreLastDeletedTab();
                    await LoadTabAsync(_store.ActiveTabId);
                    RefreshTabs();
                };
                menu.Items.Add(restore);
            }
            return menu;
        }

        private async Task SaveEditorStateAsync()
        {
            var md = await _editor.GetHtmlAsync();
            var (f, t) = await _editor.GetSelectionAsync();
            _store.UpdateText(md);
            _store.UpdateSelection(_store.ActiveTabId, f, t);
        }

        private async Task LoadTabAsync(Guid id)
        {
            _loadingTab = true;
            var tab = _store.Tabs.First(t => t.Id == id);
            var (s, l) = _store.SelectionRange(id);
            await _editor.SetHtmlAsync(tab.Text, s, s + l);
            _loadingTab = false;
        }

        // ---- 托盘（对应 macOS 菜单栏 status item）----
        private void BuildTray()
        {
            _trayIcon = new NotifyIcon
            {
                Text = "随手记",
                Icon = LoadTrayIcon(),  // 应用图标 icon-design（用户 2026-08-04 要求任务栏/托盘图标换新）
                Visible = true,
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("新建笔记", null, async (_, _) => await CreateNoteAsync());
            menu.Items.Add("显示笔记", null, (_, _) => { _pinOpenAt = DateTime.Now; _pinOpen = true; Expand(animate: true); });
            menu.Items.Add("隐藏笔记", null, (_, _) => Collapse());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出随手记", null, (_, _) => Close());
            _trayIcon.ContextMenuStrip = menu;
            // 双击应用图标 → 面板展开（用户 2026-08-04：移除单击展开，保留双击打开）
            _trayIcon.DoubleClick += (_, _) => { _pinOpenAt = DateTime.Now; _pinOpen = true; Expand(animate: true); };
        }

        /// <summary>应用图标（icon-design.ico，用户 2026-08-04 要求任务栏/托盘图标换 icon-design）</summary>
        private static System.Drawing.Icon LoadTrayIcon()
        {
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon-design.ico");
            return System.IO.File.Exists(icoPath) ? new System.Drawing.Icon(icoPath) : DrawFallbackIcon();
        }

        /// <summary>图标缺失兜底：白 ♪（原 DrawTrayIcon 逻辑）</summary>
        private static System.Drawing.Icon DrawFallbackIcon()
        {
            using var bmp = new System.Drawing.Bitmap(16, 16);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                using var font = new System.Drawing.Font("Segoe UI Symbol", 10, System.Drawing.FontStyle.Bold);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.DrawString("\u266A", font, brush, 1, 0);
            }
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        public async Task CreateNoteAsync()
        {
            try { await SaveEditorStateAsync(); } catch { } // 编辑器未就绪时保存走缓存，不阻塞新建
            _store.AddTab();
            await LoadTabAsync(_store.ActiveTabId);
            RefreshTabs(scrollToEnd: true); // 新建标签在末尾 → 滚动到末尾可见
            _pinOpenAt = DateTime.Now;
            _pinOpen = true; // 点击新建展开：不因鼠标位置自动收起
            Expand(animate: true);
        }

        // ================= 状态机 =================
        private bool _wasInActivationZone;
        private long _mouseEventCount;
        private double _dpiScale = 1.0;

        // ---- 常量判定区域（与窗口状态完全解耦）----
        // 窗口始终水平居中（Top=0，Left 由 SetProgress 保持居中），因此胶囊/面板的物理中心
        // = 屏幕物理中心，是常量。激活区/停留区只由鼠标坐标落在哪个常量区域决定，
        // 与窗口 Width/Height/Left 属性、HWND 实际位置、动画进度全部无关。
        // 根治方案：此前判定区域=窗口当前尺寸，窗口 resize 动画期间属性/HWND/DWM 异步滞后
        // → 判定错位 → 展开↔收起死循环（面板乱舞）。
        private double PhysCenterX => GetSystemMetrics(SM_CXSCREEN) / 2.0;

        private void OnMouseMoved(int x, int y)
        {
            _mouseEventCount++;
            bool inZone = IsPointerInActivationRegion(x, y);
            if (inZone != _wasInActivationZone)
            {
                _wasInActivationZone = inZone;
                Log($"MOUSE {(inZone ? "ENTER" : "LEAVE")} activation zone at ({x},{y})");
            }
            if (!_isExpanded)
            {
                // hover 模式：进入激活区即展开；click 模式：仅点击展开（见 OnGlobalMouseUp）
                if (!_clickTrigger && inZone) { _pinOpen = false; Expand(animate: true); }
            }
            else
            {
                // 弹窗打开中：禁止任何自动收起（macOS isModalPresented=true 同语义）
                if (_modalOpen) { _collapseTimer.Stop(); return; }
                // 点击展开固定（托盘/菜单）：鼠标位置不触发收起（用户 2026-08-04：点新建笔记后移开鼠标面板被关）
                if (_pinOpen) return;
                if (IsPointerInStayRegion(x, y)) _collapseTimer.Stop();
                else if (!_collapseTimer.IsEnabled) _collapseTimer.Start();
            }
        }

        private void OnGlobalMouseUp()
        {
            var p = CursorPos();
            // click 触发模式：在激活区内点击 → 展开（hover 模式此分支不生效）
            if (!_isExpanded && _clickTrigger && IsPointerInActivationRegion(p.x, p.y))
            {
                _pinOpen = false;
                Expand(animate: true);
                return;
            }
            // 点击面板外收起（含点击展开固定态）：但抑制「点托盘展开」那一次的 MouseUp（展开后 400ms 内），
            // 否则点托盘图标展开的同一记 MouseUp 会立即折叠面板（2026-08-04 用户反馈"面板不消失"根因）
            if (_isExpanded && !_modalOpen && !IsPointerInStayRegion(p.x, p.y))
            {
                if (_pinOpen && (DateTime.Now - _pinOpenAt).TotalMilliseconds < 400) return;
                _ = SaveEditorStateAsync();
                Collapse();
            }
        }

        private bool IsPointerInActivationRegion(int x, int y)
        {
            // 激活区 = 胶囊固定区域（紧凑形态视觉范围，顶部中央 210×36 物理），不随窗口展开变化
            double halfW = CompactWidth / 2.0 * _dpiScale;
            double h = CompactHeight * _dpiScale;
            return Math.Abs(x - PhysCenterX) <= halfW
                && y >= 0
                && y <= h;
        }

        private bool IsPointerInStayRegion() { var p = CursorPos(); return IsPointerInStayRegion(p.x, p.y); }

        private bool IsPointerInStayRegion(int x, int y)
        {
            // 停留区 = 面板固定区域（展开形态范围，顶部中央 540×480 物理）+ margin，常量
            double margin = StayMargin * _dpiScale;
            double halfW = ExpandedWidth / 2.0 * _dpiScale;
            double h = ExpandedHeight * _dpiScale;
            return Math.Abs(x - PhysCenterX) <= halfW + margin
                && y >= -margin
                && y <= h + margin;
        }

        private static (int x, int y) CursorPos()
        {
            GetCursorPos(out var p);
            return (p.x, p.y);
        }

        // ================= 形态切换 =================
        // 设计决策（2026-07-31，从产品目标出发）：
        // 窗口常驻 540×480（WebView2 需要大/可见窗口初始化），形态切换 = SetWindowRgn
        // 窗口区域裁剪（紧凑=胶囊形状 / 展开=全窗口）+ 内容透明度淡入（WPF 动画引擎）。
        // 窗口尺寸永不变化 → 无 HWND 抖动、无渲染回调交错（乱跳根因消除）；
        // 区域外系统级裁剪：不可见 + 点击自动穿透（低打扰）。
        private void Expand(bool animate)
        {
            if (_isExpanded) return;
            _isExpanded = true;
            _collapseTimer.Stop();
            // WebView2 显隐从 SetProgress 解耦（第一性原理，用户反馈"面板打开卡顿"）：
            // WebView2 是子 HWND，不参与 _expandedContent 的 Opacity 混合——动画期间若已全显，
            // ①airspace 盖在淡入内容上突兀 ②浏览器重绘大文档与淡入动画抢帧 → 卡顿。
            // 修复：动画期间隐藏，淡入完成后再显示（与删除弹窗错峰同机制）。
            _editor.SetVisible(false);
            _editorVisible = false;
            SetProgress(1); // 瞬时展开（区域全窗口 + 内容显示）
            if (animate)
                FadeContentOpacity(0, 1, 0.2, onCompleted: () =>
                {
                    if (_isExpanded)
                    {
                        _editor.SetVisible(true);
                        _editorVisible = true;
                        _ = _editor.FocusAsync();
                    }
                });
            else
            {
                _editor.SetVisible(true);
                _editorVisible = true;
                _ = _editor.FocusAsync();
            }
        }

        private void Collapse()
        {
            if (!_isExpanded) return;
            // 收起面板时关闭删除确认弹窗（macOS onChange(showDeleteConfirm)：收起即关）。
            // 不恢复编辑器可见性——马上 SetProgress(0) 会隐藏编辑器。
            if (_modalOpen)
            {
                _typeTimer?.Stop();
                _modalOpen = false;
                if (_modalOverlay != null) _modalOverlay.Visibility = Visibility.Collapsed;
            }
            _isExpanded = false;
            _pinOpen = false; // 收起后解除点击固定（用户手动收起后恢复正常 hover 语义）
            // 先隐藏 WebView2 再收起（避免收起瞬间浏览器重绘与窗口切换叠加卡顿）
            _editor.SetVisible(false);
            _editorVisible = false;
            SetProgress(0); // 瞬时收起（区域胶囊形状 + 内容隐藏）
        }

        private void FadeContentOpacity(double from, double to, double seconds, Action? onCompleted = null)
        {
            _expandedContent.BeginAnimation(UIElement.OpacityProperty, null);
            var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            if (onCompleted != null) anim.Completed += (_, _) => onCompleted();
            _expandedContent.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void SetProgress(double progress)
        {
            // 窗口尺寸固定（主窗口 540×480 / 胶囊窗口 210×36，创建时设定，永不变化——乱跳机制不回归）；
            // 形态切换 = 双窗口显隐 + 内容透明度淡入
            double contentOpacity = Math.Clamp((progress - FadeInStart) / (FadeInEnd - FadeInStart), 0, 1);
            _expandedContent.Opacity = contentOpacity;

            // 双窗口显隐：紧凑 = 胶囊窗口（分层，抗锯齿胶囊圆角）/ 展开 = 主窗口（DWM 圆角 + WebView2）。
            // WPF Show/Hide 只改显示状态，不动尺寸/位置/Z 序
            bool expanded = progress >= 0.5;
            if (expanded != _windowExpanded)
            {
                _windowExpanded = expanded;
                if (expanded) Show();
                else Hide();
                // 胶囊形态已移除（用户 2026-08-04：去掉胶囊条，改为点击任务栏图标展开面板）
            }
            // WebView2 显隐不再由 SetProgress 驱动——由 Expand/Collapse 控制
            // （动画期间隐藏、完成后再显示，避免 airspace 突兀 + 浏览器重绘抢帧）
        }

        private bool _editorVisible;

        private Border? panel;

        // ================= 自检 =================
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            ReportWindowStyle();
            ApplyDwmRoundedCorners();
            // 性能取证：渲染 Tier（0/1=软件光栅化嫌疑，2=硬件加速）
            Log($"PERF renderTier={(int)RenderCapability.Tier >> 16} (2=hardware)");

            // 挂水平滚轮 hook（WM_MOUSEHWHEEL）——WPF 无托管事件，需窗口消息钩子
            _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(WndProcHook);

            // 初始化 Tiptap 编辑器。主窗口保持 540×480 可见直到编辑器就绪（WebView2 页面脚本
            // 需要非极小/可见窗口才会执行，实测 210x36 与 Opacity=0 下模块不加载），就绪后进入紧凑态。
            await _editor.InitAsync();
            for (int i = 0; i < 100 && !_editor.IsReady; i++) await Task.Delay(100);
            Log("Editor ready=" + _editor.IsReady);
            await LoadTabAsync(_store.ActiveTabId); // 内容加载在可见窗口执行最稳
            SetProgress(0); // 紧凑态：主窗口隐藏 + 胶囊窗口显示
            // 胶囊窗口 EXSTYLE 断言在紧凑态（胶囊可见、HWND 有效）读取；展开后胶囊 Hide 读不到
            Log($"CAPSULE EXSTYLE=0x{_capsule.ExStyle:X} (expect LAYERED|TOOLWINDOW|TOPMOST)");

            // 初始化完成后再安装全局钩子（防初始化期误触发 Expand）
            _hookInstalled = _hook.Install();
            Log("MouseHook installed=" + _hookInstalled);
            // 全局键盘钩子：Esc 收起面板（焦点在 WebView2 时窗口 KeyDown 收不到按键）
            _keyHook = new GlobalKeyboardHook();
            _keyHook.EscapePressed += () => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (_isExpanded) Collapse(); // 仅自己展开时响应，不干扰其他应用
            }));
            if (_keyHook.Install()) Log("KeyboardHook installed=True");

            if (!_selfTest)
            {
                Log("Running. Move cursor to top-center (activation zone) to expand. Esc to quit.");
                KeyDown += (_, ke) =>
                {
                    if (ke.Key == Key.Escape) Collapse();
                };
                return;
            }

            Log("Self-test: auto expand -> smoke -> collapse -> verify");
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Expand(animate: true)));
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                await Task.Delay(400); // 等展开淡入完成
                // 主窗口 DWM 圆角读回断言
                Log($"SELFTEST main DWM cornerPreference={ReadCornerPreference()} (expect 2=ROUND)");
                // 编辑器冒烟：此刻主窗口已完成 Hide→Show 一轮，editorExists=True 即验证
                // 双窗口显隐后 WebView2 状态保留（DOM 不丢、编辑器可交互）
                await RunToolbarSmokeTestAsync();
                Log($"SELFTEST windowState: mainVisible={IsVisible} capsuleVisible={_capsule.IsVisible}");
                Collapse();
            }));

            // 存储往返验证：写文本 → Flush → 新实例读回
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _store.UpdateText("# Self-test note\nround trip");
                _store.Flush();
                var reloaded = new NoteStore();
                Log($"STORAGE round-trip ok={reloaded.Text.Contains("round trip")} tabs={reloaded.Tabs.Count}");
            }));

            var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9.0) };
            exitTimer.Tick += (_, _) =>
            {
                exitTimer.Stop();
                Log("=== selftest done, exiting ===");
                Close();
            };
            exitTimer.Start();
        }

        private void ReportWindowStyle()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            bool topmost = (ex & WS_EX_TOPMOST) != 0;
            bool toolwindow = (ex & WS_EX_TOOLWINDOW) != 0;
            if (!toolwindow)
            {
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
                ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
                toolwindow = (ex & WS_EX_TOOLWINDOW) != 0;
            }
            Log($"EXSTYLE=0x{ex:X} WS_EX_TOPMOST={topmost} WS_EX_TOOLWINDOW={toolwindow}");
        }

        /// <summary>DWM 圆角（Windows 11）：抗锯齿窗口边缘，替代 SetWindowRgn 二值区域裁剪</summary>
        private void ApplyDwmRoundedCorners()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
                Log("DWM rounded corners applied (DWMWCP_ROUND, radius=8px fixed by DWM)");
            }
            catch (Exception ex)
            {
                Log("DWM corner apply failed: " + ex.Message);
            }
        }

        /// <summary>读回 DWM 圆角偏好（selftest 断言用，expect 2=ROUND）</summary>
        private string ReadCornerPreference()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return "no-hwnd";
                DwmGetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out int pref, sizeof(int));
                return pref.ToString();
            }
            catch (Exception ex)
            {
                return "err:" + ex.Message;
            }
        }

        private void RunSelfTestChecks()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            bool topmost = (ex & WS_EX_TOPMOST) != 0;
            bool toolwindow = (ex & WS_EX_TOOLWINDOW) != 0;
            Log($"CHECK topmost={topmost} toolwindow={toolwindow} hook={_hookInstalled}");
        }

        // ---- 编辑器桥接冒烟（Tiptap 命令正确性由框架保证；主应用验证：内容往返一致 + 命令下发不抛错）----
        private async Task RunToolbarSmokeTestAsync()
        {
            // 等待编辑器真正就绪（NavigationCompleted 后就绪轮询完成）
            for (int i = 0; i < 50 && !_editor.IsReady; i++) await Task.Delay(100);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = new System.Collections.Generic.List<string>();
            try
            {
                // 编辑器真实存在性（防 pending 假阳性）
                var exists = await _editor.EditorExistsAsync();
                results.Add($"editorExists={exists}");

                // 内容往返：HTML → Tiptap → HTML 关键内容保留
                const string sample = "<h2>标题</h2><p>这是<strong>粗体</strong>、<s>删除线</s>、<code>代码</code></p><ul data-type=\"taskList\"><li data-type=\"taskItem\" data-checked=\"false\"><p>待办</p></li></ul><blockquote><p>引用</p></blockquote>";
                await _editor.SetHtmlAsync(sample);
                await Task.Delay(300); // 等 Tiptap 解析 + onUpdate 事件
                var md = await _editor.GetHtmlAsync();
                Log("TOOLBAR md=" + (md.Length > 200 ? md[..200] : md));
                results.Add($"roundtrip={md.Contains("粗体") && md.Contains("删除线") && md.Contains("待办")}");

                // 命令下发不抛错（10 个命令逐个验证桥接）
                int ok = 0;
                foreach (MarkdownCommand cmd in Enum.GetValues<MarkdownCommand>())
                {
                    try
                    {
                        await _editor.ApplyCommandAsync(cmd);
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        results.Add($"cmdFail={cmd}:{ex.Message}");
                    }
                }
                results.Add($"commands={ok}/10");

                // 选区往返
                var (f, t) = await _editor.GetSelectionAsync();
                results.Add($"selection={f >= 0 && t >= f}");
            }
            catch (Exception ex)
            {
                results.Add($"exception={ex.Message}");
            }
            sw.Stop();
            Log($"TOOLBAR bench: bridge in {sw.ElapsedMilliseconds}ms");
            Log("TOOLBAR " + string.Join(" ", results));
            Log($"TOOLBAR all-ok={results.All(r => r.EndsWith("=True") || r.EndsWith("/10"))}");
        }

        private static void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { }
            Console.WriteLine(line);
        }
    }
}
