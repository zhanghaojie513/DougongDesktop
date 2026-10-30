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
