namespace DougongDesktop.Ipc;

// ─────────────────────────────────────────────────────────────
// 审批：一等公民，不是弹窗附属
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 审批请求。决策粒度按 Codex 的做法分成四档——只给"允许/拒绝"二元
/// 会让用户在两难之间被迫选"拒绝后重来"。
/// </summary>
/// <param name="Summary">一句话说明将要做什么。审批要让人能<b>判断</b>，不是只看工具名。</param>
/// <param name="ArgumentsJson">完整参数。UI 按键值对展开，坏 JSON 也要原样给出。</param>
/// <param name="ExpiresAt">过期时刻。界面与引擎用<b>同一个</b>超时算出来，不能各算一套。</param>
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

/// <summary>四档决策。</summary>
public enum ApprovalDecision
{
    /// <summary>只允许这一次。</summary>
    AllowOnce,

    /// <summary>
    /// 本次会话内，同样的调用不再询问。
    /// </summary>
    /// <remarks>
    /// <b>作用域键里必须含参数摘要</b>：只按工具名放行等于把"允许读 A 文件"
    /// 变成"允许读任意文件"。
    /// </remarks>
    AllowForSession,

    Deny,

    /// <summary>
    /// 拒绝并中断整个回合。
    /// </summary>
    /// <remarks>
    /// <b>它不是普通拒绝</b>：普通拒绝后模型会收到"工具被拒"然后继续下一轮；
    /// 这一档要把回合也停掉。所以宿主要真的去取消运行令牌——
    /// SDK 的 <c>ToolApprovalDecision</c> 只有 <c>Approved</c>/<c>Reason</c>，
    /// 表达不了"还要停回合"，必须由宿主补这一动作。
    /// </remarks>
    DenyAndStop,
}

public sealed record ApprovalResponse(string RequestId, ApprovalDecision Decision, string? Reason);

public sealed record ApprovalRequestedEvent : ServerEvent
{
    public required ApprovalRequest Request { get; init; }
}

public sealed record RespondApprovalCommand : ClientCommand
{
    public required ApprovalResponse Response { get; init; }
}
