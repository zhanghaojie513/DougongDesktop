namespace DougongDesktop.Ipc;

// ─────────────────────────────────────────────────────────────
// 回合：开始、取消、完成
// ─────────────────────────────────────────────────────────────

public enum TurnStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>开始一个回合。历史由服务端从存储重建，客户端不传历史。</summary>
/// <remarks>
/// <b>不传历史的原因</b>：客户端是投影，不是权威。让它传历史意味着
/// 两处都能改历史，而那两处迟早不一致。服务端从存储重建是唯一真源。
/// </remarks>
public sealed record StartTurnCommand : ClientCommand
{
    public required string SessionId { get; init; }
    public required string Input { get; init; }
}

public sealed record CancelTurnCommand : ClientCommand
{
    public required string SessionId { get; init; }
}

public sealed record TurnStartedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required string TurnId { get; init; }
}

public sealed record TurnCompletedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required string TurnId { get; init; }
    public required TurnStatus Status { get; init; }
    public string? Diagnostic { get; init; }
}
