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
        Action<string> onSelect) =>
        Build(sessions, connectionDetail, selectedKey, onSelect, workspaceOwner: null);

    /// <summary>
    /// 构建侧边栏（支持 P8d D1 工作区选择器与分组/扁平会话行切换）。
    /// </summary>
    public static Sidebar Build(
        IReadOnlyList<SessionSummary>? sessions,
        string? connectionDetail,
        string? selectedKey,
        Action<string> onSelect,
        WorkspacePickerOwner? workspaceOwner)
    {
        var sidebar = new Sidebar
        {
            IsFilterVisible = true,
            FilterPlaceholder = "筛选会话",
            NoMatchesText = "没有匹配的会话",
            // 折叠入口只在标题栏/topStrip 那一个：dsh 的侧栏里没有自带折叠按钮，
            // 两处都有会让用户以为是两个功能。
            IsCollapseButtonVisible = false,
            SelectedKey = selectedKey,
            // Arc 1.3.2 开放了 ExpandedWidth（不直接设 Width，避免锁死折叠动画）。
            ExpandedWidth = Columns.SidebarDefault,

            // 侧栏填充色与标题栏同源（dsh 的 --dsw-specific-sidebar-fill）
            Background = DshTheme.Brush(DshTheme.SidebarFill),
        };

        // 品牌行：dsh 是 52px 高，带负 margin 抵消根 padding 让分隔线通栏。
        // 这里用固定高度 + 水平内边距对齐（Avalonia 没有"负 margin 抵消父 padding"
        // 这种用法，直接用 52 高的 Border 更直白）。
        var brandRow = new Border
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
                    // 品牌符号：真实资源（斗拱形体），不是图标占位
                    new Image
                    {
                        Source = BrandAssets.Symbol(DshTheme.IsDark),
                        Width = 20,
                        Height = 20,
                        Stretch = Avalonia.Media.Stretch.Uniform,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = "DOUGONG",
                        FontSize = 13,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        LetterSpacing = 0.5,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                    // dsh 的品牌名是"deepseek + HARNESS"双色；我们的对应是
                    // "DOUGONG + NET"，后段用小号字弱化
                    new Border
                    {
                        Background = DshTheme.Brush(DshTheme.LabelPrimary),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = "NET",
                            FontSize = 9,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold,
                            Foreground = DshTheme.Brush(DshTheme.SidebarFill),
                        },
                    },
                },
            },
        };

        // Header = 品牌行 + 新会话卡片（+ 工作区选择器与分组/扁平切换条）
        var headerStack = new StackPanel
        {
            Children = { brandRow, BuildNewSessionCard(onSelect) },
        };
        if (workspaceOwner is not null)
            headerStack.Children.Add(BuildWorkspaceToolbar(workspaceOwner));

        sidebar.Header = headerStack;

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
        // 「更多」是动作项而非页面，设 IsAction=true：触发 ItemInvoked 但不抢走当前选中态。
        nav.Items.Add(MakeItem(MoreKey, "更多",
            TKCode.Arc.Icons.Lucide.LucideIconKind.LayoutGrid, isAction: true));

        sidebar.Sections.Add(nav);

        AppendSessionSections(sidebar, sessions, connectionDetail, workspaceOwner);

        // 用 Arc 1.3.2 的 ItemInvoked 事件：点击已选中项也会触发，
        // 不再需要手写 RelayCommand 包装 SelectCommand。
        sidebar.ItemInvoked += (_, e) =>
        {
            if (e.Item?.Key is { Length: > 0 } key) onSelect(key);
        };

        return sidebar;
    }

    private static Control BuildWorkspaceToolbar(WorkspacePickerOwner owner)
    {
        var picker = WorkspacePickerFactory.Create(owner, WorkspacePickerSurface.NewSession);

        var modeBtn = new TKCode.Arc.Button
        {
            Variant = ButtonVariant.Ghost,
            Size = ControlSize.Small,
            Content = owner.ListMode == SessionListMode.Grouped ? "分组" : "扁平",
            Padding = new Thickness(6, 2),
        };
        ToolTip.SetTip(modeBtn, owner.ListMode == SessionListMode.Grouped
            ? "当前按工作区分组，点击切换为扁平列表"
            : "当前扁平展示全部会话，点击切换为按工作区分组");

        modeBtn.Click += (_, _) =>
        {
            var next = owner.ListMode == SessionListMode.Grouped
                ? SessionListMode.Flat
                : SessionListMode.Grouped;
            owner.SetListMode(next);
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 4,
            Margin = new Thickness(Columns.SidebarPaintInset + 1, 0,
                Columns.SidebarPaintInset + 1, 6),
        };
        Grid.SetColumn(picker, 0);
        Grid.SetColumn(modeBtn, 1);
        grid.Children.Add(picker);
        grid.Children.Add(modeBtn);
        return grid;
    }

    private static void AppendSessionSections(
        Sidebar sidebar,
        IReadOnlyList<SessionSummary>? sessions,
        string? connectionDetail,
        WorkspacePickerOwner? workspaceOwner)
    {
        if (sessions is null)
        {
            var offline = new SidebarSection { Header = "工作区", IsCollapsible = true, IsExpanded = true };
            offline.Items.Add(new SidebarItem
            {
                Key = "__offline",
                Label = connectionDetail ?? "引擎未连接",
                IsEnabled = false,
            });
            sidebar.Sections.Add(offline);
            return;
        }

        if (sessions.Count == 0)
        {
            var empty = new SidebarSection { Header = "工作区", IsCollapsible = true, IsExpanded = true };
            empty.Items.Add(new SidebarItem
            {
                Key = "__empty",
                Label = "还没有会话",
                IsEnabled = false,
            });
            sidebar.Sections.Add(empty);
            return;
        }

        var mode = workspaceOwner?.ListMode ?? SessionListMode.Grouped;
        if (mode == SessionListMode.Flat)
        {
            var flat = new SidebarSection { Header = "全部会话", IsCollapsible = true, IsExpanded = true };
            var multiWorkspace = sessions
                .Select(s => WorkspaceCatalog.NormalizePath(s.Workspace))
                .Where(p => p.Length > 0)
                .Distinct(WorkspaceCatalog.PathComparer)
                .Skip(1)
                .Any();

            foreach (var s in WorkspaceCatalog.FlattenSessions(sessions))
            {
                var title = string.IsNullOrWhiteSpace(s.Title) ? s.Id : s.Title;
                var wsTag = multiWorkspace && !string.IsNullOrWhiteSpace(s.Workspace)
                    ? $"[{WorkspaceCatalog.DisplayNameOf(s.Workspace)}] "
                    : string.Empty;

                flat.Items.Add(new SidebarItem
                {
                    Key = s.Id,
                    Label = $"{wsTag}{title}",
                    Badge = FormatRelative(s.UpdatedAt),
                    IsUnread = s.HasPendingApproval,
                });
            }

            sidebar.Sections.Add(flat);
            return;
        }

        // 分组模式：每个工作区一个可折叠 SidebarSection
        var groups = WorkspaceCatalog.GroupSessions(sessions, workspaceOwner?.ActiveWorkspace);
        foreach (var group in groups)
        {
            var section = new SidebarSection
            {
                Header = group.DisplayName,
                IsCollapsible = true,
                IsExpanded = true,
            };

            foreach (var s in group.Sessions)
            {
                section.Items.Add(new SidebarItem
                {
                    Key = s.Id,
                    Label = string.IsNullOrWhiteSpace(s.Title) ? s.Id : s.Title,
                    Badge = FormatRelative(s.UpdatedAt),
                    IsUnread = s.HasPendingApproval,
                });
            }

            sidebar.Sections.Add(section);
        }
    }

    /// <summary>
    /// 新会话卡片。
    /// </summary>
    /// <remarks>
    /// <b>为什么是卡片不是列表行</b>：它是侧栏唯一的主操作。dsh 用
    /// 38px 高 + 描边 + 12px 圆角 + 抬升填充把它做成按钮形态，
    /// 与下面的导航项、会话行明显区分。
    /// <para>
    /// <b>直接用 Arc 的 <see cref="TKCode.Arc.Button"/></b>：键盘可达性、悬停态、
    /// 按压回弹与焦点环由组件自带，不再手写 PointerEntered/PointerExited 切底色。
    /// </para>
    /// </remarks>
    private static Control BuildNewSessionCard(Action<string> onSelect)
    {
        var content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new TKCode.Arc.Icons.Lucide.LucideIcon
                {
                    Kind = TKCode.Arc.Icons.Lucide.LucideIconKind.CirclePlus,
                    Size = 15,
                },
                new TextBlock
                {
                    Text = "新会话",
                    FontSize = 14,
                    FontWeight = Avalonia.Media.FontWeight.Medium,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            },
        };

        var button = new TKCode.Arc.Button
        {
            Content = content,
            Variant = ButtonVariant.Secondary,
            Height = 38,
            Padding = new Thickness(12, 8),
            Margin = new Thickness(Columns.SidebarPaintInset + 1, 0,
                Columns.SidebarPaintInset + 1, 10),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(Columns.RadiusMd),
        };

        button.Click += (_, _) => onSelect(NewSessionKey);
        return button;
    }

    /// <summary>造一个侧边栏项。</summary>
    private static SidebarItem MakeItem(
        string key,
        string label,
        TKCode.Arc.Icons.Lucide.LucideIconKind? icon = null,
        bool isAction = false)
    {
        var item = new SidebarItem { Key = key, Label = label, IsAction = isAction };

        // Icon 是 object 类型，可以直接放控件。LucideIcon 是 ArcGlyph 派生，
        // 会跟随前景色与字号——比自己拼 Path 省事且缩放正确。
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
