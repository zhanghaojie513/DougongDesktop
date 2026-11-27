using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;
using TKCode.Arc;
// **必须起别名**：Arc 与 Avalonia 都有 Button。llms.txt 明确要求
// "Always write the arc: prefix. Otherwise names shared with Avalonia
// such as Button resolve to Avalonia's own controls."
using ArcButton = TKCode.Arc.Button;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 右栏 dock：文件 / 改动 / 计划 / 轨迹 / 上下文 / 终端。
/// </summary>
/// <remarks>
/// <b>六个 tab 里五个已有真实能力</b>，剩下的「上下文」必须如实标注"未实现"，
/// 不放假内容。方案文档的原话是"不做假功能"——假功能比没有更糟：用户会以为它坏了。
/// <para>
/// 各 tab 的数据来源与限制：
/// <list type="table">
/// <item><term>文件</term><description>真实：走 <c>fs/list</c> IPC 命令 + Arc <c>Breadcrumb</c></description></item>
/// <item><term>改动</term><description>真实：回合前后由 SDK 版本哈希比对得出差异</description></item>
/// <item><term>计划</term><description>真实：复用 P5 的 <c>PlanItem</c></description></item>
/// <item><term>轨迹</term><description>真实：基于 Arc <c>AgentTimeline</c> 投影每轮思考/工具/等待与子智能体协作（P8d D4）</description></item>
/// <item><term>上下文</term><description><b>未实现</b>：SDK 只给累计消耗（<c>UsageLedger</c>），
/// 不给"当前上下文占用多少"</description></item>
/// <item><term>终端</term><description>真实：libghostty-vt + <c>TerminalView</c> + 跨平台 PTY（F1–F4）</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class RightDock : UserControl, IDisposable
{
    private readonly TKCode.Arc.Tabs _tabs = new();
    private readonly Dictionary<string, TKCode.Arc.TabItem> _tabItems = new(StringComparer.Ordinal);
    private readonly RightDockTabRegistry _tabRegistry = new();
    private readonly RightDockSurfaceStore _surfaces = new();
    private readonly Dictionary<string, IDisposable> _pluginTabHandles = new(StringComparer.Ordinal);
    private System.Collections.ObjectModel.ObservableCollection<SlotOccupant>? _boundPluginSlot;
    private string? _currentSessionKey;
    private bool _syncingTabs;

    /// <summary>右栏 tab 类型注册表（P8e E5）：内置与插件共用。</summary>
    public RightDockTabRegistry TabRegistry => _tabRegistry;

    /// <summary>每会话独立的停靠面状态（P8e E5）。</summary>
    public RightDockSurfaceStore Surfaces => _surfaces;

    /// <summary>各 tab 的 key。</summary>
    public static class Keys
    {
        public const string Files = "files";
        public const string Changes = "changes";
        public const string Plan = "plan";
        public const string Trajectory = "trajectory";
        public const string Context = "context";
        public const string Terminal = "terminal";
    }

    /// <summary>当前选中的 tab。</summary>
    public string ActiveTab { get; private set; } = Keys.Files;

    /// <summary>用户切换 tab。</summary>
    public event Action<string>? TabChanged;

    /// <summary>用户点开了某个文件。</summary>
    public event Action<string>? FileSelected;

    /// <summary>
    /// 用户点了某个目录，要进入它。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要这个</b>：之前目录是不可点的文字，
    /// 导致 "@" 引用只能看到根目录一层——深层文件引用不到。
    /// </remarks>
    public event Action<string>? DirectoryRequested;

    /// <summary>用户点了面包屑上的某一级，要回到那里。空串表示工作区根。</summary>
    /// <remarks>
    /// <b>和 <see cref="DirectoryRequested"/> 是同一个下游动作，但语义不同</b>：
    /// 进入是"往下走一层"，这里是"跳到某个已知祖先"。分成两个事件是为了
    /// 让订阅方能区分——将来若要做"进入时记住滚动位置、返回时恢复"，
    /// 这两种导航的期望不一致。
    /// </remarks>
    public event Action<string>? DirectoryUpRequested;

    /// <summary>
    /// 当前文件 tab 正在显示哪个目录（相对工作区的路径，根为 <c>""</c>）。
    /// </summary>
    /// <remarks>
    /// <b>要有这个状态才能画面包屑</b>。上一版没有——目录可点之后，
    /// 点进 <c>src/</c> 就再也回不到根目录了：界面上没有任何返回入口，
    /// 只能重启应用。能进不能出，等于把单向门当成了导航。
    /// </remarks>
    public string CurrentDirectory { get; private set; } = string.Empty;

    public RightDock()
    {
        RegisterBuiltInTabs();
        _tabRegistry.Changed += SyncTabsFromRegistry;

        // 两种承载方式：
        //  - 默认 `Tabs`：由 TabRegistry 驱动的平铺 tab。
        //  - `DOUGONG_UI_DOCK=1` 时换 Arc 的 `DockLayout`：支持拖拽分裂、浮动窗口、自动隐藏。
        if (Environment.GetEnvironmentVariable("DOUGONG_UI_DOCK") is "1")
        {
            BuildDock();
            Content = _dock;
        }
        else
        {
            BuildTabs();
            Content = _tabs;
        }

        ShowTab(Keys.Files);
    }

    /// <summary>向右栏 tab 注册表注册一个自定义或覆写面板；释放返回句柄即可卸载。</summary>
    public IDisposable RegisterTab(RightDockTabDescriptor descriptor) => _tabRegistry.Register(descriptor);

    /// <summary>
    /// 将 <see cref="ReservedSlots.RightDockTabs"/> 插件槽位桥接到 <see cref="TabRegistry"/>，
    /// 使第三方 UI 插件无需修改 RightDock 源码即可贡献右栏 tab。
    /// </summary>
    public void BindPluginSlot(SlotRegistry slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        UnbindPluginSlot();

        var list = slots.GetList(ReservedSlots.RightDockTabs);
        _boundPluginSlot = list;
        list.CollectionChanged += OnPluginSlotChanged;
        SyncPluginOccupants(list);
    }

    /// <summary>
    /// 切换会话停靠面（P8e E5）：记住旧会话停靠的 tab，并恢复新会话上次打开的 tab。
    /// </summary>
    public void SwitchSessionSurface(string? sessionId)
    {
        _surfaces.SaveActiveTab(_currentSessionKey, ActiveTab);
        _currentSessionKey = sessionId;

        var target = _surfaces.GetActiveTab(sessionId, fallback: Keys.Files);
        var resolved = _tabRegistry.Resolve(target);
        if (resolved is not { IsEnabled: true })
            target = Keys.Files;

        SelectTab(target);
    }

    private void RegisterBuiltInTabs()
    {
        // 内置 6 个 tab 全部走 TabRegistry（Priority=100、Origin=BuiltIn），
        // 第三方插件可用更小 Priority 覆写或用新 key 扩展，不再写死两份重复数组。
        _tabRegistry.Register(new RightDockTabDescriptor(Keys.Files, "文件", () => BuildFilesTab(), Order: 10, Priority: 100, IsEnabled: true, Placement: RightDockTabPlacement.Tool, Origin: SlotOccupantOrigin.BuiltIn));
        _tabRegistry.Register(new RightDockTabDescriptor(Keys.Changes, "改动", () => BuildChangesTab(), Order: 20, Priority: 100, IsEnabled: true, Placement: RightDockTabPlacement.Tool, Origin: SlotOccupantOrigin.BuiltIn));
        _tabRegistry.Register(new RightDockTabDescriptor(Keys.Plan, "计划", () => BuildPlanTab(), Order: 30, Priority: 100, IsEnabled: true, Placement: RightDockTabPlacement.Tool, Origin: SlotOccupantOrigin.BuiltIn));
        _tabRegistry.Register(new RightDockTabDescriptor(Keys.Trajectory, "轨迹", () => BuildTrajectoryTab(), Order: 40, Priority: 100, IsEnabled: true, Placement: RightDockTabPlacement.Tool, Origin: SlotOccupantOrigin.BuiltIn));
        _tabRegistry.Register(new RightDockTabDescriptor(Keys.Context, "上下文（未实现）", () => BuildNotImplemented(Keys.Context), Order: 50, Priority: 100, IsEnabled: false, Placement: RightDockTabPlacement.Tool, Origin: SlotOccupantOrigin.BuiltIn));
        _tabRegistry.Register(new RightDockTabDescriptor(Keys.Terminal, "终端", () => BuildTerminalTab(), Order: 60, Priority: 100, IsEnabled: true, Placement: RightDockTabPlacement.Document, Origin: SlotOccupantOrigin.BuiltIn));
    }

    /// <summary>Dock 模式下的布局（仅 <c>DOUGONG_UI_DOCK=1</c> 时非空）。</summary>
    private DockLayout? _dock;

    /// <summary>Dock 模式下的 pane（诊断用）。</summary>
    public IReadOnlyDictionary<string, DockPane>? DockPanes => _dockPanes;

    private readonly Dictionary<string, DockPane> _dockPanes = new(StringComparer.Ordinal);

    /// <summary>
    /// 用 Arc 的 <see cref="DockLayout"/> 承载由 <see cref="TabRegistry"/> 解析出的全部 tab（P8e E4/E5）。
    /// </summary>
    private void BuildDock()
    {
        _dock = new DockLayout();
        PopulateDockFromRegistry();
    }

    private void PopulateDockFromRegistry()
    {
        if (_dock is null) return;

        var tools = new List<object>();
        var docs = new List<object>();
        var nextKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tab in _tabRegistry.ResolveAll())
        {
            nextKeys.Add(tab.Key);
            if (!_dockPanes.TryGetValue(tab.Key, out var pane))
            {
                pane = new DockPane
                {
                    Id = tab.Key,
                    Title = tab.Title,
                    CanClose = false,
                    CanFloat = true,
                };
                _dockPanes[tab.Key] = pane;
            }
            else
            {
                pane.Title = tab.Title;
            }

            (tab.Placement == RightDockTabPlacement.Document ? docs : tools).Add(pane);
        }

        foreach (var stale in _dockPanes.Keys.Where(k => !nextKeys.Contains(k)).ToArray())
            _dockPanes.Remove(stale);

        _dock.ToolsSource = tools;
        _dock.DocumentsSource = docs;
    }

    /// <summary>
    /// 按 <see cref="TabRegistry"/> 建平铺 tab。
    /// </summary>
    private void BuildTabs()
    {
        PopulateTabsFromRegistry();

        // 切换时按 key 懒加载内容：文件 tab 要等引擎回话，
        // 一次全建出来会白白构造多棵控件树
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_syncingTabs) return;
            foreach (var (key, item) in _tabItems)
            {
                if (!item.IsSelected) continue;
                if (key == ActiveTab) return;
                ShowTab(key);
                return;
            }
        };
    }

    private void PopulateTabsFromRegistry()
    {
        _syncingTabs = true;
        try
        {
            var descriptors = _tabRegistry.ResolveAll();
            var nextKeys = new HashSet<string>(StringComparer.Ordinal);

            _tabs.Items.Clear();

            foreach (var tab in descriptors)
            {
                nextKeys.Add(tab.Key);
                if (!_tabItems.TryGetValue(tab.Key, out var item))
                {
                    item = new TKCode.Arc.TabItem
                    {
                        Header = tab.Title,
                        IsEnabled = tab.IsEnabled,
                    };
                    _tabItems[tab.Key] = item;
                }
                else
                {
                    item.Header = tab.Title;
                    item.IsEnabled = tab.IsEnabled;
                }

                _tabs.Items.Add(item);
            }

            foreach (var stale in _tabItems.Keys.Where(k => !nextKeys.Contains(k)).ToArray())
                _tabItems.Remove(stale);
        }
        finally
        {
            _syncingTabs = false;
        }
    }

    private void SyncTabsFromRegistry()
    {
        if (_dock is not null)
            PopulateDockFromRegistry();
        else
            PopulateTabsFromRegistry();

        // 若当前选中的 tab 已被卸载或禁用，自动回退到第一个可用 tab
        var current = _tabRegistry.Resolve(ActiveTab);
        if (current is not { IsEnabled: true })
        {
            var fallback = _tabRegistry.ResolveAll().FirstOrDefault(t => t.IsEnabled)?.Key ?? Keys.Files;
            SelectTab(fallback);
        }
        else if (_tabItems.TryGetValue(ActiveTab, out var item))
        {
            item.Content = BuildBody(ActiveTab);
        }
        else if (_dockPanes.TryGetValue(ActiveTab, out var pane))
        {
            pane.Content = BuildBody(ActiveTab);
        }
    }

    private void OnPluginSlotChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_boundPluginSlot is not null)
            SyncPluginOccupants(_boundPluginSlot);
    }

    private void SyncPluginOccupants(IReadOnlyList<SlotOccupant> occupants)
    {
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occ in occupants)
        {
            liveIds.Add(occ.Id);
            if (_pluginTabHandles.ContainsKey(occ.Id)) continue;

            var key = string.IsNullOrWhiteSpace(occ.Key) ? occ.Id : occ.Key;
            var title = string.IsNullOrWhiteSpace(occ.Key) ? occ.Id : occ.Key;
            var handle = _tabRegistry.Register(new RightDockTabDescriptor(
                Key: key,
                Title: title,
                BuildContent: () => occ.Template.Build(key) ?? Hint($"插件面板 {occ.Id} 返回了空控件"),
                Order: 100,
                Priority: occ.Priority,
                IsEnabled: true,
                Placement: RightDockTabPlacement.Tool,
                Origin: occ.Origin));

            _pluginTabHandles[occ.Id] = handle;
        }

        foreach (var staleId in _pluginTabHandles.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        {
            _pluginTabHandles[staleId].Dispose();
            _pluginTabHandles.Remove(staleId);
        }
    }

    private void UnbindPluginSlot()
    {
        if (_boundPluginSlot is not null)
        {
            _boundPluginSlot.CollectionChanged -= OnPluginSlotChanged;
            _boundPluginSlot = null;
        }

        foreach (var handle in _pluginTabHandles.Values)
            handle.Dispose();
        _pluginTabHandles.Clear();
    }

    /// <summary>终端启动时的工作目录（默认当前工作区目录）。</summary>
    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>
    /// 终端面板（P8f F1–F4）。惰性创建——不点这个 tab 就不加载原生库、也不起 shell 进程；
    /// 创建后跨 tab 切换保持存活，窗口关闭时通过 <see cref="Dispose"/> 回收。
    /// </summary>
    private TerminalPanel? _terminal;

    /// <summary>终端面板实例（诊断与自检用）。</summary>
    public TerminalPanel? Terminal => _terminal;

    /// <summary>回收右栏持有的外部资源（终端 PTY 子进程与插件槽位监听）。</summary>
    public void Dispose()
    {
        _tabRegistry.Changed -= SyncTabsFromRegistry;
        UnbindPluginSlot();
        _terminal?.Dispose();
        _terminal = null;
    }

    /// <summary>
    /// 按 key 选中某个 tab（同步 Arc 的选中状态）。
    /// </summary>
    /// <remarks>
    /// <b>不能只调 <see cref="ShowTab"/></b>：那只改内部的 <c>ActiveTab</c>
    /// 并建内容，Arc 的 <c>Tabs</c> 仍高亮原来那个——用户看到的是另一页。
    /// 自检要走真实的选中路径，否则验证的不是用户会走的代码。
    /// </remarks>
    public void SelectTab(string key)
    {
        if (_dockPanes.ContainsKey(key))
        {
            ShowTab(key);
            return;
        }

        if (!_tabItems.TryGetValue(key, out var item)) return;
        _tabs.SelectedItem = item;
        item.IsSelected = true;
        if (ActiveTab != key)
            ShowTab(key);
    }

    private Control BuildTerminalTab()
    {
        if (_terminal is null)
        {
            _terminal = new TerminalPanel(workingDirectory: WorkingDirectory);

            if (Environment.GetEnvironmentVariable("DOUGONG_UI_TERMINAL") is "1")
            {
                // VT 自检模式：灌一段带转义序列的内容，不额外起交互式 shell。
                _terminal.Probe();
            }
            else
            {
                _terminal.StartShell(WorkingDirectory);
            }
        }

        return _terminal;
    }

    /// <summary>切换 tab 并加载内容（两种承载方式都支持）。</summary>
    public void ShowTab(string key)
    {
        ActiveTab = key;
        _surfaces.SaveActiveTab(_currentSessionKey, key);

        if (_dockPanes.TryGetValue(key, out var pane))
            pane.Content = BuildBody(key);
        else if (_tabItems.TryGetValue(key, out var item))
            item.Content = BuildBody(key);

        TabChanged?.Invoke(key);
    }

    /// <summary>把内容塞给某个 tab——两种承载方式都认。</summary>
    private void SetTabContent(string key, Control content)
    {
        if (_dockPanes.TryGetValue(key, out var pane)) pane.Content = content;
        else if (_tabItems.TryGetValue(key, out var item)) item.Content = content;
    }

    /// <summary>轨迹面板实例（P8d D4，诊断与自检用）。</summary>
    public TrajectoryPanel? Trajectory => _trajectory;

    private TrajectoryPanel? _trajectory;
    private IReadOnlyList<TrajectoryTurnView> _latestTrajectory = [];

    private Control BuildTrajectoryTab()
    {
        if (_trajectory is null)
        {
            _trajectory = new TrajectoryPanel();
            _trajectory.ApplyTurns(_latestTrajectory);
        }

        return _trajectory;
    }

    /// <summary>用最新的轮次投影刷新轨迹 tab。</summary>
    public void ApplyTrajectory(IReadOnlyList<TrajectoryTurnView> turns)
    {
        _latestTrajectory = turns;
        _trajectory?.ApplyTurns(turns);
    }

    private Control BuildBody(string key) =>
        _tabRegistry.Resolve(key)?.BuildContent() ?? BuildNotImplemented(key);

    /// <summary>文档预览面板实例（P8e E7，诊断与自检用）。</summary>
    public DocumentPreviewPanel? Preview => _preview;

    private DirectoryListedEvent? _latestDirectory;
    private DocumentPreviewPanel? _preview;
    private Grid? _filePreviewHost;
    private ContentControl? _previewBreadcrumbHost;
    private bool _showingPreview;

    /// <summary>把引擎回来的目录内容填进文件 tab。</summary>
    public void ApplyDirectory(DirectoryListedEvent listed)
    {
        // 出错时不推进 CurrentDirectory：用户点了 src/ 但读失败，
        // 面包屑若跟着变成 src，他会以为自己已经在 src 里
        if (listed.Error is not { Length: > 0 })
            CurrentDirectory = listed.Path ?? string.Empty;

        _latestDirectory = listed;
        _showingPreview = false;
        SetTabContent(Keys.Files, BuildFilesTab(listed));
    }

    /// <summary>在文件 tab 内切入文档预览加载态（点击文件时调用）。</summary>
    public void ShowFileLoading(string path)
    {
        EnsurePreviewPanel().ShowLoading(path);
        _showingPreview = true;
        SetTabContent(Keys.Files, GetOrUpdateFilePreviewHost());
    }

    /// <summary>用引擎返回的 <see cref="FileReadEvent"/> 渲染文档预览（P8e E7）。</summary>
    public void ApplyFilePreview(FileReadEvent file)
    {
        EnsurePreviewPanel().Apply(file);
        _showingPreview = true;
        SetTabContent(Keys.Files, GetOrUpdateFilePreviewHost());
    }

    /// <summary>关闭文档预览，回到当前目录的文件树。</summary>
    public void CloseFilePreview()
    {
        if (!_showingPreview) return;
        _showingPreview = false;
        SetTabContent(Keys.Files, BuildFilesTab(_latestDirectory));
    }

    private DocumentPreviewPanel EnsurePreviewPanel()
    {
        if (_preview is not null) return _preview;
        _preview = new DocumentPreviewPanel();
        _preview.CloseRequested += CloseFilePreview;
        return _preview;
    }

    private Control GetOrUpdateFilePreviewHost()
    {
        if (_filePreviewHost is null)
        {
            _previewBreadcrumbHost = new ContentControl();
            _filePreviewHost = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Children =
                {
                    _previewBreadcrumbHost,
                    PlaceRow(EnsurePreviewPanel(), 1),
                },
            };
        }

        _previewBreadcrumbHost!.Content = BuildBreadcrumb();
        return _filePreviewHost;
    }

    private static Control PlaceRow(Control c, int row)
    {
        Grid.SetRow(c, row);
        return c;
    }

    /// <summary>把最新的计划填进计划 tab。</summary>
    public void ApplyPlan(PlanItem? plan)
    {
        _latestPlan = plan;
        if (_tabItems.TryGetValue(Keys.Plan, out var item))
            item.Content = BuildPlanTab();
    }

    private PlanItem? _latestPlan;

    private Control BuildFilesTab(DirectoryListedEvent? listed = null)
    {
        listed ??= _latestDirectory;
        if (listed is null)
        {
            return Hint("正在读取工作区目录…");
        }

        if (listed.Error is { Length: > 0 })
        {
            // 读失败时仍画面包屑：用户停在一个读不了的目录里，
            // 更需要一条回去的路，否则又成了死胡同
            return new StackPanel
            {
                Children = { BuildBreadcrumb(), Hint(listed.Error) },
            };
        }

        if (listed.Entries.Count == 0)
        {
            return new StackPanel
            {
                Children = { BuildBreadcrumb(), Hint("（空目录）") },
            };
        }

        var list = new StackPanel { Spacing = 1, Margin = new Thickness(9, 4) };

        foreach (var entry in listed.Entries)
        {
            var label = new TextBlock
            {
                Text = entry.IsDirectory ? $"{entry.Name}/" : entry.Name,
                FontSize = 12,
                Opacity = entry.IsDirectory ? 0.9 : 0.75,
                FontFamily = entry.IsDirectory
                    ? FontFamily.Default
                    : DshTheme.MonoFont,
            };

            // 目录与文件都可点，但**点击含义不同**：
            //   目录 → 进入（发 DirectoryRequested）
            //   文件 → 选中（发 FileSelected）
            //
            // 这一点之前做错了：目录只是不可点的文字，
            // 于是 "@" 引用只能看到根目录那一层，用户引用不了
            // src/App/Shell/X.cs 这种深层文件——功能等于半残。
            var button = new Avalonia.Controls.Button
            {
                Content = label,
                Padding = new Thickness(4, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };

            var path = string.IsNullOrEmpty(listed.Path)
                ? entry.Name
                : $"{listed.Path}/{entry.Name}";

            if (entry.IsDirectory)
                button.Click += (_, _) => DirectoryRequested?.Invoke(path);
            else
                button.Click += (_, _) => FileSelected?.Invoke(path);

            list.Children.Add(button);
        }

        if (listed.Truncated is { Length: > 0 })
            list.Children.Add(Hint(listed.Truncated));

        return new ScrollArea
        {
            Content = new StackPanel { Children = { BuildBreadcrumb(), list } },
            Orientation = ScrollAreaOrientation.Vertical,
            OverlayScrollbarMode = ScrollAreaScrollbarMode.Always,
            FadeLength = 0,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    /// <summary>
    /// 文件 tab 顶部的路径面包屑：每一级可点，点了回到那一级。
    /// </summary>
    /// <remarks>
    /// <b>这是"能进不能出"的修法</b>。上一版把目录改成可点之后，
    /// 点进 <c>src/</c> 就没有任何返回入口了——界面上没有任何返回入口，
    /// 也没有路径显示，用户只能重启应用才能回到根目录。
    /// 加这一条同时解决两件事：显示当前位置、提供回退。
    /// <para>
    /// <b>用 Arc 的 <see cref="Breadcrumb"/> 替代手写路径行</b>：
    /// 自动处理末级当前态（不可点击且高亮）、chevron 分隔符、键盘导航，
    /// 以及 <c>MaxVisibleItems</c> 在深层路径下自动折叠到溢出菜单。
    /// </para>
    /// </remarks>
    private Control BuildBreadcrumb()
    {
        var breadcrumb = new Breadcrumb
        {
            Label = "工作区路径",
            OverflowLabel = "显示隐藏路径",
            MaxVisibleItems = 4,
            Margin = new Thickness(9, 6, 9, 2),
        };

        breadcrumb.Items.Add(new BreadcrumbItem
        {
            Content = "工作区",
            Tag = string.Empty,
        });

        var accumulated = string.Empty;
        foreach (var part in CurrentDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = accumulated.Length == 0 ? part : $"{accumulated}/{part}";
            breadcrumb.Items.Add(new BreadcrumbItem
            {
                Content = part,
                Tag = accumulated,
            });
        }

        breadcrumb.ItemClick += (_, e) =>
        {
            if (e.Item is BreadcrumbItem { Tag: string target } && target != CurrentDirectory)
                DirectoryUpRequested?.Invoke(target);
        };

        return breadcrumb;
    }

    /// <summary>
    /// 改动 tab：显示回合前后由 SDK 版本比对得出的真实差异。
    /// </summary>
    /// <remarks>
    /// <b>这里原来是自己猜"哪些工具碰过哪些路径"</b>——绕路了。
    /// SDK 有 <c>FileVersionSet.Capture</c> / <c>Diff</c>，按内容哈希比对，
    /// 比"猜"可靠得多：猜的方法会把"工具尝试写但失败"也算成改动，
    /// 而漏掉工具之外的变化（比如编辑器的外部修改）。
    /// <para>
    /// <b>显示的是变更描述，不是行级 diff</b>：SDK 的 <c>FileVersion</c> 只存
    /// 长度、时间、内容哈希，<b>不存原内容</b>——做不到"第几行改成什么"。
    /// 这一点在界面上写明，否则用户会以为 diff 视图坏了。
    /// </para>
    /// </remarks>
    private Control BuildChangesTab()
    {
        if (_latestDiff is null)
        {
            return Hint("还没有运行过回合。\n\n" +
                        "回合结束后这里会显示本回合的文件改动。");
        }

        if (!_latestDiff.HasChanges)
        {
            return Hint("本回合没有改动文件。\n\n" +
                        "（比对的是文件内容哈希，改动后又改回去不会被误报。）");
        }

        var list = new StackPanel { Spacing = 6, Margin = new Thickness(9, 8) };

        // SDK 的 Diff 是多行文本，一行一个文件。按行拆开展示，
        // 让长路径能换行而不是横向撑爆右栏。
        var lines = _latestDiff.Diff.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            list.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 11,
                FontFamily = DshTheme.MonoFont,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17,
            });
        }

        list.Children.Add(new TextBlock
        {
            Text = "只显示变更描述（长度/存在性变化），没有行级差异——" +
                   "SDK 的文件版本只保存内容哈希，不保存原文。",
            FontSize = 10,
            Opacity = 0.45,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        return new ScrollArea
        {
            Content = list,
            Orientation = ScrollAreaOrientation.Vertical,
            OverlayScrollbarMode = ScrollAreaScrollbarMode.Always,
            FadeLength = 0,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    private WorkspaceChangedEvent? _latestDiff;

    /// <summary>把回合结束时的改动填进改动 tab。</summary>
    public void ApplyWorkspaceChanges(WorkspaceChangedEvent changed)
    {
        _latestDiff = changed;
        if (_tabItems.TryGetValue(Keys.Changes, out var item))
            item.Content = BuildChangesTab();
    }

    private static IEnumerable<(string Key, string Value)> ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) yield break;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                yield return (prop.Name, prop.Value.GetString() ?? "");
            }
        }
    }

    private Control BuildPlanTab()
    {
        if (_latestPlan is null || _latestPlan.Steps.Count == 0)
        {
            return Hint("还没有计划。\n\n模型在开始多步骤任务时会声明计划，之后每完成一步更新。");
        }

        var steps = new Avalonia.Collections.AvaloniaList<TKCode.Arc.AgentPlanStep>();
        foreach (var step in _latestPlan.Steps)
        {
            steps.Add(new TKCode.Arc.AgentPlanStep
            {
                Title = step.Content,
                State = step.Status switch
                {
                    "completed" => TKCode.Arc.AgentPlanStepState.Done,
                    "in_progress" => TKCode.Arc.AgentPlanStepState.Active,
                    _ => TKCode.Arc.AgentPlanStepState.Pending,
                },
            });
        }

        return new ScrollArea
        {
            Content = new Border
            {
                Margin = new Thickness(6),
                Child = new TKCode.Arc.AgentPlan { Steps = steps },
            },
            Orientation = ScrollAreaOrientation.Vertical,
            OverlayScrollbarMode = ScrollAreaScrollbarMode.Always,
            FadeLength = 0,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    /// <summary>
    /// 未实现的 tab：如实说明缺什么，以及为什么缺。
    /// </summary>
    /// <remarks>
    /// <b>写清"为什么未实现"而不是只写"未实现"</b>：前者让用户知道这是
    /// 有意的取舍、以及卡在哪；后者看起来像坏了。
    /// </remarks>
    private static Control BuildNotImplemented(string key)
    {
        var (title, reason) = key switch
        {
            Keys.Context => (
                "上下文用量面板未实现",
                "SDK 目前只提供累计 token 消耗（UsageLedger 按调用记账），\n" +
                "没有提供「当前上下文占用了多少」这个数据。\n\n" +
                "窗口大小是已知的（能力表），但已用量需要运行时按当前消息列表估算——\n" +
                "等 SDK 给出该数据后接上，不在这里用估算值冒充实际用量。"),
            _ => ("未知面板", key),
        };

        return new Border
        {
            Padding = new Thickness(14, 18),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = reason,
                        FontSize = 11,
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18,
                    },
                },
            },
        };
    }

    private static Control Hint(string text) => new Border
    {
        Padding = new Thickness(14, 18),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        },
    };
}
