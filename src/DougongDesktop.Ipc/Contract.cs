using System.Text.Json;
using System.Text.Json.Serialization;

namespace DougongDesktop.Ipc;

/// <summary>
/// 本仓库自持的线上契约：会话、回合、条目、审批与权限的**全部**消息形状。
/// </summary>
/// <remarks>
/// <b>为什么这些类型从 SDK 搬到这里</b>：它们曾经全部来自
/// <c>Dougong.HostProtocol</c>，于是一个 SDK 升级改了某个字段的语义时，
/// 表现是"UI 编译通过、跑起来空着、日志里什么都没有"——两端都不报错。
/// 声明权在本仓库之后，同类改动会让 <c>Framework</c> 的项目映射编译失败，
/// 破坏从"运行时静默"移到"编译期喧哗"。
/// <para>
/// <b>线上格式必须与 SDK 逐字节一致</b>：本文件里每个判别器和 JSON 名称都照
/// 实测的 SDK 输出抄写，不是按命名约定推的。改任何一个都是在改协议——
/// 判据见 <c>docs/ipc-protocol-readiness.md</c>：类型被删除、或字段语义改变时
/// 要升版本并考虑协商；只加可空字段不需要。
/// </para>
/// <para>
/// <b>为什么要守住顺序</b>：<c>System.Text.Json</c> 按"派生类自有属性在前、
/// 基类属性在后"输出。SDK 的 <c>ToolCallItem</c> 把 <c>Id</c>/<c>At</c> 放在
/// 基类，输出时排在 <c>status</c> 之后。这里同样把 <c>Id</c>/<c>At</c> 放基类、
/// 自有属性放派生类，所以字段顺序自然一致。把 <c>Id</c> 提到派生类会让线上 JSON
/// 的字段顺序变化——字段顺序不影响语义，但会让逐字节比对失效，
/// 而那正是本仓库验证协议兼容的方式。
/// </para>
/// <para>
/// <b>不要用 <c>[JsonDerivedType]</c> 之外的方式登记</b>：判别器必须在这里声明。
/// SDK 的 <c>RegisterDerivedType</c> 是给"宿主给 SDK 的根加类型"用的口子，
/// 现在根在我们这里，直接挂特性即可。
/// </para>
/// </remarks>
public static class DesktopContract
{
    /// <summary>
    /// 协议版本。
    /// </summary>
    /// <remarks>
    /// <b>加一的判据</b>：类型被删除、或字段的<b>语义</b>改变。只加新类型、
    /// 或加可空字段不需要——旧客户端会忽略不认识的判别器。
    /// <para>
    /// <b>当前协商状态：未实现。</b> 这个字段存在，但除 <c>EngineClient</c> 的
    /// "不等于就拒绝连接"之外没有协商流程（谁先发、不匹配时怎么拒绝、能力位如何取交集）。
    /// 用它之前必须先补完整流程，否则会出现"字段设了但没生效"——
    /// 那比没有这个字段更危险，因为调用方会以为已经做了兼容检查。
    /// </para>
    /// <para>
    /// <b>不匹配时的正确行为是拒绝连接，不是"尽量解析"</b>：尽量解析会把
    /// "字段缺失"变成"字段是默认值"，而那种错误只在某个特定操作上才暴露。
    /// </para>
    /// <para>
    /// <b>这个值现在由本仓库持有</b>：它曾经取自 SDK 的 <c>HostProtocolContract.Version</c>，
    /// 但协议已经是我们自己的，版本号跟着 SDK 走会让"我们改了协议"这件事无法表达。
    /// 起始值与迁移前的 SDK 版本对齐，避免和本次重构前的客户端按同一版本互相放行。
    /// </para>
    /// </remarks>
    public const int Version = 1;

