using System.Text.Json;
using Dougong.Agent;
using DougongDesktop.Ipc;
using IpcSessionSummary = Dougong.HostProtocol.SessionSummary;
using Microsoft.Extensions.AI;

namespace DougongDesktop.Framework;

/// <summary>
/// 一个会话的运行时：持有内核装配好的 <see cref="IAgentRuntime"/>，
/// 把它的 <see cref="AgentEvent"/> 流翻译成 IPC 条目。
/// </summary>
/// <remarks>
/// 职责边界很窄，刻意的：它只做**投影**，不做产品决策。
/// 用什么工具、怎么开场、权限策略，都由 <see cref="SessionComposer"/> 在装配时决定。
/// <para>
/// 序号（<see cref="Sequence"/>）是单调递增的，客户端据此检测缺口。
/// 它不是时间戳——重连时客户端拿快照的序号做基准，只能应用更大的。
/// </para>
/// <para>
/// <b>⚠ 延后事项（P4 收尾必须处理）</b>：本类现在把两件事混在一起——
/// <list type="number">
/// <item><b>纯映射</b>：<see cref="AgentEvent"/> → <see cref="ConversationItem"/>（无状态，可测）</item>
/// <item><b>状态容器</b>：序号递增、当前条目 ID、turn 列表（有状态）</item>
/// </list>
/// 换 agent 框架时只有第 1 件要重写。拆开后重写量从 466 行降到约 200 行
/// （见实施方案 §3.6）。
/// <para>
/// <b>现在不拆的理由</b>：P4 还没定下消息流的投影形状，提前拆会返工。
/// <b>触发条件</b>：P4 收尾时。
/// <b>台账位置</b>：vault <c>projects/dougong/Dougong-Desktop-开发计划.md</c> §六之二 第 1 条。
/// </para>
/// </para>
/// </remarks>
public sealed class SessionRuntime : IAsyncDisposable
{
    private readonly IAgentRuntime _runtime;
    private readonly List<TurnSnapshot> _turns = [];
    private readonly object _sync = new();
    private long _lastSequence;
    private CancellationTokenSource? _currentTurn;
    private string? _currentAssistantItemId;

    /// <summary>
    /// 当前正在流式的推理条目。
    /// </summary>
    /// <remarks>
    /// <b>与正文条目分开维护</b>：推理与回答是两条独立的流，交错到达。
    /// 共用一个 Id 会让推理追加到回答里——那正是"把推理当结论"的错误。
    /// </remarks>
    private string? _currentReasoningItemId;

    private readonly Func<long> _nextSequence;

    /// <param name="nextSequence">
    /// 序号分配器。**必须由宿主提供全局唯一计数器**——
    /// 每会话各自从 0 计数会让同一条连接上的序号错乱
    /// （客户端按严格递增检测缺口，会误判为丢消息）。
    /// </param>
    public SessionRuntime(
        string sessionId,
        string title,
        string workspace,
        IAgentRuntime runtime,
        string? modelName,
        Func<long> nextSequence)
    {
        SessionId = sessionId;
        Title = title;
        Workspace = workspace;
        ModelName = modelName;
        _runtime = runtime;
        _nextSequence = nextSequence;
    }

    public string SessionId { get; }

    public string Title { get; private set; }

    public string Workspace { get; }

    public string? ModelName { get; }

    public bool IsRunning => _currentTurn is not null;

    /// <summary>最近一次分配的序号。快照用它做增量基准。</summary>
    public long Sequence => Interlocked.Read(ref _lastSequence);

    public IReadOnlyList<TurnSnapshot> Turns
    {
        get { lock (_sync) return [.. _turns]; }
    }

