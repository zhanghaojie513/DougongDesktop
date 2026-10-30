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
            if (_approvalPending) return;

            // 先看有没有命令 token：/clear 这类是**本地动作**，
            // 不该当成消息发给引擎。
            //
            // Arc 没有"命令被选中"的独立事件——命令以 token 形式随 Sent 一起回来。
            // 所以判断"这一条是命令还是普通消息"只能看 Tokens。
            if (TryRunCommand(e.Message))
            {
                _composer.Clear();
                return;
            }

            var text = e.Message?.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            _composer.Clear();
            Submitted?.Invoke(text);
        };

        _composer.StopRequested += (_, _) => StopRequested?.Invoke();

        _composer.ModelChanged += (_, e) =>
        {
            if (e.NewModel?.Id is { Length: > 0 } id) ModelChanged?.Invoke(id);
        };

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

    /// <summary>
    /// 如果这条输入里带命令 token，执行它并返回 true。
    /// </summary>
    /// <remarks>
    /// <b>为什么用 token 而不是解析文本</b>：用户可能手打 "/clear" 当普通文本。
    /// token 是 Arc 在用户真正从命令列表里选中时才会产生的，用它区分
    /// "选中了命令"和"打了个像命令的词"。
    /// <para>
    /// <b>命令优先于正文</b>：带命令的输入不发给引擎。
    /// 这一条与 dsh 一致——斜杠命令是本地动作。
    /// </para>
    /// </remarks>
    private bool TryRunCommand(AiComposerMessage? message)
    {
        if (message?.Tokens is not { Count: > 0 }) return false;

        foreach (var token in message.Tokens)
        {
            // 只认命令类 token；提及（@）与命令不是一回事
            var kind = token.GetType().GetProperty("Kind")?.GetValue(token)?.ToString();
            if (kind is not "Command") continue;

            var id = token.GetType().GetProperty("Id")?.GetValue(token) as string;
            if (id is { Length: > 0 })
            {
                CommandInvoked?.Invoke(id);
                return true;
            }
        }

        return false;
    }

    /// <summary>用户从 "/" 列表选中并发送了一条命令。</summary>
    public event Action<string>? CommandInvoked;

    /// <summary>底层控件，供诊断确认尺寸。</summary>
    public AiComposer Inner => _composer;

    /// <summary>
    /// 装上斜杠命令。
    /// </summary>
    /// <remarks>
    /// <b>命令由宿主提供而不是写死在这里</b>：命令的可用性取决于当前状态
    /// （引擎连上没有、有没有会话）。写死在 Composer 里会让它需要知道
    /// 这些状态，而它应当只负责输入。
    /// </remarks>
    public void SetCommands(IReadOnlyList<AiCommand> commands)
    {
        _composer.Commands.Clear();
        foreach (var c in commands) _composer.Commands.Add(c);
    }

    /// <summary>
    /// 装上模型清单与 provider 分组。
    /// </summary>
    /// <remarks>
    /// <b>空列表会隐藏模型 pill</b>（Arc 的行为）。这正是我们要的：
    /// 没连引擎时不该显示一个点不开的模型选择器。
    /// </remarks>
    public void SetModels(IReadOnlyList<AiModel> models, IReadOnlyList<AiModelProvider> providers)
    {
        _composer.Models.Clear();
        foreach (var m in models) _composer.Models.Add(m);

        _composer.Providers.Clear();
        foreach (var p in providers) _composer.Providers.Add(p);
    }

    /// <summary>选中某个模型（按 Id）。</summary>
    /// <remarks>
    /// <b>找不到时不报错</b>：模型清单可能滞后于当前配置
    /// （比如配置文件里的模型不在能力表里）。那时 pill 保持未选中状态，
    /// 比显示一个不存在的模型好。
    /// </remarks>
    public void SelectModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            _composer.SelectedModel = null;
            return;
        }

        _composer.SelectedModel = _composer.Models
            .FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 装上 "&#64;" 可引用的条目。
    /// </summary>
    /// <remarks>
    /// <b>工作区文件与目录都放进去</b>：用户想写 "看看 @src/Agent" 和
    /// "看看 @README.md" 是同一类需求，分成两个入口反而要他先想清楚。
    /// </remarks>
    public void SetContextItems(IReadOnlyList<AiContextItem> items)
    {
        _composer.ContextItems.Clear();
        foreach (var i in items) _composer.ContextItems.Add(i);
    }

    /// <summary>用户选了某个模型。</summary>
    public event Action<string>? ModelChanged;



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