    /// <summary>
    /// 序列化配置：与 SDK 那份**逐项相同**。
    /// </summary>
    /// <remarks>
    /// <b>为什么不直接借用 SDK 的 <c>HostProtocolContract.Json</c></b>：借它就还是依赖。
    /// 这里自己建一份，但每一项都必须对齐——两侧配置不一致是这类协议最常见的裂缝：
    /// <c>DefaultIgnoreCondition</c> 一旦不同，"字段不存在"与"字段是 null"
    /// 在线上就无法区分。
    /// <para>
    /// <b>枚举必须是 camelCase 且区分大小写</b>：SDK 输出 <c>"awaitingApproval"</c>。
    /// .NET 自带的 <c>JsonStringEnumConverter(JsonNamingPolicy.CamelCase)</c>
    /// 反序列化时**不区分大小写**，写出去的值却与 SDK 一致——所以这里显式把
    /// <c>allowIntegerValues: false</c> 打开，并在自检里断言反序列化严格。
    /// </para>
    /// <para>
    /// <b>必须在首次序列化之前完成构建</b>：<c>JsonSerializerOptions</c> 一旦被使用就冻结。
    /// 这里用静态只读字段，天然早于任何调用。
    /// </para>
    /// </remarks>
    public static JsonSerializerOptions Json { get; } = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            WriteIndented = false,
        };

        // SDK 用的是同一个转换器实例的语义：字符串枚举、camelCase。
        // allowIntegerValues: false —— 只接受名字，不接受 0/1/2。
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));

        return options;
    }
}

// ─────────────────────────────────────────────────────────────
// 条目：会话里给用户看的东西
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 会话里的一个条目。判别器属性名是 <c>kind</c>。
/// </summary>
/// <remarks>
/// <b>判别器取值照抄 SDK</b>：<c>userMessage</c> / <c>assistantMessage</c> /
/// <c>reasoning</c> / <c>toolCall</c> / <c>plan</c> / <c>notice</c>。
/// 不是类型名，也不是 kebab-case——按名字推会写错，而写错的后果是
/// 对端读到不认识的判别器后整个消息被丢弃。
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserMessageItem), "userMessage")]
[JsonDerivedType(typeof(AssistantMessageItem), "assistantMessage")]
[JsonDerivedType(typeof(ReasoningItem), "reasoning")]
[JsonDerivedType(typeof(ToolCallItem), "toolCall")]
[JsonDerivedType(typeof(PlanItem), "plan")]
[JsonDerivedType(typeof(NoticeItem), "notice")]
public abstract record ConversationItem
{
    /// <summary>条目 Id（会话内唯一，客户端据此做增量更新）。</summary>
    public required string Id { get; init; }

    /// <summary>创建时刻。客户端按它排序；数值本身不参与比较。</summary>
    public required DateTimeOffset At { get; init; }
}

/// <summary>用户发的一条消息。</summary>
public sealed record UserMessageItem : ConversationItem
{
    public required string Text { get; init; }
}

/// <summary>助手回复的正文。</summary>
/// <remarks>
/// <b>与 <see cref="ReasoningItem"/> 是两条独立的流</b>：推理与回答交错到达，
/// 共用一个 Id 会让推理追加进回答里——那正是"把推理当结论"的错误。
/// </remarks>
public sealed record AssistantMessageItem : ConversationItem
{
    /// <summary>正文。非空——<c>[JsonIgnore(WhenWritingNull)]</c> 会把 null 从线上抹掉，
    /// 而"没有正文"与"正文是空串"对客户端是同一个事实，不需要区分。</summary>
    public string Text { get; init; } = "";

    /// <summary>是否仍在流式输出。为 true 时 UI 不显示完成态标记。</summary>
    public bool IsStreaming { get; init; }
}

/// <summary>模型的思考过程（推理档位开启时才有）。</summary>
public sealed record ReasoningItem : ConversationItem
{
    /// <summary>思考文本。非空，理由同 <see cref="AssistantMessageItem.Text"/>。</summary>
    public string Text { get; init; } = "";

    public bool IsStreaming { get; init; }
}

/// <summary>一次工具调用。</summary>
/// <remarks>
/// <b>审批耗时与执行耗时分开记</b>：两者混成一个"总耗时"后，
/// "用户犹豫了 30 秒"会被读成"工具跑了 30 秒"，而那决定了用户该优化什么。
/// </remarks>
public sealed record ToolCallItem : ConversationItem
{
    /// <summary>调用 Id。与 SDK 的 <c>InvocationId</c> 对应，用于配对开始与结束。</summary>
    public required string InvocationId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>原始参数 JSON。原样透传，不在宿主里解析——解析过的会丢键序与精度。</summary>
    /// <remarks>
    /// <b>非空，且默认值 <c>"{}"</c></b>：SDK 就是如此。改成可空或空串会让
    /// 这个键从线上消失/值变化——那是一次无声的协议变更。
    /// <para>
    /// <b>为什么默认是 <c>"{}"</c> 而不是 <c>""</c></b>：前者是合法 JSON 对象，
    /// 审批卡与详情面板可以直接解析；空串会让那些地方各写一次"空串怎么办"。
    /// </para>
    /// </remarks>
    public string ArgumentsJson { get; init; } = "{}";

