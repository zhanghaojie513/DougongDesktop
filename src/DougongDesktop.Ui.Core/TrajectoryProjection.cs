using DougongDesktop.Ipc;

namespace DougongDesktop.Ui.Core;

/// <summary>轨迹时间轴上的一段工作类型（对应 Arc <c>AgentTimelineSpanKind</c>）。</summary>
public enum TrajectorySpanKind
{
    /// <summary>模型思考或生成回复（淡色段）。</summary>
    Thinking,

    /// <summary>工具调用或子智能体执行（实心段）。</summary>
    Tool,

    /// <summary>等待审批或阻塞等待（斜纹段）。</summary>
    Waiting,
}

/// <summary>轨迹段的结束状态。</summary>
public enum TrajectorySpanStatus
{
    Ok,
    Failed,
}

/// <summary>轨迹图中的一行智能体（首行为主智能体，其余为子智能体）。</summary>
public sealed record TrajectoryAgent(
    string Id,
    string Name,
    string? ModelName = null,
    string? Role = null);

/// <summary>轨迹图中的一段耗时条（单位：距该轮开始的秒数）。</summary>
public sealed record TrajectorySpan(
    string Id,
    string AgentId,
    TrajectorySpanKind Kind,
    double StartSeconds,
    double? EndSeconds,
    string Label,
    string? ToolName = null,
    string? Detail = null,
    TrajectorySpanStatus Status = TrajectorySpanStatus.Ok);

/// <summary>主智能体与子智能体之间的一次交接连线。</summary>
public sealed record TrajectoryHandoff(
    string FromAgentId,
    string ToAgentId,
    double AtSeconds,
    string? Label = null);

/// <summary>子智能体记录（补充本地观测到的完成时刻，不改 IPC 契约）。</summary>
public sealed record TrajectorySubagentRecord(
    SubagentInfo Info,
    DateTimeOffset? CompletedAt = null);

/// <summary>单轮对话的轨迹投影输入。</summary>
public sealed record TrajectoryTurnInput(
    string TurnId,
    DateTimeOffset StartedAt,
    TurnStatus Status,
    IReadOnlyList<ConversationItem> Items,
    DateTimeOffset? CompletedAt = null,
    IReadOnlyList<TrajectorySubagentRecord>? Subagents = null);

/// <summary>投影后的单轮轨迹视图模型（纯数据，不依赖 Arc）。</summary>
public sealed record TrajectoryTurnView(
    string TurnId,
    int TurnNumber,
    DateTimeOffset StartedAt,
    TurnStatus Status,
    double DurationSeconds,
    string Title,
    IReadOnlyList<TrajectoryAgent> Agents,
    IReadOnlyList<TrajectorySpan> Spans,
    IReadOnlyList<TrajectoryHandoff> Handoffs);

/// <summary>
/// 把会话轮次条目与子智能体记录投影成 Trajectory 时间轴数据（P8d D4）。
/// </summary>
/// <remarks>
/// <b>为什么放在 <c>Ui.Core</c> 而不是写在控件里</b>：投影逻辑只依赖 <see cref="DougongDesktop.Ipc"/>
/// 契约，不碰任何 Avalonia/Arc 控件，既能被无头/单测直接验证，也防止 UI 层出现手算时间戳的分支漂移。
/// <para>
/// <b>如实反映真实数据，不伪造 token/费用</b>：当前 IPC 契约不携带单条 span 的 token 消耗，
/// 因此投影层不编造数字；时间轴完全来自条目的 <c>At</c>、<c>ApprovalElapsed</c>、
/// <c>ExecutionElapsed</c> 与子智能体的 <c>StartedAt</c>。
/// </para>
/// </remarks>
public static class TrajectoryProjection
{
    public const string OrchestratorId = "orchestrator";
    public const double FallbackSpanSeconds = 0.2;

