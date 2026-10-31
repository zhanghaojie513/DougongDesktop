using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Collections;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;
using TKCode.Arc;
using IpcToolCallItem = Dougong.HostProtocol.ToolCallItem;
using IpcToolCallStatus = Dougong.HostProtocol.ToolCallStatus;
using ArcToolCallItem = TKCode.Arc.ToolCallItem;
using ArcToolCallStatus = TKCode.Arc.ToolCallStatus;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 消息流：按条目类型分派模板，虚拟化渲染。
/// </summary>
/// <remarks>
/// <b>为什么自建 <see cref="ItemsControl"/> 而不用 <c>arc:ChatThread</c></b>：
/// ChatThread 不支持自定义条目模板（它的文档原话："the control builds its own rows,
/// so you supply data, not item templates"），只能放文本/附件/表情。
/// 而 agent 客户端必须在对话里穿插工具卡片、审批、计划——它做不到。
/// <para>
/// <b>未知条目不丢弃、不崩溃</b>：降级为通用卡片。这是 dsh 与 Codex 的共同做法
/// （未注册的工具走 generic card），也是 IPC 契约演进的前提——
/// 服务端加新 kind 时旧客户端不该崩。
/// </para>
/// </remarks>
public sealed class ConversationView : UserControl
{
    private readonly ItemsControl _list;

    /// <summary>内部列表，供诊断读取实际尺寸与条目数。</summary>
    public ItemsControl List => _list;

    /// <summary>当前条目数。</summary>
    public int Count => _items.Count;

    /// <summary>
    /// 已实例化的条目容器数。
    /// </summary>
    /// <remarks>
    /// <b>存在的理由是区分两种失败</b>：条目数对但容器数为 0，说明数据进来了、
    /// 模板没匹配上（P3a 的 <c>FuncDataTemplate&lt;object&gt;</c> 踩过，ContentPresenter
    /// 建了但里面是空的）；两者都不为 0 且容器数远小于条目数，才是虚拟化生效。
    /// </remarks>
    public int RenderedContainerCount =>
        _list.GetVisualDescendants().OfType<ContentPresenter>().Count();
    /// <summary>输入区。宿主用它切换生成态与待决锁。</summary>
    public Composer Composer { get; } = new();

    /// <summary>
    /// 顶部信息栏（会话标题 + 状态词 + 右侧工具栏）。
    /// </summary>
    /// <remarks>
    /// dsh 的 <c>.header</c>：<c>min-height: 76px</c>、
    /// <c>padding: 10px 28px 0 20px</c>、底部 <c>0.5px solid border-l3</c>，
    /// 两列 <c>auto minmax(0,1fr)</c>。
    /// <para>
    /// <b>76px 这个高度不是随便定的</b>：它等于侧栏的 tab strip（38px）+
    /// 面板头（38px），这样两边的分隔线能在列边缘对齐。我们的侧栏没有那两层，
    /// 所以只取内容需要的高度，不强凑 76——强凑会在顶部留一块空白。
    /// </para>
    /// </remarks>
    public Border Header { get; } = new();

    /// <summary>审批卡片区：在消息流与输入区之间，常驻、不参与虚拟化。</summary>
    private readonly StackPanel _approvalHost = new() { Spacing = 0 };

    private readonly ScrollViewer _scroll;
    private readonly TextBlock _emptyHint;

    /// <summary>当前显示的条目（按 Id 索引，便于流式更新时就地替换）。</summary>
    private readonly List<ConversationItem> _items = [];

    /// <summary>
    /// 自动贴底开关。
    /// </summary>
    /// <remarks>
    /// 用户往上滚看历史时不该被新消息拽回底部——那是最烦人的交互之一。
    /// 只有"本来就在底部附近"时才自动跟随。
    /// </remarks>
    private bool _stickToBottom = true;

