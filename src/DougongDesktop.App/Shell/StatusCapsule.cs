using Avalonia;
using Avalonia.Controls;
using DougongDesktop.App.Services;
using DougongDesktop.Ipc;
using TKCode.Arc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 运行状态胶囊：图标 + 状态词 + 计时（精确到 0.1 秒）。
/// </summary>
/// <remarks>
/// <b>它解决的核心问题</b>：计时器在跑 = 引擎活着；停了 = 可能挂住。
/// 这是唯一能区分"慢"和"死"的 UI 信号。
/// <para>
/// 用 <see cref="AgentStatus"/>（Special 包）的紧凑态，不自建：
/// 状态词、步骤列表、暂停/停止按钮、无障碍文案都是现成的。
/// </para>
/// </remarks>
public sealed class StatusCapsule : UserControl
{
    private readonly AgentStatus _status;
    private readonly TextBlock _elapsed;
    private readonly Avalonia.Threading.DispatcherTimer _ticker;

    /// <summary>本回合开始的时刻。null 表示没有运行中的回合。</summary>
    private DateTimeOffset? _turnStartedAt;

    /// <summary>
    /// 暂停累计时长：等审批的时间不计入耗时。
    /// </summary>
    /// <remarks>
    /// <b>为什么要扣</b>：审批等待取决于人多久点确认，与引擎快慢无关。
    /// 把它算进耗时会让"引擎变慢了"和"我回消息慢"混为一谈。
    /// Arc 的 <c>EndedAt</c> 文档也说明它会扣掉暂停时间，我们保持一致。
    /// </remarks>
    private TimeSpan _paused;

    private DateTimeOffset? _pauseStartedAt;
    private EngineConnectionState _connection = EngineConnectionState.Disconnected;

    public StatusCapsule()
    {
        _status = new AgentStatus
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsExpanded = false,   // 收起态才是胶囊
            MaxSteps = 5,
            SettleAfter = null,   // 不自动消失：失败状态要让用户看到
        };

