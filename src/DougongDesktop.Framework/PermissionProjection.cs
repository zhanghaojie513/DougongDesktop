using Dougong.Agent;

namespace DougongDesktop.Framework;

/// <summary>
/// 权限预设的引擎形状 → 本仓库契约形状。
/// </summary>
/// <remarks>
/// <b>为什么包一层而不是让 AgentHost 直接碰 <c>PermissionPresets</c></b>：
/// AgentHost 只引用 Ipc 与 Framework，不直接引用 SDK 包。把映射放在
/// 这一层，AgentHost 拿到的就已经是纯契约类型——与 <c>EngineOptions</c>、
/// <c>SessionRuntime</c> 的做法一致：Framework 是唯一知道 SDK 长什么样的地方。
/// <para>
/// <b>为什么自己写映射而不是调 SDK 的 <c>ProtocolMapping.ToProtocol</c></b>：
/// 那个函数曾经是这里的依赖，代价是整个桌面端要引用
/// <c>Dougong.HostProtocol</c>（SDK 的契约包）。而它做的事只是
/// "取 <c>Options()</c>、包一层、找当前档的显示名"——没有我们重写不了的判断。
/// 去掉之后本层不再依赖 SDK 的协议包。
/// </para>
/// <para>
/// <b>保留下来的那条判断是 <c>Options()</c> 内部的</b>：它处理"派生的
/// <c>custom</c> 可显示但不可选"。那条必须复用——自己拼会让它复制到每个宿主，
/// 而漏掉的表现是界面允许点选"自定义"、点下去抛异常。
/// </para>
/// </remarks>
public static class PermissionProjection
{
    /// <summary>把预设状态映射成可直接上线的契约形状。</summary>
    /// <param name="presets">引擎侧的预设注册表。</param>
    /// <param name="sessionId">
    /// 所属会话。<b>当前未使用</b>——引擎的预设表是会话级的，
    /// 保留参数是为了将来按作用域分表时不改签名。
    /// </param>
    /// <remarks>
    /// <b>当前档的显示名不回退成空串</b>：状态栏显示空白比显示键名更让人困惑，
    /// 所以找不到对应选项时用键本身（<c>custom</c> 就属于这种情况）。
    /// </remarks>
    public static PermissionPresetState ToProtocol(PermissionPresets presets, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(presets);
        _ = sessionId;

        var options = presets.Options()
            .Select(o => new PermissionPresetInfo(o.Value, o.Name, o.Description, o.Selectable))
            .ToList();

        var currentKey = presets.CurrentKey;
        var currentName = options
            .FirstOrDefault(o => string.Equals(o.Key, currentKey, StringComparison.Ordinal))
            ?.Name ?? currentKey;

        return new PermissionPresetState(currentKey, currentName, options);
    }

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