    /// <summary>工具输出（已完成时）。null 表示还没输出——null 时该键不出现在线上。</summary>
    /// <remarks>
    /// <b>声明顺序照抄 SDK</b>：线上字段顺序按声明顺序输出，
    /// 这里的顺序是 <c>invocationId, toolName, argumentsJson, resultText,
    /// status, diagnostic, approvalElapsed, executionElapsed,
    /// mayHaveIrreversibleEffects</c>。挪动任何一行都会改变线上 JSON 的字段顺序。
    /// </remarks>
    public string? ResultText { get; init; }

    public ToolCallStatus Status { get; init; }

    /// <summary>失败原因（<see cref="Status"/> 为失败时）。可空，理由同 <see cref="ResultText"/>。</summary>
    public string? Diagnostic { get; init; }

    /// <summary>在审批上等待的时长。</summary>
    /// <remarks>
    /// <b>与执行耗时分开记</b>：两者混成一个"总耗时"后，"用户犹豫了 30 秒"
    /// 会被读成"工具跑了 30 秒"，而那决定了用户该优化什么。
    /// </remarks>
    public TimeSpan? ApprovalElapsed { get; init; }

    /// <summary>实际执行的时长。</summary>
    public TimeSpan? ExecutionElapsed { get; init; }

    /// <summary>该操作是否可能有不可逆影响（删除、覆盖、推送等）。</summary>
    public bool MayHaveIrreversibleEffects { get; init; }
}

/// <summary>工具调用状态。</summary>
/// <remarks>
/// <b>带 <see cref="Unknown"/></b>：新版引擎可能引入这里没列的状态。
/// 缺这一档时，未识别的值会让整条消息反序列化失败；
/// 有它则降级成"状态未知"，UI 仍能显示工具名与输出。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ToolCallStatus>))]
public enum ToolCallStatus
{
    Requested,
    AwaitingApproval,
    Running,
    Succeeded,
    Failed,
    Rejected,
    Cancelled,
    Unknown,
}

/// <summary>计划（待办列表）。整体替换，不是增量。</summary>
/// <remarks>
/// <b>全量而不是增量</b>：模型每次给出完整计划，客户端直接替换。
/// 做增量合并需要客户端参与状态机，而"计划"本身是一次快照——
/// 那种复杂度换不来任何东西。
/// </remarks>
public sealed record PlanItem : ConversationItem
{
    /// <summary>步骤列表。非空（SDK 里默认空表）——空计划不会从线上消失。</summary>
    public IReadOnlyList<PlanStep> Steps { get; init; } = [];
}

/// <summary>计划里的一步。</summary>
/// <remarks>
/// <b>三个字段都是 string 而不是枚举</b>：SDK 就是这么定的。
/// 状态与优先级来自模型的自由文本，做成枚举会在遇到新值时抛异常。
/// </remarks>
public sealed record PlanStep(string Content, string Status, string Priority);

/// <summary>一条系统提示（压缩、截断、降级等）。</summary>
public sealed record NoticeItem : ConversationItem
{
    public required string Text { get; init; }

    public NoticeLevel Level { get; init; }
}

/// <summary>提示级别。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoticeLevel>))]
public enum NoticeLevel
{
    Info,
    Warning,
    Error,
}

