using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DougongDesktop.Ui.Core;
using TKCode.Arc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 用户消息下方的动作行：时间 + 复制。
/// </summary>
/// <remarks>
/// <b>dsh 的真实内容就这么少</b>（<c>MessageIconActions.tsx:45-90</c>）：
/// 「时间(HH:mm) · 复制」。没有编辑、没有重试、没有分享、没有分支——
/// 那些在助手消息的尾部动作行里（且 dsh 也只有"在新对话中分支"）。
/// <para>
/// <b>复制按钮直接用 Arc 的 <see cref="CopyButton"/></b>：图标变对勾、
/// <c>ResetAfter = 1000ms</c> 复原（与 dsh 的 <c>COPY_FEEDBACK_MS</c> 一致）、
/// 剪贴板失败进入 <c>CopyState.Error</c> 而不假装成功、连点自动重置计时，
/// 省去手写剪贴板与令牌防抖。
/// </para>
/// <para>
/// <b>不做 hover 显隐</b>：dsh 里旧消息的动作按钮只在 hover/focus 时出现。
/// 那需要逐条接管指针事件，收益是"安静"；但"看不见按钮"与"按钮不存在"
/// 对用户是两回事，尤其桌面端用户不一定知道要 hover。先常显。
/// </para>
/// </remarks>
public sealed class MessageActions : UserControl
{
    private readonly CopyButton _copyButton;
    private readonly TextBlock _time;

    /// <summary>成功态是否还在显示（供诊断）。</summary>
    public bool ShowingCopied => _copyButton.State == CopyState.Copied;

    public MessageActions(string text, DateTimeOffset showTimestamp)
    {
        _time = new TextBlock
        {
            Text = showTimestamp.ToLocalTime().ToString("HH:mm"),
            FontSize = 11,
            Opacity = 0.4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _copyButton = new CopyButton
        {
            Value = text,
            IsIconOnly = true,
            Variant = CopyButtonVariant.Plain,
            Label = "复制",
            CopiedLabel = "复制成功",
            FailedLabel = "复制失败",
            ResetAfter = TimeSpan.FromMilliseconds(ConversationMetrics.CopyFeedbackMs),
            VerticalAlignment = VerticalAlignment.Center,
        };

        ToolTip.SetTip(_copyButton, "复制");

        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _time, _copyButton },
        };
    }
}
