using System.Text.RegularExpressions;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// 包版本约束的检查：Arc 与 Avalonia 必须成对升级。
/// </summary>
/// <remarks>
/// <b>为什么需要这条</b>：TKCode.Arc 1.0.0 的 nuspec 用 <c>[12.1.4]</c> 精确锁定
/// Avalonia，1.1.0 改成了 <c>[12.1.5, 13.0.0)</c> 区间。也就是说**版本约束的形态
/// 本身随 Arc 版本变化**——只升一边会 NU1605，而 NU1605 在增量构建时可能被
/// 缓存的 restore 结果掩盖，等到别人 clean 构建才炸。
/// <para>
/// 这条检查读的是 <c>Directory.Packages.props</c> 的实际文本，不是缓存的
/// restore 结果——所以它能在 CI 与干净环境下都给出同样的结论。
/// </para>
/// </remarks>
internal static class PackageVersionChecks
{
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
        failures += Case("arc-and-avalonia-version-pair", slug => VersionPair(breakMode == slug));
        return failures;
    }

    /// <summary>
    /// Arc 1.1.x 必须配 Avalonia ≥ 12.1.5；Arc 1.0.x 配 12.1.4。
    /// </summary>
    private static string? VersionPair(bool breakMode)
    {
        var path = Path.Combine(RepoRoot, "Directory.Packages.props");
        if (!File.Exists(path)) return "找不到 Directory.Packages.props";

        var text = File.ReadAllText(path);

        string? VersionOf(string package)
        {
            // 用 @"" 逐字字符串：正则里的 \s 要原样保留，
            // 而 """...""" 原始字面量在结尾连续引号处会报 CS8998。
            var pattern = @"<PackageVersion\s+Include=""" + Regex.Escape(package)
                        + @"""\s+Version=""([^""]+)""";
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }

        // 反证：故意要求一个不存在的包名，若断言仍"通过"说明它根本没在读文件
        if (breakMode)
        {
            var bogus = VersionOf("TKCode.Arc.DoesNotExist");
            return bogus is null ? "已确认断言在读真实文件（不存在的包取不到版本）" : null;
        }

        var arc = VersionOf("TKCode.Arc");
        var avalonia = VersionOf("Avalonia");

        if (arc is null) return "Directory.Packages.props 里没有 TKCode.Arc 的版本";
        if (avalonia is null) return "Directory.Packages.props 里没有 Avalonia 的版本";

        if (!Version.TryParse(arc.Split('-')[0], out var arcVersion))
            return $"TKCode.Arc 版本无法解析：{arc}";
        if (!Version.TryParse(avalonia.Split('-')[0], out var avaloniaVersion))
            return $"Avalonia 版本无法解析：{avalonia}";

        // 每个 Arc 大版本对 Avalonia 的下限（逐版核过 nuspec）：
        //   1.0.x → [12.1.4]（精确锁定）
        //   1.1.x → [12.1.5, 13.0.0)
        //   1.3.x → [12.1.6, 13.0.0)
        // 这张表随每次升级追加一行；写死"最新下限"会漏掉回落的情况。
        var floors = new (Version Arc, Version Avalonia, string Why)[]
        {
            (new Version(1, 3, 0), new Version(12, 1, 6), "1.3.0 的依赖是 [12.1.6, 13.0.0)"),
            (new Version(1, 1, 0), new Version(12, 1, 5), "1.1.0 的依赖是 [12.1.5, 13.0.0)"),
        };

        var floor = floors.FirstOrDefault(f => arcVersion >= f.Arc);
        if (floor.Avalonia is not null && avaloniaVersion < floor.Avalonia)
            return $"Arc {arc} 要求 Avalonia ≥ {floor.Avalonia}，当前是 {avalonia}"
                 + $"（{floor.Why}，低于会 NU1605）";

        // Arc 1.0 要求 Avalonia 恰好 12.1.4（精确锁定）
        if (arcVersion < new Version(1, 1, 0) && avaloniaVersion != new Version(12, 1, 4))
            return $"Arc {arc} 精确锁定 Avalonia 12.1.4，当前是 {avalonia}（会 NU1605）";

        // 八个 Arc 包必须同版本：混版会在运行时出现类型不匹配
        string[] packages =
        [
            "TKCode.Arc", "TKCode.Arc.Fonts", "TKCode.Arc.Special", "TKCode.Arc.Blocks",
            "TKCode.Arc.Terminal", "TKCode.Arc.Workflow", "TKCode.Arc.Icons.Lucide", "TKCode.Arc.Dock",
        ];

        foreach (var p in packages)
        {
            var v = VersionOf(p);
            if (v is null) return $"Directory.Packages.props 里没有 {p} 的版本";
            if (v != arc) return $"Arc 各包版本不一致：{p}={v}，TKCode.Arc={arc}";
        }

        // Avalonia 的四个包也必须同版本
        foreach (var p in new[] { "Avalonia.Desktop", "Avalonia.Themes.Fluent", "Avalonia.Fonts.Inter", "Avalonia.Headless" })
        {
            var v = VersionOf(p);
            if (v is null) return $"Directory.Packages.props 里没有 {p} 的版本";
            if (v != avalonia) return $"Avalonia 各包版本不一致：{p}={v}，Avalonia={avalonia}";
        }

        return null;
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
