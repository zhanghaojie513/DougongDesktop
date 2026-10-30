namespace DougongDesktop.Ipc;

// ─────────────────────────────────────────────────────────────
// 工作区：目录浏览与文件改动
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 列出工作区目录（右栏文件树用）。
/// </summary>
/// <remarks>
/// <b>为什么单独一条命令，不复用工具的 <c>list_directory</c></b>：那条是给
/// **模型**用的、会进模型上下文、受工具结果长度限制。右栏是给人看的浏览，
/// 两者对深度的需求不同，混用会让"模型看到的"和"用户看到的"互相牵制。
/// <para>
/// <b>只读</b>：右栏浏览不提供写操作，写文件仍然只能经过模型 + 审批。
/// 但那不等于"可以读工作区之外的任意位置"——宿主必须做边界判断。
/// </para>
/// </remarks>
public sealed record ListDirectoryCommand : ClientCommand
{
    /// <summary>相对工作区根的路径。空或 null 表示根目录。</summary>
    public string? Path { get; init; }
}

/// <summary>目录内容（响应 <see cref="ListDirectoryCommand"/>）。</summary>
public sealed record DirectoryListedEvent : ServerEvent
{
    /// <summary>请求的相对路径。根目录是空字符串。</summary>
    public required string Path { get; init; }

    /// <summary>条目。目录在前、按名称排序。</summary>
    public required IReadOnlyList<DirectoryEntry> Entries { get; init; }

    /// <summary>被截断时的说明；null 表示列全了。</summary>
    public string? Truncated { get; init; }

    /// <summary>列目录失败的原因；null 表示成功。</summary>
    public string? Error { get; init; }
}

/// <summary>文件树里的一个条目。</summary>
/// <param name="Name">文件名（不含路径）。</param>
/// <param name="IsDirectory">是否目录。</param>
/// <param name="Size">字节数；目录为 0。</param>
public sealed record DirectoryEntry(string Name, bool IsDirectory, long Size);

/// <summary>
/// 回合结束时的文件改动。
/// </summary>
/// <remarks>
/// <b>这里是"变更描述"而不是行级差异</b>：SDK 的 <c>FileVersion</c> 只保存
/// 长度、修改时间与内容哈希，<b>不保存文件原内容</b>，所以做不到
/// "第几行改成什么"。界面必须说清这一点，否则用户会以为 diff 视图坏了。
/// <para>
/// <b>比的是内容哈希而不是时间戳</b>：改了又改回去不会被误报，
/// 而 <c>LastWriteUtc</c> 会因精度与时钟漂移漏判。
/// </para>
/// <para>
/// <b>不在 SDK 的 <c>FileVersionSet.Diff</c> 上做</b>：实测它只报告第一个变化
/// （改 5 个文件只输出 1 行，同时改一个删一个会吞掉删除）。所以宿主自己
/// 比对 <c>FileVersion</c> 的字段，见 <c>WorkspaceDiff</c>。
/// </para>
/// </remarks>
public sealed record WorkspaceChangedEvent : ServerEvent
{
    /// <summary>变更描述。空串表示本回合没有改动。</summary>
    public required string Diff { get; init; }

    /// <summary>是否有改动。由 <see cref="Diff"/> 是否为空推得，单独给出便于界面判断。</summary>
    public required bool HasChanges { get; init; }
}
