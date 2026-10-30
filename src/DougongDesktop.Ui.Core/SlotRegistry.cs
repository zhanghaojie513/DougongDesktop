using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace DougongDesktop.Ui.Core;

/// <summary>
/// slot 的三种形态（详见 vault《Dougong-Desktop-Slot机制规格》§1）。
/// </summary>
/// <remarks>
/// <b>为什么没有 <c>Chain</c></b>：dsh 有第四种 <c>chain</c>（条目自行提名，按 priority 选一个），
/// 但它全库只有一个使用点，且语义是"匹配决策"而非"渲染占位"——
/// 混进 slot 体系会让两种失败模式（没人认领 / 认领了但崩了）说不清。
/// 它降级为普通责任链（见 <see cref="SlotRegistry"/> 的注释）。
/// </remarks>
public enum SlotKind
{
    /// <summary>一个位置一个占位者。多个注册时按 <see cref="SlotOccupant.Priority"/> 选最小者。</summary>
    Single,

    /// <summary>多个占位者并列，顺序 = 注册顺序。</summary>
    List,

    /// <summary>按 key 分派，一个 key 一个占位者。同 key 按 Priority 选最小者。</summary>
    Keyed,
}

/// <summary>
/// 一个占位者。
/// </summary>
/// <param name="Id">标识，用于诊断与冲突报告。</param>
/// <param name="Priority">
/// 数值小 = 优先。默认 1000。
/// <para>
/// <b>这是"第三方替换内置实现"的唯一机制</b>：内置用 100 段，第三方用更小的值覆盖。
/// </para>
/// </param>
/// <param name="Template">内容工厂。Avalonia 原生从它构建控件。</param>
/// <param name="Key">仅 <see cref="SlotKind.Keyed"/> 使用。</param>
public sealed record SlotOccupant(
    string Id,
    int Priority,
    IDataTemplate Template,
    string? Key = null)
{
    /// <summary>注册顺序。由注册表赋值，用于同 priority 时稳定排序。</summary>
    internal int RegistrationIndex { get; init; }
}

/// <summary>保留的 slot key。插件与内置模块都不可占用。</summary>
public static class ReservedSlots
{
    public const string Main = "main";

    /// <summary>会话界面。占用它会让用户切走后丢掉会话上下文。</summary>
    public const string Conversation = "conversation";

    public const string Sidebar = "shell.leading";
    public const string RightBar = "shell.trailing";
    public const string Bottom = "shell.bottom";
    public const string Overlay = "shell.overlay";
}

/// <summary>注册冲突。带三个名字——只说"冲突"没法排查。</summary>
public sealed class SlotConflictException(string slotKey, string existing, string incoming)
    : Exception($"slot '{slotKey}' 已有占位者 '{existing}'，拒绝 '{incoming}'。")
{
    public string SlotKey { get; } = slotKey;
    public string Existing { get; } = existing;
    public string Incoming { get; } = incoming;
}

/// <summary>
/// slot 注册表。
/// </summary>
/// <remarks>
/// <b>它只负责"谁是赢家"，不负责渲染</b>——渲染交给 Avalonia 原生控件：
/// <list type="bullet">
/// <item><see cref="SlotKind.Single"/> → <c>ContentControl.ContentTemplate</c></item>
/// <item><see cref="SlotKind.List"/> → <c>ItemsControl</c> + <see cref="ObservableCollection{T}"/></item>
/// <item><see cref="SlotKind.Keyed"/> → 按 key 取模板</item>
/// </list>
/// 自造控件容器的代价是：要自己维护当前实例、手写变更通知、自己处理空内容，
/// 而换 <c>Content</c> 时还会误重建控件丢掉滚动位置与焦点。
/// <para>
/// <b>没有 declaration epoch</b>：dsh 用它让 <c>inject</c> 判断"注册表是否过期"，
/// 在 Avalonia 里等价的手段是让集合可观察（<see cref="ObservableCollection{T}"/>
/// 自动通知 <c>ItemsControl</c>），不需要手写 epoch。
/// </para>
/// </remarks>
public sealed class SlotRegistry
{
    private readonly Dictionary<string, List<SlotOccupant>> _registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObservableCollection<SlotOccupant>> _lists = new(StringComparer.Ordinal);
    private int _nextIndex;

    /// <summary>
    /// 注册一个占位者。返回句柄，释放即注销。
    /// </summary>
    /// <exception cref="SlotConflictException">占用保留 key，或 keyed 同 key 重复。</exception>
    public IDisposable Register(string slotKey, SlotOccupant occupant)
    {
        // 保留 key 硬拦：插件占了 conversation，用户切走后会话界面就没了
        if (slotKey == ReservedSlots.Main && occupant.Key == ReservedSlots.Conversation)
            throw new SlotConflictException(slotKey, ReservedSlots.Conversation, occupant.Id);

        var stamped = occupant with { RegistrationIndex = _nextIndex++ };

        if (!_registrations.TryGetValue(slotKey, out var list))
        {
            list = [];
            _registrations[slotKey] = list;
        }

        _registrations[slotKey] = list = [.. list, stamped];

        // list kind 直接进可观察集合，ItemsControl 会自动跟踪增删
        if (slotKey is ReservedSlots.Bottom or ReservedSlots.Overlay)
        {
            if (!_lists.TryGetValue(slotKey, out var observable))
            {
                observable = [];
                _lists[slotKey] = observable;
            }

            observable.Add(stamped);
        }

        return new Registration(this, slotKey, stamped);
    }

