using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DougongDesktop.Ipc;

/// <summary>
/// 桌面端特有的消息。
/// </summary>
/// <remarks>
/// <b>通用契约（会话、回合、条目、审批、引擎状态）现在来自 SDK 的
/// <c>Dougong.HostProtocol</c></b>，不在本仓库维护。这里只放桌面端独有：
/// <list type="bullet">
/// <item><b>目录浏览</b>（<c>fs/list</c>）——右栏文件树的 UI 形态，
/// 不是通用会话协议的一部分</item>
/// <item><b>模型目录</b>（<c>models/list</c>）——宿主把 SDK 的模型能力表
/// 投影成纯数据给 UI。放在桌面端而不是 SDK，因为它带着宿主的取舍
/// （只传上下文窗口与输出上限，不传 wire format 细节）</item>
/// </list>
/// <para>
/// <b>判别器登记在类型上，不在某个集中的列表里</b>：SDK 的 <c>Protocol.cs</c>
/// 只登记它自己知道的类型，登记不了未知的派生类。所以桌面端这些消息
/// 由各自的 <c>[JsonDerivedType]</c> 声明。序列化时用
/// <see cref="Dougong.HostProtocol.HostProtocolContract.Json"/> 同一个实例即可——
/// 特性挂在类型上，不依赖登记位置。
/// </para>
/// </remarks>
public static class DesktopProtocol
{
    /// <summary>
    /// 桌面端特有消息的序列化配置。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能直接用 SDK 的 <c>HostProtocolContract.Json</c></b>：
    /// 多态基类只登记它自己知道的派生类型，而 <c>[JsonDerivedType]</c>
    /// 必须挂在基类上——挂在派生类上无效。实测报错：
    /// <c>Runtime type 'ListDirectoryCommand' is not supported by
    /// polymorphic type 'Dougong.HostProtocol.ClientCommand'</c>。
    /// <para>
    /// <b>基准设置必须与 SDK 一致</b>：SDK 文档明确警告"双方各写一份
    /// <c>JsonSerializerOptions</c> 是这类协议最常见的裂缝"——一侧忽略 null、
    /// 另一侧不忽略时，"字段不存在"与"字段是 null"在线上就无法区分。
    /// 所以这里从 SDK 的配置**克隆**，只补桌面端判别器，不动任何基准项。
    /// </para>
    /// </remarks>
    public static JsonSerializerOptions Json { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(HostProtocolContract.Json);

        // 用运行时修饰器补两条多态根的派生类型。
        //
        // 试过源生成（JsonSerializerContext）：对来自其他程序集的基类
        // 报 NullReferenceException（生成器缺陷）。运行时修饰器不受此限，
        // 且它只补"多出来的几个类型"，其余仍走 SDK 那份配置的默认行为。
        // SDK 的 TypeInfoResolver 是 null（用 STJ 默认反射解析器）。
        // 所以起点必须是 DefaultJsonTypeInfoResolver——用自写的空解析器
        // 会让所有类型都解析不出来，表现为"消息全丢"，而那条错误
        // 只在反序列化时静默发生。
        var baseResolver = options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        options.TypeInfoResolver = baseResolver.WithAddedModifier(DesktopPolymorphism.Add);

        return options;
    }
}

/// <summary>
/// 桌面端派生类型的多态登记。
/// </summary>
/// <remarks>
/// <b>只能在这里登记，不能在类型上加特性</b>：System.Text.Json 要求
/// <c>[JsonDerivedType]</c> 出现在基类上，而基类
/// （<c>Dougong.HostProtocol.ClientCommand</c>）在 SDK 程序集里，我们改不了。
/// <para>
/// <b>新增桌面端消息时这里必须同步加一行</b>，否则那类消息发得出去、收不回来。
/// <c>ipc-every-message-is-registered</c> 那条检查会提醒。
/// </para>
/// </remarks>
internal static class DesktopPolymorphism
{
    public static void Add(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type == typeof(ClientCommand))
        {
            typeInfo.PolymorphismOptions ??= new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "method",
            };

            typeInfo.PolymorphismOptions.DerivedTypes.Add(
                new JsonDerivedType(typeof(ListDirectoryCommand), "fs/list"));
            typeInfo.PolymorphismOptions.DerivedTypes.Add(
                new JsonDerivedType(typeof(ListModelsCommand), "models/list"));
        }
        else if (typeInfo.Type == typeof(ServerEvent))
        {
            typeInfo.PolymorphismOptions ??= new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "method",
            };

            typeInfo.PolymorphismOptions.DerivedTypes.Add(
                new JsonDerivedType(typeof(DirectoryListedEvent), "fs/listed"));
            typeInfo.PolymorphismOptions.DerivedTypes.Add(
                new JsonDerivedType(typeof(WorkspaceChangedEvent), "fs/changed"));
            typeInfo.PolymorphismOptions.DerivedTypes.Add(
                new JsonDerivedType(typeof(ModelsListedEvent), "models/listed"));
        }
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
// 模型目录
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 一个可选模型的描述。
/// </summary>
/// <remarks>
/// <b>为什么不直接用 SDK 的 <c>ModelCapabilities</c></b>：那个类型带着
/// <c>ReasoningField</c> 与 <c>ReasoningEfforts</c>——前者是 wire format
/// 细节（UI 不该知道"用哪个字段名传推理"），后者是个字典（跨进程要么转形状、
/// 要么泄露实现）。这里只传 UI 真正要显示的三个数，砍掉的是**宿主的取舍**。
/// </remarks>
/// <param name="ProviderId">所属 provider 的稳定标识（如 <c>anthropic</c>）。</param>
/// <param name="Id">模型标识，用于发起请求时指定。</param>
/// <param name="ContextWindow">上下文窗口（token）。0 表示未知。</param>
/// <param name="MaxOutputTokens">输出容量上限（token）。0 表示未知。</param>
/// <param name="SupportsReasoning">是否支持思考模式。</param>
public sealed record ModelInfo(
    string ProviderId,
    string Id,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsReasoning);

/// <summary>模型目录（响应 <see cref="ListModelsCommand"/>）。</summary>
// 判别器在 DesktopPolymorphism 登记（特性必须挂基类）。
// [JsonDerivedType(typeof(ModelsListedEvent), "models/listed")]
public sealed record ModelsListedEvent : ServerEvent
{
    public required IReadOnlyList<ModelInfo> Models { get; init; }

    /// <summary>provider 的稳定标识，按字母序。</summary>
    public required IReadOnlyList<string> Providers { get; init; }

    /// <summary>当前实际在用的模型 Id；null 表示未配置。</summary>
    public string? CurrentModelId { get; init; }
}

/// <summary>请求模型目录。</summary>
// 判别器在 DesktopPolymorphism 登记（特性必须挂基类）。
// [JsonDerivedType(typeof(ListModelsCommand), "models/list")]
public sealed record ListModelsCommand : ClientCommand;
