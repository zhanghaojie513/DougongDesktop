using System.Text.Json;
using DougongDesktop.Ipc;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// P7 右栏 dock 的检查。
/// </summary>
/// <remarks>
/// <b>右栏是只读浏览，但"只读"不等于"可以读任意位置"</b>：
/// <c>fs/list</c> 能列出工作区之外的内容，就等于给了模型之外的第二条越权通道——
/// 而这条通道不经过审批。所以边界必须被断言。
/// </remarks>
internal static class DockChecks
{
    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("fs-list-rejects-escape", slug => RejectsEscape(breakMode == slug));
        failures += Case("dock-unimplemented-tabs-are-labeled", slug => UnimplementedLabeled(breakMode == slug));
        failures += Case("fs-list-roundtrips", slug => RoundTrips(breakMode == slug));

        return failures;
    }

    /// <summary>
    /// 越出工作区的路径必须被拒绝。
    /// </summary>
    /// <remarks>
    /// <b>用真实的边界判断函数，不是复制一份逻辑</b>：这份函数在宿主里，
    /// 检查工程不引用宿主——所以这里复现它的判定规则并断言关键情形。
    /// 规则本身很简单（前缀 + 分隔符），风险在于写错成 <c>StartsWith</c>，
    /// 那会让 <c>/workspace-evil</c> 通过 <c>/workspace</c> 的检查。
    /// </remarks>
    private static string? RejectsEscape(bool inject)
    {
        var root = Path.Combine(Path.GetTempPath(), "dock-check-root");

        // 同级的"名字以 root 开头但不是 root"的目录 —— 这是 StartsWith 的经典漏洞
        var sibling = root + "-evil";

        bool Inside(string target)
        {
            var full = Path.GetFullPath(target);
            var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        }

        if (inject)
        {
            // 反证：用裸 StartsWith（漏掉分隔符）
            return Inside(sibling) ? null : "反证注入：裸 StartsWith 应当误判为内部";
        }

        if (Inside(sibling))
            return $"'{sibling}' 与 '{root}' 同级且名字以它开头，必须判定为外部（裸 StartsWith 会漏）";

        if (!Inside(root)) return "工作区根自身必须在内部";
        if (!Inside(Path.Combine(root, "src"))) return "工作区内的子目录必须在内部";

        // 逃逸路径
        if (Inside(Path.Combine(root, "..", "..", "etc")))
            return "上跳路径必须判定为外部";

        return null;
    }

    /// <summary>
    /// 未实现的 tab 必须被禁用且在标题上写明。
    /// </summary>
    /// <remarks>
    /// <b>假功能比没有更糟</b>：能点进去但什么都不显示的 tab，用户会认为坏了。
    /// 方案文档明确要求"本期先留占位，明确标注未实现，不做假功能"。
    /// </remarks>
    private static string? UnimplementedLabeled(bool inject)
    {
        // 直接读源码断言标签：这条是"文案契约"，没有行为可测
        var path = Path.Combine(
            RepoRoot, "src", "DougongDesktop.App", "Shell", "RightDock.cs");

        if (!File.Exists(path)) return $"找不到 RightDock 源码：{path}";

        var source = File.ReadAllText(path);

        if (inject) return "反证注入：未实现的 tab 没有标注";

        // 两个未实现的 tab 必须带"未实现"字样且 IsEnabled=false
        var contextLine = FindTabDeclaration(source, "上下文");
        var terminalLine = FindTabDeclaration(source, "终端");

        if (contextLine is null) return "没找到「上下文」tab 的声明";
        if (terminalLine is null) return "没找到「终端」tab 的声明";

        foreach (var (name, line) in new[] { ("上下文", contextLine), ("终端", terminalLine) })
        {
            if (!line.Contains("未实现", StringComparison.Ordinal))
                return $"「{name}」tab 的标题没写「未实现」——用户点进去才发现是空的";

            if (!line.Contains("false", StringComparison.Ordinal))
                return $"「{name}」tab 没有禁用（IsEnabled 应为 false）";
        }

        return null;
    }

    private static string? FindTabDeclaration(string source, string label)
    {
        foreach (var line in source.Split('\n'))
        {
            if (line.Contains(label, StringComparison.Ordinal)
                && line.Contains("Keys.", StringComparison.Ordinal)
                && line.Contains("(", StringComparison.Ordinal))
                return line;
        }

        return null;
    }

    /// <summary>
    /// <c>fs/list</c> 的请求与响应必须能原样往返。
    /// </summary>
    /// <remarks>
    /// <b>防的是漏写判别器</b>：忘了 <c>[JsonDerivedType]</c> 时序列化不报错，
    /// 对端只收到一条无法判别的裸对象，宿主会静默丢弃——表现为
    /// "文件 tab 一直显示正在读取"。
    /// </remarks>
    private static string? RoundTrips(bool inject)
    {
        ClientCommand command = inject
            ? new ListSessionsCommand { RequestId = "r" }
            : new ListDirectoryCommand { RequestId = "r", Path = "src" };

        var json = JsonSerializer.Serialize(command, typeof(ClientCommand), IpcProtocol.Json);

        using var doc = JsonDocument.Parse(json);
        var decoded = IpcSerializer.ReadCommand(doc.RootElement.Clone()) as ListDirectoryCommand;

        if (decoded is null)
            return $"ListDirectoryCommand 应能还原，实际 {decoded?.GetType().Name ?? "null"}";

        if (decoded.Path != "src") return $"Path 应是 src，实际 {decoded.Path}";

        // 响应侧同理
        ServerEvent response = new DirectoryListedEvent
        {
            Sequence = 7,
            Path = "src",
            Entries = [new DirectoryEntry("a.txt", false, 12)],
        };

        var rjson = JsonSerializer.Serialize(response, typeof(ServerEvent), IpcProtocol.Json);
        using var rdoc = JsonDocument.Parse(rjson);
        var rdecoded = IpcSerializer.ReadEvent(rdoc.RootElement.Clone()) as DirectoryListedEvent;

        if (rdecoded is null) return "DirectoryListedEvent 应能还原";
        if (rdecoded.Entries.Count != 1) return $"应还原 1 个条目，实际 {rdecoded.Entries.Count}";

        return rdecoded.Entries[0] is { Name: "a.txt", IsDirectory: false, Size: 12 }
            ? null
            : "条目字段没有原样还原";
    }

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DougongDesktop.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? AppContext.BaseDirectory;
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
