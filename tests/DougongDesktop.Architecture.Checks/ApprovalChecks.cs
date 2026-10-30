using System.Text.Json;
using Dougong.Agent;
using DougongDesktop.Framework;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// P6 审批闭环的检查。
/// </summary>
/// <remarks>
/// <b>这批检查守的是"安全语义"，不是界面好看</b>：审批器搞错方向时，
/// 界面上看起来完全正常——用户点"允许"，工具跑了，没人发现
/// "其实他上一次点的是拒绝"。这类错误只能靠断言抓。
/// <para>
/// <b>全部走真实的 <see cref="IpcApprover"/></b>，不 mock。审批器的状态机
/// （已答复集合、会话白名单、超时）正是要测的东西。
/// </para>
/// </remarks>
internal static class ApprovalChecks
{
    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("approval-duplicate-answer-denied", slug => DuplicateAnswerDenied(breakMode == slug));
        failures += Case("approval-session-scope-is-narrow", slug => SessionScopeIsNarrow(breakMode == slug));
        failures += Case("approval-silence-is-denial", slug => SilenceIsDenial(breakMode == slug));
        failures += Case("approval-channel-failure-denies", slug => ChannelFailureDenies(breakMode == slug));
        failures += Case("approval-deny-and-stop-really-stops", slug => DenyAndStopStops(breakMode == slug));

