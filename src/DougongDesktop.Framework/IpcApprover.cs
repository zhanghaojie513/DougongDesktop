using System.Text.Json;
using Dougong.Agent;
using DougongDesktop.Ipc;

namespace DougongDesktop.Framework;

/// <summary>
/// SDK 审批器到 IPC 的桥：把 <see cref="ToolApprovalRequest"/> 推给客户端，
/// 等它回一个决定。
/// </summary>
/// <remarks>
/// 三条纪律，缺一条就会变成"看起来能用、实际不安全"：
/// <list type="number">
/// <item>客户端断线时**默认拒绝**，不是默认允许。挂起等一个永不到来的答复，
/// 比假装批准安全。</item>
/// <item>同一个 invocation 只允许答复一次。重复答复（用户连点、重连重发）
/// 不能变成第二次执行。</item>
/// <item>超时按拒绝处理，且必须带原因——静默超时会让用户以为"它还在等"。</item>
/// </list>
/// </remarks>
public sealed class IpcApprover : IToolApprover
{
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse?>> _ask;
    private readonly HashSet<string> _answered = new(StringComparer.Ordinal);

    /// <summary>
    /// "本次会话允许"的白名单：会话 ID → (工具 ID, 参数摘要)。
    /// </summary>
    /// <remarks>
    /// 作用域是**会话**而不是进程：不同会话之间不能互借授权。
    /// 键里带参数摘要，所以"允许读 A 文件"不会变成"允许读任何文件"。
    /// </remarks>
    private readonly Dictionary<string, HashSet<(string ToolId, string Digest)>> _sessionAllowlist =
        new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly TimeSpan _timeout;

    public IpcApprover(
        Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse?>> ask,
        TimeSpan? timeout = null)
    {
        _ask = ask;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public async ValueTask<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        var sessionKey = (request.Tool.Id, request.Binding.ArgumentsDigest);

        lock (_sync)
        {
            // 用户选过"本次会话允许"：同一个工具 + 同一组参数直接放行。
            // 键里必须包含参数摘要——只按工具名放行等于把"允许读 A"变成"允许读任意文件"。
            if (request.SessionId is { } session
                && _sessionAllowlist.TryGetValue(session, out var allowed)
                && allowed.Contains((request.Tool.Id, request.Binding.ArgumentsDigest)))
            {
                return ToolApprovalDecision.Allow("本会话已批准过相同的调用。");
            }

            // 已经把这个问题答过了：第二次答复不能变成第二次授权。
            if (!_answered.Add(request.InvocationId))
            {
                return ToolApprovalDecision.Deny(
                    "该调用已经答复过，重复答复不再产生新的授权。");
            }
        }

        var approval = new ApprovalRequest(
            RequestId: request.InvocationId,
            SessionId: request.SessionId,
            TurnId: request.TurnId,
            InvocationId: request.InvocationId,
            ToolName: request.Tool.Name,
            Summary: DescribeSummary(request),
            ArgumentsJson: request.NormalizedArguments.GetRawText(),
            PermissionScope: request.Permission.Access.ToString(),
            ExpiresAt: DateTimeOffset.Now + _timeout,
            MayHaveIrreversibleEffects: request.Tool.MayHaveIrreversibleEffects);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        ApprovalResponse? response;
        try
        {
            response = await _ask(approval, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolApprovalDecision.Deny(
                $"审批等待超过 {_timeout.TotalMinutes:0.#} 分钟未答复，按拒绝处理。");
        }
        catch (Exception ex)
        {
            // 通道断了。默认拒绝——不能让"客户端崩了"变成"授权通过"。
            return ToolApprovalDecision.Deny($"审批通道不可用（{ex.GetType().Name}），按拒绝处理。");
        }

        if (response is null)
        {
            return ToolApprovalDecision.Deny("客户端未给出答复，按拒绝处理。");
        }

        return response.Decision switch
        {
            ApprovalDecision.AllowOnce => ToolApprovalDecision.Allow("用户批准本次调用。"),
            ApprovalDecision.AllowForSession => RememberForSession(request, sessionKey),
            ApprovalDecision.DenyAndStop => ToolApprovalDecision.Deny("用户拒绝并要求停止。"),
            _ => ToolApprovalDecision.Deny(response.Reason ?? "用户拒绝。"),
        };
    }

    /// <summary>
    /// 记下"本会话允许"，并返回批准。
    /// </summary>
    /// <remarks>
    /// 不记录的话，UI 上那个"本次会话允许"按钮就是假的——用户点完之后
    /// 下一次同样的调用还会再弹一遍，这比没有这个选项更伤信任。
    /// </remarks>
    private ToolApprovalDecision RememberForSession(
        ToolApprovalRequest request, (string ToolId, string Digest) key)
    {
        lock (_sync)
        {
            if (!_sessionAllowlist.TryGetValue(request.SessionId, out var allowed))
            {
                allowed = [];
                _sessionAllowlist[request.SessionId] = allowed;
            }

            allowed.Add(key);
        }

        return ToolApprovalDecision.Allow("用户批准本会话内的同类调用。");
    }

    /// <summary>会话结束后释放其白名单，避免长期占用内存。</summary>
    public void ForgetSession(string sessionId)
    {
        lock (_sync)
        {
            _sessionAllowlist.Remove(sessionId);
        }
    }

    private static string DescribeSummary(ToolApprovalRequest request)
    {
        var scope = request.Permission.IsKnown
            ? request.Permission.Access.ToString()
            : "未知权限";
        var workspace = string.IsNullOrWhiteSpace(request.Workspace) ? "未指定工作区" : request.Workspace;
        var irreversible = request.Tool.MayHaveIrreversibleEffects
            ? "；**可能不可撤销**"
            : string.Empty;

        return $"{request.Tool.Name} 请求 {scope} 权限（{workspace}）{irreversible}";
    }
}
