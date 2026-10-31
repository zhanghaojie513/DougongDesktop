using DougongDesktop.Ipc;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 把 IPC 的目录条目转成 composer 的 "&#64;" 引用项。
/// </summary>
/// <remarks>
/// <b>与 <see cref="ModelProjection"/> 同类</b>：IPC 只传纯数据，
/// Arc 类型在 App 层组装。区别是这里的输入来自已有的 <c>fs/list</c> 命令——
/// 右栏文件树用的就是同一份数据，不需要新协议。
/// <para>
/// <b>Id 用完整相对路径而不是文件名</b>：<c>src/Agent.cs</c> 与
/// <c>tests/Agent.cs</c> 同名不同文件，只用文件名做 id 会让 token 指错文件。
/// </para>
/// <para>
/// <b>目录也放进列表</b>：用户想写"看看 &#64;src"和"看看 &#64;README.md"
/// 是同一类需求，分成两个入口反而要他先想清楚。
/// </para>
/// </remarks>
public static class ContextItemProjection
{
    /// <summary>"&#64;" 列表的分组标题。</summary>
    /// <remarks>
    /// 只有一组：不区分文件夹与文件。dsh 默认是 "Files" 与 "Docs" 两组，
    /// 那是因为它能区分"工作区文件"与"上传文档"——我们只有前者。
    /// 硬凑两组会让第二组永远是空的。
    /// </remarks>
    public static readonly string[] Groups = ["工作区"];

    /// <summary>把一层目录内容转成引用项。</summary>
    /// <param name="listed">目录列表结果。</param>
    /// <param name="limit">最多转多少项。默认 200——太多了下拉框会卡。</param>
    public static IReadOnlyList<AiContextItem> FromDirectory(
        DirectoryListedEvent listed, int limit = 200)
    {
        if (listed.Error is { Length: > 0 }) return [];

        return
        [
            .. listed.Entries
                .OrderByDescending(e => e.IsDirectory)   // 目录在前
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(e => new AiContextItem
                {
                    // 完整相对路径：同名文件不会互串
                    Id = Combine(listed.Path, e.Name),
                    Label = e.IsDirectory ? e.Name + "/" : e.Name,
                    Group = Groups[0],
                    Hint = e.IsDirectory ? "目录" : FormatSize(e.Size),
                    Icon = Lucide.Geometry(
                        e.IsDirectory ? LucideIconKind.Folder : LucideIconKind.File),
                }),
        ];
    }

    private static string Combine(string parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";

    /// <summary>字节数转人读的大小。</summary>
    /// <remarks>
    /// <b>只到 KB/MB 两档</b>：这是下拉框里的提示，不是文件管理器。
    /// 多分档位只会让那一列宽度跳动。
    /// </remarks>
    private static string FormatSize(long bytes) => bytes switch
    {
        < 0 => "未知",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
