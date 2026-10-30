namespace DougongDesktop.Ipc;

// ─────────────────────────────────────────────────────────────
// 会话：摘要、快照，以及会话级命令
// ─────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────
// 模型目录：多 provider 场景下，UI 要能列出"有哪些模型可选"
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 一个可选模型的描述。
/// </summary>
/// <remarks>
/// <b>为什么由宿主提供而不是 UI 自己查</b>：模型能力表在 SDK 里
/// （<c>ProviderCatalog</c>），而 App 层禁止引用 SDK。经 IPC 传纯数据
/// 既守住了分层，又让 UI 不必知道 SDK 的类型。
/// <para>
/// <b>只传 UI 需要的字段</b>：上下文窗口、输出上限、是否支持思考。
/// 价格与营销文案不传——SDK 里也没有，编造会让用户看到假信息。
/// </para>
/// </remarks>
/// <param name="ProviderId">所属 provider 的稳定标识（如 <c>anthropic</c>）。</param>
/// <param name="Id">模型标识，用于发起请求时指定。</param>
/// <param name="ContextWindow">上下文窗口（token）。0 表示未知。</param>
/// <param name="MaxOutputTokens">输出容量上限（token）。0 表示未知。</param>
/// <param name="SupportsReasoning">是否支持思考模式（决定要不要显示 effort 选择器）。</param>
public sealed record ModelInfo(
    string ProviderId,
    string Id,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsReasoning);

/// <summary>模型目录（响应 <see cref="ListModelsCommand"/>）。</summary>
public sealed record ModelsListedEvent : ServerEvent
{
    public required IReadOnlyList<ModelInfo> Models { get; init; }

    /// <summary>provider 的稳定标识，按字母序。</summary>
    public required IReadOnlyList<string> Providers { get; init; }

    /// <summary>当前实际在用的模型 Id；null 表示未配置。</summary>
    public string? CurrentModelId { get; init; }
}

/// <summary>请求模型目录。</summary>
public sealed record ListModelsCommand : ClientCommand;

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