// ─────────────────────────────────────────────────────────────
// 命令：客户端 → 宿主
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 客户端发往宿主的一条命令。判别器属性名是 <c>method</c>。
/// </summary>
/// <remarks>
/// <b>为什么根类型放在这里而不是继续继承 SDK 的</b>：只有根在本仓库，
/// 才能在里面挂自己的命令（<c>fs/list</c> 等），而不用每加一条就调一次
/// SDK 的注册接口、还得记住"必须在首次序列化之前"。
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "method")]
// 会话
[JsonDerivedType(typeof(ListSessionsCommand), "session/list")]
[JsonDerivedType(typeof(CreateSessionCommand), "session/create")]
[JsonDerivedType(typeof(OpenSessionCommand), "session/open")]
[JsonDerivedType(typeof(ResyncCommand), "session/resync")]
// 回合
[JsonDerivedType(typeof(StartTurnCommand), "turn/start")]
[JsonDerivedType(typeof(CancelTurnCommand), "turn/cancel")]
// 审批
[JsonDerivedType(typeof(RespondApprovalCommand), "approval/respond")]
// 模型
[JsonDerivedType(typeof(ListModelsCommand), "model/list")]
// 权限预设
[JsonDerivedType(typeof(ListPermissionPresetsCommand), "permission/presets")]
[JsonDerivedType(typeof(SwitchPermissionPresetCommand), "permission/switch")]
[JsonDerivedType(typeof(WatchPermissionPresetsCommand), "permission/watch")]
// 子 agent
[JsonDerivedType(typeof(ListSubagentsCommand), "subagent/list")]
[JsonDerivedType(typeof(TakeSubagentResultCommand), "subagent/result")]
[JsonDerivedType(typeof(CancelSubagentCommand), "subagent/cancel")]
// 桌面端自有
[JsonDerivedType(typeof(ListDirectoryCommand), "fs/list")]
[JsonDerivedType(typeof(SubmitFeedbackCommand), "feedback/submit")]
public abstract record ClientCommand
{
    /// <summary>请求 Id。响应里原样带回，客户端据此配对。</summary>
    public required string RequestId { get; init; }
}

/// <summary>列出已保存的会话。</summary>
public sealed record ListSessionsCommand : ClientCommand;

/// <summary>新建会话。</summary>
public sealed record CreateSessionCommand : ClientCommand
{
    public required string Workspace { get; init; }

    /// <summary>会话标题。null 表示由宿主取默认标题。</summary>
    public string? Title { get; init; }
}

/// <summary>打开已有会话。</summary>
public sealed record OpenSessionCommand : ClientCommand
{
    public required string SessionId { get; init; }
}

/// <summary>请求重发当前快照（序号缺口时用）。</summary>
public sealed record ResyncCommand : ClientCommand
{
    /// <summary>要重拉的会话。null 表示"还没打开会话，只要一个基准序号"。</summary>
    public string? SessionId { get; init; }
}

/// <summary>开始一个回合。</summary>
public sealed record StartTurnCommand : ClientCommand
{
    public required string SessionId { get; init; }
    public required string Input { get; init; }
}

/// <summary>取消正在跑的回合。</summary>
public sealed record CancelTurnCommand : ClientCommand
{
    public required string SessionId { get; init; }
}

/// <summary>回答一条审批。</summary>
public sealed record RespondApprovalCommand : ClientCommand
{
    public required ApprovalResponse Response { get; init; }
}

/// <summary>列出可用模型。</summary>
public sealed record ListModelsCommand : ClientCommand;

/// <summary>列出权限预设。</summary>
public sealed record ListPermissionPresetsCommand : ClientCommand
{
    public string? SessionId { get; init; }
}

/// <summary>切换权限预设。</summary>
public sealed record SwitchPermissionPresetCommand : ClientCommand
{
    /// <summary>所属会话。空串表示全局默认。</summary>
    /// <summary>所属会话。null 表示全局默认。</summary>
    /// <c>sessionId</c> 在 <c>key</c> 之前。</remarks>
    public string? SessionId { get; init; }

    public required string Key { get; init; }
}

/// <summary>订阅/退订权限预设变化推送。</summary>
public sealed record WatchPermissionPresetsCommand : ClientCommand
{
    public string? SessionId { get; init; }
    public bool Enabled { get; init; }
}

/// <summary>列出子 agent。</summary>
public sealed record ListSubagentsCommand : ClientCommand
{
    public required string SessionId { get; init; }
}

/// <summary>取走一个子 agent 的结果（取走即消费）。</summary>
public sealed record TakeSubagentResultCommand : ClientCommand
{
    public required string SessionId { get; init; }
    public required string SubagentId { get; init; }
}

