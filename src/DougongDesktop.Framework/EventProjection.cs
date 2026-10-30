using Dougong.Agent;
using DougongDesktop.Ipc;

namespace DougongDesktop.Framework;

/// <summary>
/// <see cref="AgentEvent"/> 到 <see cref="ConversationItem"/> 的**纯映射**。
/// </summary>
/// <remarks>
/// <b>为什么把这层单独拆出来</b>：换掉 agent 框架时，只有这一层需要重写——
/// 它认识 SDK 的事件类型。<see cref="SessionRuntime"/> 里的状态维护
/// （序号、当前条目 Id、turn 列表）与框架无关，不用动。
/// <para>
/// 拆分前两者混在 480 行里，重写估算约 400 行；拆开后映射约 150 行，
/// 其余状态逻辑保留。台账 #1 就是为了这个。
/// </para>
/// <para>
/// <b>纯函数的好处</b>：不依赖时钟、不依赖状态，可以直接用表驱动测试断言
/// "这个事件应该映射成什么条目"。P9.5 的 <c>projection</c> 套件就测这里。
/// </para>
/// </remarks>
public static class EventProjection
{
    /// <summary>
    /// 映射结果：要么产生新条目，要么更新已有条目，要么只是状态信号。
    /// </summary>
    /// <remarks>
    /// 用判别联合而不是"返回 nullable 条目"：三种意图的下游处理完全不同——
    /// 新增要 Append、更新要就地替换、信号不改条目列表（如 RunFinished）。
    /// 混成一个可空返回值会丢掉这个区分。
    /// </remarks>
    public abstract record Projection
    {
        /// <summary>产生一个新条目。</summary>
        public sealed record Added(ConversationItem Item) : Projection;

        /// <summary>更新一个已有条目（流式追加、工具状态变化）。</summary>
        public sealed record Updated(ConversationItem Item) : Projection;

        /// <summary>只改运行状态，不动条目列表。</summary>
        public sealed record Signal(AgentRunOutcome Outcome, string? Diagnostic) : Projection;

        /// <summary>
        /// 流式文本的增量——需要调用方决定是追加到现有条目还是新建。
        /// </summary>
        /// <remarks>
        /// <b>它不能自己决定</b>：那取决于"当前有没有正在流式输出的 assistant 条目"，
        /// 而这是状态。纯映射层不该知道状态。
        /// </remarks>
        public sealed record TextDelta(string Text) : Projection;

        /// <summary>工具调用的终态——需要按 InvocationId 找到对应条目。</summary>
        public sealed record ToolCompleted(ToolDispatchResult Result) : Projection;

        /// <summary>这个事件不产生任何界面变化。</summary>
        public sealed record Ignored : Projection;
    }

