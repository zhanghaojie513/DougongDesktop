using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DougongDesktop.Ipc;

/// <summary>
/// 桌面端特有的消息。
/// </summary>
/// <remarks>
/// <b>通用契约现在全部来自 SDK 的 <c>Dougong.HostProtocol</c></b>，
/// 不在本仓库维护。这里只剩真正桌面端独有的：
/// <list type="bullet">
/// <item><b>目录浏览</b>（<c>fs/list</c>、<c>fs/listed</c>）——右栏文件树的 UI 形态</item>
/// <item><b>工作区改动</b>（<c>fs/changed</c>）——"改动 tab"，且"怎么算变更"
/// 是一种实现选择（桌面端用文件版本指纹比对，给不出行级 diff）</item>
/// </list>
/// <para>
/// <b>模型目录不在其中</b>：它曾经在这里，但那个取舍（只传上下文窗口与输出上限、
/// 不传 wire format 细节）对任何客户端都成立，不是桌面端独有。
/// 已收进 SDK 契约（<c>model/list</c> / <c>model/listed</c>）。
/// </para>
/// <para>
/// <b>判别器由 <see cref="RegisterMessages"/> 登记</b>，不用 <c>[JsonDerivedType]</c>：
/// 那个特性必须挂在基类上，而基类在 SDK 程序集里。
/// </para>
/// </remarks>
public static class DesktopProtocol
{
    /// <summary>
    /// 把桌面端特有的消息登记到 SDK 的共享配置里。
    /// </summary>
    /// <remarks>
    /// <b>用 SDK 的 <c>RegisterDerivedType</c> 而不是自己克隆一份配置。</b>
    /// 之前这里克隆 SDK 的 <c>Json</c> 再挂修饰器，那是绕法；
    /// SDK 补了这个口之后不再需要。
    /// <para>
    /// <b>克隆的问题</b>（SDK 文档的原文理由）：绕开"同一份配置"这条，
    /// 两侧的 <c>DefaultIgnoreCondition</c> 一旦不同，"字段不存在"与
    /// "字段是 null"在线上就无法区分。
    /// </para>
    /// <para>
    /// <b>基线类型必须显式给泛型参数</b>，不靠反射推断——SDK 文档说明了原因：
    /// 派生类型的 <c>BaseType</c> 未必就是要挂的多态根（可能有中间基类），
    /// 猜错时类型能编译、特性也都在，只是序列化时不走、反序列化时不认，
    /// 两端都不报错。
    /// </para>
    /// <para>
    /// <b>必须在首次序列化之前调用</b>。之后调用 SDK 会抛
    /// <see cref="InvalidOperationException"/> 而不是静默忽略——
    /// 静默忽略会让人对着"消息发不出去"排查半天。
    /// 所以由 <c>AppShell</c> 的静态构造调，早于任何连接。
    /// </para>
    /// </remarks>
    public static void RegisterMessages()
    {
        if (_registered) return;
        _registered = true;

        HostProtocolContract.RegisterDerivedType<ClientCommand>(
            new JsonDerivedType(typeof(ListDirectoryCommand), "fs/list"));
        HostProtocolContract.RegisterDerivedType<ClientCommand>(
            new JsonDerivedType(typeof(ListModelsCommand), "models/list"));

        HostProtocolContract.RegisterDerivedType<ServerEvent>(
            new JsonDerivedType(typeof(DirectoryListedEvent), "fs/listed"));
        HostProtocolContract.RegisterDerivedType<ServerEvent>(
            new JsonDerivedType(typeof(WorkspaceChangedEvent), "fs/changed"));
        HostProtocolContract.RegisterDerivedType<ServerEvent>(
            new JsonDerivedType(typeof(ModelsListedEvent), "models/listed"));
    }

    private static bool _registered;

    /// <summary>
    /// 序列化配置：直接用 SDK 的共享实例。
    /// </summary>
    /// <remarks>
    /// <b>不再克隆</b>：桌面端消息已经通过 <c>RegisterDerivedType</c>
    /// 进到 SDK 那份配置里，两端用同一个 <c>JsonSerializerOptions</c>，
    /// "同一份配置"这条从机制上成立，不靠约定。
    /// </remarks>
    public static JsonSerializerOptions Json => HostProtocolContract.Json;

    /// <summary>
    /// 已登记的扩展类型，供自检。
    /// </summary>
    /// <remarks>
    /// SDK 暴露这个清单就是为了让宿主能断言"我的注册真的进去了"——
    /// 注册失败是静默的（编译过、序列化不报错，只是那条消息用错配置）。
    /// </remarks>
    public static IReadOnlyList<(Type BaseType, JsonDerivedType Derived)> RegisteredExtensions =>
        HostProtocolContract.RegisteredExtensions;
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
// 模型目录
// ─────────────────────────────────────────────────────────────

// **这三个类型已改由 SDK 的 Dougong.HostProtocol 提供**：
// ModelInfo、ListModelsCommand（判别器 model/list）、ModelsListedEvent（model/listed）。
//
// 此前这里各自定义了一份，判别器写成 models/list——与契约不一致。
// 实测同一类型挂两个判别器时，序列化只用第一个，第二个反序列化直接抛
// JsonException。也就是说两份定义并存时，写出去和读回来用的不是同一个值。
//
// 现在删掉本地定义，直接用契约的。判别器统一为 model/list 与 model/listed。