/// <summary>取消一个子 agent。</summary>
public sealed record CancelSubagentCommand : ClientCommand
{
    public required string SessionId { get; init; }
    public required string SubagentId { get; init; }
    public string? Reason { get; init; }
}

// ─────────────────────────────────────────────────────────────
// 事件：宿主 → 客户端
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 宿主发往客户端的一条事件。判别器属性名是 <c>method</c>。
/// </summary>
/// <remarks>
/// <b>seq 是全局单调计数器，不是每会话独立</b>：同一条连接上的所有事件
/// 共用它，客户端按严格递增检测缺口。每会话各自从 0 计数会让同一连接上的
/// 序号错乱，客户端会误判为丢消息。
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "method")]
// 会话
[JsonDerivedType(typeof(SessionListedEvent), "session/listed")]
[JsonDerivedType(typeof(SessionOpenedEvent), "session/opened")]
// 回合
[JsonDerivedType(typeof(TurnStartedEvent), "turn/started")]
[JsonDerivedType(typeof(TurnCompletedEvent), "turn/completed")]
// 条目
[JsonDerivedType(typeof(ItemAddedEvent), "item/added")]
[JsonDerivedType(typeof(ItemUpdatedEvent), "item/updated")]
// 审批
[JsonDerivedType(typeof(ApprovalRequestedEvent), "approval/requested")]
// 引擎
[JsonDerivedType(typeof(EngineStatusEvent), "engine/status")]
[JsonDerivedType(typeof(CommandFailedEvent), "command/failed")]
// 模型
[JsonDerivedType(typeof(ModelsListedEvent), "model/listed")]
// 权限预设
[JsonDerivedType(typeof(PermissionPresetListedEvent), "permission/listed")]
// 子 agent
[JsonDerivedType(typeof(SubagentsListedEvent), "subagent/listed")]
[JsonDerivedType(typeof(SubagentUpdatedEvent), "subagent/updated")]
// 桌面端自有
[JsonDerivedType(typeof(DirectoryListedEvent), "fs/listed")]
[JsonDerivedType(typeof(WorkspaceChangedEvent), "fs/changed")]
public abstract record ServerEvent
{
    /// <summary>连接内全局单调序号。客户端据此检测缺口。</summary>
    public required long Sequence { get; init; }

    /// <summary>对应的请求 Id。推送类事件为 null。</summary>
    public string? RequestId { get; init; }
}

/// <summary>会话列表。</summary>
public sealed record SessionListedEvent : ServerEvent
{
    public required IReadOnlyList<SessionSummary> Sessions { get; init; }
}

/// <summary>会话已打开，附带完整快照。</summary>
/// <remarks>
/// <b>带快照而不是让客户端逐条拉</b>：重连与首次打开走同一条路径。
/// 少了快照就要区分"新开"与"重连"两套逻辑，而两套逻辑必然漂移。
/// </remarks>
public sealed record SessionOpenedEvent : ServerEvent
{
    public required SessionSnapshot Snapshot { get; init; }
}

/// <summary>回合开始。</summary>
public sealed record TurnStartedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required string TurnId { get; init; }
}

/// <summary>回合结束。</summary>
public sealed record TurnCompletedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required string TurnId { get; init; }
    public required TurnStatus Status { get; init; }

    /// <summary>失败/取消时的诊断。null 表示没有——可空，null 时该键不在线上。</summary>
    public string? Diagnostic { get; init; }
}

/// <summary>回合状态。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TurnStatus>))]
public enum TurnStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>新增一个条目。</summary>
/// <remarks>
/// <b>整条发，不是增量</b>：客户端不做增量合并。合并逻辑放在客户端意味着
/// 两边各有一份状态机，而它们一旦不同就表现为"偶尔少一段文字"。
/// </remarks>
public sealed record ItemAddedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required string TurnId { get; init; }
    public required ConversationItem Item { get; init; }
}

/// <summary>更新一个已有条目（流式追加、工具状态变化）。</summary>
public sealed record ItemUpdatedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required ConversationItem Item { get; init; }
}

/// <summary>引擎请求审批。</summary>
public sealed record ApprovalRequestedEvent : ServerEvent
{
    public required ApprovalRequest Request { get; init; }
}

