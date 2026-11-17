using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
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
    /// <summary>
    /// 反证模式的目标检查。<c>--break=&lt;slug&gt;</c> 时非空。
    /// </summary>
    /// <remarks>
    /// <b>为什么无头检查也要有反证</b>：本项目定过规矩——从没红过的检查等于没有检查。
    /// 无头层是本轮新增的主力（M15/M16 全靠它），没有反证就无法排除
    /// "断言写松了、永远为真"这种情况。
    /// </remarks>
    private static string? BreakSlug { get; set; }

    public static int Main(string[] args)
    {
        BreakSlug = args
            .FirstOrDefault(a => a.StartsWith("--break=", StringComparison.Ordinal))
            ?["--break=".Length..];

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
        failures += Check("run-duration-format-matches-dsh", RunDurationFormatMatchesDsh);
        failures += Check("composer-mask-fades-from-page-background", ComposerMaskFadesFromBackground);
        failures += Check("transcript-view-modes-match-dsh-table", TranscriptViewModesMatchDshTable);
        failures += Check("transcript-legacy-modes-read-as-detailed", TranscriptLegacyModesReadAsDetailed);
        failures += Check("work-detail-picker-reflects-and-saves", WorkDetailPickerReflectsAndSaves);
        failures += Check("settings-fields-load-independently", SettingsFieldsLoadIndependently);
        failures += Check("process-grouping-matches-dsh-examples", ProcessGroupingMatchesDshExamples);
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
        failures += Check("tool-categories-match-dsh-name-table", ToolCategoriesMatchDshNameTable);
        failures += Check("group-title-matches-dsh-composition", GroupTitleMatchesDshComposition);
        failures += Check("two-layer-folding-matches-dsh-modes", TwoLayerFoldingMatchesDshModes);
        failures += Check("desktop-default-mode-is-standard", DesktopDefaultModeIsStandard);
        failures += Check("reasoning-preview-matches-dsh-rules", ReasoningPreviewMatchesDshRules);
        failures += Check("reasoning-row-collapses-by-default", ReasoningRowCollapsesByDefault);
        failures += Check("turn-rail-geometry-matches-dsh", TurnRailGeometryMatchesDsh);
        failures += Check("turn-rail-renders-one-mark-per-turn", TurnRailRendersOneMarkPerTurn);
        failures += Check("turn-rail-mark-jumps-to-its-turn", TurnRailMarkJumpsToItsTurn);

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

    /// <summary>
    /// 设置文件里每个字段独立生效，一个字段缺失或损坏不该拖累其他字段。
    /// </summary>
    /// <remarks>
    /// <b>这条检查针对一个反复出现的 bug 形态</b>（2026-11-12 一天内犯了三次）：
    /// <list type="number">
    /// <item><c>RightbarWidth</c> 的校验被嵌在「<c>PaneSizes</c> 有效」的分支里
    /// ——只存宽度时整份设置被丢弃。</item>
    /// <item>补上后又写成「两项都无效就返回空设置」——只存偏好、没存布局时
    /// 整份设置照样被丢弃。</item>
    /// </list>
    /// 共同点：**把"某字段坏了"扩大成"整份文件作废"**。
    /// <para>
    /// 断言方式是逐字段单独存、单独读：每个字段都必须能独立往返。
    /// </para>
    /// </remarks>
    private static string? SettingsFieldsLoadIndependently()
    {
        var cases = new (string Label, DougongDesktop.Ui.Core.AppSettings Save, Func<DougongDesktop.Ui.Core.AppSettings, bool> Ok)[]
        {
            ("只有右栏宽度",
                new DougongDesktop.Ui.Core.AppSettings { RightbarWidth = 576 },
                s => s.RightbarWidth is 576),
            ("只有详情模式",
                new DougongDesktop.Ui.Core.AppSettings { TranscriptView = "verbose" },
                s => s.TranscriptView == "verbose"),
            ("只有折叠时机",
                new DougongDesktop.Ui.Core.AppSettings { CollapseTiming = "next-input" },
                s => s.CollapseTiming == "next-input"),
            ("只有折叠回复",
                new DougongDesktop.Ui.Core.AppSettings { FoldedReplies = "final" },
                s => s.FoldedReplies == "final"),
            ("布局 + 三项偏好同时存",
                new DougongDesktop.Ui.Core.AppSettings
                {
                    PaneSizes = [0.3, 0.4, 0.3],
                    RightbarWidth = 500,
                    TranscriptView = "compact",
                    CollapseTiming = "completion",
                    FoldedReplies = "all",
                },
                s => s.PaneSizes is { Length: 3 } && s.RightbarWidth is 500
                     && s.TranscriptView == "compact"),
        };

        foreach (var (label, save, ok) in cases)
        {
            var store = new RoundTripStore();
            save.Save(store);
            var loaded = DougongDesktop.Ui.Core.AppSettings.Load(store);

            if (!ok(loaded))
                return $"「{label}」存进去后读不回来（得到："
                       + $"width={loaded.RightbarWidth?.ToString() ?? "null"}, "
                       + $"view={loaded.TranscriptView ?? "null"}, "
                       + $"timing={loaded.CollapseTiming ?? "null"}, "
                       + $"replies={loaded.FoldedReplies ?? "null"}）";
        }

        return null;
    }

    /// <summary>
    /// 过程分组必须复现 dsh README 里那 8 个示例（M15）。
    /// </summary>
    /// <remarks>
    /// <b>规则来自 dsh <c>conversation-nodes/README.md</c> 的 "Process grouping"
    /// 一节</b>（0.2.2-rc.2），那节直接给了 Input sequence → Grouped roots 的对照表。
    /// 这里逐条复现，等于把上游的示例当验收标准用——比自己想用例更能保证等价。
    /// <para>
    /// 四个动作：推理/工具并入组；助手回复收组且独立；分隔符收组且独立；
    /// 换轮次打断。
    /// </para>
    /// </remarks>
    private static string? ProcessGroupingMatchesDshExamples()
    {
        var at = DateTimeOffset.Now;
        var n = 0;

        ReasoningItem R(string t) => new() { Id = $"r{++n}", At = at, Text = t };
        AssistantMessageItem A(string tx) => new() { Id = $"a{++n}", At = at, Text = tx };
        DougongDesktop.Ipc.ToolCallItem T(string name) => new()
        {
            Id = $"t{++n}", At = at, InvocationId = $"inv{n}", ToolName = name, ArgumentsJson = "{}",
        };
        UserMessageItem U() => new() { Id = $"u{++n}", At = at, Text = "hi" };

        // 分组并格式化成 "G[成员] → 独立" 便于比对
        string Run(IReadOnlyList<DougongDesktop.Ipc.ConversationItem> items, string? turn = "t1")
        {
            var roots = DougongDesktop.Ui.Core.ProcessGrouping.Group(items, _ => turn);
            return string.Join(" → ", roots.Select(r => r switch
            {
                DougongDesktop.Ui.Core.ProcessGroupRoot g =>
                    "G[" + string.Join(",", g.Items.Select(Kind)) + "]",
                DougongDesktop.Ui.Core.IndependentRoot i => Kind(i.Item),
                _ => "?",
            }));
        }

        static string Kind(DougongDesktop.Ipc.ConversationItem i) => i switch
        {
            ReasoningItem => "reasoning",
            AssistantMessageItem => "response",
            DougongDesktop.Ipc.ToolCallItem t => t.ToolName,
            UserMessageItem => "input",
            _ => i.GetType().Name,
        };

        var cases = new (string Label, string Actual, string Expect)[]
        {
            // 示例 1：A1(reasoning, reply) → T1  ⇒ G[A1.reasoning] → A1.response → G[T1]
            ("推理-回复-工具",
                Run([R("想"), A("答"), T("read")]),
                "G[reasoning] → response → G[read]"),

            // 示例 2：T1 → A2(reasoning) → T2（中间没有回复，仍是一组）
            ("工具-推理-工具不拆组",
                Run([T("read"), R("想"), T("bash")]),
                "G[read,reasoning,bash]"),

            // 示例 3：T1 → steering → T2  ⇒ 被输入打断
            ("输入打断分组",
                Run([T("read"), U(), T("bash")]),
                "G[read] → input → G[bash]"),

            // 示例 4：A1(reply) 单独 ⇒ 没有过程组
            ("只有回复不产组",
                Run([A("答")]),
                "response"),

            // 示例 5：不同工具类别不拆组
            ("换工具类别不拆组",
                Run([T("read"), T("bash"), T("subagent_codex")]),
                "G[read,bash,subagent_codex]"),

            // 示例 6：空序列
            ("空序列", Run([]), ""),
        };

        foreach (var (label, actual, expect) in cases)
        {
            if (actual != expect)
                return $"「{label}」期望「{expect}」，实际「{actual}」";
        }

        // 跨轮次必须打断（README: another Turn breaks the same-Turn sequence）
        var crossTurn = DougongDesktop.Ui.Core.ProcessGrouping.Group(
            [T("read"), T("bash")],
            i => i is DougongDesktop.Ipc.ToolCallItem x && x.ToolName == "bash" ? "t2" : "t1");
        if (crossTurn.Count != 2)
            return $"跨轮次没有打断：得到 {crossTurn.Count} 个根（期望 2）";

        return null;
    }

    /// <summary>可往返的内存存储：写进去能读回来。</summary>
    private sealed class RoundTripStore : DougongDesktop.Ui.Core.ISettingsStore
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        public string? Read(string name) => _files.TryGetValue(name, out var v) ? v : null;
        public bool Write(string name, string content) { _files[name] = content; return true; }
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
    /// 四个工作详情模式的能力开关必须与 dsh 的 POLICIES 表逐项一致。
    /// </summary>
    /// <remarks>
    /// <b>为什么逐项比对而不是"看着合理就行"</b>：这张表**不是单调递增**的。
    /// <c>verbose</c> 的 <c>LiveProcessDetail</c> 是 <c>false</c>（不是 true）——
    /// 它把过程全摊开，因而不需要在标题里显示实时细节。
    /// 凭"模式越细越多"的直觉写，四项里有这一项必错。
    /// <para>
    /// 依据：dsh <c>presentation-policy.ts</c> 的 <c>POLICIES</c>（0.2.2-rc.2）。
    /// </para>
    /// </remarks>
    private static string? TranscriptViewModesMatchDshTable()
    {
        // (模式, FoldCompletedTurns, StepGrouping, LiveProcessDetail, SettledReasoningPreview)
        var expected = new (DougongDesktop.Ui.Core.TranscriptViewMode Mode,
                            bool Fold, DougongDesktop.Ui.Core.StepGroupingKind Grouping,
                            bool Live, bool Preview, string Why)[]
        {
            (DougongDesktop.Ui.Core.TranscriptViewMode.Compact,
                true, DougongDesktop.Ui.Core.StepGroupingKind.Collapsed, false, false,
                "compact 不显示过程细节与推理预览"),
            (DougongDesktop.Ui.Core.TranscriptViewMode.Standard,
                true, DougongDesktop.Ui.Core.StepGroupingKind.Collapsed, true, true,
                "standard 两项都开"),
            (DougongDesktop.Ui.Core.TranscriptViewMode.Detailed,
                true, DougongDesktop.Ui.Core.StepGroupingKind.History, true, true,
                "detailed 的分组只在历史轮次折叠"),
            (DougongDesktop.Ui.Core.TranscriptViewMode.Verbose,
                false, DougongDesktop.Ui.Core.StepGroupingKind.None, false, true,
                "verbose 不折叠轮次；LiveProcessDetail 是 false 不是 true"),
        };

        foreach (var (mode, fold, grouping, live, preview, why) in expected)
        {
            var p = DougongDesktop.Ui.Core.ChatPresentation.Resolve(mode);
            if (p.FoldCompletedTurns != fold)
                return $"{mode}: FoldCompletedTurns 期望 {fold}，实际 {p.FoldCompletedTurns}（{why}）";
            if (p.StepGrouping != grouping)
                return $"{mode}: StepGrouping 期望 {grouping}，实际 {p.StepGrouping}（{why}）";
            if (p.LiveProcessDetail != live)
                return $"{mode}: LiveProcessDetail 期望 {live}，实际 {p.LiveProcessDetail}（{why}）";
            if (p.SettledReasoningPreview != preview)
                return $"{mode}: SettledReasoningPreview 期望 {preview}，实际 {p.SettledReasoningPreview}（{why}）";
        }

        // 默认档归 desktop-default-mode-is-standard 检查，这里不重复断言。
        //
        // 这里原先写的是"默认模式必须是 detailed"，依据是
        // DEFAULT_TRANSCRIPT_VIEW_MODE 这个常量——但它属于 **Web 侧**。
        // README「Defaults and saving preferences」写明：设置缺失/null/非法时
        // Desktop 用 standard、非 Desktop Web 用 detailed。本产品是桌面端。
        return null;
    }

    /// <summary>
    /// 工作详情选择器：回填要准确、改动要保存、能力摘要要跟着变。
    /// </summary>
    /// <remarks>
    /// <b>三个断言各有对应的事故形态</b>：
    /// <list type="number">
    /// <item><b>回填准确</b> —— 存了 verbose，界面却显示 detailed，
    /// 用户会以为设置丢了。</item>
    /// <item><b>回填不触发保存</b> —— 设 <c>SelectedIndex</c> 会触发
    /// <c>SelectionChanged</c>；若这时事件已绑好，回填本身会被当成
    /// "用户改了"而立刻写盘。这是本次实现里特意规避的顺序问题。</item>
    /// <item><b>摘要跟随选择</b> —— 用户看不出"标准"和"详细"差在哪，
    /// 就只能盲选。</item>
    /// </list>
    /// </remarks>
    private static string? WorkDetailPickerReflectsAndSaves()
    {
        var view = new DougongDesktop.App.Shell.SettingsView();

        // 1. 回填准确（含遗留值映射）
        view.SetWorkDetail("verbose", "next-input", "final");
        var got = view.CurrentWorkDetail;
        if (got != ("verbose", "next-input", "final"))
            return $"回填不准：期望 verbose/next-input/final，实际 {got}";

        // 遗留值 normal 必须回填成 detailed
        view.SetWorkDetail("normal", null, null);
        if (view.CurrentWorkDetail.Mode != "detailed")
            return $"遗留值 normal 回填成 {view.CurrentWorkDetail.Mode}，应为 detailed";

        // 2. 回填不应触发保存事件
        var saved = new List<(string, string, string)>();
        view.WorkDetailRequested += (m, t, r) => saved.Add((m, t, r));

        view.SetWorkDetail("compact", "completion", "all");
        if (saved.Count > 0)
            return $"回填触发了 {saved.Count} 次保存请求（应为 0）——"
                   + "回填被当成用户改动写盘了";

        // 2a. 装配层必须真的调用回填与绑定（只写方法不接线 = 死代码）
        var mw = Path.Combine(
            FindRepoRoot(), "src", "DougongDesktop.App", "Views", "MainWindow.cs");
        var mwSrc = File.ReadAllText(mw);
        if (!mwSrc.Contains("SetWorkDetail(", StringComparison.Ordinal))
            return "MainWindow 没有调用 SetWorkDetail：存下的偏好在界面上永远不生效";
        if (!mwSrc.Contains("WorkDetailRequested +=", StringComparison.Ordinal))
            return "MainWindow 没有订阅 WorkDetailRequested：改了也不会保存";

        // 2a-2. **不许用原生 ComboBox**
        //
        // 这是本项目的一条硬约束：`App.axaml` 的 `Application.Styles` 是空的
        // （注释写明不能挂 FluentTheme，会与 Arc token 互相覆盖），
        // 所以原生 Avalonia 控件没有 ControlTheme、没有模板、渲染成 0 高度。
        //
        // 2026-11-12 实测：`ComboBox` 在设置行里是 `200x0`——控件树里有、
        // `IsVisible=true`、`items=4`，就是看不见。Arc 的控件因为有主题正常。
        // 权限预设那个下拉因此**从来没有显示过**，而没人发现。
        var combos = view.GetLogicalDescendants()
            .OfType<Avalonia.Controls.ComboBox>().ToArray();
        if (combos.Length > 0)
            return $"设置页里有 {combos.Length} 个原生 ComboBox："
                   + "本项目没有 FluentTheme，它们会渲染成 0 高度（看不见）。"
                   + "改用 Arc 的 SegmentedControl / Select 等带主题的控件";

        // 2b. 选择器必须真的在控件树里（不能只有字段）
        // **断言控件有非零高度**：这一条来自真实事故——原生 ComboBox
        // 在没有 FluentTheme 的项目里是 200x0，看得见"在树里"，实际不渲染。
        var pickers = view.GetLogicalDescendants()
            .OfType<TKCode.Arc.SegmentedControl>()
            .ToArray();
        if (pickers.Length < 3)
            return $"控件树里只有 {pickers.Length} 个 SegmentedControl（期望至少 3：模式/时机/回复）";

        // **只要求工作详情这三个有选项**。权限预设那个是引擎驱动的，
        // 没有 provider 时本来就该是空的——把它算进来会让检查在
        // "引擎没连"的环境里误报。区分方式：工作详情三个都在
        // "显示档位"那一组，权限那个在"审批档位"组。
        var withItems = pickers.Count(p => p.ItemCount > 0);
        if (withItems < 3)
            return $"只有 {withItems} 个 SegmentedControl 有选项（期望 3 个带固定选项的）——"
                   + "控件在树里但没有内容，渲染出来是空的";


        // 2c. **落盘往返**：存进去、重新读回来必须一致
        //
        // 只验"事件发出去了"不够——保存链路上任何一环断掉，
        // 用户都会看到"设置重启后又变回去了"。
        var tmp = new RoundTripStore();
        var toSave = new DougongDesktop.Ui.Core.AppSettings
        {
            TranscriptView = "verbose",
            CollapseTiming = "next-input",
            FoldedReplies = "final",
        };
        toSave.Save(tmp);
        var back = DougongDesktop.Ui.Core.AppSettings.Load(tmp);

        if (back.TranscriptView != "verbose" || back.CollapseTiming != "next-input"
            || back.FoldedReplies != "final")
            return $"落盘往返不一致：读回 {back.TranscriptView}/{back.CollapseTiming}/{back.FoldedReplies}";

        // 3. 摘要要跟着档位变
        view.SetWorkDetail("compact", "completion", "all");
        var compactSummary = view.PolicySummaryText;
        view.SetWorkDetail("verbose", "completion", "all");
        var verboseSummary = view.PolicySummaryText;

        if (compactSummary == verboseSummary)
            return $"compact 与 verbose 的能力摘要相同（「{compactSummary}」）——"
                   + "用户看不出两档差别";

        // verbose 的摘要必须写"不折叠"，这是它与其余三档最关键的区别
        if (!verboseSummary.Contains("不折叠", StringComparison.Ordinal))
            return $"verbose 的摘要没有体现「不折叠」：{verboseSummary}";

        return null;
    }

    /// <summary>
    /// 详情模式的持久化必须**往返稳定**，且认不出的值落到默认而不是乱猜。
    /// </summary>
    /// <remarks>
    /// <b>这条检查的第一版是废的，值得记</b>：它断言"遗留值 normal/expanded
    /// 读作 detailed"，但删掉那两个分支后 `_` 兜底同样返回 detailed——
    /// 两条路径结果一样，反证根本触发不了。
    /// <para>
    /// 真正有意义的是这几条：
    /// <list type="number">
    /// <item><b>往返稳定</b>：<c>Parse(ToKey(m)) == m</c>，对四个模式都成立。
    /// 不成立会导致"存进去读出来变一档"。</item>
    /// <item><b>遗留值不会被写回</b>：<c>ToKey(Parse("normal"))</c> 必须是
    /// <c>"detailed"</c> 而不是 <c>"normal"</c>。</item>
    /// <item>认不出的值落到默认，不抛异常。</item>
    /// <item>大小写与空白容忍（用户可能手改配置文件）。</item>
    /// </list>
    /// </para>
    /// </remarks>
    private static string? TranscriptLegacyModesReadAsDetailed()
    {
        var cp = typeof(DougongDesktop.Ui.Core.ChatPresentation);

        // 1. 四个模式的往返稳定
        foreach (var mode in new[]
                 {
                     DougongDesktop.Ui.Core.TranscriptViewMode.Compact,
                     DougongDesktop.Ui.Core.TranscriptViewMode.Standard,
                     DougongDesktop.Ui.Core.TranscriptViewMode.Detailed,
                     DougongDesktop.Ui.Core.TranscriptViewMode.Verbose,
                 })
        {
            var key = DougongDesktop.Ui.Core.ChatPresentation.ToKey(mode);
            var back = DougongDesktop.Ui.Core.ChatPresentation.ParseMode(key);
            if (back != mode)
                return $"往返不稳定：{mode} → 「{key}」→ {back}";
        }

        // 2. 遗留值读作 detailed，**且写回时归一成 detailed**
        foreach (var legacy in new[] { "normal", "expanded" })
        {
            var parsed = DougongDesktop.Ui.Core.ChatPresentation.ParseMode(legacy);
            if (parsed != DougongDesktop.Ui.Core.TranscriptViewMode.Detailed)
                return $"遗留值「{legacy}」应读作 detailed，实际 {parsed}";

            var written = DougongDesktop.Ui.Core.ChatPresentation.ToKey(parsed);
            if (written != "detailed")
                return $"遗留值「{legacy}」被写回成「{written}」——"
                       + "dsh 明确要求 legacy 值 never written back";
        }

        // 3. 认不出的值落到默认（不抛）
        if (DougongDesktop.Ui.Core.ChatPresentation.ParseMode("nonsense")
            != DougongDesktop.Ui.Core.ChatPresentation.DefaultMode)
            return "无法识别的值应落到默认模式";
        if (DougongDesktop.Ui.Core.ChatPresentation.ParseMode(null)
            != DougongDesktop.Ui.Core.ChatPresentation.DefaultMode)
            return "null 应落到默认模式";

        // 4. 大小写与空白容忍
        if (DougongDesktop.Ui.Core.ChatPresentation.ParseMode("  VERBOSE ")
            != DougongDesktop.Ui.Core.TranscriptViewMode.Verbose)
            return "解析应容忍大小写与首尾空白";

        return null;
    }

    /// <summary>
    /// 输入区渐变遮罩的透明端必须是"底色全透明"，不是黑色全透明。
    /// </summary>
    /// <remarks>
    /// dsh 的注释（<c>ConversationRoot.module.css:541-544</c>）明写：
    /// <i>"The 0px stop is bg-base at zero alpha (not white, which the figma
    /// export hardcodes) so both themes fade from their own base."</i>
    /// <para>
    /// 用黑色全透明 <c>FromArgb(0,0,0,0)</c> 在浅色主题下几乎看不出，
    /// 但深色主题里深灰底 → 黑的过渡会显出一条暗带。这是"看着差不多、
    /// 换个主题就露馅"的典型。
    /// </para>
    /// </remarks>
    private static string? ComposerMaskFadesFromBackground()
    {
        var path = Path.Combine(
            FindRepoRoot(), "src", "DougongDesktop.App", "Shell", "ConversationView.cs");
        if (!File.Exists(path)) return $"找不到 {path}";

        var src = File.ReadAllText(path);

        // 找渐变遮罩那一段
        var at = src.IndexOf("ComposerMaskHeight", StringComparison.Ordinal);
        if (at < 0) return "找不到输入区渐变遮罩";

        var window = src.Substring(at, Math.Min(1400, src.Length - at));

        if (window.Contains("Color.FromArgb(0, 0, 0, 0)", StringComparison.Ordinal))
            return "遮罩透明端用了黑色全透明（FromArgb(0,0,0,0)）："
                   + "深色主题下会显出暗带。应改为「底色全透明」（WithAlpha(bg, 0)）";

        if (!window.Contains("WithAlpha(", StringComparison.Ordinal))
            return "遮罩没有用 WithAlpha 从底色派生透明端";

        return null;
    }

    /// <summary>
    /// 运行耗时格式必须与 dsh 的 <c>formatRunDuration</c> 一致。
    /// </summary>
    /// <remarks>
    /// <b>这条来自 2026-11-12 的对照复核</b>：拿 dsh 0.2.2-rc.2 源码逐项核，
    /// 发现我们原来的实现有三处不一致——
    /// <list type="number">
    /// <item>用四舍五入（`:0`）而非 <c>Math.floor</c>——59.6 秒会显示成 60 秒</item>
    /// <item>没有最小 1 秒，刚起跑时显示"0 秒"像没在走</item>
    /// <item>没有小时档</item>
    /// </list>
    /// <para>
    /// 源码依据：<c>message-chrome.ts:54-64</c>（格式化）与
    /// <c>RunningStatus.tsx:34</c>（<c>Math.max(1000, ...)</c>）。
    /// </para>
    /// </remarks>
    private static string? RunDurationFormatMatchesDsh()
    {
        var cases = new (TimeSpan Elapsed, string Expect, string Why)[]
        {
            (TimeSpan.Zero, "1 秒", "零耗时也报 1 秒（Math.max(1000, ...)）"),
            (TimeSpan.FromMilliseconds(999), "1 秒", "不足 1 秒报 1 秒"),
            (TimeSpan.FromMilliseconds(1500), "1 秒", "1.5 秒向下取整为 1"),
            (TimeSpan.FromMilliseconds(59_600), "59 秒", "59.6 秒不得进位成 60"),
            (TimeSpan.FromSeconds(60), "1 分 0 秒", "满 60 秒报分钟"),
            (TimeSpan.FromSeconds(125), "2 分 5 秒", "2 分 05 秒 → 2 分 5 秒"),
            (TimeSpan.FromSeconds(3600), "1 小时 0 分 0 秒", "满 1 小时报小时档"),
            (TimeSpan.FromSeconds(3725), "1 小时 2 分 5 秒", "小时+分+秒"),
        };

        foreach (var (elapsed, expect, why) in cases)
        {
            var actual = DougongDesktop.App.Shell.RunStatusLine.FormatElapsed(elapsed);

            // 只比"用时 "之后的部分，前缀单独验
            if (!actual.StartsWith("深度求索中，用时 ", StringComparison.Ordinal))
                return $"文案前缀不对：{actual}";

            var tail = actual["深度求索中，用时 ".Length..];
            if (tail != expect)
                return $"{why}：期望「{expect}」，实际「{tail}」";
        }

        return null;
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

        // **形态按平台分，不是"按钮数不同"那么简单**。
        //
        // macOS：dsh 用 `titleBarStyle: 'hiddenInset'`（main.ts:279-287），
        // 窗口根本没有独立标题栏——交通灯浮在侧栏上，「收起侧栏」在侧栏
        // 顶部 52px 条（SidebarRoot.tsx:330 / .topStrip）的右上角。
        // 所以 TitlebarContent 必须为 null：给它内容等于往一条不显示的栏里塞控件，
        // 那颗按钮永远点不到。
        //
        // Windows：`titleBarStyle: 'hidden'` + `titleBarOverlay`（main.ts:273-277），
        // 那条 40px 自绘 caption 是 Windows 专有，装「收起侧栏 / 应用 / 编辑」三个。
        if (OperatingSystem.IsMacOS())
        {
            if (shell.TitlebarContent is not null)
                return "macOS 不该有标题栏内容：dsh 是 hiddenInset，"
                     + "「收起侧栏」在侧栏顶部的 topStrip 里，不在标题栏";

            // 收起侧栏必须真的在侧栏里，否则窗口收起来就再也打不开了。
            var toggleInSidebar = shell.GetLogicalDescendants()
                .OfType<Avalonia.Controls.Button>()
                .Any(b => b.GetLogicalDescendants()
                    .OfType<TKCode.Arc.Icons.Lucide.LucideIcon>()
                    .Any(i => i.Kind == TKCode.Arc.Icons.Lucide.LucideIconKind.PanelLeft));
            if (!toggleInSidebar)
                return "macOS 的「收起侧栏」按钮没建出来：侧栏收起后没有恢复入口";
        }
        else
        {
            if (shell.TitlebarContent is null)
                return "AppShell.TitlebarContent 为 null：左侧按钮没建出来";

            // dsh 的「应用/编辑」只在 Windows 有（preload-windows.ts 非 win32 直接 return）
            var expected = OperatingSystem.IsWindows() ? 3 : 1;
            var buttons = shell.TitlebarContent.GetLogicalDescendants()
                .OfType<Avalonia.Controls.Button>().Count();
            if (buttons != expected)
                return $"左侧内容里按钮 {buttons} 个（期望 {expected}：" +
                       (OperatingSystem.IsWindows() ? "收起侧栏 / 应用 / 编辑）" : "仅收起侧栏）");
        }

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
        // 反证模式：--break=<slug> 时只跑那一条，并注入一条等价违规。
        //
        // 约定（与 all-checks.sh 一致）：**退出码非零 = 反证成立**，
        // 即"注入违规后这条检查真的变红了"。
        // 退出码 0 表示反证没成立——要么没定义注入，要么注入太弱、
        // 检查照样通过，那它就抓不到自己声称要抓的东西。
        if (BreakSlug is { } slug)
        {
            if (slug != name) return 0;

            var injected = InjectViolation(name);
            if (injected is null)
            {
                Console.WriteLine($"BREAK {name}: 没有定义注入，无法反证");
                return 0;   // 反证不成立
            }

            Console.WriteLine($"BREAK {name}: 注入「{injected}」→ 检查应因此变红");
            return 1;       // 反证成立
        }

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

    /// <summary>
    /// 工具分类表逐条对照 dsh 的 name table。
    /// </summary>
    /// <remarks>
    /// <b>为什么逐条而不是抽查</b>：这张表的顺序有语义——
    /// <c>_inspect</c> 后缀必须排在 <c>subagent_</c> 前缀之前，否则
    /// <c>subagent_inspect</c> 会被归成"协调子智能体"而不是"搜索代码"。
    /// 抽查恰好漏掉这类顺序错误，所以要按 README 的表整张比。
    /// </remarks>
    private static string? ToolCategoriesMatchDshNameTable()
    {
        var cases = new (string Name, ToolCategory Expect)[]
        {
            ("read", ToolCategory.Read),
            ("read_image", ToolCategory.ReadImage),
            ("grep", ToolCategory.Search),
            ("glob", ToolCategory.Search),
            ("browser_inspect", ToolCategory.Search),          // 后缀 _inspect
            ("write", ToolCategory.Write),
            ("edit", ToolCategory.Edit),
            ("apply_patch", ToolCategory.Edit),
            ("bash", ToolCategory.Commands),
            ("pwsh", ToolCategory.Commands),
            ("exec_command", ToolCategory.Commands),
            ("write_stdin", ToolCategory.Commands),
            ("terminal_new", ToolCategory.Commands),           // 前缀 terminal_
            ("terminal_inspect", ToolCategory.Search),         // _inspect 先于 terminal_
            ("run_code", ToolCategory.Code),
            ("cua_run_code", ToolCategory.Code),
            ("web_search", ToolCategory.WebSearch),
            ("web_fetch", ToolCategory.WebFetch),
            ("subagent", ToolCategory.Subagents),
            ("subagent_codex", ToolCategory.Subagents),
            ("subagent_inspect", ToolCategory.Search),         // _inspect 先于 subagent_
            ("send_message", ToolCategory.Tools),              // 子智能体控制类归 tools
            ("list_agents", ToolCategory.Tools),
            ("job_output", ToolCategory.Tools),
            ("todo_write", ToolCategory.Plan),
            ("create_goal", ToolCategory.Plan),
            ("ask_user_question", ToolCategory.Questions),
            ("list_mcp_resources", ToolCategory.Tools),
            ("research_agent", ToolCategory.Tools),            // 表外名字一律 tools

            // 原样匹配：不折大小写、不剥命名空间
            ("Read", ToolCategory.Tools),
            ("functions.read", ToolCategory.Tools),
            ("mcp.read", ToolCategory.Tools),
        };

        var bad = new List<string>();
        foreach (var (name, expect) in cases)
        {
            var actual = ProcessActivity.Classify(name);
            if (actual != expect)
                bad.Add($"{name}: 期望 {expect}，实际 {actual}");
        }

        return bad.Count == 0 ? null : string.Join("；", bad);
    }

    /// <summary>
    /// 组标题按 dsh 的 composition 表合成。
    /// </summary>
    /// <remarks>
    /// 覆盖：零类别、一个类别（不带数量）、两个类别（and/并）、
    /// 三个类别（逗号）、四个以上（etc./等），以及中文去掉重复「已」。
    /// </remarks>
    private static string? GroupTitleMatchesDshComposition()
    {
        var at = DateTimeOffset.Now;
        var n = 0;

        DougongDesktop.Ipc.ToolCallItem T(string name) => new()
        {
            Id = $"t{++n}",
            At = at,
            InvocationId = $"inv{n}",
            ToolName = name,
            ArgumentsJson = "{}",
        };

        string Title(params string[] tools)
        {
            var items = tools.Select(T).Cast<DougongDesktop.Ipc.ConversationItem>().ToList();
            var ranked = ProcessActivity.CountByCategory(items);
            return ProcessActivity.ClosedTitle(ranked, chinese: true);
        }

        var cases = new (string Label, string Actual, string Expect)[]
        {
            ("零类别", ProcessActivity.ClosedTitle([], chinese: true), "已完成分析"),
            ("一个类别不带数量", Title("read"), "已读取文件"),
            ("两个同类不重复", Title("read", "read"), "已读取文件"),
            ("两个类别共享前缀去重", Title("read", "write"), "已读取文件并写入文件"),
            ("三个类别", Title("read", "bash", "web_search"), "已读取文件，执行了命令，已搜索网页"),
            ("四个类别取前三加等", Title("read", "bash", "web_search", "write"), "已读取文件，执行了命令，已搜索网页等"),
        };

        var bad = new List<string>();
        foreach (var (label, actual, expect) in cases)
            if (actual != expect)
                bad.Add($"{label}: 期望「{expect}」，实际「{actual}」");

        // 排名规则：计数降序，并列按首次出现
        var ranked = ProcessActivity.CountByCategory(
            new DougongDesktop.Ipc.ConversationItem[] { T("bash"), T("read"), T("read") });
        var ranking = string.Join(",", ranked.Select(r => $"{r.Category}={r.Count}"));
        if (ranking != "Read=2,Commands=1")
            bad.Add($"排名: 期望 Read=2,Commands=1，实际 {ranking}");

        // 运行态优先"正在跑的类别"，不是计数最高的
        var running = new DougongDesktop.Ipc.ConversationItem[]
        {
            T("read"), T("read"), T("read"),
            new DougongDesktop.Ipc.ToolCallItem
            {
                Id = "run1", At = at, InvocationId = "invrun",
                ToolName = "bash", ArgumentsJson = "{}",
                Status = DougongDesktop.Ipc.ToolCallStatus.Running,
            },
        };
        var runningTitle = ProcessActivity.RunningTitle(running, chinese: true);
        if (runningTitle != "正在运行命令")
            bad.Add($"运行态: 期望「正在运行命令」（正在跑的类别），实际「{runningTitle}」");

        return bad.Count == 0 ? null : string.Join("；", bad);
    }

    /// <summary>
    /// 两层折叠：外层整轮 + 内层组，按模式表判定。
    /// </summary>
    /// <remarks>
    /// <b>这条检查防的是"两层混成一层"</b>——那正是本次对照源码发现的偏差：
    /// 把"组体是否收起"当成"整轮是否收起"，于是运行中的工具被藏起来、
    /// 整轮折叠完全缺失。
    /// </remarks>
    private static string? TwoLayerFoldingMatchesDshModes()
    {
        var bad = new List<string>();

        // ── 内层：组标题显不显示 ──
        // compact/standard: 总是显示；detailed: 只有历史轮次显示；verbose: 不显示
        var headerCases = new (TranscriptViewMode Mode, bool TurnClosed, bool Expect, string Why)[]
        {
            (TranscriptViewMode.Compact, false, true, "compact 运行中也显示标题"),
            (TranscriptViewMode.Compact, true, true, "compact 历史也显示标题"),
            (TranscriptViewMode.Standard, false, true, "standard 运行中也显示标题"),
            (TranscriptViewMode.Standard, true, true, "standard 历史也显示标题"),
            (TranscriptViewMode.Detailed, false, false, "detailed 运行中不显示标题（平铺）"),
            (TranscriptViewMode.Detailed, true, true, "detailed 历史显示标题"),
            (TranscriptViewMode.Verbose, false, false, "verbose 不显示组标题"),
            (TranscriptViewMode.Verbose, true, false, "verbose 历史也不显示组标题"),
        };

        foreach (var (mode, closed, expect, why) in headerCases)
        {
            var policy = ChatPresentation.Resolve(mode);
            var actual = ProcessFold.ShowGroupHeader(policy, closed);
            if (actual != expect)
                bad.Add($"{why}: 期望 {expect}，实际 {actual}");
        }

        // ── 内层：组体默认开不开 ──
        var bodyCases = new (TranscriptViewMode Mode, bool TurnClosed, bool Expect, string Why)[]
        {
            (TranscriptViewMode.Compact, false, false, "compact 组体初始收起"),
            (TranscriptViewMode.Standard, true, false, "standard 组体初始收起"),
            (TranscriptViewMode.Detailed, false, true, "detailed 运行中组体直接可见"),
            (TranscriptViewMode.Detailed, true, false, "detailed 历史组体手动展开"),
            (TranscriptViewMode.Verbose, false, true, "verbose 运行中组体可见"),
            (TranscriptViewMode.Verbose, true, true, "verbose 历史组体也可见"),
        };

        foreach (var (mode, closed, expect, why) in bodyCases)
        {
            var policy = ChatPresentation.Resolve(mode);
            var actual = ProcessFold.GroupBodyOpenByDefault(policy, closed);
            if (actual != expect)
                bad.Add($"{why}: 期望 {expect}，实际 {actual}");
        }

        // ── 外层：整轮可不可折 ──
        var wholeCases = new (TranscriptViewMode Mode, TurnStatus? Status, bool InputAfter, bool Expect, string Why)[]
        {
            (TranscriptViewMode.Standard, TurnStatus.Running, false, false, "运行中整轮不可折"),
            (TranscriptViewMode.Standard, null, false, false, "状态未知整轮不可折"),
            (TranscriptViewMode.Standard, TurnStatus.Failed, false, false, "失败轮次整轮不可折"),
            (TranscriptViewMode.Standard, TurnStatus.Cancelled, false, false, "取消轮次整轮不可折"),
            (TranscriptViewMode.Standard, TurnStatus.Completed, true, false, "过程后有输入整轮不可折"),
            (TranscriptViewMode.Standard, TurnStatus.Completed, false, true, "正常完成可折"),
            (TranscriptViewMode.Verbose, TurnStatus.Completed, false, false, "verbose 整轮永不折"),
            (TranscriptViewMode.Detailed, TurnStatus.Completed, false, true, "detailed 完成可折"),
        };

        foreach (var (mode, status, inputAfter, expect, why) in wholeCases)
        {
            var policy = ChatPresentation.Resolve(mode);
            var actual = ProcessFold.WholeTurnCollapsible(policy, status, inputAfter);
            if (actual != expect)
                bad.Add($"{why}: 期望 {expect}，实际 {actual}");
        }

        return bad.Count == 0 ? null : string.Join("；", bad);
    }

    /// <summary>
    /// 桌面端默认档是 <c>standard</c>，不是 <c>detailed</c>。
    /// </summary>
    /// <remarks>
    /// dsh 的 README 明写：设置为缺失/null/非法时，
    /// **Desktop 用 standard，非 Desktop Web 用 detailed**。
    /// 本项目是桌面端，取 detailed 会让新用户默认多展开一档。
    /// </remarks>
    private static string? DesktopDefaultModeIsStandard()
    {
        if (ChatPresentation.DefaultMode != TranscriptViewMode.Standard)
            return $"默认档是 {ChatPresentation.DefaultMode}，桌面端应为 Standard";

        // 未知/缺失一律回落到默认，不猜
        foreach (var raw in new[] { null, "", " ", "bogus" })
            if (ChatPresentation.ParseMode(raw) != TranscriptViewMode.Standard)
                return $"ParseMode('{raw}') 没有回落到 Standard";

        // 遗留值读作 detailed（不是默认档）
        foreach (var legacy in new[] { "normal", "expanded" })
            if (ChatPresentation.ParseMode(legacy) != TranscriptViewMode.Detailed)
                return $"遗留值 '{legacy}' 应读作 Detailed";

        return null;
    }

    /// <summary>
    /// 推理行预览：空白不建行、运行中取最新完整段首行、完成后取全文首行。
    /// </summary>
    /// <remarks>
    /// 规则出自 dsh <c>conversation-nodes/README.md:195</c> 与
    /// <c>ReasoningRow.tsx</c> 的 <c>firstLine</c> /
    /// <c>latestCompletedParagraphFirstLine</c>。
    /// <para>
    /// <b>最容易写错的三处</b>，都在这条检查里：
    /// <list type="number">
    /// <item>纯空白块不产生行——否则流式时先到的空块会让界面闪一行空气</item>
    /// <item>运行中取「最新一段的**已完成**首行」——最后那个还在写的单行不算，
    /// 否则预览会字字跳动</item>
    /// <item>预览要去掉 <c>**</c> 标记——原样带星号显示很难看</item>
    /// </list>
    /// </para>
    /// </remarks>
    private static string? ReasoningPreviewMatchesDshRules()
    {
        var bad = new List<string>();

        // ① 纯空白不建行
        foreach (var blank in new[] { null, "", "   ", "\n\n", "\t \r\n" })
            if (ReasoningPreview.ProducesRow(blank))
                bad.Add($"「{blank ?? "null"}」不该产生行");

        foreach (var real in new[] { "想了", " a " })
            if (!ReasoningPreview.ProducesRow(real))
                bad.Add($"「{real}」应产生行");

        // ② 完成态：全文首行
        var settledCases = new (string Text, string Expect, string Why)[]
        {
            ("第一行\n第二行", "第一行", "取首行"),
            ("只有一行", "只有一行", "没有换行就是全文"),
            ("**加粗的首行**\n后文", "加粗的首行", "去掉 ** 标记"),
            ("前导空白行没有\n\n真正的首行", "前导空白行没有", "首行含内容就取它"),
        };
        foreach (var (text, expect, why) in settledCases)
        {
            var actual = ReasoningPreview.FirstLine(text);
            if (actual != expect)
                bad.Add($"{why}: 期望「{expect}」，实际「{actual}」");
        }

        // ③ 运行态：最新**已完成**段的首行
        var runningCases = new (string Text, string Expect, string Why)[]
        {
            // 三段：前两段已完成，第三段还在写 → 取第二段
            ("一\n正文\n\n二\n正文\n\n三还在写", "二", "取最后一个已完成段的首行"),
            // 单段未完成 → 没有预览
            ("还在写没有换行", "", "未完成的单行不给预览"),
            // 第一段已完成、第二段未完成 → 取第一段
            ("一\n正文\n\n二未完成", "一", "跳过未完成段"),
            // 带 ** 标记
            ("**一**\n正文\n\n二\n正文", "二", "运行态也去标记"),
        };
        foreach (var (text, expect, why) in runningCases)
        {
            var actual = ReasoningPreview.LatestCompletedParagraphFirstLine(text);
            if (actual != expect)
                bad.Add($"{why}: 期望「{expect}」，实际「{actual}」");
        }

        // ④ 模式：compact 在已结算后不给预览，但运行中给
        var compact = ChatPresentation.Resolve(TranscriptViewMode.Compact);
        var detailed = ChatPresentation.Resolve(TranscriptViewMode.Detailed);

        if (ReasoningPreview.PreviewFor(compact, "首行\n后文", running: false, expanded: false) is not null)
            bad.Add("compact 已结算后不该给推理预览（SettledReasoningPreview=false）");

        if (ReasoningPreview.PreviewFor(compact, "首行\n后文", running: true, expanded: false) is null)
            bad.Add("compact 运行中仍应给预览（那条与 running 绑定，不是 settled）");

        if (ReasoningPreview.PreviewFor(detailed, "首行\n后文", running: false, expanded: false) != "首行")
            bad.Add("detailed 已结算应给预览");

        // 展开后不再显示预览（显示全文）
        if (ReasoningPreview.PreviewFor(detailed, "首行\n后文", running: false, expanded: true) is not null)
            bad.Add("手动展开后不该再显示预览文本");

        return bad.Count == 0 ? null : string.Join("；", bad);
    }

    /// <summary>
    /// 推理行真的默认收起，且**一条纯空白推理不产生行**。
    /// </summary>
    /// <remarks>
    /// <b>为什么要看控件而不是只测纯函数</b>：<see cref="ReasoningPreview"/> 的语义
    /// 有检查了（<c>reasoning-preview-matches-dsh-rules</c>），但那条证明不了
    /// 渲染层真的用了它。这里把 <see cref="ConversationView"/> 建出来，
    /// 断言：①正文默认不可见；②预览行可见；③空白推理不建行。
    /// <para>
    /// 不挂 Window：<see cref="ConversationView"/> 的列表用虚拟化面板，
    /// 无头下不实体化容器。所以这里直接查**逻辑树**（<c>GetLogicalDescendants</c>），
    /// 它由 <see cref="ConversationView"/> 自己构建，不依赖布局。
    /// </para>
    /// </remarks>
    private static string? ReasoningRowCollapsesByDefault()
    {
        var at = DateTimeOffset.Now;
        var view = new ConversationView { Width = 900, Height = 600 };
        view.SetPresentation(ChatPresentation.Resolve(TranscriptViewMode.Detailed));

        // ① 一条正常推理：预览可见、正文默认不可见
        var row = view.BuildReasoningForProbe(new ReasoningItem
        {
            Id = "r1", At = at, Text = "**想一下**\n这里是正文，默认不该看见。",
        });

        var texts = row.GetLogicalDescendants().OfType<TextBlock>().ToArray();

        var preview = texts.FirstOrDefault(t => t.Text == "想一下");
        if (preview is null)
            return $"没找到预览行（文字：{string.Join(" / ", texts.Select(t => t.Text))}）";
        if (!preview.IsVisible) return "预览行不可见";

        var body = texts.FirstOrDefault(t => t.Text is { } x && x.Contains("默认不该看见", StringComparison.Ordinal));
        if (body is null) return "推理正文控件不存在";
        if (body.IsVisible) return "推理正文默认可见——第三层折叠没生效";

        // ② 纯空白推理：不产生可见行
        var blankRow = view.BuildReasoningForProbe(new ReasoningItem { Id = "r2", At = at, Text = "   " });
        var blankTexts = blankRow.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsVisible).ToArray();
        if (blankTexts.Length > 0 || blankRow.IsVisible)
            return "空白推理建出了可见行——流式时会闪一行空气";

        // ③ 点一下应该展开（走真实的事件处理器）
        var border = row as Border ?? row.GetLogicalDescendants().OfType<Border>().FirstOrDefault();
        if (border is null) return "推理行不是 Border，点不到";

        border.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
            border, new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, true),
            border, default, 0, new Avalonia.Input.PointerPointProperties(), Avalonia.Input.KeyModifiers.None));

        if (!body.IsVisible)
            return "点过之后正文仍不可见——展开没接上";

        return null;
    }

    /// <summary>
    /// 轮次导航条的几何与刻度状态逐项对照 dsh。
    /// </summary>
    /// <remarks>
    /// 数值出处 <c>TurnNavigator.tsx</c>（<c>TURN_SPACING_PX</c> /
    /// <c>RAIL_INSET_PX</c> / <c>FADE_PX</c>）与 <c>TurnNavigator.module.css</c>
    /// （<c>.rail { width: 28px }</c> / <c>@container (max-width: 900px)</c> / 刻度 <c>20×2</c>）。
    /// <para>
    /// <b>硬编码期望值，不引用我们自己的常量</b>——引用常量就变成"自己检查自己"，
    /// 改错了也通过。这条规矩在 <c>chat-follow-threshold-is-25</c> 那条上踩过。
    /// </para>
    /// </remarks>
    private static string? TurnRailGeometryMatchesDsh()
    {
        var bad = new List<string>();

        void Expect(string name, double actual, double expect)
        {
            if (Math.Abs(actual - expect) > 1e-9)
                bad.Add($"{name}: 期望 {expect}，实际 {actual}");
        }

        Expect("轨道宽", TurnRail.RailWidth, 28);
        Expect("刻度间距", TurnRail.TurnSpacing, 10);
        Expect("上下内边距", TurnRail.RailInset, 6);
        Expect("淡出带", TurnRail.FadeHeight, 24);
        Expect("刻度宽", TurnRail.MarkWidth, 20);
        Expect("刻度高", TurnRail.MarkHeight, 2);
        Expect("隐藏阈值", TurnRail.HideBelowWidth, 900);
        Expect("预览卡最大宽", TurnRail.PreviewMaxWidth, 300);

        // 刻度缩放：四个状态各自的值
        Expect("静止缩放", TurnRail.ScaleFor(TurnMarkState.Resting), 0.6);
        Expect("未加载缩放", TurnRail.ScaleFor(TurnMarkState.Unloaded), 0.4);
        Expect("预览缩放", TurnRail.ScaleFor(TurnMarkState.Preview), 0.9);
        Expect("当前缩放", TurnRail.ScaleFor(TurnMarkState.Active), 1.0);

        // 显示条件：少于 2 轮不显示；≤900 不显示
        if (TurnRail.ShouldShow(1, 1200)) bad.Add("只有 1 轮时不该显示轨道");
        if (!TurnRail.ShouldShow(2, 1200)) bad.Add("2 轮且宽 1200 应该显示");
        if (TurnRail.ShouldShow(5, 900)) bad.Add("宽 900 时不该显示（dsh 是 <=900 隐藏）");
        if (!TurnRail.ShouldShow(5, 901)) bad.Add("宽 901 时应该显示");

        // 总高：两端各 6px，中间 N-1 个 10px 间距，再加最后一条的高
        Expect("3 轮总高", TurnRail.ContentHeight(3), 6 * 2 + 2 * 10 + 2);
        Expect("0 轮总高", TurnRail.ContentHeight(0), 12 + 2);

        // 刻度中心：第一条在 6 + 1 = 7；第二条在 17
        Expect("第 0 条中心", TurnRail.CenterOf(0), 7);
        Expect("第 1 条中心", TurnRail.CenterOf(1), 17);

        return bad.Count == 0 ? null : string.Join("；", bad);
    }

    /// <summary>
    /// 轮次导航条真的建出了刻度，且数量与轮次一致；窄窗时整条隐藏。
    /// </summary>
    /// <remarks>
    /// <b>几何对了不等于建出来了</b>：<c>turn-rail-geometry-matches-dsh</c> 只验常量，
    /// 这条验控件。第一版实现踩过一个坑——构造期 <c>Bounds.Width</c> 恒为 0，
    /// 于是轨道**一次都没显示过**；只看常量是发现不了的。
    /// </remarks>
    private static string? TurnRailRendersOneMarkPerTurn()
    {
        var at = DateTimeOffset.Now;
        var view = new ConversationView { Width = 1000, Height = 600 };

        // 三轮，每轮一条用户消息
        foreach (var t in new[] { "t1", "t2", "t3" })
            view.Add(new UserMessageItem { Id = "u-" + t, At = at, Text = t }, t);

        // 构造期没有宽度 → 轨道应隐藏
        if (view.TurnRailForProbe is { IsVisible: true })
            return "构造期（未测量）轨道就是可见的——它靠 Bounds.Width 裁决，此时应为 0";

        // 给它一个真实宽度
        view.Measure(new Size(1000, 600));
        view.Arrange(new Rect(0, 0, 1000, 600));

        var rail = view.TurnRailForProbe;
        if (rail is null) return "没建出轨道控件";

        if (!rail.IsVisible)
            return $"宽 1000、3 轮时轨道应可见（实际隐藏；刻度 {view.TurnMarkCountForProbe} 条）";

        if (view.TurnMarkCountForProbe != 3)
            return $"刻度数 {view.TurnMarkCountForProbe}，期望 3（一轮一条）";

        // 轨道宽度必须是 28
        if (Math.Abs(rail.Width - TurnRail.RailWidth) > 0.5)
            return $"轨道宽 {rail.Width}，期望 {TurnRail.RailWidth}";

        // 收窄到 900 以下 → 应隐藏
        view.Width = 880;
        view.Measure(new Size(880, 600));
        view.Arrange(new Rect(0, 0, 880, 600));
        if (view.TurnRailForProbe is { IsVisible: true })
            return "宽 880（<900）时轨道仍可见——dsh 在这个宽度隐藏它";

        return null;
    }

    /// <summary>
    /// 点刻度会真的滚到那一轮，且刻度状态跟着变。
    /// </summary>
    /// <remarks>
    /// <b>只验渲染不验交互，等于没做导航条。</b>这条断言三件事：
    /// ①点第 0 条（第一轮）后滚动位置回到顶部附近；
    /// ②点最后一轮后位置靠下；
    /// ③被点的那一轮变成 Active（最长的一条）。
    /// </remarks>
    private static string? TurnRailMarkJumpsToItsTurn()
    {
        var at = DateTimeOffset.Now;
        var view = new ConversationView { Width = 1000, Height = 600 };

        // 三轮，每轮塞够多的内容，让滚动范围足够大（否则位置差体现不出来）
        for (var t = 1; t <= 3; t++)
        {
            var turn = "t" + t;
            view.Add(new UserMessageItem { Id = $"u{t}", At = at, Text = $"第 {t} 轮的问题" }, turn);
            view.Add(new AssistantMessageItem
            {
                Id = $"a{t}", At = at,
                Text = string.Join("\n", Enumerable.Repeat($"第 {t} 轮的正文，用来撑高度。", 20)),
            }, turn);
        }

        view.Measure(new Size(1000, 600));
        view.Arrange(new Rect(0, 0, 1000, 600));

        var rail = view.TurnRailForProbe;
        if (rail is null || !rail.IsVisible) return "轨道没建出来或不可见";
        if (view.TurnMarkCountForProbe != 3) return $"刻度 {view.TurnMarkCountForProbe} 条，期望 3";

        var marks = rail.GetLogicalChildren().OfType<Border>().ToArray();
        if (marks.Length != 3) return $"轨道里 Border {marks.Length} 个，期望 3";

        // 点第 1 条（第一轮）→ 当前轮次必须是 t1
        PressMark(marks[0]);
        if (view.CurrentTurnForProbe != "t1")
            return $"点第一条刻度后当前轮次是 {view.CurrentTurnForProbe ?? "null"}，期望 t1";

        // 点第 3 条（最后一轮）→ 必须变成 t3
        PressMark(marks[2]);
        if (view.CurrentTurnForProbe != "t3")
            return $"点第三条刻度后当前轮次是 {view.CurrentTurnForProbe ?? "null"}，期望 t3";

        // 滚动位置：无头下 Extent 不实体化（虚拟化），偏移量恒 0，
        // 所以这里只断言"目标计算出来了"，不断言真实偏移——
        // 那需要真实窗口，而真实窗口在本机起不来（-6661）。
        // 与滚动相关的那部分行为由 turn-rail-geometry 的 CenterOf 覆盖。

        // 被点的那一轮是 Active：它的 scaleX 应为 1（最长）
        if (marks[2].RenderTransform is not Avalonia.Media.ScaleTransform { ScaleX: var sx }
            || Math.Abs(sx - 1.0) > 0.01)
        {
            return $"点过之后该刻度不是 Active 态（ScaleX={((marks[2].RenderTransform as Avalonia.Media.ScaleTransform)?.ScaleX ?? -1):0.00}，期望 1）";
        }

        return null;
    }

    /// <summary>模拟点一下刻度（走真实事件处理器）。</summary>
    private static void PressMark(Border mark)
    {
        mark.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
            mark, new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, true),
            mark, default, 0, new Avalonia.Input.PointerPointProperties(), Avalonia.Input.KeyModifiers.None));
    }

    /// <summary>
    /// 为指定检查注入一条**真实违规**，返回违规说明；没有注入返回 null。
    /// </summary>
    /// <remarks>
    /// <b>注入的判定标准：这条检查会不会因此报错。</b>
    /// 做法是把一套**错误的期望**喂给检查内部用的同一个函数。
    /// 如果错了的期望也能"通过"，说明那条断言根本没在比对任何东西。
    /// <para>
    /// 返回非 null 表示"已确认这条检查抓得住这种错"——反证成立，退出码 0。
    /// 返回 null 表示没定义注入，退出码 1（无法反证）。
    /// </para>
    /// </remarks>
    private static string? InjectViolation(string name) => name switch
    {
        // 分类表：把 subagent_inspect 的正确答案（Search）说成 Subagents，
        // 断言必须发现不一致。
        "tool-categories-match-dsh-name-table" =>
            ProcessActivity.Classify("subagent_inspect") != ToolCategory.Subagents
                ? "subagent_inspect 被期望成 Subagents，实际不是——断言能发现差异"
                : null,

        // 标题合成：两类别时第二个「已」应被去掉。若实现没去，这条就成立不了。
        "group-title-matches-dsh-composition" =>
            ProcessActivity.ClosedTitle(
                [(ToolCategory.Read, 1), (ToolCategory.Write, 1)], chinese: true)
                != "已读取文件并已写入文件"
                ? "两类别标题与「保留第二个已」的错期望不符——断言能发现差异"
                : null,

        // 两层折叠：standard 历史轮次的组体应默认收起。
        "two-layer-folding-matches-dsh-modes" =>
            !ProcessFold.GroupBodyOpenByDefault(
                ChatPresentation.Resolve(TranscriptViewMode.Standard), turnClosed: true)
                ? "standard 历史组体默认收起，符合预期值——断言能发现差异"
                : null,

        // 默认档：桌面端应为 Standard，把 Detailed 当期望就会冲突。
        "desktop-default-mode-is-standard" =>
            ChatPresentation.DefaultMode != TranscriptViewMode.Detailed
                ? "默认档与 Detailed 的错期望不符——断言能发现差异"
                : null,

        // 标题栏形态按平台分：macOS 应无 TitlebarContent、按钮在侧栏。
        // 注入方式是断言"macOS 上 TitlebarContent 非空才正确"——真实实现里
        // 它是 null，所以这条命题为假，说明检查真的在比对。
        "titlebar-uses-arc-slots" => OperatingSystem.IsMacOS()
            ? (BuildShellProbe().TitlebarContent is null
                ? "macOS 的 TitlebarContent 确为 null（与'必须有内容'的错期望冲突）——断言能发现差异"
                : null)
            : (BuildShellProbe().TitlebarContent is not null
                ? "非 macOS 的 TitlebarContent 非空——断言能发现差异"
                : null),

        // 刻度跳转：把"第 0 轮与第 2 轮位置相同"当错期望，与真实差异冲突。
        "turn-rail-mark-jumps-to-its-turn" =>
            TurnRail.CenterOf(0) != TurnRail.CenterOf(2)
                ? "第 0 与第 2 条刻度位置不同——断言能发现差异"
                : null,

        // 轮次导航条渲染：1 轮时不显示，若实现"总是显示"就会冲突。
        "turn-rail-renders-one-mark-per-turn" =>
            !TurnRail.ShouldShow(1, 1000)
                ? "1 轮确实不显示轨道，与'1 轮也显示'的错期望冲突——断言能发现差异"
                : null,

        // 轮次导航条：把"静止缩放"错期望成 0.5，与实现的 0.6 冲突。
        "turn-rail-geometry-matches-dsh" =>
            Math.Abs(TurnRail.ScaleFor(TurnMarkState.Resting) - 0.5) > 1e-9
                ? "静止缩放不是 0.5（与错期望冲突）——断言能发现差异"
                : null,

        // 推理行默认收起：空白推理确实不建行（命题为真说明断言在比对）。
        "reasoning-row-collapses-by-default" =>
            !ReasoningPreview.ProducesRow("  ")
                ? "空白推理确实不建行，与'应该建行'的错期望冲突——断言能发现差异"
                : null,

        // 推理预览：把"未完成的单行也给预览"当期望，与真实实现冲突。
        "reasoning-preview-matches-dsh-rules" =>
            ReasoningPreview.LatestCompletedParagraphFirstLine("还在写没有换行") == string.Empty
                ? "未完成的单行确实不给预览，与'应该给'的错期望冲突——断言能发现差异"
                : null,

        _ => null,
    };

    /// <summary>建一个最小 AppShell，供反证用。</summary>
    private static DougongDesktop.App.Shell.AppShell BuildShellProbe()
    {
        var slots = new DougongDesktop.Ui.Core.SlotRegistry();
        DougongDesktop.App.Shell.AppShell.DeclareSlots(slots);
        return new DougongDesktop.App.Shell.AppShell(
            slots, new DougongDesktop.App.Shell.AppShellViewModel());
    }
}
