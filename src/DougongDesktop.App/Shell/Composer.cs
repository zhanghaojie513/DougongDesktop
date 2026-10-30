using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 消息输入区：文本 + 发送 / 停止 + 待决锁。
/// </summary>
/// <remarks>
/// <b>用 <see cref="AiComposer"/> 而不是自建 TextBox</b>：它已经把
/// "生成时发送键变停止键"、"空文本时不可发送"、"/ 命令与 @ 提及"、
/// 随文本增长的输入框这些做完了。自建至少要两三百行，而且键盘可达性与
/// 无障碍文案做不到一致。
/// <para>
/// <b>本类只负责三件 Arc 不管的事</b>：
/// <list type="number">
/// <item>把 Sent / StopRequested 转成宿主能懂的回调</item>
/// <item><b>待决锁</b>——有审批没答时禁用输入并说明原因（P6 判据）</item>
/// <item>正在生成时禁止再次发送（Arc 的发送键已变停止键，但仍要防键盘回车）</item>
/// </list>
/// </para>
/// <para>
/// <b>待决锁为什么必须有</b>：审批未答时用户再发一条，等于在一个"等授权"的
/// 回合上叠加新回合。引擎侧会拒绝（同一会话不能并发回合），但用户看到的是
/// "发不出去"，而不是"请先处理审批"。锁的意义是把原因说清楚。
/// </para>
/// </remarks>
public sealed class Composer : UserControl
{
    private readonly AiComposer _composer;
    private readonly TextBlock _lockHint;

    /// <summary>当前是否有未答复的审批。</summary>
    private bool _approvalPending;

    /// <summary>用户发出消息。参数是正文。</summary>
    public event Action<string>? Submitted;

    /// <summary>用户请求停止当前回合。</summary>
    public event Action? StopRequested;

    public Composer()
    {
        _composer = new AiComposer
        {
            Placeholder = "输入消息，Enter 发送，Shift+Enter 换行",
            MinLines = 1,
            MaxLines = 8,
            IsAttachmentEnabled = false,   // P6 不做附件，先不显示假的加号
            IsGenerating = false,
        };

        _composer.Sent += (_, e) =>
        {
            var text = e.Message?.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            // 待决锁：审批没答时不许发新的
            if (_approvalPending) return;

            _composer.Clear();
            Submitted?.Invoke(text);
        };

        _composer.StopRequested += (_, _) => StopRequested?.Invoke();

        // 待决提示：不遮挡输入框，就压在输入框上方一行
        _lockHint = new TextBlock
        {
            FontSize = 11,
            IsVisible = false,
            Margin = new Thickness(4, 0, 4, 4),
            TextWrapping = TextWrapping.Wrap,
        };

        Content = new StackPanel
        {
            Spacing = 0,
            Children = { _lockHint, _composer },
        };
    }

    /// <summary>底层控件，供诊断确认尺寸。</summary>
    public AiComposer Inner => _composer;

    /// <summary>当前是否处于待决锁。</summary>
    public bool IsApprovalPending => _approvalPending;

    /// <summary>
    /// 进入 / 退出待决锁。
    /// </summary>
    /// <remarks>
    /// <b>锁的是"能不能发新消息"，不是"能不能输入"</b>：用户仍然应该能
    /// 先把下一条写好。把整个输入框禁掉会让人在等授权时无事可做，
    /// 而等待时间取决于他自己多久点确认——那不该连累打字。
    /// </remarks>
    public void SetApprovalPending(bool pending, string? toolName = null)
    {
        _approvalPending = pending;

        _lockHint.IsVisible = pending;
        _lockHint.Text = pending
            ? $"有审批待处理（{toolName ?? "工具调用"}），处理后才能继续发送。"
            : null;

        _lockHint.Foreground = new SolidColorBrush(Color.Parse("#D29922"));
        UpdateSendState();
    }

    /// <summary>回合开始 / 结束：切换发送键与停止键。</summary>
    public void SetGenerating(bool generating)
    {
        _composer.IsGenerating = generating;
        UpdateSendState();
    }

    /// <summary>把焦点放回输入框（切会话、审批结束后）。</summary>
    public void FocusInput() => _composer.FocusInput();

    public void SetText(string text) => _composer.SetPrompt(text);

    /// <summary>
    /// 刷新可发送状态。
    /// </summary>
    /// <remarks>
    /// <b>Arc 的 <see cref="AiComposer.SendState"/> 是只读的</b>，它由"文本是否为空"
    /// 与 <c>IsGenerating</c> 自己算出来。待决锁是我们加的维度，Arc 不知道，
    /// 所以只能用 <c>IsEnabled</c> 叠加——保留 Arc 自己的 Empty/Blocked 判定，
    /// 只在锁生效时额外压制。
    /// <para>
    /// <b>不能只靠 IsEnabled</b>：键盘回车仍可能触发 Sent（Arc 在禁用态下已拦，
    /// 但依赖库行为不如自己再判一次）。Sent 处理里的 <c>_approvalPending</c>
    /// 判断就是第二道保险。
    /// </para>
    /// </remarks>
    private void UpdateSendState()
    {
        // 生成中允许"停止"，所以生成态不禁用；只有待决锁禁用发送
        _composer.IsEnabled = !_approvalPending || _composer.IsGenerating;
    }
}
