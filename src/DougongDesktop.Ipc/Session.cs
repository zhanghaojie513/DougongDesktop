namespace DougongDesktop.Ipc;

// ─────────────────────────────────────────────────────────────
// 会话：摘要、快照，以及会话级命令
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

public sealed record SessionListedEvent : ServerEvent
{
    public required IReadOnlyList<SessionSummary> Sessions { get; init; }
}

public sealed record SessionOpenedEvent : ServerEvent
{
    public required SessionSnapshot Snapshot { get; init; }
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
/// <para>
/// <b>它复用 <c>session/open</c> 的响应路径</b>：宿主把重同步实现成"再开一次会话"，
/// 回一份带当前序号的全量快照。两条路径共用同一份快照构造代码，避免
/// 字段不一致——那种不一致只在丢事件之后才暴露。
/// </para>
/// </remarks>
public sealed record ResyncCommand : ClientCommand
{
    /// <summary>要重同步的会话。null 表示只需要一个基准序号（比如还没打开会话时）。</summary>
    public string? SessionId { get; init; }
}
