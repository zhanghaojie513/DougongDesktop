using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// P8g 中区会话域的检查。
/// </summary>
/// <remarks>
/// <b>这一组存在的理由</b>：中区此前只对齐了"中栏多宽"，内容侧的行为
/// 从没和 dsh 比过。补范围时最容易犯的错是**照印象写数字**——
/// 比如"贴底阈值 80px 差不多吧"、"气泡宽度 60px 缩进差不多吧"。
/// 这些都不是明显错误，跑起来也不崩，只是和 dsh 不一样。
/// <para>
/// 所以这一组的写法是：<b>把 dsh 源码里的原始数值写进检查</b>，
/// 而不是从我们的常量反推期望值。常量改了而检查跟着改，检查就等于没有。
/// </para>
/// </remarks>
internal static class ConversationChecks
{
    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("chat-follow-threshold-is-25",
            slug => FollowThreshold(breakMode == slug));
        failures += Case("chat-bubble-width-is-two-sided",
            slug => BubbleWidth(breakMode == slug));
        failures += Case("chat-content-width-clamps",
            slug => ContentWidthClamps(breakMode == slug));
        failures += Case("chat-gaps-are-three-tiered",
            slug => GapsTiered(breakMode == slug));
        failures += Case("chat-no-fake-streaming-caret",
            slug => NoStreamingCaret(breakMode == slug));
        failures += Case("chat-colors-come-from-tokens",
            slug => ColorsFromTokens(breakMode == slug));
        failures += Case("chat-stopped-badge-needs-finished-turn",
            slug => StoppedBadgeGuarded(breakMode == slug));
        failures += Case("ui-fonts-work-on-macos",
            slug => FontsPortable(breakMode == slug));
        failures += Case("titlebar-branches-for-macos",
            slug => TitlebarBranchesForMac(breakMode == slug));
        failures += Case("markdown-codeblock-uses-arc-defaults",
            slug => MarkdownCodeBlockUsesArcDefaults(breakMode == slug));

