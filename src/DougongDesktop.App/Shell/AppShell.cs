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

    /// <summary>界面偏好。构造时读一次，拖拽结束后整份写回。</summary>
    private AppSettings _settings = AppSettings.Load();

    public AppShell(SlotRegistry slots, AppShellViewModel vm)
    {
        _slots = slots;
        _vm = vm;

        Content = Build();
        DataContext = vm;

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

            // 右栏：有内容才展开，没内容保持折叠（空栏不该白占宽度）
            if (slotKey == ReservedSlots.RightBar && _rightBarPanel is not null)
                _rightBarPanel.IsCollapsed = slots.Resolve(slotKey) is null;
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

    /// <summary>右栏面板，用于"有内容时自动展开"。</summary>
    private ResizablePanel? _rightBarPanel;

    /// <summary>侧边栏面板，用于命令面板的折叠/展开。</summary>
    private ResizablePanel? _sidebarPanel;

    /// <summary>
    /// 折叠/展开侧边栏。
    /// </summary>
    /// <remarks>
    /// <b>只切 <see cref="ResizablePanel.IsCollapsed"/>，不销毁内容</b>：侧边栏里
    /// 有筛选词和滚动位置，销毁重建会让用户每折叠一次就丢掉当前浏览位置。
    /// </remarks>
    public void ToggleSidebar()
    {
        if (_sidebarPanel is null || !_sidebarPanel.IsCollapsible) return;
        _sidebarPanel.IsCollapsed = !_sidebarPanel.IsCollapsed;
    }

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
        // 三栏：侧边栏 | 主区 | 右栏
        // 用 Arc 的 ResizablePanels：可拖拽、有最小宽度、可折叠
        // Panels 是只读集合，只能往里 Add
        var panels = new ResizablePanels();

        panels.Panels.Add(new ResizablePanel
        {
            Id = "sidebar",
            Label = "侧边栏",
            DefaultSize = 2.4,
            MinSize = 220,
            MaxSize = 420,
            IsCollapsible = true,
            Content = SlotSingle(ReservedSlots.Sidebar),
        });
        _sidebarPanel = panels.Panels[^1];

        panels.Panels.Add(new ResizablePanel
        {
            Id = "main",
            Label = "主区",
            DefaultSize = 5.2,
            MinSize = 400,
            Content = SlotKeyed(ReservedSlots.Main),
        });

        panels.Panels.Add(new ResizablePanel
        {
            Id = "rightbar",
            Label = "右栏",
            DefaultSize = 2.4,
            MinSize = 280,
            MaxSize = 640,
            IsCollapsible = true,
            // 默认折叠，但有占位者时自动展开——否则注册进去的插件永远看不见。
            // 实测发现过：示例插件注册到 shell.trailing，而右栏折叠着，
            // 插件加载成功但内容不可见，等于没做成。
            IsCollapsed = _slots.Resolve(ReservedSlots.RightBar) is null,
            Content = SlotSingle(ReservedSlots.RightBar),
        });
        _rightBarPanel = panels.Panels[^1];

        // 还原上次的栏宽。Sizes 是"每栏占比百分比"，双向属性——
        // 在这里设初值就等于还原布局，拖拽结束后 LayoutChanged 再存回去。
        if (_settings.PaneSizes is { Length: 3 } savedSizes)
            panels.Sizes = savedSizes;

        // 只在拖拽**结束**时触发，不是每一帧。所以这里可以直接落盘，
        // 不需要防抖；每帧写文件才是需要防抖的场景。
        panels.LayoutChanged += (_, e) =>
        {
            // 折叠的栏占比是 0，那也是合法状态——只过滤掉 NaN/负数和长度不对的情况。
            // 把 0 当成坏数据丢掉，会表现为"折叠后布局存不下来"。
            var sizes = e.Sizes;
            if (sizes is { Count: 3 } && sizes.All(s => s >= 0 && !double.IsNaN(s)))
            {
                _settings = new AppSettings { PaneSizes = [.. sizes] };
                _settings.Save();
            }
        };

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

        // 两层 Grid：
        //   bottomRow —— 左侧 slot 内容（胶囊），右侧引擎状态条
        //   stack     —— 面板在上、底部行在下
        //   root      —— stack + 覆盖层（overlay 浮在最上层，不参与布局）
        //
        // 状态条不放进 list slot：它是框架级信息（进程活没活、连的哪个模型），
        // 不该由插件注册，也不该被插件顶掉。
        var bottomRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(bottom, 0);
        bottomRow.Children.Add(bottom);

        StatusBar.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(StatusBar, 1);
        bottomRow.Children.Add(StatusBar);

        var stack = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(panels, 0);
        stack.Children.Add(panels);
        Grid.SetRow(bottomRow, 1);
        stack.Children.Add(bottomRow);
        BottomBar = bottomRow;

        var root = new Grid();
        root.Children.Add(stack);
        root.Children.Add(overlay);

        return root;
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