    /// <summary>
    /// 解析出应该显示哪个模板。
    /// </summary>
    /// <param name="key">仅 <see cref="SlotKind.Keyed"/> 使用。</param>
    /// <returns>没有占位者时返回 null，由调用方的 Fallback 决定显示什么。</returns>
    public IDataTemplate? Resolve(string slotKey, SlotKind kind, string? key = null)
    {
        if (!_registrations.TryGetValue(slotKey, out var list) || list.Count == 0)
            return null;

        return kind switch
        {
            SlotKind.Single => PickWinner(list)?.Template,

            // list 不走这里——调用方拿 ObservableCollection 交给 ItemsControl
            SlotKind.List => throw new InvalidOperationException(
                $"slot '{slotKey}' 是 list kind，用 {nameof(GetList)} 取集合，不要用 Resolve。"),

            SlotKind.Keyed => PickWinner(list.Where(o => o.Key == key))?.Template,

            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>
    /// 安全构建：占位者的模板抛异常时返回 null 并回调诊断，不向上抛。
    /// </summary>
    /// <remarks>
    /// <b>故障隔离是 slot 框架的职责，不是渲染侧的</b>。理由：
    /// <list type="bullet">
    /// <item>一个插件崩了不能影响同 slot 的其他占位者（list kind 尤其明显）</item>
    /// <item>所有 UI 插件都失败时，App 必须还能用 Fallback 起来</item>
    /// </list>
    /// 这与 agent 插件不同——引擎插件失败可以拒绝接单，UI 插件失败只是少一块。
    /// <para>
    /// 失败必须**回报**而不是静默吞掉：只写日志的话，用户看到"某个 slot 空了但不报错"，
    /// 而这个信息要出现在插件管理页里。
    /// </para>
    /// </remarks>
    public Control? BuildSafely(
        IDataTemplate template,
        Action<string, Exception>? onFailure = null,
        object? dataContext = null)
    {
        try
        {
            return template.Build(dataContext) as Control;
        }
        catch (Exception ex)
        {
            onFailure?.Invoke(template.GetType().Name, ex);
            return null;
        }
    }

    /// <summary>取 list kind 的可观察集合，直接绑到 <c>ItemsControl.ItemsSource</c>。</summary>
    public ObservableCollection<SlotOccupant> GetList(string slotKey)
    {
        if (!_lists.TryGetValue(slotKey, out var observable))
        {
            observable = [];
            _lists[slotKey] = observable;
        }

        return observable;
    }

    /// <summary>
    /// 有 keyed 占位者时判断某 key 是否存在。
    /// </summary>
    /// <remarks>
    /// <c>SelectPanel</c> 用它做**实时候选检查**：显式指名一个不存在的面板要抛错，
    /// 而不是切到空白页。这与渲染侧的"未命中返回 null 降级"是两回事——
    /// 前者是用户明确要求，后者是数据驱动。
    /// </remarks>
    public bool HasKey(string slotKey, string key) =>
        _registrations.TryGetValue(slotKey, out var list)
        && list.Any(o => o.Key == key);

    /// <summary>
    /// 选赢家：priority 小的优先；同 priority 时先注册者优先。
    /// </summary>
    /// <remarks>
    /// <b><c>ThenBy(RegistrationIndex)</c> 必须显式写</b>。不写时 LINQ 的 <c>OrderBy</c>
    /// 虽然稳定，但换实现或换人维护就可能丢。dsh 原文专门强调"相同值按注册顺序"，
    /// 说明这是被踩过的。
    /// <para>
    /// <b>同 priority 时先注册者胜</b>带来的好处：内置实现（先注册）在同值时仍占位，
    /// 第三方必须显式给更小数值才能覆盖，不会"碰巧抢先"。
    /// </para>
    /// </remarks>
    private static SlotOccupant? PickWinner(IEnumerable<SlotOccupant> occupants) =>
        occupants
            .OrderBy(o => o.Priority)
            .ThenBy(o => o.RegistrationIndex)
            .FirstOrDefault();

    private void Unregister(string slotKey, SlotOccupant occupant)
    {
        if (_registrations.TryGetValue(slotKey, out var list))
        {
            _registrations[slotKey] = list = list.Where(o => o.RegistrationIndex != occupant.RegistrationIndex).ToList();
            if (list.Count == 0) _registrations.Remove(slotKey);
        }

        // list kind 同步从可观察集合移除，ItemsControl 会自动回收视图
        if (_lists.TryGetValue(slotKey, out var observable))
        {
            var existing = observable.FirstOrDefault(o => o.RegistrationIndex == occupant.RegistrationIndex);
            if (existing is not null) observable.Remove(existing);
        }
    }

    private sealed class Registration(SlotRegistry registry, string slotKey, SlotOccupant occupant) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            registry.Unregister(slotKey, occupant);
        }
    }
}
