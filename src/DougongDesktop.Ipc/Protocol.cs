using System.Text.Json;
using System.Text.Json.Serialization;

namespace DougongDesktop.Ipc;

/// <summary>
/// 协议信封：版本、序列化配置，以及两条消息流的多态根。
/// </summary>
/// <remarks>
/// <b>这个文件只放"线上契约的总表"</b>：所有 <c>[JsonDerivedType]</c> 登记都在这里，
/// 因此看这一个文件就知道总共有哪些命令和事件。具体的消息类型按域分文件
/// （Session / Turn / Conversation / Approval / Workspace / Diagnostics）。
/// <para>
/// <b>为什么按域拆文件而不是抽成独立包</b>：三个消费者（Framework / AgentHost / App）
/// 同仓同版本，抽包零收益。拆文件解决的是 418 行单文件的可读性，
/// 不改变任何引用关系。何时才该抽包：出现仓库外的消费者，或 UI 插件需要访问契约。
/// </para>
/// </remarks>
public static class IpcProtocol
{
    /// <summary>
    /// 协议版本。
    /// </summary>
    /// <remarks>
    /// 客户端与服务端各自校验，不匹配时<b>拒绝连接</b>而不是"尽量解析"——
    /// 尽量解析会把"字段缺失"变成"字段是默认值"，而那种错误只在某个
    /// 特定操作上才暴露。
    /// <para>
    /// <b>改这个数字的判据</b>：类型被删除、字段语义改变（不只是新增可空字段）
    /// 时必须加一。只加新类型或新可空字段不需要——旧客户端遇到不认识的
    /// 判别器会忽略（见 <c>ServerEvent</c> 的说明）。
    /// </para>
    /// </remarks>
    public const int Version = 1;

    /// <summary>JSON 序列化配置。枚举写字符串，便于抓包定位。</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

// ─────────────────────────────────────────────────────────────
// 会话条目（客户端渲染单元）
// ─────────────────────────────────────────────────────────────

/// <summary>会话条目的多态根。判别字段是 <c>kind</c>。</summary>
/// <remarks>
/// <b>不认识的 kind 必须降级为通用卡片</b>：宿主或 SDK 加新条目类型时，
/// 旧客户端不该崩。这与命令/事件侧"忽略不认识的判别器"是同一条原则。
/// <para>
/// 登记在这里而不是 <c>Conversation.cs</c>，是为了让"线上契约总表"
/// 保持完整——看 <c>Protocol.cs</c> 一个文件就知道总共有多少种消息。
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserMessageItem), "userMessage")]
[JsonDerivedType(typeof(AssistantMessageItem), "assistantMessage")]
[JsonDerivedType(typeof(ReasoningItem), "reasoning")]
[JsonDerivedType(typeof(ToolCallItem), "toolCall")]
[JsonDerivedType(typeof(PlanItem), "plan")]
[JsonDerivedType(typeof(NoticeItem), "notice")]
public abstract partial record ConversationItem
{
    public required string Id { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

// ─────────────────────────────────────────────────────────────
// 命令（客户端 → 服务端）
// ─────────────────────────────────────────────────────────────

/// <summary>客户端命令的多态根。判别字段是 <c>method</c>。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "method")]
[JsonDerivedType(typeof(ListSessionsCommand), "session/list")]
[JsonDerivedType(typeof(CreateSessionCommand), "session/create")]
[JsonDerivedType(typeof(OpenSessionCommand), "session/open")]
[JsonDerivedType(typeof(ResyncCommand), "session/resync")]
[JsonDerivedType(typeof(StartTurnCommand), "turn/start")]
[JsonDerivedType(typeof(CancelTurnCommand), "turn/cancel")]
[JsonDerivedType(typeof(RespondApprovalCommand), "approval/respond")]
[JsonDerivedType(typeof(ListDirectoryCommand), "fs/list")]
[JsonDerivedType(typeof(ListModelsCommand), "models/list")]
public abstract record ClientCommand
{
    /// <summary>请求 ID。响应带同一个 ID 回来，用于配对。</summary>
    public required string RequestId { get; init; }
}

// ─────────────────────────────────────────────────────────────
// 通知（服务端 → 客户端）
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 服务端推送的增量。每条带 <see cref="Sequence"/>。判别字段是 <c>method</c>。
/// </summary>
/// <remarks>
/// <b>序号的语义，以及它现在还做不到什么——按现状如实写</b>：
/// <list type="bullet">
/// <item>序号由<b>单一全局计数器</b>分配（<c>IpcServer.NextStatusSequence</c>），
/// 会话事件与引擎状态事件共用它，因此同一连接内严格单调、不重复。</item>
/// <item>客户端发现缺口会发 <see cref="ResyncCommand"/> 请求重同步；
/// 重同步走"重开一次会话"路径，回到全量快照。</item>
/// <item><b>断线期间没有事件时</b>，重连后第一帧的序号会远大于本地记录，
/// 看起来像漏了几千条。所以断线一律整条重建、重新 <c>session/open</c>，
/// 不做"从某个序号继续"的增量恢复——服务端目前也没有回放能力。</item>
/// </list>
/// <para>
/// 为什么把这段写清楚而不是留空：单人单连接下有漏洞也不会暴露，
/// 但一旦有人按"序号可靠"去实现增量同步，就会踩到一个<b>看起来能用、
/// 断线后才错</b>的坑。契约里没实现的条款，比没有条款更危险。
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "method")]
[JsonDerivedType(typeof(SessionListedEvent), "session/listed")]
[JsonDerivedType(typeof(SessionOpenedEvent), "session/opened")]
[JsonDerivedType(typeof(TurnStartedEvent), "turn/started")]
[JsonDerivedType(typeof(TurnCompletedEvent), "turn/completed")]
[JsonDerivedType(typeof(ItemAddedEvent), "item/added")]
[JsonDerivedType(typeof(ItemUpdatedEvent), "item/updated")]
[JsonDerivedType(typeof(ApprovalRequestedEvent), "approval/requested")]
[JsonDerivedType(typeof(EngineStatusEvent), "engine/status")]
[JsonDerivedType(typeof(DirectoryListedEvent), "fs/listed")]
[JsonDerivedType(typeof(WorkspaceChangedEvent), "fs/changed")]
[JsonDerivedType(typeof(ModelsListedEvent), "models/listed")]
public abstract record ServerEvent
{
    public required long Sequence { get; init; }
}
