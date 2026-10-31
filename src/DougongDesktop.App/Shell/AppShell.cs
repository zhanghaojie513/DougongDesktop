using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DougongDesktop.App.Services;
using DougongDesktop.Ui.Core;
using TKCode.Arc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 应用外壳：三栏布局 + 五个 slot。
/// </summary>
/// <remarks>
/// <b>结构照 dsh 的分层，但渲染用 Avalonia 原生控件</b>
/// （详见 vault《Dougong-Desktop-Slot机制规格》）。
/// <para>
/// 外壳只负责"把这些位置摆好、把占位者的模板塞进去"，不知道每块内容是什么。
/// 具体内容由内置模块或（将来）UI 插件注册。
/// </para>
/// </remarks>
public sealed class AppShell : UserControl
{
    private readonly SlotRegistry _slots;
    private readonly AppShellViewModel _vm;

    /// <summary>
    /// 初始尺寸算一次。
    /// </summary>
    /// <remarks>
    /// <b>不能只靠 <c>SizeChanged</c></b>：控件被放进窗口时若尺寸已确定，
    /// 事件可能不触发，三栏会停在初值 280/0 上——表现为"右栏明明有插件却不见了"。
    /// 测量完成后补算一次是最稳的。
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 布局还没跑时 Bounds 是 0，延到本帧布局结束再算
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // 内置模块在 AppShell 构造**之后**才注册，所以 slot 变化事件
            // 在初始这一轮不会覆盖到"右栏现在有占位者"这件事——
            // 必须在这里主动探一次，否则右栏永远是空的。
            _rightbarShown = _slots.Resolve(ReservedSlots.RightBar) is not null;

            if (_rightbarShown && _rightbarWidth <= 0 && Bounds.Width > 0)
                _rightbarWidth = Columns.RightbarDefault(Bounds.Width);

            ApplyColumns();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>界面偏好。构造时读一次，拖拽结束后整份写回。</summary>
    /// <summary>界面偏好的存储后端。桌面用文件，web 端换实现即可。</summary>
    private readonly ISettingsStore _store = new Services.FileSettingsStore();

    private AppSettings _settings;

    public AppShell(SlotRegistry slots, AppShellViewModel vm)
    {
        _slots = slots;
        _vm = vm;
        _settings = AppSettings.Load(_store);

        Content = Build();
        DataContext = vm;

        // 框架宽度变了就重算三栏。dsh 用 ResizeObserver + rAF，
        // Avalonia 直接用 SizeChanged——不需要节流，布局本身就是按帧做的。
        SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0) return;

            // 首次测量：右栏若有占位者，按 45% 给个初始宽
            if (_rightbarWidth <= 0 && _rightbarShown)
                _rightbarWidth = Columns.RightbarDefault(e.NewSize.Width);

