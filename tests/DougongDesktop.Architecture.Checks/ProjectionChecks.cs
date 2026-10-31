using System.Text.Json;
using Dougong.Agent;
using DougongDesktop.Framework;
using DougongDesktop.Ipc;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// 事件投影的检查（纯映射层，表驱动）。
/// </summary>
/// <remarks>
/// <b>这是拆出 <see cref="EventProjection"/> 的直接收益</b>：
/// 纯函数可以逐条断言"这个事件应该映射成什么"，不需要构造完整运行时、
/// 不需要假模型、不需要 UI。拆分前这些逻辑埋在 480 行的 async 迭代器里，
/// 测它要跑一整轮对话。
/// </remarks>
internal static class ProjectionChecks
{
    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("proj-text-delta", slug => TextDelta(breakMode == slug));
        failures += Case("proj-tool-requested-new-item", slug => ToolRequested(breakMode == slug));
        failures += Case("proj-tool-id-is-deterministic", slug => ToolIdDeterministic(breakMode == slug));
        failures += Case("proj-unknown-outcome-preserved", slug => UnknownOutcomePreserved(breakMode == slug));
        failures += Case("proj-unknown-event-ignored", slug => UnknownEventIgnored(breakMode == slug));
        failures += Case("proj-outcome-mapping", slug => OutcomeMapping(breakMode == slug));

        return failures;
    }

    private static string? TextDelta(bool inject)
    {
        var result = EventProjection.Map(new AgentEvent.TextDelta("hello"));

        if (inject) return "反证注入：TextDelta 不产出增量";

        return result is EventProjection.Projection.TextDelta { Text: "hello" }
            ? null
            : $"TextDelta 应映射为 TextDelta 投影，实际 {result.GetType().Name}";
    }

    private static string? ToolRequested(bool inject)
    {
        var args = JsonDocument.Parse("{\"path\":\"a.txt\"}");
        var result = EventProjection.Map(
            new AgentEvent.ToolCallRequested("inv-1", "read_file", args.RootElement.Clone()));

        if (inject) return "反证注入：工具请求不产新条目";

        return result is EventProjection.Projection.Added { Item: ToolCallItem tool }
            && tool.Status == ToolCallStatus.Running
            && tool.ToolName == "read_file"
            ? null
            : $"工具请求应产出 Running 状态的 ToolCallItem，实际 {result.GetType().Name}";
    }

    /// <summary>
    /// 工具条目 Id 必须由 InvocationId 确定性派生。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是"同一次调用显示成两行"</b>：若请求与完成两处算出不同的 Id，
    /// 更新时就找不到原条目，会新建一条。而这个错误不会报错——
    /// 界面上只是多了一行看起来像重复的内容。
    /// </remarks>
    private static string? ToolIdDeterministic(bool inject)
    {
        var a = EventProjection.ToolItemId("inv-1");
        var b = EventProjection.ToolItemId("inv-1");
        var c = EventProjection.ToolItemId("inv-2");

        if (inject) return "反证注入：同一 InvocationId 产出不同 Id";

        if (a != b) return $"同一 InvocationId 必须产出相同 Id：{a} vs {b}";
        return a != c ? null : "不同 InvocationId 应产出不同 Id";
    }

    /// <summary>
    /// OutcomeUnknown 必须映射成 Unknown，不能映成 Failed。
    /// </summary>
    /// <remarks>
    /// <b>这是安全语义</b>：Failed 会让用户以为"可以重试"，而 Unknown 的含义是
    /// "可能已执行，必须人工核实"。混同会诱导用户重试一个可能已有副作用的调用。
    /// </remarks>
    private static string? UnknownOutcomePreserved(bool inject)
    {
        var existing = new ToolCallItem
        {
            Id = "tool-inv-1",
            InvocationId = "inv-1",
            ToolName = "write_file",
            Status = ToolCallStatus.Running,
        };

        var result = new ToolDispatchResult(
            "inv-1", "write_file", ToolCallOutcome.OutcomeUnknown,
            Diagnostic: "调用开始后进程退出");

        var updated = EventProjection.ApplyToolResult(existing, result);
        var expected = inject ? ToolCallStatus.Failed : ToolCallStatus.Unknown;

        if (updated.Status != expected)
            return $"OutcomeUnknown 应映射为 Unknown（不是 Failed），实际 {updated.Status}";

        // 九种 Outcome 都要有非默认映射
        var all = Enum.GetValues<ToolCallOutcome>()
            .Where(o => o != ToolCallOutcome.OutcomeUnknown)
            .Where(o => EventProjection.ApplyToolResult(existing, new ToolDispatchResult("i", "t", o)).Status
                        == ToolCallStatus.Requested)
            .ToArray();

        return all.Length == 0 ? null : $"以下 Outcome 没有映射：{string.Join(",", all)}";
    }

    private static string? UnknownEventIgnored(bool inject)
    {
        // 用一个 SDK 未来可能新增的事件形状：这里用现有事件的派生类模拟
        var result = EventProjection.Map(new AgentEvent.RunStarted("s", "t"));

        if (inject) return "反证注入：未知事件抛错而非忽略";

        return result is EventProjection.Projection.Ignored
            ? null
            : $"未映射的事件应返回 Ignored（SDK 加新事件时旧客户端不该崩），实际 {result.GetType().Name}";
    }

    private static string? OutcomeMapping(bool inject)
    {
        var cases = new (AgentRunOutcome In, TurnStatus Out)[]
        {
            (AgentRunOutcome.Completed, TurnStatus.Completed),
            (AgentRunOutcome.Cancelled, TurnStatus.Cancelled),
            (AgentRunOutcome.ToolRoundLimitReached, TurnStatus.Failed),
        };

        if (inject) return "反证注入：Completed 映射成 Failed";

        foreach (var (input, expected) in cases)
        {
            var actual = EventProjection.MapOutcome(input);
            if (actual != expected) return $"{input} 应映射为 {expected}，实际 {actual}";
        }

        return null;
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
