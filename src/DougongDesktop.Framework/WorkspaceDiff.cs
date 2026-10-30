using Dougong.Agent;

namespace DougongDesktop.Framework;

/// <summary>
/// 比对两次 <see cref="FileVersionSet"/> 快照，得出本回合的完整改动。
/// </summary>
/// <remarks>
/// <b>为什么不用 SDK 的 <see cref="FileVersionSet.Diff"/></b>：实测它<b>只报告
/// 第一个变化</b>。五个文件全改了，它输出一行；同时改一个 + 删一个，它只报修改
/// 那一条，删除被吞掉。
/// <para>
/// 实测证据（SDK 0.1.9-alpha.1）：
/// <code>
/// 改 5 个文件 → Diff 输出 1 行
/// 改 f0 + 删 f1 → Diff 只报 f0，f1 的删除没有出现
/// </code>
/// 这**不是**它文档里说的语义（"Raised with each pane's share"那类全量描述），
/// 所以在 SDK 修好之前不能依赖它。
/// </para>
/// <para>
/// <b>自己比对的依据是充分的</b>：<see cref="FileVersion"/> 已经带
/// <c>Path</c>、<c>Length</c>、<c>LastWriteUtc</c>、<c>ContentHash</c>、<c>Exists</c>
/// 五个字段，做集合比较不需要任何 SDK 内部信息。
/// <b>用内容哈希而不是时间戳</b>：改了又改回去不会被误报，而
/// <c>LastWriteUtc</c> 会因精度与时钟漂移漏判。
/// </para>
/// </remarks>
public static class WorkspaceDiff
{
    /// <summary>一条文件改动。</summary>
    /// <param name="Path">文件完整路径。</param>
    /// <param name="Kind">改动类型。</param>
    /// <param name="BeforeLength">改动前字节数；新增时为 null。</param>
    /// <param name="AfterLength">改动后字节数；删除时为 null。</param>
    public sealed record Change(
        string Path,
        ChangeKind Kind,
        long? BeforeLength,
        long? AfterLength);

    /// <summary>改动类型。</summary>
    public enum ChangeKind
    {
        /// <summary>新增。</summary>
        Added,

        /// <summary>内容或长度变了。</summary>
        Modified,

        /// <summary>被删除。</summary>
        Deleted,
    }

    /// <summary>
    /// 比对两次快照。
    /// </summary>
    /// <param name="before">回合开始时的条目。</param>
    /// <param name="after">回合结束时的条目（<b>必须包含已删除文件的条目</b>——
    /// 用"开始清单 ∪ 结束扫描"再 <c>Capture</c> 一次即可）。</param>
    /// <returns>按路径排序的改动列表；没有改动时为空。</returns>
    /// <remarks>
    /// <b>两边都要有被删文件的条目</b>：<c>Capture</c> 只对传入路径取快照，
    /// 消失的文件如果不在传入列表里就完全看不到。所以调用方必须再 Capture
    /// 一次并集——这层不能省。
    /// </remarks>
    public static IReadOnlyList<Change> Compare(
        IEnumerable<FileVersion> before,
        IEnumerable<FileVersion> after)
    {
        var beforeMap = ToMap(before);
        var afterMap = ToMap(after);

        var changes = new List<Change>();

        foreach (var (path, b) in beforeMap)
        {
            if (!afterMap.TryGetValue(path, out var a))
            {
                // 结束时没有这个条目：无法判断是删除还是调用方没传进列表。
                // 保守地报告为删除——漏报删除比多报一条更难排查。
                if (b.Exists) changes.Add(new Change(b.Path, ChangeKind.Deleted, b.Length, null));
                continue;
            }

            if (b.Exists && !a.Exists)
            {
                changes.Add(new Change(b.Path, ChangeKind.Deleted, b.Length, null));
                continue;
            }

            if (!b.Exists && a.Exists)
            {
                changes.Add(new Change(b.Path, ChangeKind.Added, null, a.Length));
                continue;
            }

            if (!b.Exists && !a.Exists) continue;

            // 两边都存在：比内容哈希。哈希相同就是没改——这正是用哈希而不是
            // 时间戳的理由，"改了又改回去"不会被误报。
            if (!string.Equals(b.ContentHash, a.ContentHash, StringComparison.Ordinal))
                changes.Add(new Change(b.Path, ChangeKind.Modified, b.Length, a.Length));
        }

        // 只在 ending 侧出现的（新增）
        foreach (var (path, a) in afterMap)
        {
            if (beforeMap.ContainsKey(path)) continue;
            if (a.Exists) changes.Add(new Change(a.Path, ChangeKind.Added, null, a.Length));
        }

        return changes
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>把改动列表转成给人看的多行文本。</summary>
    /// <remarks>
    /// <b>删除必须说"删除"</b>：SDK 把它表达成长度变成 -1，
    /// 用户看到"长度 5 → -1"会以为文件被写坏了，而不是被删了。
    /// </remarks>
    public static string Describe(IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0) return string.Empty;

        return string.Join(Environment.NewLine, changes.Select(c => c.Kind switch
        {
            ChangeKind.Added => $"新增：{c.Path}（{FormatSize(c.AfterLength)}）",
            ChangeKind.Deleted => $"删除：{c.Path}（原 {FormatSize(c.BeforeLength)}）",
            _ => $"修改：{c.Path}（{FormatSize(c.BeforeLength)} → {FormatSize(c.AfterLength)}）",
        }));
    }

    private static string FormatSize(long? bytes) =>
        bytes is null or < 0 ? "未知" : $"{bytes} 字节";

    private static Dictionary<string, FileVersion> ToMap(IEnumerable<FileVersion> versions)
    {
        var map = new Dictionary<string, FileVersion>(StringComparer.OrdinalIgnoreCase);

        foreach (var v in versions)
        {
            // 路径统一成完整路径，避免 ./a.txt 与 a.txt 被当成两个文件
            string key;
            try
            {
                key = Path.GetFullPath(v.Path);
            }
            catch
            {
                key = v.Path;
            }

            map[key] = v;
        }

        return map;
    }
}
