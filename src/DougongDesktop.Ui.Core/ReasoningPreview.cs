namespace DougongDesktop.Ui.Core;

/// <summary>
/// 推理行的折叠预览规则（dsh 的 <c>ReasoningRow</c>）。
/// </summary>
/// <remarks>
/// <b>这是折叠的第三层</b>：整轮 → 过程组 → 单条推理。三层是包含关系，
/// 外层关着时内层开不开都看不见。
/// <para>
/// 规则出处 <c>conversation-nodes/README.md:195</c>：
/// "Only reasoning blocks with non-whitespace text produce individual rows;
/// each retains its original text and expands independently. A blank streaming
/// tail produces no row... Individual reasoning starts collapsed, including
/// while streaming. All modes preview the latest paragraph whose first line
/// ends with a newline; an unfinished single line has no preview."
/// </para>
/// <para>
/// <b>运行中与已完成的预览来源不同</b>，这点最容易写错：
/// <list type="bullet">
/// <item>运行中 → <i>最新一段</i>的首行（读者要看到"现在在想什么"）</item>
/// <item>已完成 → 全文首行（内容定型了，从头开始读）</item>
/// </list>
/// 两者都不能在有 <c>**</c> 标记时原样输出——README 明写 "removes <c>**</c> markers"。
/// </para>
/// </remarks>
public static class ReasoningPreview
{
    /// <summary>
    /// 该推理块是否应该产生一行。
    /// </summary>
    /// <remarks>
    /// <b>纯空白不产生行</b>：dsh 的 "Only reasoning blocks with non-whitespace
    /// text produce individual rows"。流式过程中会先推一个空块，
    /// 若为它建行，用户会看到一行空气——而且它还会把"运行中"的状态
    /// 转移到一个更早的块上（README 明确禁止那条）。
    /// </remarks>
    public static bool ProducesRow(string? text) =>
        !string.IsNullOrWhiteSpace(text);

    /// <summary>已完成时的预览文本：全文首行。</summary>
    public static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var idx = text.IndexOf('\n', StringComparison.Ordinal);
        var line = idx < 0 ? text : text[..idx];
        return StripMarkers(line.Trim());
    }

    /// <summary>
    /// 运行中的预览文本：最新一段里"首行已完整"的那一段。
    /// </summary>
    /// <remarks>
    /// 照 <c>latestCompletedParagraphFirstLine</c> 的语义：
    /// 按空行切段，每段只要自身含有换行（即首行已完整）就取它的首行；
    /// 一直取到最后一个满足条件的段。**最后那个不完整的单行不算**——
    /// 它还在写，首行没完，显示它会让预览不停跳动。
    /// <para>
    /// 没有任何完整段落时返回空（也就是没有预览）。
    /// </para>
    /// </remarks>
    public static string LatestCompletedParagraphFirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var summary = string.Empty;
        var paragraphStart = 0;

        while (paragraphStart <= text.Length)
        {
            // 找段尾：连续空行
            var paragraphEnd = FindParagraphEnd(text, paragraphStart);

            // 段内是否有换行（首行已完整）
            var newline = text.IndexOf('\n', paragraphStart, StringComparison.Ordinal);
            if (newline >= 0 && newline <= paragraphEnd)
            {
                var candidate = StripMarkers(text[paragraphStart..newline].Trim());
                if (candidate.Length > 0) summary = candidate;
            }

            if (paragraphEnd >= text.Length) break;
            paragraphStart = SkipBlankLines(text, paragraphEnd);
        }

        return summary;
    }

    /// <summary>
    /// 按模式决定显示预览还是全文。
    /// </summary>
    /// <param name="policy">当前工作详情策略。</param>
    /// <param name="text">推理原文。</param>
    /// <param name="running">该块是否还在流式输出。</param>
    /// <param name="expanded">读者是否手动展开。</param>
    /// <remarks>
    /// 模式表（README 第 124 行附近）：
    /// <c>SettledReasoningPreview</c> 为假的档（compact）在**已结算**后不显示预览。
    /// 运行中的预览在 compact 下仍然显示——那一条是「运行中的组不折」的配套，
    /// 与 here 的 settled 判断是两回事。
    /// </remarks>
    public static string? PreviewFor(
        ChatPresentationPolicy policy, string? text, bool running, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (expanded) return null;
        if (!policy.SettledReasoningPreview && !running) return null;

        var preview = running
            ? LatestCompletedParagraphFirstLine(text)
            : FirstLine(text);

        return preview.Length == 0 ? null : preview;
    }

    /// <summary>去掉 Markdown 的 <c>**</c> 标记（dsh 预显示原文时这么做）。</summary>
    private static string StripMarkers(string s) => s.Replace("**", string.Empty, StringComparison.Ordinal);

    /// <summary>某段正文的结束位置（不含其后的空行）。</summary>
    /// <remarks>
    /// <b>段尾 = 一个空行的起点</b>：即某个 <c>\n</c> 之后直到下一个 <c>\n</c>
    /// 之间只有空白。
    /// <para>
    /// 我第一版写成"这个 <c>\n</c> 之后<b>整篇</b>都只剩空白"，于是第一个换行
    /// 就被判成段尾，整篇被当成一段，取到头段。检查立刻抓到：
    /// 「一\n正文\n\n二\n正文\n\n三还在写」应取第二段，实际取到第一段。
    /// </para>
    /// </remarks>
    private static int FindParagraphEnd(string text, int start)
    {
        for (var i = start; i < text.Length - 1; i++)
        {
            if (text[i] != '\n') continue;

            // 看 i+1 这一行是否为空行：往后扫到下一个 \n 或结尾，中间是否全空白
            var lineBlank = true;
            for (var j = i + 1; j < text.Length && text[j] != '\n'; j++)
            {
                if (text[j] is '\r' or ' ' or '\t') continue;
                lineBlank = false;
                break;
            }

            if (lineBlank) return i;
        }

        return text.Length;
    }

    /// <summary>跳过空行，返回下一段正文的起点。</summary>
    private static int SkipBlankLines(string text, int i)
    {
        var j = i;
        while (j < text.Length && text[j] is '\n' or '\r' or ' ' or '\t') j++;
        return j;
    }
}
