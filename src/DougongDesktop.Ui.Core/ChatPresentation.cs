namespace DougongDesktop.Ui.Core;

/// <summary>
/// 工作详情模式（dsh 的 <c>transcriptView</c>）。
/// </summary>
/// <remarks>
/// <b>来源</b>：dsh <c>packages/client/ui-chat/src/chat-settings.ts</c> 与
/// <c>client/presentation-policy.ts</c>（0.2.2-rc.2）。
/// <para>
/// <b>四个模式不是"UI 风格"，是一组能力开关</b>——见 <see cref="ChatPresentationPolicy"/>。
/// 这一点很容易误判：以为只是"显示多一点少一点"，实际它同时决定
/// 轮次是否折叠、分组标题是否可折叠、过程细节是否出现。
/// </para>
/// </remarks>
public enum TranscriptViewMode
{
    /// <summary>最紧凑：不显示过程细节，不显示推理预览。</summary>
    Compact,

    /// <summary>标准：显示过程细节与推理预览。</summary>
    Standard,

    /// <summary>详细（默认）：分组标题只在历史轮次上可折叠。</summary>
    Detailed,

    /// <summary>啰嗦：不折叠完成的轮次，过程始终可见。</summary>
    Verbose,
}

/// <summary>完成的轮次何时回到历史呈现（dsh 的 <c>collapseTiming</c>）。</summary>
public enum CollapseTiming
{
    /// <summary>完成即折叠（默认）。</summary>
    Completion,

    /// <summary>等到下一次输入才折叠。</summary>
    NextInput,
}

/// <summary>折叠时哪些助手回复保持可见（dsh 的 <c>foldedReplies</c>）。</summary>
public enum FoldedReplies
{
    /// <summary>所有回复都可见（默认）。</summary>
    All,

    /// <summary>只有最终答复可见。</summary>
    Final,
}

/// <summary>
/// 一个工作详情模式启用的能力集合（dsh 的 <c>ChatPresentationPolicy</c>）。
/// </summary>
/// <remarks>
/// <b>照抄 dsh 的 <c>POLICIES</c> 表，不自己推导</b>：四个模式之间不是单调递增的。
/// 例如 <c>verbose</c> 的 <c>liveProcessDetail</c> 是 <c>false</c> 而
/// <c>detailed</c> 是 <c>true</c>——它不是"看更多"，而是"不看实时细节、
/// 但把所有过程都摊开"。凭直觉推"模式越细越多"会得到错的表。
/// </remarks>
public sealed record ChatPresentationPolicy(
    TranscriptViewMode Mode,
    CollapseTiming CollapseTiming,
    FoldedReplies FoldedReplies,
    bool FoldCompletedTurns,
    StepGroupingKind StepGrouping,
    bool LiveProcessDetail,
    bool SettledReasoningPreview);

/// <summary>分组标题的折叠范围。</summary>
public enum StepGroupingKind
{
    /// <summary>所有轮次的分组标题都可折叠。</summary>
    Collapsed,

    /// <summary>只有历史轮次可折叠，当前轮次保持展开。</summary>
    History,

    /// <summary>不折叠。</summary>
    None,
}

/// <summary>
/// 把三个偏好解析成一份能力集合。
/// </summary>
/// <remarks>
/// 对应 dsh 的 <c>presentationPolicyFor</c>。取值逐个照抄
/// <c>presentation-policy.ts</c> 的 <c>POLICIES</c> 表。
/// </remarks>
public static class ChatPresentation
{
    /// <summary>默认详情模式。<b>桌面端是 <c>standard</c></b>。</summary>
    /// <remarks>
    /// README 原文（"Defaults and saving preferences"）：
    /// "When the setting is missing, <c>null</c>, or invalid, **Desktop uses
    /// <c>standard</c> and non-Desktop Web uses <c>detailed</c>**."
    /// <para>
    /// 我第一版取 <c>detailed</c>，理由写的是 "dsh 的 DEFAULT_TRANSCRIPT_VIEW_MODE
    /// = detailed"——那个常量确实存在，但它是 **Web 侧**的默认。本产品是桌面端，
    /// 取错了会让新用户的默认呈现整体偏"更展开"一档。
    /// </para>
    /// <para>
    /// 用 <c>static readonly</c> 而不是 <c>const</c>：<c>const</c> 是编译期常量，
    /// 调用方写 `if (DefaultMode != X)` 时编译器能把它当常量折叠，
    /// 于是断言句后面的代码被判"不可达"（CS0162）。检查里正好是这种写法。
    /// </para>
    /// </remarks>
    public static readonly TranscriptViewMode DefaultMode = TranscriptViewMode.Standard;