    public SessionSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new SessionSnapshot(
                SessionId, Title, Workspace, Sequence, [.. _turns], IsRunning, ModelName);
        }
    }

    public IpcSessionSummary Summary() => new(
        SessionId,
        Title,
        Workspace,
        UpdatedAt,
        Turns.Sum(t => t.Items.Count),
        HasPendingApproval);

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.Now;

    public bool HasPendingApproval { get; set; }

    /// <summary>
    /// 跑一个回合。事件边产出边回调，调用方负责推给客户端。
    /// </summary>
    /// <remarks>
    /// <paramref name="emit"/> 收到的每个事件都带新的序号。
    /// 这里不做节流：文本增量本来就不多，过早优化会让"流式"变成"分段刷新"。
    /// </remarks>
    public async Task RunTurnAsync(
        string userInput,
        Func<ServerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var turnId = $"turn-{Guid.NewGuid():N}"[..13];
        var turn = new TurnSnapshot(turnId, DateTimeOffset.Now, TurnStatus.Running, []);

        lock (_sync)
        {
            _turns.Add(turn);
            UpdatedAt = DateTimeOffset.Now;
        }

        _currentTurn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentAssistantItemId = null;
        _currentReasoningItemId = null;


        try
        {
            await emit(new TurnStartedEvent
            {
                Sequence = NextSequence(),
                SessionId = SessionId,
                TurnId = turnId,
            }).ConfigureAwait(false);

            // 用户输入作为第一条条目出现，先于任何模型输出。
            await AppendItemAsync(turnId, new UserMessageItem
            {
                Id = NextItemId(),
                Text = userInput,
            }, emit).ConfigureAwait(false);

            // 历史从已提交的 turns 重建。SDK 要求调用方传历史，
            // 但它只接受纯文本 + 工具配对，所以这里做有损投影——
            // 工具调用必须保留配对，否则 provider 会拒掉整轮。
            var history = BuildHistory();

            var request = new AgentRunRequest(
                SessionId, turnId, history, userInput);

            var outcome = AgentRunOutcome.Completed;
            string? diagnostic = null;

            await foreach (var evt in _runtime.RunAsync(request, _currentTurn.Token)
                               .ConfigureAwait(false))
            {
                // 投影逻辑在 EventProjection（纯映射），这里只做状态维护。
                // 换 agent 框架时只改 EventProjection，这段不动。
                switch (EventProjection.Map(evt))
                {
                    case EventProjection.Projection.TextDelta delta:
                        await AppendTextDeltaAsync(turnId, delta.Text, emit).ConfigureAwait(false);
                        break;

                    case EventProjection.Projection.ReasoningDelta reasoning:
                        await AppendReasoningDeltaAsync(turnId, reasoning.Text, emit).ConfigureAwait(false);
                        break;

                    case EventProjection.Projection.Added added:
                        await AppendItemAsync(turnId, added.Item, emit).ConfigureAwait(false);
                        break;

                    case EventProjection.Projection.ToolCompleted completed:
                        await UpdateToolCallAsync(turnId, completed.Result, emit).ConfigureAwait(false);
                        break;

                    case EventProjection.Projection.Signal signal:
                        outcome = signal.Outcome;
                        diagnostic = signal.Diagnostic;
                        break;

                    case EventProjection.Projection.Updated updated:
                        await emit(new ItemUpdatedEvent
                        {
                            Sequence = NextSequence(),
                            SessionId = SessionId,
                            Item = updated.Item,
                        }).ConfigureAwait(false);
                        break;

                    case EventProjection.Projection.Ignored:
                        break;
                }
            }

            var status = outcome switch
            {
                AgentRunOutcome.Completed => TurnStatus.Completed,
                AgentRunOutcome.Cancelled => TurnStatus.Cancelled,
                _ => TurnStatus.Failed,
            };

            CompleteTurn(turnId, status);

            await emit(new TurnCompletedEvent
            {
                Sequence = NextSequence(),
                SessionId = SessionId,
                TurnId = turnId,
                Status = status,
                Diagnostic = diagnostic,
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompleteTurn(turnId, TurnStatus.Cancelled);
            await emit(new TurnCompletedEvent
            {
                Sequence = NextSequence(),
                SessionId = SessionId,
                TurnId = turnId,
                Status = TurnStatus.Cancelled,
                Diagnostic = "已取消。",
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CompleteTurn(turnId, TurnStatus.Failed);
            await emit(new TurnCompletedEvent
            {
                Sequence = NextSequence(),
                SessionId = SessionId,
                TurnId = turnId,
                Status = TurnStatus.Failed,
                Diagnostic = $"{ex.GetType().Name}: {ex.Message}",
            }).ConfigureAwait(false);
        }
        finally
        {
            _currentTurn?.Dispose();
            _currentTurn = null;
            UpdatedAt = DateTimeOffset.Now;
        }
    }

    public void Cancel()
    {
        try
        {
            _currentTurn?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 回合已经结束，取消是空操作。
        }
    }

    public ValueTask DisposeAsync()
    {
        _currentTurn?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 内部：条目维护 ───────────────────────────────────────

    private async Task AppendTextDeltaAsync(
        string turnId, string text, Func<ServerEvent, Task> emit)
    {
        if (_currentAssistantItemId is null)
        {
            var item = new AssistantMessageItem
            {
                Id = NextItemId(),
                Text = text,
                IsStreaming = true,
            };
            _currentAssistantItemId = item.Id;
            await AppendItemAsync(turnId, item, emit).ConfigureAwait(false);
            return;
        }

        var updated = UpdateItem<AssistantMessageItem>(
            turnId, _currentAssistantItemId,
            existing => existing with { Text = existing.Text + text });

        if (updated is not null)
        {
            await emit(new ItemUpdatedEvent
            {
                Sequence = NextSequence(),
                SessionId = SessionId,
                Item = updated,
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 追加推理增量。
    /// </summary>
    /// <remarks>
    /// 与正文增量同构，但维护独立的条目 Id。
    /// <para>
    /// <b>推理条目标记 IsStreaming</b>：它随 provider 逐段到达，
    /// 不标的话界面上会显示成"已经写完了的一段引用"，
    /// 用户会误以为那是模型给出的结论。
    /// </para>
    /// </remarks>
    private async Task AppendReasoningDeltaAsync(
        string turnId, string text, Func<ServerEvent, Task> emit)
    {
        if (_currentReasoningItemId is null)
        {
            var item = new ReasoningItem
            {
                Id = NextItemId(),
                Text = text,
                IsStreaming = true,
            };
            _currentReasoningItemId = item.Id;
            await AppendItemAsync(turnId, item, emit).ConfigureAwait(false);
            return;
        }

        var updated = UpdateItem<ReasoningItem>(
            turnId, _currentReasoningItemId,
            existing => existing with { Text = existing.Text + text });

        if (updated is not null)
        {
            await emit(new ItemUpdatedEvent
            {
                Sequence = NextSequence(),
                SessionId = SessionId,
                Item = updated,
            }).ConfigureAwait(false);
        }
    }

    private async Task UpdateToolCallAsync(
        string turnId, ToolDispatchResult result, Func<ServerEvent, Task> emit)
    {
        var itemId = $"tool-{result.InvocationId}";
        var status = result.Outcome switch
        {
            ToolCallOutcome.Succeeded => ToolCallStatus.Succeeded,
            ToolCallOutcome.Failed => ToolCallStatus.Failed,
            ToolCallOutcome.RejectedNotApproved => ToolCallStatus.Rejected,
            ToolCallOutcome.RejectedInvalidArguments => ToolCallStatus.Rejected,
            ToolCallOutcome.RejectedDuplicateInvocation => ToolCallStatus.Rejected,
            ToolCallOutcome.RejectedUnknownTool => ToolCallStatus.Rejected,
            ToolCallOutcome.CancelledBeforeExecution => ToolCallStatus.Cancelled,
            ToolCallOutcome.CancelledDuringExecution => ToolCallStatus.Cancelled,
            ToolCallOutcome.OutcomeUnknown => ToolCallStatus.Unknown,
            _ => ToolCallStatus.Failed,
        };

        var updated = UpdateItem<ToolCallItem>(turnId, itemId, existing => existing with
        {
            Status = status,
            ResultText = result.Result?.Content.GetRawText() ?? result.Diagnostic,
            Diagnostic = result.Diagnostic,
        });

        if (updated is not null)
        {
            await emit(new ItemUpdatedEvent
            {
                Sequence = NextSequence(),
                SessionId = SessionId,
                Item = updated,
            }).ConfigureAwait(false);
        }
    }

    private async Task AppendItemAsync(
        string turnId, ConversationItem item, Func<ServerEvent, Task> emit)
    {
        lock (_sync)
        {
            var index = _turns.FindIndex(t => t.Id == turnId);
            if (index >= 0)
            {
                _turns[index] = _turns[index] with
                {
                    Items = [.. _turns[index].Items, item],
                };
            }
            UpdatedAt = DateTimeOffset.Now;
        }

        await emit(new ItemAddedEvent
        {
            Sequence = NextSequence(),
            SessionId = SessionId,
            TurnId = turnId,
            Item = item,
        }).ConfigureAwait(false);
    }

    private T? UpdateItem<T>(string turnId, string itemId, Func<T, T> update)
        where T : ConversationItem
    {
        lock (_sync)
        {
            var turnIndex = _turns.FindIndex(t => t.Id == turnId);
            if (turnIndex < 0) return null;

            var items = _turns[turnIndex].Items;
            var itemIndex = items.ToList().FindIndex(i => i.Id == itemId);
            if (itemIndex < 0 || items[itemIndex] is not T existing) return null;

            var updated = update(existing);
            var newItems = items.ToList();
            newItems[itemIndex] = updated;
            _turns[turnIndex] = _turns[turnIndex] with { Items = newItems };
            UpdatedAt = DateTimeOffset.Now;
            return updated;
        }
    }

    private void CompleteTurn(string turnId, TurnStatus status)
    {
        lock (_sync)
        {
            var index = _turns.FindIndex(t => t.Id == turnId);
            if (index >= 0)
            {
                _turns[index] = _turns[index] with { Status = status };
            }

            // 流式结束：把 IsStreaming 关掉，UI 据此移除光标。
            var items = _turns[index >= 0 ? index : _turns.Count - 1].Items.ToList();
            for (var i = 0; i < items.Count; i++)
            {
                items[i] = items[i] switch
                {
                    AssistantMessageItem a => a with { IsStreaming = false },
                    ReasoningItem r => r with { IsStreaming = false },
                    _ => items[i],
                };
            }

            if (index >= 0) _turns[index] = _turns[index] with { Items = items };
            UpdatedAt = DateTimeOffset.Now;
        }
    }

    /// <summary>
    /// 从已发生的回合重建 SDK 需要的历史。
    /// </summary>
    /// <remarks>
    /// 只保留纯文本对话：工具调用不进入历史，因为 SDK 要求
    /// <c>tool_calls</c> 与 <c>tool_call_id</c> 严格配对，
    /// 而这里的投影是给"新一轮对话"用的，不是完整的会话重放。
    /// 完整恢复走 <c>JsonlAgentStore</c>，那是另一条路径（Phase 2）。
    /// </remarks>
    private IReadOnlyList<AgentMessage> BuildHistory()
    {
        var messages = new List<AgentMessage>();
        lock (_sync)
        {
            foreach (var turn in _turns)
            {
                foreach (var item in turn.Items)
                {
                    switch (item)
                    {
                        case UserMessageItem user:
                            messages.Add(new AgentMessage(AgentRole.User, user.Text));
                            break;
                        case AssistantMessageItem assistant when !string.IsNullOrEmpty(assistant.Text):
                            messages.Add(new AgentMessage(AgentRole.Assistant, assistant.Text));
                            break;
                    }
                }
            }
        }

        // 最后一条是本轮刚加入的用户输入，SDK 会自己加，避免重复。
        if (messages.Count > 0 && messages[^1].Role is AgentRole.User)
        {
            messages.RemoveAt(messages.Count - 1);
        }

        return messages;
    }

    private long NextSequence()
    {
        var value = _nextSequence();
        Interlocked.Exchange(ref _lastSequence, value);
        return value;
    }

    private static string NextItemId() => $"item-{Guid.NewGuid():N}"[..13];
}
