using DougongDesktop.Ipc;

namespace DougongDesktop.Ui.Core;

/// <summary>侧栏会话列表的排列方式（P8d D1：分组 vs 扁平）。</summary>
public enum SessionListMode
{
    /// <summary>按工作区分组，每组一个可折叠分区。</summary>
    Grouped,

    /// <summary>全部会话按最近活动时间倒序扁平展示。</summary>
    Flat,
}

/// <summary>工作区选择器所处的界面表面（dsh rc.3：两个表面共用同一份 owner 状态）。</summary>
public enum WorkspacePickerSurface
{
    /// <summary>空白会话 Hero 下的工作区 chip。</summary>
    EmptyHero,

    /// <summary>侧栏 / 新会话视图中的工作区选择器。</summary>
    NewSession,
}

/// <summary>用户触发工作区选择器时的裁决动作（dsh rc.3 契约）。</summary>
public enum WorkspacePickerOpenAction
{
    /// <summary>菜单无锚点时保持关闭，不弹出悬浮孤立菜单。</summary>
    None,

    /// <summary>在锚点处打开工作区切换菜单。</summary>
    OpenMenu,

    /// <summary>列表已就绪但尚无可用工作区，直接打开目录选择流（<c>workspace.picker.directoryFlow</c>）。</summary>
    OpenDirectoryFlow,
}

/// <summary>一条可选的工作区摘要。</summary>
public sealed record WorkspaceEntry(
    string Path,
    string DisplayName,
    bool IsCurrent,
    int SessionCount,
    DateTimeOffset? LastActivity);

/// <summary>分组模式下的一组工作区会话。</summary>
public sealed record WorkspaceSessionGroup(
    string WorkspacePath,
    string DisplayName,
    IReadOnlyList<SessionSummary> Sessions);

/// <summary>
/// 工作区归一化、会话分组/扁平投影与 rc.3 选择器规则（纯逻辑，零 UI 依赖）。
/// </summary>
public static class WorkspaceCatalog
{
    public const int MaxRecentWorkspaces = 12;

    public static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>解析持久化的列表模式字符串，未知值回落为 <see cref="SessionListMode.Grouped"/>。</summary>
    public static SessionListMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "flat" => SessionListMode.Flat,
        _ => SessionListMode.Grouped,
    };

    /// <summary>序列化列表模式字符串。</summary>
    public static string FormatMode(SessionListMode mode) => mode switch
    {
        SessionListMode.Flat => "flat",
        _ => "grouped",
    };

    /// <summary>规范化工作区路径：去首尾空白与非根尾部路径分隔符。</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var trimmed = path.Trim();
        var root = Path.GetPathRoot(trimmed);
        if (!string.IsNullOrEmpty(root) && string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>工作区显示名：取末级目录名，根目录回退为完整路径。</summary>
    public static string DisplayNameOf(string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length == 0) return "未分组";

        var leaf = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(leaf) ? normalized : leaf;
    }

    /// <summary>
    /// 汇总已知工作区（保存的历史 + 会话里出现的工作区 + 当前活动工作区），去重并按活跃度排序。
    /// </summary>
    public static IReadOnlyList<WorkspaceEntry> BuildEntries(
        IEnumerable<string>? knownWorkspaces,
        IReadOnlyList<SessionSummary>? sessions,
        string? activeWorkspace)
    {
        var cmp = PathComparer;
        var active = NormalizePath(activeWorkspace);

        var stats = new Dictionary<string, (int Count, DateTimeOffset? LastActivity, int Order)>(cmp);
        var order = 0;

        void Register(string? rawPath)
        {
            var norm = NormalizePath(rawPath);
            if (norm.Length == 0) return;
            if (!stats.ContainsKey(norm))
                stats[norm] = (0, null, order++);
        }

        if (active.Length > 0) Register(active);

        if (knownWorkspaces is not null)
        {
            foreach (var ws in knownWorkspaces)
                Register(ws);
        }

        if (sessions is not null)
        {
            foreach (var s in sessions)
            {
                var norm = NormalizePath(s.Workspace);
                if (norm.Length == 0) continue;

                if (stats.TryGetValue(norm, out var existing))
                {
                    var latest = existing.LastActivity is { } prev && prev > s.UpdatedAt
                        ? prev
                        : s.UpdatedAt;
                    stats[norm] = (existing.Count + 1, latest, existing.Order);
                }
                else
                {
                    stats[norm] = (1, s.UpdatedAt, order++);
                }
            }
        }

        return stats
            .Select(kv => new WorkspaceEntry(
                Path: kv.Key,
                DisplayName: DisplayNameOf(kv.Key),
                IsCurrent: active.Length > 0 && cmp.Equals(kv.Key, active),
                SessionCount: kv.Value.Count,
                LastActivity: kv.Value.LastActivity))
            .OrderByDescending(e => e.IsCurrent)
            .ThenByDescending(e => e.LastActivity ?? DateTimeOffset.MinValue)
            .ThenBy(e => stats[e.Path].Order)
            .ToArray();
    }

    /// <summary>按工作区分组会话：组内按最近更新倒序，组间按活动工作区与最新会话倒序。</summary>
    public static IReadOnlyList<WorkspaceSessionGroup> GroupSessions(
        IReadOnlyList<SessionSummary> sessions,
        string? activeWorkspace = null)
    {
        if (sessions.Count == 0) return [];

        var cmp = PathComparer;
        var active = NormalizePath(activeWorkspace);
        var buckets = new Dictionary<string, List<SessionSummary>>(cmp);

        foreach (var s in sessions)
        {
            var key = NormalizePath(s.Workspace);
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = [];
            list.Add(s);
        }

        return buckets
            .Select(kv =>
            {
                var sorted = kv.Value
                    .OrderByDescending(x => x.UpdatedAt)
                    .ToArray();
                return new WorkspaceSessionGroup(
                    WorkspacePath: kv.Key,
                    DisplayName: DisplayNameOf(kv.Key),
                    Sessions: sorted);
            })
            .OrderByDescending(g => active.Length > 0 && cmp.Equals(g.WorkspacePath, active))
            .ThenByDescending(g => g.Sessions[0].UpdatedAt)
            .ToArray();
    }

    /// <summary>扁平会话列表：按最近更新倒序排列。</summary>
    public static IReadOnlyList<SessionSummary> FlattenSessions(IReadOnlyList<SessionSummary> sessions) =>
        sessions.OrderByDescending(s => s.UpdatedAt).ToArray();
}

