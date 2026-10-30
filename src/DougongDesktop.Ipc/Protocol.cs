using System.Text.Json;
using System.Text.Json.Serialization;

namespace DougongDesktop.Ipc;

/// <summary>
/// 协议版本。客户端与服务端各自校验，不匹配时拒绝连接而不是"尽量解析"。
/// </summary>
public static class IpcProtocol
{
    public const int Version = 1;

    /// <summary>JSON 序列化配置。枚举写字符串，便于抓包定位。</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

// ─────────────────────────────────────────────────────────────
// 会话模型：Thread → Turn → Item（取自 Codex 的三层结构）
// ─────────────────────────────────────────────────────────────

/// <summary>会话摘要，用于侧栏列表。权威状态由服务端持有，客户端只做投影。</summary>
public sealed record SessionSummary(
    string Id,
    string Title,
    string Workspace,
    DateTimeOffset UpdatedAt,
    int ItemCount,
    bool HasPendingApproval);

/// <summary>一个会话的完整快照：附着时用它重建界面，之后只应用增量。</summary>
/// <param name="Sequence">快照对应的服务端序号。增量事件的序号必须严格大于它。</param>
public sealed record SessionSnapshot(
    string Id,
    string Title,
    string Workspace,
    long Sequence,
    IReadOnlyList<TurnSnapshot> Turns,
    bool IsRunning,
    string? ModelName);

public sealed record TurnSnapshot(
    string Id,
    DateTimeOffset StartedAt,
    TurnStatus Status,
    IReadOnlyList<ConversationItem> Items);

public enum TurnStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// 消息流里的一个可渲染单元。客户端按 <see cref="Kind"/> 选择视图，
/// 不认识的 Kind 必须降级为通用卡片，而不是丢弃或报错。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserMessageItem), "userMessage")]
[JsonDerivedType(typeof(AssistantMessageItem), "assistantMessage")]
[JsonDerivedType(typeof(ReasoningItem), "reasoning")]
[JsonDerivedType(typeof(ToolCallItem), "toolCall")]
[JsonDerivedType(typeof(PlanItem), "plan")]
[JsonDerivedType(typeof(NoticeItem), "notice")]
public abstract record ConversationItem
{
    public required string Id { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

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
    Unknown,
}

/// <summary>模型声明的计划。全量替换语义，UI 不做增量合并。</summary>
public sealed record PlanItem : ConversationItem
{
    public IReadOnlyList<PlanStep> Steps { get; init; } = [];
}

public sealed record PlanStep(string Content, string Status, string Priority);

/// <summary>系统级提示：压缩发生、结果被裁剪、连接异常等。必须让用户看到。</summary>
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

// ─────────────────────────────────────────────────────────────
// 审批：一等公民，不是弹窗附属
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 审批请求。决策粒度按 Codex 的做法分成四档——只给"允许/拒绝"二元
/// 会让用户在两难之间被迫选"拒绝后重来"。
/// </summary>
public sealed record ApprovalRequest(
    string RequestId,
    string SessionId,
    string TurnId,
    string InvocationId,
    string ToolName,
    string Summary,
    string ArgumentsJson,
    string PermissionScope,
    DateTimeOffset ExpiresAt,
    bool MayHaveIrreversibleEffects);

public enum ApprovalDecision
{
    /// <summary>只允许这一次。</summary>
    AllowOnce,
    /// <summary>本次会话内，同样的调用不再询问。</summary>
    AllowForSession,
    Deny,
    /// <summary>拒绝并中断整个回合。</summary>
    DenyAndStop,
}

public sealed record ApprovalResponse(string RequestId, ApprovalDecision Decision, string? Reason);

// ─────────────────────────────────────────────────────────────
// 命令（客户端 → 服务端）
// ─────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "method")]
[JsonDerivedType(typeof(ListSessionsCommand), "session/list")]
[JsonDerivedType(typeof(CreateSessionCommand), "session/create")]
[JsonDerivedType(typeof(OpenSessionCommand), "session/open")]
[JsonDerivedType(typeof(StartTurnCommand), "turn/start")]
[JsonDerivedType(typeof(CancelTurnCommand), "turn/cancel")]
[JsonDerivedType(typeof(RespondApprovalCommand), "approval/respond")]
[JsonDerivedType(typeof(ResyncCommand), "session/resync")]
[JsonDerivedType(typeof(ListDirectoryCommand), "fs/list")]
public abstract record ClientCommand
{
    /// <summary>请求 ID。响应带同一个 ID 回来，用于配对。</summary>
    public required string RequestId { get; init; }
}

public sealed record ListSessionsCommand : ClientCommand;

public sealed record CreateSessionCommand : ClientCommand
{
    public required string Workspace { get; init; }
    public string? Title { get; init; }
}

public sealed record OpenSessionCommand : ClientCommand
{
    public required string SessionId { get; init; }
}

/// <summary>开始一个回合。历史由服务端从存储重建，客户端不传历史。</summary>
public sealed record StartTurnCommand : ClientCommand
{
    public required string SessionId { get; init; }
    public required string Input { get; init; }
}

public sealed record CancelTurnCommand : ClientCommand
{
    public required string SessionId { get; init; }
}

public sealed record RespondApprovalCommand : ClientCommand
{
    public required ApprovalResponse Response { get; init; }
}

/// <summary>
/// 列出工作区目录（右栏文件树用）。
/// </summary>
/// <remarks>
/// <b>为什么单独一条命令，不复用工具的 <c>list_directory</c></b>：那条是给
/// **模型**用的、会进模型上下文、受工具结果长度限制。右栏是给人看的浏览，
/// 两者对深度的需求不同，混用会让"模型看到的"和"用户看到的"互相牵制。
/// <para>
/// <b>只读</b>：右栏浏览不提供写操作，写文件仍然只能经过模型 + 审批。
/// </para>
/// </remarks>
public sealed record ListDirectoryCommand : ClientCommand
{
    /// <summary>相对工作区根的路径。空或 null 表示根目录。</summary>
    public string? Path { get; init; }
}

/// <summary>
/// 请求重新同步：客户端发现事件序号缺口后，用这条命令要一份当前状态。
/// </summary>
/// <remarks>
/// <b>为什么需要单独一条命令，而不是让客户端"等下一个事件"</b>：缺口意味着中间
/// 有事件丢了，丢的可能正是"回合结束"或"工具完成"。等下去会一直停在错误状态。
/// <para>
/// <b>为什么要 <see cref="SessionId"/></b>：重同步的目标是"把某个会话的界面状态
/// 拉回正确"。只回一个全局序号没有意义——用户看的是会话内容。
/// </para>
/// </remarks>
public sealed record ResyncCommand : ClientCommand
{
    /// <summary>要重同步的会话。null 表示只需要一个基准序号（比如还没打开会话时）。</summary>
    public string? SessionId { get; init; }
}

// ─────────────────────────────────────────────────────────────
// 通知（服务端 → 客户端）
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 服务端推送的增量。每条带 <see cref="Sequence"/>。
/// </summary>
/// <remarks>
/// <b>序号的语义，以及它现在还做不到什么——按现状如实写</b>：
/// <list type="bullet">
/// <item>序号由<b>单一全局计数器</b>分配（<c>IpcServer.NextStatusSequence</c>），
/// 会话事件与引擎状态事件共用它，因此同一连接内严格单调、不重复。</item>
/// <item><b>但目前没有任何客户端在做缺口检测。</b>客户端是单连接模型：
/// 断线即整条重建，重连后重新 <c>session/open</c> 拿快照，不去比对序号。
/// 所以"序号不连续要重新同步"这条规则<b>尚未实现</b>，不要据此以为它生效了。</item>
/// <item><b>将来要做多客户端或长连接恢复时必须补</b>：断线期间没有事件时，
/// 重连后第一帧的序号会远大于本地记录，看起来像漏了几千条；而断线期间
/// 真跑了事件时，又需要能判断"快照之后哪些帧是新增的"。
/// 两者都要求服务端提供"从某个序号开始回放"的能力，现在没有。</item>
/// </list>
/// <para>
/// 为什么现在把这段写清楚而不是留空：单人单连接下有漏洞也不会暴露，
/// 但一旦有人按"序号可靠"去实现增量同步，就会踩到一个<b>看起来能用、
/// 断线后才错</b>的坑。契约里没实现的条款，比没有条款更危险。
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "method")]
[JsonDerivedType(typeof(SessionListedEvent), "session/listed")]
[JsonDerivedType(typeof(SessionOpenedEvent), "session/opened")]
[JsonDerivedType(typeof(TurnStartedEvent), "turn/started")]
[JsonDerivedType(typeof(ItemAddedEvent), "item/added")]
[JsonDerivedType(typeof(ItemUpdatedEvent), "item/updated")]
[JsonDerivedType(typeof(TurnCompletedEvent), "turn/completed")]
[JsonDerivedType(typeof(ApprovalRequestedEvent), "approval/requested")]
[JsonDerivedType(typeof(EngineStatusEvent), "engine/status")]
[JsonDerivedType(typeof(DirectoryListedEvent), "fs/listed")]
public abstract record ServerEvent
{
    public required long Sequence { get; init; }
}

