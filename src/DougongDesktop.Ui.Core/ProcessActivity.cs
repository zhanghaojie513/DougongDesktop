using DougongDesktop.Ipc;

namespace DougongDesktop.Ui.Core;

/// <summary>
/// 过程活动的工具类别（dsh <c>process-activity.ts</c> 的 <c>ToolCategory</c>）。
/// </summary>
/// <remarks>
/// <b>顺序即排名前的首次出现序</b>：dsh 的计数并列时按"首次出现"排，
/// 不是按这里声明的顺序。枚举本身不参与排序，只用来做键。
/// </remarks>
public enum ToolCategory
{
    /// <summary>没有类别（纯推理 / 无工具）。</summary>
    None,

    Read,
    ReadImage,
    Search,
    Write,
    Edit,
    Commands,
    Code,
    WebSearch,
    WebFetch,
    Subagents,
    Plan,
    Questions,
    Tools,

    /// <summary>全部成员都是上下文注入（dsh 的 <c>context</c>）。本轮不区分。</summary>
    Context,
}

/// <summary>
/// 按工具名分类 + 计数 + 合成组标题。
/// </summary>
/// <remarks>
/// <b>照抄 dsh 的规则表，不自己归纳</b>（<c>conversation-nodes/README.md</c>
/// "Tool-name classification" + "Activity summaries"，0.2.2-rc.2）：
/// <list type="bullet">
/// <item>分类只用记录下来的工具名**原样**匹配：不折叠大小写、不剥命名空间、
/// 不查 provider、不根据参数猜。</item>
/// <item>计数统计的是"这个组里有几次调用"，不是"成功了几个"——
/// 失败/取消照样计一次。</item>
/// <item>关组标题**不带数字**，只按排名列出类别名。</item>
/// </list>
/// <para>
/// <b>为什么这些细节不能简化</b>：标题是用户判断"刚才干了什么"的唯一摘要。
/// 把 <c>read → bash → read</c> 说成「使用了 3 个工具」丢掉的是"读了两次、
/// 跑了一次命令"这个结构；而 dsh 的排名规则恰好保留它（read=2, commands=1）。
/// </para>
/// </remarks>
public static class ProcessActivity
{
    /// <summary>
    /// 按 dsh 的名字表给一个工具名归类。
    /// </summary>
    /// <remarks>
    /// 顺序**有意义**：<c>_inspect</c> 后缀检查在 <c>subagent_</c> 前缀之前，
    /// 所以 <c>subagent_inspect</c> 是 <c>Search</c> 而不是 <c>Subagents</c>；
    /// <c>terminal_inspect</c> 同理是 <c>Search</c> 而不是 <c>Commands</c>。
    /// 调换顺序会静默改变这些结果。
    /// </remarks>
    public static ToolCategory Classify(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return ToolCategory.Tools;

        return toolName switch
        {
            "read" => ToolCategory.Read,
            "read_image" => ToolCategory.ReadImage,
            "write" => ToolCategory.Write,
            "edit" or "apply_patch" => ToolCategory.Edit,
            "bash" or "pwsh" or "exec_command" or "write_stdin" => ToolCategory.Commands,
            "run_code" or "cua_run_code" => ToolCategory.Code,
            "web_search" => ToolCategory.WebSearch,
            "web_fetch" => ToolCategory.WebFetch,
            "todo_write" or "create_goal" or "update_goal" or "get_goal" => ToolCategory.Plan,
            "ask_user_question" or "request_user_input" => ToolCategory.Questions,

            // 前缀 / 后缀规则。顺序照 dsh：_inspect 先于 subagent_，terminal_ 最后。
            _ when toolName.EndsWith("_inspect", StringComparison.Ordinal) => ToolCategory.Search,
            _ when toolName is "grep" or "glob" => ToolCategory.Search,
            _ when toolName.StartsWith("subagent_", StringComparison.Ordinal)
                || toolName == "subagent" => ToolCategory.Subagents,
            _ when toolName.StartsWith("terminal_", StringComparison.Ordinal) => ToolCategory.Commands,

            _ => ToolCategory.Tools,
        };
    }