/// <summary>
/// dsh 0.2.2-rc.3 <c>workspace.picker</c> 的触发裁决规则
/// （对照 <c>ui-workspace/src/client/contract/slots.ts:145,235</c>）。
/// </summary>
public static class WorkspacePickerPolicy
{
    /// <summary>
    /// 裁决用户点击 picker 触发器时的行为：
    /// <list type="number">
    /// <item>列表已就绪且没有任何可用工作区 → 直接进入目录选择流（不需要菜单锚点）。</item>
    /// <item>否则若没有锚点 → 不显示菜单（防止出现孤立浮层）。</item>
    /// <item>有锚点且有工作区（或列表仍在加载） → 打开锚定菜单。</item>
    /// </list>
    /// </summary>
    public static WorkspacePickerOpenAction DecideOnTrigger(
        bool listReady,
        int workspaceCount,
        bool hasAnchor)
    {
        if (listReady && workspaceCount == 0)
            return WorkspacePickerOpenAction.OpenDirectoryFlow;

        if (!hasAnchor)
            return WorkspacePickerOpenAction.None;

        return WorkspacePickerOpenAction.OpenMenu;
    }
}

/// <summary>
/// 工作区选择器的单一 owner：空白会话 Hero 与新会话/侧栏两个表面共用这份状态。
/// </summary>
/// <remarks>
/// <b>为什么抽成单一 owner</b>（dsh rc.3 的设计）：两个表面若各自维护一份
/// "当前工作区"，在一处切换后另一处会停在旧值。共用 owner 后，任意一处切换或通过
/// <c>workspace.picker.directoryFlow</c> 选入新目录，两处同步刷新。
/// </remarks>
public sealed class WorkspacePickerOwner
{
    private readonly List<string> _recent = [];
    private IReadOnlyList<SessionSummary>? _sessions;
    private IReadOnlyList<WorkspaceEntry> _entries = [];

    /// <summary>当前活动工作区路径；null 表示尚未选定。</summary>
    public string? ActiveWorkspace { get; private set; }

    /// <summary>工作区/会话列表是否已就绪（决定 0 工作区时是否直接开目录流）。</summary>
    public bool IsReady { get; private set; }

    /// <summary>当前侧栏会话排列模式（分组 / 扁平）。</summary>
    public SessionListMode ListMode { get; private set; } = SessionListMode.Grouped;

    /// <summary>当前合并排序后的可选工作区列表。</summary>
    public IReadOnlyList<WorkspaceEntry> Entries => _entries;

    /// <summary>持久化用的最近工作区列表（规范化去重）。</summary>
    public IReadOnlyList<string> RecentWorkspaces => _recent;

    /// <summary>当前活动工作区的显示名；未选定时返回"选择工作区"。</summary>
    public string ActiveDisplayName => string.IsNullOrWhiteSpace(ActiveWorkspace)
        ? "选择工作区"
        : WorkspaceCatalog.DisplayNameOf(ActiveWorkspace);

    /// <summary>任一字段变化时通知所有 picker 表面刷新文本/状态。</summary>
    public event Action? StateChanged;

    /// <summary>当前工作区切换为新路径时触发（参数为规范化路径）。</summary>
    public event Action<string>? WorkspaceChanged;

