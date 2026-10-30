namespace DougongDesktop.Ipc;

// ─────────────────────────────────────────────────────────────
// 会话条目：Thread → Turn → Item（取自 Codex 的三层结构）
// ─────────────────────────────────────────────────────────────

// ConversationItem 的多态根声明在 Protocol.cs（线上契约总表），
// 判别器登记也在那里。这里只放派生类型——把根类型拆到两个文件
// 是为了让"总表"完整，避免以后加条目类型时漏登记判别器。

public sealed record UserMessageItem : ConversationItem
{
    public required string Text { get; init; }
}

public sealed record AssistantMessageItem : ConversationItem
{
    public string Text { get; init; } = string.Empty;
    public bool IsStreaming { get; init; }
}

/// <summary>模型的思考过程。UI 默认折叠——它不是结论，不该抢占注意力。</summary>
/// <remarks>
/// <b>与正文分开的原因</b>：推理里常带未定稿的自我纠错（"等等，这里算错了"）。
/// 并进正文会让用户以为模型已经答完，而那段话可能是它自己后来否掉的。
/// </remarks>
public sealed record ReasoningItem : ConversationItem
{
    public string Text { get; init; } = string.Empty;
    public bool IsStreaming { get; init; }
}

/// <summary>一次工具调用。输入输出都是原始 JSON 文本，由 UI 决定如何呈现。</summary>
public sealed record ToolCallItem : ConversationItem
{
    public required string InvocationId { get; init; }
    public required string ToolName { get; init; }
    public string ArgumentsJson { get; init; } = "{}";
    public string? ResultText { get; init; }
    public ToolCallStatus Status { get; init; } = ToolCallStatus.Requested;
    public string? Diagnostic { get; init; }

    /// <summary>
    /// 等待审批的耗时。null 表示这一段没有发生（没配审批器），不是 0。
    /// </summary>
    /// <remarks>
    /// <b>与执行耗时分开</b>：审批等待取决于人多快点确认，执行耗时取决于工具本身多慢，
    /// 两者的优化方向相反。合成一个数会让"这次调用很慢"无法归因。
    /// <para>
    /// <b>0 与 null 是两件事</b>：0 表示"审批瞬间通过"，null 表示"根本没有这一阶段"。
    /// 用 0 冒充会让统计里混进一批假的"审批很快"。
    /// </para>
    /// </remarks>
    public TimeSpan? ApprovalElapsed { get; init; }

    /// <summary>工具执行的耗时。null 表示没有执行（被拒绝或被取消）。</summary>
    public TimeSpan? ExecutionElapsed { get; init; }

    /// <summary>是否可能产生不可撤销的副作用。UI 据此决定风险提示的强度。</summary>
    public bool MayHaveIrreversibleEffects { get; init; }
}

public enum ToolCallStatus
{
    Requested,
    AwaitingApproval,
    Running,
    Succeeded,
    Failed,
    Rejected,
    Cancelled,

    /// <summary>结果未知：有开始记录无终态。不得自动重放，必须人工核实。</summary>
    /// <remarks>
    /// <b>不能映成 <see cref="Failed"/></b>：Failed 的含义是"可以重试"，
    /// 而 Unknown 的含义是"可能已执行，必须人工核实"。混同会诱导用户
    /// 重试一个可能已有副作用的调用。
    /// </remarks>
    Unknown,
}

/// <summary>模型声明的计划。全量替换语义，UI 不做增量合并。</summary>
/// <remarks>
/// <b>为什么不做增量合并</b>：模型重新规划时可能去掉某一步。合并会让
/// 那一步永远留在界面上——用户看到一个已经取消的步骤，以为还在做。
/// </remarks>
public sealed record PlanItem : ConversationItem
{
    public IReadOnlyList<PlanStep> Steps { get; init; } = [];
}

public sealed record PlanStep(string Content, string Status, string Priority);

/// <summary>系统级提示：压缩发生、结果被裁剪、连接异常等。必须让用户看到。</summary>
/// <remarks>
/// <b>为什么这些必须是消息流里的一条，而不是状态栏闪一下就没了</b>：
/// 上下文压缩意味着"模型看到的不再是完整历史"，那条信息在后续对话里
/// 一直有效。看不到它，用户会误判模型为什么"忘了"前面说的事。
/// </remarks>
public sealed record NoticeItem : ConversationItem
{
    public required string Text { get; init; }
    public NoticeLevel Level { get; init; } = NoticeLevel.Info;
}

public enum NoticeLevel
{
    Info,
    Warning,
    Error,
}

// ── 条目级事件 ──────────────────────────────────────────────

/// <summary>新条目。整条发送而不是只发 delta，避免客户端合并状态出错。</summary>
/// <remarks>
/// <b>整条发送的代价是带宽，换的是正确性</b>：delta 合并要求客户端与服务端
/// 对"追加顺序"完全一致，任何一次乱序都会得到一段错位文本，而那种错误
/// 在界面上看起来像"模型说错了话"。
/// </remarks>
public sealed record ItemAddedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required string TurnId { get; init; }
    public required ConversationItem Item { get; init; }
}

/// <summary>已有条目更新（流式文本追加、工具状态变化）。</summary>
public sealed record ItemUpdatedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required ConversationItem Item { get; init; }
}
