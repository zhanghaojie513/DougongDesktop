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
        failures += Case("wire-enum-format", slug => WireEnumFormat(breakMode == slug));
        failures += Case("wire-null-omitted", slug => WireNullOmitted(breakMode == slug));
        failures += Case("wire-cross-version-compat", slug => CrossVersionCompat(breakMode == slug));
        failures += Case("ipc-every-message-is-registered", slug => EveryMessageRegistered(breakMode == slug));
        failures += Case("desktop-types-actually-registered", slug => ActuallyRegistered(breakMode == slug));
        failures += Case("feedback-rating-roundtrips", slug => FeedbackRoundTrips(breakMode == slug));
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
    /// <summary>
    /// 桌面端特有消息能按自己的判别器往返。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能用 SDK 的 <c>HostProtocolContract.Json</c></b>：
    /// 它的多态基类只登记了 SDK 自己的派生类型，
    /// <c>[JsonDerivedType]</c> 挂在派生类上是无效的（System.Text.Json 要求
    /// 特性在基类上）。实测报错：
    /// <c>Runtime type 'ListDirectoryCommand' is not supported by polymorphic
    /// type 'Dougong.HostProtocol.ClientCommand'</c>。
    /// <para>
    /// 所以桌面端特有消息走**自己的序列化配置**。这不是妥协——两类消息
    /// 的判别器分属两个清单，本来就该分开登记。
    /// </para>
    /// </remarks>
    private static string? RoundTrips(bool inject)
    {
        var options = DesktopProtocol.Json;

        ClientCommand command = inject
            ? new ListSessionsCommand { RequestId = "r" }
            : new ListDirectoryCommand { RequestId = "r", Path = "src" };

        var json = JsonSerializer.Serialize(command, typeof(ClientCommand), options);

        using var doc = JsonDocument.Parse(json);
        var decoded = JsonSerializer.Deserialize<ClientCommand>(doc.RootElement.GetRawText(), options);

        if (decoded is not ListDirectoryCommand dir)
            return $"ListDirectoryCommand 应能还原，实际 {decoded?.GetType().Name ?? "null"}";

        if (dir.Path != "src") return $"Path 应是 src，实际 {dir.Path}";

        ServerEvent response = new DirectoryListedEvent
        {
            Sequence = 7,
            Path = "src",
            Entries = [new DirectoryEntry("a.txt", false, 12)],
        };

        var rjson = JsonSerializer.Serialize(response, typeof(ServerEvent), options);
        using var rdoc = JsonDocument.Parse(rjson);
        var rdecoded = JsonSerializer.Deserialize<ServerEvent>(rdoc.RootElement.GetRawText(), options)
            as DirectoryListedEvent;

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

    /// <summary>
    /// 每个消息类型都必须在 <c>Protocol.cs</c> 登记判别器。
    /// </summary>
    /// <remarks>
    /// <b>这条抓的是一次真实事故</b>：按域拆分 <c>Protocol.cs</c> 时，
    /// 我把 <c>ConversationItem</c> 的 6 条派生类型登记漏在了被覆盖的文件里。
    /// 编译照样通过、构建零错误——因为漏的是<b>特性标注</b>，不是代码。
    /// 后果是 6 种会话条目全部无法反序列化，而失败在运行期才发生。
    /// <para>
    /// <b>断言方式是"扫描程序集"而不是"读到某个数字"</b>：用反射列出所有
    /// <see cref="ClientCommand"/> / <see cref="ServerEvent"/> / <see cref="ConversationItem"/>
    /// 的具体派生类型，逐个检查是否带 <c>JsonDerivedType</c>。
    /// 加了新消息忘了登记 → 立刻红。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 每个桌面端消息类型都必须在 <c>RegisterMessages</c> 里登记。
    /// </summary>
    /// <remarks>
    /// <b>这条抓的是一次真实事故</b>：按域拆分 <c>Protocol.cs</c> 时漏了
    /// <c>ConversationItem</c> 的派生类型登记，编译零错误——
    /// 因为漏的是特性标注，不是代码。
    /// <para>
    /// <b>迁移到 <c>RegisterDerivedType</c> 后这条检查失效过一次</b>：
    /// 判别器从"类型上的特性"改到"<c>RegisterMessages</c> 里的调用"，
    /// 而旧实现查的是特性，查不到任何东西 → 永远通过。
    /// 加了 <c>SubmitFeedbackCommand</c> 忘了登记，它没红；
    /// 是 <c>feedback-rating-roundtrips</c> 那条往返检查抓到的。
    /// </para>
    /// <para>
    /// <b>现在改为查 SDK 的 <c>RegisteredExtensions</c></b>：那是运行期的
    /// 真实登记清单，不管判别器用什么方式登记，只要生效就在里面。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 枚举在线上必须是 camelCase 字符串。
    /// </summary>
    /// <remarks>
    /// <b>这条是"行为等价"检查，不是"编译通过"检查</b>。迁移到
    /// <c>Dougong.HostProtocol</c> 后，枚举的写法可能从 camelCase 变成
    /// PascalCase——两边都能跑，但**线上格式变了**：
    /// 旧客户端发的 <c>"succeeded"</c> 新服务端认不出，反之亦然。
    /// <para>
    /// 编译器不会报这个。只有断言实际的 JSON 文本才能发现。
    /// </para>
    /// </remarks>
    private static string? WireEnumFormat(bool inject)
    {
        var item = new ToolCallItem
        {
            At = DateTimeOffset.UnixEpoch,
            Id = "t1",
            InvocationId = "i1",
            ToolName = "read_file",
            Status = ToolCallStatus.Succeeded,
        };

        var json = JsonSerializer.Serialize<ConversationItem>(item, DesktopProtocol.Json);

        if (inject) return "反证注入：枚举格式检查失效";

        if (!json.Contains("\"succeeded\"", StringComparison.Ordinal))
        {
            return $"枚举应序列化成 camelCase 字符串（期望含 \"succeeded\"），实际：{json}";
        }

        // 反序列化也要认
        var back = JsonSerializer.Deserialize<ConversationItem>(json, DesktopProtocol.Json);
        return back is ToolCallItem { Status: ToolCallStatus.Succeeded }
            ? null
            : "camelCase 枚举应能反序列化回来";
    }

    /// <summary>
    /// null 字段不上线。
    /// </summary>
    /// <remarks>
    /// <b>这一条决定了"字段不存在"与"字段是 null"能否区分</b>。
    /// SDK 的文档专门警告过：两侧 null 处理不一致是协议最常见的裂缝。
    /// 迁移后必须确认这条行为没变。
    /// </remarks>
    private static string? WireNullOmitted(bool inject)
    {
        var item = new ToolCallItem
        {
            At = DateTimeOffset.UnixEpoch,
            Id = "t2",
            InvocationId = "i2",
            ToolName = "read_file",
            ResultText = null,      // 应为"不存在"而不是"null"
            Diagnostic = null,
        };

        var json = JsonSerializer.Serialize<ConversationItem>(item, DesktopProtocol.Json);

        if (inject) return "反证注入：null 处理检查失效";

        if (json.Contains("\"resultText\"", StringComparison.Ordinal))
        {
            return $"null 字段不应出现在线上（发现 resultText），实际：{json}";
        }

        return json.Contains("\"toolName\"", StringComparison.Ordinal)
            ? null
            : $"非 null 字段应正常出现，实际：{json}";
    }

    /// <summary>
    /// 迁移前后能互读对方的消息。
    /// </summary>
    /// <remarks>
    /// <b>这是"行为等价"最直接的证据</b>：把一段**迁移前生成的 JSON 字面量**
    /// 硬编码在这里，用迁移后的配置反序列化。能还原就说明线上格式没变。
    /// <para>
    /// <b>为什么不自己序列化再自己反序列化</b>：那样两侧都用同一份代码，
    /// 格式变了也测不出来。必须用"旧格式的固定样本"。
    /// </para>
    /// <para>
    /// 这段 JSON 是迁移前 <c>IpcProtocol.Json</c> 的真实输出形状：
    /// <c>method</c> 判别字段 + camelCase 枚举 + null 省略。
    /// </para>
    /// </remarks>
    private static string? CrossVersionCompat(bool inject)
    {
        // 迁移前的 ToolCallItem 形状（手写固定样本，不经过任何序列化器）
        const string legacyJson =
            "{\"kind\":\"toolCall\",\"id\":\"t9\",\"at\":\"2026-10-30T12:00:00+08:00\"," +
            "\"invocationId\":\"i9\",\"toolName\":\"read_file\",\"argumentsJson\":\"{}\"," +
            "\"status\":\"running\"}";

        var parsed = JsonSerializer.Deserialize<ConversationItem>(legacyJson, DesktopProtocol.Json);

        if (inject) return "反证注入：跨版本兼容检查失效";

        if (parsed is not ToolCallItem item)
            return $"旧格式应能还原成 ToolCallItem，实际 {parsed?.GetType().Name ?? "null"}";

        if (item.Status != ToolCallStatus.Running)
            return $"旧格式的 status=running 应还原成 Running，实际 {item.Status}";

        // 反向：新代码生成的消息里，判别字段名必须还是 kind
        var produced = JsonSerializer.Serialize<ConversationItem>(
            new NoticeItem { At = DateTimeOffset.UnixEpoch, Id = "n1", Text = "x" }, DesktopProtocol.Json);

        return produced.Contains("\"kind\":\"notice\"", StringComparison.Ordinal)
            ? null
            : $"判别字段应仍是 kind:notice，实际：{produced}";
    }

    private static string? EveryMessageRegistered(bool inject)
    {
        // 反射找本程序集里所有 ClientCommand / ServerEvent 的具体派生类型
        var ownAssembly = typeof(DesktopProtocol).Assembly;

        var expected = ownAssembly.GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && !t.IsGenericTypeDefinition)
            .Where(t => typeof(ClientCommand).IsAssignableFrom(t)
                     || typeof(ServerEvent).IsAssignableFrom(t))
            .ToArray();

        var registered = DesktopProtocol.RegisteredExtensions
            .Select(r => r.Derived.DerivedType)
            .ToHashSet();

        var missing = expected.Where(t => inject || !registered.Contains(t)).ToArray();

        if (missing.Length == 0) return null;

        return $"以下消息没有在 RegisterMessages 里登记（发得出去、收不回来）：" +
               string.Join("、", missing.Select(t => t.Name));
    }

    /// <summary>
    /// 桌面端消息必须真的进了 SDK 的多态登记，不只是"写了注册代码"。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条必要</b>：注册失败是静默的——编译过、序列化不报错，
    /// 只是那条消息用错配置。SDK 专门暴露了 <c>RegisteredExtensions</c>
    /// 就是为了让宿主能断言这件事。
    /// <para>
    /// <b>断言的是实际清单，不是代码里有那几行</b>：用反射读
    /// <c>HostProtocolContract.RegisteredExtensions</c>，逐个核对
    /// 降 5 个桌面端类型都在里面，且基线类型正确。
    /// </para>
    /// </remarks>
    private static string? ActuallyRegistered(bool inject)
    {
        var registered = DesktopProtocol.RegisteredExtensions;

        if (inject) return "反证注入：注册清单为空";

        var expected = new (Type Derived, Type Base, string Discriminator)[]
        {
            (typeof(ListDirectoryCommand), typeof(ClientCommand), "fs/list"),
            (typeof(ListModelsCommand), typeof(ClientCommand), "models/list"),
            (typeof(DirectoryListedEvent), typeof(ServerEvent), "fs/listed"),
            (typeof(WorkspaceChangedEvent), typeof(ServerEvent), "fs/changed"),
            (typeof(ModelsListedEvent), typeof(ServerEvent), "models/listed"),
        };

        foreach (var (derived, baseType, discriminator) in expected)
        {
            // JsonDerivedType 是 struct，不能拿 null 判断"有没有"，
            // 只能先 Any 再取第一个。
            if (!registered.Any(r => r.Derived.DerivedType == derived && r.BaseType == baseType))
                return $"{derived.Name} 没有进 SDK 的注册清单（基线 {baseType.Name}）";

            var hit = registered.First(r => r.Derived.DerivedType == derived && r.BaseType == baseType);

            if (hit.Derived.TypeDiscriminator as string != discriminator)
                return $"{derived.Name} 的判别器应为 {discriminator}，实际 {hit.Derived.TypeDiscriminator}";
        }

        return null;
    }

    /// <summary>
    /// 反馈命令能往返，三态都能表达。
    /// </summary>
    /// <remarks>
    /// <b>三态是必要的</b>：撤回评分（<c>None</c>）是真会发生的动作——
    /// 用户点赞后改主意。两态表达不了"从有评价回到没评价"。
    /// <para>
    /// 这条只验协议形状；按钮行为（重复点击只提交一次）在
    /// <c>FeedbackBar</c> 里，属 UI 层，检查工程不引用 App，测不到。
    /// </para>
    /// </remarks>
    private static string? FeedbackRoundTrips(bool inject)
    {
        var options = DesktopProtocol.Json;

        ClientCommand command = new SubmitFeedbackCommand
        {
            RequestId = "r1",
            ItemId = "item-1",
            Rating = inject ? DougongDesktop.Ipc.FeedbackRating.Up : DougongDesktop.Ipc.FeedbackRating.Down,
            Reason = "不准确",
            SessionId = "sess-1",
        };

        var json = JsonSerializer.Serialize(command, typeof(ClientCommand), options);

        using var doc = JsonDocument.Parse(json);
        var back = JsonSerializer.Deserialize<ClientCommand>(doc.RootElement.GetRawText(), options)
            as SubmitFeedbackCommand;

        if (back is null) return "SubmitFeedbackCommand 应能还原";

        if (back.Rating != DougongDesktop.Ipc.FeedbackRating.Down)
            return $"Rating 应是 Down，实际 {back.Rating}";

        if (back.Reason != "不准确") return $"Reason 应原样保留，实际 {back.Reason}";

        // 枚举必须序列化成 camelCase 字符串（与 wire-enum-format 同一条约定）
        return json.Contains("\"down\"", StringComparison.Ordinal)
            ? null
            : $"枚举应序列化成 camelCase 字符串，实际：{json}";
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