    public ConversationView()
    {
        _list = new ItemsControl
        {
            ItemsSource = _items,
            ItemTemplate = new FuncDataTemplate<ConversationItem>((item, _) =>
                item is null ? null : BuildItem(item)),
            // 虚拟化：1000 条不进视觉树，只构建可见的那部分
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>( 
                () => new VirtualizingStackPanel()),
            Margin = new Thickness(0),
        };

        _emptyHint = new TextBlock
        {
            Text = "还没有消息。在下方输入开始对话。",
            FontSize = 13,
            Opacity = 0.45,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = true,
        };

        _scroll = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        _scroll.ScrollChanged += (_, _) =>
        {
            // 距底部 80px 以内算"在底部"
            var distance = _scroll.Extent.Height - _scroll.Offset.Y - _scroll.Viewport.Height;
            _stickToBottom = distance < 80;
        };

        var grid = new Grid();
        grid.Children.Add(_scroll);
        grid.Children.Add(_emptyHint);

        // 输入区固定在下方，消息流占剩余空间。
        // 用 Grid 两行而不是把 Composer 叠在 ScrollViewer 上——叠上去会遮住
        // 最后几条消息，而"审批卡片被输入框挡住"正是 P6 判据明确要避免的。
        BuildHeader();

        // 顶部信息栏：dsh 的 .header 是 76px 高、padding 10/28/0/20、
        // 底部 0.5px 分隔线、两列（标题区 + 右侧工具栏）。
        // 没有它时主区从窗口顶端直接开始，与 dsh 差一条明显的横线。
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };

        Grid.SetRow(Header, 0);
        root.Children.Add(Header);

        Grid.SetRow(grid, 1);
        root.Children.Add(grid);

        // 审批区在消息流下方、输入区上方：不遮挡输入框（P6 判据），
        // 也不被虚拟化回收
        Grid.SetRow(_approvalHost, 2);
        root.Children.Add(_approvalHost);

        Grid.SetRow(Composer, 3);
        root.Children.Add(Composer);

        Content = root;

