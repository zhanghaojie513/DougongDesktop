using System.Text.Json;
using Dougong.Agent;

namespace DougongDesktop.Framework;

/// <summary>
/// 内置工具的共同基类：负责 JSON schema 与权限描述的样板。
/// </summary>
/// <remarks>
/// 刻意只放**只读**和**可撤销**的工具。写文件、执行 shell 命令是产品决策
/// （"这个 agent 能干什么"），不在框架层预设——见 SDK 的 host-layering-decision.md。
/// </remarks>
internal abstract class WorkspaceTool : IAgentTool
{
    protected WorkspaceTool(string workspace) => WorkspaceRoot = Path.GetFullPath(workspace);

    protected string WorkspaceRoot { get; }

    public abstract ToolDescriptor Descriptor { get; }

    public virtual ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Read, true, [], [], []);

    public virtual ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial);

    public abstract ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken);

    /// <summary>
    /// 把相对路径解析成工作区内的绝对路径。
    /// </summary>
    /// <remarks>
    /// 解析后的路径必须仍在工作区内。这条不是可选的礼貌检查——
    /// 没有它，<c>../../etc/passwd</c> 就是一个能读任意文件的洞。
    /// </remarks>
    protected string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(WorkspaceRoot, relative));
        var root = WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"路径 '{relative}' 解析到工作区之外（{full}），已拒绝。");
        }

        return full;
    }

    protected static AgentToolResult Text(string content) =>
        new(JsonSerializer.SerializeToElement(new { content }));
}

internal sealed class ReadFileTool(string workspace) : WorkspaceTool(workspace)
{
    /// <summary>单次读取上限。超过就截断并说明——不静默截断。</summary>
    private const int MaxChars = 60_000;

    public override ToolDescriptor Descriptor { get; } = new(
        Id: "read_file",
        Name: "read_file",
        Version: "1.0",
        Description: "读取工作区内的一个文本文件。参数 path 为相对工作区根目录的路径。",
        InputSchema: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { path = new { type = "string", description = "相对工作区根目录的文件路径" } },
            required = new[] { "path" },
        }));

    public override async ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = invocation.Arguments.GetProperty("path").GetString()
                   ?? throw new ArgumentException("path 不能为空。");

        var full = Resolve(path);
        if (!File.Exists(full))
            return Text($"文件不存在：{path}");

        var content = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        var truncated = content.Length > MaxChars;

        return Text(truncated
            ? content[..MaxChars] + $"\n\n[已截断：原文 {content.Length} 字符，只显示前 {MaxChars} 字符]"
            : content);
    }
}

internal sealed class ListDirectoryTool(string workspace) : WorkspaceTool(workspace)
{
    private const int MaxEntries = 500;

    public override ToolDescriptor Descriptor { get; } = new(
        Id: "list_directory",
        Name: "list_directory",
        Version: "1.0",
        Description: "列出工作区内一个目录的条目。参数 path 为相对工作区根目录的路径，省略表示根目录。",
        InputSchema: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { path = new { type = "string", description = "相对路径，默认为工作区根目录" } },
            required = Array.Empty<string>(),
        }));

    public override ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var relative = invocation.Arguments.TryGetProperty("path", out var p)
            ? p.GetString() ?? "."
            : ".";

        var full = Resolve(relative);
        if (!Directory.Exists(full))
            return ValueTask.FromResult(Text($"目录不存在：{relative}"));

        var entries = new DirectoryInfo(full)
            .EnumerateFileSystemInfos()
            .OrderBy(e => e is DirectoryInfo ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .Select(e => e is DirectoryInfo ? $"{e.Name}/" : $"{e.Name} ({e.Length()} 字节)")
            .ToArray();

        var suffix = entries.Length >= MaxEntries
            ? $"\n[只列出前 {MaxEntries} 项]"
            : string.Empty;

        return ValueTask.FromResult(Text(
            entries.Length == 0 ? "（空目录）" : string.Join('\n', entries) + suffix));
    }
}

file static class FileInfoExtensions
{
    public static long Length(this FileSystemInfo info) =>
        info is FileInfo file ? file.Length : 0;
}

/// <summary>
/// 计划工具的适配器。SDK 提供 <c>todo_write</c> 工具，模型显式声明计划，
/// 运行时把结果投影成 <see cref="AgentEvent.PlanUpdated"/>。
/// </summary>
/// <remarks>
/// 这里用最小实现而不是包装 SDK 内部类型：计划是**模型声明**的，
/// 不是运行时猜的。没有真实数据源时宁可没有计划，也不要凭空推测。
/// </remarks>
internal sealed class PlanToolAdapter : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Id: "todo_write",
        Name: "todo_write",
        Version: "1.0",
        Description:
        "声明或更新当前任务的计划。全量替换语义：每次传入完整列表。" +
        "用于让用户看到你要做什么、做到哪一步。",
        InputSchema: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                entries = new
                {
                    type = "array",
                    description = "完整的计划条目列表",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            content = new { type = "string", description = "这一步要做什么" },
                            status = new
                            {
                                type = "string",
                                @enum = new[] { "pending", "in_progress", "completed" },
                            },
                            priority = new { type = "string", @enum = new[] { "high", "medium", "low" } },
                        },
                        required = new[] { "content", "status" },
                    },
                },
            },
            required = new[] { "entries" },
        }));

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.None, true, [], [], []);

    public ToolExecutionPlan DescribeExecution(JsonElement arguments) =>
        new(ToolConcurrency.Serial);

    public ValueTask<AgentToolResult> ExecuteAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        // 参数已由 dispatcher 校验；这里只回一个确认，计划内容由
        // LoopAgentRuntime 转成 PlanUpdated 事件。
        var count = invocation.Arguments.TryGetProperty("entries", out var entries)
                    && entries.ValueKind == JsonValueKind.Array
            ? entries.GetArrayLength()
            : 0;

        return ValueTask.FromResult(
            new AgentToolResult(JsonSerializer.SerializeToElement(new { content = $"计划已记录（{count} 项）。" })));
    }
}
