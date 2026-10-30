using Avalonia;
using Avalonia.Controls;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;
using TKCode.Arc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 侧边栏：工作区导航 + 会话列表 + 设置入口。
/// </summary>
/// <remarks>
/// 用 <see cref="Sidebar"/>（Blocks 包）而不是自己拼列表，理由：
/// 折叠成图标栏、筛选框、选中标记滑动、键盘导航都是现成的，
/// 自建这些要几百行且很难做到同等质感。
/// <para>
/// <b>结构照 dsh</b>：工作区作为 section 头，会话作为其下的 item，
/// 设置固定在 <c>Footer</c>。这样会话多了以后仍能靠筛选框找到。
/// </para>
/// </remarks>
public static class SidebarView
{
    /// <summary>设置项固定用这个 key，便于 SelectKey 分派。</summary>
    public const string SettingsKey = "__settings";

    /// <summary>插件入口（dsh 的"插件"导航项）。</summary>
    public const string PluginsKey = "__plugins";

    /// <summary>自动化任务入口。</summary>
    public const string TasksKey = "__tasks";

    /// <summary>"更多"入口。</summary>
    public const string MoreKey = "__more";

    /// <summary>新建会话动作项。</summary>
    public const string NewSessionKey = "__new";

    /// <summary>
    /// 构建侧边栏。
    /// </summary>
    /// <param name="sessions">真实会话列表；null 表示引擎未连接。</param>
    /// <param name="connectionDetail">未连接时的原因，直接显示给用户。</param>
    /// <param name="selectedKey">当前选中的会话 id。</param>
    /// <param name="onSelect">选中回调（会话 id / __settings / __new）。</param>
    public static Sidebar Build(
        IReadOnlyList<SessionSummary>? sessions,
        string? connectionDetail,
        string? selectedKey,
        Action<string> onSelect)
    {
        var sidebar = new Sidebar
        {
            IsFilterVisible = true,
            FilterPlaceholder = "筛选会话",
            IsCollapseButtonVisible = true,
            SelectedKey = selectedKey,

            // 宽度不在这里设：OpenWidth 是主题层属性（Arc 注释写 "from the theme"），
            // 不对外开放赋值。展开宽度由外层 Grid 的列宽决定（280，见 Columns）。

            // 侧栏填充色与标题栏同源（dsh 的 --dsw-specific-sidebar-fill）
            Background = DshTheme.Brush(DshTheme.SidebarFill),
        };

        // 品牌行：dsh 是 52px 高，带负 margin 抵消根 padding 让分隔线通栏。
        // 这里用固定高度 + 水平内边距对齐（Avalonia 没有"负 margin 抵消父 padding"
        // 这种用法，直接用 52 高的 Border 更直白）。
        sidebar.Header = new Border
        {
            Height = Columns.SidebarBrandRowHeight,
            Padding = new Thickness(Columns.SidebarInlinePadding + 4, 0),
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children =
                {
                    // 品牌标记：dsh 用鱼形；我们用 Lucide 的拱形（Dougong = 斗拱）
                    new TKCode.Arc.Icons.Lucide.LucideIcon
                    {
                        Kind = TKCode.Arc.Icons.Lucide.LucideIconKind.Layers,
                        Size = 18,
                        Foreground = DshTheme.Brush(DshTheme.LabelPrimary),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = "Dougong",
                        FontSize = 14,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                    // dsh 的品牌名是"deepseek + HARNESS"双色；我们的对应是
                    // "Dougong + DESKTOP"，后段用小号字弱化
                    new Border
                    {
                        Background = DshTheme.Brush(DshTheme.LabelPrimary),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = "DESKTOP",
                            FontSize = 9,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold,
                            Foreground = DshTheme.Brush(DshTheme.SidebarFill),
                        },
                    },
                },
            },
        };

        // 底部：设置入口（dsh 也是把 Settings 固定在底部）
        // Footer 是 object 类型，可以直接放 SidebarItem
        sidebar.Footer = MakeItem(SettingsKey, "设置");

        // 导航项：dsh 有「插件 / 自动化任务 / 更多」三项，在会话列表之前。
        // 它们不是会话，是全局入口，所以单列一个分区。
        var nav = new SidebarSection
        {
            Header = null,          // 无标题：dsh 的这三项直接跟在品牌行下方
            IsCollapsible = false,
        };

