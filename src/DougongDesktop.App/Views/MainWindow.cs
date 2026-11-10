using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using DougongDesktop.App.Services;
using DougongDesktop.App.Shell;
using DougongDesktop.Ipc;
using IpcToolCallItem = DougongDesktop.Ipc.ToolCallItem;
using IpcToolCallStatus = DougongDesktop.Ipc.ToolCallStatus;
using IpcFeedbackRating = DougongDesktop.Ipc.FeedbackRating;
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
public sealed class MainWindow : TKCode.Arc.ArcWindow
{
    private readonly SlotRegistry _slots = new();
    private readonly AppShellViewModel _vm = new();

    /// <summary>
    /// 等待会话建立后要发送的第一条消息。
    /// </summary>
    /// <remarks>
    /// 新建会话时正文要先挂起：会话 Id 由服务端生成并随
    /// <c>SessionOpenedEvent</c> 回来，在那之前发不出回合。
    /// </remarks>
    private string? _pendingFirstMessage;

    /// <summary>
    /// 排队等待显示的审批。
    /// </summary>
    /// <remarks>
    /// 引擎侧审批是串行的（同一会话不能并发回合），所以队列里通常最多一条。
    /// 队列存在只是为了不丢请求——丢了就是"静默卡死"。
    /// </remarks>
    private readonly Queue<ApprovalRequest> _approvalQueue = new();

    /// <summary>命令面板的占位者 Id（不进 <see cref="BuiltInModules.All"/>，见注册处注释）。</summary>
    private const string CommandPaletteSlug = "builtin.command-palette";

    /// <summary>当前命令面板实例，供诊断确认它真的建出来了。</summary>
    private TKCode.Arc.CommandPaletteDialog? _commandPalette;

    /// <summary>外壳实例。构造后不为 null。</summary>
    private AppShell Shell { get; }
    private readonly EngineClient _engine = new(log: msg => Console.Error.WriteLine($"[ui] {msg}"));
    private readonly Dictionary<string, IDisposable> _handles = new(StringComparer.Ordinal);
    private string? _connectionDetail;

    /// <summary>当前连接状态。设置页要把它翻成中文再显示。</summary>
    private EngineConnectionState? _connectionState;
    private string? _selectedSessionId;
    private IReadOnlyList<SessionSummary>? _currentSessions;
    private readonly StatusCapsule _capsule = new();
    private readonly ConversationView _conversation = new();

    /// <summary>右栏 dock（P7）。</summary>
    private readonly RightDock _rightDock = new();

    /// <summary>设置面板（P8c），惰性构建——不进设置页就不该付构造成本。</summary>
    private SettingsView? _settingsView;

    /// <summary>
    /// 最近一次的模型目录。
    /// </summary>
    /// <remarks>
    /// <b>必须缓存</b>：<c>models/list</c> 在连接后立刻返回，而设置页是惰性构建的。
    /// 用户完全可能先看会话、过一会儿才点开设置——那时事件早就过去了。
    /// 不缓存的话设置页会一直显示"—"，看起来像"引擎没报目录"。
    /// </remarks>
    private (IReadOnlyList<ModelInfo> Models, IReadOnlyList<string> Providers, string? Current)? _lastCatalog;

    /// <summary>最近一次的权限预设状态。</summary>
    /// <remarks>
    /// <b>同样要缓存</b>：<c>permission/listed</c> 在连接后立刻返回，
    /// 而设置页是惰性构建的。不缓存的话，用户先看会话再点设置会看到空下拉。
    /// </remarks>
    private PermissionPresetState? _lastPermissionState;

    private readonly UiExtensionLoader _uiPlugins;

    /// <summary>
    /// 架构守卫的条数，只用于设置页的"诊断"分区展示。
    /// </summary>
    /// <remarks>
    /// <b>为什么写死而不是运行时统计</b>：检查工程不引用 App（守分层），
    /// App 也拿不到它的结果。这个数字只是给用户看的"确实有守卫在跑"，
    /// 精确性不影响功能。**但它会随检查数变化**——改检查数时这里要同步，
    /// `settingsDiagnosticsReportsCheckCount` 那条无头检查会盯着。
    /// </remarks>
    private const int ArchitectureCheckCount = 88;

    public MainWindow()
    {
        Title = "Dougong Desktop";
        Width = 1280;
        Height = 820;

        // 扩展客户区：让原生标题栏消失，改由 AppShell 自绘 40px 那条。
        //
        // **为什么不用原生标题栏**：Windows 原生标题栏的颜色由 DWM 管，
        // Avalonia 没有着色 API。dsh 是 Electron，能用 titleBarOverlay 把
        // 标题栏设成与侧栏同色；我们不扩展客户区就只能留一条系统灰，
        // 与侧栏的 #F9FAFB 明显不同——那正是"看起来不像"的第一眼来源。
        //
        // 代价：窗口按钮（最小化/最大化/关闭）要自己画。
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = Ui.Core.Columns.TitlebarHeight;

        // 窗口图标用真实品牌资源（品牌规格：纯黑白，无渐变）
        Icon = new Avalonia.Controls.WindowIcon(
            Avalonia.Platform.AssetLoader.Open(new Uri("avares://DougongDesktop.App/Assets/dougong.ico")));

        // 页面底色用 dsh 的 bg-base，不用 Arc 默认。
        // 这是"看起来像不像 dsh"最直接的一块：Arc 的默认底色与 dsh 差一档明度，
        // 而侧边栏/主区都是从它上面叠出来的。
        Background = DshTheme.Brush(DshTheme.PageBackground);

        // 毛玻璃：dsh 的 macOS 侧边栏是半透明叠加窗口 vibrancy。
        // Avalonia 12 有 AcrylicBlur（Windows）/Blur/Mica，先按能力降级声明；
        // 实际生效的级别在 Opened 里读 TransparencyLevel 确认，
        // 拿不到时退化为不透明——不能假设某个平台支持。
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.None,
        ];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        AppShell.DeclareSlots(_slots);
        // 带诊断日志：uiPlugin 的 register/build/render 三段耗时写 diagnostics.jsonl。
        // P3b 台账 #5 要求的埋点——第三方插件涌入前才拿得到这份数据。
        _uiPlugins = new UiExtensionLoader(
            _slots,
            msg => Console.Error.WriteLine(msg),
            new DiagnosticsLog());
        RegisterBuiltInModules();

