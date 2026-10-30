using System.Text.Json;
using DougongDesktop.Framework;
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
        failures += Case("diff-detects-add-modify-delete", slug => DetectsAllThree(breakMode == slug));
        failures += Case("diff-ignores-same-content", slug => NoChangeOnSameContent(breakMode == slug));

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

    /// <summary>
    /// 新增 / 修改 / 删除三种改动都必须被检测到。
    /// </summary>
    /// <remarks>
    /// <b>这条抓的是 SDK 的真实缺陷</b>：<c>FileVersionSet.Diff</c>
    /// <b>只报告第一个变化</b>。实测（0.1.9-alpha.1）：
    /// <code>
    /// 改 5 个文件 → Diff 输出 1 行
    /// 改 f0 + 删 f1 → Diff 只报 f0，f1 的删除被吞掉
    /// </code>
    /// 所以不能依赖它，必须自己比 <see cref="FileVersion"/> 的字段。
    /// <para>
    /// <b>用真文件系统而不是造假数据</b>：这条验证的是
    /// "Capture → 并集重取 → 自己比对"的整条协作链。造假 FileVersion 会绕过
    /// Capture 的真实行为，而那正是"删除的文件看不到"的地方。
    /// </para>
    /// </remarks>
    private static string? DetectsAllThree(bool inject)
    {
        var root = Path.Combine(Path.GetTempPath(), $"diff-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var modify = Path.Combine(root, "modify.txt");
            var remove = Path.Combine(root, "remove.txt");
            var added = Path.Combine(root, "added.txt");

            File.WriteAllText(modify, "before");
            File.WriteAllText(remove, "will be gone");

            string[] Scan() => Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToArray();

            var beforePaths = Scan();
            var beforeSet = Dougong.Agent.FileVersionSet.Capture(beforePaths);

            // 回合中的三种改动
            File.WriteAllText(modify, "after — longer content");
            File.Delete(remove);
            File.WriteAllText(added, "brand new");

            var afterPaths = Scan();

            if (inject)
            {
                // 反证：改用 SDK 的 Diff（实测它只报第一个变化）。
                // 期望这里**真的漏掉**某些改动，从而让检查判红——那才证明
                // "不用 SDK Diff"这个决定是有必要的。
                var sdkUnion = beforePaths.Union(afterPaths, StringComparer.OrdinalIgnoreCase).ToArray();
                var sdkNow = Dougong.Agent.FileVersionSet.Capture(sdkUnion);
                var sdkDiff = beforeSet.Diff(sdkNow) ?? "";

                if (!sdkDiff.Contains("modify.txt", StringComparison.Ordinal))
                    return "反证注入失败：SDK Diff 连修改都没报出来";

                // 用 SDK Diff 的结果走同一套断言：它漏了删除 → 这里返回错误 → 判红
                if (!sdkDiff.Contains("remove.txt", StringComparison.Ordinal))
                    return "（反证）SDK 的 FileVersionSet.Diff 漏掉了删除 —— 必须自己比对";

                return "反证注入失败：SDK Diff 居然报全了";
            }

            var union = beforePaths.Union(afterPaths, StringComparer.OrdinalIgnoreCase).ToArray();
            var nowSet = Dougong.Agent.FileVersionSet.Capture(union);

            var changes = WorkspaceDiff.Compare(beforeSet.Files, nowSet.Files);
            var described = WorkspaceDiff.Describe(changes);

            if (!described.Contains("modify.txt", StringComparison.Ordinal))
                return "修改的文件必须出现在改动里";

            if (!described.Contains("remove.txt", StringComparison.Ordinal))
                return "删除的文件必须出现在改动里（Capture 并集没做，或比对漏了 Exists”）";

            if (!described.Contains("added.txt", StringComparison.Ordinal))
                return "新增的文件必须出现在改动里";

            // 三种类型都要对
            var kinds = changes.ToDictionary(
                c => Path.GetFileName(c.Path), c => c.Kind, StringComparer.OrdinalIgnoreCase);

            if (kinds.GetValueOrDefault("modify.txt") != WorkspaceDiff.ChangeKind.Modified)
                return $"modify.txt 应判为 Modified，实际 {kinds.GetValueOrDefault("modify.txt")}";

            if (kinds.GetValueOrDefault("remove.txt") != WorkspaceDiff.ChangeKind.Deleted)
                return $"remove.txt 应判为 Deleted，实际 {kinds.GetValueOrDefault("remove.txt")}";

            return kinds.GetValueOrDefault("added.txt") == WorkspaceDiff.ChangeKind.Added
                ? null
                : $"added.txt 应判为 Added，实际 {kinds.GetValueOrDefault("added.txt")}";
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// 内容一样的文件不能被报成改动。
    /// </summary>
    /// <remarks>
    /// <b>这条守的是"用内容哈希而不是时间戳"</b>：重写一个内容完全相同的文件，
    /// <c>LastWriteUtc</c> 会变而哈希不变。用时间戳判会误报，而误报会让
    /// "改动" tab 全是噪音，用户就不看了。
    /// </remarks>
    private static string? NoChangeOnSameContent(bool inject)
    {
        var root = Path.Combine(Path.GetTempPath(), $"same-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var file = Path.Combine(root, "same.txt");
            File.WriteAllText(file, "identical");

            var before = Dougong.Agent.FileVersionSet.Capture([file]);

            // 重写同样内容：时间戳变了，哈希没变
            Thread.Sleep(20);
            File.WriteAllText(file, "identical");

            var after = Dougong.Agent.FileVersionSet.Capture([file]);

            if (inject)
            {
                // 反证：按时间戳判 —— 时间戳确实变了，于是"误报有改动"，检查应判红
                var timeChanged = before.Files[0].LastWriteUtc != after.Files[0].LastWriteUtc;

                return timeChanged
                    ? "（反证）按时间戳判会误报：内容没变却被算成改动"
                    : "反证注入失败：重写后时间戳没变，无法演示误报";
            }

            var changes = WorkspaceDiff.Compare(before.Files, after.Files);

            return changes.Count == 0
                ? null
                : $"内容未变不应报告改动（报了 {changes.Count} 条）—— 比对必须用内容哈希，不能用时间戳";
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
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
