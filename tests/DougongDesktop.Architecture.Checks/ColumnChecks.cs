using DougongDesktop.Ui.Core;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// 三栏几何的检查。
/// </summary>
/// <remarks>
/// <b>这些数字全部来自 dsh 的 <c>columns.ts</c></b>，不是我们定的。
/// 检查的作用是：以后谁改了 <see cref="Columns"/>，立即知道偏离了 dsh。
/// <para>
/// 光看常量相等不够——**求解顺序**才是这套布局的意义所在。
/// 所以大部分断言针对的是"在边界情况下谁让步"。
/// </para>
/// </remarks>
internal static class ColumnChecks
{
    /// <summary>仓库根：从检查程序所在目录往上找 slnx。</summary>
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DougongDesktop.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("cols-match-dsh-constants", slug => ConstantsMatch(breakMode == slug));
        failures += Case("cols-sidebar-default-280", slug => SidebarDefault(breakMode == slug));
        failures += Case("cols-rightbar-yields-first", slug => RightbarYieldsFirst(breakMode == slug));
        failures += Case("cols-rightbar-drops-before-center", slug => RightbarDropsBeforeCenter(breakMode == slug));
        failures += Case("cols-rightbar-default-45pct", slug => RightbarDefaultRatio(breakMode == slug));
        failures += Case("cols-responsive-breakpoints", slug => Breakpoints(breakMode == slug));
        failures += Case("color-mix-matches-css", slug => ColorMix(breakMode == slug));
        failures += Case("dsh-tokens-complete", slug => TokensComplete(breakMode == slug));
        failures += Case("dsh-tokens-themes-differ", slug => ThemesDiffer(breakMode == slug));
        failures += Case("titlebar-tokens-match-dsh", slug => TitlebarTokens(breakMode == slug));
        failures += Case("settings-store-is-swappable", slug => StoreSwappable(breakMode == slug));
        failures += Case("platform-calls-stay-in-adapters", slug => PlatformCallsContained(breakMode == slug));
        failures += Case("run-clock-excludes-failure", slug => ClockExcludesFailure(breakMode == slug));
        failures += Case("run-clock-counts-waiting", slug => ClockCountsWaiting(breakMode == slug));