        _elapsed = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Text = "—",   // 未开始显示破折号，不显示 0
        };

        Content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2,
            Children = { _status, _elapsed },
        };

        // 100ms 一跳：要能看出"它还在动"，1 秒一跳太粗
        _ticker = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _ticker.Tick += (_, _) => UpdateElapsed();
        _ticker.Start();
    }

    /// <summary>引擎连接状态变化。断开时不应显示"工作中"。</summary>
    public void SetConnection(EngineConnectionState state, string? detail)
    {
        _connection = state;

        if (state is EngineConnectionState.Failed or EngineConnectionState.Disconnected)
        {
            _status.Status = AgentStatusRunState.Failed;
            _status.Activity = new AgentStatusActivity(detail ?? "引擎未连接");
            StopTiming();
            return;
        }

        if (state == EngineConnectionState.Connected && _turnStartedAt is null)
        {
            _status.Status = AgentStatusRunState.Idle;
            _status.Activity = new AgentStatusActivity("就绪");
        }
    }

    /// <summary>回合开始：启动计时。</summary>
    public void OnTurnStarted()
    {
        _turnStartedAt = DateTimeOffset.Now;
        _paused = TimeSpan.Zero;
        _pauseStartedAt = null;

        _status.Status = AgentStatusRunState.Working;
        _status.StartedAt = _turnStartedAt;
        _status.Activity = new AgentStatusActivity("思考中");
    }

    /// <summary>
    /// 进入等待审批：停止计时。
    /// </summary>
    /// <remarks>
    /// 把状态切成 <see cref="AgentStatusRunState.NeedsInput"/>，
    /// 视觉上与"工作中"区分——用户能立刻看出是在等他。
    /// </remarks>
    public void OnApprovalRequested(string toolName)
    {
        _status.Status = AgentStatusRunState.NeedsInput;
        _status.Activity = new AgentStatusActivity($"等待审批：{toolName}");

        // 记下暂停起点，恢复时扣掉这段
        _pauseStartedAt = DateTimeOffset.Now;
    }

    /// <summary>审批完成：恢复计时。</summary>
    public void OnApprovalResolved()
    {
        if (_pauseStartedAt is { } start)
        {
            _paused += DateTimeOffset.Now - start;
            _pauseStartedAt = null;
        }

        _status.Status = AgentStatusRunState.Working;
        _status.Activity = new AgentStatusActivity("继续执行");
    }

    /// <summary>工具执行中：更新活动描述。</summary>
    public void OnToolRunning(string toolName) =>
        _status.Activity = new AgentStatusActivity($"执行 {toolName}");

    /// <summary>
    /// 工具结束、回到模型思考。
    /// </summary>
    /// <remarks>
    /// <b>必须显式回切</b>：胶囊只在收到新活动描述时才变。工具完成后若不回切，
    /// 界面会一直停在"执行 read_file"，即使模型已经开始下一轮——
    /// 那是"卡住"的假象，而胶囊的全部价值就是区分"慢"和"死"。
    /// </remarks>
    public void OnToolCompleted()
    {
        // 只在回合进行中回切：已经结束的回合不该被拉回"思考中"
        if (_turnStartedAt is null || _pauseStartedAt is not null) return;

        _status.Status = AgentStatusRunState.Working;
        _status.Activity = new AgentStatusActivity("思考中");
    }

    /// <summary>
    /// 回合结束。
    /// </summary>
    /// <remarks>
    /// <b>保留最后耗时，不归零</b>——那是排查依据。失败时尤其要留着。
    /// </remarks>
    public void OnTurnCompleted(TurnStatus status, string? diagnostic)
    {
        _status.Status = status switch
        {
            TurnStatus.Completed => AgentStatusRunState.Done,
            TurnStatus.Cancelled => AgentStatusRunState.Stopped,
            _ => AgentStatusRunState.Failed,
        };

        if (diagnostic is { Length: > 0 })
            _status.Activity = new AgentStatusActivity(diagnostic);

        // 停表但不清 _turnStartedAt：UpdateElapsed 还要用它算最终耗时
        _status.EndedAt = DateTimeOffset.Now;
        _ticker.Stop();
        UpdateElapsed();
    }

    private void StopTiming()
    {
        _turnStartedAt = null;
        _pauseStartedAt = null;
        _ticker.Stop();
        _elapsed.Text = "—";
        _status.StartedAt = null;
    }

    /// <summary>
    /// 刷新耗时文本。
    /// </summary>
    /// <remarks>
    /// 格式 <c>15分 7.9秒</c>，精确到 0.1 秒。
    /// <b>不足 1 分钟时只显示秒</b>（<c>7.9秒</c>），因为"0分 7.9秒"里的 0 是噪音。
    /// </remarks>
    private void UpdateElapsed()
    {
        if (_turnStartedAt is not { } start)
        {
            _elapsed.Text = "—";
            return;
        }

        var elapsed = DateTimeOffset.Now - start - _paused;

        // 正在等审批：显示暂停标记，让用户知道"卡住"是我在等他
        if (_pauseStartedAt is not null)
        {
            _elapsed.Text = Format(elapsed) + "（等待）";
            return;
        }

        // 已结束：用 EndedAt 而不是 Now，否则耗时会在结束后继续涨
        if (_status.EndedAt is { } ended)
        {
            elapsed = ended - start - _paused;
            _elapsed.Text = Format(elapsed);
            return;
        }

        _elapsed.Text = Format(elapsed);
    }

    private static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;

        var seconds = t.TotalSeconds % 60;
        var minutes = (int)(t.TotalSeconds / 60);

        // 不足一分钟只显示秒——"0分 7.9秒"里的 0 是噪音
        return minutes == 0
            ? $"{seconds:0.0}秒"
            : $"{minutes}分 {seconds:0.0}秒";
    }
}