            ApplyColumns();
        };

        // slot 变化后重取模板：ContentControl 不知道注册表内部变了。
        // 只影响 single/keyed——list 由 ObservableCollection 自动通知。
        slots.Changed += slotKey =>
        {
            if (_contentBySlot.TryGetValue(slotKey, out var content))
            {
                // 必须把 Content 清掉再设回：只换 ContentTemplate 时 Avalonia 会复用
                // 已有的内容实例，模板压根不会重新 Build——实测 Sidebar 一直是 0 个就是这原因。
                var key = content.Content;
                content.Content = null;
                content.ContentTemplate = slots.Resolve(slotKey, key as string);
                content.Content = key;
            }

            // 右栏：有占位者才要轨道（空栏不该白占宽度）。
            // 首次有内容时用 dsh 的 45% 默认宽。
            if (slotKey == ReservedSlots.RightBar)
            {
                var hasOccupant = slots.Resolve(slotKey) is not null;
                if (hasOccupant && !_rightbarShown && Bounds.Width > 0)
                    _rightbarWidth = Columns.RightbarDefault(Bounds.Width);

                _rightbarShown = hasOccupant;
                ApplyColumns();
            }
        };
    }

    /// <summary>single/keyed slot 的 ContentControl，用于响应注册表变化。</summary>
    private readonly Dictionary<string, ContentControl> _contentBySlot = new(StringComparer.Ordinal);

    /// <summary>底部行容器，供诊断读取实际尺寸。</summary>
    public Control? BottomBar { get; private set; }

    /// <summary>引擎状态条（进程级状态 + 模型名）。</summary>
    public EngineStatusBar StatusBar { get; } = new();

    /// <summary>消息流列表，供诊断读取实际尺寸。</summary>
    public Control? ConversationList { get; private set; }

    /// <summary>三栏 Grid。列宽由 <see cref="Columns.Compute"/> 算出后写进这里。</summary>
    private Grid _columns = null!;

    /// <summary>侧边栏列的承载控件。</summary>
    private Grid _sidebarHost = null!;

    /// <summary>右栏列的承载控件。</summary>
    private Control _rightbarHost = null!;

    /// <summary>中栏承载控件（带左上圆角）。</summary>
    private Border _centerHost = null!;

    /// <summary>
    /// 窗口标题栏。
    /// </summary>
    /// <remarks>
    /// <b>与侧边栏同色</b>：dsh 的 <c>AppFrame.module.css</c> 把标题栏
    /// (<c>.frame::before</c>) 与框架底色都设成 <c>--dsw-specific-sidebar-fill</c>。
    /// 不同色会在窗口顶部留一条突兀的分界。
    /// <para>
    /// <b>可拖拽</b>：Avalonia 里用 <c>BeginMoveDrag</c>（对应 CSS 的
    /// <c>-webkit-app-region: drag</c>）。
    /// </para>
    /// </remarks>
    public Border Titlebar { get; } = new();

    /// <summary>侧边栏偏好宽度（像素）。0 表示收起。</summary>
    private double _sidebarWidth = Columns.SidebarDefault;

    /// <summary>右栏偏好宽度（像素）。0 表示不要轨道。</summary>
    private double _rightbarWidth;

    /// <summary>右栏当前是否要显示（有占位者且未手动关闭）。</summary>
    private bool _rightbarShown;

    /// <summary>
    /// 按当前框架宽度重算三栏。
    /// </summary>
    /// <remarks>
    /// <b>求解顺序由 <see cref="Columns.Compute"/> 保证，这里只负责把结果写进 Grid</b>。
    /// 中栏是 <c>*</c>，所以它的宽度永远是"剩余"，不需要显式设——
    /// 设了反而会和求解器的结果打架。
    /// </remarks>
    private void ApplyColumns()
    {
        if (_columns is null) return;

        var viewport = Bounds.Width;
        if (viewport <= 0) return;

        // 响应式（dsh AppFrame 的同名判断）：
        //  - 视口 < 1024：侧边栏自动收起到轨道（用户手动展开仍生效）
        //  - 视口 < 768：抽屉态，侧边栏不占列宽（浮层）
        var narrow = viewport < Columns.SidebarAutoCollapse;
        var drawer = viewport < Columns.SidebarDrawerBreakpoint;

        var sidebarPreference = drawer
            ? 0                                   // 抽屉态不占列宽
            : _sidebarWidth == 0 || narrow && !_narrowExpanded
                ? 0                               // 收起（含自动收起）
                : _sidebarWidth;

        var solution = Columns.Compute(
            viewport,
            sidebarPreference,
            _rightbarShown ? _rightbarWidth : 0,
            collapsedWidth: drawer ? 0 : Columns.SidebarCollapsed);

        // 收起时侧边栏自己要知道（它要切到图标轨道布局）。
        // 注意：dsh 是 56px，Arc 的 Sidebar 内建 64px 且写死——
        // 这是已知偏差，记在 docs/dsh-parity-spec.md §9.1。
        _sidebarCollapsed = sidebarPreference == 0;

        // 把收起状态传给侧边栏控件（它是双向属性，会自己播放折叠动画）
        if (_contentBySlot.TryGetValue(ReservedSlots.Sidebar, out var sidebarContent)
            && sidebarContent.Content is TKCode.Arc.Sidebar arcSidebar)
        {
            arcSidebar.IsCollapsed = _sidebarCollapsed;
        }

        _columns.ColumnDefinitions[0].Width = new GridLength(solution.Sidebar, GridUnitType.Pixel);
        _columns.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        _columns.ColumnDefinitions[2].Width = new GridLength(solution.Rightbar, GridUnitType.Pixel);

        _rightbarHost.IsVisible = solution.Rightbar > 0;
        Layout = solution;
    }

    /// <summary>最近一次解算结果，供诊断与测试读取。</summary>
    public Columns.Solution Layout { get; private set; }

    /// <summary>
    /// 展开 / 收起侧边栏。
    /// </summary>
    /// <remarks>
    /// <b>收起不等于消失</b>：dsh 收起后保留 56px 图标轨道。
    /// 把宽度设 0 会让用户失去"从哪点回去"的入口。
    /// </remarks>
    public void ToggleSidebar()
    {
        // 窄屏下切换的是 narrowExpanded，不是绝对宽度：
        // 否则"自动收起"会在每次尺寸变化后覆盖用户的手动展开
        if (IsNarrow)
        {
            _narrowExpanded = !_narrowExpanded;
            if (_narrowExpanded && _sidebarWidth == 0)
                _sidebarWidth = Columns.SidebarDefault;
        }
        else
        {
            _sidebarWidth = _sidebarWidth == 0 ? Columns.SidebarDefault : 0;
        }

        ApplyColumns();
    }

    /// <summary>右栏是否显示。</summary>
    public bool IsRightbarShown => _rightbarShown;

    /// <summary>侧边栏是否处于收起态（56px 轨道）。</summary>
    private bool _sidebarCollapsed;

    /// <summary>
    /// 窄屏下用户是否手动展开过侧边栏。
    /// </summary>
    /// <remarks>
    /// dsh 的 <c>narrowExpanded</c>：视口 &lt; 1024 时自动收起，但用户手动
    /// 展开后要尊重他的选择，不能每次尺寸变化又收回去。
    /// </remarks>
    private bool _narrowExpanded;

    /// <summary>侧边栏当前是否收起。</summary>
    public bool IsSidebarCollapsed => _sidebarCollapsed;

    /// <summary>侧边栏是否因窄屏自动收起。</summary>
    public bool IsNarrow => Bounds.Width > 0 && Bounds.Width < Columns.SidebarAutoCollapse;

    /// <summary>
    /// 折叠/展开侧边栏。
    /// </summary>
    /// <remarks>
    /// <b>只切 <see cref="ResizablePanel.IsCollapsed"/>，不销毁内容</b>：侧边栏里
    /// 有筛选词和滚动位置，销毁重建会让用户每折叠一次就丢掉当前浏览位置。
    /// </remarks>

    /// <summary>声明五个 slot。必须在任何注册之前调用。</summary>
    public static void DeclareSlots(SlotRegistry slots)
    {
        // main 是 keyed：conversation / plugins / settings 按 key 分派
        slots.Declare(ReservedSlots.Main, SlotKind.Keyed);

        // 三条边栏与覆盖层：都是 single/list
        slots.Declare(ReservedSlots.Sidebar, SlotKind.Single);
        slots.Declare(ReservedSlots.RightBar, SlotKind.Single);
        slots.Declare(ReservedSlots.Bottom, SlotKind.List);
        slots.Declare(ReservedSlots.Overlay, SlotKind.List);
    }

    private Control Build()
    {
        BuildTitlebar();

        // 三栏：侧边栏 | 主区 | 右栏
        //
        // 用 Grid 三列 + 像素求解器（Columns.Compute），不用 Arc 的 ResizablePanels。
        // 原因是 dsh 的列宽语义是**像素 + 求解顺序**，而 ResizablePanels 用百分比：
        //  - 百分比在窗口缩放时漂移，dsh 的像素不漂
        //  - dsh 的"右栏先让步、再整条消失、中栏才允许低于 400"在百分比模型里表达不了
        // 详见 docs/dsh-parity-spec.md §1。
        _columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("280,*,0"),
        };

        // 侧边栏 = slot 内容 + 固定底部的状态条。
        //
        // dsh 的侧边栏底部是固定席位（Settings 在那里），状态条跟随这个位置：
        // 进程活没活、连的哪个模型，属于"全局环境信息"，与底部导航同类。
        // 放进 shell.bottom slot 不行——那个 slot 默认是空的，且会被插件顶掉。
        _sidebarHost = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var sidebarSlot = SlotSingle(ReservedSlots.Sidebar);
        Grid.SetRow(sidebarSlot, 0);
        _sidebarHost.Children.Add(sidebarSlot);

        StatusBar.Margin = new Thickness(10, 4, 10, 8);
        Grid.SetRow(StatusBar, 1);
        _sidebarHost.Children.Add(StatusBar);

        // 侧栏分界：dsh 是 0.5px solid border-l3（.sidebarCol 的 border-right）。
        // 没有它时侧栏的 #F9FAFB 与主区白只差 2 个色阶，视觉上分不开。
        // Grid 没有边框属性，所以外面包一层 Border。
        var sidebarFrame = new Border
        {
            Child = _sidebarHost,
            BorderBrush = DshTheme.Brush(DshTheme.BorderL3),
            BorderThickness = new Thickness(0, 0, 0.5, 0),
        };

        Grid.SetColumn(sidebarFrame, 0);
        _columns.Children.Add(sidebarFrame);

        // 中栏：外面包一层 Border 做左上圆角（dsh 的 .centerCol）。
        //
        // **圆角只有 Windows 标题栏形态下才有**（dsh 的
        // `[data-windows-titlebar] .centerCol { border-radius: 16px 0 0 0 }`）。
        // 没有标题栏时不圆角——否则白色主区左上角会缺一块，
        // 露出底下侧栏的 #F9FAFB，看起来像"缝隙"。
        var center = SlotKeyed(ReservedSlots.Main);
        _centerHost = new Border
        {
            Child = center,
            CornerRadius = new CornerRadius(Columns.ContentCornerRadius, 0, 0, 0),
            Background = DshTheme.Brush(DshTheme.PageBackground),
            ClipToBounds = true,
            Margin = new Thickness(0),
        };
        Grid.SetColumn(_centerHost, 1);
        _columns.Children.Add(_centerHost);

        // 右栏
        _rightbarHost = SlotSingle(ReservedSlots.RightBar);
        Grid.SetColumn(_rightbarHost, 2);
        _columns.Children.Add(_rightbarHost);

        // 底部条：list kind，空内容不占位
        var bottom = new ItemsControl
        {
            ItemsSource = _slots.GetList(ReservedSlots.Bottom),
            ItemTemplate = new FuncDataTemplate<SlotOccupant>((o, _) =>
                o is null ? null : SafeBuild(o)),
            IsVisible = false,
        };

        // 覆盖层：命令面板、宠物、Toast
        var overlay = new ItemsControl
        {
            ItemsSource = _slots.GetList(ReservedSlots.Overlay),
            ItemTemplate = new FuncDataTemplate<SlotOccupant>((o, _) =>
                o is null ? null : SafeBuild(o)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };

        // 底部条有内容时才占位（dsh 原文："空内容不预留空间"）
        void SyncBottomVisibility() =>
            bottom.IsVisible = bottom.ItemsSource is System.Collections.IEnumerable e
                               && e.Cast<object>().Any();

        SyncBottomVisibility();
        if (bottom.ItemsSource is System.Collections.Specialized.INotifyCollectionChanged changed)
            changed.CollectionChanged += (_, _) => SyncBottomVisibility();

        // dsh 的底部行是**横跨三列的单一 slot**（shell.bottom），且默认组合不占用它，
        // 空内容不预留空间。之前我做成"左 slot + 右状态条"，那不是 dsh 的形态。
        //
        // 状态条（进程活没活 / 连的哪个模型）不放进这个 slot：
        //  - 它是框架级信息，不该由插件注册、也不该被插件顶掉
        //  - dsh 的 shell.bottom 默认是空的
        // 所以状态条改为浮在侧边栏底部（与 dsh 侧边栏的固定底部席位一致）。
        // 结构照 dsh 的 Windows 布局：
        //   row 0: 标题栏（与侧边栏同色，可拖拽）
        //   row 1: 三栏
        //   row 2: shell.bottom
        //
        // 主区左上角要圆角，所以中栏所在的那块用 Border 包一层
        // （dsh 是 `.centerCol { border-radius: 16px 0 0 0 }`）。
        var stack = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        Grid.SetRow(Titlebar, 0);
        stack.Children.Add(Titlebar);

        Grid.SetRow(_columns, 1);
        stack.Children.Add(_columns);

        Grid.SetRow(bottom, 2);
        stack.Children.Add(bottom);
        BottomBar = bottom;

        var root = new Grid { Background = DshTheme.Brush(DshTheme.SidebarFill) };
        root.Children.Add(stack);
        root.Children.Add(overlay);

        return root;
    }

    /// <summary>
    /// 建标题栏：标题文字 + 拖拽。
    /// </summary>
    /// <remarks>
    /// <b>只做 dsh 有的部分</b>：dsh 的 Windows caption row 只有一个可拖拽的
    /// 空白条（内容由宿主自绘），没有自绘最小化/最大化/关闭按钮——
    /// 那些是 Electron 的原生控件。我们保留 Avalonia 的原生窗口按钮
    /// （`ExtendClientAreaToDecorationsHint` 会让它们浮在标题栏右侧），
    /// 只负责把左边这块涂成与侧边栏一致的颜色。
    /// </remarks>
    private void BuildTitlebar()
    {
        Titlebar.Height = Columns.TitlebarHeight;
        Titlebar.Background = DshTheme.Brush(DshTheme.SidebarFill);

        // 形态照 dsh 的 Windows caption：左侧「收起侧栏图标 + 应用 + 编辑」，
        // 右侧窗口按钮。不显示项目名——dsh 那里也没有，标题信息在侧栏品牌行。
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Children =
            {
                BuildSidebarToggleButton(),
                BuildMenuButton("应用"),
                BuildMenuButton("编辑"),
            },
        };
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        var buttons = BuildWindowButtons();
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);

        Titlebar.Child = row;
    }

    /// <summary>
    /// 标题栏左侧的收起/展开侧栏按钮。
    /// </summary>
    /// <remarks>
    /// <b>dsh 的标题栏最左就是这个</b>：面板图标，点击折叠侧栏。
    /// 它不是装饰——侧栏收起后，这个按钮是唯一的恢复入口
    /// （收起态只剩 56px 轨道，轨道顶部的按钮也在，但标题栏这个永远可见）。
    /// </remarks>
    private Control BuildSidebarToggleButton()
    {
        var button = TitleBarIconButton(
            TKCode.Arc.Icons.Lucide.LucideIconKind.PanelLeft, 28);

        button.Click += (_, e) =>
        {
            e.Handled = true;
            ToggleSidebar();
        };

        return button;
    }

    /// <summary>
    /// 标题栏的菜单按钮（应用 / 编辑）。
    /// </summary>
    /// <remarks>
    /// <b>为什么自绘而不是用系统菜单栏</b>：dsh 桌面端用的是 Electron 原生菜单，
    /// 位置在标题栏左侧且与自绘 caption 同色。Avalonia 的系统菜单栏（Windows 上是
    /// 窗口下方独立一行）会多出一条，视觉上完全不是 dsh 的样子。
    /// <para>
    /// 当前只放两个入口，菜单内容是后续（P9 设置域）接的活。
    /// 先让位置与形态对上——空菜单比没有按钮更早暴露问题。
    /// </para>
    /// </remarks>
    private static Control BuildMenuButton(string label)
    {
        var button = new Avalonia.Controls.Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Height = 28,
            Padding = new Thickness(8, 0),
            Margin = new Thickness(0, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Columns.RadiusSm),
            VerticalAlignment = VerticalAlignment.Center,
        };

        button.PointerEntered += (_, _) => button.Background = DshTheme.Brush(DshTheme.Layer3);
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;

        // 菜单内容待接（P9）。先给出可见反馈，不让点击像坏了
        button.Click += (_, e) =>
        {
            e.Handled = true;
            Console.Error.WriteLine($"[ui] 菜单「{label}」尚未实现（P9 设置域）");
        };

        return button;
    }

    /// <summary>标题栏的纯图标按钮。</summary>
    private static Avalonia.Controls.Button TitleBarIconButton(
        TKCode.Arc.Icons.Lucide.LucideIconKind kind, double size)
    {
        var button = new Avalonia.Controls.Button
        {
            Content = new TKCode.Arc.Icons.Lucide.LucideIcon
            {
                Kind = kind,
                Size = 16,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Width = Columns.SidebarControlRowHeight,
            Height = Columns.SidebarControlRowHeight,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Columns.RadiusSm),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        button.PointerEntered += (_, _) => button.Background = DshTheme.Brush(DshTheme.Layer3);
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;

        return button;
    }

    /// <summary>
    /// 自绘窗口按钮：最小化 / 最大化 / 关闭。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须自绘</b>：扩展客户区后原生标题栏消失，系统按钮也一起没了。
    /// 不自绘就没法最小化或关闭窗口。
    /// <para>
    /// <b>按钮尺寸 46×40</b>：Windows 11 原生 caption button 的尺寸。
    /// 尺寸不同会让"这个窗口看起来不像 Windows 程序"。
    /// </para>
    /// <para>
    /// <b>关闭按钮悬停变红</b>：那是 Windows 的既有习惯，破坏它会让用户
    /// 在误点前多犹豫一次。
    /// </para>
    /// </remarks>
    private Control BuildWindowButtons()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(TitleButton("—", 12, window => window.WindowState = WindowState.Minimized));

        panel.Children.Add(TitleButton("□", 12, window => window.WindowState =
            window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));

        panel.Children.Add(TitleButton("✕", 12, window => window.Close(), isClose: true));

        return panel;
    }

    private static Avalonia.Controls.Button TitleButton(string glyph, double fontSize, Action<Window> action, bool isClose = false)
    {
        var button = new Avalonia.Controls.Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = fontSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Width = 46,
            Height = Columns.TitlebarHeight,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
        };

        // 悬停态：普通按钮是淡灰，关闭是红底白字
        if (isClose)
        {
            button.PointerEntered += (_, _) =>
                button.Background = DshTheme.Brush(DshTheme.Danger);
        }
        else
        {
            button.PointerEntered += (_, _) =>
                button.Background = DshTheme.Brush(DshTheme.Layer3);
        }

        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;

        button.Click += (_, e) =>
        {
            // 不要让点击冒泡到 Titlebar——那会触发拖拽
            e.Handled = true;

            if (TopLevel.GetTopLevel(button) is Window window) action(window);
        };

        return button;
    }

    /// <summary>single slot：ContentControl + ContentTemplate。</summary>
    private Control SlotSingle(string slotKey)
    {
        var control = new ContentControl
        {
            Content = slotKey,
            ContentTemplate = _slots.Resolve(slotKey),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };

        _contentBySlot[slotKey] = control;
        return control;
    }

    /// <summary>keyed slot：按当前选中的面板 key 取模板。</summary>
    private Control SlotKeyed(string slotKey)
    {
        var control = new ContentControl
        {
            Content = _vm.ActivePanel,
            ContentTemplate = _slots.Resolve(slotKey, _vm.ActivePanel),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };

        _contentBySlot[slotKey] = control;

        // 面板切换时重取模板（keyed 按 ActivePanel 分派）
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.Property != AppShellViewModel.ActivePanelProperty) return;
            control.Content = _vm.ActivePanel;
            control.ContentTemplate = _slots.Resolve(slotKey, _vm.ActivePanel);
        };

        return control;
    }

    /// <summary>安全构建占位者内容：异常隔离在 slot 层。</summary>
    private Control SafeBuild(SlotOccupant occupant)
    {
        var built = _slots.BuildSafely(occupant.Template,
            (id, ex) => System.Diagnostics.Debug.WriteLine($"[slot] {occupant.Id} 构建失败：{ex.Message}"),
            DataContext);

        return built ?? new TextBlock
        {
            Text = $"{occupant.Id} 加载失败",
            Opacity = 0.5,
            Margin = new Avalonia.Thickness(8),
        };
    }
}

/// <summary>外壳的视图模型：只持有"当前选中哪个面板"这类界面状态。</summary>
public sealed class AppShellViewModel : AvaloniaObject
{
    public static readonly AvaloniaProperty<string?> ActivePanelProperty =
        AvaloniaProperty.Register<AppShellViewModel, string?>(nameof(ActivePanel),
            defaultValue: ReservedSlots.Conversation);

    /// <summary>
    /// 当前 main 面板的 key。
    /// </summary>
    /// <remarks>
    /// <b><see cref="ReservedSlots.Conversation"/> 是保留 key</b>：切到插件页再切回来，
    /// 会话实例保持存活（不销毁、不重建）。做错会让用户丢掉会话上下文。
    /// </remarks>
    public string? ActivePanel
    {
        get => (string?)GetValue(ActivePanelProperty);
        set => SetValue(ActivePanelProperty, value);
    }
}