    /// <summary>
    /// 统计一个组的类别计数，按 dsh 的排名规则排序。
    /// </summary>
    /// <remarks>
    /// <b>排名规则</b>：计数降序；并列时按该类别在成员顺序里**首次出现**的先后，
    /// 不按枚举值、也不按固定优先级。dsh 原文："No category has a fixed
    /// priority when counts tie."
    /// <para>
    /// <b>推理不计入</b>：推理条目对工具计数没有贡献，但它们仍然占一个组内位置
    /// （决定顺序）。所以这里只遍历工具条目。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(ToolCategory Category, int Count)> CountByCategory(
        IReadOnlyList<ConversationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var counts = new Dictionary<ToolCategory, int>();
        var firstSeen = new Dictionary<ToolCategory, int>();
        var order = 0;

        foreach (var item in items)
        {
            if (item is not ToolCallItem tool) continue;

            var category = Classify(tool.ToolName);
            counts[category] = counts.TryGetValue(category, out var n) ? n + 1 : 1;

            if (!firstSeen.ContainsKey(category))
                firstSeen[category] = order;

            order++;
        }

        return [.. counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => firstSeen[kv.Key])
            .Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>
    /// 组标题的类别名（关闭态）。
    /// </summary>
    /// <remarks>
    /// 中文与英文各一套，直接照抄 dsh 的 <c>locale.ts</c>。
    /// 英文要**把后续标签首字母改小写**（"Read files and ran commands"），
    /// 中文不做这一步。
    /// </remarks>
    public static string ClosedLabel(ToolCategory category, bool chinese) => (category, chinese) switch
    {
        (ToolCategory.None, true) => "已完成分析",
        (ToolCategory.None, false) => "Analysis completed",
        (ToolCategory.Read, true) => "已读取文件",
        (ToolCategory.Read, false) => "Read files",
        (ToolCategory.ReadImage, true) => "已读取图片",
        (ToolCategory.ReadImage, false) => "Read images",
        (ToolCategory.Search, true) => "已搜索代码",
        (ToolCategory.Search, false) => "Searched code",
        (ToolCategory.Write, true) => "已写入文件",
        (ToolCategory.Write, false) => "Wrote files",
        (ToolCategory.Edit, true) => "修改了文件",
        (ToolCategory.Edit, false) => "Edited files",
        (ToolCategory.Commands, true) => "执行了命令",
        (ToolCategory.Commands, false) => "Ran commands",
        (ToolCategory.Code, true) => "运行了代码",
        (ToolCategory.Code, false) => "Ran code",
        (ToolCategory.WebSearch, true) => "已搜索网页",
        (ToolCategory.WebSearch, false) => "Searched the web",
        (ToolCategory.WebFetch, true) => "已访问网页",
        (ToolCategory.WebFetch, false) => "Visited web pages",
        (ToolCategory.Subagents, true) => "已协调子智能体",
        (ToolCategory.Subagents, false) => "Coordinated subagents",
        (ToolCategory.Plan, true) => "更新了计划",
        (ToolCategory.Plan, false) => "Updated the plan",
        (ToolCategory.Questions, true) => "向用户提出了问题",
        (ToolCategory.Questions, false) => "Asked questions",
        (ToolCategory.Tools, true) => "已调用工具",
        (ToolCategory.Tools, false) => "Called tools",
        (ToolCategory.Context, true) => "已注入上下文",
        (ToolCategory.Context, false) => "Injected context",
        _ => chinese ? "已完成分析" : "Analysis completed",
    };

    /// <summary>
    /// 组标题的类别名（运行态）。
    /// </summary>
    public static string RunningLabel(ToolCategory category, bool chinese) => (category, chinese) switch
    {
        (ToolCategory.None, true) => "正在分析请求",
        (ToolCategory.None, false) => "Analyzing the request",
        (ToolCategory.Read, true) => "正在读取文件",
        (ToolCategory.Read, false) => "Reading files",
        (ToolCategory.ReadImage, true) => "正在读取图片",
        (ToolCategory.ReadImage, false) => "Reading images",
        (ToolCategory.Search, true) => "正在搜索代码",
        (ToolCategory.Search, false) => "Searching code",
        (ToolCategory.Write, true) => "正在写入文件",
        (ToolCategory.Write, false) => "Writing files",
        (ToolCategory.Edit, true) => "正在编辑文件",
        (ToolCategory.Edit, false) => "Editing files",
        (ToolCategory.Commands, true) => "正在运行命令",
        (ToolCategory.Commands, false) => "Running commands",
        (ToolCategory.Code, true) => "正在运行代码",
        (ToolCategory.Code, false) => "Running code",
        (ToolCategory.WebSearch, true) => "正在搜索网页",
        (ToolCategory.WebSearch, false) => "Searching the web",
        (ToolCategory.WebFetch, true) => "正在访问网页",
        (ToolCategory.WebFetch, false) => "Visiting web pages",
        (ToolCategory.Subagents, true) => "正在协调子智能体",
        (ToolCategory.Subagents, false) => "Coordinating subagents",
        (ToolCategory.Plan, true) => "正在更新计划",
        (ToolCategory.Plan, false) => "Updating the plan",
        (ToolCategory.Questions, true) => "等待你的操作",
        (ToolCategory.Questions, false) => "Waiting for your action",
        (ToolCategory.Tools, true) => "正在调用工具",
        (ToolCategory.Tools, false) => "Calling tools",
        _ => chinese ? "正在分析请求" : "Analyzing the request",
    };

