using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DougongDesktop.Ipc;

/// <summary>
/// 桌面端特有的消息。
/// </summary>
/// <remarks>
/// <b>通用契约现在由本仓库自持</b>，见 <see cref="DesktopContract"/>。
/// 这里只剩真正桌面端独有的：
/// <list type="bullet">
/// <item><b>目录浏览</b>（<c>fs/list</c>、<c>fs/listed</c>）——右栏文件树的 UI 形态</item>
/// <item><b>工作区改动</b>（<c>fs/changed</c>）——"改动 tab"，且"怎么算变更"
/// 是一种实现选择（桌面端用文件版本指纹比对，给不出行级 diff）</item>
/// </list>
/// <para>
/// <b>判别器全部挂在 <see cref="DesktopContract"/> 的根类型特性上</b>。
/// 曾经的做法是调用 SDK 的 <c>RegisterDerivedType</c>，因为基类在 SDK 程序集里、
/// 挂不了特性。根搬进本仓库后那个绕法不再需要——特性就在声明处，
/// 不可能出现"注册了但没生效"，因为不注册就编译不过。
/// </para>
/// <para>
/// <b><see cref="RegisterMessages"/> 保留为空操作</b>：<c>AppShell</c> 的静态构造
/// 与检查工程都在调它。删掉会牵连五处调用点，而那些调用点本身没有错——
/// 它们表达的是"在连接之前把协议准备好"这个意图，将来若引入
/// 需要运行时组装的机制（比如按引擎能力裁剪判别器），这个入口正好是落点。
/// 现在它是空操作，但保留调用点的成本为零。
/// </para>
/// </remarks>
public static class DesktopProtocol
{
    /// <summary>
    /// 协议准备。现在是空操作——判别器已由特性声明。
    /// </summary>
    /// <remarks>
    /// 见类注释：保留是因为调用点表达"连接前先备好协议"这一意图。
    /// </remarks>
    public static void RegisterMessages()
    {
        // 不再需要注册：判别器在 DesktopContract 的类型声明上。
        // 这里刻意保留一个方法体而不是删掉整个入口。
    }

    /// <summary>
    /// 序列化配置：本仓库自持，与 SDK 的逐项对齐。
    /// </summary>
    /// <remarks>
    /// 见 <see cref="DesktopContract.Json"/> 的说明——包括为什么必须自己建一份
    /// 而不能借用 SDK 的。
    /// </remarks>
    public static JsonSerializerOptions Json => DesktopContract.Json;

    /// <summary>
    /// 诊断用：确认线上帧的字段命名。
    /// </summary>
    /// <remarks>
    /// <b>为什么要有这个</b>：2026-11-10 我向 SDK 侧报了一个"字段命名不一致"的问题
    /// （说 <c>Key</c>/<c>Enabled</c> 是 PascalCase 而 <c>sessionId</c> 是 camelCase）。
    /// 复核发现是**我的探针写错了**——那次探针用了默认 <c>JsonSerializerOptions</c>。
    /// 用对配置后全是 camelCase：
    /// <code>
    /// 正确：{"method":"permission/switch","sessionId":"s","key":"ask","requestId":"2"}
    /// 错误：{"method":"permission/switch","SessionId":"s","Key":"ask","RequestId":"2"}
    /// </code>
    /// <b>教训</b>：凡是"验证线上格式"的探针，必须用生产同一份 <c>JsonSerializerOptions</c>。
    /// 用默认配置验证等于验证了一个不存在的实现。
    /// </remarks>
    public static string DescribeWireFormat()
    {
        var sample = JsonSerializer.Serialize(
            new SwitchPermissionPresetCommand
            {
                RequestId = "probe",
                SessionId = "probe",
                Key = "probe",
            },
            typeof(ClientCommand),
            Json);

        return sample;
    }
}

// ─────────────────────────────────────────────────────────────
// 目录浏览（右栏文件树）
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
// 判别器在 DesktopPolymorphism 登记（特性必须挂基类）。
// [JsonDerivedType(typeof(ListDirectoryCommand), "fs/list")]
public sealed record ListDirectoryCommand : ClientCommand
{
    /// <summary>相对工作区根的路径。空或 null 表示根目录。</summary>
    public string? Path { get; init; }
}

