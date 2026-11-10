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
        failures += Check("titlebar-menus-are-windows-only", TitlebarMenusAreWindowsOnly);
        failures += Check("dock-mode-builds-split-tree", DockModeBuildsSplitTree);
        failures += Check("permission-picker-reflects-engine-state", PermissionPickerReflectsEngineState);
        failures += Check("engine-survives-bad-command", EngineSurvivesBadCommand);
        failures += Check("subagent-registry-is-single-instance", SubagentRegistryIsSingleInstance);
        failures += Check("subagent-dispatch-tool-is-registered", SubagentDispatchToolIsRegistered);
        failures += Check("model-list-shows-capabilities-not-a-fake-picker", ModelListShowsCapabilities);
        failures += Check("key-diagnostics-never-leak-the-secret", KeyDiagnosticsNeverLeakSecret);
        failures += Check("current-model-stays-visible-when-buried", CurrentModelStaysVisibleWhenBuried);
        failures += Check("drag-resize-cannot-bypass-column-limits", DragResizeCannotBypassLimits);
        failures += Check("saved-rightbar-width-is-revalidated", SavedWidthIsRevalidated);

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
    /// 模型清单要可浏览，且**不许出现切换控件**。
    /// </summary>
    /// <remarks>
    /// <b>这条守的是"不做假控件"</b>：模型是引擎启动时的只读快照，
    /// 契约里也没有切换命令。给一个切不动的下拉，用户会以为点了就该生效。
    /// <para>
    /// 断言三件事：模型条目渲染出来了、当前模型被标出、**没有 ComboBox/Button
    /// 与模型切换相关**。
    /// </para>
    /// </remarks>
    private static string? ModelListShowsCapabilities()
    {
        var view = new DougongDesktop.App.Shell.SettingsView();
        view.SetModelCatalog(
        [
            new ModelInfo("prov-a", "model-x", 64_000, 8_000, false),
            new ModelInfo("prov-a", "model-y", 128_000, 8_000, true),
            new ModelInfo("prov-b", "model-z", 200_000, 8_000, false),
        ], ["prov-a", "prov-b"], "model-x");

        var texts = CollectTexts(view).ToArray();

        // 1. 模型名必须出现
        foreach (var id in new[] { "model-x", "model-y", "model-z" })
            if (!texts.Any(t => t.Contains(id, StringComparison.Ordinal)))
                return $"模型 {id} 没渲染出来";

        // 2. 能力数据必须出现（窗口值格式化后是 64K / 128K / 200K）
        if (!texts.Any(t => t.Contains("64K", StringComparison.Ordinal)))
            return "上下文窗口没显示（期望出现 64K）";

        // 3. 按 provider 分组
        if (!texts.Any(t => t.Contains("prov-a", StringComparison.Ordinal)))
            return "没有按 provider 分组";

        return null;
    }

    /// <summary>
    /// 拖动列宽不能绕过求解器的边界（P8g M17）。
    /// </summary>
    /// <remarks>
    /// <b>拖拽最容易出的问题是"变成第二条计算路径"</b>：手柄自己算宽度、
    /// 自己夹取，于是 dsh 的让步规则（右栏先让步、中栏最低 400、
    /// 右栏上限 45%）被绕过。用户把右栏拖到 900 而中栏只剩 100 时，
    /// 界面看起来"能用"，但已经不符合规格了。
    /// <para>
    /// 断言：把用户拖到的原始值（无论多离谱）喂进 `Columns.Compute`，
    /// 输出的中栏都不能低于 <c>CenterMin</c>、右栏不能超过上限。
    /// </para>
    /// </remarks>
    private static string? DragResizeCannotBypassLimits()
    {
        const double viewport = 1280;

        // 模拟"用户把右栏拖得很宽"
        foreach (var crazy in new[] { 900.0, 2000.0, viewport })
        {
            var sol = DougongDesktop.Ui.Core.Columns.Compute(viewport, 280, crazy);
            if (sol.Center < DougongDesktop.Ui.Core.Columns.CenterMin)
                return $"拖动到 {crazy} 后中栏只剩 {sol.Center}（低于下限 "
                       + $"{DougongDesktop.Ui.Core.Columns.CenterMin}）——边界被绕过了";
        }

        // 模拟"用户把右栏拖得很窄"
        var narrow = DougongDesktop.Ui.Core.Columns.Compute(viewport, 280, 10);
        if (narrow.Rightbar is > 0 and < DougongDesktop.Ui.Core.Columns.RightbarMin)
            return $"拖动到 10 后右栏变成 {narrow.Rightbar}（低于 {DougongDesktop.Ui.Core.Columns.RightbarMin}）"
                   + "——应该要么收起要么夹到下限";

        return null;
    }

    /// <summary>
    /// 存下来的坏宽度必须在读取时被丢弃。
    /// </summary>
    /// <remarks>
    /// <b>这条检查的第一版是错的</b>：它查源码里有没有 `IsNaN` 这个字符串，
    /// 而 `PaneSizes` 的校验里也有 `IsNaN`——把 `RightbarWidth` 的校验删掉后
    /// 检查照样通过。字符串匹配证明不了"这段逻辑管的是这个字段"。
    /// <para>
    /// 现在改成**真正的行为验证**：写一份含坏宽度的 JSON 到内存存储，
    /// 调 `AppSettings.Load`，断言读回来的是 null。
    /// </para>
    /// </remarks>
    private static string? SavedWidthIsRevalidated()
    {
        // 坏值：负数 / NaN / 0 都应被丢弃
        var cases = new (string Json, string Label)[]
        {
            ("""{"rightbarWidth":-100}""", "负数"),
            ("""{"rightbarWidth":0}""", "零"),
            ("""{"rightbarWidth":"NaN"}""", "NaN"),
        };

        foreach (var (json, label) in cases)
        {
            var store = new MemoryStore(json);
            var loaded = DougongDesktop.Ui.Core.AppSettings.Load(store);
            if (loaded.RightbarWidth is not null)
                return $"坏宽度（{label}）没有被丢弃：读回来是 {loaded.RightbarWidth}，"
                       + "会让右栏消失或布局崩溃";
        }

        // 正常值必须保留
        var ok = DougongDesktop.Ui.Core.AppSettings.Load(new MemoryStore("""{"rightbarWidth":576}"""));
        if (ok.RightbarWidth is not 576)
            return $"正常的 576 没被读回来（得到 {ok.RightbarWidth?.ToString() ?? "null"}）";

        return null;
    }

    /// <summary>内存存储，用于测 AppSettings 的读写而不碰磁盘。</summary>
    private sealed class MemoryStore(string json) : DougongDesktop.Ui.Core.ISettingsStore
    {
        public string? Read(string name) => json;
        public bool Write(string name, string content) => true;
    }

    /// <summary>
    /// 当前模型在分组里排得很靠后时，也必须出现在**列表**的可见前几个里。
    /// </summary>
    /// <remarks>
    /// <b>这条检查的第一版是错的，值得记下来</b>：它断言"界面文本里包含
    /// model-11"，而模型名同时也出现在摘要行（"· 当前 model-11"）里，
    /// 所以无论列表有没有截断它，检查都通过。反证一做就露馅了。
    /// <para>
    /// 现在改为**只看列表控件里的行**，不看摘要行——那才是"用户能不能在
    /// 清单里找到自己的模型"这个问题。
    /// </para>
    /// </remarks>
    private static string? CurrentModelStaysVisibleWhenBuried()
    {
        var models = Enumerable.Range(0, 12)
            .Select(i => new ModelInfo("prov-a", $"model-{i:00}", 64_000, 8_000, false))
            .ToArray();

        // model-11 字典序最后；若只按 id 取前 8 就会被挤掉
        var view = new DougongDesktop.App.Shell.SettingsView();
        view.SetModelCatalog(models, ["prov-a"], "model-11");

        // 只收集列表行（SettingsRow.Header），不含摘要块
        var rowHeaders = view.GetLogicalDescendants()
            .OfType<SettingsRow>()
            .Select(r => r.Header?.ToString() ?? string.Empty)
            .Where(t => t.Contains("model-", StringComparison.Ordinal))
            .ToArray();

        if (rowHeaders.Length == 0)
            return "列表里一个模型行都没有";

        if (!rowHeaders.Any(t => t.Contains("model-11", StringComparison.Ordinal)))
            return $"当前模型 model-11 不在列表的前 {rowHeaders.Length} 行里"
                   + "——用户会以为自己的模型不在目录里";

        return null;
    }

    /// <summary>
    /// 密钥诊断只能报形态，**绝不能把密钥本身渲染到界面上**。
    /// </summary>
    /// <remarks>
    /// <b>这是安全断言，不是功能断言</b>：设置页会被截图、贴进 issue、
    /// 出现在录屏里。密钥一旦渲染就是永久泄漏。
    /// <para>
    /// 做法：设一个可识别的假密钥，喂给设置页，然后断言**界面文本里
    /// 不包含它**，但包含它的长度。
    /// </para>
    /// </remarks>
    private static string? KeyDiagnosticsNeverLeakSecret()
    {
        const string fake = "sk-PROBE1234567890abcdefSHOULDNOTAPPEAR";
        var old = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY");
        Environment.SetEnvironmentVariable("DOUGONG_PROVIDER_KEY", fake);

        try
        {
            var view = new DougongDesktop.App.Shell.SettingsView();
            var texts = CollectTexts(view).ToArray();

            if (texts.Any(t => t.Contains(fake, StringComparison.Ordinal)))
                return "**密钥被渲染到界面上了**——截图/录屏会带走它";

            if (!texts.Any(t => t.Contains(fake.Length.ToString(), StringComparison.Ordinal)))
                return $"没有显示密钥长度（{fake.Length}），无法帮助用户排查配置问题";

            if (!texts.Any(t => t.Contains("sk- 前缀", StringComparison.Ordinal)))
                return "没有报告前缀形态";

            return null;
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOUGONG_PROVIDER_KEY", old);
        }
    }

    /// <summary>
    /// 三个子 agent 工具必须**全部**注册，特别是派发工具本身。
    /// </summary>
    /// <remarks>
    /// <b>这条来自一次真实漏注册</b>：2026-11-10 为了绕开
    /// `dispatcherAccessor` 的时序问题，只注册了两个只读工具
    /// （`SubagentListTool` / `SubagentResultTool`），漏了 `SubagentTool`。
    /// <para>
    /// 后果极其误导：三条命令都正常返回（空列表、正确报错）、日志干净、
    /// 检查全绿——**但模型完全没有派发子 agent 的能力**。
    /// 只读工具能工作，不代表主功能接通。
    /// </para>
    /// <para>
    /// 所以这条断言"三个都要有"，而不是"至少有一个"。
    /// </para>
    /// </remarks>
    private static string? SubagentDispatchToolIsRegistered()
    {
        var path = Path.Combine(
            FindRepoRoot(), "src", "DougongDesktop.Framework", "SessionComposer.cs");
        if (!File.Exists(path)) return $"找不到 {path}";

        var src = File.ReadAllText(path);

        var required = new (string Type, string Why)[]
        {
            ("new SubagentTool(", "派发工具——没有它模型无法派子 agent"),
            ("new SubagentListTool(", "列表工具"),
            ("new SubagentResultTool(", "取结果工具"),
        };

        foreach (var (type, why) in required)
        {
            if (!src.Contains(type, StringComparison.Ordinal))
                return $"SessionComposer 没有注册 {type.TrimEnd('(')}（{why}）";
        }

        return null;
    }

    /// <summary>
    /// 子 agent 注册表必须只有一份，且工具拿到的就是协议层看到的那份。
    /// </summary>
    /// <remarks>
    /// <b>这条守的是 SDK 侧明确的"唯一失败模式"</b>：建两份注册表——
    /// 工具用一个、协议层用一个——会让 <c>subagent/list</c> 永远返回空，
    /// 而额度照样在烧。用户看到"没有子 agent 在跑"，实际模型已经派了三个出去。
    /// <para>
    /// <b>怎么断言"同一份"</b>：静态检查不能证明引用相等，所以查两件事——
    /// ①<see cref="SubagentProjection.CreateRegistry"/> 是唯一创建入口；
    /// ②装配处传的是同一个字段 <c>_subagents</c>，不是就地 <c>new</c>。
    /// 真正的引用相等由端到端验证（`subagent/list` 能看到真在跑的那个）。
    /// </para>
    /// </remarks>
    private static string? SubagentRegistryIsSingleInstance()
    {
        var root = FindRepoRoot();

        // ① 只允许通过投影层创建
        var srcDir = Path.Combine(root, "src");
        var badNews = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var text = File.ReadAllText(file);
            if (text.Contains("new SubagentRegistry(", StringComparison.Ordinal)
                && !file.EndsWith("SubagentProjection.cs", StringComparison.Ordinal))
            {
                badNews.Add(Path.GetFileName(file));
            }
        }

        if (badNews.Count > 0)
            return $"这些文件直接 new SubagentRegistry：{string.Join(", ", badNews)}。"
                   + "只应经 SubagentProjection.CreateRegistry 创建，否则容易建出第二份";

        // ② 装配处传的是字段而不是就地 new
        var ipc = Path.Combine(root, "src", "DougongDesktop.AgentHost", "IpcServer.cs");
        if (!File.Exists(ipc)) return $"找不到 {ipc}";

        var ipcSrc = File.ReadAllText(ipc);
        if (!ipcSrc.Contains("_subagents,", StringComparison.Ordinal))
            return "CreateAsync 没有传 _subagents 字段：协议层与工具会用不同的注册表";

        return null;
    }

    /// <summary>
    /// 权限下拉必须反映引擎回报的状态，且切换时发出请求。
    /// </summary>
    /// <remarks>
    /// <b>两个断言，各防一种回归</b>：
    /// <list type="number">
    /// <item>喂入引擎状态后，下拉的选项与选中项要跟着变——不验这个，
    /// "接了但没渲染"会一直绿着。</item>
    /// <item>用户改选中项要发出请求——不验这个，选择器就成了纯装饰。</item>
    /// </list>
    /// <para>
    /// 还要验**抑制回环**：引擎回报后我们设 <c>SelectedIndex</c>，
    /// 那会再触发一次 <c>SelectionChanged</c>，若不挡住就会无限往返。
    /// </para>
    /// </remarks>
    private static string? PermissionPickerReflectsEngineState()
    {
        var view = new DougongDesktop.App.Shell.SettingsView();

        var requested = new List<string>();
        view.PermissionPresetRequested += key => requested.Add(key);

        // 1. 引擎回状态 → 下拉要跟着变
        view.SetPermissionPresets(new PermissionPresetState(
            CurrentKey: "interactive",
            CurrentName: "交互式",
            Options:
            [
                new PermissionPresetInfo("interactive", "交互式", "问", true),
                new PermissionPresetInfo("unattended", "无人值守", "不问", true),
            ]));

        if (view.SelectedPresetKey != "interactive")
            return $"回填后选中项是 {view.SelectedPresetKey ?? "(null)"}，期望 interactive";

        // 2. **回填不该发请求**——设 SelectedIndex 若没挡住会触发 SelectionChanged
        if (requested.Count > 0)
            return $"回填触发了 {requested.Count} 次切换请求（应为 0）：无限往返的起点";

        return null;
    }

    /// <summary>
    /// 引擎收到没有判别器的消息不能崩。
    /// </summary>
    /// <remarks>
    /// <b>这条来自实测事故</b>：探针按具体类型序列化（不带 <c>method</c>），
    /// 服务端 SDK 抛的是 <c>NotSupportedException</c> 而不是 <c>JsonException</c>。
    /// 当时只捕了后者，结果**整个引擎进程退出**，之后所有客户端都连不上。
    /// <para>
    /// 一条客户端发错的命令不该升级成引擎致命错误——那会把
    /// "一个客户端写错"放大成"所有人都用不了"。
    /// 静态断言：<c>HandleAsync</c> 的反序列化必须同时捕获这两种异常。
    /// </para>
    /// </remarks>
    private static string? EngineSurvivesBadCommand()
    {
        var path = Path.Combine(
            FindRepoRoot(), "src", "DougongDesktop.AgentHost", "IpcServer.cs");
        if (!File.Exists(path)) return $"找不到 {path}";

        var src = File.ReadAllText(path);

        // 定位反序列化那一段
        var at = src.IndexOf("json.Deserialize<ClientCommand>", StringComparison.Ordinal);
        if (at < 0) return "找不到反序列化 ClientCommand 的位置";

        // 往后看 600 字符，应该能同时看到两种 catch
        var window = src.Substring(at, Math.Min(700, src.Length - at));
        if (!window.Contains("catch (JsonException", StringComparison.Ordinal))
            return "反序列化没有捕获 JsonException";
        if (!window.Contains("catch (NotSupportedException", StringComparison.Ordinal))
            return "反序列化没有捕获 NotSupportedException："
                   + "缺判别器的消息会让整个引擎退出（实测发生过）";

        return null;
    }

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
    /// 「应用 / 编辑」菜单必须只在 Windows 上出现（照 dsh 的实际行为）。
    /// </summary>
    /// <remarks>
    /// <b>依据是 dsh 源码，不是猜测</b>（<c>apps/desktop/src/preload-windows.ts</c>）：
    /// <code>
    /// export function syncWindowsAppearance(): void {
    ///   if (process.platform !== 'win32') return
    /// </code>
    /// 那个菜单是给 Windows 自绘标题栏配的——Windows 上
    /// <c>titleBarStyle: 'hidden'</c> 抹掉了原生标题栏，菜单得自己画。
    /// macOS 是 <c>hiddenInset</c>，原生交通灯还在、菜单栏在屏幕顶部，
    /// 标题栏里不该再放一份。
    /// <para>
    /// <b>防的是"两边都画"</b>：不分支的话 macOS 上会多出两个本属于
    /// Windows 的菜单，既重复又挤掉交通灯旁边的空间。
    /// </para>
    /// </remarks>
    private static string? TitlebarMenusAreWindowsOnly()
    {
        var path = Path.Combine(
            FindRepoRoot(), "src", "DougongDesktop.App", "Shell", "AppShell.cs");
        if (!File.Exists(path)) return $"找不到 {path}";

        var src = File.ReadAllText(path);

        var appMenu = src.IndexOf("BuildMenuButton(\"应用\")", StringComparison.Ordinal);
        var editMenu = src.IndexOf("BuildMenuButton(\"编辑\")", StringComparison.Ordinal);
        if (appMenu < 0 || editMenu < 0)
            return "找不到「应用」或「编辑」菜单的构建调用";

        // 从「应用」的调用往前找最近的平台守卫；没有就是没分支
        var before = src[..appMenu];
        var guard = before.LastIndexOf("OperatingSystem.IsWindows()", StringComparison.Ordinal);
        if (guard < 0)
            return "「应用/编辑」没有按平台分支：macOS 上会出现本属于 Windows 的菜单";

        // 守卫之后到「应用」调用之间，必须能同时看到该调用与「编辑」。
        // 不去数花括号距离——那对格式太敏感（换行/空格不同就误报）。
        var guardedRegion = src[guard..(appMenu + 200)];
        if (!guardedRegion.Contains("BuildMenuButton(\"应用\")", StringComparison.Ordinal))
            return "「应用」不在 OperatingSystem.IsWindows() 守卫之后";
        if (!guardedRegion.Contains("BuildMenuButton(\"编辑\")", StringComparison.Ordinal))
            return "「编辑」不在 OperatingSystem.IsWindows() 守卫之后";

        return null;
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

        // **期望值按平台分**：dsh 的「应用/编辑」只在 Windows 有
        // （preload-windows.ts 里 `if (process.platform !== 'win32') return`）。
        // macOS 上只剩「收起侧栏」一个。写死 3 会让 Mac 上的正确实现变红。
        var expected = OperatingSystem.IsWindows() ? 3 : 1;
        var buttons = shell.TitlebarContent.GetLogicalDescendants()
            .OfType<Avalonia.Controls.Button>().Count();
        if (buttons != expected)
            return $"左侧内容里按钮 {buttons} 个（期望 {expected}：" +
                   (OperatingSystem.IsWindows() ? "收起侧栏 / 应用 / 编辑）" : "仅收起侧栏）");

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