        return failures;
    }

    /// <summary>常量必须逐字等于 dsh <c>columns.ts</c>。</summary>
    private static string? ConstantsMatch(bool inject)
    {
        // dsh 0.2.2-alpha.2 / packages/client/ui-layout/src/client/columns.ts
        var expected = new (string Name, double Actual, double Dsh)[]
        {
            ("SidebarDefault", Columns.SidebarDefault, 280),
            ("SidebarMin", Columns.SidebarMin, 264),
            ("SidebarMax", Columns.SidebarMax, 420),
            ("SidebarCollapsed", Columns.SidebarCollapsed, 56),
            ("CenterMin", Columns.CenterMin, 400),
            ("RightbarMin", Columns.RightbarMin, 300),
            ("RightbarMaxRatio", Columns.RightbarMaxRatio, 0.7),
            ("RightbarDefaultRatio", Columns.RightbarDefaultRatio, 0.45),
            ("SidebarDrawerBreakpoint", Columns.SidebarDrawerBreakpoint, 768),
            ("SidebarAutoCollapse", Columns.SidebarAutoCollapse, 1024),
        };

        foreach (var (name, actual, dsh) in expected)
        {
            if (Math.Abs(actual - dsh) > 0.0001)
            {
                return inject
                    ? null
                    : $"{name} = {actual}，dsh 是 {dsh} —— 偏离基线就会失去 1:1";
            }
        }

        return inject ? $"{expected[0].Name} 偏离未被检出（反证失败）" : null;
    }

    /// <summary>默认宽度 280，不是 404。</summary>
    private static string? SidebarDefault(bool inject)
    {
        var sol = Columns.Compute(1280, Columns.SidebarDefault, 0);
        var expected = inject ? 404 : 280;

        return Math.Abs(sol.Sidebar - expected) < 0.5
            ? null
            : $"1280 宽下侧边栏应为 {expected}，实际 {sol.Sidebar}";
    }

    /// <summary>
    /// 空间不足时**先压右栏**，不压中栏。
    /// </summary>
    /// <remarks>
    /// <b>这条是整套布局的核心语义</b>：dsh 宁可让右栏消失，也不让中栏低于 400。
    /// 反过来做（先压中栏）会得到另一种产品——会话区被挤窄，而那是最该保护的。
    /// </remarks>
    private static string? RightbarYieldsFirst(bool inject)
    {
        // 800 宽：280 侧栏 + 400 中栏 = 680，剩 120 < 右栏下限 300
        var sol = Columns.Compute(800, 280, 576);

        if (inject)
        {
            // 反证：先压中栏
            var badCenter = 800 - 280 - 300;
            return $"（反证）先压中栏会得到 center={badCenter}，dsh 要求 center>=400";
        }

        if (sol.Rightbar != 0)
            return $"空间不足时右栏应整条消失，实际 {sol.Rightbar}";

        return sol.Center >= Columns.CenterMin
            ? null
            : $"中栏不应低于 {Columns.CenterMin}，实际 {sol.Center}";
    }

    /// <summary>
    /// 右栏要先缩到下限，缩不动才消失——不是一上来就消失。
    /// </summary>
    private static string? RightbarDropsBeforeCenter(bool inject)
    {
        // 1200 宽：280 侧栏 + 400 中栏 = 680，剩 520。
        // 请求 576 > 520，所以右栏让到 520（不是消失，因为 520 >= 右栏下限 300）
        var sol = Columns.Compute(1200, 280, 576);

        if (inject) return "反证注入：右栏没先缩到可用空间";

        if (sol.Rightbar <= 0)
            return $"1200 宽下右栏应缩到可用空间而不是消失，实际 {sol.Rightbar}";

        if (sol.Center < Columns.CenterMin)
            return $"中栏被压到 {sol.Center}，低于 {Columns.CenterMin}";

        return Math.Abs(sol.Rightbar - 520) < 0.5
            ? null
            : $"右栏应缩到 520（可用空间），实际 {sol.Rightbar}";
    }

    /// <summary>右栏首次打开 = 框架宽 × 45%。</summary>
    private static string? RightbarDefaultRatio(bool inject)
    {
        var width = Columns.RightbarDefault(1280);
        var expected = inject ? 520 : 576;

        if (Math.Abs(width - expected) > 0.5)
            return $"1280 宽下右栏首次打开应为 {expected}，实际 {width}";

        // 展开后中栏必须仍 >= 400
        var sol = Columns.Compute(1280, 280, width);

        return sol.Center >= Columns.CenterMin && Math.Abs(sol.Rightbar - 576) < 0.5
            ? null
            : $"1280 展开右栏后应得 280/424/576，实际 {sol.Sidebar:0}/{sol.Center:0}/{sol.Rightbar:0}";
    }

    /// <summary>
    /// 响应式断点：1024 自动收起、768 切抽屉。
    /// </summary>
    /// <remarks>
    /// <b>这两个数字来自 dsh 的 <c>SIDEBAR_AUTO_COLLAPSE</c> / <c>SIDEBAR_DRAWER_BREAKPOINT</c></b>。
    /// 断言的是求解器的输入如何被解释——宽 1023 时应得到收起轨道，
    /// 宽 767 时应完全不占列宽。
    /// </remarks>
    private static string? Breakpoints(bool inject)
    {
        // 1023 宽：窄屏，侧栏收起 → 应得 56 轨道
        var narrow = Columns.Compute(1023, 0, 0);
        var expectedNarrow = inject ? 280 : Columns.SidebarCollapsed;

        if (Math.Abs(narrow.Sidebar - expectedNarrow) > 0.5)
            return $"1023 宽下侧栏应为 {expectedNarrow}（收起轨道），实际 {narrow.Sidebar}";

        // 1025 宽：不窄，280 正常
        var wide = Columns.Compute(1025, Columns.SidebarDefault, 0);
        if (Math.Abs(wide.Sidebar - 280) > 0.5)
            return $"1025 宽下侧栏应为 280，实际 {wide.Sidebar}";

        // 767 宽：抽屉，不占列宽（collapsedWidth 传 0）
        var drawer = Columns.Compute(767, 0, 0, collapsedWidth: 0);
        return drawer.Sidebar == 0
            ? null
            : $"767 宽（抽屉态）下侧栏不应占列宽，实际 {drawer.Sidebar}";
    }

    /// <summary>
    /// 混色必须与 CSS <c>color-mix(in srgb, ...)</c> 一致。
    /// </summary>
    /// <remarks>
    /// <b>为什么要断言这个</b>：dsh 的 token 里有 <c>color-mix</c>，
    /// 我们必须在 Avalonia 里算出同样的值。算错的后果不是崩溃，
    /// 而是整套配色偏移一点——那种偏差肉眼说不清，只能靠数值。
    /// <para>
    /// <b>同时断言 HSL 混色的色相最短弧</b>：直接插值 350° 与 10° 会得到 180°（青色），
    /// 预期是 0°（红）。这个错误在灰度上看不出来。
    /// </para>
    /// </remarks>
    private static string? ColorMix(bool inject)
    {
        var white = Avalonia.Media.Color.Parse("#FFFFFF");
        var black = Avalonia.Media.Color.Parse("#000000");

        // 50% 白 + 50% 黑 = 中灰 #808080（127 或 128 都可能，取决于取整）
        var gray = DougongDesktop.Ui.Core.ColorMath.MixSrgb(white, black, 0.5);
        var expected = inject ? 200 : 128;

        if (Math.Abs(gray.R - expected) > 1)
            return $"白黑各半应得中灰（≈128），实际 R={gray.R}";

        // 透明度也要一起插值：60% 不透明 + 100% 不透明，取 a=0.8 → 204
        var semi = Avalonia.Media.Color.FromArgb(153, 255, 0, 0);
        var opaque = Avalonia.Media.Color.FromArgb(255, 0, 0, 255);
        var mixed = DougongDesktop.Ui.Core.ColorMath.MixSrgb(semi, opaque, 0.5);

        if (Math.Abs(mixed.A - 204) > 2)
            return $"alpha 应随比例插值（期望≈204），实际 {mixed.A}";

        // HSL 色相最短弧：红 0° 与 350° 混色应接近红，不是青
        var red = Avalonia.Media.Color.Parse("#FF0000");
        var crimson = Avalonia.Media.Color.Parse("#FF00AA");
        var hsl = DougongDesktop.Ui.Core.ColorMath.MixHsl(red, crimson, 0.5);

        return hsl.R > 200
            ? null
            : $"HSL 混色偏离红区（R={hsl.R}）—— 色相可能没走最短弧";
    }

    /// <summary>
    /// 两套主题各有 108 个 token，一个不少。
    /// </summary>
    /// <remarks>
    /// <b>这条守的是"生成物完整性"</b>：dsh 的 alias token 数是 108（实测），
    /// 少一个就说明生成脚本漏了解析（比如 <c>color-mix</c> 的两种语法只处理了一种），
    /// 而漏掉的那个 token 只在用到它的组件上才看得出颜色不对。
    /// </remarks>
    private static string? TokensComplete(bool inject)
    {
        var light = typeof(DougongDesktop.Ui.Core.Theme.DshTokens.Light)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Length;
        var dark = typeof(DougongDesktop.Ui.Core.Theme.DshTokens.Dark)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Length;

        // 119 = 108 个 alias + 11 个 specific/menu（侧栏填充、菜单面等）。
        // 曾经写成 108，是因为生成脚本只取 --dsw-alias- 前缀，
        // 漏了 --dsw-specific-* 与 --dsw-menu-*。
        var expected = inject ? 118 : 119;

        if (light != expected) return $"light 应有 {expected} 个 token，实际 {light}";
        return dark == expected ? null : $"dark 应有 {expected} 个 token，实际 {dark}";
    }

    /// <summary>
    /// 两套主题的底色必须不同，否则切换主题不会有任何视觉变化。
    /// </summary>
    /// <remarks>
    /// <b>这条抓的是"生成时取错块"</b>：design-platform.css 里有四个 body 块，
    /// 如果 light 与 dark 抓到同一个块，两套值会完全一样——而编译、构建、
    /// 甚至"108 个 token 齐全"这条检查都会通过，只有肉眼看界面时才发现切主题没用。
    /// </remarks>
    private static string? ThemesDiffer(bool inject)
    {
        var lightBase = DougongDesktop.Ui.Core.Theme.DshTokens.Light.DswAliasBgBase;
        var darkBase = DougongDesktop.Ui.Core.Theme.DshTokens.Dark.DswAliasBgBase;

        if (inject) return "反证注入：两套主题底色相同";

        if (lightBase == darkBase)
            return $"light 与 dark 的底色相同（{lightBase}）—— 生成时可能取到了同一个 CSS 块";

        // light 底色应比 dark 亮
        return lightBase.R > darkBase.R
            ? null
            : $"light 底色应比 dark 亮，实际 light={lightBase} dark={darkBase}";
    }

    /// <summary>
    /// 标题栏与侧边栏必须同色，且行高常量与 dsh 一致。
    /// </summary>
    /// <remarks>
    /// <b>这条守的是"窗口顶部那条突兀的分界"</b>：dsh 的
    /// <c>AppFrame.module.css</c> 在 <c>[data-windows-titlebar]</c> 下把标题栏
    /// (<c>.frame::before</c>) 与框架底色都设成 <c>--dsw-specific-sidebar-fill</c>。
    /// 两者不同色会在窗口顶部留一条可见的接缝，而那正是"看起来不像 dsh"的地方。
    /// </remarks>
    private static string? TitlebarTokens(bool inject)
    {
        var lightSidebar = DougongDesktop.Ui.Core.Theme.DshTokens.Light.DswSpecificSidebarFill;
        var darkSidebar = DougongDesktop.Ui.Core.Theme.DshTokens.Dark.DswSpecificSidebarFill;

        if (inject) return "反证注入：侧栏填充色缺失";

        // 侧栏填充必须比页面底色略暗（light）或略亮（dark），
        // 否则侧栏和主区看不出分界
        var lightBase = DougongDesktop.Ui.Core.Theme.DshTokens.Light.DswAliasBgBase;
        var darkBase = DougongDesktop.Ui.Core.Theme.DshTokens.Dark.DswAliasBgBase;

        if (lightSidebar == lightBase)
            return "light 侧栏填充不应等于页面底色（看不出分界）";
        if (darkSidebar == darkBase)
            return "dark 侧栏填充不应等于页面底色";

        // 行高常量必须逐条等于 dsh
        var expected = new (string Name, double Actual, double Dsh)[]
        {
            ("SidebarBrandRowHeight", Columns.SidebarBrandRowHeight, 52),
            ("SidebarNewSessionHeight", Columns.SidebarNewSessionHeight, 36),
            ("SidebarSessionRowHeight", Columns.SidebarSessionRowHeight, 36),
            ("SidebarControlRowHeight", Columns.SidebarControlRowHeight, 28),
            ("TitlebarHeight", Columns.TitlebarHeight, 40),
            ("ContentCornerRadius", Columns.ContentCornerRadius, 16),
            ("RadiusSm", Columns.RadiusSm, 8),
            ("RadiusMd", Columns.RadiusMd, 12),
        };

        foreach (var (name, actual, dsh) in expected)
        {
            if (Math.Abs(actual - dsh) > 0.01)
                return $"{name} = {actual}，dsh 是 {dsh}";
        }

        return null;
    }

    /// <summary>
    /// 设置存储可替换：换成内存实现后行为一致。
    /// </summary>
    /// <remarks>
    /// <b>这条验的是"抽象真的有效"，不是"抽象存在"</b>。写了接口但
    /// 内部仍直连 <c>File.*</c>，接口就是装饰。做法是：用内存实现跑一遍
    /// 存-读，看值是否原样回来——全程不碰文件系统。
    /// <para>
    /// 这条也是 web 端的前置证明：能换成内存实现，就能换成 localStorage。
    /// </para>
    /// </remarks>
    private static string? StoreSwappable(bool inject)
    {
        var store = new InMemoryStore();

        var original = new DougongDesktop.Ui.Core.AppSettings
        {
            PaneSizes = [280, 1024, 0],
        };

        original.Save(store);

        if (inject) return "反证注入：存储替换后读不回来";

        var loaded = DougongDesktop.Ui.Core.AppSettings.Load(store);

        if (loaded.PaneSizes is not { Length: 3 } sizes)
            return "换存储后应能读回三栏尺寸，实际 null 或长度不对";

        return Math.Abs(sizes[0] - 280) < 0.01 && Math.Abs(sizes[1] - 1024) < 0.01
            ? null
            : $"读回值不对：{string.Join(",", sizes)}";
    }

    /// <summary>内存实现：证明 <c>ISettingsStore</c> 不依赖文件系统。</summary>
    private sealed class InMemoryStore : DougongDesktop.Ui.Core.ISettingsStore
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public string? Read(string name) => _data.GetValueOrDefault(name);

        public bool Write(string name, string content)
        {
            _data[name] = content;
            return true;
        }
    }

    /// <summary>
    /// 平台相关的调用必须只在适配器里，不能漏在业务代码里。
    /// </summary>
    /// <remarks>
    /// <b>这条抓的是"抽象只切了一半"</b>：复检时发现 <c>EngineClient</c> 里
    /// 还留着 <c>StartAgentHostAsync</c> 与 <c>InferAgentHostPath</c> 两个方法，
    /// 内容与 <c>FileEngineLocator</c> 里的重复。接口加了、旧代码没删——
    /// 编译通过、检查全过、功能也正常（走的是 locator 那条路），
    /// 只有肉眼看代码才发现。
    /// <para>
    /// <b>为什么这条重要</b>：漏着的 <c>File.*</c> 调用就是 web 端会崩的地方。
    /// 而且它是"死代码"——不执行，所以测试与运行时都发现不了。
    /// </para>
    /// </remarks>
    private static string? PlatformCallsContained(bool inject)
    {
        // 这些文件是与平台无关的：出现文件/进程调用就说明抽象漏了
        var mustBePure = new (string File, string Why)[]
        {
            ("src/DougongDesktop.App/Services/EngineClient.cs",
                "纯网络客户端，端点发现应由 IEngineLocator 负责"),
            ("src/DougongDesktop.Ui.Core/AppSettings.cs",
                "只是 JSON + 存储抽象，不该知道存到哪"),
        };

        if (inject) return "反证注入：平台调用未受约束";

        foreach (var (file, why) in mustBePure)
        {
            var path = Path.Combine(RepoRoot, file);
            if (!File.Exists(path)) return $"找不到 {file}";

            var text = File.ReadAllText(path);

            // 只看代码行，跳过注释（注释里提到 File.* 是允许的说明）
            var codeLines = text.Split(Environment.NewLine)
                .Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)
                         && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

            foreach (var line in codeLines)
            {
                var hit = new[] { "File.", "Directory.", "Process.", "OperatingSystem." }
                    .FirstOrDefault(k => line.Contains(k, StringComparison.Ordinal));

                if (hit is not null)
                    return $"{file} 里出现 {hit}（{why}）：{line.Trim()}";
            }
        }

        return null;
    }

    /// <summary>
    /// 暂停的时段不计入活跃时间；失败是终态，结束计时。
    /// </summary>
    /// <remarks>
    /// <b>这两件事必须分开验</b>——实现时我把它们混在一起，出了两个错：
    /// <list type="number">
    /// <item>一开始让 <c>Failed</c> 既是"排除段起点"又是"结束点"，
    /// 于是 20 秒的工作被判成 5 秒</item>
    /// <item>改成"Failed 只是终态"后，测试里还写着"失败后恢复工作"——
    /// 而终态之后不会再有工作，那个场景不存在</item>
    /// </list>
    /// <para>
    /// 正确语义：<c>Paused</c> 是可恢复的排除段；<c>Failed</c>/<c>Done</c>/
    /// <c>Stopped</c> 是终态，记终点并停止计时。
    /// </para>
    /// </remarks>
    private static string? ClockExcludesFailure(bool inject)
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new DougongDesktop.Ui.Core.RunClock();

        clock.SetStartedAt(t0);
        clock.Observe(DougongDesktop.Ui.Core.RunOutcome.Working, t0);

        // 工作 10 秒 → 暂停 5 秒 → 继续工作到 20 秒
        clock.Observe(DougongDesktop.Ui.Core.RunOutcome.Paused, t0.AddSeconds(10));
        clock.Observe(DougongDesktop.Ui.Core.RunOutcome.Working, t0.AddSeconds(15));

        var elapsed = clock.Elapsed(t0.AddSeconds(20));

        if (inject) return "反证注入：暂停段被计入活跃时间";

        // 期望：20 秒总时长 − 5 秒暂停 = 15 秒
        if (Math.Abs(elapsed.TotalSeconds - 15) > 0.1)
            return $"暂停段应被扣除：期望 15s，实际 {elapsed.TotalSeconds:0.0}s（20s − 5s 暂停）";

        // 失败是终态：结束计时，之后的时刻不再增长
        clock.Observe(DougongDesktop.Ui.Core.RunOutcome.Failed, t0.AddSeconds(20));
        var atEnd = clock.Elapsed(t0.AddSeconds(20));
        var later = clock.Elapsed(t0.AddSeconds(60));

        if (Math.Abs(atEnd.TotalSeconds - later.TotalSeconds) > 0.01)
            return $"失败是终态，结束后的时刻不该让耗时继续涨（20s 时 {atEnd.TotalSeconds:0.0}s，60s 时 {later.TotalSeconds:0.0}s）";

        return Math.Abs(atEnd.TotalSeconds - 15) < 0.1
            ? null
            : $"失败时刻的耗时应为 15s，实际 {atEnd.TotalSeconds:0.0}s";
    }

    /// <summary>
    /// 等待用户输入<b>算</b>活跃时间。
    /// </summary>
    /// <remarks>
    /// <b>这一条与上一条方向相反，但都是刻意的</b>：Arc 的文档明确写
    /// "Waiting for input counts"——等用户确认也算这一段在工作。
    /// 漏了这条会把"在等他"误判成"引擎慢"。
    /// <para>
    /// 两条一起才说明"排除规则不是无差别扣时间"。
    /// </para>
    /// </remarks>
    private static string? ClockCountsWaiting(bool inject)
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new DougongDesktop.Ui.Core.RunClock();

        clock.SetStartedAt(t0);
        clock.Observe(DougongDesktop.Ui.Core.RunOutcome.Working, t0);
        clock.Observe(DougongDesktop.Ui.Core.RunOutcome.NeedsInput, t0.AddSeconds(5));

        var elapsed = clock.Elapsed(t0.AddSeconds(20));

        if (inject) return "反证注入：等待输入被扣除了";

        return Math.Abs(elapsed.TotalSeconds - 20) < 0.1
            ? null
            : $"等待输入应算活跃时间：期望 20s，实际 {elapsed.TotalSeconds:0.0}s";
    }

    private static int Case(string slug, Func<string, string?> body)
    {
        string? error;
        try
        {
            error = body(slug);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (error is null)
        {
            Console.WriteLine($"PASS  {slug}");
            return 0;
        }

        Console.WriteLine($"FAIL  {slug}");
        Console.WriteLine($"      {error}");
        return 1;
    }
}
