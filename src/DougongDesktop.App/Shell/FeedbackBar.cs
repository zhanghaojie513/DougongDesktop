using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;
using TKCode.Arc.Icons.Lucide;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 消息反馈条：赞 / 踩两个按钮。
/// </summary>
/// <remarks>
/// <b>为什么自建而不是用 Arc 的 <c>AiAnswerActions</c></b>：那个类实测是
/// <c>internal</c>（反射确认 <c>IsPublic=False</c>），外部引用不到。
/// 它所在的 <c>AiAnswer</c> 是**整页控件**（含 reasoning、sources、追问），
/// 而我们要的只是消息流里一条消息下面的一行按钮——用量级不匹配。
/// <para>
/// <b>所以只做两件事，不做成"AI 答案页"</b>：
/// <list type="number">
/// <item>两个按钮（赞 / 踩），踩之后才问理由</item>
/// <item>点过之后显示"已记录"，不再让重复点</item>
/// </list>
/// 不做版本导航、不做重新生成、不做分享——那些要么没有数据支撑，
/// 要么属于 P8d 的会话增强。
/// </para>
/// <para>
/// <b>踩的理由在本地弹一个小面板</b>，不用 Dialog：Dialog 会遮住正文，
/// 而用户在评价时往往正看着那段正文。
/// </para>
/// </remarks>
public sealed class FeedbackBar : UserControl
{
    private readonly string _itemId;
    private readonly StackPanel _buttons;
    private readonly TextBlock _status;
    private readonly StackPanel _reasons;

    private FeedbackRating _rating = FeedbackRating.None;

    /// <summary>用户提交了评分。</summary>
    public event Action<string, FeedbackRating, string?>? Submitted;

    /// <summary>踩的理由。用短词，方便后续统计。</summary>
    private static readonly string[] DownReasons = ["不准确", "没帮上忙", "太啰嗦", "跑题了"];

    public FeedbackBar(string itemId)
    {
        _itemId = itemId;

        _status = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            IsVisible = false,
        };

        _reasons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false,
        };

        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _buttons.Children.Add(MakeButton(LucideIconKind.ThumbsUp, "有帮助", () => Rate(FeedbackRating.Up)));
        _buttons.Children.Add(MakeButton(LucideIconKind.ThumbsDown, "没帮助", OnDownClicked));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _buttons, _status },
        };

        Content = new StackPanel { Children = { row, _reasons } };
    }

    /// <summary>当前评分，供诊断。</summary>
    public FeedbackRating Rating => _rating;

    private void OnDownClicked()
    {
        // 已经点过就不再弹理由
        if (_rating != FeedbackRating.None) return;

        if (_reasons.IsVisible)
        {
            _reasons.IsVisible = false;
            return;
        }

        BuildReasonButtons();
        _reasons.IsVisible = true;
    }

    private void BuildReasonButtons()
    {
        if (_reasons.Children.Count > 0) return;

        foreach (var reason in DownReasons)
        {
            var tag = reason;
            var button = new Avalonia.Controls.Button
            {
                Content = new TextBlock { Text = tag, FontSize = 11 },
                Padding = new Thickness(8, 3),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(DshTheme.BorderL3),
                CornerRadius = new CornerRadius(Columns.RadiusSm),
                Tag = tag,
            };

            button.Click += (_, _) => Rate(FeedbackRating.Down, tag);
            _reasons.Children.Add(button);
        }
    }

    private void Rate(FeedbackRating rating, string? reason = null)
    {
        // 重复点击不产生第二次提交——与审批的"重复答复只授权一次"同一条原则
        if (_rating != FeedbackRating.None) return;

        _rating = rating;
        _reasons.IsVisible = false;
        _buttons.IsVisible = false;

        _status.Text = rating switch
        {
            FeedbackRating.Up => "已记录：有帮助",
            FeedbackRating.Down => reason is { Length: > 0 } ? $"已记录：{reason}" : "已记录：没帮助",
            _ => "已记录",
        };
        _status.IsVisible = true;

        Submitted?.Invoke(_itemId, rating, reason);
    }

    private Avalonia.Controls.Button MakeButton(LucideIconKind kind, string tip, Action onClick)
    {
        var button = new Avalonia.Controls.Button
        {
            Content = new LucideIcon { Kind = kind, Size = 14 },
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Columns.RadiusSm),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Opacity = 0.55,
        };

        ToolTip.SetTip(button, tip);

        button.PointerEntered += (_, _) => button.Opacity = 0.9;
        button.PointerExited += (_, _) => button.Opacity = 0.55;

        button.Click += (_, _) => onClick();

        return button;
    }
}
