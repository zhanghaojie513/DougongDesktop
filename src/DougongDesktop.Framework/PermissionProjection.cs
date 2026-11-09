using Dougong.Agent;
using Dougong.Hosting;
using Dougong.HostProtocol;

namespace DougongDesktop.Framework;

/// <summary>
/// 权限预设的 SDK 形状 → 协议形状。
/// </summary>
/// <remarks>
/// <b>为什么包一层而不是让 AgentHost 直接用 <c>ProtocolMapping</c></b>：
/// AgentHost 目前只引用 Ipc 与 Framework，不直接引用 SDK 包。把映射放在
/// 这一层，AgentHost 拿到的就已经是纯协议类型——与 <c>EngineOptions</c>、
/// <c>SessionRuntime</c> 的做法一致：Framework 是唯一知道 SDK 长什么样的地方。
/// <para>
/// 同时把"预设注册表"本身也封在这里，避免上层为了拿状态而被迫认识
/// <c>PermissionPresets</c> 这个 SDK 类型。
/// </para>
/// </remarks>
public static class PermissionProjection
{
    /// <summary>把预设状态映射成可直接上线的协议形状。</summary>
    /// <param name="presets">引擎侧的预设注册表。</param>
    /// <param name="sessionId">所属会话；null 表示全局默认。</param>
    public static PermissionPresetState ToProtocol(PermissionPresets presets, string? sessionId) =>
        ProtocolMapping.ToProtocol(presets, sessionId);

    /// <summary>建一份默认预设表。</summary>
    /// <remarks>
    /// <b>全进程只该调用一次</b>：同一个实例既要给审批链，也要给协议层。
    /// 建两份的后果是"界面切了、引擎没变"——SDK 侧明确列为唯一失败模式。
    /// </remarks>
    public static PermissionPresets CreatePresets(ApprovalPolicy policy) =>
        PermissionPresets.CreateDefault(policy);

    /// <summary>切档。未知键抛异常（SDK 刻意如此，避免"以为切了其实没切"）。</summary>
    public static Task<ApprovalPolicy> SwitchAsync(
        PermissionPresets presets, string key, CancellationToken ct = default) =>
        presets.SwitchAsync(key, ct);

    /// <summary>当前档位键。</summary>
    public static string CurrentKey(PermissionPresets presets) => presets.CurrentKey;
}
