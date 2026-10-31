using Avalonia;
using Avalonia.Controls;
using DougongDesktop.App.Services;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;
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

    /// <summary>
    /// 回合计时。
    /// </summary>
    /// <remarks>
    /// <b>语义对齐 Arc 的 <c>AgentStatusRunClock</c></b>：从运行开始到现在，
    /// <b>扣掉每段 Paused 与 Failed</b>。
    /// <para>
    /// <b>为什么不直接用那个类</b>：实测它是 <c>internal</c>（反射确认
    /// <c>IsPublic=False</c>），外部引用不到。所以按它的语义自己实现一份——
    /// 关键不是复用代码，是复用**语义**：原来的手写版本只扣审批等待，
    /// <b>漏了失败段</b>，一个工具连续失败时那些重试时间会被算进"引擎耗时"。
    /// </para>
    /// <para>
    /// 纯记账：<c>Observe(state, now)</c> 记状态变化，<c>Elapsed(now)</c>
    /// 取当前活跃时长。时间由调用方传入，所以测试可注入固定时刻。
    /// </para>
    /// </remarks>
    private readonly RunClock _clock = new();

    /// <summary>本回合是否还在进行。</summary>
    private bool _running;

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

        if (state == EngineConnectionState.Connected && !_running)
        {
            _status.Status = AgentStatusRunState.Idle;
            _status.Activity = new AgentStatusActivity("就绪");
        }
    }

    /// <summary>回合开始：启动计时。</summary>
    /// <remarks>
    /// <b>用 <c>SetStartedAt</c> 而不是继续累加</b>：新回合是新的计时起点，
    /// 上一回合记录的暂停段不再适用。clock 的文档专门说了这一点。
    /// </remarks>
    public void OnTurnStarted()
    {
        _running = true;
        _clock.SetStartedAt(DateTimeOffset.Now);

        _status.Status = AgentStatusRunState.Working;
        _status.StartedAt = _clock.StartedAt;
        _status.Activity = new AgentStatusActivity("思考中");
    }

    /// <summary>
    /// 进入等待审批。
    /// </summary>
    /// <remarks>
    /// <b>切成 <c>NeedsInput</c> 而不是 <c>Paused</c></b>：视觉上要能看出
    /// "在等他"而不是"卡住了"。而 <b>计时不停</b>——手动文档里明确
    /// "Waiting for input counts"（等输入算活跃时间），所以这一步只改状态，
    /// clock 会把它记为活跃段。
    /// </remarks>
    public void OnApprovalRequested(string toolName)
    {
        _clock.Observe(RunOutcome.NeedsInput, DateTimeOffset.Now);

        _status.Status = AgentStatusRunState.NeedsInput;
        _status.Activity = new AgentStatusActivity($"等待审批：{toolName}");
    }

    /// <summary>审批完成：回到工作状态。</summary>
    public void OnApprovalResolved()
    {
        _clock.Observe(RunOutcome.Working, DateTimeOffset.Now);

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
        if (!_running) return;

        _status.Status = AgentStatusRunState.Working;
        _status.Activity = new AgentStatusActivity("思考中");
    }

    /// <summary>
    /// 回合结束。
    /// </summary>
    /// <remarks>
    /// <b>保留最后耗时，不归零</b>——那是排查依据。失败时尤其要留着。
    /// <para>
    /// <b>把终态也交给 clock</b>：失败时段会被它排除在活跃时间之外。
    /// 原来的手写版本只扣审批等待，<b>漏了失败</b>——一个工具连续失败时，
    /// 那些重试时间会被算进"引擎耗时"。
    /// </para>
    /// </remarks>
    public void OnTurnCompleted(TurnStatus status, string? diagnostic)
    {
        var now = DateTimeOffset.Now;

        var runState = status switch
        {
            TurnStatus.Completed => AgentStatusRunState.Done,
            TurnStatus.Cancelled => AgentStatusRunState.Stopped,
            _ => AgentStatusRunState.Failed,
        };

        _clock.Observe(status switch
        {
            TurnStatus.Completed => RunOutcome.Done,
            TurnStatus.Cancelled => RunOutcome.Stopped,
            _ => RunOutcome.Failed,
        }, now);
        _running = false;

        _status.Status = runState;

        if (diagnostic is { Length: > 0 })
            _status.Activity = new AgentStatusActivity(diagnostic);

        // 停表但不清 startedAt：UpdateElapsed 还要用它算最终耗时
        _status.EndedAt = now;
        _ticker.Stop();
        UpdateElapsed();
    }

    private void StopTiming()
    {
        _running = false;
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
    /// <para>
    /// <b>耗时全部来自 <see cref="RunClock"/></b>，这里只负责格式化——
    /// 扣哪些段是 clock 的事，这里不掺和。
    /// </para>
    /// </remarks>
    private void UpdateElapsed()
    {
        if (_clock.StartedAt is null)
        {
            _elapsed.Text = "—";
            return;
        }

        // 已结束：用 EndedAt 而不是 Now，否则耗时会在结束后继续涨
        var at = _clock.EndedAt ?? DateTimeOffset.Now;
        var elapsed = _clock.Elapsed(at);

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