    /// <summary>
    /// 关闭态的完整组标题（多类别合成）。
    /// </summary>
    /// <remarks>
    /// 照 README 的「Group state / Title composition」表：
    /// <list type="bullet">
    /// <item>零类别 → 已完成分析</item>
    /// <item>一个类别 → 该标签，<b>不带数量</b></item>
    /// <item>两个类别 → 用 "and"（中文「并」）连接</item>
    /// <item>三个类别 → 逗号连接全部</item>
    /// <item>三个以上 → 前三个 + "etc."（中文「等」）</item>
    /// </list>
    /// 中文在两标签都以「已」开头时**去掉第二个的「已」**。
    /// </remarks>
    public static string ClosedTitle(IReadOnlyList<(ToolCategory Category, int Count)> ranked, bool chinese)
    {
        ArgumentNullException.ThrowIfNull(ranked);

        if (ranked.Count == 0)
            return ClosedLabel(ToolCategory.None, chinese);

        var labels = new List<string>();
        foreach (var (category, _) in ranked.Take(4))
            labels.Add(ClosedLabel(category, chinese));

        var more = ranked.Count > 3;
        var shown = more ? labels.Take(3).ToList() : labels;

        // 中文只在**两个**标签时去掉第二个重复的「已」
        // （README: "Chinese removes the second leading 已 only when both labels
        // start with it"）。三个以上用逗号连接，各标签保持完整——
        // 去掉每个的「已」会得到「读取文件，执行了命令，搜索网页」这种
        // 忽有忽无的拼接，读起来像错字。
        //
        // 英文则在**所有**位置的后续标签上都做首字母小写
        // （step-process.ts:25 `continuation`，三标签那条也逐个 map）。
        // 中文没有大小写，这一步是恒等，所以统一走 continuation 即可。
        var joined = shown.Count switch
        {
            1 => shown[0],
            2 => chinese
                ? $"{shown[0]}并{StripSharedPrefix(shown[1])}"
                : $"{shown[0]} and {LowerFirst(shown[1])}",
            _ => string.Join(
                chinese ? "，" : ", ",
                [shown[0], .. shown.Skip(1).Select(LowerFirst)]),
        };

        return more ? (chinese ? $"{joined}等" : $"{joined}, etc.") : joined;
    }

    /// <summary>去掉中文标签开头重复的「已」。</summary>
    private static string StripSharedPrefix(string label) =>
        label.StartsWith("已", StringComparison.Ordinal) ? label[1..] : label;

    /// <summary>
    /// 运行态的完整组标题。
    /// </summary>
    /// <remarks>
    /// <b>运行中优先"正在跑的那个类别"，而不是计数最高的</b>
    /// （README: "Use the live category, not the highest-count category"）。
    /// 没有正在跑的工具时用「正在分析请求」，即便已完成工具的计数非零。
    /// </remarks>
    public static string RunningTitle(IReadOnlyList<ConversationItem> items, bool chinese)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 找最后一个仍在运行的工具（dsh：取 time 最大的；我们按顺序取最后一个）
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i] is ToolCallItem { Status: ToolCallStatus.Running or ToolCallStatus.AwaitingApproval } t)
                return RunningLabel(Classify(t.ToolName), chinese);

        return RunningLabel(ToolCategory.None, chinese);
    }

    private static string LowerFirst(string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
