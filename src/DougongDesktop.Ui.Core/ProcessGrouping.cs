using DougongDesktop.Ipc;

namespace DougongDesktop.Ui.Core;

/// <summary>
/// 一条"根"：独立条目、过程组，或整轮过程块（M15）。
/// </summary>
public abstract record ConversationRoot;

/// <summary>独立条目：回复、输入、分隔符等不该进组的东西。</summary>
public sealed record IndependentRoot(ConversationItem Item) : ConversationRoot;

/// <summary>
/// 整轮过程块：同一轮次里相邻的若干过程组，共用一条轮次控制条。
/// </summary>
/// <remarks>
/// <b>这是"外层"</b>（dsh 的 whole-Turn folding）。它管"过程可见吗"，
/// 内部的 <see cref="ProcessGroupRoot"/> 管"每一组怎么展开"。
/// 外层关着时内层开不开都看不见——可见性是**由外向内**生效的。
/// </remarks>
/// <param name="TurnId">所属轮次。</param>
/// <param name="Groups">块内的过程组，保持原顺序。</param>
/// <param name="TurnStatus">轮次终态；null 表示还不知道（未关闭）。</param>
/// <param name="HasInputAfterProcess">过程之后是否还有用户输入（决定整轮可不可折）。</param>
public sealed record TurnProcessRoot(
    string? TurnId,
    IReadOnlyList<ProcessGroupRoot> Groups,
    TurnStatus? TurnStatus,
    bool HasInputAfterProcess) : ConversationRoot
{
    /// <summary>块内还有工具在跑吗。</summary>
    public bool IsRunning => Groups.Any(g => g.IsRunning);

    /// <summary>块内全部条目数（诊断用）。</summary>
    public int ItemCount => Groups.Sum(g => g.Items.Count);
}

/// <summary>一个过程组：相邻的过程内容（推理 / 工具）归在一起。</summary>
/// <param name="TurnId">所属轮次；null 表示条目没有轮次信息。</param>
/// <param name="Items">组内成员，保持原顺序。</param>
public sealed record ProcessGroupRoot(string? TurnId, IReadOnlyList<ConversationItem> Items) : ConversationRoot
{
    /// <summary>组内工具调用数。</summary>
    public int ToolCount => Items.Count(i => i is ToolCallItem);

    /// <summary>组内推理条目数。</summary>
    public int ReasoningCount => Items.Count(i => i is ReasoningItem);

    /// <summary>类别计数（已按 dsh 的排名规则排序）。</summary>
    public IReadOnlyList<(ToolCategory Category, int Count)> RankedCounts =>
        ProcessActivity.CountByCategory(Items);

    /// <summary>组内还有工具在跑吗。</summary>
    public bool IsRunning =>
        Items.Any(i => i is ToolCallItem
        {
            Status: ToolCallStatus.Running or ToolCallStatus.AwaitingApproval,
        });

    /// <summary>
    /// 组标题（dsh 的 process title）。
    /// </summary>
    /// <param name="running">组是否还在运行（决定用运行态还是关闭态文案）。</param>
    /// <param name="chinese">用中文标签还是英文标签。</param>
    /// <remarks>
    /// <b>不是「使用了 N 个工具」</b>。dsh 的标题说的是**干了哪几类事**，
    /// 且关闭态不显示数量（README "Group-title rules"）。我第一版自己编了
    /// 「使用了 N 个工具」，那是凭直觉归纳的产物——它丢失了类别信息，
    /// 也把 <c>read → bash → read</c> 与 <c>read → bash → edit</c> 说成同一件事。
    /// </remarks>
    public string SummaryText(bool running, bool chinese = true) =>
        running
            ? ProcessActivity.RunningTitle(Items, chinese)
            : ProcessActivity.ClosedTitle(RankedCounts, chinese);
}

/// <summary>
/// 把扁平条目序列切成"过程组 + 独立根"（dsh 的 process-groups.ts）。
/// </summary>
/// <remarks>
/// <b>规则来源</b>：dsh <c>packages/client/ui-chat/src/client/conversation-nodes/README.md</c>
/// 的 "Process grouping" 一节（0.2.2-rc.2）。四类输入，四种动作：
/// <list type="table">
/// <item><term>推理</term><description>加入当前组（必要时新建）</description></item>
/// <item><term>助手回复</term><description><b>结束当前组</b>，自身独立成根</description></item>
/// <item><term>分隔符</term><description><b>结束当前组</b>，自身独立成根</description></item>
/// <item><term>工具</term><description>加入当前组（必要时新建）</description></item>
/// </list>
/// <para>
/// <b>分组与"折叠"是两件事</b>：这里只决定"哪些条目属于同一组"，
/// 不决定"要不要收起来"。后者由 <see cref="ChatPresentationPolicy"/> 的
/// <c>FoldCompletedTurns</c> 决定，两者独立。
/// </para>
/// <para>
/// <b>不需要事件流</b>：dsh 的实现读的是它自己的 <c>ChatNode</c> 事件模型，
/// 但规则本身只依赖"有序条目 + 每条的分类"。我们的条目已经是投影好的扁平
/// 序列，所以这条规则可以直接套用——这也是它能在本项目落地的前提。
/// </para>
/// </remarks>
public static class ProcessGrouping
{
    /// <summary>
    /// 分组。
    /// </summary>
    /// <param name="items">有序条目。</param>
    /// <param name="turnIdOf">查某条目所属轮次；没有轮次信息返回 null。</param>
    /// <remarks>
    /// <b>轮次是硬边界</b>：README 写明"A Node from another Turn or without a Turn
    /// breaks the same-Turn sequence"。所以换轮次必须结束当前组，
    /// 否则两个轮的推理会被并成一组，折叠时会一起收起来。
    /// </remarks>
    public static IReadOnlyList<ConversationRoot> Group(
        IReadOnlyList<ConversationItem> items,
        Func<ConversationItem, string?> turnIdOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(turnIdOf);

        var roots = new List<ConversationRoot>();
        var current = new List<ConversationItem>();
        string? currentTurn = null;

        void Flush()
        {
            if (current.Count == 0) return;
            roots.Add(new ProcessGroupRoot(currentTurn, [.. current]));
            current.Clear();
        }

        foreach (var item in items)
        {
            var turn = turnIdOf(item);

            // 轮次变了：先收掉上一组（README: 跨轮次打断同轮序列）
            if (current.Count > 0 && turn != currentTurn)
                Flush();

            switch (item)
            {
                // 推理与工具：并入当前组
                case ReasoningItem:
                case ToolCallItem:
                    currentTurn = turn;
                    current.Add(item);
                    break;

                // 助手回复：收组，自身独立
                case AssistantMessageItem:
                    Flush();
                    roots.Add(new IndependentRoot(item));
                    break;

                // 其余（用户输入、计划、通知等）按独立根处理并收组。
                //
                // README 把「Turn 输入 / turn-error / turn-tail / turn-process 控件」
                // 都列为"结束前一组并独立成根"。我们对这些一视同仁：
                // 它们都不是过程内容，留在组里会让折叠把用户的输入也藏起来。
                default:
                    Flush();
                    roots.Add(new IndependentRoot(item));
                    break;
            }

            // 独立根之后，下一组的轮次基准要重置
            if (roots.Count > 0 && roots[^1] is IndependentRoot)
                currentTurn = null;
        }

        Flush();
        return roots;
    }
}
