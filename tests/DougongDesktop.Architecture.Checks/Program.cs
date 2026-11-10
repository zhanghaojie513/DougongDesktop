using System.Text.Json;
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

    /// <summary>
    /// 引擎包前缀——这些是"换 agent 框架"真正要换掉的东西。
    /// </summary>
    /// <remarks>
    /// <b>与 <see cref="EnginePackagePrefixes"/> 是同一条清单</b>，只是多了
    /// "契约包单独登记"这一层：
    /// <list type="bullet">
    /// <item><b>引擎包</b>（本清单）：跑 agent loop 的实现。任何非 Framework 层
    /// 解析出它们，换框架时就都要改。</item>
    /// <item><b>契约包</b>：只有消息类型。本仓库已不再依赖任何 SDK 契约包——
    /// 线上契约自持在 <c>src/DougongDesktop.Ipc/Contract.cs</c>。</item>
    /// </list>
    /// <para>
    /// 两者混成一个清单会得到一条测不出真话的检查：要么把契约包也禁掉
    /// （而 <c>Ipc</c> 的 <c>Protocol.cs</c> 明确论述过通用契约就该来自 SDK），
    /// 要么整个放行（那引擎泄漏就再也拦不住）。
    /// </para>
    /// </remarks>
    private static readonly string[] EnginePackagePrefixes =
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

        // **必须在任何检查之前注册**：SDK 的 JsonSerializerOptions 一旦
        // 被用过就冻结，此后 RegisterDerivedType 会抛 InvalidOperationException。
        // 真实进程在 Program 第一行做这件事，检查工程照做。
        //
        // 实测过一次：注册放在 DockChecks 内部（序列化之后），
        // SDK 直接抛"必须在首次序列化之前调用"——那条清晰的诊断
        // 比静默失败好得多，立刻就定位到了原因。
        DougongDesktop.Ipc.DesktopProtocol.RegisterMessages();

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

        // 读取循环的容错：一条认不出的消息不该让整条连接失效。
        failures += ReadLoopChecks.Run(breakMode);

        // P5：两段耗时、推理流隔离、计划全量替换
        failures += ToolChecks.Run(breakMode);

        // P6：审批闭环的安全语义
        failures += ApprovalChecks.Run(breakMode);

        // P7：右栏 dock 的路径边界与未实现标注
        failures += DockChecks.Run(breakMode);

        // P8a：三栏几何必须与 dsh columns.ts 一致
        failures += ColumnChecks.Run(breakMode);

        // P8g：中区会话域的几何与"别凭想象加功能"
        failures += ConversationChecks.Run(breakMode);

        // 事件投影（纯映射层，表驱动）
        failures += ProjectionChecks.Run(breakMode);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ARCHITECTURE: ALL PASS"
            : $"ARCHITECTURE: {failures} FAILURE(S)");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// App 不得引用任何**引擎** SDK 包或工程；契约包是已登记的例外。
    /// </summary>
    /// <remarks>
    /// 这是整个解耦的地基：UI 只依赖 IPC 契约，换掉 SDK 时 UI 一行不改。
    /// <para>
    /// <b>断言的是"一个 SDK 包都不许"</b>：<c>App</c> 用的那些类型
    /// （<c>ConversationView</c> 的条目 switch、<c>SettingsView</c> 的模型清单、
    /// <c>CommandPalette</c> 的会话摘要）现在全部来自 <c>DougongDesktop.Ipc</c>，
    /// 而那个工程是 <c>App</c> 自己的 <c>ProjectReference</c>，不在本检查的禁止范围内。
    /// 曾经这里有一个 <c>Dougong.HostProtocol</c> 的例外——契约搬进本仓库后它被删掉了。
    /// </para>
    /// <para>
    /// <b>读 <c>project.assets.json</c> 而不是 csproj</b>：从 props 注入的引用
    /// 在 csproj 文本里看不见，而那种注入正是这条检查曾经漏报的原因。
    /// 理由同 <see cref="IpcDependsOnNothingButBcl"/>。
    /// </para>
    /// </remarks>
    private static string? AppDoesNotReferenceSdk(bool injectViolation)
    {
        var project = "DougongDesktop.App";
        var csproj = Path.Combine(RepoRoot, "src", project, $"{project}.csproj");
        if (!File.Exists(csproj)) return $"找不到 {csproj}";

        var violations = new List<string>();

        // ① 还原后的包依赖：能看见 props 注入的引用。
        var declared = ResolvedPackages(project);
        if (declared is null)
            return $"{project} 没有还原结果（找不到或读不懂 obj/project.assets.json）。"
                 + "\n  先跑一次 dotnet restore / build —— 读不到依赖图时只能判失败。";

        foreach (var name in declared)
        {
            if (name.StartsWith("DougongDesktop.", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith("Dougong.", StringComparison.OrdinalIgnoreCase)) continue;

            violations.Add($"{name}（SDK 包）");
        }

        // ② csproj 文本：查跨仓工程引用。
        //
        // 这一项必须留在 csproj 上：跨仓 ProjectReference 若指向一个**没有还原成功**
        // 的路径，依赖图里根本不会出现它，反而查不到。
        // 而 "..\..\dougong\src\Dougong.Agent\..." 这种 Include 不以 "Dougong." 开头，
        // 只看前缀会漏 —— 所以按路径片段判断。
        var doc = XDocument.Load(csproj);
        foreach (var reference in doc.Descendants("PackageReference").Concat(doc.Descendants("ProjectReference")))
        {
            var include = (string?)reference.Attribute("Include") ?? string.Empty;

            var isSdkPath = include.Contains("dougong" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            || include.Contains("dougong/", StringComparison.OrdinalIgnoreCase)
                            || include.Contains(@"\dougong\", StringComparison.OrdinalIgnoreCase);

            if (isSdkPath) violations.Add($"{include}（跨仓工程引用）");
        }

        if (injectViolation) violations.Add("(反证注入) Dougong.Agent（引擎包）");

        if (violations.Count > 0)
        {
            return "App 不得引用任何 SDK 包："
                + string.Join("；", violations)
                + "\n  UI 与引擎只通过 DougongDesktop.Ipc 相接（实施方案 §3.2）；"
                + "\n  线上契约由本仓库自持（src/DougongDesktop.Ipc/Contract.cs），不来自 SDK";
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
    /// Ipc 只能依赖 BCL，**不得依赖任何 SDK 包**。
    /// </summary>
    /// <remarks>
    /// <b>这条检查曾经既读错了地方、也断言错了内容</b>：
    /// <list type="number">
    /// <item>读的地方错：只读 csproj 文本，而 <c>Dougong.HostProtocol</c> 来自
    /// <c>Directory.Build.props</c> 的全局引用——看不见，于是一直绿。</item>
    /// <item>断言的内容错：那时它允许"契约包"，理由是"通用契约归 SDK"。
    /// 后来那条理由被推翻了——契约现在自持，所以正确断言是 BCL 之外全不许。</item>
    /// </list>
    /// 两处都是"检查在说一件它没验证过的事"，这类错误比漏检查更难发现。
    /// <para>
    /// <b>现在断言什么</b>：
    /// <list type="number">
    /// <item>不得出现任何 <see cref="EnginePackagePrefixes"/> 里的引擎包</item>
    /// <item>不得出现任何 SDK 包（含契约包——契约已自持）</item>
    /// <item>不得出现其它未登记的非 BCL 包</item>
    /// </list>
    /// 第 2 条让"新增一个非 BCL 依赖"必须改这个文件，而不是悄悄通过。
    /// </para>
    /// <para>
    /// <b>读 <c>project.assets.json</c> 而不是 csproj</b>：见
    /// <see cref="ResolvedPackages"/> 的说明。这一改动本身就是修 bug——
    /// 旧读法看不见 props 注入的引用。
    /// </para>
    /// </remarks>
    private static string? IpcDependsOnNothingButBcl(bool injectViolation)
    {
        var project = "DougongDesktop.Ipc";
        var csproj = Path.Combine(RepoRoot, "src", project, $"{project}.csproj");
        if (!File.Exists(csproj)) return $"找不到 {csproj}";

        var declared = ResolvedPackages(project);
        if (declared is null)
            return $"{project} 没有还原结果（找不到或读不懂 obj/project.assets.json）。"
                 + "\n  先跑一次 dotnet restore / build——读不到依赖图时，"
                 + "这条检查既不能通过也不能失败，只能判失败。";

        var violations = new List<string>();

        foreach (var name in declared)
        {
            if (name.StartsWith("DougongDesktop.", StringComparison.OrdinalIgnoreCase))
                continue;

            var isEngine = name.StartsWith("Dougong.", StringComparison.OrdinalIgnoreCase);

            // 判失败的理由要分开写：引擎包是"越界"，其它包是"未登记"。
            // 混成一句话会让人以为随便加个包也算越界，然后去删检查。
            violations.Add(isEngine
                ? $"{name}（SDK 包，{project} 不得引用）"
                : $"{name}（未登记的非 BCL 依赖）");
        }

        if (injectViolation) violations.Add("(反证注入) Dougong.Agent（SDK 包）");

        if (violations.Count > 0)
        {
            return "Ipc 只允许依赖 BCL；实际声明："
                + string.Join("；", violations)
                + "\n  引擎包进 Ipc 会让「换 agent 框架」波及契约层；"
                + "\n  其它包要在 Program.cs 里显式登记并写明理由";
        }

        return null;
    }

    /// <summary>
    /// 读某个工程**自己声明**的包依赖（<c>projectFileDependencyGroups</c> 段）。
    /// </summary>
    /// <remarks>
    /// <b>为什么读这一段而不是 csproj</b>：<c>project.assets.json</c> 是还原产物，
    /// 它记录的是"这个工程最终声明了什么"——包括从 <c>Directory.Build.props</c>、
    /// <c>Directory.Packages.props</c>、targets 文件注入的引用。
    /// csproj 文本只记录"这个 csproj 写了什么"，两者不等价。
    /// <para>
    /// <b>本仓库正是靠这一点漏了 <c>Dougong.HostProtocol</c></b>：
    /// 它来自 <c>Directory.Build.props</c> 的全局 <c>PackageReference</c>，
    /// 旧版检查读 csproj 时完全看不见，于是 <c>Ipc</c> 明明解析出了 SDK 契约包，
    /// 检查却是绿的。
    /// </para>
    /// <para>
    /// <b>为什么读"声明"而不是 <c>libraries</c>（完整传递闭包）</b>：
    /// 传递闭包会把"我依赖 A、A 依赖 B"里的 B 也算成我的依赖。
    /// 对"这一层引入了什么"这个问题，声明集才是答案 ——
    /// 否则任何包只要间接带到 SDK，检查就会误报。
    /// </para>
    /// <para>
    /// <b>只保留 <c>type == package</c></b>：同一段里还有工程引用
    /// （<c>DougongDesktop.Ipc</c> 之类），混进来会把我们自己的工程报成违规包。
    /// 条目格式是 <c>Name &gt;= Version</c> 或 <c>Name/Version</c>，这里统一切掉版本。
    /// </para>
    /// <para>
    /// <b>读不到时返回 null 而不是空列表</b>：空列表意味着"没有依赖"，
    /// 会让检查静默通过；读不到必须是一个能被看见的失败。
    /// </para>
    /// </remarks>
    private static List<string>? ResolvedPackages(string project)
    {
        var path = Path.Combine(RepoRoot, "src", project, "obj", "project.assets.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("projectFileDependencyGroups", out var groups))
                return null;

            // 按 TFM 分组（"net10.0"、"任意框架" 之类）。本仓库只有一个 TFM，
            // 但仍按全部分组收集——多 TFM 时漏读一个分支就是漏判。
            var names = new List<string>();
            foreach (var group in groups.EnumerateObject())
            {
                foreach (var entry in group.Value.EnumerateArray())
                {
                    var text = entry.GetString();
                    if (string.IsNullOrEmpty(text)) continue;

                    // "/" 用于传递依赖的写法，" >= " 用于声明。两种都切。
                    var cut = text.IndexOfAny([' ', '/', '>', '=']);
                    var name = cut > 0 ? text[..cut] : text;

                    // 工程引用形如 DougongDesktop.Ipc：不带 ">= "，但会出现在
                    // 别的分组里；这里按"是否是 package"过滤不了（该段不标 type），
                    // 所以改用前缀判断——本仓库自己的工程全以 DougongDesktop. 开头。
                    if (name.StartsWith("DougongDesktop.", StringComparison.OrdinalIgnoreCase))
                        continue;

                    names.Add(name);
                }
            }

            return names;
        }
        catch (JsonException)
        {
            // 还原文件损坏/正在写：当作读不到，交给调用方判失败。
            return null;
        }
        catch (IOException)
        {
            return null;
        }
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
    /// 引用**引擎** SDK 的文件数不能超出预算。
    /// </summary>
    /// <remarks>
    /// <b>防的是"引擎类型泄漏到别处"</b>：换掉 agent 框架时只有 Framework 需要重写
    /// （约 800 行）。一旦引擎类型出现在别的层，那个估算就不成立了——
    /// 而且这种扩散是渐进的：每次只多一个文件，没人会注意到，
    /// 直到某天要换框架时发现到处都要改。
    /// <para>
    /// <b>这条检查覆盖的是 SDK 包。</b> 我们自持的契约
    /// 的类型本来就在 Ipc / App 层用——但那些类型现在是**我们自己的**
    /// （<c>DougongDesktop.Ipc</c>），不是 SDK 的，所以本预算管不到它们。
    /// 全局 using 指向 <c>DougongDesktop.Ipc</c>，源码里不会出现 <c>using Dougong.</c>。
    /// </para>
    /// <para>
    /// <b>它数的是源码文本，不是还原结果</b>，所以看不见全局 using 带来的类型使用。
    /// 这是刻意的：本检查管的是"哪些文件显式认 SDK"，与
    /// <see cref="AppDoesNotReferenceSdk"/> / <see cref="IpcDependsOnNothingButBcl"/>
    /// 的"还原出了什么包"是两个不同的问题，两者都需要。
    /// </para>
    /// <para>
    /// 预算按当前实际值锁定。新增引用必须显式调高这个数字，
    /// 那个动作本身就是一次"要不要这么做"的复审。
    /// </para>
    /// </remarks>
    private static string? SdkFilesStayContained(bool inject)
    {
        // 当前预算：Framework 6 个文件（实测值）。
        //
        // 2026-10-30 从 4 调到 5：新增 EventProjection.cs（事件投影纯映射层），
        // 它就是"认识 SDK 事件类型"的那一层，必须引用 SDK。
        // 这次调高经过了确认——这正是这个预算存在的意义：
        // 每次扩散都要显式做一次"要不要这么做"的判断。
        //
        // 2026-10-30 从 5 调到 6：新增 WorkspaceDiff.cs（文件改动比对）。
        // 复审结论：**该引用**。理由是 SDK 的 FileVersionSet.Diff 只报第一个
        // 变化（实测：改 5 个文件只输出 1 行），必须自己比 FileVersion 的字段；
        // 而那段比对逻辑需要被检查工程断言，检查工程只引用 Framework 不引用
        // 宿主。放到宿主里就无法被测——"不可测的正确"等于没有保证。
        // 2026-11-10 从 6 调到 7：新增 PermissionProjection.cs。
        // 复审结论：**该引用**。它把"权限预设的 SDK 形状 → 协议形状"这条映射
        // 收在一处。若不放在 Framework，AgentHost 就要直接引用 SDK 包——
        // 那会让 AgentHost 从"只认 Ipc + Framework"变成认识 SDK 类型，
        // 与 SessionRuntime / EngineOptions 的既有分层相反。
        // 放在 Framework 的额外好处：能被检查工程断言（它只引用 Framework）。
        // 2026-11-10 从 7 调到 8：新增 SubagentProjection.cs（C8）。
        // 复审结论：**该引用**，理由与 PermissionProjection 相同——
        // 把"子 agent 的 SDK 形状 → 协议形状"收在 Framework，
        // 否则 AgentHost 要直接引用 SDK 包，破坏既有的两层分层。
        // 状态映射复用了 SDK 的 ProtocolMapping（"已结束但无结果算取消还是完成"
        // 是一条只该有一份的判断），这一点也只能在引用 SDK 的层做。
        const int budget = 8;

        var violations = new List<string>();

        foreach (var project in new[] { "DougongDesktop.Ipc", "DougongDesktop.Ui.Core", "DougongDesktop.App" })
        {
            var dir = Path.Combine(RepoRoot, "src", project);
            if (!Directory.Exists(dir)) continue;

            // 排除 obj/ 与 bin/：`Directory.Build.props` 的全局 using 会在
            // 那里生成 `<Project>.GlobalUsings.g.cs`，它含 `using Dougong.HostProtocol;`。
            // 那是构建产物，不是我们的源码——把它算进来会让守卫在每个工程上都误报。
            var count = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase)
                         && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase))
                .Count(f => File.ReadAllText(f).Contains("using Dougong.", StringComparison.Ordinal)
                         || File.ReadAllText(f).Contains("Dougong.Agent", StringComparison.Ordinal)
                         || File.ReadAllText(f).Contains("Dougong.Hosting", StringComparison.Ordinal));

            if (count > 0) violations.Add($"{project}: {count} 个文件引用了 SDK");
        }

        var frameworkDir = Path.Combine(RepoRoot, "src", "DougongDesktop.Framework");

        // 同样要排除 obj/bin：全局 using 会在那里生成 GlobalUsings.g.cs，
        // 它含 `using Dougong.HostProtocol;` 而那是构建产物。
        static bool IsSource(string path) =>
            !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

        var frameworkCount = Directory.EnumerateFiles(frameworkDir, "*.cs", SearchOption.AllDirectories)
            .Where(IsSource)
            .Count(f => File.ReadAllText(f).Contains("using Dougong.", StringComparison.Ordinal));

        if (inject) violations.Add($"(反证注入) Framework: {budget + 1} 个文件");

        if (violations.Count > 0)
            return "引擎 SDK 类型只允许出现在 Framework：" + string.Join("；", violations);

        return frameworkCount <= budget
            ? null
            : $"Framework 引用引擎 SDK 的文件 {frameworkCount} 个，超出预算 {budget}。"
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
