using System.Xml.Linq;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// 架构守卫：断言 UI 与 SDK 互不耦合（实施方案 §3.2、§3.5）。
/// </summary>
/// <remarks>
/// <b>为什么这个仓库需要自己的守卫</b>：SDK 的 <c>ArchitectureBoundaryTests</c>
/// 只扫 <c>dougong/src</c> 和 <c>dougong/samples</c>，扫不到本仓库。
/// 也就是说桌面端直接 <c>ProjectReference</c> 到 <c>..\dougong\src\Dougong.Agent</c>
/// 时，SDK 那边一条测试都不会红——契约被悄悄破坏，还把桌面拉进 SDK 的构建图。
/// <para>
/// <b>反证</b>：每条检查都能用 <c>--break=&lt;名字&gt;</c> 故意注入违规，
/// 必须变红。从没红过的检查等于没有检查——这是 SDK <c>status.md</c> 反复强调的。
/// </para>
/// </remarks>
public static class Program
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>禁止 App 出现的前缀：SDK 包与工程。</summary>
    private static readonly string[] ForbiddenSdkPrefixes =
    [
        "Dougong.Agent",
        "Dougong.Ai",
        "Dougong.Mcp",
        "Dougong.Acp",
        "Dougong.Hosting",
        "Dougong.Kernel",
        "Dougong.Cli",
    ];

    /// <summary>SDK 侧工程禁止出现的 UI 技术栈（与 SDK 的边界测试同款）。</summary>
    private static readonly string[] ForbiddenUiPrefixes =
    [
        "Avalonia",
        "TKCode.Arc",
        "AtomUI",
    ];

    public static int Main(string[] args)
    {
        // 支持两种写法：--break=<名字> 和 --break <名字>
        var breakMode = ParseBreakMode(args);

        var failures = 0;

        failures += Check("app-does-not-reference-sdk", () => AppDoesNotReferenceSdk(breakMode == "app-does-not-reference-sdk"));
        failures += Check("sdk-side-has-no-ui-dependencies", () => SdkSideHasNoUiDependencies(breakMode == "sdk-side-has-no-ui-dependencies"));
        failures += Check("ipc-depends-on-nothing-but-bcl", () => IpcDependsOnNothingButBcl(breakMode == "ipc-depends-on-nothing-but-bcl"));
        failures += Check("sdk-files-stay-contained", () => SdkFilesStayContained(breakMode == "sdk-files-stay-contained"));

        // slot 框架的行为检查（九条）。这些错误全是"出错时看起来正常"类型，
        // 靠人工点击几乎不可能发现。
        failures += SlotChecks.Run(breakMode);

        // UI 插件加载器的行为检查
        failures += ExtensionChecks.Run(breakMode);

        // 序号缺口 → 重同步。P3 复检补的：原来只有检测没有处置。
        failures += ResyncChecks.Run(breakMode);

        // 事件投影（纯映射层，表驱动）
        failures += ProjectionChecks.Run(breakMode);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ARCHITECTURE: ALL PASS"
            : $"ARCHITECTURE: {failures} FAILURE(S)");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// App 不得引用任何 Dougong.* 包或工程。
    /// </summary>
    /// <remarks>
    /// 这是整个解耦的地基：UI 只依赖 IPC 契约，换掉 SDK 时 UI 一行不改。
    /// </remarks>
    private static string? AppDoesNotReferenceSdk(bool injectViolation)
    {
        var csproj = Path.Combine(RepoRoot, "src", "DougongDesktop.App", "DougongDesktop.App.csproj");
        if (!File.Exists(csproj)) return $"找不到 {csproj}";

        var doc = XDocument.Load(csproj);
        var violations = new List<string>();

        foreach (var reference in doc.Descendants("PackageReference").Concat(doc.Descendants("ProjectReference")))
        {
            var include = (string?)reference.Attribute("Include") ?? string.Empty;

            // 两种越界形态都要拦：
            // 1) 包引用 Dougong.Agent
            // 2) 跨仓工程引用 ..\..\dougong\src\Dougong.Agent\...
            //    第二种的 Include 不以 "Dougong." 开头，只看前缀会漏。
            var isSdkPackage = ForbiddenSdkPrefixes.Any(p => include.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            var isSdkPath = include.Contains("dougong" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            || include.Contains("dougong/", StringComparison.OrdinalIgnoreCase)
                            || include.Contains(@"\dougong\", StringComparison.OrdinalIgnoreCase);

            if (isSdkPackage || isSdkPath)
                violations.Add(include);
        }

        if (injectViolation) violations.Add("(反证注入) Dougong.Agent");

        if (violations.Count > 0)
        {
            return "App 不得引用 SDK："
                + string.Join("；", violations)
                + "\n  UI 与 SDK 只通过 DougongDesktop.Ipc 相接（实施方案 §3.2）";
        }

        return null;
    }

    /// <summary>
    /// Frame/Host/Ipc 三个非 UI 工程不得出现 Avalonia / TKCode.Arc。
    /// </summary>
    /// <remarks>
    /// 与 SDK 那边"UI 技术栈不进入 SDK"对称：这里保证"UI 技术栈不进入框架层"。
    /// </remarks>
    private static string? SdkSideHasNoUiDependencies(bool injectViolation)
    {
        var violations = new List<string>();

        foreach (var project in new[] { "DougongDesktop.Ipc", "DougongDesktop.Framework", "DougongDesktop.AgentHost" })
        {
            var csproj = Path.Combine(RepoRoot, "src", project, $"{project}.csproj");
            if (!File.Exists(csproj)) { violations.Add($"{project}: 找不到 csproj"); continue; }

            var doc = XDocument.Load(csproj);
            foreach (var package in doc.Descendants("PackageReference"))
            {
                var include = (string?)package.Attribute("Include") ?? string.Empty;
                if (ForbiddenUiPrefixes.Any(p => include.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    violations.Add($"{project} → {include}");
            }
        }

        if (injectViolation) violations.Add("(反证注入) DougongDesktop.Framework → Avalonia");

        if (violations.Count > 0)
        {
            return "框架层不得引用 UI 技术栈："
                + string.Join("；", violations)
                + "\n  这是 §3.5 两套插件体系的基础：UI 插件契约不进 SDK 侧";
        }

        return null;
    }

    /// <summary>
    /// Ipc 工程只能依赖 BCL——它是纯契约，连 SDK 都不该引。
    /// </summary>
    private static string? IpcDependsOnNothingButBcl(bool injectViolation)
    {
        var csproj = Path.Combine(RepoRoot, "src", "DougongDesktop.Ipc", "DougongDesktop.Ipc.csproj");
        if (!File.Exists(csproj)) return $"找不到 {csproj}";

        var doc = XDocument.Load(csproj);
        var refs = doc.Descendants("PackageReference")
            .Select(p => (string?)p.Attribute("Include") ?? string.Empty)
            .Where(n => n.Length > 0)
            .ToList();

        if (injectViolation) refs.Add("(反证注入) Dougong.Agent");

        if (refs.Count > 0)
        {
            return "Ipc 必须是纯契约，只能依赖 BCL；实际引用："
                + string.Join("；", refs)
                + "\n  一旦引入依赖，UI 与框架就不再只共享消息类型";
        }

        return null;
    }

    /// <summary>
    /// 解析反证开关。
    /// </summary>
    /// <remarks>
    /// <b>第一版这里有 bug 且差点没发现</b>：只认 <c>--break x</c>（两个 token），
    /// 而实际调用写的是 <c>--break=x</c>（一个 token），于是反证静默失效——
    /// 三条检查全部 PASS，看起来"守卫正常"，实际反证一次都没跑。
    /// <para>
    /// 这正是"从没红过的检查等于没有检查"的活标本。
    /// </para>
    /// </remarks>
    private static string? ParseBreakMode(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--break=", StringComparison.Ordinal))
                return args[i]["--break=".Length..];

            if (args[i] == "--break" && i + 1 < args.Length)
                return args[i + 1];
        }

        return null;
    }

    /// <summary>
    /// 引用 SDK 的文件数不能超出预算。
    /// </summary>
    /// <remarks>
    /// <b>防的是"SDK 类型泄漏到别处"</b>：换掉 agent 框架时只有 Framework 需要重写
    /// （约 800 行）。一旦 SDK 类型出现在别的层，那个估算就不成立了——
    /// 而且这种扩散是渐进的：每次只多一个文件，没人会注意到，
    /// 直到某天要换框架时发现到处都要改。
    /// <para>
    /// 预算按当前实际值锁定。新增引用必须显式调高这个数字，
    /// 那个动作本身就是一次"要不要这么做"的复审。
    /// </para>
    /// </remarks>
    private static string? SdkFilesStayContained(bool inject)
    {
        // 当前预算：Framework 5 个文件（实测值）。
        //
        // 2026-10-30 从 4 调到 5：新增 EventProjection.cs（事件投影纯映射层），
        // 它就是"认识 SDK 事件类型"的那一层，必须引用 SDK。
        // 这次调高经过了确认——这正是这个预算存在的意义：
        // 每次扩散都要显式做一次"要不要这么做"的判断。
        const int budget = 5;

        var violations = new List<string>();

        foreach (var project in new[] { "DougongDesktop.Ipc", "DougongDesktop.Ui.Core", "DougongDesktop.App" })
        {
            var dir = Path.Combine(RepoRoot, "src", project);
            if (!Directory.Exists(dir)) continue;

            var count = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Count(f => File.ReadAllText(f).Contains("using Dougong.", StringComparison.Ordinal)
                         || File.ReadAllText(f).Contains("Dougong.Agent", StringComparison.Ordinal)
                         || File.ReadAllText(f).Contains("Dougong.Hosting", StringComparison.Ordinal));

            if (count > 0) violations.Add($"{project}: {count} 个文件引用了 SDK");
        }

        var frameworkDir = Path.Combine(RepoRoot, "src", "DougongDesktop.Framework");
        var frameworkCount = Directory.EnumerateFiles(frameworkDir, "*.cs", SearchOption.AllDirectories)
            .Count(f => File.ReadAllText(f).Contains("using Dougong.", StringComparison.Ordinal));

        if (inject) violations.Add($"(反证注入) Framework: {budget + 1} 个文件");

        if (violations.Count > 0)
            return "SDK 类型只允许出现在 Framework：" + string.Join("；", violations);

        return frameworkCount <= budget
            ? null
            : $"Framework 引用 SDK 的文件 {frameworkCount} 个，超出预算 {budget}。"
              + "新增引用请同步调高预算——那个动作是复审的机会。";
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
        Console.WriteLine($"      {error.Replace("\n", "\n      ")}");
        return 1;
    }

    /// <summary>从可执行文件位置向上找含 <c>Directory.Build.props</c> 的目录。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上找不到仓库根（需要含 Directory.Build.props 与 src/）。");
    }
}
