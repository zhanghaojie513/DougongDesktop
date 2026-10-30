using Avalonia.Controls;
using Avalonia.VisualTree;
using DougongDesktop.App.Services;
using DougongDesktop.App.Shell;
using DougongDesktop.Ipc;
using IpcToolCallItem = DougongDesktop.Ipc.ToolCallItem;
using DougongDesktop.Ui.Core;
using TKCode.Arc;

namespace DougongDesktop.App.Views;

/// <summary>
/// 主窗口：装配 AppShell 并注册内置占位内容。
/// </summary>
/// <remarks>
/// <b>内置内容也走 slot 注册</b>——不是直接塞进布局。这样关掉某块 = 该 slot 空着，
/// 将来换成 UI 插件时也不用改外壳。P3b 只不过是把"注册者"从这段代码换成外部程序集。
/// </remarks>
public sealed class MainWindow : Window
{
    private readonly SlotRegistry _slots = new();
    private readonly AppShellViewModel _vm = new();

    /// <summary>命令面板的占位者 Id（不进 <see cref="BuiltInModules.All"/>，见注册处注释）。</summary>
    private const string CommandPaletteSlug = "builtin.command-palette";

    /// <summary>当前命令面板实例，供诊断确认它真的建出来了。</summary>
    private TKCode.Arc.CommandPaletteDialog? _commandPalette;

    /// <summary>外壳实例。构造后不为 null。</summary>
    private AppShell Shell { get; }
    private readonly EngineClient _engine = new(msg => Console.Error.WriteLine($"[ui] {msg}"));
    private readonly Dictionary<string, IDisposable> _handles = new(StringComparer.Ordinal);
    private string? _connectionDetail;
    private string? _selectedSessionId;
    private IReadOnlyList<SessionSummary>? _currentSessions;
    private readonly StatusCapsule _capsule = new();
    private readonly ConversationView _conversation = new();
    private readonly UiExtensionLoader _uiPlugins;

    public MainWindow()
    {
        Title = "Dougong Desktop";
        Width = 1280;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        AppShell.DeclareSlots(_slots);
        // 带诊断日志：uiPlugin 的 register/build/render 三段耗时写 diagnostics.jsonl。
        // P3b 台账 #5 要求的埋点——第三方插件涌入前才拿得到这份数据。
        _uiPlugins = new UiExtensionLoader(
            _slots,
            msg => Console.Error.WriteLine(msg),
            new DiagnosticsLog());
        RegisterBuiltInModules();

        Content = new AppShell(_slots, _vm);
        Shell = (AppShell)Content;

        LoadUiPlugins();

        // 自检模式：没 provider 时也能验证消息流渲染
        if (Environment.GetEnvironmentVariable("DOUGONG_UI_SELFTEST") is "1")
            _conversation.LoadSelfTestData();

        // 压力模式：验证虚拟化（1000 条只该构建少量容器）
        if (int.TryParse(Environment.GetEnvironmentVariable("DOUGONG_UI_STRESS"), out var stress) && stress > 0)
            _conversation.LoadStressData(stress);

        Opened += async (_, _) =>
        {
            DumpLayout();

            try
            {
                await ConnectEngineAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[ui] 连接流程异常：" + ex);
            }

            // 连接后再 dump：第一次 dump 时侧边栏还是占位内容，
            // 用它判断最终状态会误判（实测踩过）
            await Task.Delay(1200);
            DumpLayout();
        };
    }

