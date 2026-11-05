using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;   // SetTextAsync 是扩展方法，缺这个 using 会报"未包含定义"
using Avalonia.Layout;
using Avalonia.Media;
using DougongDesktop.Ui.Core;
using TKCode.Arc.Icons.Lucide;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 用户消息下方的动作行：时间 + 复制。
/// </summary>
/// <remarks>
/// <b>dsh 的真实内容就这么少</b>（<c>MessageIconActions.tsx:45-90</c>）：
/// 「时间(HH:mm) · 复制」。没有编辑、没有重试、没有分享、没有分支——
/// 那些在助手消息的尾部动作行里（且 dsh 也只有"在新对话中分支"）。
/// <para>
/// <b>复制反馈是图标变成对勾、1000ms 后复原</b>
/// （<c>use-clipboard-copy.ts:18-44</c>，文案 <c>zh.ts:6-7</c> 的
/// <c>copy</c> / <c>copied</c>），<b>不是 toast</b>。这一点容易做错：
/// 弹 toast 会遮住消息，而动作用户正在看那条消息。
/// </para>
/// <para>
/// <b>不做 hover 显隐</b>：dsh 里旧消息的动作按钮只在 hover/focus 时出现。
/// 那需要逐条接管指针事件，收益是"安静"；但"看不见按钮"与"按钮不存在"
/// 对用户是两回事，尤其桌面端用户不一定知道要 hover。先常显。
/// </para>
/// </remarks>
public sealed class MessageActions : UserControl
{
    private readonly LucideIcon _icon;
    private readonly TextBlock _time;
    private readonly string _text;

    /// <summary>复制成功态的令牌，用来作废旧计时。</summary>
    private int _copyToken;

    /// <summary>成功态是否还在显示（供诊断）。</summary>
    public bool ShowingCopied => _icon.Kind == LucideIconKind.Check;

    public MessageActions(string text, DateTimeOffset showTimestamp)
    {
        _text = text;

        _time = new TextBlock
        {
            Text = showTimestamp.ToLocalTime().ToString("HH:mm"),
            FontSize = 11,
            Opacity = 0.4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _icon = new LucideIcon
        {
            Kind = LucideIconKind.Copy,
            Size = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var copyButton = new Button
        {
            Content = _icon,
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Columns.RadiusSm),
            Opacity = 0.5,
        };

        ToolTip.SetTip(copyButton, "复制");

        copyButton.PointerEntered += (_, _) => copyButton.Opacity = 0.9;
        copyButton.PointerExited += (_, _) => copyButton.Opacity = 0.5;
        copyButton.Click += async (_, _) => await CopyAsync(copyButton);

        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _time, copyButton },
        };
    }

    private async Task CopyAsync(Button button)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

        if (clipboard is null)
        {
            // 拿不到剪贴板时**不假装成功**：显示对勾等于骗用户，
            // 他会以为复制了然后粘贴出空内容
            ToolTip.SetTip(button, "复制失败：剪贴板不可用");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(_text);
        }
        catch (Exception ex)
        {
            ToolTip.SetTip(button, $"复制失败：{ex.Message}");
            return;
        }

        // **每次点击都作废上一次的复原计时**。
        //
        // 不这么做的话，连点三次会排三个 1000ms 的复原任务：
        // 第一个到期就把对勾换回复制图标，而用户才刚点完第三次——
        // 表现为"点了没反应"。用一个递增的令牌让旧任务自己退出。
        var token = ++_copyToken;

        _icon.Kind = LucideIconKind.Check;
        ToolTip.SetTip(button, "复制成功");

        // 1000ms 后复原——与 dsh 的 COPY_FEEDBACK_MS 一致
        await Task.Delay(ConversationMetrics.CopyFeedbackMs);

        if (token != _copyToken) return;   // 期间又点过，交给新任务管

        _icon.Kind = LucideIconKind.Copy;
        ToolTip.SetTip(button, "复制");
    }
}