    /// <summary>默认折叠时机。</summary>
    public const CollapseTiming DefaultTiming = CollapseTiming.Completion;

    /// <summary>默认折叠回复范围。</summary>
    public const FoldedReplies DefaultReplies = FoldedReplies.All;

    /// <summary>
    /// 解析出能力集合。
    /// </summary>
    /// <param name="mode">详情模式。</param>
    /// <param name="timing">折叠时机。</param>
    /// <param name="replies">折叠时保留的回复范围。</param>
    public static ChatPresentationPolicy Resolve(
        TranscriptViewMode mode,
        CollapseTiming timing = DefaultTiming,
        FoldedReplies replies = DefaultReplies)
    {
        // 四个模式的固有开关（`presentation-policy.ts` 的 POLICIES 表）
        var (fold, grouping, liveDetail, reasoningPreview) = mode switch
        {
            TranscriptViewMode.Compact => (true, StepGroupingKind.Collapsed, false, false),
            TranscriptViewMode.Standard => (true, StepGroupingKind.Collapsed, true, true),
            TranscriptViewMode.Detailed => (true, StepGroupingKind.History, true, true),

            // verbose 的 liveProcessDetail 是 false，不是 true——
            // 它把过程全摊开，因而不需要"标题里显示实时细节"。
            TranscriptViewMode.Verbose => (false, StepGroupingKind.None, false, true),

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的详情模式"),
        };

        return new ChatPresentationPolicy(mode, timing, replies, fold, grouping, liveDetail, reasoningPreview);
    }

    /// <summary>
    /// 把持久化的字符串解析成模式，兼容两个历史值。
    /// </summary>
    /// <remarks>
    /// dsh 有两个遗留值（<c>chat-settings.ts</c>）：
    /// <list type="bullet">
    /// <item><c>normal</c> — 两模式世代的旧值，读作 <c>detailed</c></item>
    /// <item><c>expanded</c> — 读作 <c>detailed</c></item>
    /// </list>
    /// 两者都**不出现在可选项里、也不会被写回**。认不出的一律用默认，
    /// 不猜——猜错会让用户的设置莫名其妙变一档。
    /// </remarks>
    public static TranscriptViewMode ParseMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "compact" => TranscriptViewMode.Compact,
        "standard" => TranscriptViewMode.Standard,
        "detailed" => TranscriptViewMode.Detailed,
        "verbose" => TranscriptViewMode.Verbose,
        "normal" => TranscriptViewMode.Detailed,
        "expanded" => TranscriptViewMode.Detailed,
        _ => DefaultMode,
    };

    /// <summary>模式的持久化键名。</summary>
    public static string ToKey(TranscriptViewMode mode) => mode switch
    {
        TranscriptViewMode.Compact => "compact",
        TranscriptViewMode.Standard => "standard",
        TranscriptViewMode.Detailed => "detailed",
        TranscriptViewMode.Verbose => "verbose",
        _ => "detailed",
    };

    /// <summary>解析折叠时机。</summary>
    public static CollapseTiming ParseTiming(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "next-input" => CollapseTiming.NextInput,
        "completion" => CollapseTiming.Completion,
        _ => DefaultTiming,
    };

    /// <summary>解析折叠回复范围。</summary>
    public static FoldedReplies ParseReplies(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "final" => FoldedReplies.Final,
        "all" => FoldedReplies.All,
        _ => DefaultReplies,
    };
}