    /// <summary>
    /// 注册内置占位内容。
    /// </summary>
    /// <remarks>
    /// 用 priority 100 段（内置标准层）。第三方 UI 插件想覆盖时注册更小的值即可
    /// ——这是《Slot 机制规格》§2 定的约定。
    /// </remarks>
    /// <summary>
    /// 按 <see cref="BuiltInModules.All"/> 的**声明顺序**注册内置模块。
    /// </summary>
    /// <remarks>
    /// 顺序来自清单，不是这里的代码顺序——加模块只改清单，不用找插入点。
    /// <para>
    /// 句柄统一收进 <c>_handles</c>：漏存会导致后来的注册删不掉旧的，
    /// 而"同 priority 先注册者胜"会让界面永远停在占位内容（P3a 踩过）。
    /// </para>
    /// </remarks>
    private void RegisterBuiltInModules()
    {
        const int BuiltIn = 100;
        const SlotOccupantOrigin Origin = SlotOccupantOrigin.BuiltIn;

        foreach (var module in BuiltInModules.All)
        {
            // 占位用更大的 priority：它是"没人提供时兜底"，插件注册后该顶掉它。
            // 真正的内置功能仍用 BuiltIn(100)，插件要替换必须显式给更小值。
            var priority = module.Kind == BuiltInModuleKind.Placeholder
                ? BuiltInModules.PlaceholderPriority
                : BuiltIn;

            var handle = _slots.Register(module.SlotKey, new SlotOccupant(
                module.Id, priority, MakeTemplate(() => BuildModule(module)), module.Key)
            {
                Origin = Origin,
            });

            _handles[module.Id] = handle;
        }

        RegisterCommandPalette();
    }

    /// <summary>
    /// 把命令面板注册进 overlay slot。
    /// </summary>
    /// <remarks>
    /// <b>它不在 <see cref="BuiltInModules.All"/> 里</b>：那张表描述的是"占据某个固定位置的
    /// 内容块"，而命令面板是浮层。放进 overlay 的额外好处是第三方 UI 插件可以注册
    /// 自己的命令面板顶掉它。
    /// <para>
    /// <b>会话列表变化时要重建命令项</b>：命令面板里的会话条目是构建时快照的，
    /// 不重建会表现为"新建的会话在 Cmd+K 里搜不到"。
    /// </para>
    /// </remarks>
    private void RegisterCommandPalette()
    {
        RebuildCommandPalette();
    }

    /// <summary>重建命令面板的占位者（会话列表变了就调一次）。</summary>
    private void RebuildCommandPalette()
    {
        if (_handles.Remove(CommandPaletteSlug, out var old)) old.Dispose();

        _handles[CommandPaletteSlug] = _slots.Register(ReservedSlots.Overlay, new SlotOccupant(
            CommandPaletteSlug,
            Priority: 100,      // 内置层：插件想替换给更小值
            Template: MakeTemplate(() =>
            {
                _commandPalette = CommandPaletteModule.Build(_currentSessions, RunCommand);
                return CommandPaletteModule.AsOverlay(_commandPalette);
            }))
        {
            Origin = SlotOccupantOrigin.BuiltIn,
        });
    }

    /// <summary>按模块声明构建控件。</summary>
    private Control BuildModule(BuiltInModule module) => module.Kind switch
    {
        // 对话区：真实消息流，不是占位
        _ when module.Id == "builtin.conversation" => _conversation,

        BuiltInModuleKind.Sidebar => SidebarView.Build(
            _currentSessions, _connectionDetail, _selectedSessionId, OnSidebarSelect),

        BuiltInModuleKind.StatusCapsule => _capsule,

        BuiltInModuleKind.Placeholder => Placeholder(
            module.Title ?? module.Id, module.Subtitle ?? ""),

        _ => throw new ArgumentOutOfRangeException(nameof(module)),
    };

