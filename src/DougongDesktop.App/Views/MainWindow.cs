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
    private readonly EngineClient _engine = new(msg => Console.Error.WriteLine($"[ui] {msg}"));
    private IDisposable? _sidebarHandle;
    private string? _selectedSessionId;
    private IReadOnlyList<SessionSummary>? _currentSessions;
    private readonly StatusCapsule _capsule = new();
    private IDisposable? _capsuleHandle;
    private readonly UiExtensionLoader _uiPlugins;

    public MainWindow()
    {
        Title = "Dougong Desktop";
        Width = 1280;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        AppShell.DeclareSlots(_slots);
        _uiPlugins = new UiExtensionLoader(_slots, msg => Console.Error.WriteLine(msg));
        RegisterBuiltInPlaceholders();

        Content = new AppShell(_slots, _vm);

        LoadUiPlugins();

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
    private void RegisterBuiltInPlaceholders()
    {
        const int BuiltIn = 100;
        const SlotOccupantOrigin Origin = SlotOccupantOrigin.BuiltIn;

        // 每条都显式标 BuiltIn：conversation 这类保留 key 只接受内置来源，
        // 而"是不是内置"必须由注册方声明，不能靠 priority 数值猜。
        // 句柄必须保存：否则 UpdateSidebar 里的 Dispose 删不掉它，
        // 而新注册的同 priority 占位者会被"先注册者胜"规则压住——
        // 表现为界面永远显示占位内容（实测踩过）
        _sidebarHandle = _slots.Register(ReservedSlots.Sidebar, new SlotOccupant(
            "builtin.sidebar", BuiltIn,
            MakeTemplate(() => Placeholder("侧边栏", "会话列表 · 工作区导航")))
        {
            Origin = Origin,
        });

        _slots.Register(ReservedSlots.Main, new SlotOccupant(
            "builtin.conversation", BuiltIn,
            MakeTemplate(() => Placeholder("对话区", "消息流 · 工具卡片 · 审批")),
            Key: ReservedSlots.Conversation)
        {
            Origin = Origin,
        });

        _slots.Register(ReservedSlots.Main, new SlotOccupant(
            "builtin.plugins", BuiltIn,
            MakeTemplate(() => Placeholder("插件", "插件管理 · 已安装列表")),
            Key: "plugins")
        {
            Origin = Origin,
        });

        _slots.Register(ReservedSlots.Main, new SlotOccupant(
            "builtin.settings", BuiltIn,
            MakeTemplate(() => Placeholder("设置", "提供商 · 外观 · 诊断")),
            Key: "settings")
        {
            Origin = Origin,
        });

        // 状态胶囊：常驻底部条，不可关——它回答"引擎还在动吗"
        _capsuleHandle = _slots.Register(ReservedSlots.Bottom, new SlotOccupant(
            "builtin.status", BuiltIn,
            MakeTemplate(() => _capsule))
        {
            Origin = Origin,
        });

        _slots.Register(ReservedSlots.RightBar, new SlotOccupant(
            "builtin.rightbar", BuiltIn,
            MakeTemplate(() => Placeholder("右栏", "文件 · 改动 · 计划 · 上下文")))
        {
            Origin = Origin,
        });
    }

    /// <summary>
    /// 连接引擎，成功后就拉一次会话列表。
    /// </summary>
    /// <remarks>
    /// <b>失败要可见</b>：连不上时在侧边栏显示原因，而不是留一个空白框让用户猜。
    /// 这条是 P3a-5 的验收点——"连不上时明确提示"比"连上了能用"更容易被忽略。
    /// </remarks>
    private async Task ConnectEngineAsync()
    {
        _engine.StateChanged += (state, detail) =>
        {
            Console.Error.WriteLine($"[ui] 引擎状态 {state}{(detail is null ? "" : $"：{detail}")}");
            _capsule.SetConnection(state, detail);
        };

        _engine.EventReceived += OnEngineEvent;

        var ok = await _engine.ConnectAsync();
        if (!ok)
        {
            Console.Error.WriteLine("[ui] 引擎连接失败，会话列表不可用");
            return;
        }

        // 连上后立刻要一次会话列表（真实数据，不是占位）
        await _engine.SendAsync(new ListSessionsCommand { RequestId = Guid.NewGuid().ToString("N") });
    }

    private void OnEngineEvent(ServerEvent evt)
    {
        switch (evt)
        {
            case SessionListedEvent listed:
                Console.Error.WriteLine($"[ui] 会话列表：{listed.Sessions.Count} 条");
                foreach (var s in listed.Sessions.Take(5))
                    Console.Error.WriteLine($"[ui]   {s.Id} · {s.Title} · {s.ItemCount} 条目");
                UpdateSidebar(listed.Sessions);
                break;

            case EngineStatusEvent status when status.Detail is { Length: > 0 }:
                Console.Error.WriteLine($"[ui] 引擎：{status.State} · {status.Detail}");
                break;

            case TurnStartedEvent:
                _capsule.OnTurnStarted();
                break;

            case ItemAddedEvent { Item: IpcToolCallItem }:
                _capsule.OnToolRunning("工具");
                break;

            case ApprovalRequestedEvent approval:
                _capsule.OnApprovalRequested(approval.Request.ToolName);
                break;

            case TurnCompletedEvent done:
                _capsule.OnTurnCompleted(done.Status, done.Diagnostic);
                break;
        }
    }

    /// <summary>把真实会话列表填进侧边栏 slot。</summary>
    private void UpdateSidebar(IReadOnlyList<SessionSummary>? sessions, string? detail = null)
    {
        _sidebarHandle?.Dispose();

        _sidebarHandle = _slots.Register(ReservedSlots.Sidebar, new SlotOccupant(
            "builtin.sidebar", 100,
            MakeTemplate(() => SidebarView.Build(sessions, detail, _selectedSessionId, OnSidebarSelect)))
        {
            Origin = SlotOccupantOrigin.BuiltIn,
        });

        _currentSessions = sessions;
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
                // P4 接入新建会话；现在先记录，不做假动作
                Console.Error.WriteLine("[ui] 新建会话（P4 接入）");
                break;

            case "__offline":
            case "__empty":
                break;   // 不可选中的提示行

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
        Report("Bottom(list)", shell?.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault());

        var panels = shell?.GetVisualDescendants().OfType<ResizablePanel>().ToArray() ?? [];
        Console.Error.WriteLine($"[shell] ResizablePanel 数量 {panels.Length}（期望 3）");
        foreach (var p in panels)
            Report($"  panel:{p.Id}", p);

        // 统计关键控件的真实实例数——截图可能受合成影响，控件树不会
        var all = shell?.GetVisualDescendants().ToArray() ?? [];
        Console.Error.WriteLine($"[shell] 视觉树节点数 {all.Length}");
        Console.Error.WriteLine($"[shell] ContentControl {all.OfType<ContentControl>().Count()}");
        Console.Error.WriteLine($"[shell] Sidebar {all.OfType<TKCode.Arc.Sidebar>().Count()}");
        Console.Error.WriteLine($"[shell] AgentStatus {all.OfType<TKCode.Arc.AgentStatus>().Count()}");
        Console.Error.WriteLine($"[shell] ItemsControl {all.OfType<ItemsControl>().Count()}");
        Console.Error.WriteLine("[shell] DUMP-END");
    }
}