/// <summary>引擎状态变化。</summary>
public sealed record EngineStatusEvent : ServerEvent
{
    public required EngineState State { get; init; }

    /// <summary>当前模型名。null 表示引擎还没确定——可空，null 时该键不在线上。</summary>
    /// <remarks><b>声明顺序有意义</b>：线上字段顺序按声明顺序输出，SDK 里是
    /// <c>state, modelName, detail</c>。</remarks>
    public string? ModelName { get; init; }

    /// <summary>补充说明（失败原因等）。可空。</summary>
    public string? Detail { get; init; }
}

/// <summary>引擎状态。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EngineState>))]
public enum EngineState
{
    Starting,
    Ready,
    Busy,
    Failed,
}

/// <summary>一条命令失败了。</summary>
/// <remarks>
/// <b>失败要有专门的事件而不是靠超时</b>：客户端等不到响应时的表现是"按钮点了没反应"，
/// 用户无法区分"还没跑完"与"跑失败没告诉它"。
/// </remarks>
public sealed record CommandFailedEvent : ServerEvent
{
    /// <summary>失败原因分类。</summary>
    /// <remarks><b>声明顺序有意义</b>：线上按声明顺序输出，SDK 里
    /// <c>reason</c> 在 <c>message</c> 之前。</remarks>
    public required CommandFailureReason Reason { get; init; }

    public required string Message { get; init; }

    /// <summary>失败的那条命令的 method。空串表示未知。</summary>
    /// <remarks>
    /// <b>必须改名</b>：多态判别器占用了 <c>method</c> 这个键，同名的属性会让
    /// <c>System.Text.Json</c> 在构造 <c>JsonTypeInfo</c> 时抛
    /// <c>InvalidOperationException</c>——不是"少个字段"，是整个序列化不可用。
    /// SDK 自己也撞过，它的解法就是显式改名成 <c>failedMethod</c>。
    /// </remarks>
    [JsonPropertyName("failedMethod")]
    public string? Method { get; init; }
}

/// <summary>命令失败的原因分类。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommandFailureReason>))]
public enum CommandFailureReason
{
    Unsupported,
    Invalid,
    Conflict,
    Failed,
}

/// <summary>模型清单。</summary>
public sealed record ModelsListedEvent : ServerEvent
{
    public required IReadOnlyList<ModelInfo> Models { get; init; }
    public required IReadOnlyList<string> Providers { get; init; }
    public string CurrentModelId { get; init; } = "";
}

/// <summary>一个模型的元数据。</summary>
/// <remarks>
/// <b>只给上下文窗口与输出上限，不给 wire format 细节</b>：后者是引擎的实现选择，
/// 客户端拿它没有用处，还会让协议跟着 provider 的差异漂。
/// </remarks>
public sealed record ModelInfo(
    string ProviderId,
    string Id,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsReasoning);

/// <summary>权限预设清单（对某条查询的响应，或订阅后的推送）。</summary>
public sealed record PermissionPresetListedEvent : ServerEvent
{
    /// <summary>所属会话。null 表示全局默认。</summary>
    public string? SessionId { get; init; }

    public required PermissionPresetState State { get; init; }

    /// <summary>查询失败的原因。null 表示成功——<b>可空</b>，
    /// 成功时该键不出现在线上（SDK 如此）。</summary>
    public string? Error { get; init; }
}

/// <summary>当前权限预设与可选项。</summary>
public sealed record PermissionPresetState(
    string CurrentKey,
    string CurrentName,
    IReadOnlyList<PermissionPresetInfo> Options);

/// <summary>一个权限预设。</summary>
/// <remarks>
/// <b><c>Selectable</c> 单独给出，而不是让客户端按 <c>Key</c> 猜</b>：
/// 有些预设（如"只读"）在某些场景下不可选，把判断规则放在客户端会让两个客户端
/// 对同一状态给出不同的可用性。
/// </remarks>
public sealed record PermissionPresetInfo(
    string Key,
    string Name,
    string? Description,
    bool Selectable);

/// <summary>子 agent 清单。</summary>
public sealed record SubagentsListedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required IReadOnlyList<SubagentInfo> Agents { get; init; }

    /// <summary>本次取走的最终结果。没有可取的结果时为 null。</summary>
    public SubagentOutcome? Outcome { get; init; }

    /// <summary>查询失败的原因。null 表示成功。</summary>
    public string? Error { get; init; }
}

