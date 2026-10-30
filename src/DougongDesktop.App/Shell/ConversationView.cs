using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DougongDesktop.Ipc;
using TKCode.Arc;
using IpcToolCallItem = DougongDesktop.Ipc.ToolCallItem;
using IpcToolCallStatus = DougongDesktop.Ipc.ToolCallStatus;
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
        Content = grid;
    }

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
        var now = DateTimeOffset.Now;
        var seq = 0;
        string NextId() => $"selftest-{++seq}";

        Add(new UserMessageItem { Id = NextId(), Text = "帮我读一下 README.md" });

        Add(new AssistantMessageItem
        {
            Id = NextId(),
            Text = "好的，我先读文件。**这是加粗**，`这是行内代码`。\n\n```csharp\nvar x = 1;\n```",
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
                Id = NextId(),
                InvocationId = $"inv-{label}",
                ToolName = "read_file",
                ArgumentsJson = "{\"path\":\"README.md\"}",
                ResultText = $"（{label} 的输出）",
                Diagnostic = status == IpcToolCallStatus.Unknown
                    ? "结果未知：可能已执行，请人工核实，不要重试。"
                    : null,
                Status = status,
            });
        }

        Add(new PlanItem
        {
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
            Id = NextId(),
            Level = NoticeLevel.Warning,
            Text = "上下文已压缩：移除 12 条消息，约 48000 → 12000 tokens。模型看到的不再是完整历史。",
        });

        Add(new ReasoningItem { Id = NextId(), Text = "（推理内容示例——SDK 目前不产出实时事件，仅在恢复历史时可能出现）" });

        Add(new AssistantMessageItem
        {
            Id = NextId(),
            Text = string.Join(Environment.NewLine,
                Enumerable.Range(1, 30).Select(i => $"第 {i} 行：用于测试长文本与滚动。")),
        });

        Console.Error.WriteLine($"[ui] 自检数据已灌入：{_items.Count} 条");
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
        var markdown = new Markdown
        {
            Text = item.Text,
            // 流式时不设 Height，让文档自然增长（Arc 的 Markdown 不自带内部滚动）
        };

        return new Border
        {
            Margin = new Thickness(12, 6, 60, 6),
            Padding = new Thickness(2, 4),
            Child = markdown,
        };
    }

    private static Control BuildToolCall(IpcToolCallItem item)
    {
        // 用 Arc 的 ToolCall 卡片；状态由 ToolCallItem.Status 决定
        var toolItem = new ArcToolCallItem
        {
            Id = item.InvocationId,
            ToolName = item.ToolName,
            Status = MapStatus(item.Status),
            Title = DescribeTitle(item),
            Message = item.Diagnostic ?? item.ResultText,
        };

        return new Border
        {
            Margin = new Thickness(12, 4),
            Child = new ToolCall { Item = toolItem },
        };
    }

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
