using Dougong.Agent;
using DougongDesktop.Framework;
using DougongDesktop.Ipc;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// P5 的检查：工具两段耗时、推理流隔离、计划全量替换。
/// </summary>
/// <remarks>
/// <b>为什么这些要检查而不是靠看</b>：§6.6 的耗时映射有四行规则，
/// 其中"都是 null 时不要显示 0"这条错了界面上看不出来——一个假的"瞬间完成"
/// 比不显示更糟。这类缺陷只能靠断言抓。
/// <para>
/// 推理流的隔离也是同理：它若并进正文，界面上表现为"模型答完了"，
/// 而那正是把推理当结论的误导。
/// </para>
/// </remarks>
internal static class ToolChecks
{
    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("tool-durations-passed-through", slug => DurationsPassedThrough(breakMode == slug));
        failures += Case("tool-null-duration-stays-null", slug => NullDurationStaysNull(breakMode == slug));
        failures += Case("reasoning-separate-from-text", slug => ReasoningSeparate(breakMode == slug));
        failures += Case("plan-replaces-wholesale", slug => PlanReplacesWholesale(breakMode == slug));
        failures += Case("plan-tool-comes-from-sdk", slug => PlanToolFromSdk(breakMode == slug));

        return failures;
    }

    /// <summary>
    /// 两段耗时必须原样透传到 IPC 条目，不能合并成一个数。
    /// </summary>
    /// <remarks>
    /// <b>合并会丢掉"慢在哪一段"</b>：审批等待取决于人多快点确认，
    /// 执行耗时取决于工具本身多慢，优化方向相反。
    /// </remarks>
    private static string? DurationsPassedThrough(bool inject)
    {
        var existing = new ToolCallItem
        {
            Id = "tool-inv-1",
            InvocationId = "inv-1",
            ToolName = "read_file",
            Status = ToolCallStatus.Running,
        };

        var result = new ToolDispatchResult(
            "inv-1", "read_file", ToolCallOutcome.Succeeded,
            Result: null,
            Diagnostic: null,
            Permission: null,
            ApprovalElapsed: TimeSpan.FromMilliseconds(1200),
            ExecutionElapsed: TimeSpan.FromMilliseconds(45));

        var updated = EventProjection.ApplyToolResult(existing, result);

        var expectedApproval = inject ? (TimeSpan?)null : TimeSpan.FromMilliseconds(1200);
        var expectedExecution = inject ? (TimeSpan?)null : TimeSpan.FromMilliseconds(45);

        if (updated.ApprovalElapsed != expectedApproval)
            return $"ApprovalElapsed 应透传 {expectedApproval}，实际 {updated.ApprovalElapsed}";

        return updated.ExecutionElapsed == expectedExecution
            ? null
            : $"ExecutionElapsed 应透传 {expectedExecution}，实际 {updated.ExecutionElapsed}";
    }

    /// <summary>
    /// 没有发生的耗时必须是 null，不能变成 0。
    /// </summary>
    /// <remarks>
    /// <b>0 与 null 是两件事</b>：0 表示"审批瞬间通过"，null 表示"根本没有这一阶段"。
    /// 用 0 冒充会让统计里混进一批假的"审批很快"，而且界面上会显示一个
    /// 假的"瞬间完成"。
    /// </remarks>
    private static string? NullDurationStaysNull(bool inject)
    {
        var existing = new ToolCallItem
        {
            Id = "tool-inv-2",
            InvocationId = "inv-2",
            ToolName = "write_file",
            Status = ToolCallStatus.Running,
        };

        // 被拒绝：只有审批等待，没有执行
        var rejected = new ToolDispatchResult(
            "inv-2", "write_file", ToolCallOutcome.RejectedNotApproved,
            Result: null, Diagnostic: "用户拒绝", Permission: null,
            ApprovalElapsed: TimeSpan.FromMilliseconds(3400),
            ExecutionElapsed: null);

        var updated = EventProjection.ApplyToolResult(existing, rejected);

        if (inject)
        {
            // 反证：把 null 变成 0
            updated = updated with { ExecutionElapsed = TimeSpan.Zero };
        }

        if (updated.ExecutionElapsed is not null)
            return $"被拒绝的调用不该有执行耗时（显示 {updated.ExecutionElapsed} 会造出假的\"瞬间完成\"）";

        return updated.ApprovalElapsed == TimeSpan.FromMilliseconds(3400)
            ? null
            : $"审批耗时应为 3400ms，实际 {updated.ApprovalElapsed}";
    }

    /// <summary>
    /// 推理增量必须映射成独立投影，不能并进文本增量。
    /// </summary>
    /// <remarks>
    /// <b>并进去的后果是误导</b>：界面会把推理显示成正文，用户以为模型已经答完；
    /// 而推理里常带未定稿的自我纠错。
    /// </remarks>
    private static string? ReasoningSeparate(bool inject)
    {
        var reasoning = EventProjection.Map(new AgentEvent.ReasoningDelta("等等，这里算错了"));
        var text = EventProjection.Map(new AgentEvent.TextDelta("答案是 42"));

        if (inject)
        {
            // 反证：把推理当成正文处理
            reasoning = new EventProjection.Projection.TextDelta("等等，这里算错了");
        }

        if (reasoning is not EventProjection.Projection.ReasoningDelta { Text: "等等，这里算错了" })
            return $"ReasoningDelta 应映射为独立投影，实际 {reasoning.GetType().Name}";

        return text is EventProjection.Projection.TextDelta
            ? null
            : "TextDelta 应仍然映射为文本增量";
    }

    /// <summary>
    /// 每次计划更新都是全量替换（新条目），不是就地合并。
    /// </summary>
    /// <remarks>
    /// <b>就地合并会让"删掉的步骤"残留</b>：模型重新规划时可能去掉某一步，
    /// 合并会让它永远留在界面上。
    /// </remarks>
    private static string? PlanReplacesWholesale(bool inject)
    {
        var first = EventProjection.Map(new AgentEvent.PlanUpdated(
            [new PlanEntry("读文件", "high", "completed")]));

        var second = EventProjection.Map(new AgentEvent.PlanUpdated(
        [
            new PlanEntry("读文件", "high", "completed"),
            new PlanEntry("写文件", "high", "in_progress"),
        ]));

        if (first is not EventProjection.Projection.Added { Item: PlanItem a })
            return "计划更新应产出新的 PlanItem";

        if (second is not EventProjection.Projection.Added { Item: PlanItem b })
            return "第二次计划更新也应产出新的 PlanItem";

        if (inject) return "反证注入：第二次计划不产新条目";

        // 全量替换：两条不同的条目（新 Id），内容各自完整
        if (a.Id == b.Id) return "每次计划更新都该是新条目（新 Id），不是就地合并";
        if (b.Steps.Count != 2) return $"第二次计划应有 2 步，实际 {b.Steps.Count}";

        return a.Steps.Count == 1 ? null : $"第一次计划应有 1 步，实际 {a.Steps.Count}";
    }

    /// <summary>
    /// 计划工具必须是 SDK 的 <see cref="TodoWriteTool"/>，不能手写一份。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是"重复实现看起来更好"</b>：手写一份 schema 当时更简单，
    /// 但 SDK 改 schema 时我们这份不会跟着变——模型按新 schema 传参被拒，
    /// 而错误信息看起来像模型的问题。
    /// <para>
    /// 更严重的是：手写版只是"一个名字叫 todo_write 的普通工具"，
    /// 运行时不会把它识别成计划来源，<c>PlanUpdated</c> 永远不会发出，
    /// 界面上一条计划都看不到。
    /// </para>
    /// </remarks>
    private static string? PlanToolFromSdk(bool inject)
    {
        var tool = PlanTools.Create();

        if (inject) return "反证注入：计划工具不是 SDK 实现";

        if (tool is not TodoWriteTool)
            return $"计划工具应为 SDK 的 TodoWriteTool，实际 {tool.GetType().Name}";

        return tool.Descriptor.Id == "todo_write"
            ? null
            : $"工具 Id 应为 todo_write，实际 {tool.Descriptor.Id}";
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
