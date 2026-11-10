using Dougong.Agent;

namespace DougongDesktop.Framework;

/// <summary>
/// 子 agent 的引擎形状 → 本仓库契约形状。
/// </summary>
/// <remarks>
/// <b>为什么要这一层</b>：与 <see cref="PermissionProjection"/> 同理——
/// AgentHost 只认 Ipc 与 Framework，不直接认识 SDK 类型。
/// <para>
/// <b>为什么自己写映射而不是调 SDK 的 <c>ProtocolMapping.ToProtocol</c></b>：
/// 那个函数曾经是这里的依赖，代价是整个桌面端要引用
/// <c>Dougong.HostProtocol</c>（SDK 的契约包）。而它做的事只是
/// "读句柄的公开属性、包一层"。去掉之后本层不再依赖 SDK 的协议包。
/// </para>
/// <para>
/// <b>必须复用的那条判断是 <see cref="SubagentHandle.CurrentStatus"/></b>，
/// 不是 <c>ProtocolMapping</c>：那里编码了"已结束但没有结果该算取消还是完成"。
/// 它是引擎自己的属性，我们直接读它——既不重复实现，也不需要协议包。
/// </para>
/// </remarks>
public static class SubagentProjection
{
    /// <summary>建一份注册表。<b>全进程只该调用一次</b>。</summary>
    /// <remarks>
    /// 建两份是 SDK 侧明确的"唯一失败模式"：工具用一个、协议层用一个，
    /// 结果是 <c>subagent/list</c> 永远返回空，而额度照样在烧。
    /// </remarks>
    public static SubagentRegistry CreateRegistry(int maxDepth = 3, int maxConcurrent = 4) =>
        new(maxDepth, maxConcurrent);

    /// <summary>把一个句柄映射成契约形状（用于 <c>subagent/updated</c> 推送）。</summary>
    /// <remarks>
    /// <b>状态取 <see cref="SubagentHandle.CurrentStatus"/></b>，不要自己拼
    /// <c>IsCompleted</c> 与 <c>Result</c>：只看 <c>IsCompleted</c> 会把
    /// "已结束、等你去取结果"报成"还在跑"，界面于是一直转圈。
    /// <para>
    /// <b>可空字段直接透传（不写 <c>?? ""</c>）</b>：两边都可空，线上语义是
    /// "null 时该键不存在"。补空串会让客户端读到"父回合是空字符串"。
    /// </para>
    /// </remarks>
    public static SubagentInfo ToProtocol(SubagentHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return new SubagentInfo(
            handle.Id,
            handle.Label,
            FromEngine(handle.CurrentStatus),
            handle.StartedAt,
            handle.ParentTurnId,
            handle.ToolRounds);
    }

    /// <summary>把结果映射成契约形状（用于 <c>subagent/result</c>）。</summary>
    public static SubagentOutcome ToProtocol(SubagentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SubagentOutcome(
            result.Id,
            FromEngine(result.Status),
            result.Text,
            result.Diagnostic,
            result.ToolRounds);
    }

    /// <summary>引擎的子 agent 状态 → 本仓库的。</summary>
    /// <remarks>
    /// <b>为什么不用 <c>(SubagentRunState)(int)status</c> 强转</b>：两个枚举成员
    /// 目前一一对应，但 <c>SubagentStatus</c> 的注释明确说了 "Running 是后加的、
    /// 排在末尾是为不移动已有值"——也就是说序号被当作公开契约维护。
    /// 一旦哪一边插入成员，强转会按序号对齐，把新值映射成一个语义无关的旧值，
    /// 而界面上不报错、只是显示成错的。
    /// <para>
    /// <b>穷尽 switch 让新增成员在<em>运行时</em>立刻暴露</b>（不是编译期——
    /// 两边都是运行时）带一条能直接照做的消息，好过静默错位。
    /// SDK 升版本时仍要人工看一眼枚举变更。
    /// </para>
    /// </remarks>
    private static SubagentRunState FromEngine(SubagentStatus status) => status switch
    {
        SubagentStatus.Running => SubagentRunState.Running,
        SubagentStatus.Completed => SubagentRunState.Completed,
        SubagentStatus.Failed => SubagentRunState.Failed,
        SubagentStatus.Cancelled => SubagentRunState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status,
            "引擎出现了本仓库契约里没有的子 agent 状态。"
            + "补到 DougongDesktop.Ipc.SubagentRunState 并在此处映射——"
            + "静默归到某个已有状态会让 UI 显示错误结论。"),
    };
}
