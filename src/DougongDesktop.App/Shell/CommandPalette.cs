using Avalonia;
using Avalonia.Input;
using DougongDesktop.App.Services;
using DougongDesktop.Ui.Core;
using TKCode.Arc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 命令面板（Cmd/Ctrl+K）。
/// </summary>
/// <remarks>
/// <b>用 Arc 的 <see cref="CommandPaletteDialog"/> 而不是自建</b>：它已经处理了
/// Cmd 与 Ctrl 的平台差异、Esc 逐级清除（先清查询再关闭）、上下键共享高亮、
/// 结果分组与空态文案。自建这些细节至少要两百行，而且做不到一致的手感。
/// <para>
/// <b>它注册进 overlay slot，不是挂在窗口上</b>：这样第三方 UI 插件可以注册
/// 自己的命令面板并给出更小的 priority 顶掉内置的，与侧边栏同理。
/// 命令清单本身也在注册时一次性给定——面板不知道命令从哪来。
/// </para>
/// </remarks>
public static class CommandPaletteModule
{
    /// <summary>命令的 Id 前缀，便于从选中结果里分辨来源。</summary>
    public const string NewSession = "conv.new";
    public const string OpenSettings = "app.settings";
    public const string OpenPlugins = "app.plugins";
    public const string ToggleSidebar = "view.toggle-sidebar";

    /// <summary>
    /// 构建面板。
    /// </summary>
    /// <param name="sessions">会话列表；为 null 表示引擎没连上，只放全局命令。</param>
    /// <param name="onRun">命令执行回调，参数是命令 Id。</param>
    public static CommandPaletteDialog Build(
        IReadOnlyList<Dougong.HostProtocol.SessionSummary>? sessions,
        Action<string> onRun)
    {
        var dialog = new CommandPaletteDialog
        {
            PlaceholderText = "搜索命令或会话",
            EmptyTitle = "没有匹配的命令",
            EmptyDescription = "试试「新建」「设置」或会话标题的前几个字",
            IsShortcutEnabled = true,      // Cmd+K / Ctrl+K 在任何位置都聚焦
            CloseOnSelect = true,
            CloseOnEscape = true,
            CloseOnOutsidePress = true,
            PanelTopRatio = 0.18,          // 偏上：留出下方结果列表的空间
        };

        var items = new List<CommandPaletteItem>
        {
            // 全局命令排在最前：它们是"任何上下文下都该能找到"的
            new()
            {
                Id = NewSession,
                Label = "新建会话",
                Group = "会话",
                Keywords = ["new", "chat", "新", "建"],
                Shortcut = "N",
            },
            new()
            {
                Id = OpenSettings,
                Label = "打开设置",
                Group = "导航",
                Keywords = ["settings", "配置", "provider", "提供商"],
                Shortcut = ",",
            },
            new()
            {
                Id = OpenPlugins,
                Label = "打开插件",
                Group = "导航",
                Keywords = ["plugins", "扩展", "extension"],
            },
            new()
            {
                Id = ToggleSidebar,
                Label = "折叠/展开侧边栏",
                Group = "视图",
                Keywords = ["sidebar", "侧栏", "收起"],
                Shortcut = "B",
            },
        };

        // 会话条目：切换会话是命令面板最常用的用途，
        // 不放进来用户就得回到侧边栏逐个找。
        if (sessions is { Count: > 0 })
        {
            foreach (var s in sessions)
            {
                items.Add(new CommandPaletteItem
                {
                    Id = "session:" + s.Id,
                    Label = s.Title is { Length: > 0 } ? s.Title : s.Id,
                    Description = s.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                    Group = "会话",
                    Keywords = [s.Id],
                });
            }
        }

        dialog.ItemsSource = items;

        dialog.ItemSelected += (_, e) =>
        {
            if (e.Item?.Id is { Length: > 0 } id) onRun(id);
        };

        return dialog;
    }

    /// <summary>
    /// 把面板包成 overlay 占位者。
    /// </summary>
    /// <remarks>
    /// <b>overlay 的 ItemsControl 设了 <c>IsHitTestVisible=false</c></b>（见
    /// <c>AppShell</c> 注释：普通浮层不该吃掉点击）。命令面板是交互控件，
    /// 所以外面必须再包一层可命中的容器，否则点不进去——这类问题
    /// 在 dump 里表现为"尺寸全对但点不动"。
    /// </remarks>
    public static Avalonia.Controls.Control AsOverlay(CommandPaletteDialog dialog)
    {
        // 面板自身只在打开时显示，容器始终透明、不吃点击
        return new Avalonia.Controls.Panel
        {
            Children = { dialog },
            Background = null,
            IsHitTestVisible = true,
        };
    }
}