    /// <summary>侧栏分组/扁平模式切换时触发。</summary>
    public event Action<SessionListMode>? ListModeChanged;

    /// <summary>
    /// <c>workspace.picker.directoryFlow</c> 洞：请求打开系统目录选择器。
    /// 桌面端挂 Avalonia <c>StorageProvider.OpenFolderPickerAsync</c>，测试可注入假实现。
    /// </summary>
    public event Action<WorkspacePickerSurface>? DirectoryFlowRequested;

    /// <summary>用保存的偏好与默认工作区初始化 owner。</summary>
    public void Initialize(
        string? activeWorkspace,
        IEnumerable<string>? recentWorkspaces,
        SessionListMode listMode,
        bool isReady = false)
    {
        _recent.Clear();
        if (recentWorkspaces is not null)
        {
            foreach (var raw in recentWorkspaces)
                AppendRecentInternal(raw);
        }

        var normActive = WorkspaceCatalog.NormalizePath(activeWorkspace);
        ActiveWorkspace = normActive.Length > 0 ? normActive : null;
        if (ActiveWorkspace is not null)
            PromoteRecentInternal(ActiveWorkspace);

        ListMode = listMode;
        IsReady = isReady;
        RebuildEntries();
        StateChanged?.Invoke();
    }

    /// <summary>同步引擎返回的会话列表，并标记列表已就绪。</summary>
    public void SyncSessions(IReadOnlyList<SessionSummary>? sessions)
    {
        _sessions = sessions;
        IsReady = true;
        RebuildEntries();
        StateChanged?.Invoke();
    }

    /// <summary>切换当前活动工作区（同时加入最近列表并通知订阅方）。</summary>
    public bool SelectWorkspace(string path)
    {
        var norm = WorkspaceCatalog.NormalizePath(path);
        if (norm.Length == 0) return false;

        PromoteRecentInternal(norm);
        var changed = !WorkspaceCatalog.PathComparer.Equals(ActiveWorkspace, norm);
        ActiveWorkspace = norm;
        RebuildEntries();
        StateChanged?.Invoke();

        if (changed)
            WorkspaceChanged?.Invoke(norm);

        return changed;
    }

    /// <summary>新增一个工作区到最近列表，可选立即切换为当前工作区。</summary>
    public void AddWorkspace(string path, bool select = true)
    {
        var norm = WorkspaceCatalog.NormalizePath(path);
        if (norm.Length == 0) return;

        if (select)
        {
            SelectWorkspace(norm);
            return;
        }

        AppendRecentInternal(norm);
        RebuildEntries();
        StateChanged?.Invoke();
    }

    /// <summary>切换侧栏会话排列模式（分组 ↔ 扁平）。</summary>
    public void SetListMode(SessionListMode mode)
    {
        if (ListMode == mode) return;
        ListMode = mode;
        StateChanged?.Invoke();
        ListModeChanged?.Invoke(mode);
    }

    /// <summary>
    /// 按 rc.3 契约响应用户触发 picker：无工作区且已就绪时直接触发 <see cref="DirectoryFlowRequested"/>。
    /// </summary>
    public WorkspacePickerOpenAction TriggerPicker(WorkspacePickerSurface surface, bool hasAnchor)
    {
        var action = WorkspacePickerPolicy.DecideOnTrigger(IsReady, _entries.Count, hasAnchor);
        if (action == WorkspacePickerOpenAction.OpenDirectoryFlow)
            DirectoryFlowRequested?.Invoke(surface);

        return action;
    }

    /// <summary>显式打开目录选择流（例如点击菜单里的「打开其他目录…」）。</summary>
    public void RequestDirectoryFlow(WorkspacePickerSurface surface) =>
        DirectoryFlowRequested?.Invoke(surface);

    private void RebuildEntries()
    {
        _entries = WorkspaceCatalog.BuildEntries(_recent, _sessions, ActiveWorkspace);
    }

    private void AppendRecentInternal(string? raw)
    {
        var norm = WorkspaceCatalog.NormalizePath(raw);
        if (norm.Length == 0) return;
        if (_recent.Contains(norm, WorkspaceCatalog.PathComparer)) return;
        if (_recent.Count < WorkspaceCatalog.MaxRecentWorkspaces)
            _recent.Add(norm);
    }

    private void PromoteRecentInternal(string norm)
    {
        var cmp = WorkspaceCatalog.PathComparer;
        _recent.RemoveAll(x => cmp.Equals(x, norm));
        _recent.Insert(0, norm);
        if (_recent.Count > WorkspaceCatalog.MaxRecentWorkspaces)
            _recent.RemoveRange(WorkspaceCatalog.MaxRecentWorkspaces, _recent.Count - WorkspaceCatalog.MaxRecentWorkspaces);
    }
}