public sealed record SessionListedEvent : ServerEvent
{
    public required IReadOnlyList<SessionSummary> Sessions { get; init; }
}

public sealed record SessionOpenedEvent : ServerEvent
{
    public required SessionSnapshot Snapshot { get; init; }
}

public sealed record TurnStartedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required string TurnId { get; init; }
}

/// <summary>新条目。整条发送而不是只发 delta，避免客户端合并状态出错。</summary>
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

public sealed record TurnCompletedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required string TurnId { get; init; }
    public required TurnStatus Status { get; init; }
    public string? Diagnostic { get; init; }
}

public sealed record ApprovalRequestedEvent : ServerEvent
{
    public required ApprovalRequest Request { get; init; }
}

/// <summary>引擎健康状态。用于状态栏，不属于任何会话。</summary>
/// <summary>目录内容（响应 <see cref="ListDirectoryCommand"/>）。</summary>
public sealed record DirectoryListedEvent : ServerEvent
{
    /// <summary>请求的相对路径。根目录是空字符串。</summary>
    public required string Path { get; init; }

    /// <summary>条目。目录在前、按名称排序。</summary>
    public required IReadOnlyList<DirectoryEntry> Entries { get; init; }

    /// <summary>被截断时的说明；null 表示列全了。</summary>
    public string? Truncated { get; init; }

    /// <summary>列目录失败的原因；null 表示成功。</summary>
    public string? Error { get; init; }
}

/// <summary>文件树里的一个条目。</summary>
/// <param name="Name">文件名（不含路径）。</param>
/// <param name="IsDirectory">是否目录。</param>
/// <param name="Size">字节数；目录为 0。</param>
public sealed record DirectoryEntry(string Name, bool IsDirectory, long Size);

public sealed record EngineStatusEvent : ServerEvent
{
    public required EngineState State { get; init; }
    public string? ModelName { get; init; }
    public string? Detail { get; init; }
}

public enum EngineState
{
    Starting,
    Ready,
    Busy,
    Failed,
}
