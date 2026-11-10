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
using IpcToolCallItem = DougongDesktop.Ipc.ToolCallItem;
using IpcToolCallStatus = DougongDesktop.Ipc.ToolCallStatus;
using IpcFeedbackRating = DougongDesktop.Ipc.FeedbackRating;
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

    /// <summary>滚动视口，供诊断读取视口高度。</summary>
    public ScrollViewer Scroll => _scroll;

    /// <summary>当前条目数。</summary>
    public int Count => _items.Count;

    /// <summary>条目列表的只读视图，供诊断统计"某种条目有几个"。</summary>
    /// <remarks>
    /// <b>暴露只读而不是让诊断去翻控件树</b>：控件树里只有可见的那几个
    /// （虚拟化），统计"已完成消息应有多少反馈条"必须看数据而非控件。
    /// </remarks>
    public IReadOnlyList<ConversationItem> Items => _items;

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

    /// <summary>流式进行中的状态行（"深度求索中，用时 N 秒"）。</summary>
    private readonly RunStatusLine _runStatus = new()
    {
        Margin = new Thickness(ConversationMetrics.Gutter, 4, ConversationMetrics.Gutter, 12),
        IsVisible = false,
    };

    private readonly ScrollViewer _scroll;
    private readonly Control _emptyHint;

    /// <summary>当前显示的条目（按 Id 索引，便于流式更新时就地替换）。</summary>
    private readonly List<ConversationItem> _items = [];

    /// <summary>
    /// 自动贴底开关。
    /// </summary>
    /// <remarks>
    /// 用户往上滚看历史时不该被新消息拽回底部——那是最烦人的交互之一。
    /// 只有"本来就在底部附近"时才自动跟随。
    /// <para>
    /// <b>阈值 25px，不是 80px</b>。原实现写 80 是凭手感定的；dsh 的
    /// <c>use-chat-reading.ts:7</c> 定义 <c>FOLLOW_THRESHOLD = 24</c>，
    /// 而采样调用处（同文件 :277）传的是 <c>FOLLOW_THRESHOLD + 1</c>，
    /// 实际生效 25。80 与 25 的可观察差别很大：用户上滚一行（约 20px）时，
    /// 80 仍判定"在底部"并把视图拽回去，25 则正确停止跟随。
    /// </para>
    /// </remarks>
    private bool _stickToBottom = true;

    /// <summary>当前是否处于"贴底"状态，供诊断与按钮显隐。</summary>
    public bool IsStickingToBottom => _stickToBottom;

    /// <summary>上一次 ScrollChanged 时的偏移，用来区分"用户上滚"与"内容变长"。</summary>
    private double _lastOffset;

    /// <summary>上一次 ScrollChanged 时的内容高度，用来识别 ItemsSource 重建导致的高度缩水。</summary>
    private double _lastExtent;

    /// <summary>上一轮的基准是否有效。ItemsSource 重建后为 false，跳过一轮判断。</summary>
    private bool _baselineKnown;

    /// <summary>本轮连续补滚的轮数，防止 ScrollChanged 自激。</summary>
    /// <remarks>
    /// 只在"仍没到底"时递增，到底或用户接管时归零。
    /// 上限 8 轮足够覆盖虚拟化列表的几轮测量（实测 2–3 轮就到底）。
    /// </remarks>
    private int _compensationRounds;

    /// <summary>补滚轮数上限。</summary>
    private const int MaxCompensationRounds = 8;

    private readonly Avalonia.Controls.Button _scrollToBottom;

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

        _emptyHint = BuildEmptyHero();

        _scroll = new ScrollViewer
        {
            // 列表 + 状态行放进同一个滚动内容。
            //
            // 状态行必须**在滚动区里**：它是"即将出现新内容"的指示，
            // 放在滚动区外（比如审批区那一行）的话，用户上滚看历史时
            // 它就跟着消失了——而那正是最需要知道"还在跑吗"的时刻。
            Content = new StackPanel { Children = { _list, _runStatus } },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        _scrollToBottom = new Avalonia.Controls.Button
        {
            Content = new TKCode.Arc.Icons.Lucide.LucideIcon
            {
                Kind = TKCode.Arc.Icons.Lucide.LucideIconKind.ChevronDown,
                Size = 16,
            },
            Width = ConversationMetrics.ScrollToBottomSize,
            Height = ConversationMetrics.ScrollToBottomSize,
            CornerRadius = new CornerRadius(ConversationMetrics.ScrollToBottomSize / 2),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, ConversationMetrics.ScrollToBottomInset),
            IsVisible = false,
            Background = new SolidColorBrush(DshTheme.ButtonElevatedFill),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(DshTheme.BorderL3),
        };

        // 订阅放在按钮构造**之后**：ScrollChanged 会在构造期间触发，
        // 先订阅会拿到 null 的 _scrollToBottom（CS8602 抓到的就是这个顺序问题）
        _scroll.ScrollChanged += (_, _) =>
        {
            var extent = _scroll.Extent.Height;
            var offset = _scroll.Offset.Y;
            var viewport = _scroll.Viewport.Height;

            // 布局还没完成（视口或内容为 0）时不做任何判断：
            // 此时的 distance 是个无意义的大数，会把跟随状态打成"已暂停"
            if (viewport <= 0 || extent <= 0)
            {
                _lastOffset = offset;
                _lastExtent = extent;
                _baselineKnown = true;
                return;
            }

            // 基准未知（刚重建过 ItemsSource）：只记录，不判断
            if (!_baselineKnown)
            {
                _lastOffset = offset;
                _lastExtent = extent;
                _baselineKnown = true;
                return;
            }

            var distance = extent - offset - viewport;

            // **区分"用户上滚"与"内容变长"**。
            //
            // 这是第一版写错的地方，症状是"载入后没滚到底"：
            // 内容追加时 Extent 先变大、Offset 还没跟上，`distance`
            // 一瞬间变得很大，于是被判成"用户滚走了"→ 跟随被关掉 →
            // 排队中的滚底回调又被守卫拦下 → 停在中途。
            //
            // 正确的判据不是"离底部多远"，而是**Offset 有没有变小**：
            //   - Offset 变小、且内容没缩水 → 用户往上滚了 → 暂停跟随
            //   - Offset 没变或变大、内容变长  → 系统追加内容 → 继续跟随
            //   - Extent 缩水（ItemsSource 重建）→ 两者的比较无意义，跳过
            var contentShrank = extent < _lastExtent - 0.5;
            var offsetWentUp = offset < _lastOffset - 0.5;

            // 内容又变长了 → 布局在往前推进，允许再补一次滚
            if (extent > _lastExtent + 0.5) _compensationRounds = 0;

            if (!contentShrank && offsetWentUp)
                _stickToBottom = false;
            else if (distance <= ConversationMetrics.FollowThreshold)
                _stickToBottom = true;   // 用户自己滚回来了，重新贴底

            _lastOffset = offset;
            _lastExtent = extent;

            // 不在底部才显示"回到底部"
            _scrollToBottom.IsVisible = !_stickToBottom && extent > viewport;

            // **跟随中但还没到底 → 补滚**。
            //
            // 这是第二个必须补的地方，也是"载入后卡在中途"的真正原因：
            // 虚拟化列表的 Extent 是**分几轮**长出来的——先建可见容器、
            // 再测量、再扩充。每次 Add 只排一次滚底，滚动时 Extent 还没
            // 最终定下来，于是停在半路（实测窄栏停在距底 572px）。
            //
            // 判据加上 `!contentShrank`：重建 ItemsSource 时的缩水是
            // 中间态，那时补滚会把新内容的滚动位置搞乱。
            //
            // 防自激：补滚会再次触发 ScrollChanged，若那时仍没到底会继续
            // 排队，形成"滚一点→再排一次"的活锁，UI 线程永远忙。
            // 上限 8 次；超过就放弃这一轮，等下一次真正的内容增长重置。
            //
            // **原来这里是个死字段**：`_compensating` 有声明、有读、有重置，
            // 但**没有任何一处把它置为 true**——注释写着"只允许一层"，
            // 实际一层也没限。这类"注释描述了不存在的机制"比没注释更糟，
            // 因为它让人以为已经防住了。
            if (_stickToBottom && !contentShrank && distance > 1)
            {
                if (++_compensationRounds <= MaxCompensationRounds)
                    QueueScrollToBottom();
            }
            else
            {
                _compensationRounds = 0;   // 到底了或用户接管了，计数归零
            }
        };

        ToolTip.SetTip(_scrollToBottom, "回到底部");

        _scrollToBottom.Click += (_, _) => ScrollToBottom();

        var grid = new Grid();
        grid.Children.Add(_scroll);
        grid.Children.Add(_emptyHint);
        grid.Children.Add(_scrollToBottom);

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

        // 输入区上方的渐变遮罩（dsh `ConversationRoot.module.css:507-530`）。
        //
        // **为什么需要**：消息滚到输入区边缘时会硬切一刀——最后一行文字
        // 紧贴输入框上沿被截断，视觉上像"内容被吃了"。dsh 用 36px 渐变
        // 让内容淡出，读起来是"还有更多，往下滚"，而不是"到这儿就没了"。
        //
        // **叠在消息流那一行的底部**（不是输入区那行的顶部）：
        // 放输入区行里会盖住输入框自身。它必须压在消息流末端之上。
        // 不参与命中测试，否则会挡住消息里的链接与按钮。
        var mask = new Border
        {
            Height = ConversationMetrics.ComposerMaskHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                    new GradientStop(DshTheme.PageBackground, 1),
                },
            },
        };

        Grid.SetRow(mask, 1);
        root.Children.Add(mask);

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
        AddApproval(new ApprovalCard(new ApprovalRequest(
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
    /// 自检：往 composer 加一个附件，验证附件条的渲染。
    /// </summary>
    /// <remarks>
    /// <b>为什么要这一步</b>：附件的真实入口是系统文件选择器，
    /// 自动化测试点不到它。所以这里直接往 <c>Attachments</c> 集合塞一个条目，
    /// 验证"有了附件之后界面上真的看得见"——至于选择器本身，
    /// 那是 Avalonia 的能力，不是我们的逻辑。
    /// </remarks>
    public void LoadAttachmentSelfTest()
    {
        Composer.Inner.Attachments.Add(new AiAttachment
        {
            Id = "attach-selftest",
            Name = "README.md",
            Size = 2150,   // 字节；控件自己格式化
            Kind = AiAttachmentKind.File,
            Status = AiAttachmentStatus.Ready,
        });

        Console.Error.WriteLine($"[ui] 自检附件已加入：{Composer.Inner.Attachments.Count} 个");
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

        if (_stickToBottom) QueueScrollToBottom();
    }

    /// <summary>已停止的助手消息 Id 集合。</summary>
    /// <remarks>
    /// <b>为什么用旁挂集合而不是给条目加字段</b>：<c>AssistantMessageItem</c>
    /// 是 SDK 的契约 record，只有 <c>Text</c> 与 <c>IsStreaming</c>——
    /// 没有"这轮被用户中止"的表达。不能改 SDK 类型，
    /// 也不该为此加一条假的"已停止"消息（那会写脏会话记录）。
    /// <para>
    /// 代价是不参与持久化：重开会话后标记会消失。当前阶段可以接受——
    /// 会话持久化（P9）尚未做，届时再决定"中止"要不要进日志。
    /// </para>
    /// </remarks>
    private readonly HashSet<string> _stoppedAssistantIds = new(StringComparer.Ordinal);

    /// <summary>给最后一条助手消息打上"已停止"标记。</summary>
    /// <remarks>
    /// 由宿主在收到 <c>TurnCompletedEvent(Cancelled)</c> 时调用。
    /// dsh 的表现是 assistant 尾部一个 11px 灰底小标签
    /// （<c>AssistantMarkdown.tsx:152-154</c>）。
    /// <para>
    /// 标记**最后一条 assistant**：用户点停止时界面上"正在生成"的就是它。
    /// 即使末尾是工具卡（中途停的），被截断的文字仍在最后那条 assistant 里。
    /// </para>
    /// </remarks>
    public void MarkLastAssistantStopped()
    {
        // 回合还在跑时不标：那是自相矛盾的状态（"正在生成"和"已停止"
        // 同时出现）。真实调用顺序是先 OnTurnEnded 再标记，
        // 但**不能依赖调用方守顺序**——这类保护要写在自己身上。
        if (_turnRunning) return;

        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i] is not AssistantMessageItem assistant) continue;

            if (!_stoppedAssistantIds.Add(assistant.Id)) return;   // 已标过

            Refresh();
            return;
        }
    }

    /// <summary>该助手消息是否已被标记为停止。</summary>
    public bool IsStopped(string itemId) => _stoppedAssistantIds.Contains(itemId);

    /// <summary>回合开始：显示并启动"深度求索中"状态行。</summary>
    public void OnTurnStarted()
    {
        _runStatus.IsVisible = true;
        _runStatus.Start(DateTimeOffset.Now);
        _turnRunning = true;

        // 新内容开始产生，用户期待看到它 —— 恢复跟随。
        // 这与 dsh 的行为一致：自己的提交会让阅读位置回到末尾。
        _stickToBottom = true;
        QueueScrollToBottom();
    }

    /// <summary>回合结束：隐藏状态行。</summary>
    public void OnTurnEnded()
    {
        _runStatus.Stop();
        _turnRunning = false;
    }

    /// <summary>当前是否有回合在跑。</summary>
    public bool IsTurnRunning => _turnRunning;

    /// <summary>回合是否在跑。状态行与"已停止"标记互斥用。</summary>
    private bool _turnRunning;

    /// <summary>状态行当前文本（供诊断）。</summary>
    public string RunStatusText => _runStatus.CurrentText;

    /// <summary>
    /// 排到布局之后滚到底。
    /// </summary>
    /// <remarks>
    /// <b>必须延后</b>：刚 <c>Add</c> 完时 <c>ScrollViewer.Extent</c> 还是旧值。
    /// <para>
    /// <b>要重试几轮</b>：自检/首次载入时消息是在窗口显示**之前**灌进去的，
    /// 那时连视口高度都是 0，第一次 <c>ScrollToEnd</c> 什么也不会做。
    /// 实测：只调一次时停在 offset=0（完全没滚），
    /// 窄栏下停在距底 572px。所以要连排几轮，
    /// 每轮都重新判断"是否还在跟随、是否还没到底"。
    /// </para>
    /// <para>
    /// <b>轮数有上限</b>：无限重试会在"内容永远比视口高一像素"时自激。
    /// 6 轮足够覆盖虚拟化列表的几轮测量，再多就是逻辑错了。
    /// </para>
    /// </remarks>
    private void QueueScrollToBottom()
    {
        // 只排两批：一批 Background（布局前），一批 Loaded（布局后）。
        // 用固定优先级而不是"Background + round"——后者是枚举加法，
        // 会撞上相邻优先级的语义，行为不可预期。
        QueueScrollToBottomAt(Avalonia.Threading.DispatcherPriority.Background);
        QueueScrollToBottomAt(Avalonia.Threading.DispatcherPriority.Loaded);
        QueueScrollToBottomAt(Avalonia.Threading.DispatcherPriority.Render);
    }

    private void QueueScrollToBottomAt(Avalonia.Threading.DispatcherPriority priority) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (!_stickToBottom) return;   // 排队期间用户可能滚走了
                _scroll.ScrollToEnd();
            },
            priority);

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

        // 强制滚到底：审批不处理回合就停着，出现在视口外等于让会话静默卡住。
        // 这里用**无条件**滚到底：哪怕用户正在看历史，也得让审批进入视野——
        // 它是阻塞性的，看不见就等于卡死。
        _stickToBottom = true;
        QueueScrollToBottom();
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

    /// <summary>
    /// 把工具条目挪到列表最前面。**只给自检用**。
    /// </summary>
    /// <remarks>
    /// 视口只有 353px，而消息流是虚拟化的——中间那些工具卡永远不会被构建，
    /// 于是 `ToolCall 0 张` 在"视口外"和"构建失败"两种情形下完全一样。
    /// 把工具条目前置能强制它们进入视口，一次运行就能定论。
    /// <para>
    /// <b>不能跟着滚到底</b>：前置的意义就是把工具卡放到**视口顶部**，
    /// 一滚到底就又滚走了——第一版就是这么写的，结果"前置后仍是 0 张"，
    /// 差点被当成构建失败。
    /// </para>
    /// </remarks>
    public void MoveToolsToFrontForProbe()
    {
        var tools = _items.Where(i => i is IpcToolCallItem).ToList();
        if (tools.Count == 0) return;

        _items.RemoveAll(i => i is IpcToolCallItem);
        _items.InsertRange(0, tools);
        Refresh();

        // 明确停在顶部。_stickToBottom 不在这里改——它是用户行为的状态，
        // 这个探针只在自检里跑，不该留下副作用。
        _scroll.Offset = new Avalonia.Vector(_scroll.Offset.X, 0);
    }

    private void Refresh()
    {
        // List<T> 不触发集合通知，重建 ItemsSource 强制刷新。
        // 真正的虚拟化在 ItemsPanel，重建 source 不会重建所有容器。
        _list.ItemsSource = null;
        _list.ItemsSource = _items;

        _emptyHint.IsVisible = _items.Count == 0;

        // 重建 source 会让 Offset 归零、Extent 先缩后涨，那些中间态
        // 不该被当成"用户滚走了"。把基准标成未知，下一个 ScrollChanged
        // 只做记录不做判断。
        _baselineKnown = false;
        _compensationRounds = 0;
    }

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

    private Control BuildItem(ConversationItem item) => item switch
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

    private Control BuildUserMessage(UserMessageItem item)
    {
        // dsh：用户消息**右对齐**、气泡最大宽 min(内容列宽 × 0.702, 82%)
        // （MessageItem.module.css:3-8,10-21）。
        //
        // 原来写的是 Margin(60,...) 固定左缩进——那意味着窗口变宽时
        // 气泡宽度上限不变，短消息在宽窗口里会缩在右边一小块，
        // 长消息又会一直撑到只差 60px。dsh 是**按比例**卡的。
        var bubble = new Border
        {
            Padding = new Thickness(16, 10),
            CornerRadius = new CornerRadius(Columns.RadiusXl),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(DshTheme.Bubble),
            Child = new TextBlock
            {
                Text = item.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
            },
        };

        // 复制按钮 + 时间：dsh 的顺序是「时间 · 复制」，
        // 且**只有最后一条用户消息常显**、其余 hover 才显形
        // （MessageIconActions.module.css:42-61）。这里先做常显——
        // hover 显隐要接管 PointerEntered 并逐条维护状态，
        // 但"看不见按钮"和"按钮不存在"对用户是两回事，先保证能点。
        var actions = new MessageActions(item.Text, item.At);

        // 气泡限宽：两条约束取小（见 ConversationMetrics.UserBubbleWidth）。
        // 用 LayoutTransform-ish 的做法——ContainerSize 这里拿不到（还没布局），
        // 所以挂在 AttachedToVisualTree 之后按实际可用宽度重算一次，
        // 并在尺寸变化时更新。硬写 525px 只在一个窗口宽度下正确。
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { bubble, actions },
        };

        stack.SizeChanged += (_, e) =>
        {
            var contentWidth = ConversationMetrics.ContentWidth(e.NewSize.Width);
            var limit = ConversationMetrics.UserBubbleWidth(contentWidth, e.NewSize.Width);

            if (limit > 0 && bubble.MaxWidth != limit) bubble.MaxWidth = limit;
        };

        return new Border
        {
            Margin = new Thickness(
                ConversationMetrics.Gutter,
                ConversationMetrics.GapDefault,
                ConversationMetrics.Gutter,
                ConversationMetrics.GapDefault),
            Child = stack,
        };
    }

    private Control BuildAssistantMessage(AssistantMessageItem item)
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

        // 反馈条只挂在**已完成**的消息上：流式中的正文还在变，
        // 那时评价没有意义。这也是 dsh 的做法（动作行在轮次末尾）。
        if (item.IsStreaming)
        {
            return new Border
            {
                Margin = new Thickness(
                    ConversationMetrics.Gutter, ConversationMetrics.GapAssistant,
                    ConversationMetrics.Gutter, ConversationMetrics.GapDefault),
                Padding = new Thickness(2, 4),
                Child = markdown,
            };
        }

        var feedback = new FeedbackBar(item.Id);
        feedback.Submitted += (id, rating, reason) =>
            FeedbackRequested?.Invoke(id, rating, reason);

        var stack = new StackPanel { Children = { markdown } };

        // 已停止标记：dsh 在 assistant 尾部追加一个 11px 灰底小标签
        // （AssistantMarkdown.tsx:152-154 / .module.css:60-71）。
        //
        // 它不是"错误"而是"这轮没跑完"——用户自己点的停止，
        // 用红色报错会让人以为出了问题。所以是中性灰底，不是状态色。
        if (_stoppedAssistantIds.Contains(item.Id))
        {
            stack.Children.Add(new Border
            {
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(8, 2),
                CornerRadius = new CornerRadius(Columns.RadiusSm),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(DshTheme.WithAlpha(DshTheme.LabelPrimary, 8)),
                Child = new TextBlock
                {
                    Text = "已停止",
                    FontSize = 11,
                    Opacity = 0.6,
                },
            });
        }

        stack.Children.Add(feedback);

        // 相邻 assistant 消息之间 12px（dsh ChatView.module.css:77-93）。
        // 原来统一 6px：连续几条助手消息会挤成一坨，读起来分不清边界。
        return new Border
        {
            Margin = new Thickness(
                ConversationMetrics.Gutter, ConversationMetrics.GapAssistant,
                ConversationMetrics.Gutter, ConversationMetrics.GapDefault),
            Padding = new Thickness(2, 4),
            Child = stack,
        };
    }

    /// <summary>用户对某条消息做了评价。</summary>
    /// <remarks>
    /// <b>转发给宿主而不是自己发 IPC</b>：<see cref="ConversationView"/> 不认识
    /// 引擎（这是分层的既定点）。主窗口把它接到 <c>SubmitFeedbackCommand</c>。
    /// </remarks>
    public event Action<string, IpcFeedbackRating, string?>? FeedbackRequested;

    /// <summary>工具输出超过这么多行就折叠。</summary>
    /// <remarks>
    /// <b>折叠而不是截断</b>：截断会丢信息，而排查问题往往要看完整输出。
    /// 折叠让长输出不撑爆界面，同时保持可取回。
    /// </remarks>
    private const int CollapseAfterLines = 8;

    private const char NewLineChar = '\n';
    private const string NewLine = "\n";

    /// <summary>
    /// 空状态：dsh 的 Hero。
    /// </summary>
    /// <remarks>
    /// dsh <c>EmptyHero.tsx:132-166</c> 的实际内容是：鱼形 logo（34px）+
    /// 标题 <b>探索未至之境</b>（26/32、500 字重）+ 右上角 <b>预览版</b> 徽章
    /// + 下方"选择工作区"chip。文案出处 <c>locales.ts:79-81</c>。
    /// <para>
    /// <b>原来这里只有一行灰字</b>"还没有消息。在下方输入开始对话。"——
    /// 信息量等于没有。空状态是新用户唯一会看到的界面，
    /// 它得回答"这是什么东西、我该做什么"，而不只是"没有任何消息"。
    /// </para>
    /// <para>
    /// <b>dsh 的 hero 里没有示例问题、推荐提示、快捷操作按钮</b>
    /// （代码里确实没有），所以这里也不加——加了就是偏离，不是增强。
    /// 唯一多出来的"选择工作区"我们不做：桌面端工作区在启动参数里定，
    /// 没有 dsh 那种多工作区切换。做出来会是个点了没反应的 chip。
    /// </para>
    /// </remarks>
    private static Control BuildEmptyHero()
    {
        var logo = new TKCode.Arc.Icons.Lucide.LucideIcon
        {
            Kind = TKCode.Arc.Icons.Lucide.LucideIconKind.Sparkles,
            Size = 34,
            Foreground = new SolidColorBrush(DshTheme.BrandPrimary),
        };

        var title = new TextBlock
        {
            Text = "探索未至之境",
            FontSize = 26,
            FontWeight = FontWeight.Medium,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var badge = new Border
        {
            Padding = new Thickness(6, 1),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(DshTheme.BorderL3),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "预览版",
                FontSize = 10,
                Opacity = 0.6,
            },
        };

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { title, badge },
            Spacing = 8,
        };

        var hint = new TextBlock
        {
            Text = "在下方输入开始对话。",
            FontSize = 13,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };

        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { logo, titleRow, hint },
        };
    }

    /// <summary>
    /// 滚到底部。
    /// </summary>
    /// <remarks>
    /// <b>同时恢复跟随</b>：用户点"回到底部"的意图就是"继续看新内容"，
    /// 只把位置拉过去而不恢复跟随，下一条消息进来又会滚走，
    /// 用户会以为按钮没生效。
    /// <para>
    /// <b>不做动画</b>：dsh 支持 <c>'instant' | 'smooth'</c> 两档，但 Avalonia
    /// 的 <c>ScrollViewer</c> 没有内建平滑滚动 API，自己插值要处理
    /// "动画中途用户又滚动"的打断。当前实现聚焦正确性，先做瞬时跳转——
    /// 它不是视觉差异的重点。
    /// </para>
    /// </remarks>
    private void ScrollToBottom()
    {
        _stickToBottom = true;
        _scrollToBottom.IsVisible = false;

        // 用 ScrollToEnd 而不是自己算 Offset。
        //
        // 自己算的版本会**静默失败**：Extent 还没测量完时算出的目标是旧值，
        // 赋值后看起来"滚了"，实际停在上一版底部（实测距底 572px）。
        // ScrollToEnd 是控件自己的滚动入口，它会等布局就绪再滚。
        _scroll.ScrollToEnd();
    }

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
            FontFamily = DshTheme.MonoFont,
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
            // 状态色的**低透明度底色**，不是另配一套颜色。
            //
            // 这里原来写的是手调 (200,60,60) / (200,150,40) /
            // (100,100,100)——三个既不属于 dsh 任何一档、
            // 也从没在深色主题下算过对比度的色值。
            // 正确做法是拿 token 的状态色降透明度，
            // 这样深浅主题都会跟着变。
            NoticeLevel.Error => DshTheme.WithAlpha(DshTheme.Danger, 20),
            NoticeLevel.Warning => DshTheme.WithAlpha(DshTheme.Warning, 20),
            _ => DshTheme.WithAlpha(DshTheme.LabelSecondary, 14),
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
