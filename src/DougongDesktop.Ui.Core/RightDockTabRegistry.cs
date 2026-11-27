using Avalonia.Controls;

namespace DougongDesktop.Ui.Core;

/// <summary>右栏 tab 在 <c>DockLayout</c> 模式下的停靠位置。</summary>
public enum RightDockTabPlacement
{
    /// <summary>侧边工具面板（文件 / 改动 / 计划 / 轨迹 / 插件工具等）。</summary>
    Tool,

    /// <summary>主文档面板（终端或自定义编辑器）。</summary>
    Document,
}

/// <summary>
/// 一条右栏 tab 注册项（P8e E5：tab 类型注册表）。
/// </summary>
/// <param name="Key">唯一键（如 <c>"files"</c>、<c>"terminal"</c> 或插件自定义键）。</param>
/// <param name="Title">标签标题。</param>
/// <param name="BuildContent">惰性内容工厂，首次选中或刷新时调用。</param>
/// <param name="Order">展示顺序（升序，同序按注册先后）。内置 tab 占 10–60，插件默认 100。</param>
/// <param name="Priority">同 key 竞争优先级（数值小胜出，内置用 100，插件覆写给更小值）。</param>
/// <param name="IsEnabled">是否可点击（未实现项为 false）。</param>
/// <param name="Placement">Dock 模式下的停靠位置（Tool / Document）。</param>
/// <param name="Origin">来源（默认 <see cref="SlotOccupantOrigin.Installed"/>）。</param>
public sealed record RightDockTabDescriptor(
    string Key,
    string Title,
    Func<Control> BuildContent,
    int Order = 100,
    int Priority = 1000,
    bool IsEnabled = true,
    RightDockTabPlacement Placement = RightDockTabPlacement.Tool,
    SlotOccupantOrigin Origin = SlotOccupantOrigin.Installed);

/// <summary>
/// 右栏 tab 注册表（P8e E5）：管理内置与第三方插件贡献的右栏面板。
/// </summary>
/// <remarks>
/// <b>与 <see cref="SlotRegistry"/> 同一套冲突与卸载语义</b>：
/// 同 key 按 <see cref="RightDockTabDescriptor.Priority"/> 数值最小者胜出（并列按先注册者胜）；
/// 返回的 <see cref="IDisposable"/> 句柄在释放时彻底移除该项并恢复次优兜底，
/// 确保插件卸载零残留。
/// </remarks>
public sealed class RightDockTabRegistry
{
    private sealed record Entry(RightDockTabDescriptor Descriptor, int RegistrationIndex);

    private readonly Dictionary<string, List<Entry>> _byKey = new(StringComparer.Ordinal);
    private int _nextIndex;

    /// <summary>注册或卸载改变生效清单时触发。</summary>
    public event Action? Changed;

    /// <summary>注册一个右栏 tab；释放返回的句柄即可卸载。</summary>
    public IDisposable Register(RightDockTabDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Title);
        ArgumentNullException.ThrowIfNull(descriptor.BuildContent);

        var entry = new Entry(descriptor, _nextIndex++);
        if (!_byKey.TryGetValue(descriptor.Key, out var list))
            _byKey[descriptor.Key] = list = [];

        list.Add(entry);
        Changed?.Invoke();

        return new RegistrationHandle(this, descriptor.Key, entry);
    }

    /// <summary>按 key 解析当前生效的 tab（Priority 最小、并列先注册者胜）。</summary>
    public RightDockTabDescriptor? Resolve(string key)
    {
        if (!_byKey.TryGetValue(key, out var list) || list.Count == 0)
            return null;

        return PickWinner(list).Descriptor;
    }

    /// <summary>指定 key 当前是否有生效注册。</summary>
    public bool Contains(string key) => _byKey.TryGetValue(key, out var list) && list.Count > 0;

    /// <summary>返回所有生效的 tab（按 <c>Order</c> 升序、同序按注册先后）。</summary>
    public IReadOnlyList<RightDockTabDescriptor> ResolveAll() =>
        _byKey.Values
            .Where(l => l.Count > 0)
            .Select(PickWinner)
            .OrderBy(e => e.Descriptor.Order)
            .ThenBy(e => e.RegistrationIndex)
            .Select(e => e.Descriptor)
            .ToArray();

    private static Entry PickWinner(List<Entry> list) =>
        list.OrderBy(e => e.Descriptor.Priority)
            .ThenBy(e => e.RegistrationIndex)
            .First();

    private void Unregister(string key, Entry entry)
    {
        if (!_byKey.TryGetValue(key, out var list)) return;
        if (!list.Remove(entry)) return;
        if (list.Count == 0) _byKey.Remove(key);

        Changed?.Invoke();
    }

    private sealed class RegistrationHandle(RightDockTabRegistry owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Unregister(key, entry);
        }
    }
}

/// <summary>
/// 每会话独立的右栏停靠面状态（P8e E5：对照 dsh <c>ui-sidebar-right</c> 每会话停靠面）。
/// </summary>
public sealed record RightDockSurfaceState(string SessionKey, string ActiveTab);

/// <summary>按会话 Id 记住并恢复每个会话的右栏选中 tab。</summary>
public sealed class RightDockSurfaceStore
{
    public const string DraftSessionKey = "__draft";

    private readonly Dictionary<string, RightDockSurfaceState> _surfaces = new(StringComparer.Ordinal);

    /// <summary>当前已记录停靠面的会话数。</summary>
    public int Count => _surfaces.Count;

    /// <summary>保存某会话当前的选中 tab。</summary>
    public void SaveActiveTab(string? sessionId, string activeTab)
    {
        if (string.IsNullOrWhiteSpace(activeTab)) return;
        var key = NormalizeKey(sessionId);
        _surfaces[key] = new RightDockSurfaceState(key, activeTab);
    }

    /// <summary>读取某会话上次停靠的 tab；未记录时返回 <paramref name="fallback"/>。</summary>
    public string GetActiveTab(string? sessionId, string fallback)
    {
        var key = NormalizeKey(sessionId);
        return _surfaces.TryGetValue(key, out var state) ? state.ActiveTab : fallback;
    }

    /// <summary>清除指定会话的停靠面记录。</summary>
    public void Remove(string? sessionId) => _surfaces.Remove(NormalizeKey(sessionId));

    private static string NormalizeKey(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? DraftSessionKey : sessionId.Trim();
}