    /// <summary>将一轮会话数据投影为 <see cref="TrajectoryTurnView"/>。</summary>
    public static TrajectoryTurnView ProjectTurn(
        TrajectoryTurnInput turn,
        int turnNumber,
        string? modelName = null)
    {
        var ordered = turn.Items
            .OrderBy(i => i.At)
            .ToArray();

        var origin = turn.StartedAt;
        if (ordered.Length > 0 && ordered[0].At < origin)
            origin = ordered[0].At;

        var title = ExtractTurnTitle(ordered, turnNumber);
        var isRunning = turn.Status == TurnStatus.Running;

        var agents = new List<TrajectoryAgent>
        {
            new(OrchestratorId, "主智能体", NullIfBlank(modelName), "编排与对话"),
        };

        var spans = new List<TrajectorySpan>();
        var handoffs = new List<TrajectoryHandoff>();

        var turnEndSeconds = turn.CompletedAt is { } completedAt
            ? Math.Max(0, (completedAt - origin).TotalSeconds)
            : (double?)null;

        for (var i = 0; i < ordered.Length; i++)
        {
            var item = ordered[i];
            var start = Math.Max(0, (item.At - origin).TotalSeconds);
            var nextStart = i + 1 < ordered.Length
                ? Math.Max(start, (ordered[i + 1].At - origin).TotalSeconds)
                : (double?)null;

            switch (item)
            {
                case ReasoningItem reasoning:
                {
                    if (string.IsNullOrWhiteSpace(reasoning.Text) && !reasoning.IsStreaming)
                        break;

                    var open = isRunning && reasoning.IsStreaming;
                    var end = open
                        ? (double?)null
                        : ResolveEnd(start, nextStart, turnEndSeconds);

                    var preview = ReasoningPreview.LatestCompletedParagraphFirstLine(reasoning.Text);
                    if (string.IsNullOrWhiteSpace(preview))
                        preview = Truncate(reasoning.Text.Trim(), 36);

                    spans.Add(new TrajectorySpan(
                        Id: reasoning.Id,
                        AgentId: OrchestratorId,
                        Kind: TrajectorySpanKind.Thinking,
                        StartSeconds: start,
                        EndSeconds: end,
                        Label: string.IsNullOrEmpty(preview) ? "深度思考" : preview));
                    break;
                }

                case ToolCallItem tool:
                {
                    var toolStart = start;
                    var hasApprovalWait = tool.ApprovalElapsed is { } wait && wait > TimeSpan.Zero;

                    if (hasApprovalWait)
                    {
                        var waitEnd = start + tool.ApprovalElapsed!.Value.TotalSeconds;
                        spans.Add(new TrajectorySpan(
                            Id: $"{tool.Id}:approval",
                            AgentId: OrchestratorId,
                            Kind: TrajectorySpanKind.Waiting,
                            StartSeconds: start,
                            EndSeconds: waitEnd,
                            Label: $"等待审批 · {tool.ToolName}",
                            ToolName: tool.ToolName));
                        toolStart = waitEnd;
                    }
                    else if (tool.Status == ToolCallStatus.AwaitingApproval)
                    {
                        spans.Add(new TrajectorySpan(
                            Id: $"{tool.Id}:approval",
                            AgentId: OrchestratorId,
                            Kind: TrajectorySpanKind.Waiting,
                            StartSeconds: start,
                            EndSeconds: isRunning ? null : ResolveEnd(start, nextStart, turnEndSeconds),
                            Label: $"等待审批 · {tool.ToolName}",
                            ToolName: tool.ToolName));
                        break;
                    }

                    var toolOpen = isRunning && tool.Status is ToolCallStatus.Requested or ToolCallStatus.Running;
                    double? toolEnd;
                    if (toolOpen)
                    {
                        toolEnd = null;
                    }
                    else if (tool.ExecutionElapsed is { } exec && exec > TimeSpan.Zero)
                    {
                        toolEnd = toolStart + exec.TotalSeconds;
                    }
                    else
                    {
                        toolEnd = ResolveEnd(toolStart, nextStart, turnEndSeconds);
                    }

                    var failed = tool.Status is ToolCallStatus.Failed
                        or ToolCallStatus.Rejected
                        or ToolCallStatus.Cancelled;

                    var detail = NullIfBlank(tool.Diagnostic) ?? tool.Status switch
                    {
                        ToolCallStatus.Rejected => "审批被拒绝",
                        ToolCallStatus.Cancelled => "调用已取消",
                        ToolCallStatus.Failed => "工具执行失败",
                        _ => null,
                    };

                    spans.Add(new TrajectorySpan(
                        Id: tool.Id,
                        AgentId: OrchestratorId,
                        Kind: TrajectorySpanKind.Tool,
                        StartSeconds: toolStart,
                        EndSeconds: toolEnd,
                        Label: DescribeToolAction(tool.ToolName),
                        ToolName: tool.ToolName,
                        Detail: detail,
                        Status: failed ? TrajectorySpanStatus.Failed : TrajectorySpanStatus.Ok));
                    break;
                }

                case AssistantMessageItem msg:
                {
                    if (string.IsNullOrWhiteSpace(msg.Text) && !msg.IsStreaming)
                        break;

                    var open = isRunning && msg.IsStreaming;
                    var end = open
                        ? (double?)null
                        : ResolveEnd(start, nextStart, turnEndSeconds);

                    spans.Add(new TrajectorySpan(
                        Id: msg.Id,
                        AgentId: OrchestratorId,
                        Kind: TrajectorySpanKind.Thinking,
                        StartSeconds: start,
                        EndSeconds: end,
                        Label: "生成回复"));
                    break;
                }
            }
        }

        // 子智能体分行与交接连线
        if (turn.Subagents is { Count: > 0 } subs)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { OrchestratorId };
            foreach (var rec in subs.OrderBy(s => s.Info.StartedAt))
            {
                var sub = rec.Info;
                var agentId = string.IsNullOrWhiteSpace(sub.Id) ? $"sub-{agents.Count}" : sub.Id;
                if (seen.Add(agentId))
                {
                    agents.Add(new TrajectoryAgent(
                        Id: agentId,
                        Name: string.IsNullOrWhiteSpace(sub.Label) ? agentId : sub.Label,
                        ModelName: NullIfBlank(modelName),
                        Role: "子智能体"));
                }

                var dispatchAt = Math.Max(0, (sub.StartedAt - origin).TotalSeconds);
                handoffs.Add(new TrajectoryHandoff(
                    FromAgentId: OrchestratorId,
                    ToAgentId: agentId,
                    AtSeconds: dispatchAt,
                    Label: sub.Label));

                var subOpen = sub.State == SubagentRunState.Running && isRunning;
                double? subEnd = subOpen
                    ? null
                    : rec.CompletedAt is { } doneAt && doneAt > sub.StartedAt
                        ? (doneAt - origin).TotalSeconds
                        : ResolveEnd(dispatchAt, null, turnEndSeconds);

                var subFailed = sub.State is SubagentRunState.Failed or SubagentRunState.Cancelled;
                var subLabel = sub.ToolRounds is { } rounds
                    ? $"{sub.Label}（{rounds} 轮）"
                    : sub.Label;

                spans.Add(new TrajectorySpan(
                    Id: $"subagent:{agentId}",
                    AgentId: agentId,
                    Kind: TrajectorySpanKind.Tool,
                    StartSeconds: dispatchAt,
                    EndSeconds: subEnd,
                    Label: string.IsNullOrWhiteSpace(subLabel) ? "子智能体执行" : subLabel,
                    ToolName: "subagent",
                    Detail: sub.State switch
                    {
                        SubagentRunState.Failed => "子智能体执行失败",
                        SubagentRunState.Cancelled => "子智能体已取消",
                        _ => null,
                    },
                    Status: subFailed ? TrajectorySpanStatus.Failed : TrajectorySpanStatus.Ok));

                if (sub.State == SubagentRunState.Completed && subEnd is { } returnAt)
                {
                    handoffs.Add(new TrajectoryHandoff(
                        FromAgentId: agentId,
                        ToAgentId: OrchestratorId,
                        AtSeconds: returnAt,
                        Label: "返回结果"));
                }
            }
        }