        nav.Items.Add(MakeItem(PluginsKey, "插件",
            TKCode.Arc.Icons.Lucide.LucideIconKind.Blocks));
        nav.Items.Add(MakeItem(TasksKey, "自动化任务",
            TKCode.Arc.Icons.Lucide.LucideIconKind.Clock));
        nav.Items.Add(MakeItem(MoreKey, "更多",
            TKCode.Arc.Icons.Lucide.LucideIconKind.LayoutGrid));

        sidebar.Sections.Add(nav);

        var section = new SidebarSection
        {
            Header = "工作区",
            IsCollapsible = true,
            IsExpanded = true,
        };

        // 新建会话永远是第一项
        section.Items.Add(MakeItem(NewSessionKey, "新建会话",
            TKCode.Arc.Icons.Lucide.LucideIconKind.CirclePlus));

        if (sessions is null)
        {
            // 引擎未连接：如实说明原因，不放一个空列表让用户猜
            section.Items.Add(new SidebarItem
            {
                Key = "__offline",
                Label = connectionDetail ?? "引擎未连接",
                IsEnabled = false,
            });
        }
        else if (sessions.Count == 0)
        {
            section.Items.Add(new SidebarItem
            {
                Key = "__empty",
                Label = "还没有会话",
                IsEnabled = false,
            });
        }
        else
        {
            foreach (var s in sessions)
            {
                section.Items.Add(new SidebarItem
                {
                    Key = s.Id,
                    Label = string.IsNullOrWhiteSpace(s.Title) ? s.Id : s.Title,
                    // dsh 的行尾显示"多久前"，不是条目数——条目数对用户没用，
                    // 时间才帮他找回"我刚才在聊哪个"
                    Badge = FormatRelative(s.UpdatedAt),
                    IsUnread = s.HasPendingApproval,
                });
            }
        }

        sidebar.Sections.Add(section);

        // 用 SelectCommand 而不是监听 SelectedKey：dsh 的文档明确推荐这个，
        // 因为重复选中同一项时 SelectedKey 不变化，命令仍会触发。
        sidebar.SelectCommand = new RelayCommand(p =>
        {
            if (p is string key) onSelect(key);
        });

        return sidebar;
    }

    /// <summary>
    /// 造一个侧边栏项。
    /// </summary>
    /// <remarks>
    /// <b>暂时不配图标</b>：<c>Icon</c> 是 object 类型（可放 Geometry 或 Control），
    /// 但 Arc 的内置字形在 <c>ArcIcons</c> 里，需要逐个确认键名。
    /// 先用纯文字——图标缺失不影响导航功能，而猜错的字形会显示成方块，
    /// 那比没有更糟。
    /// </remarks>
    private static SidebarItem MakeItem(
        string key, string label, TKCode.Arc.Icons.Lucide.LucideIconKind? icon = null)
    {
        var item = new SidebarItem { Key = key, Label = label };

        // Icon 是 object 类型，可以直接放控件。LucideIcon 是 ArcGlyph 派生，
        // 会跟随前景色与字号——比自己拼 Path 省事且缩放假正确。
        if (icon is { } kind)
        {
            item.Icon = new TKCode.Arc.Icons.Lucide.LucideIcon
            {
                Kind = kind,
                Size = 16,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        }

        return item;
    }

    /// <summary>
    /// 相对时间：dsh 的会话行尾显示"1小时""2小时""9天"这种。
    /// </summary>
    /// <remarks>
    /// <b>为什么不用绝对时间</b>：绝对时间要用户自己算"那是多久前"。
    /// 找回"刚才那个会话"时，相对时间更快。
    /// </remarks>
    private static string? FormatRelative(DateTimeOffset at)
    {
        var span = DateTimeOffset.Now - at;

        if (span < TimeSpan.Zero) return "刚刚";
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}分钟";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}小时";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}天";

        return at.ToLocalTime().ToString("MM-dd");
    }
}

/// <summary>极简命令实现，避免为侧边栏引入 MVVM 框架。</summary>
internal sealed class RelayCommand(Action<object?> execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
