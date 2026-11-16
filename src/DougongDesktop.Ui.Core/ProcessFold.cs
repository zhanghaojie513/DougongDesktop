using DougongDesktop.Ipc;

namespace DougongDesktop.Ui.Core;

/// <summary>
/// 两层折叠判定（dsh 的 whole-Turn folding + 组内 disclosure）。
/// </summary>
/// <remarks>
/// <b>dsh 是两层，不是一层</b>——这是本次对照源码发现的最大偏差。
/// <list type="number">
/// <item><b>整轮折叠</b>（外层）：已完成的轮次把**整段过程**藏到轮次控制条后面。
/// 它管"过程可见吗"，与分组无关。</item>
/// <item><b>组内折叠</b>（内层）：每个过程组的标题与组体各自是否可折、默认开合。</item>
/// </list>
/// 两层是包含关系：外层关着时，内层开不开都看不见
/// （README: "Visibility applies from outside inward ... Opening an inner layer
/// cannot bypass a closed outer layer"）。
/// <para>
/// 我第一版只有一层，把「组体是否收起」当成「整轮是否收起」，
/// 结果既丢了整轮折叠，又把正在运行的组也折了（正在跑的工具看不见）。
/// </para>
/// </remarks>
public static class ProcessFold
{
    /// <summary>
    /// 整轮折叠是否可用（轮次控制条能不能点）。
    /// </summary>
    /// <remarks>
    /// 照 README 的「Turn condition」表：
    /// <list type="bullet">
    /// <item>未关闭 → 不可折（过程保持展开）</item>
    /// <item>停止或失败 → 不可折（这些正是要回看的现场）</item>
    /// <item>首个过程证据之后还有可见输入 → 不可折（过程被输入打断，折起来会藏掉上下文）</item>
    /// </list>
    /// Verbose 档整轮永不折叠。
    /// </remarks>
    public static bool WholeTurnCollapsible(
        ChatPresentationPolicy policy,
        TurnStatus? turnStatus,
        bool hasInputAfterProcess)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.FoldCompletedTurns) return false;
        if (turnStatus is not TurnStatus.Completed) return false;   // 未关闭/失败/取消
        if (hasInputAfterProcess) return false;

        return true;
    }

    /// <summary>
    /// 整轮过程是否**默认藏起来**。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="WholeTurnCollapsible"/> 分开：可折不等于默认折。
    /// dsh 里 <c>collapseTiming = next-input</c> 会让最近一次交换保持展开态，
    /// 此时仍可手动折。这里把"可折"和"默认折"分成两个判断，是为了
    /// 以后接 collapseTiming 时不用改渲染层的语义。
    /// </remarks>
    public static bool WholeTurnHiddenByDefault(
        ChatPresentationPolicy policy,
        TurnStatus? turnStatus,
        bool hasInputAfterProcess)
    {
        if (!WholeTurnCollapsible(policy, turnStatus, hasInputAfterProcess)) return false;

        // 完成即折是默认时机；等到下次输入才折时，最近一轮保持展开。
        return policy.CollapseTiming == CollapseTiming.Completion;
    }

    /// <summary>
    /// 组标题是否显示（并可否折叠）。
    /// </summary>
    /// <remarks>
    /// 照模式表：
    /// <list type="bullet">
    /// <item>Compact / Standard — 总是显示类别摘要标题</item>
    /// <item>Detailed — **运行中的轮次不显示标题**（保持平铺），历史轮次显示</item>
    /// <item>Verbose — 不显示标题（过程全摊开，没有组的概念）</item>
    /// </list>
    /// </remarks>
    public static bool ShowGroupHeader(ChatPresentationPolicy policy, bool turnClosed)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy.StepGrouping switch
        {
            StepGroupingKind.Collapsed => true,
            StepGroupingKind.History => turnClosed,
            _ => false,
        };
    }

    /// <summary>
    /// 组体是否**默认展开**。
    /// </summary>
    /// <remarks>
    /// 照模式表「Process-group body」一行：
    /// <list type="bullet">
    /// <item>Compact / Standard — 初始收起</item>
    /// <item>Detailed — 运行中直接可见；历史轮次手动展开</item>
    /// <item>Verbose — 运行中与历史都直接可见</item>
    /// </list>
    /// <para>
    /// <b>Detailed 运行中"直接可见"这条很重要</b>：它是默认档最常见的状态——
    /// 用户正在等结果时看的应该是实时的工具行，而不是一个折叠标题。
    /// </para>
    /// </remarks>
    public static bool GroupBodyOpenByDefault(ChatPresentationPolicy policy, bool turnClosed)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy.StepGrouping switch
        {
            StepGroupingKind.Collapsed => false,   // compact / standard
            StepGroupingKind.History => !turnClosed,
            _ => true,                              // verbose
        };
    }
}
