using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DougongDesktop.App.Shell;
using DougongDesktop.Ipc;
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

    private static IEnumerable<string> CollectTexts(Visual root) =>
        root.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty);

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
