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

    private readonly ScrollArea _scroll;
    private readonly Control _emptyHint;
    private readonly Border _heroWorkspaceHost = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 14, 0, 0),
        IsVisible = false,
    };

    /// <summary>空状态 Hero 内的工作区选择器（P8d D1，与侧栏共用同一 owner）。</summary>
    public WorkspacePickerControl? HeroWorkspacePicker => _heroWorkspaceHost.Child as WorkspacePickerControl;

    /// <summary>挂载空状态 Hero 的工作区 chip（dsh rc.3 EmptyHero 表面）。</summary>
    public void AttachWorkspacePicker(WorkspacePickerOwner owner)
    {
        _heroWorkspaceHost.Child = WorkspacePickerFactory.Create(owner, WorkspacePickerSurface.EmptyHero);
        _heroWorkspaceHost.IsVisible = true;
    }

    /// <summary>当前显示的条目（按 Id 索引，便于流式更新时就地替换）。</summary>
    private readonly List<ConversationItem> _items = [];

    /// <summary>
    /// 渲染用的根序列：过程组 + 独立条目（M15）。
    /// </summary>
    /// <remarks>
    /// <b>为什么渲染绑它而不是 <c>_items</c></b>：过程组的标题要接在组前面，
    /// 而虚拟化列表按"根"构建容器。直接绑扁平条目就没法插组标题。
    /// <para>
    /// <c>_items</c> 仍是唯一真源——这个列表每次 <see cref="Refresh"/> 从它
    /// 重算，不单独维护状态，避免两份数据不同步。
    /// </para>
    /// </remarks>
    private readonly List<ConversationRoot> _roots = [];

    /// <summary>
    /// 当前的工作详情策略（M16）。
    /// </summary>
    /// <remarks>
    /// 默认 <c>detailed</c>，与 dsh 的 <c>DEFAULT_TRANSCRIPT_VIEW_MODE</c> 一致。
    /// 用户在设置页改档位时经 <see cref="SetPresentation"/> 更新。
    /// </remarks>
    private ChatPresentationPolicy _presentation = ChatPresentation.Resolve(ChatPresentation.DefaultMode);

    /// <summary>
    /// 条目 → 所属轮次。**旁挂而不改契约**。
    /// </summary>
    /// <remarks>
    /// <b>为什么不在 <c>ConversationItem</c> 上加 TurnId</b>：
    /// 那是线格式的一部分，加字段等于改协议，而引擎侧的
    /// <c>ItemAddedEvent</c> 已经带了 TurnId——我们只是没往下传。
    /// 旁挂表让"契约不动"和"分组有依据"两件事同时成立，
    /// 与 <c>_stoppedAssistantIds</c> 是同一手法。
    /// <para>
    /// M15 的轮次分组全靠这张表：没有它就无法判断"哪几条属于同一轮"。
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, string> _itemTurn = new(StringComparer.Ordinal);

    /// <summary>
    /// 轮次 → 状态（M15 折叠判定）。
    /// </summary>
    /// <remarks>
    /// <b>必须旁挂</b>：<see cref="TurnCompletedEvent"/> 不改任何条目，
    /// 但它决定"这一轮的组该不该收起"。契约里没有"条目带轮次状态"这种东西，
    /// 也不该为它加字段（那是改协议）。
    /// <para>
    /// 没有记录的轮次一律当"未关闭"——宁可多摊开，也不要误折正在发生的组。
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, TurnStatus> _turnStatus = new(StringComparer.Ordinal);

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
            ItemsSource = _roots,
            ItemTemplate = new FuncDataTemplate<ConversationRoot>((root, _) =>
                root switch
                {
                    null => null,
                    IndependentRoot independent => BuildItem(independent.Item),
                    TurnProcessRoot turn => BuildTurnProcess(turn),
                    ProcessGroupRoot group => BuildProcessGroup(group),
                    _ => null,
                }),
            // 虚拟化：1000 条不进视觉树，只构建可见的那部分
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>( 
                () => new VirtualizingStackPanel()),
            Margin = new Thickness(0),
        };

        _emptyHint = BuildEmptyHero();

        _scroll = new ScrollArea
        {
            // 列表 + 状态行放进同一个滚动内容。
            //
            // 状态行必须**在滚动区里**：它是"即将出现新内容"的指示，
            // 放在滚动区外（比如审批区那一行）的话，用户上滚看历史时
            // 它就跟着消失了——而那正是最需要知道"还在跑吗"的时刻。
            //
            // 用 Arc 的 ScrollArea（P8a A13）：常驻细悬浮滚动条贴合 dsh 的 5px 细条；
            // FadeLength 设 0，因为底部已有 36px 专属渐变遮罩，避免双重渐隐。
            Content = new StackPanel { Children = { _list, _runStatus } },
            Orientation = ScrollAreaOrientation.Vertical,
            OverlayScrollbarMode = ScrollAreaScrollbarMode.Always,
            FadeLength = 0,
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

        // 轮次导航轨道：右侧竖排刻度（M13），浮在消息流之上。
        // 放在这个 Grid 里（不是外层根 Grid）——它只覆盖消息流区域，
        // 不该盖住顶部信息栏与输入区。
        _turnRail = BuildTurnRail();
        grid.Children.Add(_turnRail);

        // 布局完成后才知道真实宽度——轨道是否显示要等这一刻才裁决。
        // 构造期的 Bounds 恒为 0，只靠 Refresh 的话轨道永远不显示。
        SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0) return;
            RefreshTurnRail();
        };

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
                    // **0 端必须是 bg-base 全透明，不能用黑色全透明**。
                    // dsh 的注释写得很明确（`ConversationRoot.module.css:541-544`）：
                    //   "The 0px stop is bg-base at zero alpha (**not white**,
                    //    which the figma export hardcodes) so both themes fade
                    //    from their own base."
                    // 用黑色全透明在浅色主题下几乎看不出差别，但深色主题里
                    // 深灰底 → 黑的过渡会显出一条暗带。
                    new GradientStop(DshTheme.WithAlpha(DshTheme.PageBackground, 0), 0),
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

        // 自检覆盖**三轮**，不是一轮：
        // ① 轮次导航条要求 ≥2 轮才显示，单轮的数据永远验不到它；
        // ② 折叠的"跨轮次必须断组"规则也要多轮才看得见。
        // 每轮都标为已完成——折叠只在 turnClosed 时发生（dsh 语义）。
        const string Turn1 = "turn-selftest-1";
        const string Turn2 = "turn-selftest-2";
        const string Turn3 = "turn-selftest-3";
        const string SelfTestTurnId = Turn2;   // 工具那批挂第 2 轮

        Add(new UserMessageItem { Id = NextId(), At = DateTimeOffset.Now, Text = "帮我读一下 README.md" }, Turn1);

        Add(new AssistantMessageItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Text = "好的，我先读文件。**这是加粗**，`这是行内代码`。\n\n```csharp\nvar x = 1;\n```",
        }, Turn1);

        // 流式条目：验证 Stream 路径（Push + Complete）
        Add(new AssistantMessageItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Text = "## 流式渲染验证\n\n这段文字通过 **MarkdownStream.Push** 推入，"
                 + "不是重设 Text。如果标题和加粗都正常，说明 Stream 路径可用。",
            IsStreaming = true,
        }, Turn1);

        // 工具：五种状态各来一个
        foreach (var (status, label, toolName) in new[]
        {
            // 工具名故意取 dsh 表内的不同类别：这样截图里能直接验证
            // 标题合成（已读取文件 → 执行了命令 → …），而不是清一色
            // 落到 tools 的「已调用工具」。
            (IpcToolCallStatus.Succeeded, "成功", "read"),
            (IpcToolCallStatus.Failed, "失败", "bash"),
            (IpcToolCallStatus.Rejected, "被拒绝", "write"),
            (IpcToolCallStatus.Cancelled, "已取消", "web_search"),
            (IpcToolCallStatus.Unknown, "结果未知", "grep"),
        })
        {
            // 挂到自检轮次上并标为已关闭：这样 detailed 档（默认）
            // 才真的会折——不挂轮次的话它们永远当"当前轮"，永远平铺，
            // 分组标题可折这件事就没被验证到。
            Add(new IpcToolCallItem
            {
                At = DateTimeOffset.Now,
                Id = NextId(),
                InvocationId = $"inv-{label}",
                ToolName = toolName,
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
            }, SelfTestTurnId);
        }

        // 该轮次已结束：上面的工具因此在 standard/detailed 档下默认收起。
        _turnStatus[Turn1] = TurnStatus.Completed;
        _turnStatus[Turn2] = TurnStatus.Completed;
        _turnStatus[Turn3] = TurnStatus.Completed;

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
        }, SelfTestTurnId);

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
        }, Turn3);

        Add(new NoticeItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Level = NoticeLevel.Warning,
            Text = "上下文已压缩：移除 12 条消息，约 48000 → 12000 tokens。模型看到的不再是完整历史。",
        }, Turn3);

        // 推理内容：SDK 0.1.1 起有 ReasoningDelta 实时事件，
        // 但是否出现取决于 provider（只有思考模式的模型才给）
        Add(new ReasoningItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Text = "（推理示例）用户要看的是默认布局对不对。先确认三栏尺寸，"
                 + "再看消息流里每种条目是否都渲染。等等——这里要注意 JSON 输出那一档，"
                 + "它走的是折叠路径，和纯文本不一样。",
        }, Turn3);

        Add(new AssistantMessageItem
        {
            At = DateTimeOffset.Now,
           Id = NextId(),
            Text = string.Join(Environment.NewLine,
                Enumerable.Range(1, 30).Select(i => $"第 {i} 行：用于测试长文本与滚动。")),
        }, Turn3);

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

    /// <summary>
    /// 新增一个条目，并记录它属于哪个轮次。
    /// </summary>
    /// <param name="turnId">
    /// 所属轮次。<c>ItemAddedEvent</c> 一直带着它，此前在
    /// <c>MainWindow</c> 里被丢掉了——M15 的分组需要它。
    /// </param>
    public void Add(ConversationItem item, string? turnId)
    {
        if (turnId is { Length: > 0 })
        {
            _itemTurn[item.Id] = turnId;

            // 最新出现的轮次就是"当前正在读的那一轮"——轨道上它最长。
            // dsh 用滚动位置算 activeTurn；我们没有逐轮的滚动锚点，
            // 用"最后一条消息属于哪轮"是同一含义的近似，且不会指错方向。
            _currentTurnId = turnId;
        }

        Add(item);
    }

    /// <summary>某条目所属的轮次；没记录过返回 null。</summary>
    public string? TurnIdOf(ConversationItem item) =>
        _itemTurn.TryGetValue(item.Id, out var t) ? t : null;

    /// <summary>
    /// 记录一个轮次的状态（M15 折叠判定需要）。
    /// </summary>
    /// <remarks>
    /// <b>为什么要单独的旁挂表</b>：<see cref="TurnCompletedEvent"/> 不改任何条目，
    /// 但从它到达那一刻起，**已有的组**就该按"已关闭"重新判定折叠。
    /// 只靠条目内容和 <see cref="ProcessGrouping"/> 推不出这件事。
    /// <para>
    /// 有记录的轮次但状态是 <see cref="TurnStatus.Running"/>，与"没有记录"
    /// 在折叠判定上等价（都当未关闭处理），所以不必为 TurnStarted 单独写表项。
    /// </para>
    /// </remarks>
    public void SetTurnStatus(string turnId, TurnStatus status)
    {
        if (turnId is not { Length: > 0 }) return;
        _turnStatus[turnId] = status;
        Refresh();
    }

    /// <summary>按轮次分组后的结果（供诊断与 M15 使用）。</summary>
    /// <remarks>
    /// 保持插入顺序：<c>GroupBy</c> 在有序序列上是稳定的，轮次不会乱序。
    /// 没有 TurnId 的条目归入 <c>null</c> 组——自检数据就是这种情况，
    /// 不能因此把它们丢掉或强行塞进某个轮次。
    /// </remarks>
    public IReadOnlyList<(string? TurnId, IReadOnlyList<ConversationItem> Items)> GroupedByTurn() =>
        [.. _items
            .GroupBy(i => TurnIdOf(i))
            .Select(g => (g.Key, (IReadOnlyList<ConversationItem>)[.. g]))];

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

    /// <summary>
    /// 应用工作详情策略（M16）。
    /// </summary>
    /// <remarks>
    /// <b>改档位要重建列表</b>：<c>FoldCompletedTurns</c> 直接影响组是否收起，
    /// 而收起状态在构建时就定了。只改字段不重建，界面不会变。
    /// </remarks>
    public void SetPresentation(ChatPresentationPolicy policy)
    {
        _presentation = policy;
        Refresh();
    }

    /// <summary>当前策略（诊断用）。</summary>
    public ChatPresentationPolicy Presentation => _presentation;

    /// <summary>分组结果（诊断用）。</summary>
    public IReadOnlyList<ConversationRoot> Roots => _roots;

    /// <summary>
    /// 滚到会话顶部（自检用）。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要它</b>：自检内容高度 3143px，视口只有 353px，
    /// 列表默认停在底部。要看顶部的用户消息（比如验证气泡没被压成竖排）
    /// 只能滚上去，而无头/无辅助功能权限的环境发不了按键。
    /// 这个钩子只被 <c>DOUGONG_UI_TOPGUARD</c> 调用，不影响正常路径。
    /// </remarks>
    public void ScrollToTopForProbe() => _scroll.Offset = new Avalonia.Vector(_scroll.Offset.X, 0);

    /// <summary>
    /// 滚到第一条推理行（自检用）。
    /// </summary>
    /// <remarks>
    /// <b>为什么要它</b>：自检内容里推理条目排在中后段，视口只有 ~350px，
    /// 不滚就永远看不见——而"看不见"和"没渲染"必须分清。
    /// 只用 <c>BringIntoView</c> 找控件：无头下虚拟化不实体化容器，
    /// 但真实窗口里能拿到。
    /// </remarks>
    public void ScrollToReasoningForProbe()
    {
        // 虚拟化下，视口外的条目根本没实体化——直接找控件必然找不到。
        // 所以先滚到尾部（推理条目在自检数据后段），等实体化后再定位。
        _stickToBottom = false;
        _scroll.Offset = new Avalonia.Vector(_scroll.Offset.X, _scroll.Extent.Height);
    }

    /// <summary>第二轮：条目已实体化后，把推理行滚进视野。</summary>
    public void BringReasoningIntoViewForProbe()
    {
        var row = _list.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.GetVisualDescendants()
                .OfType<TKCode.Arc.Icons.Lucide.LucideIcon>()
                .Any(i => i.Kind == TKCode.Arc.Icons.Lucide.LucideIconKind.Brain));

        if (row is null) return;

        row.BringIntoView();
    }

    /// <summary>
    /// 把所有整轮块与过程组都展开（自检用）。
    /// </summary>
    /// <remarks>
    /// <b>为什么要它</b>：<c>standard</c>（桌面默认档）下已完成的轮次默认收起，
    /// 自检内容又全是已完成轮次——不展开的话截图里永远看不到组标题，
    /// 于是"标题没渲染"和"标题被折起来了"分不清。
    /// 展开后走的是与用户点击完全相同的记录路径（<c>_expandedTurns</c> /
    /// <c>_expandedGroups</c>），不是特例渲染。
    /// </remarks>
    public void ExpandEverythingForProbe()
    {
        foreach (var root in _roots)
        {
            switch (root)
            {
                case TurnProcessRoot turn:
                    _collapsedTurns.Remove(TurnKey(turn));
                    _expandedTurns.Add(TurnKey(turn));
                    foreach (var g in turn.Groups)
                    {
                        _collapsedGroups.Remove(GroupKey(g));
                        _expandedGroups.Add(GroupKey(g));
                    }
                    break;

                case ProcessGroupRoot g:
                    _collapsedGroups.Remove(GroupKey(g));
                    _expandedGroups.Add(GroupKey(g));
                    break;
            }
        }

        Refresh();
    }

    /// <summary>
    /// 从扁平条目重算根序列（M15 分组）。
    /// </summary>
    /// <remarks>
    /// <b>每次都全量重算</b>：与 <see cref="Refresh"/> 的既有做法一致——
    /// 条目列表是 <see cref="List{T}"/>，本来就不发集合通知，每次重建
    /// ItemsSource 是既定模式。真到性能成问题时再改成增量，
    /// 现在先保证"两份数据不会不同步"。
    /// </remarks>
    private void RebuildRoots()
    {
        _roots.Clear();
        var grouped = ProcessGrouping.Group(_items, i => TurnIdOf(i));

        // 整轮折叠（dsh 的 whole-Turn folding）：
        // 把同一轮次里**连续的过程组**收拢到一条轮次控制条下面。
        // 回复、输入、分隔符是独立根，不参与外层折叠（README: "every Assistant
        // reply remains outside whole-Turn folding"）。
        _roots.AddRange(ApplyWholeTurnFold(grouped));
    }

    /// <summary>
    /// 把过程组按轮次收拢成"整轮块"，其余根原样保留。
    /// </summary>
    /// <remarks>
    /// <b>只收拢相邻的同轮过程组</b>：中间夹了回复就不是同一段过程了
    /// （README 的完整例子：G1 → 中间回复 → G2，回复把过程分成两段，
    /// 但整轮折叠仍然同时管这两段——那属于"跨段汇总"，本轮不做）。
    /// <para>
    /// 本实现按"连续同轮过程组"聚块，因为这是最常见也最有价值的情形
    /// （推理 + 一串工具）。回复打断后各自成块，块多的时候只是多了几条控制条，
    /// 不会藏错内容——错的方向是"多显示"，不是"藏起来"。
    /// </para>
    /// </remarks>
    private IReadOnlyList<ConversationRoot> ApplyWholeTurnFold(IReadOnlyList<ConversationRoot> roots)
    {
        var result = new List<ConversationRoot>(roots.Count);
        var run = new List<ProcessGroupRoot>();

        void FlushRun()
        {
            if (run.Count == 0) return;

            var turnId = run[0].TurnId;
            var turnStatus = turnId is { } t && _turnStatus.TryGetValue(t, out var s) ? s : (TurnStatus?)null;

            // 这一块过程之后是否还有可见输入（决定整轮可不可折）
            var hasInputAfter = HasInputAfterProcess(turnId);

            result.Add(new TurnProcessRoot(turnId, [.. run], turnStatus, hasInputAfter));
            run.Clear();
        }

        foreach (var root in roots)
        {
            if (root is ProcessGroupRoot g)
            {
                // 轮次变了要断块（不同轮的过程不能共用一个控制条）
                if (run.Count > 0 && run[0].TurnId != g.TurnId) FlushRun();
                run.Add(g);
            }
            else
            {
                FlushRun();
                result.Add(root);
            }
        }

        FlushRun();
        return result;
    }

    /// <summary>
    /// 某轮次的过程内容之后是否还有用户输入。
    /// </summary>
    /// <remarks>
    /// README："Visible input after the first process evidence → Whole-Turn
    /// collapse is unavailable"。过程被输入打断时折起来会藏掉"用户中途说了什么"，
    /// 所以整轮不折，只留组级披露。
    /// </remarks>
    private bool HasInputAfterProcess(string? turnId)
    {
        var sawProcess = false;
        foreach (var item in _items)
        {
            if (TurnIdOf(item) != turnId) continue;

            if (item is ReasoningItem or IpcToolCallItem)
                sawProcess = true;
            else if (sawProcess && item is UserMessageItem)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 渲染整轮过程块：轮次控制条 + 块内各组（外层折叠）。
    /// </summary>
    /// <remarks>
    /// <b>控制条只在可折时出现</b>：不可折的轮次（运行中 / 失败 / 中途有输入）
    /// 不该出现一个点了没反应的东西——README 说这种情况"Whole-Turn collapse
    /// is unavailable"，过程保持展开。
    /// <para>
    /// 控制条文案用"已完成"这类状态词（<c>message.turnProcess.worked</c>）。
    /// 时长后续接上 <see cref="TurnCompletedEvent"/> 时再补。
    /// </para>
    /// </remarks>
    private Control BuildTurnProcess(TurnProcessRoot turn)
    {
        var policy = _presentation;
        var collapsible = ProcessFold.WholeTurnCollapsible(policy, turn.TurnStatus, turn.HasInputAfterProcess);
        var hiddenByDefault = ProcessFold.WholeTurnHiddenByDefault(
            policy, turn.TurnStatus, turn.HasInputAfterProcess);

        var key = TurnKey(turn);
        var userExpanded = _expandedTurns.Contains(key);
        var userCollapsed = _collapsedTurns.Contains(key);

        var bodyOpen = userCollapsed ? false : userExpanded || !hiddenByDefault;

        var body = new StackPanel();
        foreach (var group in turn.Groups)
            body.Children.Add(BuildProcessGroup(group));

        body.IsVisible = bodyOpen;

        var control = new Avalonia.Controls.Button
        {
            Content = new TextBlock
            {
                Text = TurnControlText(turn),
                Opacity = 0.65,
                FontSize = 12,
            },
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsVisible = collapsible,
            IsEnabled = collapsible,
            Cursor = collapsible
                ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                : Avalonia.Input.Cursor.Default,
        };

        control.Click += (_, _) =>
        {
            var nowOpen = !body.IsVisible;
            _expandedTurns.Remove(key);
            _collapsedTurns.Remove(key);
            (nowOpen ? _expandedTurns : _collapsedTurns).Add(key);
            body.IsVisible = nowOpen;
        };

        return new StackPanel { Children = { control, body } };
    }

    /// <summary>整轮控制条的文案。</summary>
    /// <remarks>
    /// dsh 的 <c>message.turnProcess.*</c>：完成写 "Completed"（中文「已完成」），
    /// 失败写 "Failed"（「已失败」）。运行中不显示控制条，所以这里没有运行态文案。
    /// </remarks>
    private static string TurnControlText(TurnProcessRoot turn) => turn.TurnStatus switch
    {
        TurnStatus.Failed => "已失败",
        TurnStatus.Cancelled => "已停止",
        _ => "已完成",
    };

    /// <summary>整轮块的稳定标识。</summary>
    private static string TurnKey(TurnProcessRoot turn) =>
        turn.TurnId is { Length: > 0 } id
            ? id
            : string.Join("|", turn.Groups.SelectMany(g => g.Items).Select(i => i.Id));

    /// <summary>
    /// 渲染一个过程组：标题行 + 组内条目。
    /// </summary>
    /// <remarks>
    /// <b>判定全在 <see cref="ProcessFold"/> 里</b>，这里只执行。三件事分开：
    /// 标题显不显示、标题能不能点、组体默认开不开——它们各自取决于不同的
    /// 模式开关，合并成一个布尔值就会互相牵连（第一版就是这么错的）。
    /// <para>
    /// 组体的默认开合还要叠用户手动展开过的事实（<c>_expandedGroups</c>），
    /// 那是用户意图，优先级高于默认值。
    /// </para>
    /// </remarks>
    private Control BuildProcessGroup(ProcessGroupRoot group)
    {
        var policy = _presentation;
        var running = group.IsRunning;

        // 轮次是否已关闭。没有轮次信息的条目按"当前轮"处理——
        // 保守取"未关闭"，宁可多摊开也不要误折正在发生的东西。
        var turnState = group.TurnId is { } tid && _turnStatus.TryGetValue(tid, out var st)
            ? st
            : (TurnStatus?)null;
        var turnClosed = turnState is not null and not TurnStatus.Running;

        var showHeader = ProcessFold.ShowGroupHeader(policy, turnClosed);
        var openByDefault = ProcessFold.GroupBodyOpenByDefault(policy, turnClosed);
        var userExpanded = _expandedGroups.Contains(GroupKey(group));

        // 用户手动开合过就以用户的为准；没动过才用默认
        var bodyOpen = _collapsedGroups.Contains(GroupKey(group))
            ? false
            : userExpanded || openByDefault;

        var body = new StackPanel();
        foreach (var item in group.Items)
            body.Children.Add(BuildItem(item));

        body.IsVisible = bodyOpen;

        var header = new Avalonia.Controls.Button
        {
            Content = new TextBlock
            {
                // 中文标签：本项目界面是中文（dsh 的 locale.ts 两套都有，用中文那套）
                Text = group.SummaryText(running),
                Opacity = 0.65,
                FontSize = 12,
            },
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsVisible = showHeader,
            IsEnabled = showHeader,
            Cursor = showHeader
                ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                : Avalonia.Input.Cursor.Default,
        };

        header.Click += (_, _) =>
        {
            // 记录的是"用户把这一组拨到哪边"，不是"翻转当前可见性"。
            // 只翻 IsVisible 会在下次 Refresh（新消息到达）时被默认值覆盖回去——
            // 也就是用户刚展开的组自己又合上了。
            var key = GroupKey(group);
            var nowOpen = !body.IsVisible;

            _expandedGroups.Remove(key);
            _collapsedGroups.Remove(key);
            (nowOpen ? _expandedGroups : _collapsedGroups).Add(key);

            body.IsVisible = nowOpen;
        };

        return new StackPanel { Children = { header, body } };
    }

    /// <summary>组的稳定标识：由成员 Id 拼成，重算后同一组得到同一个键。</summary>
    /// <remarks>
    /// 不能用对象引用：每次 <see cref="Refresh"/> 都会新建 <see cref="ProcessGroupRoot"/>，
    /// 引用一变，用户展开过的状态就全丢了。
    /// </remarks>
    private static string GroupKey(ProcessGroupRoot group) =>
        string.Join("|", group.Items.Select(i => i.Id));

    /// <summary>用户手动展开过的组。</summary>
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);

    /// <summary>
    /// 用户手动收起过的组。
    /// </summary>
    /// <remarks>
    /// <b>要单独记一侧，不能只记"展开"</b>：<c>detailed</c> 档下运行中的组默认展开，
    /// 用户把它收起来之后，下一条消息到达触发的 <see cref="Refresh"/> 会重新构建控件——
    /// 只记"展开"的话没法表达"用户明确关掉了这个默认展开的组"，
    /// 它会自己弹开。
    /// </remarks>
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);

    /// <summary>用户手动展开过的整轮块（外层）。</summary>
    private readonly HashSet<string> _expandedTurns = new(StringComparer.Ordinal);

    /// <summary>用户手动收起过的整轮块（外层）。</summary>
    private readonly HashSet<string> _collapsedTurns = new(StringComparer.Ordinal);

    /// <summary>
    /// 用户手动展开过的推理行（第三层）。
    /// </summary>
    /// <remarks>
    /// 按条目 Id 记，不按控件引用：<see cref="Refresh"/> 每次都重建控件，
    /// 引用一变状态就丢——流式过程中每来一段推理都会 Refresh 一次，
    /// 拿引用记的话用户刚展开的行会一直自己合上。
    /// </remarks>
    private readonly HashSet<string> _expandedReasoning = new(StringComparer.Ordinal);

    /// <summary>
    /// 轮次导航轨道（M13）。为空表示还没建。
    /// </summary>
    /// <remarks>
    /// 叠在消息流右侧（不是单独一列）：它是浮在内容上的指示器，
    /// 占列宽会把中栏再压窄 28px。
    /// </remarks>
    private Canvas? _turnRail;

    /// <summary>轨道上的刻度线，按轮次顺序。</summary>
    private readonly List<(string TurnId, Border Mark)> _turnMarks = [];

    private void Refresh()
    {
        // List<T> 不触发集合通知，重建 ItemsSource 强制刷新。
        // 真正的虚拟化在 ItemsPanel，重建 source 不会重建所有容器。
        RebuildRoots();
        _list.ItemsSource = null;
        _list.ItemsSource = _roots;

        _emptyHint.IsVisible = _items.Count == 0;

        // 轨道刻度跟随轮次变化（M13）
        RefreshTurnRail();

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

        // **必须用列表视口宽度，不能用 e.NewSize.Width**。
        // e.NewSize 是这个 bubble 自己的宽度，而 bubble 的宽度又受 MaxWidth 约束——
        // 于是"用自己算自己"形成正反馈：某一帧量到窄宽度 → MaxWidth 被设小 →
        // 下一帧更窄 → 最后塌成一字一行（实测：用户消息竖排单字）。
        // 容器宽是列表视口，与气泡宽度无关，所以没有回路。
        stack.SizeChanged += (_, _) => ApplyBubbleLimit();

        void ApplyBubbleLimit()
        {
            var containerWidth = _list.Bounds.Width;
            if (containerWidth <= 0) return;

            var limit = ConversationMetrics.UserBubbleWidth(
                ConversationMetrics.ContentWidth(containerWidth), containerWidth);

            if (limit > 0 && bubble.MaxWidth != limit) bubble.MaxWidth = limit;

        }

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

        var feedback = new FeedbackBar(item.Id, item.Text, item.At);
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
    private Control BuildEmptyHero()
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
            Children = { logo, titleRow, hint, _heroWorkspaceHost },
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

    /// <summary>
    /// 建轮次导航轨道：右侧 28px 竖排刻度（M13）。
    /// </summary>
    /// <remarks>
    /// 几何全部走 <see cref="TurnRail"/>（28 / 10 / 6 / 20×2 都来自 dsh），
    /// 这里只负责摆放与交互。
    /// <para>
    /// <b>为什么用 Canvas 而不是 StackPanel</b>：刻度要靠
    /// <see cref="TurnRail.CenterOf"/> 精确落位，而 StackPanel 会引入自己的
    /// 间距规则；dsh 那边是绝对定位的虚拟列表。
    /// </para>
    /// <para>
    /// <b>初始不显示</b>：宽度 ≤900 或轮次少于 2 时整条没有意义，
    /// 由 <see cref="RefreshTurnRail"/> 在每次刷新时按这两个条件裁决。
    /// </para>
    /// </remarks>
    private Canvas BuildTurnRail()
    {
        var rail = new Canvas
        {
            Width = TurnRail.RailWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = true,
            Background = Avalonia.Media.Brushes.Transparent,
            IsVisible = false,
        };

        // 轨道自己不滚动：刻度的总高由轮次数决定，会话再长也只是一串 2px 线。
        // dsh 让轨道内部滚动是为了"几千轮的极端情况"，我们暂不做——
        // 但保留 Canvas 的绝对定位，将来加滚动只要换容器。
        return rail;
    }

    /// <summary>
    /// 按当前轮次重建轨道刻度。
    /// </summary>
    /// <remarks>
    /// <b>每次 Refresh 全量重建</b>：与列表一致的做法。刻度数量等于轮次数，
    /// 增量维护的复杂度不值得——它的元素数比消息少一个量级。
    /// </remarks>
    private void RefreshTurnRail()
    {
        if (_turnRail is null) return;

        // 轮次顺序 = 首次出现顺序（与 ProcessGrouping 的轮次边界一致）
        var turnIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in _items)
        {
            if (TurnIdOf(item) is not { Length: > 0 } id) continue;
            if (seen.Add(id)) turnIds.Add(id);
        }

        // **宽度取列表的，不取自己的**：构造期 ConversationView 自己还没被测量
        // （Bounds.Width 恒 0），而 _list 在布局后会有真实宽度。
        // 用 0 判断会把轨道永久藏起来——实测第一版就是这样，一次都没显示过。
        var width = Bounds.Width > 0 ? Bounds.Width : _list.Bounds.Width;
        var visible = TurnRail.ShouldShow(turnIds.Count, width);
        _turnRail.IsVisible = visible;
        _turnRail.Height = TurnRail.ContentHeight(turnIds.Count);

        if (!visible) return;

        // 已存在的刻度复用，缺的补、多的删
        while (_turnMarks.Count > turnIds.Count)
        {
            _turnRail.Children.Remove(_turnMarks[^1].Mark);
            _turnMarks.RemoveAt(_turnMarks.Count - 1);
        }

        for (var i = 0; i < turnIds.Count; i++)
        {
            if (i >= _turnMarks.Count)
            {
                var mark = BuildTurnMark();
                _turnRail.Children.Add(mark);
                _turnMarks.Add((turnIds[i], mark));
            }

            // 轮次可能因为重排而换位（旧数据被清掉等），把 Tag 同步过去
            var (turnId, markControl) = _turnMarks[i];
            if (turnId != turnIds[i])
            {
                turnId = turnIds[i];
                _turnMarks[i] = (turnId, markControl);
            }
            markControl.Tag = turnId;

            var active = turnId == _currentTurnId;

            Canvas.SetTop(markControl, TurnRail.CenterOf(i) - TurnRail.MarkHeight / 2);
            Canvas.SetRight(markControl, 0);

            ApplyTurnMarkState(markControl, active ? TurnMarkState.Active : TurnMarkState.Resting,
                active);
        }

        UpdateTurnRailOffset();
    }

    /// <summary>当前正在读的轮次（轨道上显示为最长的一条）。</summary>
    private string? _currentTurnId;

    /// <summary>建一条刻度：20×2 的横线，靠宽度表达状态。</summary>
    /// <remarks>
    /// <b>点击跳到该轮</b>：这是导航条的全部意义。跳转按"该轮第一条消息
    /// 在列表里的位置"算滚动偏移——不是按轮次序号等比换算，
    /// 因为每轮的消息长度差很多，等比换算会落到错误的位置。
    /// </remarks>
    private Border BuildTurnMark()
    {
        var mark = new Border
        {
            Width = TurnRail.MarkWidth,
            Height = TurnRail.MarkHeight,
            CornerRadius = new CornerRadius(TurnRail.MarkHeight / 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = DshTheme.Brush(DshTheme.BorderL4),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        // 静止态是 scaleX(0.6)：用 RenderTransform 表达，不缩宽度——
        // 缩宽度会让 2px 的线在分数宽下被像素对齐压掉。
        mark.RenderTransformOrigin = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative);
        mark.RenderTransform = new Avalonia.Media.ScaleTransform(TurnRail.ScaleFor(TurnMarkState.Resting), 1);

        // hover：预览态（更长、更亮），dsh 的 .markPreview
        mark.PointerEntered += (_, _) =>
        {
            if (mark.Tag is string id && id == _currentTurnId) return;
            ApplyTurnMarkState(mark, TurnMarkState.Preview, false);
        };

        mark.PointerExited += (_, _) =>
        {
            if (mark.Tag is string id && id == _currentTurnId) return;
            ApplyTurnMarkState(mark, TurnMarkState.Resting, false);
        };

        mark.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (mark.Tag is string turnId) ScrollToTurn(turnId);
        };

        return mark;
    }

    /// <summary>滚到某一轮的第一条消息。</summary>
    /// <remarks>
    /// <b>按条目在列表中的位置算偏移，不按轮次序号等比换算</b>：
    /// 每轮的消息长度差很多（一个长回答可能是短问题的 50 倍），
    /// 等比换算会落到完全错误的地方。
    /// <para>
    /// 偏移取"该轮之前的条目总高度"。我们拿不到逐条高度（虚拟化），
    /// 所以用比例估计后交给 <c>BringIntoView</c> 修正——它会把目标条目
    /// 真正滚进视野，估计只是为了让滚动从附近开始，避免长距离跳动。
    /// </para>
    /// </remarks>
    private void ScrollToTurn(string turnId)
    {
        var index = _items.FindIndex(i => TurnIdOf(i) == turnId);
        if (index < 0) return;

        // 关掉自动贴底，否则下一帧又被拽回底部
        _stickToBottom = false;

        var ratio = _items.Count <= 1 ? 0 : (double)index / _items.Count;
        var extent = _scroll.Extent.Height;
        var viewport = _scroll.Viewport.Height;
        var target = Math.Max(0, ratio * extent - viewport / 3);

        _scroll.Offset = new Avalonia.Vector(_scroll.Offset.X, target);

        // 记住"用户选了哪一轮"，刻度上它是最长的
        _currentTurnId = turnId;
        RefreshTurnRail();
    }

    /// <summary>把一个刻度拨到指定状态。</summary>
    private static void ApplyTurnMarkState(Border mark, TurnMarkState state, bool active)
    {
        mark.RenderTransform = new Avalonia.Media.ScaleTransform(TurnRail.ScaleFor(state), 1);

        mark.Background = DshTheme.Brush(state switch
        {
            TurnMarkState.Active => DshTheme.LabelPrimary,
            TurnMarkState.Preview => DshTheme.LabelTertiary,
            TurnMarkState.Unloaded => DshTheme.BorderL4,
            _ => DshTheme.BorderL4,
        });

        mark.Opacity = state == TurnMarkState.Unloaded ? 0.6 : 1.0;
        _ = active;
    }

    /// <summary>轨道跟着滚动位置上下走（dsh 的刻度是相对整段会话的）。</summary>
    private void UpdateTurnRailOffset()
    {
        if (_turnRail is null || !_turnRail.IsVisible) return;

        // 轨道整体高度固定，不随滚动位移——dsh 的刻度代表"第几轮"，
        // 与当前滚动位置无关。这里只保证它垂直居中于消息流区域。
        var host = _turnRail.Parent as Control;
        if (host is null) return;

        var top = Math.Max(0, (host.Bounds.Height - _turnRail.Height) / 2);
        _turnRail.Margin = new Thickness(0, top, 4, 0);
    }

    /// <summary>当前被选中的轮次（自检用）。</summary>
    public string? CurrentTurnForProbe => _currentTurnId;

    /// <summary>滚动器（自检用，读当前偏移）。</summary>
    public ScrollViewer ScrollForProbe => _scroll;

    /// <summary>轮次导航轨道控件（自检用）。</summary>
    public Canvas? TurnRailForProbe => _turnRail;

    /// <summary>轨道上实际建出的刻度数（自检用）。</summary>
    public int TurnMarkCountForProbe => _turnMarks.Count;

    /// <summary>
    /// 建一条推理行的控件（自检用）。
    /// </summary>
    /// <remarks>
    /// <b>为什么要把构建路径暴露出来</b>：列表用虚拟化面板，
    /// 无头环境下不实体化容器（实测 Bounds 恒 0x0、容器 0 个），
    /// 所以从视觉树里查不到条目控件。
    /// 但"默认收起"这条必须验到**真实控件**——纯函数测不了"正文是否可见"。
    /// 这里返回的就是 <see cref="BuildReasoning"/> 的产物，
    /// 与列表里渲染走的同一段代码，不是另建一条路。
    /// </remarks>
    public Control BuildReasoningForProbe(ReasoningItem item) => BuildReasoning(item);

    /// <summary>
    /// 推理行：默认收起，只显示首行预览；点开后显示全文（第三层折叠）。
    /// </summary>
    /// <remarks>
    /// <b>第三层是独立的一层</b>：整轮 → 过程组 → 单条推理，可见性由外向内生效。
    /// 这里只管自己这一条展开与否，不关心外层开着没开。
    /// <para>
    /// <b>预览文案随状态变化</b>：运行中取「最新一段已完成的首行」，
    /// 完成后取「全文首行」，两者都去掉 <c>**</c> 标记；纯空白块不建行。
    /// 规则见 <see cref="ReasoningPreview"/>，检查
    /// <c>reasoning-preview-matches-dsh-rules</c> 钉住这些语义。
    /// </para>
    /// <para>
    /// <b>展开状态要持久</b>：<see cref="Refresh"/> 会重建所有控件，
    /// 只存在局部变量里的话，流式过程中每来一段推理用户的展开状态就丢一次。
    /// 所以按条目 Id 记到 <c>_expandedReasoning</c>。
    /// </para>
    /// </remarks>
    private Control BuildReasoning(ReasoningItem item)
    {
        // 纯空白推理不产生行（README: "Only reasoning blocks with
        // non-whitespace text produce individual rows"）。流式过程中会先推
        // 一个空块，为它建行会让界面闪一行空气。
        if (!ReasoningPreview.ProducesRow(item.Text))
            return new Panel { IsVisible = false };

        var preview = ReasoningPreview.PreviewFor(
            _presentation, item.Text, item.IsStreaming, _expandedReasoning.Contains(item.Id));

        // 预览为空说明该块没有可显示的首行（未完成的单行）——
        // 这时用一行占位，而不是把整段正文摊开。
        var collapsedLabel = preview ?? "思考中";

        var body = new TextBlock
        {
            Text = item.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.5,
            FontStyle = FontStyle.Italic,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var previewText = new TextBlock
        {
            Text = collapsedLabel,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            Opacity = 0.5,
            FontStyle = FontStyle.Italic,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TKCode.Arc.Icons.Lucide.LucideIcon
                {
                    Kind = TKCode.Arc.Icons.Lucide.LucideIconKind.Brain,
                    Size = 14,
                    Opacity = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                previewText,
            },
        };

        var content = new StackPanel { Children = { row, body } };
        var expanded = _expandedReasoning.Contains(item.Id);
        body.IsVisible = expanded;

        var host = new Border
        {
            Margin = new Thickness(12, 2),

            // 展开/收起都只改 Padding，不换控件——避免展开时整行重新布局
            Padding = new Thickness(12, 4),
            CornerRadius = new CornerRadius(Columns.RadiusSm),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = content,
        };

        host.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            var nowExpanded = !body.IsVisible;

            _expandedReasoning.Remove(item.Id);
            if (nowExpanded) _expandedReasoning.Add(item.Id);

            body.IsVisible = nowExpanded;
            // 展开后预览行不再重复显示首行（全文里有）
            row.IsVisible = !nowExpanded;
        };

        // 已经展开过的（Refresh 重建时）保持展开态
        if (expanded) row.IsVisible = false;

        return host;
    }

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