/// <summary>某个子 agent 状态变化。</summary>
public sealed record SubagentUpdatedEvent : ServerEvent
{
    public required string SessionId { get; init; }
    public required SubagentInfo Agent { get; init; }
}

/// <summary>一个子 agent。</summary>
/// <remarks>
/// <b><c>ToolRounds</c> 可空</b>：null 表示"还没跑够一轮"或"引擎不报这个数"，
/// 与 0（"跑了 0 轮"）是不同的事实。
/// </remarks>
public sealed record SubagentInfo(
    string Id,
    string Label,
    SubagentRunState State,
    DateTimeOffset StartedAt,
    string? ParentTurnId,
    int? ToolRounds);

/// <summary>子 agent 运行状态。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubagentRunState>))]
public enum SubagentRunState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>一个子 agent 的最终结果。</summary>
/// <remarks>
/// <b>字段名照抄 SDK</b>（<c>Id</c> / <c>Text</c> / <c>ToolRounds</c>）：
/// 线上 JSON 的键名由属性名推得，改名字就是改协议。
/// </remarks>
public sealed record SubagentOutcome(
    string Id,
    SubagentRunState State,
    string? Text,
    string? Diagnostic,
    int ToolRounds);

// ─────────────────────────────────────────────────────────────
// 会话快照
// ─────────────────────────────────────────────────────────────

/// <summary>会话快照。打开会话与断线重连都走它。</summary>
public sealed record SessionSnapshot(
    string Id,
    string Title,
    string Workspace,
    long Sequence,
    IReadOnlyList<TurnSnapshot> Turns,
    bool IsRunning,
    string? ModelName);

/// <summary>一个回合的快照。</summary>
/// <remarks>
/// <b>条目挂在这个回合下，而不是给会话一个扁平列表</b>：UI 的视觉分组按回合走
/// （每条回复带自己的状态行）。扁平的条目列表在渲染时还得重建分组，
/// 而重建规则和宿主的规则一旦不同，分组就会错位。
/// </remarks>
public sealed record TurnSnapshot(
    string Id,
    DateTimeOffset StartedAt,
    TurnStatus Status,
    IReadOnlyList<ConversationItem> Items);

/// <summary>会话列表里的一行。</summary>
public sealed record SessionSummary(
    string Id,
    string Title,
    string Workspace,
    DateTimeOffset UpdatedAt,
    int ItemCount,
    bool HasPendingApproval);

// ─────────────────────────────────────────────────────────────
// 审批
// ─────────────────────────────────────────────────────────────

/// <summary>引擎发起的一条审批请求。</summary>
/// <remarks>
/// <b><c>ArgumentsJson</c> 原样透传</b>：审批卡要展示模型到底要做什么，
/// 宿主解析再重组会丢掉键序、数字精度与注释；而用户判断风险依赖的正是这些细节。
/// <para>
/// <b><c>MayHaveIrreversibleEffects</c> 由引擎判定</b>：客户端不自己按工具名列表猜。
/// 猜的后果是新增一个危险工具时忘了更新列表，而那种遗漏看起来完全正常。
/// </para>
/// </remarks>
public sealed record ApprovalRequest(
    string RequestId,
    string SessionId,
    string TurnId,
    string InvocationId,
    string ToolName,
    string Summary,
    string ArgumentsJson,
    string PermissionScope,
    DateTimeOffset ExpiresAt,
    bool MayHaveIrreversibleEffects);

/// <summary>对审批的回答。</summary>
public sealed record ApprovalResponse(
    string RequestId,
    ApprovalDecision Decision,
    string? Reason);

/// <summary>审批结论。</summary>
/// <remarks>
/// <b><see cref="DenyAndStop"/> 与 <see cref="Deny"/> 必须分开</b>：
/// 前者要终止整个回合（模型不该继续试别的路），后者只拒这一次。
/// 合成一档后，"拒绝并停止"退化成"拒绝"，模型会绕道再来一次。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ApprovalDecision>))]
public enum ApprovalDecision
{
    AllowOnce,
    AllowForSession,
    Deny,
    DenyAndStop,
}