/// <summary>目录内容（响应 <see cref="ListDirectoryCommand"/>）。</summary>
// 判别器在 DesktopPolymorphism 登记（特性必须挂基类）。
// [JsonDerivedType(typeof(DirectoryListedEvent), "fs/listed")]
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

// ─────────────────────────────────────────────────────────────
// 文件改动（回合结束时）
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 回合结束时的文件改动。
/// </summary>
/// <remarks>
/// <b>这是"变更描述"而不是行级差异</b>：SDK 的 <c>FileVersion</c> 只保存
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
/// <para>
/// <b>为什么不进 SDK 的 HostProtocol</b>：它是桌面端右栏"改动"tab 的形状，
/// 别的宿主未必有这个概念。这与目录浏览同类——UI 形态，不是通用会话协议。
/// </para>
/// </remarks>
// 判别器在 DesktopPolymorphism 登记（特性必须挂基类）。
// [JsonDerivedType(typeof(WorkspaceChangedEvent), "fs/changed")]
public sealed record WorkspaceChangedEvent : ServerEvent
{
    /// <summary>变更描述。空串表示本回合没有改动。</summary>
    public required string Diff { get; init; }

    /// <summary>是否有改动。由 <see cref="Diff"/> 是否为空推得，单独给出便于界面判断。</summary>
    public required bool HasChanges { get; init; }
}

// ─────────────────────────────────────────────────────────────
// 消息反馈
// ─────────────────────────────────────────────────────────────

/// <summary>一次评分的取值。</summary>
/// <remarks>
/// <b>三态而不是布尔</b>：撤回评分（<see cref="None"/>）是真会发生的动作——
/// 用户点赞后改主意。两态表达不了"从有评价回到没评价"。
/// </remarks>
public enum FeedbackRating
{
    /// <summary>没有评价（或撤回）。</summary>
    None,

    /// <summary>正面。</summary>
    Up,

    /// <summary>负面。</summary>
    Down,
}

/// <summary>提交一条消息反馈。</summary>
/// <remarks>
/// <b>为什么不发给引擎而是只落本地</b>：反馈是产品改进数据，不是 agent 运行的一部分。
/// 引擎（AgentHost）不关心用户是否满意。所以这条目前只写本地诊断日志，
/// 将来接产品分析时替换接收端即可。
/// <para>
/// <b>但协议放在 Ipc 而不是直接调文件</b>：将来要发给远端时，
/// 改的是宿主的处理，而不是 UI。
/// </para>
/// </remarks>
/// <param name="ItemId">被评价的条目 Id（assistant 消息或工具卡片）。</param>
/// <param name="Rating">评分。</param>
/// <param name="Reason">负面评价时的理由；正面可以为 null。</param>
/// <param name="SessionId">所属会话；null 表示当前会话。</param>
// 判别器在 DesktopPolymorphism 登记（特性必须挂基类）。
// [JsonDerivedType(typeof(SubmitFeedbackCommand), "feedback/submit")]
public sealed record SubmitFeedbackCommand : ClientCommand
{
    public required string ItemId { get; init; }
    public required FeedbackRating Rating { get; init; }
    public string? Reason { get; init; }
    public string? SessionId { get; init; }
}

// ─────────────────────────────────────────────────────────────
// 模型目录
// ─────────────────────────────────────────────────────────────

// **这三个类型现在在 Contract.cs 里，由本仓库自持**：
// ModelInfo、ListModelsCommand（判别器 model/list）、ModelsListedEvent（model/listed）。
//
// 历史：它们一度由 SDK 的 Dougong.HostProtocol 提供；更早之前这里各自定义过一份，
// 判别器写成 models/list——与契约不一致。实测同一类型挂两个判别器时，
// 序列化只用第一个，第二个反序列化直接抛 JsonException。也就是说两份定义并存时，
// 写出去和读回来用的不是同一个值。
//
// 现在统一在 Contract.cs 声明，判别器为 model/list 与 model/listed。
// 本文件不出现它们，就是为了不再有"第二份定义"的可能。