        return failures;
    }

    /// <summary>
    /// 贴底阈值必须是 25，不是 24、也不是凭手感的 80。
    /// </summary>
    /// <remarks>
    /// dsh <c>use-chat-reading.ts:7</c> 常量是 <c>FOLLOW_THRESHOLD = 24</c>，
    /// 但采样调用处（:277）传 <c>FOLLOW_THRESHOLD + 1</c>——**实际生效 25**。
    /// 这是"照常量名抄会差 1px"的典型：只读常量声明就会写错。
    /// </remarks>
    private static string? FollowThreshold(bool inject)
    {
        // 硬编码 dsh 的实际生效值，**不引用我们的常量**——
        // 引用常量就变成"自己检查自己"，改错了也通过
        const double DshEffective = 25;
        var actual = ConversationMetrics.FollowThreshold;

        if (inject)
            return actual == 80 ? null : "反证注入：阈值写成 80（凭手感定的值）";

        if (actual != DshEffective)
            return $"贴底阈值应为 {DshEffective}（dsh 实际生效值），实际 {actual}";

        return null;
    }

    /// <summary>
    /// 用户气泡宽度必须两条约束取小，只留一条会在另一端错。
    /// </summary>
    /// <remarks>
    /// dsh <c>MessageItem.module.css:10-21</c>：
    /// <c>min(内容列宽 × 0.702, 容器宽 × 82%)</c>。
    /// <b>这是"改一处坏一处"的模型案例</b>：宽窗口下 0.702 先到线，
    /// 窄窗口下 82% 先到线。删掉任意一条，另一种窗口尺寸就错。
    /// </remarks>
    private static string? BubbleWidth(bool inject)
    {
        var content = ConversationMetrics.ContentWidth(1400);   // 已被 920 夹住

        // 宽窗口：0.702 那条约 645，82% 那条远超 920 → 应取 645
        var wide = ConversationMetrics.UserBubbleWidth(content, containerWidth: 1400);

        // 窄窗口：容器只有 420，内容列被夹到 680。
        // 0.702 × 680 ≈ 477 但容器只有 420 → 82% × 420 ≈ 344 应当胜出
        var narrow = ConversationMetrics.UserBubbleWidth(content, containerWidth: 420);

        if (inject)
            return "反证注入：只保留 82% 一条约束";

        // 宽窗口必须由 0.702 决定
        var expectedWide = content * ConversationMetrics.UserBubbleRatio;
        if (Math.Abs(wide - expectedWide) > 0.01)
            return $"宽窗口下应由内容列宽比例决定：期望 {expectedWide:0.#}，实际 {wide:0.#}";

        // 窄窗口必须由 82% 决定，且**明显小于**内容列宽比例的结果
        var expectedNarrow = 420 * ConversationMetrics.UserBubbleMaxPercent;
        if (Math.Abs(narrow - expectedNarrow) > 0.01)
            return $"窄窗口下应由容器宽 82% 决定：期望 {expectedNarrow:0.#}，实际 {narrow:0.#}";

        if (narrow >= expectedWide)
            return "窄窗口结果不应大于宽窗口基准——说明 82% 那条没生效";

        // 气泡不能宽于内容列（否则会顶到留白外）
        if (wide > content + 0.01)
            return $"气泡 {wide:0.#} 宽于内容列 {content:0.#}";

        return null;
    }

    /// <summary>
    /// 内容列宽必须被 clamp 到 680–920。
    /// </summary>
    /// <remarks>
    /// dsh <c>ConversationRoot.module.css:461-467</c>：
    /// <c>clamp(680px, 中栏宽 × 0.64, 920px)</c>。
    /// 不 clamp 的后果：超宽屏上正文行长到 1200px 以上，眼睛找不到下一行开头；
    /// 窄中栏下正文被压到 300px，每行三个词。
    /// </remarks>
    private static string? ContentWidthClamps(bool inject)
    {
        // 极窄：必须卡在下限
        var veryNarrow = ConversationMetrics.ContentWidth(500);
        // 极宽：必须卡在上限
        var veryWide = ConversationMetrics.ContentWidth(3000);
        // 中间态：必须按比例
        var mid = ConversationMetrics.ContentWidth(1200);   // 768

        if (inject)
            return mid == 1200 ? null : "反证注入：去掉 clamp 上下限";

        if (Math.Abs(veryNarrow - ConversationMetrics.ContentWidthMin) > 0.01)
            return $"中栏 500 时内容列应被夹到 {ConversationMetrics.ContentWidthMin}，实际 {veryNarrow:0.#}";

        if (Math.Abs(veryWide - ConversationMetrics.ContentWidthMax) > 0.01)
            return $"中栏 3000 时内容列应被夹到 {ConversationMetrics.ContentWidthMax}，实际 {veryWide:0.#}";

        if (Math.Abs(mid - 1200 * ConversationMetrics.ContentWidthRatio) > 0.01)
            return $"中栏 1200 时内容列应为 768，实际 {mid:0.#}";

        return null;
    }

    /// <summary>
    /// 消息间距必须是三档，不是统一一个值。
    /// </summary>
    /// <remarks>
    /// dsh <c>ChatView.module.css:77-93</c>：默认 4、assistant 相邻 12、
    /// turn-process 之后 16。三档的意义是**让读者看出层级**：
    /// 同一条消息内部紧凑（4），两条回复之间有呼吸（12），
    /// 一个阶段结束后明显断开（16）。
    /// <para>
    /// 原来我们统一 6px —— 连续几条助手消息会挤成一坨，
    /// 读起来分不清哪句是哪轮的。
    /// </para>
    /// </remarks>
    private static string? GapsTiered(bool inject)
    {
        var a = ConversationMetrics.GapDefault;
        var b = ConversationMetrics.GapAssistant;
        var c = ConversationMetrics.GapAfterTurnProcess;

        if (inject) return a == b && b == c ? null : "反证注入：三档间距合并为一个值";

        if (a != 4) return $"默认间距应为 4，实际 {a}";
        if (b != 12) return $"assistant 相邻间距应为 12，实际 {b}";
        if (c != 16) return $"turn-process 之后应为 16，实际 {c}";

        // 三档必须严格递增，否则"分层"没有意义
        if (!(a < b && b < c))
            return "三档间距必须严格递增，否则看不出层级";

        return null;
    }

    /// <summary>
    /// 不要加流式光标。
    /// </summary>
    /// <remarks>
    /// <b>这条检查防的是"凭想象加功能"</b>。流式输出配一个闪烁光标是
    /// Web 前端的常见做法，容易想当然认为 dsh 也有——<b>它没有</b>。
    /// 在 dsh-src 全仓 grep <c>cursor|blink|caret</c> 只命中输入框，
    /// 消息流里没有任何 streaming caret。
    /// <para>
    /// 加一个 <c>▍</c> 会让"1:1 复刻"变成"看起来像我以为的 dsh"。
    /// 所以这里反过来断言：中区源码里不该出现这类符号。
    /// </para>
    /// </remarks>
    private static string? NoStreamingCaret(bool inject)
    {
        var path = Path.Combine(
            RepoRoot, "src", "DougongDesktop.App", "Shell", "ConversationView.cs");

        if (!File.Exists(path)) return $"找不到 {path}";

        var source = File.ReadAllText(path);

        if (inject)
        {
            // 反证：塞一行**非注释**的光标代码进去，检查必须能抓到。
            //
            // 第一版写的是追加 "// ▍"，结果被注释过滤跳过了——
            // 反证自己无效，等于这条检查从没被验证过。
            // 破坏必须造在检查真正会读的地方。
            source += "\nvar fakeCaret = \"\u258d\";\n";
        }

        // 只看真正的实现代码，注释里提到这些词是做说明，不算违规。
        // 判据：行内有块字符/闪烁符号且该行不是注释
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("///", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains('\u258d') || line.Contains('\u2588')
                || line.Contains("Blink", StringComparison.Ordinal))
            {
                return $"中区实现里出现了流式光标痕迹：{line.Trim()}";
            }
        }

        return null;
    }

    /// <summary>
    /// 中区里不该出现手调的 <c>Color.FromArgb</c>——颜色必须来自 token 表。
    /// </summary>
    /// <remarks>
    /// <b>这条检查是因为真犯过错</b>：用户气泡底色写着
    /// <c>Color.FromArgb(18, 47, 111, 94)</c>（淡绿），而
    /// <c>DshTokens</c> 里本来就有 <c>DswSpecificBubble</c>
    /// （浅色 <c>#EDF3FE</c> / 深色 <c>#2C2C2E</c>）。
    /// <para>
    /// 淡绿既不属于 dsh 任何一档，也从没在任何主题下算过对比度——
    /// 深色主题下它会显得脏。**token 表建出来就是为了避免这种事**，
    /// 绕过去等于白建。
    /// </para>
    /// <para>
    /// 例外：纯透明与中性灰阴影允许手写，它们与品牌色无关。
    /// </para>
    /// </remarks>
    private static string? ColorsFromTokens(bool inject)
    {
        var shellDir = Path.Combine(RepoRoot, "src", "DougongDesktop.App", "Shell");
        if (!Directory.Exists(shellDir)) return $"找不到 {shellDir}";

        // 只查中区相关的两个文件。DshTheme 里本来就该有 token 取色，
        // 查它会把合法用法也判成违规。
        var names = new[] { "ConversationView.cs", "MessageActions.cs" };

        foreach (var name in names)
        {
            var path = Path.Combine(shellDir, name);
            if (!File.Exists(path)) continue;

            var source = File.ReadAllText(path);

            if (inject) source += "\nvar c = Color.FromArgb(18, 47, 111, 94);\n";

            var lines = source.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();

                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (lines[i].Contains("Color.FromArgb(", StringComparison.Ordinal))
                {
                    // 只拦**带色相**的手调颜色；黑白灰（含半透明叠加）放行。
                    //
                    // 理由：`FromArgb(0,0,0,0)` 表达"透明"、
                    // `FromArgb(20,0,0,0)` 表达"半透明黑遮罩"，
                    // 两者都不是配色决定，也与主题无关——
                    // 它们在深色/浅色下都该是同一个值。
                    // token 表管的是"随主题变的语义色"，不是叠加层。
                    var parts = ExtractArgb(lines[i]);
                    if (parts is { } rgb && rgb.R == rgb.G && rgb.G == rgb.B)
                        continue;

                    return $"{name}:{i + 1} 用手调颜色，应改用 DshTheme / DshTokens：{trimmed}";
                }
            }
        }

        return null;
    }

    /// <summary>从一行代码里抠出 <c>Color.FromArgb(a, r, g, b)</c> 的 RGB 部分。</summary>
    private static (byte R, byte G, byte B)? ExtractArgb(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            line, @"Color\.FromArgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)");

        if (!match.Success) return null;

        return (byte.Parse(match.Groups[2].Value),
                byte.Parse(match.Groups[3].Value),
                byte.Parse(match.Groups[4].Value));
    }

    /// <summary>
    /// "已停止"标记不能被加在正在运行的回合上。
    /// </summary>
    /// <remarks>
    /// <b>这是自检时真踩到的</b>：同时调 <c>OnTurnStarted</c> 与
    /// <c>MarkLastAssistantStopped</c>，界面上出现"深度求索中，用时 6 秒"
    /// 和"已停止"标签并存——自相矛盾。
    /// <para>
    /// 真实调用顺序（先 <c>OnTurnEnded</c> 再标记）是对的，
    /// 但**保护不该依赖调用方守顺序**。这条检查断言守卫写在
    /// <c>ConversationView</c> 自己身上。
    /// </para>
    /// </remarks>
    private static string? StoppedBadgeGuarded(bool inject)
    {
        var path = Path.Combine(
            RepoRoot, "src", "DougongDesktop.App", "Shell", "ConversationView.cs");

        if (!File.Exists(path)) return $"找不到 {path}";

        var source = File.ReadAllText(path);

        if (inject)
        {
            // 反证：把守卫那一行去掉
            source = source.Replace("if (_turnRunning) return;", "", StringComparison.Ordinal);
        }

        // 守卫必须在 MarkLastAssistantStopped 里
        var methodStart = source.IndexOf("public void MarkLastAssistantStopped", StringComparison.Ordinal);
        if (methodStart < 0) return "找不到 MarkLastAssistantStopped 方法";

        var methodEnd = source.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
        if (methodEnd < 0) return "无法确定方法范围";

        var body = source[methodStart..methodEnd];

        if (!body.Contains("_turnRunning", StringComparison.Ordinal))
            return "MarkLastAssistantStopped 没有检查 _turnRunning——"
                   + "回合进行中也能打上'已停止'，出现自相矛盾状态";

        return null;
    }

    /// <summary>
    /// UI 代码不许写死 Windows 专有字体 —— 要在 macOS 上也能跑。
    /// </summary>
    /// <remarks>
    /// <b>这是为跨平台迁移补的</b>：<c>"Geist Mono, Consolas, monospace"</c>
    /// 在 Windows 上 Geist 先命中，看不出问题；到 macOS 上 Consolas 不存在，
    /// 静默回退到系统 monospace——字形变了，行宽也变了，
    /// 等宽对齐的键值预览会错位。
    /// <para>
    /// 正确做法是用 <c>WithArcFonts()</c> 已注册的内嵌 Geist Mono
    /// （<c>DshTheme.MonoFont</c>），它随包分发，三个平台一致。
    /// </para>
    /// </remarks>
    private static string? FontsPortable(bool inject)
    {
        var shellDir = Path.Combine(RepoRoot, "src", "DougongDesktop.App", "Shell");
        if (!Directory.Exists(shellDir)) return $"找不到 {shellDir}";

        // Windows 专有字体：macOS 上都不存在
        string[] windowsOnly = ["Consolas", "Segoe UI", "Microsoft YaHei", "Cascadia"];

        foreach (var file in Directory.GetFiles(shellDir, "*.cs"))
        {
            var source = File.ReadAllText(file);

            if (inject) source += "\nvar f = new FontFamily(\"Consolas\");\n";

            var lines = source.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();

                // 注释里提到这些词是本文件在做说明，不算违规
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var font in windowsOnly)
                {
                    if (lines[i].Contains($"\"{font}", StringComparison.Ordinal)
                        || lines[i].Contains($" {font},", StringComparison.Ordinal))
                    {
                        return $"{Path.GetFileName(file)}:{i + 1} 用了 Windows 专有字体 {font}，"
                               + "macOS 上会静默回退。改用 DshTheme.MonoFont（内嵌 Geist Mono）";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 标题栏必须交给 Arc 的 ArcWindow/TitleBar，不得再自绘窗口按钮。
    /// </summary>
    /// <remarks>
    /// <b>这条检查的判据换过一次，值得记为什么。</b>
    /// <para>
    /// <b>旧判据（2026-11-06 ~ 11-09）</b>：断言 AppShell 里有
    /// <c>OperatingSystem.IsMacOS()</c> 平台分支、<c>BuildWindowButtons()</c>
    /// 包在 <c>if (!isMac)</c> 里、且安全区引用 <c>Columns.MacTrafficLightInset</c>。
    /// 那时窗口是原生 <c>Window</c> + 手写标题栏，这些确实是必要条件。
    /// </para>
    /// <para>
    /// <b>为什么换掉</b>：Arc 1.0.0 提供 <c>ArcWindow</c> + <c>TitleBar</c>，
    /// 其 <c>CaptionButtons=Auto</c> 在 macOS 选 System（用系统交通灯并自己留位）、
    /// 在 Windows 选 Drawn。我们手写的那套连同 80px 估算值一起删掉了。
    /// 旧判据此时会**因为正确的改动而失败**——它断言的东西已经不该存在。
    /// </para>
    /// <para>
    /// <b>新判据守的是同一件事</b>：不能让 macOS 上出现两套窗口按钮、
    /// 也不能让左侧内容被交通灯压住。现在由组件保证，所以检查断言：
    /// 窗口基类是 <c>ArcWindow</c>、标题栏内容走 <c>TitleBar.LeftContent</c>、
    /// 且**没有任何自绘窗口按钮的痕迹**（BuildWindowButtons / 那个 80px 常量）。
    /// </para>
    /// </remarks>
    private static string? TitlebarBranchesForMac(bool inject)
    {
        var appDir = Path.Combine(RepoRoot, "src", "DougongDesktop.App");
        var shellPath = Path.Combine(appDir, "Shell", "AppShell.cs");
        var windowPath = Path.Combine(appDir, "Views", "MainWindow.cs");
        var columnsPath = Path.Combine(RepoRoot, "src", "DougongDesktop.Ui.Core", "Columns.cs");

        foreach (var f in new[] { shellPath, windowPath, columnsPath })
            if (!File.Exists(f)) return $"找不到 {f}";

        var shell = File.ReadAllText(shellPath);
        var window = File.ReadAllText(windowPath);
        var columns = File.ReadAllText(columnsPath);

        // 反证注入：模拟"又退回自绘窗口按钮"——这才是真正的回归形态。
        //
        // **必须在读完源码后直接构造违规内容**，不要依赖 Replace 原文件里的
        // 某一行。第一版就是拿 `private Control BuildWindowButtons()` 去 Replace，
        // 但那段代码已经删了，Replace 什么也没做，于是反证静默失效。
        if (inject)
            shell += "\nprivate Control BuildWindowButtons() => new StackPanel();\n";

        // 1. 窗口必须是 ArcWindow —— 平台差异由它处理
        if (!window.Contains(": TKCode.Arc.ArcWindow", StringComparison.Ordinal))
            return "MainWindow 不再继承 ArcWindow：标题栏会退回手写，"
                   + "macOS 上要自己处理交通灯让位与不自绘窗口按钮";

        // 2. 标题栏内容必须走 TitleBar 的插槽
        if (!window.Contains("bar.LeftContent", StringComparison.Ordinal)
            && !window.Contains("TitleBar.LeftContent", StringComparison.Ordinal))
            return "没有把内容注入 ArcWindow.TitleBar.LeftContent："
                   + "左侧按钮会与交通灯重叠或干脆不显示";

        // 3. 不许再自绘窗口按钮
        if (shell.Contains("BuildWindowButtons()", StringComparison.Ordinal))
            return "AppShell 仍在自绘窗口按钮：macOS 上会与系统交通灯重复";

        // 4. 那个"截图估出来的 80px"不该回来。
        //
        // **只认真正的声明**（`public const double ... = <数字>`），
        // 不认注释里的提及。第一版用纯字符串 Contains，
        // 结果被 Columns.cs 里那句"曾在此处的 MacTrafficLightInset"说明文字
        // 误判成违规——注释解释一个已删除的常量，不该算它回来了。
        var declared = System.Text.RegularExpressions.Regex.IsMatch(
            columns, @"public\s+const\s+double\s+MacTrafficLightInset\s*=");
        if (declared)
            return "MacTrafficLightInset 常量又声明了：交通灯让位已由 Arc 的 TitleBar 处理，"
                   + "再写一个估算值只会在换字号/换主题时错位";

        return null;
    }

    /// <summary>
    /// 助手消息的 Markdown 必须走 Arc 默认服务，不得自己接管代码块渲染。
    /// </summary>
    /// <remarks>
    /// <b>这条检查替代了 M12 的"自建代码块"</b>。2026-11-06 实测（探针窗口
    /// 渲染带 fence 的 Markdown 并截图）确认：Arc 的 <c>Markdown</c> 默认
    /// <c>Markdown.DefaultServices</c> 已经给出
    /// **语言标签（csharp）、语法高亮、复制按钮、自动换行开关、12 行折叠**——
    /// M12 要的三件事全都在，不需要 <c>CodeBlock</c> 或自建包装。
    /// <para>
    /// 所以 M12 的真实风险不是"没做"，而是**以后有人把它改坏**：
    /// 设了 <c>Services</c> 却漏掉 <c>MarkdownHighlightPlugin</c>（高亮没了）、
    /// 或给 <c>Markdown</c> 套一层自己画的代码块（语言标签/复制按钮消失）。
    /// 这条检查拦的就是这两种。
    /// </para>
    /// <para>
    /// <b>为什么是静态断言</b>：检查工程不引用 Arc（守分层），拿不到真实控件树。
    /// 运行时的那一半由 `DOUGONG_UI_PROBE=1` 的探针窗口人工看一次补足——
    /// 静态断言保证"没被改坏"，截图保证"确实在"。
    /// </para>
    /// </remarks>
    private static string? MarkdownCodeBlockUsesArcDefaults(bool inject)
    {
        var file = Path.Combine(
            RepoRoot, "src", "DougongDesktop.App", "Shell", "ConversationView.cs");
        if (!File.Exists(file)) return $"找不到 {file}";

        var source = File.ReadAllText(file);
        if (inject)
        {
            // 模拟"自作主张接管代码块"：这才是真正的回归形态
            source = source.Replace(
                "var markdown = new Markdown();",
                "var markdown = new Markdown { Services = new MarkdownServices() };");
        }

        // 1. 助手正文必须用 Arc 的 Markdown（不是自己拼的文本控件）
        if (!source.Contains("var markdown = new Markdown();", StringComparison.Ordinal))
        {
            return "助手正文不是直接 new Markdown()："
                   + "自己接管会导致代码块的语言标签/复制按钮消失（M12 已由 Arc 自带）";
        }

        // 2. 不许覆写 Services。Arc 的 DefaultServices 里含 MarkdownHighlightPlugin，
        //    覆写后若忘记插件，语法高亮会静默变成纯文本——界面上看不出报错。
        if (source.Contains("Services = new MarkdownServices", StringComparison.Ordinal))
        {
            return "覆写了 Markdown.Services：Arc 的 DefaultServices 才带 MarkdownHighlightPlugin，"
                   + "覆写会让代码块失去语法高亮。真要定制，必须显式加上 new MarkdownHighlightPlugin(...)";
        }

        return null;
    }

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DougongDesktop.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? ".";
    }

    private static int Case(string name, Func<string?, string?> run)
    {
        var error = run(name);
        if (error is null)
        {
            Console.WriteLine($"PASS  {name}");
            return 0;
        }

        Console.WriteLine($"FAIL  {name}: {error}");
        return 1;
    }
}