        var shell = new AppShell(_slots, _vm);
        Content = shell;
        Shell = shell;


        // 标题栏左侧内容注入 ArcWindow 的 TitleBar。
        // 必须在这里做：AppShell 构造时才 BuildTitlebar() 生成内容，
        // 而 ArcWindow.TitleBar 在基类构造时就有了。
        if (TitleBar is { } bar && shell.TitlebarContent is { } left)
            bar.LeftContent = left;

        // 输入区 → 引擎。三段式：确保有会话 → 发回合 → 把本地消息先显示出来
        _conversation.MessageSubmitted += text => _ = SubmitAsync(text);

        // 斜杠命令：/ 开头能选的是"全局动作"，与会话状态无关
        _conversation.Composer.SetCommands(BuildSlashCommands());
        _conversation.Composer.CommandInvoked += RunSlashCommand;

        // 用户在 composer 里换了模型：只记日志。
        // 真正切换需要重开会话（模型是会话级参数），那是 P8d 的事——
        // 现在假装切换成功比不响应更糟。
        _conversation.Composer.ModelChanged += modelId =>
            Console.Error.WriteLine($"[ui] 选择模型 {modelId}（切换需重开会话，P8d 接入）");
        _conversation.StopRequested += () => _ = StopTurnAsync();

        // 右栏：点目录进入、点文件选中（选中目前只记日志，P8d 的交付物视图再用）
        _rightDock.DirectoryRequested += path => _ = RequestDirectoryAsync(path);
        _rightDock.DirectoryUpRequested += path => _ = RequestDirectoryAsync(path);
        _rightDock.FileSelected += path =>
            Console.Error.WriteLine($"[ui] 选中文件：{path}（预览见 P8e）");

        // 消息反馈 → 引擎（宿主侧只落本地诊断，不改会话状态）
        _conversation.FeedbackRequested += (itemId, rating, reason) =>
            _ = SendFeedbackAsync(itemId, rating, reason);

        LoadUiPlugins();

        // 设置页自检：直接切到 settings 面板，省得手动点侧栏。
        if (Environment.GetEnvironmentVariable("DOUGONG_UI_SETTINGS") is "1")
            _vm.ActivePanel = "settings";

        // 自检模式：没 provider 时也能验证消息流渲染
        if (Environment.GetEnvironmentVariable("DOUGONG_UI_SELFTEST") is "1")
        {
            _conversation.LoadSelfTestData();
            _conversation.LoadAttachmentSelfTest();

            // 自检也验证"运行中"这条路径：没有 provider 时跑不到
            // TurnStarted → TurnCompleted 的真实往返，但状态行的
            // 渲染与计时可以直接驱动。**不验证就等于没接上**。
            //
            // 两个开关分开：DOUGONG_UI_RUNNING=1 只看状态行（回合进行中），
            // =2 走完整流程（起 → 停 → 标已停止）。合在一起会造出
            // "正在跑又已停止"的自相矛盾状态，而那正是要防的东西。
            switch (Environment.GetEnvironmentVariable("DOUGONG_UI_RUNNING"))
            {
                case "1":
                    _conversation.OnTurnStarted();
                    break;

                case "2":
                    _conversation.OnTurnStarted();
                    _conversation.OnTurnEnded();
                    _conversation.MarkLastAssistantStopped();
                    break;
            }
        }

        // 审批自检：走真实的 ShowApproval 路径，验证待决锁真的上了。
        // 与 LoadSelfTestData 里那张卡片不同——那张只验证渲染，
        // 这条验证"有审批时输入区被锁"。
        if (Environment.GetEnvironmentVariable("DOUGONG_UI_APPROVAL") is "1")
        {
            ShowApproval(new ApprovalRequest(
                RequestId: "req-live",
                SessionId: "sess-live",
                TurnId: "turn-live",
                InvocationId: "inv-live",
                ToolName: "write_file",
                Summary: "写入文件 README.md",
                ArgumentsJson: "{\"path\":\"README.md\"}",
                PermissionScope: "Write",
                ExpiresAt: DateTimeOffset.Now.AddMinutes(5),
                MayHaveIrreversibleEffects: true));
        }

        // 压力模式：验证虚拟化（1000 条只该构建少量容器）
        if (int.TryParse(Environment.GetEnvironmentVariable("DOUGONG_UI_STRESS"), out var stress) && stress > 0)
            _conversation.LoadStressData(stress);

