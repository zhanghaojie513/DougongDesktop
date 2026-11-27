using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DougongDesktop.Ui.Core;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 工作区选择器工厂（对照 dsh 0.2.2-rc.3 <c>WorkspacePickerFactory</c>）。
/// </summary>
/// <remarks>
/// <b>两个表面（空白会话 Hero / 新会话与侧栏）共用同一份 <see cref="WorkspacePickerOwner"/></b>，
/// 避免两处各自存状态产生分叉。菜单只挂在已附着视觉树的锚点上打开（无锚点时不显示）；
/// 当列表已就绪且没有可用工作区时，点击直接进入 <c>workspace.picker.directoryFlow</c> 目录选择流。
/// </remarks>
public static class WorkspacePickerFactory
{
    public static WorkspacePickerControl Create(
        WorkspacePickerOwner owner,
        WorkspacePickerSurface surface) => new(owner, surface);
}

/// <summary>单个工作区选择器触发控件（由 <see cref="WorkspacePickerFactory"/> 创建）。</summary>
public sealed class WorkspacePickerControl : UserControl
{
    private readonly WorkspacePickerOwner _owner;
    private readonly WorkspacePickerSurface _surface;
    private readonly ArcButton _button;
    private readonly TextBlock _label;
    private readonly MenuFlyout _menu = new();

    public WorkspacePickerControl(WorkspacePickerOwner owner, WorkspacePickerSurface surface)
    {
        _owner = owner;
        _surface = surface;

        _label = new TextBlock
        {
            Text = owner.ActiveDisplayName,
            FontSize = surface == WorkspacePickerSurface.EmptyHero ? 12 : 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = surface == WorkspacePickerSurface.EmptyHero ? 220 : 150,
        };

        _button = new ArcButton
        {
            Variant = surface == WorkspacePickerSurface.EmptyHero
                ? ButtonVariant.Secondary
                : ButtonVariant.Ghost,
            Size = ControlSize.Small,
            CornerRadius = new CornerRadius(surface == WorkspacePickerSurface.EmptyHero ? 999 : 6),
            Padding = surface == WorkspacePickerSurface.EmptyHero
                ? new Thickness(12, 4)
                : new Thickness(6, 3),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new LucideIcon
                    {
                        Kind = LucideIconKind.FolderOpen,
                        Size = 13,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    _label,
                    new LucideIcon
                    {
                        Kind = LucideIconKind.ChevronDown,
                        Size = 12,
                        Opacity = 0.65,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };

        _button.Click += (_, _) => Activate();

        Content = _button;

        _owner.StateChanged += RefreshFromOwner;
        DetachedFromVisualTree += (_, _) => _owner.StateChanged -= RefreshFromOwner;
        AttachedToVisualTree += (_, _) =>
        {
            _owner.StateChanged -= RefreshFromOwner;
            _owner.StateChanged += RefreshFromOwner;
            RefreshFromOwner();
        };

        RefreshFromOwner();
    }

    /// <summary>所属界面表面（EmptyHero / NewSession）。</summary>
    public WorkspacePickerSurface Surface => _surface;

    /// <summary>触发按钮（无头自检用）。</summary>
    public ArcButton TriggerButton => _button;

    /// <summary>当前显示的标签文本。</summary>
    public string LabelText => _label.Text ?? string.Empty;

    /// <summary>当前锚点是否有效（已附着视觉树且处于可视化宿主内）。</summary>
    public bool HasAnchor => TopLevel.GetTopLevel(_button) is not null;

    /// <summary>执行一次触发裁决（点击按钮或测试驱动）。</summary>
    public WorkspacePickerOpenAction Activate(bool? forceAnchor = null)
    {
        var hasAnchor = forceAnchor ?? HasAnchor;
        var action = _owner.TriggerPicker(_surface, hasAnchor);
        if (action == WorkspacePickerOpenAction.OpenMenu && HasAnchor)
        {
            RebuildMenu();
            _menu.ShowAt(_button);
        }

        return action;
    }

    private void RefreshFromOwner()
    {
        _label.Text = _owner.ActiveDisplayName;
        ToolTip.SetTip(_button, string.IsNullOrWhiteSpace(_owner.ActiveWorkspace)
            ? "选择工作区目录"
            : _owner.ActiveWorkspace);
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        foreach (var entry in _owner.Entries)
        {
            var suffix = entry.SessionCount > 0 ? $"（{entry.SessionCount}）" : string.Empty;
            var prefix = entry.IsCurrent ? "✓ " : "   ";
            var item = new MenuItem
            {
                Header = $"{prefix}{entry.DisplayName}{suffix}",
            };
            ToolTip.SetTip(item, entry.Path);

            var targetPath = entry.Path;
            item.Click += (_, _) => _owner.SelectWorkspace(targetPath);
            _menu.Items.Add(item);
        }

        if (_owner.Entries.Count > 0)
            _menu.Items.Add(new Separator());

        var browse = new MenuItem { Header = "打开其他目录…" };
        browse.Click += (_, _) => _owner.RequestDirectoryFlow(_surface);
        _menu.Items.Add(browse);
    }
}