    /// <summary>
    /// 加载 UI 插件。
    /// </summary>
    /// <remarks>
    /// <b>插件目录用环境变量指定</b>，不是硬编码路径——P3b 只验证加载链路，
    /// 真正的安装目录约定等插件管理做好再定。
    /// <para>
    /// <b>加载失败不能让 App 起不来</b>：与 agent 插件不同，UI 插件失败只是少一块。
    /// </para>
    /// </remarks>
    private void LoadUiPlugins()
    {
        var dir = Environment.GetEnvironmentVariable("DOUGONG_UI_PLUGINS");
        if (string.IsNullOrWhiteSpace(dir)) return;

        try
        {
            var results = _uiPlugins.LoadFromDirectory(dir);

            foreach (var r in results.Where(r => r.IsUserVisible))
            {
                Console.Error.WriteLine(
                    $"[ui-plugin] {r.AssemblyFile}: {r.Status} " +
                    $"{(r.ExtensionId is null ? "" : r.ExtensionId + " ")}{r.Reason}");
            }

            Console.Error.WriteLine($"[ui-plugin] 共加载 {_uiPlugins.Loaded.Count} 个插件");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ui-plugin] 加载目录失败：{ex.Message}");
        }
    }

    /// <summary>连接引擎，成功后就拉一次会话列表。</summary>
    private async Task ConnectEngineAsync()
    {
        _engine.StateChanged += (state, detail) =>
        {
            Console.Error.WriteLine($"[ui] 引擎状态 {state}{(detail is null ? "" : "：" + detail)}");
            _connectionDetail = detail;
            _capsule.SetConnection(state, detail);
            Shell.StatusBar.SetConnection(state, detail);
        };

        _engine.EventReceived += OnEngineEvent;

        var ok = await _engine.ConnectAsync();
        if (!ok)
        {
            Console.Error.WriteLine("[ui] 引擎连接失败，会话列表不可用");
            RefreshSidebar();
            return;
        }

        await _engine.SendAsync(new ListSessionsCommand { RequestId = Guid.NewGuid().ToString("N") });
    }

    private void OnEngineEvent(ServerEvent evt)
    {
        switch (evt)
        {
            case SessionListedEvent listed:
                Console.Error.WriteLine($"[ui] 会话列表：{listed.Sessions.Count} 条");
                _currentSessions = listed.Sessions;
                RefreshSidebar();
                break;

            case EngineStatusEvent status:
                Console.Error.WriteLine($"[ui] 引擎：{status.State} · {status.Detail}");
                Shell.StatusBar.SetEngineState(status.State, status.ModelName, status.Detail);
                break;

            case TurnStartedEvent:
                _capsule.OnTurnStarted();
                break;

            case ItemAddedEvent added:
                _conversation.Add(added.Item);
                if (added.Item is IpcToolCallItem tool)
                    _capsule.OnToolRunning(tool.ToolName);
                break;

            case ItemUpdatedEvent updated:
                _conversation.Update(updated.Item);
                break;

            case SessionOpenedEvent opened:
                // 切会话：先清空再重放快照里的条目
                _conversation.Clear();
                foreach (var turn in opened.Snapshot.Turns)
                    foreach (var item in turn.Items)
                        _conversation.Add(item);
                break;

            case ApprovalRequestedEvent approval:
                _capsule.OnApprovalRequested(approval.Request.ToolName);
                break;

            case TurnCompletedEvent done:
                _capsule.OnTurnCompleted(done.Status, done.Diagnostic);
                break;
        }
    }

    /// <summary>
    /// 侧边栏数据变化后重新注册。
    /// </summary>
    /// <remarks>
    /// 先 Dispose 旧句柄再注册新的——否则同 priority 下"先注册者胜"
    /// 会让界面停在旧内容（P3a 踩过）。句柄从 <c>_handles</c> 里取。
    /// </remarks>
    private void RefreshSidebar()
    {
        var module = BuiltInModules.Find("builtin.sidebar");
        if (module is null) return;

        if (_handles.TryGetValue(module.Id, out var old)) old.Dispose();

        _handles[module.Id] = _slots.Register(module.SlotKey, new SlotOccupant(
            module.Id, 100, MakeTemplate(() => BuildModule(module)), module.Key)
        {
            Origin = SlotOccupantOrigin.BuiltIn,
        });

        // 命令面板里的会话条目是构建时快照，会话列表变了必须重建，
        // 否则新建的会话在 Cmd+K 里搜不到——这不是崩溃，是"新东西搜不到"，
        // 用户只会以为功能坏了。
        RebuildCommandPalette();
    }

    /// <summary>
    /// 执行命令面板选中的命令。
    /// </summary>
    /// <remarks>
    /// <b>会话命令走 <c>session:</c> 前缀</b>而不是另开一个字段：命令 Id 是
    /// 字符串，加前缀就等于在同一个平面里分了类，不需要给 <c>CommandPaletteItem</c>
    /// 额外挂枚举——将来插件注册的命令也照这个约定加前缀即可。
    /// </remarks>
    private void RunCommand(string commandId)
    {
        if (commandId.StartsWith("session:", StringComparison.Ordinal))
        {
            OnSidebarSelect(commandId["session:".Length..]);
            return;
        }

        switch (commandId)
        {
            case CommandPaletteModule.NewSession:
                _conversation.Clear();
                _vm.ActivePanel = ReservedSlots.Conversation;
                _selectedSessionId = null;
                RefreshSidebar();
                break;

            case CommandPaletteModule.OpenSettings:
                _vm.ActivePanel = "settings";
                break;

            case CommandPaletteModule.OpenPlugins:
                _vm.ActivePanel = "plugins";
                break;

            case CommandPaletteModule.ToggleSidebar:
                Shell.ToggleSidebar();
                break;

            default:
                Console.Error.WriteLine($"[ui] 未实现的命令：{commandId}");
                break;
        }
    }

    /// <summary>侧边栏选中回调。</summary>
    private void OnSidebarSelect(string key)
    {
        switch (key)
        {
            case SidebarView.SettingsKey:
                _vm.ActivePanel = "settings";
                break;

            case SidebarView.NewSessionKey:
                Console.Error.WriteLine("[ui] 新建会话（P4 接入）");
                break;

            case "__offline":
            case "__empty":
                break;

            default:
                _selectedSessionId = key;
                _vm.ActivePanel = ReservedSlots.Conversation;
                _ = OpenSessionAsync(key);
                break;
        }
    }

    private async Task OpenSessionAsync(string sessionId)
    {
        try
        {
            // 告诉客户端"现在附着在这个会话上"：番号缺口时它要靠这个知道重拉哪个
            _engine.AttachedSessionId = sessionId;

            await _engine.SendAsync(new OpenSessionCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ui] 打开会话失败：{ex.Message}");
        }
    }

    private static Control Placeholder(string title, string subtitle) => new Border
    {
        Padding = new Avalonia.Thickness(20),
        Child = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 16,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = subtitle,
                    Opacity = 0.55,
                    FontSize = 12,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "（P3a 占位，内容在后续阶段接入）",
                    Opacity = 0.35,
                    FontSize = 11,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
            },
        },
    };

    /// <summary>
    /// 造一个 slot 模板。
    /// </summary>
    /// <remarks>
    /// <b>必须用 <c>FuncDataTemplate.Create</c>（无类型约束），不能用 <c>FuncDataTemplate&lt;object&gt;</c></b>。
    /// 后者只在数据是 <c>object</c> 时才匹配；而 slot 的 <c>ContentControl.Content</c> 放的是
    /// <c>string</c>（slot key），类型不匹配 → <c>ContentPresenter</c> 建了但里面是空的。
    /// 实测表现就是 <c>Sidebar 0</c>：模板有、子节点有、但真正的控件没被构建。
    /// </remarks>
    /// <summary>
    /// 造一个 slot 模板。
    /// </summary>
    /// <remarks>
    /// <b>用带 <c>match</c> 的重载，不要用 <c>FuncDataTemplate&lt;object&gt;</c></b>。
    /// 泛型版本只在"数据类型是 <c>object</c>"时匹配，而 slot 的
    /// <c>ContentControl.Content</c> 放的是 <c>string</c>（slot key）——
    /// 类型不匹配时 <c>ContentPresenter</c> 会建出来但里面是空的。
    /// 实测表现：<c>Sidebar 0</c>（模板有、子节点有、真正的控件没构建）。
    /// <para>
    /// 传一个恒真的 <c>match</c> 让它对任意数据生效。
    /// </para>
    /// </remarks>
    private static Avalonia.Controls.Templates.IDataTemplate MakeTemplate(Func<Control> build) =>
        new Avalonia.Controls.Templates.FuncDataTemplate(
            match: _ => true,
            build: (_, _) => build());

    /// <summary>
    /// 把布局实测结果打到 stderr。
    /// </summary>
    /// <remarks>
    /// <b>用 dump 不用截图</b>：P2 的经验是截图受裁切与 DPI 影响会误判成布局 bug，
    /// 而 Bounds 非零能确定"真的参与了布局"。
    /// </remarks>
    private void DumpLayout()
    {
        var shell = Content as Control;
        Console.Error.WriteLine($"[shell] 窗口 {ClientSize.Width:0} x {ClientSize.Height:0}");

        void Report(string name, Control? c)
        {
            if (c is null) { Console.Error.WriteLine($"[shell] {name,-22} MISSING"); return; }
            var b = c.Bounds;
            Console.Error.WriteLine($"[shell] {name,-22} {b.Width,7:0.0} x {b.Height,-7:0.0} {(b.Width > 0 && b.Height > 0 ? "OK" : "ZERO")}");
        }

        Report("AppShell", shell);
        Report("ResizablePanels", shell?.GetVisualDescendants().OfType<ResizablePanels>().FirstOrDefault());
        // 按类型取 FirstOrDefault 会拿到消息流里的 ItemsControl（那是最大的一个），
        // 不是底部条。改成从 AppShell 拿它自己持有的引用。
        if (shell is AppShell appShell)
        {
            Report("Bottom(list)", appShell.BottomBar);
            Report("EngineStatusBar", appShell.StatusBar);
            Report("Conversation.List", _conversation.List);
            Console.Error.WriteLine($"[shell] 消息条目数 {_conversation.Count}");
        }

        var panels = shell?.GetVisualDescendants().OfType<ResizablePanel>().ToArray() ?? [];
        Console.Error.WriteLine($"[shell] ResizablePanel 数量 {panels.Length}（期望 3）");
        foreach (var p in panels)
            Report($"  panel:{p.Id}", p);

        // 统计关键控件的真实实例数——截图可能受合成影响，控件树不会
        var all = shell?.GetVisualDescendants().ToArray() ?? [];
        Console.Error.WriteLine($"[shell] 视觉树节点数 {all.Length}");
        Console.Error.WriteLine($"[shell] ContentControl {all.OfType<ContentControl>().Count()}");
        Console.Error.WriteLine($"[shell] AgentStatus {all.OfType<TKCode.Arc.AgentStatus>().Count()}");

        // Sidebar 光有实例和有尺寸不算数。P3a-3 踩过：Bounds 是 404x780 全对，
        // 但 visualChildren=0——UseArcBlocks() 漏了，ControlTheme 没应用。
        // 所以这里要报**内部渲染**，否则"实例在"会被当成"渲染出来了"。
        foreach (var sidebar in all.OfType<TKCode.Arc.Sidebar>())
        {
            var kids = sidebar.GetVisualChildren().Count();
            Console.Error.WriteLine(
                $"[shell] Sidebar 内部 {(kids > 0 ? "OK" : "空壳")} visualChildren={kids}");
        }

        // 消息条目同理：条目数对但容器数为 0，说明模板没匹配上（P3a 踩过）。
        Console.Error.WriteLine(
            $"[shell] 消息容器 {_conversation.RenderedContainerCount} / 条目 {_conversation.Count}");

        // 命令面板：光有实例不算数，要确认命令列表真的装上去了
        // （P3a 踩过的坑：控件在、尺寸对、里面是空的）
        if (_commandPalette is null)
        {
            Console.Error.WriteLine("[shell] CommandPalette        MISSING");
        }
        else
        {
            var count = _commandPalette.ItemsSource?.Count() ?? 0;
            Console.Error.WriteLine(
                $"[shell] CommandPalette        命令 {count} 条 {(count > 0 ? "OK" : "空列表")} " +
                $"shortcut={_commandPalette.IsShortcutEnabled}");
        }

        Console.Error.WriteLine("[shell] DUMP-END");
    }
}
