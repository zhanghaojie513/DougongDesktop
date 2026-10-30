namespace DougongDesktop.Ui.Core;

/// <summary>
/// 内置模块清单：显式有序，不靠代码执行顺序。
/// </summary>
/// <remarks>
/// <b>为什么把顺序显式化</b>（借鉴 dsh 的 <c>dsh.profile.bundles</c>）：
/// 之前内置模块的顺序是"构造函数里谁先调用谁在前"——顺序不可见、不可测，
/// 新增模块时不知道该插在哪，改动时也无法断言"顺序没变"。
/// <para>
/// dsh 的原文理由是：扫描依赖 + 字母序排序会**产生两个真源和一条隐式决胜规则**，
/// 而一份显式有序列表更小、完全确定。
/// </para>
/// <para>
/// <b>这张表就是唯一真源。</b>加模块只改这里，不用在构造函数里找插入点。
/// </para>
/// </remarks>
public static class BuiltInModules
{
    /// <summary>
    /// 模块的注册顺序（从前往后）。
    /// </summary>
    /// <remarks>
    /// 顺序有意义的地方：同 priority 时先注册者胜（<see cref="SlotRegistry"/> 的规则）。
    /// 所以这张表的先后决定"同 priority 下谁占位"。
    /// </remarks>
    public static IReadOnlyList<BuiltInModule> All { get; } =
    [
        // 侧边栏排第一：它是导航入口，视觉上也在最左
        new("builtin.sidebar", ReservedSlots.Sidebar, Key: null, BuiltInModuleKind.Sidebar),

        // 主区三个面板：conversation 是默认，放最前
        new("builtin.conversation", ReservedSlots.Main, ReservedSlots.Conversation,
            BuiltInModuleKind.Placeholder,
            "对话区", "消息流 · 工具卡片 · 审批"),

        new("builtin.plugins", ReservedSlots.Main, "plugins",
            BuiltInModuleKind.Placeholder,
            "插件", "插件管理 · 已安装列表"),

        new("builtin.settings", ReservedSlots.Main, "settings",
            BuiltInModuleKind.Placeholder,
            "设置", "提供商 · 外观 · 诊断"),

        // 状态胶囊常驻底部，不可关
        new("builtin.status", ReservedSlots.Bottom, Key: null, BuiltInModuleKind.StatusCapsule),

        // 右栏最后：默认折叠，放最后不影响首屏
        new("builtin.rightbar", ReservedSlots.RightBar, Key: null,
            BuiltInModuleKind.Placeholder,
            "右栏", "文件 · 改动 · 计划 · 上下文"),
    ];

    /// <summary>按 Id 查；找不到返回 null。</summary>
    public static BuiltInModule? Find(string id) =>
        All.FirstOrDefault(m => m.Id == id);
}

/// <summary>一条内置模块声明。</summary>
/// <param name="Id">占位者 Id。</param>
/// <param name="SlotKey">注册到哪个 slot。</param>
/// <param name="Key">keyed slot 的 key。</param>
/// <param name="Kind">构建方式。</param>
/// <param name="Title">占位标题（仅 Placeholder kind）。</param>
/// <param name="Subtitle">占位副标题（仅 Placeholder kind）。</param>
public sealed record BuiltInModule(
    string Id,
    string SlotKey,
    string? Key,
    BuiltInModuleKind Kind,
    string? Title = null,
    string? Subtitle = null);

/// <summary>
/// 模块的构建方式。
/// </summary>
/// <remarks>
/// <b>为什么用枚举而不是放 <c>Func&lt;Control&gt;</c></b>：清单要能被 dump 成可读文本
/// （见 <c>--dump-config</c>），而委托没法序列化。枚举 + 参数能完整描述"装了什么"。
/// </remarks>
public enum BuiltInModuleKind
{
    /// <summary>占位框，P4 之后逐个换成真实内容。</summary>
    Placeholder,

    /// <summary>侧边栏（内容随会话列表变化，单独处理）。</summary>
    Sidebar,

    /// <summary>状态胶囊。</summary>
    StatusCapsule,
}
