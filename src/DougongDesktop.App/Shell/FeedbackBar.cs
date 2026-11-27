using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DougongDesktop.Ui.Core;
using TKCode.Arc;
using IpcFeedbackRating = DougongDesktop.Ipc.FeedbackRating;
using ArcFeedbackRating = TKCode.Arc.FeedbackRating;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 助手消息尾部的动作与反馈行：复制 + 赞/踩（含原因 chips 与可选备注）+ 时间戳。
/// </summary>
/// <remarks>
/// <b>采用 Arc 的 <see cref="FeedbackActions"/>（Special 包，public）</b>：
/// 此前自建是因为 <c>AiAnswerActions</c> 为 <c>internal</c>；重扫组件库后确认
/// <see cref="FeedbackActions"/> 为 public，专为宿主自定义消息行设计：
/// <list type="bullet">
/// <item><c>Leading</c> 插槽承接 <see cref="CopyButton"/>（1000ms 复原）</item>
/// <item><c>Trailing</c> 插槽承接时间戳</item>
/// <item>踩后内联展开原因 chips + 可选备注，提交后显示致谢并自动折叠，不遮挡正文</item>
/// <item>通过 <c>FeedbackHandler</c> 接入宿主 IPC，失败时自带内联错误与重试</item>
/// </list>
/// </remarks>
public sealed class FeedbackBar : UserControl
{
    private readonly string _itemId;
    private readonly FeedbackActions _actions;

    /// <summary>用户提交了评分或反馈细节。</summary>
    public event Action<string, IpcFeedbackRating, string?>? Submitted;

    /// <summary>踩的理由。用短词，方便后续统计，也保证窄屏最多折两行。</summary>
    private static readonly string[] DownReasons = ["不准确", "没帮上忙", "太啰嗦", "跑题了"];

    private static readonly ResponseFeedbackLabels ChineseLabels = new()
    {
        GoodResponse = "有帮助",
        BadResponse = "没帮助",
        NegativeTitle = "这条回复有什么问题？",
        PositiveTitle = "哪些方面有帮助？",
        Other = "其他",
        OtherPlaceholder = "告诉我们具体哪里不对…",
        Submit = "提交",
        Send = "发送",
        Skip = "跳过",
        Cancel = "取消",
        Close = "关闭",
        TryAgain = "重试",
        Note = "补充说明",
        Thanks = "感谢反馈",
        ThanksMessage = "已记录你的反馈。",
        SendFailed = "反馈提交失败",
        RatingFailed = "评分保存失败",
        Sending = "正在提交…",
        RatedUp = "已标记为有帮助",
        RatedDown = "已标记为没帮助",
        RatingCleared = "已取消评分",
        CopyReply = "复制回复",
    };

    public FeedbackBar(string itemId, string? copyText = null, DateTimeOffset? timestamp = null)
    {
        _itemId = itemId;

        _actions = new FeedbackActions
        {
            MessageId = itemId,
            NegativeReasons = DownReasons,
            PositiveReasons = [],
            AllowComment = true,
            CommentPlaceholder = "补充细节（可选）",
            PrivacyNote = null,
            ThanksMessage = "已记录你的反馈。",
            Labels = ChineseLabels,
            Margin = new Thickness(0, 4, 0, 0),
        };

        if (!string.IsNullOrEmpty(copyText))
        {
            _actions.Leading = new CopyButton
            {
                Value = copyText,
                IsIconOnly = true,
                Variant = CopyButtonVariant.Plain,
                Label = "复制回复",
                CopiedLabel = "复制成功",
                FailedLabel = "复制失败",
                ResetAfter = TimeSpan.FromMilliseconds(ConversationMetrics.CopyFeedbackMs),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        if (timestamp is { } at)
        {
            _actions.Trailing = new TextBlock
            {
                Text = at.ToLocalTime().ToString("HH:mm"),
                FontSize = 11,
                Opacity = 0.4,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        _actions.FeedbackHandler = (submission, _) =>
        {
            var rating = MapRating(submission.Rating);
            var reason = FormatReason(submission.Reasons, submission.Comment);
            Submitted?.Invoke(_itemId, rating, reason);
            return Task.CompletedTask;
        };

        Content = _actions;
    }

    /// <summary>当前评分，供诊断。</summary>
    public IpcFeedbackRating Rating => MapRating(_actions.Rating);

    /// <summary>底层 Arc 控件，供诊断与无头检查。</summary>
    public FeedbackActions Inner => _actions;

    private static IpcFeedbackRating MapRating(ArcFeedbackRating rating) => rating switch
    {
        ArcFeedbackRating.Up => IpcFeedbackRating.Up,
        ArcFeedbackRating.Down => IpcFeedbackRating.Down,
        _ => IpcFeedbackRating.None,
    };

    private static string? FormatReason(IReadOnlyList<string>? reasons, string? comment)
    {
        var parts = new List<string>();
        if (reasons is { Count: > 0 })
            parts.Add(string.Join("、", reasons.Where(r => !string.IsNullOrWhiteSpace(r))));
        if (!string.IsNullOrWhiteSpace(comment))
            parts.Add(comment.Trim());
        return parts.Count > 0 ? string.Join("：", parts) : null;
    }
}