        var duration = ComputeDuration(spans, handoffs, turnEndSeconds);

        return new TrajectoryTurnView(
            TurnId: turn.TurnId,
            TurnNumber: turnNumber,
            StartedAt: origin,
            Status: turn.Status,
            DurationSeconds: duration,
            Title: title,
            Agents: agents,
            Spans: spans,
            Handoffs: handoffs);
    }

    private static double ResolveEnd(double start, double? nextStart, double? turnEnd)
    {
        if (nextStart is { } next && next > start + 0.01)
            return next;
        if (turnEnd is { } end && end > start + 0.01)
            return end;
        return start + FallbackSpanSeconds;
    }

    private static double ComputeDuration(
        List<TrajectorySpan> spans,
        List<TrajectoryHandoff> handoffs,
        double? turnEnd)
    {
        var max = turnEnd ?? 0;
        foreach (var s in spans)
        {
            var point = s.EndSeconds ?? (s.StartSeconds + FallbackSpanSeconds);
            if (point > max) max = point;
        }

        foreach (var h in handoffs)
        {
            if (h.AtSeconds > max) max = h.AtSeconds;
        }

        return max > 0 ? max : (spans.Count > 0 ? FallbackSpanSeconds : 0);
    }

    private static string ExtractTurnTitle(ConversationItem[] items, int turnNumber)
    {
        var user = items.OfType<UserMessageItem>().FirstOrDefault();
        if (user is not null && !string.IsNullOrWhiteSpace(user.Text))
        {
            var firstLine = user.Text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(firstLine))
                return $"第 {turnNumber} 轮 · {Truncate(firstLine, 28)}";
        }

        return $"第 {turnNumber} 轮";
    }

    private static string DescribeToolAction(string toolName) => ProcessActivity.Classify(toolName) switch
    {
        ToolCategory.Read => $"读取文件 · {toolName}",
        ToolCategory.ReadImage => $"查看图片 · {toolName}",
        ToolCategory.Search => $"检索内容 · {toolName}",
        ToolCategory.Write => $"写入文件 · {toolName}",
        ToolCategory.Edit => $"编辑文件 · {toolName}",
        ToolCategory.Commands => $"执行命令 · {toolName}",
        ToolCategory.Code => $"运行代码 · {toolName}",
        ToolCategory.WebSearch => $"网页搜索 · {toolName}",
        ToolCategory.WebFetch => $"抓取网页 · {toolName}",
        ToolCategory.Subagents => $"子智能体 · {toolName}",
        ToolCategory.Plan => $"更新计划 · {toolName}",
        ToolCategory.Questions => $"询问用户 · {toolName}",
        _ => toolName,
    };

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 1)] + "…";
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// 会话级轨迹状态仓库：聚合 IPC 增量事件与快照，按需输出各轮 <see cref="TrajectoryTurnView"/>。
/// </summary>
public sealed class TrajectorySessionStore
{
    private sealed class MutableTurn
    {
        public required string TurnId { get; init; }
        public DateTimeOffset StartedAt { get; set; }
        public TurnStatus Status { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public List<ConversationItem> Items { get; } = [];
    }