        Opened += async (_, _) =>
        {
            // A8 验证：TransparencyLevelHint 只是"我请求这些"，
            // 实际生效的是 TransparencyLevel——平台可能一个都不支持。
            // 不读这个值就不知道毛玻璃到底有没有开。
            Console.Error.WriteLine(
                $"[shell] 毛玻璃 请求={string.Join("/", TransparencyLevelHint)} " +
                $"实际={ActualTransparencyLevel} " +
                $"{(ActualTransparencyLevel == WindowTransparencyLevel.None ? "（未生效，已降级为不透明）" : "（生效）")}");

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

            // 滚动自检：有些条目在视口外，虚拟化不会构建它们。
            // 不滚下去就永远看不到"工具卡到底能不能构建"——
            // 而 `ToolCall 0 张` 在"视口外"和"构建失败"两种情况下完全相同。
            if (Environment.GetEnvironmentVariable("DOUGONG_UI_SCROLL") is "end")
            {
                _conversation.Scroll.ScrollToEnd();
                await Task.Delay(600);
                Console.Error.WriteLine("[shell] 已滚到底（滚动自检）");
            }

            // 工具卡自检：把工具条目临时挪到最前面，确保它们进入视口。
            // 滚到底也覆盖不到中间那 6 张工具卡（视口只有 353px），
            // 而"看着像没渲染"和"确实没渲染"必须分清。
            if (Environment.GetEnvironmentVariable("DOUGONG_UI_TOOLFIRST") is "1")
            {
                _conversation.MoveToolsToFrontForProbe();
                await Task.Delay(400);
                Console.Error.WriteLine("[shell] 工具条目已前置（工具卡自检）");
            }

            DumpLayout(layoutSettled: true);

            // 目录导航自检：模拟"在右栏点进子目录"。
            //
            // 为什么需要这个开关：根目录加载只能证明 RequestDirectoryAsync
            // 被调用一次。P8b 复核前，目录点击压根没接到这条链上——
            // 而那时 @ 引用项 18 个照样是绿的。所以要验的是**第二次**请求，
            // 以及它带来的是累加（> 第一次）而不是替换（= 子目录条目数）。
            // 终端自检：切到右栏的终端 tab，并灌一段带转义序列的内容。
            if (Environment.GetEnvironmentVariable("DOUGONG_UI_TERMINAL") is "1")
            {
                _rightDock.SelectTab("terminal");
                Console.Error.WriteLine(
                    $"[shell] 终端面板 核心就绪={_rightDock.Terminal?.IsCoreReady}");
            }

            if (Environment.GetEnvironmentVariable("DOUGONG_UI_NAV") is "1")
            {
                var before = _conversation.Composer.Inner.ContextItems.Count;
                Console.Error.WriteLine($"[nav] 导航前 @ 引用项 {before} 个");

                await RequestDirectoryAsync("src");

                // **等条件成立，不等固定时长**。
                // 第一版写的是 await Task.Delay(800)，结果两轮里有一轮
                // 响应慢于 800ms，打印出"未增长，替换或未生效"——
                // 自检自己抖动，等于把人往错误结论上引。
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline
                       && _conversation.Composer.Inner.ContextItems.Count <= before)
                {
                    await Task.Delay(50);
                }

                var after = _conversation.Composer.Inner.ContextItems.Count;
                Console.Error.WriteLine(
                    $"[nav] 导航后 @ 引用项 {after} 个" +
                    (after > before ? "（**累加，符合预期**）" : "（**未增长，替换或未生效**）"));

                // 回退验证：回到工作区根，右栏当前目录必须变回 ""。
                //
                // **这条测的是下游动作，不是面包屑本身**：自检模式没有鼠标，
                // 点不了面包屑按钮。"按钮点击 → DirectoryUpRequested" 那一段
                // 由静态检查 dock-directory-nav-is-two-way 覆盖（事件有触发点、
                // 装配根有订阅）；这里只证明"回退路径真的把状态改回来了"。
                await RequestDirectoryAsync(string.Empty);

                var backDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < backDeadline && _rightDock.CurrentDirectory.Length != 0)
                    await Task.Delay(50);