        return failures;
    }

    /// <summary>
    /// 同一个调用答复两次，第二次不能变成第二次授权。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条最重要</b>：界面上连点两次"允许"是很常见的误操作
    /// （尤其按钮没及时变灰时）。若审批器不拦，一次点击会变成两次授权——
    /// 而用户以为自己只批了一次。
    /// </remarks>
    private static string? DuplicateAnswerDenied(bool inject)
    {
        var asked = 0;
        var approver = new IpcApprover((_, _) =>
        {
            asked++;
            // 第一次答"允许"，第二次也答"允许"（模拟连点）
            return Task.FromResult<DougongDesktop.Ipc.ApprovalResponse?>(
                new DougongDesktop.Ipc.ApprovalResponse("inv-1", DougongDesktop.Ipc.ApprovalDecision.AllowOnce, null));
        });

        var request = MakeRequest("inv-1", "read_file", "{\"path\":\"a.txt\"}");

        var first = approver.RequestApprovalAsync(request).AsTask().GetAwaiter().GetResult();
        var second = approver.RequestApprovalAsync(request).AsTask().GetAwaiter().GetResult();

        if (inject) return "反证注入：重复答复产生了第二次授权";

        if (!first.Approved) return $"第一次答复应被允许，实际 {first}";
        if (second.Approved)
            return "同一调用答复两次时，第二次必须被拒绝（不能产生第二次授权）";

        // 第二次根本不该再问用户——问了他也不会注意到自己又在批同一个调用
        return asked == 1
            ? null
            : $"重复答复不该再次询问用户，实际询问了 {asked} 次";
    }

    /// <summary>
    /// 「本会话允许」的键必须含参数摘要。
    /// </summary>
    /// <remarks>
    /// <b>只按工具名放行等于把"允许读 A"变成"允许读任意文件"</b>：
    /// 用户批准的是"读这个路径"，不是给这个工具发永久通行证。
    /// <para>
    /// <b>用询问计数而不是返回值得出结论</b>：白名单放行与"又问了用户且用户点允许"
    /// 都会返回 allowed，返回值区分不了。计数才能证明是否真的走了白名单。
    /// </para>
    /// </remarks>
    private static string? SessionScopeIsNarrow(bool inject)
    {
        var asks = 0;
        var approver = new IpcApprover((_, _) =>
        {
            asks++;
            return Task.FromResult<DougongDesktop.Ipc.ApprovalResponse?>(
                new DougongDesktop.Ipc.ApprovalResponse("x", DougongDesktop.Ipc.ApprovalDecision.AllowForSession, null));
        });

        var readA = MakeRequest("inv-a", "read_file", "{\"path\":\"a.txt\"}");
        var readB = MakeRequest("inv-b", "read_file", inject
            ? "{\"path\":\"a.txt\"}"    // 反证：参数相同 → 会被白名单放行 → 不重新询问
            : "{\"path\":\"b.txt\"}");

        approver.RequestApprovalAsync(readA).AsTask().GetAwaiter().GetResult();
        var afterFirst = asks;

        approver.RequestApprovalAsync(readB).AsTask().GetAwaiter().GetResult();
        var afterSecond = asks;

        if (inject) return "反证注入：不同参数也被白名单放行";

        // 不同参数必须重新询问 —— 这是作用域收窄的证据
        return afterSecond > afterFirst
            ? null
            : $"'本会话允许'只对同参数有效：换成 b.txt 后应重新询问（询问数 {afterFirst}→{afterSecond}）";
    }

    /// <summary>
    /// 客户端不答复（null）必须按拒绝处理，不能默认放行。
    /// </summary>
    /// <remarks>
    /// <b>默认放行是最坏的失败模式</b>：客户端崩溃、网络断、超时——
    /// 任何一种都意味着"没有人同意过"，把它当成同意等于让安全机制在最需要时失效。
    /// </remarks>
    private static string? SilenceIsDenial(bool inject)
    {
        var approver = new IpcApprover((_, _) =>
            Task.FromResult<DougongDesktop.Ipc.ApprovalResponse?>(null));

        var decision = approver
            .RequestApprovalAsync(MakeRequest("inv-null", "write_file", "{\"path\":\"x\"}"))
            .AsTask().GetAwaiter().GetResult();

        if (inject) return "反证注入：无答复被当成放行";

        return decision.Approved
            ? "客户端未答复时必须按拒绝处理，不能默认放行"
            : null;
    }

    /// <summary>
    /// 审批通道抛异常必须按拒绝处理。
    /// </summary>
    /// <remarks>
    /// <b>"客户端崩了"不能变成"授权通过"</b>：通道坏了意味着界面进程没了，
    /// 那时没有任何人做过同意这个动作。
    /// </remarks>
    private static string? ChannelFailureDenies(bool inject)
    {
        var approver = new IpcApprover((_, _) =>
            throw new InvalidOperationException("通道断了"));

        var decision = approver
            .RequestApprovalAsync(MakeRequest("inv-ex", "shell", "{\"command\":\"rm -rf /\"}"))
            .AsTask().GetAwaiter().GetResult();

        if (inject) return "反证注入：通道异常被当成放行";

        return decision.Approved
            ? "审批通道异常时必须按拒绝处理"
            : null;
    }

    /// <summary>
    /// "拒绝并停止"必须真的触发停止动作，不能只返回 Deny。
    /// </summary>
    /// <remarks>
    /// <b>这条抓的是一个真实缺陷</b>：复检时发现 SDK 的
    /// <see cref="ToolApprovalDecision"/> 只有 <c>Approved</c> 与 <c>Reason</c>，
    /// <b>表达不了"这个拒绝还要终止回合"</b>。原先的实现是
    /// <c>DenyAndStop => ToolApprovalDecision.Deny(...)</c>——模型收到
    /// "工具被拒"然后继续下一轮，用户点了"拒绝并停止"却发现它还在跑。
    /// <para>
    /// <b>断言的是"停止回调被调用"，不是返回值</b>：返回值在两种实现下都是
    /// Deny，区分不了。只有观测副作用才能抓到这个缺陷。
    /// </para>
    /// </remarks>
    private static string? DenyAndStopStops(bool inject)
    {
        var stopCalls = 0;
        string? stopReason = null;

        var approver = new IpcApprover(
            (_, _) => Task.FromResult<DougongDesktop.Ipc.ApprovalResponse?>(
                new DougongDesktop.Ipc.ApprovalResponse(
                    "inv-stop", DougongDesktop.Ipc.ApprovalDecision.DenyAndStop, "不要再写了")),
            timeout: null,
            stopTurn: reason =>
            {
                stopCalls++;
                stopReason = reason;
            });

        var decision = approver
            .RequestApprovalAsync(MakeRequest("inv-stop", "write_file", "{\"path\":\"x\"}"))
            .AsTask().GetAwaiter().GetResult();

        if (inject) return "反证注入：拒绝并停止没有触发停止动作";

        if (decision.Approved)
            return "拒绝并停止必须返回拒绝";

        if (stopCalls == 0)
            return "「拒绝并停止」没有触发停止动作 —— 回合会继续，用户点了停止却发现还在跑";

        return stopCalls == 1
            ? null
            : $"停止动作应恰好调用一次，实际 {stopCalls} 次";
    }

    // 待决锁（P6 判据"单一待决锁"）没有在这里断言：
    // Composer 在 DougongDesktop.App 里，而检查工程**刻意不引用 App**——
    // 那层分层由 app-does-not-reference-sdk 等守卫保护着，为一条检查破坏它不值。
    // 这条由运行时 dump 观测：`[shell] Composer ... 待决锁=True/False`。
    // 缺失的代价是它不参与回归；可接受，因为待决锁失效的表现是
    // "有审批时还能发消息"，而那种情况引擎会立刻拒绝并报错，不会静默。

    /// <summary>
    /// 造一个审批请求。
    /// </summary>
    /// <remarks>
    /// <b>参数摘要用 argumentsJson 本身</b>：审批器按
    /// <c>(ToolId, ArgumentsDigest)</c> 做会话白名单的键，所以这里的摘要
    /// 必须随参数变化——用固定的"摘要"字符串会让作用域检查永远通过，
    /// 那正是它该抓的错误。
    /// </remarks>
    private static ToolApprovalRequest MakeRequest(string invocationId, string toolId, string argumentsJson)
    {
        using var doc = JsonDocument.Parse(argumentsJson);

        return new ToolApprovalRequest(
            SessionId: "sess-1",
            TurnId: "turn-1",
            InvocationId: invocationId,
            Tool: new ToolDescriptor(
                Id: toolId,
                Name: toolId,
                Version: "1.0",
                Description: toolId,
                InputSchema: JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()),
            NormalizedArguments: doc.RootElement.Clone(),
            Permission: new ToolPermission(ToolAccess.Write, true, [], [], []),
            Execution: ToolExecutionPlan.Serial,
            Workspace: "/workspace",
            Binding: new ApprovalBinding(
                SessionId: "sess-1",
                TurnId: "turn-1",
                InvocationId: invocationId,
                ToolId: toolId,
                ToolVersion: "1.0",
                ArgumentsDigest: argumentsJson,
                Workspace: "/workspace",
                PermissionScope: "Write",
                ExpiresAt: DateTimeOffset.Now.AddMinutes(5),
                FileVersions: ""));
    }

    private static int Case(string slug, Func<string, string?> body)
    {
        string? error;
        try
        {
            error = body(slug);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (error is null)
        {
            Console.WriteLine($"PASS  {slug}");
            return 0;
        }

        Console.WriteLine($"FAIL  {slug}");
        Console.WriteLine($"      {error}");
        return 1;
    }
}