    private readonly List<MutableTurn> _turns = [];
    private readonly Dictionary<string, MutableTurn> _turnById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _itemTurn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrajectorySubagentRecord> _subagents = new(StringComparer.Ordinal);

    private string? _modelName;

    public string? ModelName => _modelName;

    public void SetModelName(string? modelName)
    {
        if (!string.IsNullOrWhiteSpace(modelName))
            _modelName = modelName.Trim();
    }

    public void Clear()
    {
        _turns.Clear();
        _turnById.Clear();
        _itemTurn.Clear();
        _subagents.Clear();
    }

    /// <summary>从会话快照全量重置轨迹。</summary>
    public void LoadSnapshot(SessionSnapshot snapshot)
    {
        Clear();
        if (!string.IsNullOrWhiteSpace(snapshot.ModelName))
            _modelName = snapshot.ModelName;

        foreach (var turn in snapshot.Turns)
        {
            var entry = EnsureTurn(turn.Id, turn.StartedAt, turn.Status);
            entry.Items.AddRange(turn.Items);
            foreach (var item in turn.Items)
                _itemTurn[item.Id] = turn.Id;
        }
    }

    public void OnTurnStarted(string turnId, DateTimeOffset startedAt)
    {
        var entry = EnsureTurn(turnId, startedAt, TurnStatus.Running);
        entry.Status = TurnStatus.Running;
        entry.CompletedAt = null;
    }