                Console.Error.WriteLine(
                    $"[nav] 回退后 CurrentDirectory='{_rightDock.CurrentDirectory}'" +
                    (_rightDock.CurrentDirectory.Length == 0
                        ? "（**已回到工作区根，符合预期**）"
                        : "（**仍在子目录，回退未生效**）"));
            }
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

    /// <summary>
    /// 把连接状态翻成中文，附上细节。
    /// </summary>
    /// <remarks>
    /// <b>必须翻</b>：这个值直接显示在设置页的"诊断"里，混进一个
    /// <c>Connected</c> 会在满屏中文里格外突兀——而且它不是给开发者看的
    /// 日志，是给用户看的状态。
    /// </remarks>
    private static string DescribeConnection(EngineConnectionState state, string? detail)
    {
        var word = state switch
        {
            EngineConnectionState.Connected => "已连接",
            EngineConnectionState.Connecting => "连接中",
            EngineConnectionState.Starting => "启动中",
            EngineConnectionState.Failed => "连接失败",
            EngineConnectionState.Disconnected => "未连接",
            _ => state.ToString(),
        };

        return string.IsNullOrWhiteSpace(detail) ? word : $"{word} · {detail}";
    }

    /// <summary>
    /// 构建（并补齐数据）设置面板。
    /// </summary>
    /// <remarks>
    /// <b>为什么要在这里补喂</b>：<c>models/list</c> 只在连接时发一次，
    /// 而设置页是惰性构建的。用户先看会话、之后再点设置是完全正常的路径——
    /// 那时缓存里已有目录，补一次就对了。不补的话，首次打开设置页
    /// 会看到一片"—"，很容易被当成"引擎没报模型目录"。
    /// </remarks>
    private Control BuildSettingsPanel()
    {
        _settingsView ??= new SettingsView();

        if (_lastCatalog is { } catalog)
            _settingsView.SetModelCatalog(catalog.Models, catalog.Providers, catalog.Current);

        // 插件清单：直接读加载器的已加载列表，不另存一份。
        // 用户在客户端生命周期内装不了新插件（安装链路属 P8e），
        // 所以这里不需要订阅变化。
        _settingsView.SetPlugins(_uiPlugins.Loaded);

        _settingsView.SetDiagnostics(
            engineState: _connectionState is { } st ? DescribeConnection(st, _connectionDetail) : "未连接",
            providerProtocol: Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_PROTOCOL") ?? "openai",
            checkCount: ArchitectureCheckCount);

        // 权限预设：缓存里有就补喂（与模型目录同样的时序问题——
        // permission/listed 在连接后立刻返回，设置页可能之后才打开）。
        if (_lastPermissionState is { } presets)
            _settingsView.SetPermissionPresets(presets);

        // 切预设 → 发命令。绑定在这里而不是构造期：设置页是惰性建的。
        // **不在本地先改**：以引擎回的 permission/listed 为准，
        // 否则"切换失败"会显示成"成功"。
        _settingsView.PermissionPresetRequested += key => _ = SendPermissionSwitchAsync(key);

        return _settingsView;
    }

    /// <summary>按模块声明构建控件。</summary>
    private Control BuildModule(BuiltInModule module) => module.Kind switch
    {
        // 对话区：真实消息流，不是占位
        _ when module.Id == "builtin.conversation" => _conversation,

        // 设置：真实面板，不是占位（P8c）
        _ when module.Id == "builtin.settings" => BuildSettingsPanel(),

        BuiltInModuleKind.Sidebar => SidebarView.Build(
            _currentSessions, _connectionDetail, _selectedSessionId, OnSidebarSelect),

        BuiltInModuleKind.StatusCapsule => _capsule,

        BuiltInModuleKind.RightDock => _rightDock,

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
            _connectionState = state;
            _capsule.SetConnection(state, detail);
            Shell.StatusBar.SetConnection(state, detail);

            // 设置页开着时同步刷新；没开就跳过（下次打开时会补喂一次）。
            _settingsView?.SetDiagnostics(
                engineState: DescribeConnection(state, detail),
                providerProtocol:
                    Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_PROTOCOL") ?? "openai",
                checkCount: ArchitectureCheckCount);
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

        // 右栏文件 tab 一打开就要有内容，不等用户手动切
        await RequestDirectoryAsync(string.Empty);

        // 模型目录：composer 的模型 pill 需要它。
        // 不请求的话 pill 会是空的——Arc 在 Models 为空时直接隐藏它。
        await _engine.SendAsync(new ListModelsCommand
        {
            RequestId = Guid.NewGuid().ToString("N"),
        });

        // 权限预设：和模型目录一样，连接后拉一次。
        await _engine.SendAsync(new ListPermissionPresetsCommand
        {
            RequestId = Guid.NewGuid().ToString("N"),
        });
    }

    /// <summary>把用户选的预设发给引擎。</summary>
    /// <remarks>
    /// <b>只发不猜</b>：引擎回 <c>permission/listed</c>（带当前真实状态），
    /// 界面照它渲染。本地先改再等服务端确认，会把"切换失败"显示成"成功"。
    /// </remarks>
    private async Task SendPermissionSwitchAsync(string key)
    {
        try
        {
            await _engine.SendAsync(new SwitchPermissionPresetCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Key = key,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ui] 切换权限到 {key} 失败：{ex.Message}");
        }
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

            case PermissionPresetListedEvent presets:
                Console.Error.WriteLine(
                    $"[ui] 权限预设：当前={presets.State.CurrentKey}，" +
                    $"可选 {presets.State.Options.Count} 档" +
                    (presets.Error is { Length: > 0 } ? $"（错误：{presets.Error}）" : ""));
                _lastPermissionState = presets.State;
                // 只有设置页建过才喂——它是惰性的
                _settingsView?.SetPermissionPresets(presets.State);
                break;

            case ModelsListedEvent listed:
                Console.Error.WriteLine(
                    $"[ui] 模型目录：{listed.Models.Count} 个 / {listed.Providers.Count} 家，" +
                    $"当前={listed.CurrentModelId ?? "(未配置)"}");
                _conversation.Composer.SetModels(
                    ModelProjection.ToAiModels(listed.Models),
                    ModelProjection.ToProviders(listed.Providers));
                _conversation.Composer.SelectModel(listed.CurrentModelId);

                // 缓存一份：设置页是惰性构建的，可能在这个事件之后才打开。
                _lastCatalog = (listed.Models, listed.Providers, listed.CurrentModelId);

                // 设置页复用同一份目录——不为了设置页再发一次 models/list。
                _settingsView?.SetModelCatalog(
                    listed.Models, listed.Providers, listed.CurrentModelId);
                break;

            case WorkspaceChangedEvent changed:
                Console.Error.WriteLine(
                    changed.HasChanges
                        ? $"[ui] 本回合改动：{changed.Diff}"
                        : "[ui] 本回合没有文件改动");
                _rightDock.ApplyWorkspaceChanges(changed);
                break;

            case DirectoryListedEvent listed:
                Console.Error.WriteLine(
                    $"[ui] 目录 '{listed.Path}'：{listed.Entries.Count} 项" +
                    (listed.Error is { Length: > 0 } ? $"（错误：{listed.Error}）" : ""));
                _rightDock.ApplyDirectory(listed);

                // 同一份目录数据也给 composer 的 "@" 引用用。
                //
                // **累加而不是替换**：右栏点进 src 之后，根目录的文件
                // 仍应该能被 "@" 引用到。替换会让列表只剩最后浏览的那一层，
                // 用户刚看过的根目录文件就"消失"了。
                foreach (var item in ContextItemProjection.FromDirectory(listed))
                    _conversation.Composer.AddContextItem(item);

                // 分组标题也要设，否则 Arc 的默认是英文 "Files"/"Docs"
                _conversation.Composer.SetMentionGroups(ContextItemProjection.Groups);
                break;

            case TurnStartedEvent:
                _capsule.OnTurnStarted();
                // 生成中：发送键变停止键（Arc 的 AiComposer 自带这个切换）
                _conversation.Composer.SetGenerating(true);
                // 消息流末尾的"深度求索中"状态行：只有它能回答
                // "在跑吗、跑多久了"，而用户上滚时顶部胶囊已经看不见了
                _conversation.OnTurnStarted();

                break;

            case ItemAddedEvent added:
                _conversation.Add(added.Item);

                // 右栏的计划 tab 跟着消息流更新（全量替换）。
                // "改动" tab 不靠这里——它用宿主推来的 FileVersionSet 比对结果
                if (added.Item is PlanItem plan)
                {
                    _rightDock.ApplyPlan(plan);
                }
                else if (added.Item is IpcToolCallItem tool)
                {
                    _capsule.OnToolRunning(tool.ToolName);
                }

                break;

            case ItemUpdatedEvent updated:
                _conversation.Update(updated.Item);

                // 工具终态 → 回切到"思考中"。不回切的话胶囊会永远停在
                // "执行 xxx"，看起来像卡住（P5 判据：Working/NeedsInput/Done 随事件变化）
                if (updated.Item is IpcToolCallItem { Status: not IpcToolCallStatus.Running
                                                        and not IpcToolCallStatus.Requested
                                                        and not IpcToolCallStatus.AwaitingApproval })
                    _capsule.OnToolCompleted();
                break;

            case SessionOpenedEvent opened:
                // 切会话：先清空再重放快照里的条目
                _conversation.Clear();
                foreach (var turn in opened.Snapshot.Turns)
                    foreach (var item in turn.Items)
                        _conversation.Add(item);

                // 新会话建立：把挂起的第一条消息补发出去
                if (_pendingFirstMessage is { Length: > 0 } first)
                {
                    _pendingFirstMessage = null;
                    _selectedSessionId = opened.Snapshot.Id;
                    _engine.AttachedSessionId = opened.Snapshot.Id;

                    // 这里已经在 UI 线程（EventReceived 由 Dispatcher.Post 送回），
                    // 不需要 Task.Run —— 换到线程池去发只会多一次线程跳转。
                    _ = SendFirstTurnAsync(opened.Snapshot.Id, first);
                }
                break;

            case ApprovalRequestedEvent approval:
                _capsule.OnApprovalRequested(approval.Request.ToolName);
                ShowApproval(approval.Request);
                break;

            case TurnCompletedEvent done:
                _capsule.OnTurnCompleted(done.Status, done.Diagnostic);
                _conversation.Composer.SetGenerating(false);
                _conversation.OnTurnEnded();

                // 用户中止的回合：给最后一条助手消息打"已停止"标记。
                //
                // dsh 只有这一种情况下会在正文尾部挂标签
                // （AssistantMarkdown.tsx:152-154）；失败走的是 turn-error
                // 那一行，不重复标。这里只认 Cancelled。
                if (done.Status == TurnStatus.Cancelled)
                    _conversation.MarkLastAssistantStopped();

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

    /// <summary>
    /// 发送一条消息。
    /// </summary>
    /// <remarks>
    /// <b>没有当前会话时先建一个</b>：用户打开应用直接打字是最自然的入口，
    /// 让他先去侧边栏点"新建会话"才能开始说话是多余的仪式。
    /// <para>
    /// <b>先本地回显用户消息</b>：引擎会把它作为条目发回来，但那是异步的。
    /// 不等回显会让用户按了回车之后界面毫无变化——看起来像没发出去。
    /// 用本地条目保证"按下即有反馈"，服务端的同名条目到达时按 Id 覆盖。
    /// </para>
    /// </remarks>
    private async Task SubmitAsync(string text)
    {
        try
        {
            if (_selectedSessionId is null)
            {
                // CreateSessionCommand 的响应是 SessionOpenedEvent，
                // 会话 Id 在事件里回来——这里不能自己编一个。
                // 所以先把待发的正文挂起，等 SessionOpenedEvent 到达再补发回合。
                // 直接 return 会把用户刚输入的内容丢掉。
                _pendingFirstMessage = text;

                await _engine.SendAsync(new CreateSessionCommand
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Workspace = Environment.CurrentDirectory,
                    Title = text.Length > 24 ? text[..24] : text,
                });

                return;
            }

            await _engine.SendAsync(new StartTurnCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                SessionId = _selectedSessionId,
                Input = text,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ui] 发送失败：{ex.Message}");
            _conversation.Add(new NoticeItem
            {
                At = DateTimeOffset.Now,
                Id = $"notice-{Guid.NewGuid():N}"[..16],
                Level = NoticeLevel.Error,
                Text = $"消息没有发出去：{ex.Message}",
            });
        }
    }

    /// <summary>
    /// 显示审批卡片、上待决锁。
    /// </summary>
    /// <remarks>
    /// <b>单一待决锁</b>：同一时刻只允许一条未答复的审批。引擎侧本来就是串行的
    /// （同一会话不能并发回合），但界面必须自己也守这条——否则第二条审批到达时
    /// 会出现两张卡片，用户答完第一张后第二张的答案可能配错回合。
    /// <para>
    /// <b>同一 RequestId 重复到达要忽略</b>：重连或重同步可能重放事件，
    /// 重复插入会出现两张一模一样的卡片，而答了其中一张另一张仍可点。
    /// </para>
    /// </remarks>
    private void ShowApproval(ApprovalRequest request)
    {
        if (_conversation.PendingApprovals.ContainsKey(request.RequestId))
        {
            Console.Error.WriteLine($"[ui] 重复的审批请求已忽略：{request.RequestId}");
            return;
        }

        // 单一待决锁：已有未答复的审批时，新的这条**必须排队**而不是丢弃。
        // 丢弃的话引擎侧的审批器会一直阻塞到 5 分钟超时——用户看到的是
        // "回合莫名其妙卡死"，而根本不知道有一条审批被界面吞了。
        if (_conversation.PendingApprovals.Count > 0)
        {
            _approvalQueue.Enqueue(request);
            Console.Error.WriteLine($"[ui] 已有待决审批，新的请求排队：{request.RequestId}");
            return;
        }

        var card = new ApprovalCard(request);
        card.Decided += (decision, reason) =>
        {
            _conversation.ResolveApproval(request.RequestId);
            _conversation.Composer.SetApprovalPending(false);
            _capsule.OnApprovalResolved();
            _ = RespondApprovalAsync(request.RequestId, decision, reason);

            // 展示排队中的下一条（如果有）
            ShowNextQueuedApproval();
        };

        _conversation.AddApproval(card);
        _conversation.Composer.SetApprovalPending(true, request.ToolName);
    }

    /// <summary>显示队列里的下一条审批。</summary>
    private void ShowNextQueuedApproval()
    {
        if (_conversation.PendingApprovals.Count > 0) return;
        if (!_approvalQueue.TryDequeue(out var next)) return;

        ShowApproval(next);
    }

    /// <summary>把审批答复送回引擎。</summary>
    /// <remarks>
    /// <b>失败只记日志，不改界面状态</b>：卡片已经把"已允许"显示出来了。
    /// 这里再回滚会让界面反复横跳；而且真正的事实由引擎的审批器决定——
    /// 它收不到答复就按超时拒绝处理，回合会自己结束。
    /// </remarks>
    private async Task RespondApprovalAsync(string requestId, ApprovalDecision decision, string? reason)
    {
        try
        {
            await _engine.SendAsync(new RespondApprovalCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Response = new ApprovalResponse(requestId, decision, reason),
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ui] 审批答复发送失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 斜杠命令清单。
    /// </summary>
    /// <remarks>
    /// <b>只放已实现的</b>：Arc 会把命令列给用户，点了没反应比没有更糟。
    /// 现在能用的只有"清空对话"和"打开设置"——它们是纯本地动作，
    /// 不依赖引擎状态。
    /// <para>
    /// dsh 的 <c>ui-commands</c> 有更多（/model、/permission、/compact…）。
    /// 那些要等对应功能实现后再加，现在加等于给假入口。
    /// </para>
    /// </remarks>
    private IReadOnlyList<TKCode.Arc.AiCommand> BuildSlashCommands() =>
    [
        new TKCode.Arc.AiCommand
        {
            Id = "clear",
            Name = "clear",
            Description = "清空当前消息流（不删除会话）",
        },
        new TKCode.Arc.AiCommand
        {
            Id = "settings",
            Name = "settings",
            Description = "打开设置",
        },
    ];

    /// <summary>
    /// 执行斜杠命令。
    /// </summary>
    /// <remarks>
    /// <b>只做本地动作，不发给引擎</b>：斜杠命令在 dsh 里也是客户端行为。
    /// 需要引擎参与的命令（/model 切换要重开会话）等对应功能实现后再加。
    /// </remarks>
    private void RunSlashCommand(string commandId)
    {
        switch (commandId)
        {
            case "clear":
                _conversation.Clear();
                break;

            case "settings":
                _vm.ActivePanel = "settings";
                break;

            default:
                // 不认识的命令：如实说明，不静默吞掉
                Console.Error.WriteLine($"[ui] 未实现的斜杠命令：{commandId}");
                _conversation.Add(new NoticeItem
                {
                    At = DateTimeOffset.Now,
                    Id = $"notice-{Guid.NewGuid():N}"[..16],
                    Level = NoticeLevel.Warning,
                    Text = $"命令 /{commandId} 还没有实现。",
                });
                break;
        }
    }

    /// <summary>请求某个目录的内容（右栏文件树）。</summary>
    private async Task RequestDirectoryAsync(string path)
    {
        try
        {
            await _engine.SendAsync(new ListDirectoryCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Path = path,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ui] 列目录失败：{ex.Message}");
        }
    }

    /// <summary>补发新会话的首条消息。</summary>
    private async Task SendFirstTurnAsync(string sessionId, string text)
    {
        try
        {
            await _engine.SendAsync(new StartTurnCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                Input = text,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ui] 补发首条消息失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 提交消息反馈。
    /// </summary>
    /// <remarks>
    /// <b>失败只记日志</b>：反馈是锦上添花，发不出去不该打扰用户。
    /// 界面上那边已经显示"已记录"了——回滚那个显示比静默失败更糟。
    /// </remarks>
    private async Task SendFeedbackAsync(
        string itemId, IpcFeedbackRating rating, string? reason)
    {
        try
        {
            await _engine.SendAsync(new SubmitFeedbackCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                ItemId = itemId,
                Rating = rating,
                Reason = reason,
                SessionId = _selectedSessionId,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ui] 反馈提交失败：{ex.Message}");
        }
    }

    /// <summary>取消当前回合。</summary>
    private async Task StopTurnAsync()
    {
        if (_selectedSessionId is not { } sessionId) return;

        try
        {
            await _engine.SendAsync(new CancelTurnCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ui] 取消失败：{ex.Message}");
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
    private void DumpLayout(bool layoutSettled = false)
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

        if (shell is not null)
        {
            var hdr = _conversation.Header;
            Report("ConvHeader", hdr);
            var hb = hdr.GetVisualDescendants().OfType<Avalonia.Controls.Button>().ToArray();
            Console.Error.WriteLine($"[shell] 顶栏按钮 {hb.Length} 个：" +
                string.Join(",", hb.Select(b => $"{b.Bounds.Width:0}x{b.Bounds.Height:0}")));
        }

        // 标题栏：1.0.0 起由 ArcWindow.TitleBar 提供，不再是我们自建的那条。
        // 断言三件事：它存在、左侧内容已注入、窗口按钮由 Arc 按平台决定。
        if (shell is AppShell barHost)
        {
            var tb = TitleBar!;
            Report("ArcWindow.TitleBar", tb);
            Console.Error.WriteLine(
                $"[shell] 标题栏 CaptionButtons={tb.CaptionButtons}（Auto=macOS 用系统交通灯）");

            if (tb.LeftContent is null)
                Console.Error.WriteLine("[shell] 标题栏 LeftContent **(空)**——收起侧栏按钮没注进去");
            else
                Console.Error.WriteLine("[shell] 标题栏 LeftContent 已注入");

            // 自绘按钮只应出现在"我们自己塞的左侧内容"里；
            // 整条标题栏上的窗口按钮由 Arc 管，我们不该再画一套。
            var ownButtons = barHost.TitlebarContent?.GetVisualDescendants()
                .OfType<Avalonia.Controls.Button>().Count() ?? 0;
            // 期望值按平台分：dsh 的「应用/编辑」只在 Windows 有
            // （preload-windows.ts 在非 win32 直接 return）。
            var expected = OperatingSystem.IsWindows() ? 3 : 1;
            Console.Error.WriteLine(
                $"[shell] 标题栏 LeftContent 内按钮 {ownButtons} 个（期望 {expected}：" +
                (OperatingSystem.IsWindows() ? "收起侧栏 + 应用 + 编辑）" : "仅收起侧栏）"));

            var sidebar = _conversation.GetVisualDescendants()
                .OfType<TKCode.Arc.Sidebar>().FirstOrDefault();
            if (sidebar is not null)
            {
                var sbBrush = sidebar.Background as Avalonia.Media.ISolidColorBrush;
                Console.Error.WriteLine(
                    $"[shell] 侧栏实际底色 {sbBrush?.Color.ToString() ?? "(无)"} " +
                    $"期望 {DshTheme.SidebarFill}");
            }
        }

        // dsh 配色是否真的应用：读实际 Background，不看代码写了什么
        var bg = Background as Avalonia.Media.ISolidColorBrush;
        Console.Error.WriteLine(
            $"[shell] 底色 {bg?.Color.ToString() ?? "(无)"} 深色={DshTheme.IsDark} " +
            $"（dsh light={Ui.Core.Theme.DshTokens.Light.DswAliasBgBase} " +
            $"dark={Ui.Core.Theme.DshTokens.Dark.DswAliasBgBase}）");

        // dsh 对照：三栏宽度必须等于求解器结果（不是"看起来差不多"）
        if (shell is AppShell host)
        {
            var lay = host.Layout;
            Console.Error.WriteLine(
                $"[shell] dsh 三栏 sidebar={lay.Sidebar:0} center={lay.Center:0} rightbar={lay.Rightbar:0}" +
                $"（期望 sidebar {Columns.SidebarDefault:0} / 右栏 {(host.IsRightbarShown ? "45%" : "0")}）");
        }
        // 按类型取 FirstOrDefault 会拿到消息流里的 ItemsControl（那是最大的一个），
        // 不是底部条。改成从 AppShell 拿它自己持有的引用。
        if (shell is AppShell appShell)
        {
            Report("Bottom(list)", appShell.BottomBar);
            Report("EngineStatusBar", appShell.StatusBar);
            // **List 的 Bounds 是内容高度，不是视口**。实测踩过：
            // 看到 List=658 就以为布局溢出（因为 658+174+161+40 > 820），
            // 实际视口只有 356，658 是"可滚动的内容总高"。
            // 判断布局是否溢出要看 Scroll 的视口，不是 List 的 Bounds。
            Report("Conversation.List", _conversation.List);
            Report("Conversation.Scroll", _conversation.Scroll);
            Console.Error.WriteLine($"[shell] 消息条目数 {_conversation.Count}");
        }

        // 三栏不用 Arc 的 ResizablePanels：dsh 的列宽是"像素 + 求解顺序"，
        // 而 ResizablePanels 用百分比（见 AppShell.cs 三栏处注释）。
        // 所以这里数到 0 是**预期**，这条诊断只用来确认没有半路又换回 Arc。
        var panels = shell?.GetVisualDescendants().OfType<ResizablePanel>().ToArray() ?? [];
        Console.Error.WriteLine(
            $"[shell] ResizablePanel 数量 {panels.Length}" +
            (panels.Length == 0 ? "（预期 0：三栏是 Grid+像素求解）" : "（**意外**：三栏应走 Grid）"));
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
            var sbg = sidebar.Background as Avalonia.Media.ISolidColorBrush;
            Console.Error.WriteLine(
                $"[shell] Sidebar.Background={sbg?.Color.ToString() ?? "(null)"} " +
                $"期望={DshTheme.SidebarFill}");
            var kids = sidebar.GetVisualChildren().Count();
            Console.Error.WriteLine(
                $"[shell] Sidebar 内部 {(kids > 0 ? "OK" : "空壳")} visualChildren={kids}");
        }

        // 消息条目同理：条目数对但容器数为 0，说明模板没匹配上（P3a 踩过）。
        Console.Error.WriteLine(
            $"[shell] 消息容器 {_conversation.RenderedContainerCount} / 条目 {_conversation.Count}");

        // 消息反馈（P8b）：反馈条要出现在**可见的**已完成消息上。
        //
        // 不能拿"总 assistant 数 − 流式数"当期望值：消息流是虚拟化的，
        // 视口外的条目不构建控件。第一次这么写得到"期望 2 实际 1"，
        // 差点误判成漏建——实际是第三条（30 行长文本）滚出了视口。
        //
        // 正确口径：可见的 assistant 容器里，非流式几个就该有几个反馈条。
        var visibleAssistant = _conversation.GetVisualDescendants()
            .OfType<Border>()
            .Count(b => b.Child is StackPanel sp && sp.Children.OfType<Markdown>().Any());
        var bars = _conversation.GetVisualDescendants().OfType<FeedbackBar>().Count();
        // "@" 引用项：要确认它们真的装进去了（SetContextItems 之前没有调用者）
        var mentions = _conversation.Composer.Inner.ContextItems.Count;
        Console.Error.WriteLine(
            $"[shell] @ 引用项 {mentions} 个（期望 > 0，来自 fs/list）");

        Console.Error.WriteLine(
            $"[shell] 反馈条 {bars} 个 / 可见 assistant 容器 {visibleAssistant} 个" +
            (bars <= visibleAssistant ? "（不多于可见容器，符合预期）" : "（**多于可见容器，可疑**）"));

        // P8g 中区会话域：这几个元素都是新加的，必须能在 dump 里看到，
        // 否则"接进装配但没验证"——这一类本仓库已经踩过多次。
        var actions = _conversation.GetVisualDescendants().OfType<MessageActions>().ToArray();
        var bottomButton = _conversation.GetVisualDescendants()
            .OfType<Avalonia.Controls.Button>()
            .FirstOrDefault(b => b.Width == ConversationMetrics.ScrollToBottomSize);
        // 空状态 Hero：判的是**整块是否真的显示**，不是里面某个 TextBlock 的标志。
        //
        // 第一版查 `TextBlock.IsVisible`，得到"13 条消息时 Hero 仍显示"的假警报：
        // 父容器隐藏后，子元素的 IsVisible 仍是 true——`IsVisible` 是**自身标志**，
        // 不等于"实际可见"。要看渲染树上是否真有它，用 IsEffectivelyVisible。
        var heroTitle = _conversation.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == "探索未至之境");
        var heroVisible = heroTitle?.IsEffectivelyVisible == true;
        var mask = _conversation.GetVisualDescendants()
            .OfType<Border>()
            .Count(b => Math.Abs(b.Height - ConversationMetrics.ComposerMaskHeight) < 0.5);

        // **判据而不是日志**：下面三行带 OK/坏 的标记，是为了让人扫一眼看出
        // 哪个坏了；但真正把它当检查用的前提是"读得懂坏的表现"。
        // 曾经这里全是纯打印，曲线救国地"看过一眼就算验了"。
        //
        // **口径**：动作行只会出现在用户消息上，而虚拟化列表不构建视口外
        // 的条目。所以"0 个"不等于没接上——窄栏下用户消息可能早已滚出视口。
        //
        // 判据改成：**可见的用户气泡**有几个，动作行就该有几个。
        // 气泡的可靠标识是它绑定了 MessageActions 兄弟节点（不是靠 FontSize 猜）。
        var userBubbles = _conversation.GetVisualDescendants()
            .OfType<Border>()
            .Count(b => b.Child is StackPanel sp
                        && sp.Children.OfType<MessageActions>().Any());
        var actionsOk = actions.Length == userBubbles;

        Console.Error.WriteLine(
            $"[shell] 用户消息 气泡 {userBubbles} / 动作行 {actions.Length}" +
            (actions.Length > 0 ? $" 首个：复制态={actions[0].ShowingCopied}" : "") +
            (actionsOk ? " OK" : "（**气泡与动作行数不符 = 复制功能没接上**）"));

        Console.Error.WriteLine(
            $"[shell] 空状态 Hero {(_conversation.Count == 0 ? "应显示" : "应隐藏")}" +
            $" 实际={(heroVisible ? "显示" : "隐藏")}" +
            ((heroVisible == (_conversation.Count == 0)) ? " OK" : "（**与条目数不符**）") +
            $" / 渐变遮罩 {mask} 个" + (mask == 1 ? " OK" : "（**遮罩数量不对**）"));

        Console.Error.WriteLine(
            $"[shell] 回到底部按钮 {(bottomButton is null ? "**缺失**" : $"{bottomButton.Width:0}×{bottomButton.Height:0} OK")}" +
            $" / 可见性={(bottomButton?.IsVisible == true ? "显示" : "隐藏")}");

        // P1：流式状态行与已停止标记。
        var runLine = _conversation.GetVisualDescendants().OfType<RunStatusLine>().FirstOrDefault();
        var stoppedBadges = _conversation.GetVisualDescendants().OfType<TextBlock>()
            .Count(t => t.Text == "已停止" && t.IsEffectivelyVisible);

        Console.Error.WriteLine(
            $"[shell] 运行状态行 {(runLine is null ? "**缺失**" : "存在")}" +
            $" 可见={(runLine?.IsEffectivelyVisible == true ? "是" : "否")}" +
            (string.IsNullOrEmpty(_conversation.RunStatusText) ? "" : $" 文本=\"{_conversation.RunStatusText}\"") +
            $" / 已停止标记 {stoppedBadges} 个");

        // 贴底：内容高于视口时**必须**在底部。
        // 这是 P8g 里唯一有真实失败史的判据——第一版写完运行时
        // 停在距底 572px，界面看起来"滚了一点但没到底"，
        // 而所有静态检查都是绿的。
        //
        // **只在窗口已显示时判**：Opened 里第一次 dump 时布局还没跑完
        // （Extent 已有值但 Offset 必然是 0），那时报"没在底部"是误报。
        // 误报比漏报更伤——它教会人忽略这条输出。
        var dist = _conversation.Scroll.Extent.Height
                   - _conversation.Scroll.Offset.Y
                   - _conversation.Scroll.Viewport.Height;
        var overflow = _conversation.Scroll.Extent.Height > _conversation.Scroll.Viewport.Height;
        var atBottom = !overflow || Math.Abs(dist) <= ConversationMetrics.FollowThreshold;

        Console.Error.WriteLine(
            $"[shell] 贴底状态={(_conversation.IsStickingToBottom ? "跟随中" : "已暂停")}" +
            $"，偏移={_conversation.Scroll.Offset.Y:0.#}/{_conversation.Scroll.Extent.Height:0.#}" +
            $" 视口={_conversation.Scroll.Viewport.Height:0.#}，距底={dist:0.#}" +
            (!layoutSettled
                ? "（布局未完成，不判定）"
                : atBottom ? " OK" : "（**溢出却没在底部**）"));

        // 右栏 dock（P7）：面板展开不够，要看到 tab 数与内容真的建出来了。
        // P3a 踩过"尺寸全对但里面是空的"，右栏同样可能重演。
        //
        // **两种承载方式要分开报**：Dock 模式下根本没有 `TabItem`，
        // 照旧报"tab 0 个（期望 5）"会把人引向错误结论——第一次跑
        // DOUGONG_UI_DOCK=1 就出现了这条假警报。
        if (_rightDock.DockPanes is { } panes)
        {
            var dockRoot = (_rightDock.Content as DockLayout)?.Root?.GetType().Name ?? "(null)";
            Console.Error.WriteLine(
                $"[shell] RightDock Dock 模式：pane {panes.Count} 个，" +
                $"根={dockRoot}，当前={_rightDock.ActiveTab}");
        }
        else
        {
            var tabs = _rightDock.GetVisualDescendants().OfType<TKCode.Arc.TabItem>().ToArray();
            Console.Error.WriteLine(
                $"[shell] RightDock tab {tabs.Length} 个（期望 5），" +
                $"可用 {tabs.Count(t => t.IsEnabled)}，当前={_rightDock.ActiveTab}，" +
                $"内容={(tabs.FirstOrDefault(t => t.IsSelected)?.Content is null ? "**空**" : "有")}");
        }

        // 审批卡片（P6）：卡片在、尺寸非零、按钮数量正确。
        // 审批坏了的表现是"回合静默卡住"，用户完全看不出是卡片没渲染。
        var approvals = _conversation.PendingApprovals.Values.ToArray();
        foreach (var card in approvals)
        {
            Report($"ApprovalCard", card);
            var buttons = card.GetVisualDescendants().OfType<TKCode.Arc.Button>().Count();
            Console.Error.WriteLine(
                $"[shell] 审批卡片 {card.RequestId} 按钮 {buttons} 个" +
                $"（期望 4）过期={card.IsExpired}");
        }
        if (approvals.Length == 0)
            Console.Error.WriteLine("[shell] 审批卡片            (无待决)");

        // 输入区（P6）：光有实例不算数，要确认它有非零尺寸、且锁状态正确。
        // AiComposer 是复合控件，尺寸为 0 时的表现是"输入框完全看不见"，
        // 而那在截图里容易被当成布局留白。
        Report("Composer", _conversation.Composer);
        Console.Error.WriteLine(
            $"[shell] Composer 尺寸 {_conversation.Composer.Inner.Bounds.Width:0} x " +
            $"{_conversation.Composer.Inner.Bounds.Height:0} " +
            $"待决锁={_conversation.Composer.IsApprovalPending}");

        // 工具卡片（P5）：确认 Input 真解析出键值对、Duration 真按规则映射。
        // 光看"卡片在"不够——Input 解析失败和"这个工具本来就没参数"在界面上一样。
        //
        // **0 张不等于坏了**：消息流是虚拟化的，视口外的条目不构建控件。
        // 视口只有 353px 时，排在中间的工具卡本来就看不见。
        // 用 DOUGONG_UI_TOOLFIRST=1 把工具条目前置，才能真正验证构建路径。
        var toolCards = all.OfType<TKCode.Arc.ToolCall>().ToArray();
        var withInput = toolCards.Count(c => c.Item?.Input?.Count > 0);
        var withDuration = toolCards.Count(c => c.Item?.Duration is not null);
        var withSummary = toolCards.Count(c => c.Item?.Summary is { Length: > 0 });
        var dataTools = _conversation.Items.Count(i => i is IpcToolCallItem);
        Console.Error.WriteLine(
            $"[shell] ToolCall {toolCards.Length} 张" +
            $"（有 Input {withInput}、有 Duration {withDuration}、有 Summary {withSummary}）" +
            $" / 数据侧工具条目 {dataTools} 条" +
            (toolCards.Length == 0 && dataTools > 0 ? "（视口外未构建，可加 DOUGONG_UI_TOOLFIRST=1 验证）" : ""));

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
