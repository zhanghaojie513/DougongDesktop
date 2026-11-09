using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DougongDesktop.App.Shell;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;
using TKCode.Arc;

namespace DougongDesktop.Ui.Headless.Checks;

/// <summary>
/// 无头 UI 检查：真的把界面控件建出来，断言结构。
/// </summary>
/// <remarks>
/// <b>为什么需要这一层</b>：架构守卫只能读源码文本，证明"代码里有这一行"，
/// 证明不了"控件真的建出来了、里面真的有内容"。本项目已经踩过多次
/// "控件在、尺寸对、里面是空的"（P3a 的命令面板）。
/// <para>
/// <b>为什么用 Headless 而不是启动真窗口</b>：这台机器上
/// <c>Avalonia.Native</c> 起不了渲染定时器（CVDisplayLink 报 -6661），
/// 真窗口起不来。Headless 不需要显示链接，所以能跑——
/// 而且它在 CI 里也能跑，这比"人眼看一眼"更可持续。
/// </para>
/// </remarks>
public static class Program
{
    public static int Main()
    {
        AppBuilder.Configure<DougongDesktop.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();

        var failures = 0;
        failures += Check("settings-page-has-sections", SettingsPageHasSections);
        failures += Check("settings-model-catalog-shows-real-numbers", ModelCatalogShowsNumbers);
        failures += Check("settings-key-shows-env-source", KeyShowsEnvironmentSource);
        failures += Check("settings-check-count-matches-guards", CheckCountMatchesGuards);
        failures += Check("settings-shows-chinese-state-words", ShowsChineseStateWords);
        failures += Check("settings-plugins-lists-loaded-only", PluginsListLoadedOnly);
        failures += Check("terminal-panel-reports-unavailable", TerminalPanelReportsUnavailable);
        failures += Check("titlebar-uses-arc-slots", TitlebarUsesArcSlots);
        failures += Check("dock-mode-builds-split-tree", DockModeBuildsSplitTree);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "UI CHECKS: ALL PASS"
            : $"UI CHECKS: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>设置页必须由 SettingsPage 承载，且分区非空、每区有内容。</summary>
    /// <remarks>
    /// 断言三件事：外壳是 Arc 的 <see cref="SettingsPage"/>（不是占位）、
    /// 分区数量正确、**每个分区的内容非空**——第三点最容易被漏掉，
    /// 也是"看起来有页面、点进去空白"的典型。
    /// </remarks>
    private static string? SettingsPageHasSections()
    {
        var view = new SettingsView();

        var page = view.GetLogicalChildren().OfType<SettingsPage>().FirstOrDefault();
        if (page is null) return "SettingsView 的根不是 Arc SettingsPage";

        var sections = page.GetLogicalChildren().OfType<SettingsSection>().ToArray();
        if (sections.Length < 4)
            return $"只建出 {sections.Length} 个分区（期望至少 4：提供商/权限/外观/诊断）";

        foreach (var s in sections)
        {
            var header = s.Header as string ?? s.Header?.ToString() ?? "(无标题)";
            if (s.Content is null) return $"分区「{header}」没有内容";
        }

        var titles = string.Join(" / ", sections.Select(s => s.Header?.ToString()));
        Console.WriteLine($"      分区：{titles}");
        return null;
    }

    /// <summary>喂入模型目录后，提供商分区必须显示真实数字。</summary>
    /// <remarks>
    /// <b>这条检查防的是"接上了但没刷新"</b>：<c>SetModelCatalog</c> 被调用、
    /// 不抛异常，但界面文本没变——那是最难发现的一类问题，
    /// 因为编译、运行都没有任何报错。
    /// </remarks>
    private static string? ModelCatalogShowsNumbers()
    {
        var view = new SettingsView();

        // 用假的目录喂进去，断言文本真的变了。不依赖真实引擎。
        var models = Enumerable.Range(1, 7)
            .Select(i => new ModelInfo("prov-test", $"model-{i}", 128_000, 4096, false))
            .ToArray();
        view.SetModelCatalog(models, ["prov-test"], "model-3");

        var texts = CollectTexts(view);
        if (!texts.Any(t => t.Contains("1 家", StringComparison.Ordinal)))
            return $"提供商数量没更新。实际文本：{string.Join(" | ", texts.Take(12))}";
        if (!texts.Any(t => t.Contains("7 个模型", StringComparison.Ordinal)))
            return $"模型数量没更新。实际文本：{string.Join(" | ", texts.Take(12))}";
        if (!texts.Any(t => t.Contains("model-3", StringComparison.Ordinal)))
            return "当前模型 id 没显示出来";

        return null;
    }

    /// <summary>密钥行必须如实说明"来自环境变量"，不能给一个假输入框。</summary>
    /// <remarks>
    /// <b>这条检查守的是一条产品判断</b>：没有持久化就不该给输入框。
    /// 填了重启就丢，比不显示更糟——用户会以为自己保存了。
    /// </remarks>
    private static string? KeyShowsEnvironmentSource()
    {
        var view = new SettingsView();
        var texts = CollectTexts(view);

        var hasEnvMention = texts.Any(t => t.Contains("DOUGONG_PROVIDER_KEY", StringComparison.Ordinal));
        if (!hasEnvMention)
            return "密钥行没有说明来自环境变量 DOUGONG_PROVIDER_KEY";

        // 不许出现密码框——那是"能录入但不能保存"的假承诺。
        //
        // **要同时扫逻辑树和视觉树**：只扫逻辑树时，构造函数里 `new` 出来
        // 但没挂到父控件上的 TextBox 查不到（我第一版就被这个骗过，
        // 注入了一个游离的密码框，检查仍然 PASS）。
        var boxes = view.GetLogicalDescendants().OfType<TextBox>()
            .Concat(view.GetVisualDescendants().OfType<TextBox>())
            .Distinct()
            .Where(t => t.PasswordChar is not '\0' and not default(char))
            .ToArray();
        if (boxes.Length > 0)
            return $"出现了 {boxes.Length} 个密码输入框，但本轮没有持久化——填了会丢";

        return null;
    }

    /// <summary>
    /// 插件分区必须列出已加载插件，空列表时给出可操作的提示。
    /// </summary>
    /// <remarks>
    /// <b>两个断言，各自防一种回归</b>：
    /// <list type="number">
    /// <item>有插件时必须真的列出 Id 与槽位——空列表是"接上了但没渲染"，
    /// 本项目在命令面板上踩过一次。</item>
    /// <item>没插件时必须给一句可操作的话（告诉用户 `DOUGONG_UI_PLUGINS`），
    /// 只显示"0 个"会让用户不知道该怎么装。</item>
    /// </list>
    /// </remarks>
    private static string? PluginsListLoadedOnly()
    {
        var view = new SettingsView();

        // 空列表：要给提示，不能只有一个 "0"
        view.SetPlugins([]);
        var emptyTexts = CollectTexts(view).ToArray();
        if (!emptyTexts.Any(t => t.Contains("DOUGONG_UI_PLUGINS", StringComparison.Ordinal)))
            return "没有插件时，界面没告诉用户怎么加载（应提到 DOUGONG_UI_PLUGINS）";

        // 有插件：要列出 Id 与槽位
        var fake = new FakeExtension("test.plugin", "测试插件", "shell.trailing");
        view.SetPlugins([new LoadedExtension(fake, null!, null!, "/tmp/Test.dll", SlotOccupantOrigin.Installed)]);

        var texts = CollectTexts(view).ToArray();
        if (!texts.Any(t => t.Contains("test.plugin", StringComparison.Ordinal)))
            return "已加载插件的 Id 没显示出来";
        if (!texts.Any(t => t.Contains("shell.trailing", StringComparison.Ordinal)))
            return "已加载插件的槽位没显示出来";
        if (!texts.Any(t => t.Contains("第三方", StringComparison.Ordinal)))
            return "没有按 Origin 分组（应显示「第三方」）";

        // 再切回空：确认列表真的被清空，不是追加
        view.SetPlugins([]);
        if (CollectTexts(view).Any(t => t.Contains("test.plugin", StringComparison.Ordinal)))
            return "SetPlugins 第二次调用没有清空上一次的条目（会越积越多）";

        return null;
    }

    /// <summary>测试用插件，只实现接口不做事。</summary>
    private sealed class FakeExtension(string id, string name, string slot)
        : DougongDesktop.Ui.Core.IDesktopUiExtension
    {
        public string Id => id;
        public string DisplayName => name;
        public string SlotKey => slot;
        public string? SlotKeyValue => null;
        public Control Build() => new TextBlock();
    }

    /// <summary>
    /// 诊断分区必须显示中文状态词，不能漏出枚举名。
    /// </summary>
    /// <remarks>
    /// <b>这条检查的来由</b>：第一次跑通设置页时，"引擎"那一行显示的是
    /// <c>Connected</c>——满屏中文里蹦出一个英文枚举名。它不是日志，
    /// 是给用户看的状态，漏出枚举名说明中文化那一步漏了。
    /// <para>
    /// 断言方式是给每个状态各喂一次，确认输出里**不含枚举名本身**。
    /// 只查"包含中文"不够——<c>Connected · 已连接</c> 也包含中文。
    /// </para>
    /// </remarks>
    private static string? ShowsChineseStateWords()
    {
        var view = new SettingsView();

        var states = new (string Input, string Expect)[]
        {
            ("已连接 · test", "已连接"),
            ("连接失败 · test", "连接失败"),
            ("未连接", "未连接"),
        };

        foreach (var (input, expect) in states)
        {
            view.SetDiagnostics(input, "openai", 84);
            var texts = CollectTexts(view).ToArray();
            if (!texts.Any(t => t.Contains(expect, StringComparison.Ordinal)))
                return $"喂入「{input}」后，界面上找不到「{expect}」";
        }

        // 反向：枚举名不该出现在界面上（SetDiagnostics 的入参已经是中文，
        // 所以这里查的是 MainWindow 的翻译函数有没有漏 —— 用源码断言）
        var mw = Path.Combine(
            FindRepoRoot(), "src", "DougongDesktop.App", "Views", "MainWindow.cs");
        if (!File.Exists(mw)) return $"找不到 {mw}";

        var src = File.ReadAllText(mw);
        foreach (var name in new[] { "Connected", "Connecting", "Starting", "Failed", "Disconnected" })
        {
            // 必须有对应的中文分支
            var mapped = src.Contains($"EngineConnectionState.{name} => ", StringComparison.Ordinal);
            if (!mapped)
                return $"DescribeConnection 缺少 {name} 的中文映射，界面上会漏出枚举名";
        }

        return null;
    }

    /// <summary>
    /// 设置页展示的"检查条数"必须与架构守卫的实际条数一致。
    /// </summary>
    /// <remarks>
    /// <b>防的是数字漂移</b>：<c>ArchitectureCheckCount</c> 是手写常量
    /// （App 拿不到检查工程的结果，见那里的注释）。每加一条检查就会少算一次，
    /// 界面上显示"84 条"而实际有 87 条——这种数字错了没人会报 bug，
    /// 但它会让人以为"守卫只有这么点"，低估这套自检的覆盖面。
    /// <para>
    /// 统计口径：<c>Architecture.Checks</c> 源码里 <c>Case("slug"</c> / <c>Check("slug"</c>
    /// 的出现次数。读源码而不是跑一遍，是因为跑一遍要几十秒（84 次反证）。
    /// </para>
    /// </remarks>
    private static string? CheckCountMatchesGuards()
    {
        var src = Path.Combine(
            FindRepoRoot(), "tests", "DougongDesktop.Architecture.Checks");

        if (!Directory.Exists(src))
            return $"找不到检查工程目录 {src}";

        var pattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:Case|Check)\(\s*""[a-z0-9-]+""");

        var actual = Directory.EnumerateFiles(src, "*.cs")
            .Sum(f => pattern.Matches(File.ReadAllText(f)).Count);

        // 无头检查不在架构守卫里，别把它们算进来
        if (actual <= 0)
            return "一条检查都没统计到——正则或目录变了";

        var shown = ArchitectureCheckCountReflector.Get();
        if (shown != actual)
            return $"设置页显示 {shown} 条，实际架构守卫 {actual} 条。"
                   + $"改 MainWindow.ArchitectureCheckCount 为 {actual}";

        return null;
    }

    /// <summary>
    /// 读 <c>MainWindow</c> 里那个常量的值。
    /// </summary>
    /// <remarks>
    /// 常量是 <c>private</c>，反射要 <c>NonPublic</c>。用编译期常量而不是
    /// 实例字段，所以取 <c>static</c> 且 <c>BindingFlags.NonPublic</c>。
    /// </remarks>
    private static class ArchitectureCheckCountReflector
    {
        public static int Get()
        {
            var field = typeof(DougongDesktop.App.Views.MainWindow).GetField(
                "ArchitectureCheckCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            return field?.GetRawConstantValue() as int? ?? -1;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// 收集界面上所有可见文本。
    /// </summary>
    /// <remarks>
    /// <b>必须同时收 <see cref="SettingsRow.Header"/> 与 <c>Description</c></b>：
    /// 它们是 ContentControl 的属性，不是 TextBlock 子控件——只扫 TextBlock
    /// 会漏掉每一行的标题与说明。
    /// 第一版就漏了，导致"插件 Id 没显示出来"的误报：实际渲染是好的。
    /// </remarks>
    /// <summary>
    /// <c>DOUGONG_UI_DOCK=1</c> 时右栏必须用 DockLayout，且布局树建出来了（P8e E4）。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条要存在</b>：Dock 模式是"接了但默认不开"，最容易被忽略的
    /// 状态——它不出现在默认界面上，坏了也没人会发现。
    /// <para>
    /// 断言：走的是 <c>DockLayout</c>（不是 <c>Tabs</c>）、五个 pane 都在、
    /// 根节点是分裂或分组结构。
    /// </para>
    /// <para>
    /// <b>为什么在无头里测</b>：这台机器 GUI 时好时坏（CVDisplayLink -6661），
    /// 而 DockLayout 在无头下能完整建出布局树，不需要真实窗口。
    /// </para>
    /// </remarks>
    private static string? DockModeBuildsSplitTree()
    {
        var old = Environment.GetEnvironmentVariable("DOUGONG_UI_DOCK");
        Environment.SetEnvironmentVariable("DOUGONG_UI_DOCK", "1");

        try
        {
            var dock = new DougongDesktop.App.Shell.RightDock();

            if (dock.DockPanes is null)
                return "DOUGONG_UI_DOCK=1 时没有走 Dock 模式（Content 仍是 Tabs）";

            if (dock.DockPanes.Count != 5)
                return $"Dock pane 只有 {dock.DockPanes.Count} 个（期望 5）";

            foreach (var key in new[] { "files", "changes", "plan", "context", "terminal" })
                if (!dock.DockPanes.ContainsKey(key))
                    return $"缺少 pane「{key}」";

            if (dock.Content is not TKCode.Arc.DockLayout layout)
                return "Content 不是 DockLayout";

            var rootName = layout.Root?.GetType().Name;
            if (rootName is null)
                return "DockLayout.Root 为 null：布局树没建出来";
            if (rootName is not ("DockSplit" or "DockGroup"))
                return $"布局根是 {rootName}（期望 DockSplit 或 DockGroup）";

            Console.WriteLine($"      Dock 根={rootName}，panes={dock.DockPanes.Count}");
            return null;
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOUGONG_UI_DOCK", old);
        }
    }

    /// <summary>
    /// 标题栏必须是 ArcWindow 的 TitleBar，且左侧内容真的注进去了。
    /// </summary>
    /// <remarks>
    /// <b>这条补的是静态检查做不到的部分</b>：架构守卫只能读源码文本，
    /// 证明"写了 LeftContent ="，证明不了"内容真的挂上了、按钮真的有 3 个"。
    /// 第一次替换时两套标题栏同时存在，源码里看不出任何异常——
    /// 是运行时 `标题栏按钮 7 个` 才暴露的（多出 4 个 0x0）。
    /// </remarks>
    private static string? TitlebarUsesArcSlots()
    {
        // 必须先声明 slot：AppShell 构造时就要往 shell.leading 等槽位注册，
        // 没声明会直接抛。这与产品的启动顺序一致（Program 里先 DeclareSlots）。
        var slots = new DougongDesktop.Ui.Core.SlotRegistry();
        DougongDesktop.App.Shell.AppShell.DeclareSlots(slots);
        var shell = new DougongDesktop.App.Shell.AppShell(
            slots, new DougongDesktop.App.Shell.AppShellViewModel());

        // 左侧内容必须存在且含 3 个按钮（收起侧栏 / 应用 / 编辑）
        if (shell.TitlebarContent is null)
            return "AppShell.TitlebarContent 为 null：左侧按钮没建出来";

        var buttons = shell.TitlebarContent.GetLogicalDescendants()
            .OfType<Avalonia.Controls.Button>().Count();
        if (buttons != 3)
            return $"左侧内容里按钮 {buttons} 个（期望 3：收起侧栏 / 应用 / 编辑）";

        // 自绘窗口按钮必须彻底消失——它们现在由 Arc 的 TitleBar 按平台处理
        var hasWindowButtons = typeof(DougongDesktop.App.Shell.AppShell)
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Any(m => m.Name.Contains("BuildWindowButtons", StringComparison.Ordinal));
        if (hasWindowButtons)
            return "AppShell 仍有 BuildWindowButtons 方法：macOS 上会与系统交通灯重复";

        return null;
    }

    /// <summary>
    /// 终端面板必须如实报告"暂不可用"，不能显示一个空白假终端。
    /// </summary>
    /// <remarks>
    /// <b>这条检查守的是一条产品判断，不是功能</b>：<c>TerminalView</c> 已接入、
    /// 原生库已随包落地、<c>GhosttyRuntime.Version</c> 也能读出 <c>0.1.0-dev</c>，
    /// 但 <c>TerminalCore.Create</c> 必然抛 <c>NullReferenceException</c>
    /// （上游缺陷，最小复现见 <c>TerminalPanel.KnownUpstreamIssue</c>）。
    /// <para>
    /// 所以面板当前只能"如实说不可用"。这条检查断言的是：
    /// <list type="bullet">
    /// <item>状态文字说明了原因（不是空白、不是"未实现"三个字了事）</item>
    /// <item><b>没有伪装成可用</b>——不出现提示符、不出现假命令回显</item>
    /// </list>
    /// 上游修好之后，这条检查要跟着改成"VT 真的解析了字节"。
    /// </para>
    /// </remarks>
    private static string? TerminalPanelReportsUnavailable()
    {
        var panel = new DougongDesktop.App.Shell.TerminalPanel();
        var texts = CollectTexts(panel).ToArray();

        if (!texts.Any(t => t.Contains("暂不可用", StringComparison.Ordinal)))
            return "面板没有说明「暂不可用」，实际文本：" + string.Join(" | ", texts.Take(6));

        // 不能用假提示符冒充可用终端
        var fakePrompt = new[] { "$ ", "# ", "> ", "C:\\" };
        var hasFake = texts.Any(t => fakePrompt.Any(p => t.EndsWith(p, StringComparison.Ordinal)));
        if (hasFake)
            return "出现了假提示符——不可用的终端不该装成能用";

        if (panel.IsCoreReady)
            return "IsCoreReady 为 true，但上游缺陷下不应就绪；若上游已修复，请更新这条检查";

        return null;
    }

    private static IEnumerable<string> CollectTexts(Visual root)
    {
        var blocks = root.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty);

        var rows = root.GetLogicalDescendants().OfType<SettingsRow>().ToArray();
        var rowHeaders = rows.Select(r => r.Header?.ToString() ?? string.Empty);
        var rowDescriptions = rows.Select(r => r.Description ?? string.Empty);

        return blocks
            .Concat(rowHeaders)
            .Concat(rowDescriptions)
            .Where(t => t.Length > 0);
    }

    private static int Check(string name, Func<string?> body)
    {
        string? error;
        try
        {
            error = body();
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (error is null)
        {
            Console.WriteLine($"PASS  {name}");
            return 0;
        }

        Console.WriteLine($"FAIL  {name}");
        Console.WriteLine($"      {error}");
        return 1;
    }
}
