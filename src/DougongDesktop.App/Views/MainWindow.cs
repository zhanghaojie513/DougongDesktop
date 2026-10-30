using Avalonia.Controls;
using Avalonia.VisualTree;
using DougongDesktop.App.Services;
using DougongDesktop.App.Shell;
using DougongDesktop.Ipc;
using IpcToolCallItem = DougongDesktop.Ipc.ToolCallItem;
using IpcToolCallStatus = DougongDesktop.Ipc.ToolCallStatus;
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
    private readonly EngineClient _engine = new(msg => Console.Error.WriteLine($"[ui] {msg}"));
    private readonly Dictionary<string, IDisposable> _handles = new(StringComparer.Ordinal);
    private string? _connectionDetail;
    private string? _selectedSessionId;
    private IReadOnlyList<SessionSummary>? _currentSessions;
    private readonly StatusCapsule _capsule = new();
    private readonly ConversationView _conversation = new();

    /// <summary>右栏 dock（P7）。</summary>
    private readonly RightDock _rightDock = new();

    private readonly UiExtensionLoader _uiPlugins;

    public MainWindow()
    {
        Title = "Dougong Desktop";
        Width = 1280;
        Height = 820;

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

        Content = new AppShell(_slots, _vm);
        Shell = (AppShell)Content;

        // 输入区 → 引擎。三段式：确保有会话 → 发回合 → 把本地消息先显示出来
        _conversation.MessageSubmitted += text => _ = SubmitAsync(text);
        _conversation.StopRequested += () => _ = StopTurnAsync();

        LoadUiPlugins();

        // 自检模式：没 provider 时也能验证消息流渲染
        if (Environment.GetEnvironmentVariable("DOUGONG_UI_SELFTEST") is "1")
            _conversation.LoadSelfTestData();

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

        // 右栏文件 tab 一打开就要有内容，不等用户手动切
        await RequestDirectoryAsync(string.Empty);
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
                break;

            case TurnStartedEvent:
                _capsule.OnTurnStarted();
                // 生成中：发送键变停止键（Arc 的 AiComposer 自带这个切换）
                _conversation.Composer.SetGenerating(true);

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

        // 右栏 dock（P7）：面板展开不够，要看到 tab 数与内容真的建出来了。
        // P3a 踩过"尺寸全对但里面是空的"，右栏同样可能重演。
        var tabs = _rightDock.GetVisualDescendants().OfType<TKCode.Arc.TabItem>().ToArray();
        Console.Error.WriteLine(
            $"[shell] RightDock tab {tabs.Length} 个（期望 5），" +
            $"可用 {tabs.Count(t => t.IsEnabled)}，当前={_rightDock.ActiveTab}，" +
            $"内容={(tabs.FirstOrDefault(t => t.IsSelected)?.Content is null ? "**空**" : "有")}");

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
        var toolCards = all.OfType<TKCode.Arc.ToolCall>().ToArray();
        var withInput = toolCards.Count(c => c.Item?.Input?.Count > 0);
        var withDuration = toolCards.Count(c => c.Item?.Duration is not null);
        var withSummary = toolCards.Count(c => c.Item?.Summary is { Length: > 0 });
        Console.Error.WriteLine(
            $"[shell] ToolCall {toolCards.Length} 张" +
            $"（有 Input {withInput}、有 Duration {withDuration}、有 Summary {withSummary}）");

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
