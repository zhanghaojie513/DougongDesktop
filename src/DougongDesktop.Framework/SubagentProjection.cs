using Dougong.Agent;
using Dougong.Hosting;
using Dougong.HostProtocol;

namespace DougongDesktop.Framework;

/// <summary>
/// 子 agent 的 SDK 形状 → 协议形状。
/// </summary>
/// <remarks>
/// <b>为什么要这一层</b>：与 <see cref="PermissionProjection"/> 同理——
/// AgentHost 只认 Ipc 与 Framework，不直接认识 SDK 类型。
/// <para>
/// <b>状态映射复用 SDK 的实现，不自己写</b>：<c>ProtocolMapping.ToProtocol(handle)</c>
/// 取的是 <c>SubagentHandle.CurrentStatus</c>。"
/// 已结束但没有结果"该算取消还是完成，是一条只该有一份的判断——
/// 映射层重复实现它会与引擎各错一次。
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

    /// <summary>把一个句柄映射成协议形状（用于 <c>subagent/updated</c> 推送）。</summary>
    public static SubagentInfo ToProtocol(SubagentHandle handle) =>
        ProtocolMapping.ToProtocol(handle);

    /// <summary>把结果映射成协议形状（用于 <c>subagent/result</c>）。</summary>
    public static SubagentOutcome ToProtocol(SubagentResult result) =>
        ProtocolMapping.ToProtocol(result);
}