        // 发送 / 停止转发给宿主：本类不认识引擎，只负责把意图交出去
        Composer.Submitted += text => MessageSubmitted?.Invoke(text);
        Composer.StopRequested += () => StopRequested?.Invoke();
    }


    /// <summary>用户在输入区提交了一条消息。</summary>
    public event Action<string>? MessageSubmitted;

    /// <summary>用户请求停止当前回合。</summary>
    public event Action? StopRequested;

    /// <summary>
    /// 灌入自检数据，验证每种条目的渲染。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要自检模式</b>：没有 provider 时跑不了真实对话，
    /// 而消息流的核心是"每种条目能否正确渲染"——这不需要真实模型。
    /// 它还能覆盖真实对话里难复现的边界：未知 kind、Unknown 状态、超长工具输出。
    /// <para>
    /// 用 <c>DOUGONG_UI_SELFTEST=1</c> 启用。
    /// </para>
    /// </remarks>
    public void LoadSelfTestData()
    {
        // 自检数据里放一条审批，让"审批卡片是否真的渲染出来"可观测。
        // 审批是 P6 唯一会阻塞回合的交互，坏了没人会发现——用户只会以为卡了。
        //
        // 注意：这条只验证**卡片渲染**，不走 MainWindow.ShowApproval，
        // 所以 dump 里"待决锁=False"是符合预期的——锁由宿主在真实事件里上。
        // 真实审批链路的检查在 ApprovalChecks（断言锁与卡片互斥）。
        AddApproval(new ApprovalCard(new Dougong.HostProtocol.ApprovalRequest(
            RequestId: "req-selftest",
            SessionId: "sess-selftest",
            TurnId: "turn-selftest",
            InvocationId: "inv-selftest",
            ToolName: "write_file",
            Summary: "写入文件 README.md",
            ArgumentsJson: "{\"path\":\"README.md\",\"content\":\"…\"}",
            PermissionScope: "Write",
            ExpiresAt: DateTimeOffset.Now.AddMinutes(5),
            MayHaveIrreversibleEffects: true)));

        var now = DateTimeOffset.Now;
        var seq = 0;
        string NextId() => $"selftest-{++seq}";

        Add(new UserMessageItem { Id = NextId(), At = DateTimeOffset.Now, Text = "帮我读一下 README.md" });

        Add(new AssistantMessageItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Text = "好的，我先读文件。**这是加粗**，`这是行内代码`。\n\n```csharp\nvar x = 1;\n```",
        });

        // 流式条目：验证 Stream 路径（Push + Complete）
        Add(new AssistantMessageItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Text = "## 流式渲染验证\n\n这段文字通过 **MarkdownStream.Push** 推入，"
                 + "不是重设 Text。如果标题和加粗都正常，说明 Stream 路径可用。",
            IsStreaming = true,
        });

        // 工具：五种状态各来一个
        foreach (var (status, label) in new[]
        {
            (IpcToolCallStatus.Succeeded, "成功"),
            (IpcToolCallStatus.Failed, "失败"),
            (IpcToolCallStatus.Rejected, "被拒绝"),
            (IpcToolCallStatus.Cancelled, "已取消"),
            (IpcToolCallStatus.Unknown, "结果未知"),
        })
        {
            Add(new IpcToolCallItem
            {
                At = DateTimeOffset.Now,
                Id = NextId(),
                InvocationId = $"inv-{label}",
                ToolName = "read_file",
                ArgumentsJson = "{\"path\":\"README.md\"}",
                ResultText = $"（{label} 的输出）",
                Diagnostic = status == IpcToolCallStatus.Unknown
                    ? "结果未知：可能已执行，请人工核实，不要重试。"
                    : null,
                Status = status,

                // 两段耗时：成功的那条两段都有，其余只演一种情形，
                // 用来验证 §6.6 的四行映射规则都显示对。
                ApprovalElapsed = status switch
                {
                    IpcToolCallStatus.Succeeded => TimeSpan.FromMilliseconds(1200),
                    IpcToolCallStatus.Rejected => TimeSpan.FromMilliseconds(3400),
                    _ => null,
                },
                ExecutionElapsed = status switch
                {
                    IpcToolCallStatus.Succeeded => TimeSpan.FromMilliseconds(45),
                    IpcToolCallStatus.Failed => TimeSpan.FromMilliseconds(12),
                    _ => null,
                },
            });
        }

        // JSON 输出：验证 Arc 的 JsonOutput 着色与十行折叠
        Add(new IpcToolCallItem
        {
            At = DateTimeOffset.Now,
            Id = NextId(),
            InvocationId = "inv-json",
            ToolName = "read_config",
            ArgumentsJson = "{\"path\":\"appsettings.json\",\"depth\":2}",
            ResultText = "{\"name\":\"dougong\",\"version\":\"0.1.5\",\"features\":[\"ipc\",\"slots\",\"plugins\"],"
                         + "\"limits\":{\"context\":200000,\"output\":8192}}",
            Status = IpcToolCallStatus.Succeeded,
            ExecutionElapsed = TimeSpan.FromMilliseconds(8),
        });

        Add(new PlanItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Steps =
            [
                new PlanStep("读取文件", "completed", "high"),
                new PlanStep("分析内容", "in_progress", "medium"),
                new PlanStep("给出建议", "pending", "low"),
            ],
        });

        Add(new NoticeItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Level = NoticeLevel.Warning,
            Text = "上下文已压缩：移除 12 条消息，约 48000 → 12000 tokens。模型看到的不再是完整历史。",
        });

        // 推理内容：SDK 0.1.1 起有 ReasoningDelta 实时事件，
        // 但是否出现取决于 provider（只有思考模式的模型才给）
        Add(new ReasoningItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Text = "（推理示例）用户要看的是默认布局对不对。先确认三栏尺寸，"
                 + "再看消息流里每种条目是否都渲染。等等——这里要注意 JSON 输出那一档，"
                 + "它走的是折叠路径，和纯文本不一样。",
        });

        Add(new AssistantMessageItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Text = string.Join(Environment.NewLine,
                Enumerable.Range(1, 30).Select(i => $"第 {i} 行：用于测试长文本与滚动。")),
        });

        Console.Error.WriteLine($"[ui] 自检数据已灌入：{_items.Count} 条");
    }

    /// <summary>
    /// 灌入压力数据，验证虚拟化。
    /// </summary>
    /// <remarks>
    /// <b>为什么单独一个模式</b>：1000 条消息的滚动性能不能靠看——必须看
    /// 视觉树里实际构建了多少个容器。虚拟化生效时，1000 条只该有
    /// 十几到几十个可视化条目；没生效时会构建全部 1000 个。
    /// <para>
    /// 用 <c>DOUGONG_UI_STRESS=1000</c> 启用（数字为条数）。
    /// </para>
    /// </remarks>
    public void LoadStressData(int count)
    {
        var started = DateTimeOffset.Now;

        for (var i = 1; i <= count; i++)
        {
            // 混合条目类型，更接近真实负载
            if (i % 10 == 0)
            {
                _items.Add(new IpcToolCallItem
                {
                    At = DateTimeOffset.Now,
                    Id = $"stress-{i}",
                    InvocationId = $"inv-{i}",
                    ToolName = "read_file",
                    ArgumentsJson = "{}",
                    ResultText = $"第 {i} 条工具输出",
                    Status = IpcToolCallStatus.Succeeded,
                });
            }
            else
            {
                _items.Add(new AssistantMessageItem
                {
                    At = DateTimeOffset.Now,
                   Id = $"stress-{i}",
                    Text = $"第 {i} 条消息。用于验证虚拟化：如果 1000 条全部构建，"
                         + "视觉树会膨胀到几千个节点，滚动明显发卡。",
                });
            }
        }

        Refresh();

        var elapsed = DateTimeOffset.Now - started;
        Console.Error.WriteLine($"[stress] 灌入 {count} 条，耗时 {elapsed.TotalMilliseconds:0}ms");
    }

    /// <summary>清空（切换会话时）。</summary>
    public void Clear()
    {
        _items.Clear();
        _list.ItemsSource = null;
        _list.ItemsSource = _items;
        _emptyHint.IsVisible = true;
        _stickToBottom = true;
    }

    /// <summary>追加条目。</summary>
    public void Add(ConversationItem item)
    {
        _items.Add(item);
        Refresh();

        if (_stickToBottom) ScrollToBottom();
    }

    /// <summary>
    /// 插入一张审批卡片。
    /// </summary>
    /// <remarks>
    /// <b>审批卡片不是 <see cref="ConversationItem"/></b>：它是**交互控件**
    /// （按钮、定时器、答复回调），塞进纯数据条目会让 IPC 契约沾上 UI 概念。
    /// 所以它放在消息流下方的独立区域，不进虚拟化列表。
    /// <para>
    /// <b>为什么在列表外</b>：虚拟化列表只构建可见条目，把带定时器的交互控件
    /// 放进去，滚出视口就会被回收——定时器还在跑，控件已经不在树上，
    /// 表现为"审批莫名其妙消失了"。审批是阻塞性的，必须常驻。
    /// </para>
    /// </remarks>
    public void AddApproval(ApprovalCard card)
    {
        _approvalCards[card.RequestId] = card;
        _approvalHost.Children.Add(card);

        // 强制滚到底：审批不处理回合就停着，出现在视口外等于让会话静默卡住
        ScrollToBottom();
    }

    /// <summary>移除一张审批卡片（已答复）。</summary>
    /// <remarks>
    /// <b>答复后保留显示、只把按钮换成结果文字</b>，不整个删掉——
    /// 用户需要看到"我刚才拒了哪个调用"，那是回合记录的一部分。
    /// 这里只把它从"待决"集合里摘掉。
    /// </remarks>
    public void ResolveApproval(string requestId) => _approvalCards.Remove(requestId);

    /// <summary>当前未答复的审批。</summary>
    public IReadOnlyDictionary<string, ApprovalCard> PendingApprovals => _approvalCards;

    private readonly Dictionary<string, ApprovalCard> _approvalCards = new(StringComparer.Ordinal);

    /// <summary>
    /// 更新条目（流式文本追加、工具状态变化）。
    /// </summary>
    /// <remarks>
    /// <b>就地替换而不是重建控件</b>：重建会丢滚动位置，而且流式文本每 100ms 追加一次，
    /// 重建会导致明显的闪烁。
    /// <para>
    /// 这里换的是列表里的对象引用，<see cref="ItemsControl"/> 会复用已有容器，
    /// 只有模板内部绑定的值变化。
    /// </para>
    /// </remarks>
    public void Update(ConversationItem item)
    {
        var index = _items.FindIndex(i => i.Id == item.Id);
        if (index < 0)
        {
            Add(item);
            return;
        }

        _items[index] = item;
        Refresh();
    }

    private void Refresh()
    {
        // List<T> 不触发集合通知，重建 ItemsSource 强制刷新。
        // 真正的虚拟化在 ItemsPanel，重建 source 不会重建所有容器。
        _list.ItemsSource = null;
        _list.ItemsSource = _items;

        _emptyHint.IsVisible = _items.Count == 0;
    }

    private void ScrollToBottom() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _scroll.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);

    // ── 条目构建：按 kind 分派 ──────────────────────────────

    /// <summary>
    /// 建顶部信息栏。
    /// </summary>
    /// <remarks>
    /// 形态照 dsh：左边是会话标题 + 状态词（"你好 · 标准模式"），
    /// 右边是工具栏（文件夹 / 更多 / 刷新 / 侧栏切换）。
    /// <para>
    /// <b>会话标题暂时用静态占位</b>：它应该来自当前会话的摘要，
    /// 而 <see cref="ConversationView"/> 拿不到会话列表（那是主窗口的事）。
    /// 先放固定文字，等 P8d 的会话增强把标题流接进来再替换。
    /// 宁可显示一个明确的占位，也不要显示空——空白让人以为加载失败。
    /// </para>
    /// </remarks>
    private void BuildHeader()
    {
        Header.Padding = new Thickness(20, 10, 28, 10);
        Header.BorderBrush = new SolidColorBrush(DshTheme.BorderL3);
        Header.BorderThickness = new Thickness(0, 0, 0, 0.5);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        // 标题
        var title = new TextBlock
        {
            Text = "会话",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        row.Children.Add(title);

        // 状态词：dsh 这里是"标准模式"
        var mode = new TextBlock
        {
            Text = "标准模式",
            FontSize = 12,
            Opacity = 0.55,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(mode, 1);
        row.Children.Add(mode);

        // 右侧工具条
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var kind in new[]
        {
            TKCode.Arc.Icons.Lucide.LucideIconKind.Folder,
            TKCode.Arc.Icons.Lucide.LucideIconKind.Ellipsis,
            TKCode.Arc.Icons.Lucide.LucideIconKind.RotateCw,
            TKCode.Arc.Icons.Lucide.LucideIconKind.PanelRight,
        })
        {
            tools.Children.Add(HeaderToolButton(kind));
        }

        Grid.SetColumn(tools, 2);
        row.Children.Add(tools);

        Header.Child = row;
    }

    /// <summary>顶栏的纯图标按钮。</summary>
    /// <remarks>
    /// 功能待接（P8d）。先让形态对上——工具栏的位置与图标是
    /// "看起来像不像 dsh"的一部分，而它们不影响任何人点击其他东西。
    /// </remarks>
    private static Control HeaderToolButton(TKCode.Arc.Icons.Lucide.LucideIconKind kind)
    {
        var button = new Avalonia.Controls.Button
        {
            Content = new TKCode.Arc.Icons.Lucide.LucideIcon
            {
                Kind = kind,
                Size = 16,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Columns.RadiusSm),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
        };

        button.PointerEntered += (_, _) => button.Background = new SolidColorBrush(
            Color.FromArgb(20, 0, 0, 0));
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;

        return button;
    }

    private static Control BuildItem(ConversationItem item) => item switch
    {
        UserMessageItem user => BuildUserMessage(user),
        AssistantMessageItem assistant => BuildAssistantMessage(assistant),
        IpcToolCallItem tool => BuildToolCall(tool),
        PlanItem plan => BuildPlan(plan),
        NoticeItem notice => BuildNotice(notice),
        ReasoningItem reasoning => BuildReasoning(reasoning),

        // 未知 kind：不崩溃、不丢弃，降级为通用卡片
        _ => BuildUnknown(item),
    };

    private static Control BuildUserMessage(UserMessageItem item) => new Border
    {
        Margin = new Thickness(60, 6, 12, 6),
        Padding = new Thickness(14, 10),
        CornerRadius = new CornerRadius(12),
        HorizontalAlignment = HorizontalAlignment.Right,
        Background = new SolidColorBrush(Color.FromArgb(18, 47, 111, 94)),
        Child = new TextBlock
        {
            Text = item.Text,
            TextWrapping = TextWrapping.Wrap,   // 长文本换行
            FontSize = 14,
        },
    };

    private static Control BuildAssistantMessage(AssistantMessageItem item)
    {
        var markdown = new Markdown();

        if (item.IsStreaming)
        {
            // 流式：用 Stream 属性拿"只解析增量"的能力。
            // Text 每次重设会重解析全文，长回复时明显（实测 500 段后卡顿）。
            var stream = new StreamMark.Avalonia.MarkdownStream(
                pacing: default, smooth: true, fadeDuration: null, profile: default);
            markdown.Stream = stream;
            stream.Push(item.Text);
        }
        else
        {
            markdown.Text = item.Text;
        }

        return new Border
        {
            Margin = new Thickness(12, 6, 60, 6),
            Padding = new Thickness(2, 4),
            Child = markdown,
        };
    }

    /// <summary>工具输出超过这么多行就折叠。</summary>
    /// <remarks>
    /// <b>折叠而不是截断</b>：截断会丢信息，而排查问题往往要看完整输出。
    /// 折叠让长输出不撑爆界面，同时保持可取回。
    /// </remarks>
    private const int CollapseAfterLines = 8;

    private const char NewLineChar = '\n';
    private const string NewLine = "\n";

    private static Control BuildToolCall(IpcToolCallItem item)
    {
        // 用 Arc 的 ToolCall 卡片；状态由 ToolCallItem.Status 决定。
        // Message 只放一行摘要（诊断原因），完整输出走 Output——
        // 全塞进 Message 会让卡片被长输出撑开。
        var toolItem = new ArcToolCallItem
        {
            Id = item.InvocationId,
            ToolName = item.ToolName,
            Status = MapStatus(item.Status),
            Title = DescribeTitle(item),
            Message = DescribeMessage(item),
            Output = BuildOutput(item.ResultText),
        };

        // Input 是只读集合（没有 AvaloniaProperty，状态变化靠 Id 不重建行），
        // 所以是"构造后往里加"而不是对象初始化器。
        foreach (var argument in ParseInput(item.ArgumentsJson) ?? [])
            toolItem.Input.Add(argument);

        ApplyDuration(toolItem, item);

        return new Border { Margin = new Thickness(12, 4), Child = new ToolCall { Item = toolItem } };
    }

    /// <summary>
    /// 把参数 JSON 解析成 Arc 的键值对列表。
    /// </summary>
    /// <remarks>
    /// <b>显示的是"模型传了什么"，不是原始 JSON</b>：Arc 的 Input 区是
    /// 等宽键值列表，比让用户自己在一行压缩 JSON 里找参数清楚得多。
    /// <para>
    /// <b>解析失败要退回原文而不是显示空白</b>：参数不可解析本身就是重要信息
    /// （可能模型吐了坏 JSON）。吞掉它会让用户以为"这个工具没参数"。
    /// </para>
    /// </remarks>
    private static List<ToolCallArgument>? ParseInput(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson == "{}")
            return null;

        var list = new List<ToolCallArgument>();

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                list.Add(new ToolCallArgument
                {
                    Key = "(参数)",
                    Value = argumentsJson,
                });
                return list;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                list.Add(new ToolCallArgument
                {
                    Key = prop.Name,
                    Value = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.GetRawText(),
                });
            }
        }
        catch (JsonException)
        {
            // 坏 JSON：原样给出，不假装没有参数
            list.Add(new ToolCallArgument { Key = "(无法解析的参数)", Value = argumentsJson });
        }

        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// 按内容形状选 Arc 的输出类型。
    /// </summary>
    /// <remarks>
    /// <b>JSON 用 <see cref="ToolCallJsonOutput"/>，其余用文本</b>：
    /// JSON 那一档自带缩进、着色、超过十行折叠——这些自己实现一遍不会更好。
    /// <para>
    /// <b>判定用"整段能否解析成 JSON 对象或数组"，不是"看起来像 JSON"</b>：
    /// 纯数字、`true`、带引号的字符串都是合法 JSON，但它们当文本显示更自然。
    /// 只认容器类型可以避开这个坑。
    /// </para>
    /// </remarks>
    private static ToolCallOutput? BuildOutput(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return null;

        var trimmed = resultText.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var _ = JsonDocument.Parse(resultText);
                return new ToolCallJsonOutput(JsonNode.Parse(resultText)!);
            }
            catch (JsonException)
            {
                // 不是合法 JSON（可能是被截断的）：退回文本，不吞内容
            }
        }

        return new ToolCallTextOutput(resultText);
    }

    /// <summary>
    /// 把两段耗时映射到 Arc 的单一 <c>Duration</c> + <c>Summary</c>。
    /// </summary>
    /// <remarks>
    /// <b>都是 null 时不设 Duration</b>：Arc 的 Duration 是可空值，null 表示不显示。
    /// 用 0 代替会显示出一个假的"瞬间完成"——§6.6 明确禁止。
    /// <para>
    /// <b>Summary 里保留两段的拆分</b>：Arc 只有一个时长槽位，但"审批慢"和"执行慢"
    /// 的排查方向相反。所以合并总分的同时，把拆分写进 Summary。
    /// </para>
    /// </remarks>
    private static void ApplyDuration(ArcToolCallItem toolItem, IpcToolCallItem item)
    {
        var approval = item.ApprovalElapsed;
        var execution = item.ExecutionElapsed;

        if (approval is null && execution is null) return;   // 不显示，不是显示 0

        toolItem.Duration = (approval ?? TimeSpan.Zero) + (execution ?? TimeSpan.Zero);

        toolItem.Summary = (approval, execution) switch
        {
            ({ } a, { } e) => $"审批 {FormatSpan(a)} / 执行 {FormatSpan(e)}",
            (null, { } e) => $"执行 {FormatSpan(e)}",
            ({ } a, null) => $"等待审批 {FormatSpan(a)}",
            _ => null,
        };
    }

    /// <summary>耗时格式化：小于 1 秒用毫秒，否则用一位小数的秒。</summary>
    /// <remarks>
    /// <b>毫秒不取整到 0</b>：工具执行常在几十毫秒，取整后显示"0ms"
    /// 会让人以为没执行。
    /// </remarks>
    private static string FormatSpan(TimeSpan span) => span.TotalSeconds >= 1
        ? $"{span.TotalSeconds:0.0}s"
        : $"{Math.Max(1, (int)span.TotalMilliseconds)}ms";

    /// <summary>
    /// 卡片的补充说明。
    /// </summary>
    /// <remarks>
    /// <b>结果未知的文案必须压过诊断信息</b>：两者都有时，先要让用户看到
    /// "不要重试"——诊断细节是给排查用的，放后面。
    /// </remarks>
    private static string? DescribeMessage(IpcToolCallItem item)
    {
        if (item.Status == IpcToolCallStatus.Unknown)
        {
            return item.Diagnostic is { Length: > 0 } d
                ? $"结果未知，请人工核实，不要重试。（{d}）"
                : "结果未知，请人工核实，不要重试。";
        }

        return item.Diagnostic;
    }

    /// <summary>
    /// 构建工具输出：短的内联，长的折叠。
    /// </summary>
    /// <remarks>
    /// 折叠用 <see cref="Expander"/>（Avalonia 原生），不自建：
    /// 它有现成的键盘可达性与展开动画。
    /// <para>
    /// 折叠时显示前 <see cref="CollapseAfterLines"/> 行做预览——
    /// 只显示"（已折叠）"会让用户必须展开才知道有没有用。
    /// </para>
    /// </remarks>
    private static Control BuildToolOutput(string text)
    {
        var lines = text.Split(NewLineChar);
        var needsCollapse = lines.Length > CollapseAfterLines;

        if (!needsCollapse)
            return OutputBlock(text);

        var preview = string.Join(NewLine, lines.Take(CollapseAfterLines));
        var hidden = lines.Length - CollapseAfterLines;

        return new Expander
        {
            Margin = new Thickness(0, 2, 0, 0),
            Header = new TextBlock
            {
                Text = $"输出（{lines.Length} 行，展开查看其余 {hidden} 行）",
                FontSize = 11,
                Opacity = 0.55,
            },
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 4,
                Children = { OutputBlock(preview), OutputBlock(string.Join(NewLine, lines.Skip(CollapseAfterLines))) },
            },
        };
    }

    private static Control OutputBlock(string text) => new Border
    {
        Padding = new Thickness(10, 6),
        CornerRadius = new CornerRadius(4),
        Background = new SolidColorBrush(Color.FromArgb(10, 0, 0, 0)),
        Child = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Geist Mono, Consolas, monospace"),
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            Opacity = 0.75,
        },
    };

    /// <summary>
    /// IPC 的 <see cref="ToolCallStatus"/> 映射到 Arc 的 <c>ToolCallStatus</c>。
    /// </summary>
    /// <remarks>
    /// <b><see cref="IpcToolCallStatus.Unknown"/> 必须保留语义</b>：Arc 没有"结果未知"这一档。
    /// 不能简单映成 <c>Error</c>——那会让用户以为可以重试，而 <c>Unknown</c>
    /// 的真实含义是"可能执行了但不知道结果，必须人工核实"。
    /// <para>
    /// 所以映成 <c>Error</c>，但在 Message 里显式写清"结果未知，请人工核实，不要重试"。
    /// </para>
    /// </remarks>
    private static ArcToolCallStatus MapStatus(IpcToolCallStatus status) => status switch
    {
        IpcToolCallStatus.Requested => ArcToolCallStatus.Running,
        IpcToolCallStatus.AwaitingApproval => ArcToolCallStatus.Approval,
        IpcToolCallStatus.Running => ArcToolCallStatus.Running,
        IpcToolCallStatus.Succeeded => ArcToolCallStatus.Success,
        IpcToolCallStatus.Failed => ArcToolCallStatus.Error,
        IpcToolCallStatus.Rejected => ArcToolCallStatus.Denied,
        IpcToolCallStatus.Cancelled => ArcToolCallStatus.Error,
        IpcToolCallStatus.Unknown => ArcToolCallStatus.Error,
        _ => ArcToolCallStatus.Error,
    };

    private static string DescribeTitle(IpcToolCallItem item) => item.Status switch
    {
        IpcToolCallStatus.Running or IpcToolCallStatus.Requested => $"正在调用 {item.ToolName}",
        IpcToolCallStatus.AwaitingApproval => $"等待审批：{item.ToolName}",
        IpcToolCallStatus.Succeeded => $"已调用 {item.ToolName}",
        IpcToolCallStatus.Failed => $"{item.ToolName} 失败",
        IpcToolCallStatus.Rejected => $"{item.ToolName} 被拒绝",
        IpcToolCallStatus.Cancelled => $"{item.ToolName} 已取消",

        // 结果未知：文案必须点明"不要重试"
        IpcToolCallStatus.Unknown => $"{item.ToolName}：结果未知，请人工核实，不要重试",

        _ => item.ToolName,
    };

    private static Control BuildPlan(PlanItem item)
    {
        var steps = new Avalonia.Collections.AvaloniaList<AgentPlanStep>();
        foreach (var step in item.Steps)
        {
            steps.Add(new AgentPlanStep
            {
                Title = step.Content,
                State = MapStepState(step.Status),
            });
        }

        return new Border
        {
            Margin = new Thickness(12, 6),
            Child = new AgentPlan { Steps = steps },
        };
    }

    private static AgentPlanStepState MapStepState(string status) => status switch
    {
        "completed" => AgentPlanStepState.Done,
        "in_progress" => AgentPlanStepState.Active,
        _ => AgentPlanStepState.Pending,
    };

    private static Control BuildNotice(NoticeItem item) => new Border
    {
        Margin = new Thickness(12, 4),
        Padding = new Thickness(12, 8),
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(item.Level switch
        {
            NoticeLevel.Error => Color.FromArgb(20, 200, 60, 60),
            NoticeLevel.Warning => Color.FromArgb(20, 200, 150, 40),
            _ => Color.FromArgb(14, 100, 100, 100),
        }),
        Child = new TextBlock
        {
            Text = item.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        },
    };

    private static Control BuildReasoning(ReasoningItem item) => new Border
    {
        Margin = new Thickness(12, 4),
        Padding = new Thickness(12, 6),
        Child = new TextBlock
        {
            Text = item.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.5,
            FontStyle = FontStyle.Italic,
        },
    };

    /// <summary>
    /// 未知 kind 的降级卡片。
    /// </summary>
    /// <remarks>
    /// <b>保留原始信息而不是显示"未知消息"</b>：至少让用户看到有东西到达了，
    /// 也便于排查"服务端发了什么客户端不认识的东西"。
    /// </remarks>
    private static Control BuildUnknown(ConversationItem item) => new Border
    {
        Margin = new Thickness(12, 4),
        Padding = new Thickness(12, 8),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(40, 150, 150, 150)),
        Child = new TextBlock
        {
            Text = $"（未识别的条目类型：{item.GetType().Name}）",
            FontSize = 12,
            Opacity = 0.5,
        },
    };
}