    public void OnItemAdded(string turnId, ConversationItem item)
    {
        var entry = EnsureTurn(turnId, item.At, TurnStatus.Running);
        if (item.At < entry.StartedAt)
            entry.StartedAt = item.At;

        var existingIdx = entry.Items.FindIndex(x => x.Id == item.Id);
        if (existingIdx >= 0)
            entry.Items[existingIdx] = item;
        else
            entry.Items.Add(item);

        _itemTurn[item.Id] = turnId;
    }

    public void OnItemUpdated(ConversationItem item)
    {
        if (!_itemTurn.TryGetValue(item.Id, out var turnId)
            || !_turnById.TryGetValue(turnId, out var entry))
        {
            return;
        }

        var idx = entry.Items.FindIndex(x => x.Id == item.Id);
        if (idx >= 0)
            entry.Items[idx] = item;
        else
            entry.Items.Add(item);
    }

    public void OnTurnCompleted(string turnId, TurnStatus status, DateTimeOffset completedAt)
    {
        var entry = EnsureTurn(turnId, completedAt, status);
        entry.Status = status;
        entry.CompletedAt = completedAt;
    }

    public void OnSubagentsListed(IReadOnlyList<SubagentInfo> agents, DateTimeOffset observedAt)
    {
        foreach (var agent in agents)
            OnSubagentUpdated(agent, observedAt);
    }

    public void OnSubagentUpdated(SubagentInfo agent, DateTimeOffset observedAt)
    {
        DateTimeOffset? completedAt = null;
        if (_subagents.TryGetValue(agent.Id, out var prev))
            completedAt = prev.CompletedAt;

        if (agent.State != SubagentRunState.Running && completedAt is null)
            completedAt = observedAt;

        _subagents[agent.Id] = new TrajectorySubagentRecord(agent, completedAt);
    }

    /// <summary>生成所有有内容的轮次轨迹视图（按时间先后编号 1..N）。</summary>
    public IReadOnlyList<TrajectoryTurnView> BuildViews()
    {
        var result = new List<TrajectoryTurnView>(_turns.Count);

        for (var i = 0; i < _turns.Count; i++)
        {
            var turn = _turns[i];
            var subs = _subagents.Values
                .Where(s => string.Equals(s.Info.ParentTurnId, turn.TurnId, StringComparison.Ordinal))
                .ToArray();

            var view = TrajectoryProjection.ProjectTurn(
                new TrajectoryTurnInput(
                    TurnId: turn.TurnId,
                    StartedAt: turn.StartedAt,
                    Status: turn.Status,
                    Items: turn.Items,
                    CompletedAt: turn.CompletedAt,
                    Subagents: subs),
                turnNumber: i + 1,
                modelName: _modelName);

            if (view.Spans.Count > 0 || turn.Status == TurnStatus.Running)
                result.Add(view);
        }

        return result;
    }

    private MutableTurn EnsureTurn(string turnId, DateTimeOffset startedAt, TurnStatus initialStatus)
    {
        if (_turnById.TryGetValue(turnId, out var existing))
            return existing;

        var created = new MutableTurn
        {
            TurnId = turnId,
            StartedAt = startedAt,
            Status = initialStatus,
        };
        _turns.Add(created);
        _turnById[turnId] = created;
        return created;
    }
}