    /// <summary>
    /// 把 SDK 事件映射成投影意图。不认识的事件返回 <see cref="Projection.Ignored"/>。
    /// </summary>
    /// <remarks>
    /// <b>不认识的返回 Ignored 而不是抛错</b>：SDK 加新事件类型时旧客户端不该崩。
    /// 这与 UI 侧"未知条目降级为通用卡片"是同一条原则。
    /// </remarks>
    public static Projection Map(AgentEvent evt) => evt switch
    {
        AgentEvent.TextDelta delta => new Projection.TextDelta(delta.Text),

        AgentEvent.ToolCallRequested requested => new Projection.Added(new ToolCallItem
        {
            // Id 用 InvocationId 派生：同一个调用在"请求"和"完成"两处必须算出同一个 Id，
            // 否则更新时找不到原条目
            Id = ToolItemId(requested.InvocationId),
            InvocationId = requested.InvocationId,
            ToolName = requested.ToolId,
            ArgumentsJson = requested.Arguments.GetRawText(),
            Status = ToolCallStatus.Running,
        }),

        AgentEvent.ToolCallCompleted completed => new Projection.ToolCompleted(completed.Result),

        AgentEvent.PlanUpdated plan => new Projection.Added(new PlanItem
        {
            // 计划是全量替换语义，每次都是新条目
            Id = $"plan-{Guid.NewGuid():N}"[..14],
            Steps = [.. plan.Entries.Select(e => new PlanStep(e.Content, e.Status, e.Priority))],
        }),

        AgentEvent.ContextCompacted compacted => new Projection.Added(new NoticeItem
        {
            Id = NoticeId(),
            Level = NoticeLevel.Warning,
            Text = compacted.Applied
                ? $"上下文已压缩：移除 {compacted.RemovedMessages} 条消息，" +
                  $"约 {compacted.EstimatedTokensBefore} → {compacted.EstimatedTokensAfter} tokens。" +
                  "模型看到的不再是完整历史。"
                : $"上下文压缩已决定但未生效：{compacted.Diagnostic ?? "预算不足"}",
        }),

        AgentEvent.ToolResultTruncated truncated => new Projection.Added(new NoticeItem
        {
            Id = NoticeId(),
            Level = NoticeLevel.Info,
            Text = $"工具 {truncated.ToolId} 的输出过长，进入模型上下文时" +
                   $"裁剪为 {truncated.KeptChars} / {truncated.OriginalChars} 字符。" +
                   "会话记录仍保留完整原文。",
        }),

        AgentEvent.RunFinished finished => new Projection.Signal(finished.Outcome, finished.Diagnostic),

        // SDK 加新事件时旧客户端不该崩
        _ => new Projection.Ignored(),
    };

    /// <summary>
    /// 工具条目的 Id。
    /// </summary>
    /// <remarks>
    /// <b>必须确定性派生</b>：请求与完成两处都要算出同一个值，
    /// 否则更新时会新建一条而不是改原来那条——表现为同一次调用显示成两行。
    /// </remarks>
    public static string ToolItemId(string invocationId) => $"tool-{invocationId}";

    /// <summary>通知条目的 Id。</summary>
    public static string NoticeId() => $"notice-{Guid.NewGuid():N}"[..16];

    /// <summary>
    /// 工具终态到条目的更新。
    /// </summary>
    /// <remarks>
    /// <b><see cref="ToolCallOutcome.OutcomeUnknown"/> 必须保留语义</b>：
    /// 它不是"失败"，是"可能执行了但不知道结果"。
    /// 映射成 <see cref="ToolCallStatus.Unknown"/> 而不是 <c>Failed</c>，
    /// 让 UI 能显示"请人工核实"而不是"可以重试"。
    /// </remarks>
    public static ToolCallItem ApplyToolResult(ToolCallItem existing, ToolDispatchResult result) =>
        existing with
        {
            Status = result.Outcome switch
            {
                ToolCallOutcome.Succeeded => ToolCallStatus.Succeeded,
                ToolCallOutcome.Failed => ToolCallStatus.Failed,
                ToolCallOutcome.RejectedNotApproved => ToolCallStatus.Rejected,
                ToolCallOutcome.RejectedInvalidArguments => ToolCallStatus.Rejected,
                ToolCallOutcome.RejectedDuplicateInvocation => ToolCallStatus.Rejected,
                ToolCallOutcome.RejectedUnknownTool => ToolCallStatus.Rejected,
                ToolCallOutcome.CancelledBeforeExecution => ToolCallStatus.Cancelled,
                ToolCallOutcome.CancelledDuringExecution => ToolCallStatus.Cancelled,

                // 结果未知：不是失败。UI 要区别对待，不能让用户以为可以重试
                ToolCallOutcome.OutcomeUnknown => ToolCallStatus.Unknown,

                _ => ToolCallStatus.Failed,
            },
            ResultText = result.Result?.Content.GetRawText() ?? result.Diagnostic,
            Diagnostic = result.Diagnostic,
        };

    /// <summary>运行结果到 turn 状态。</summary>
    public static TurnStatus MapOutcome(AgentRunOutcome outcome) => outcome switch
    {
        AgentRunOutcome.Completed => TurnStatus.Completed,
        AgentRunOutcome.Cancelled => TurnStatus.Cancelled,
        _ => TurnStatus.Failed,
    };
}
