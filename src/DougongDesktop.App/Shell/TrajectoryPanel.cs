using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;
using TKCode.Arc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// Trajectory 轨迹视图（P8d D4）：基于 Arc <see cref="AgentTimeline"/> 展示每轮思考、工具调用、审批等待与子智能体协作。
/// </summary>
/// <remarks>
/// <b>为什么直接用 Arc 的 <see cref="AgentTimeline"/></b>：它原生提供 thinking（淡色）/ tool（实心）/
/// waiting（斜纹）三种耗时段、主/子智能体分行、交接肘形连线、悬停详情卡片、点击钉选路径、
/// 倍速回放与 live 模式——比自建事件表表达力高一个量级，且与全站 Arc 主题一致。
/// <para>
/// <b>不造假数据</b>：没有运行记录时明确显示空状态说明，不塞演示数据。
/// </para>
/// </remarks>
public sealed class TrajectoryPanel : UserControl
{
    private readonly AgentTimeline _timeline;
    private readonly SegmentedControl _turnPicker;
    private readonly Border _turnBar;
    private readonly Border _emptyHint;
    private readonly Border _detailStrip;
    private readonly TextBlock _detailTitle;
    private readonly TextBlock _detailBody;
    private readonly ScrollArea _scroll;

    private IReadOnlyList<TrajectoryTurnView> _turns = [];
    private string? _selectedTurnId;
    private bool _pinnedByUser;
    private bool _updatingPicker;

    public TrajectoryPanel()
    {
        _turnPicker = new SegmentedControl
        {
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _turnPicker.SelectionChanged += (_, _) =>
        {
            if (_updatingPicker) return;
            var idx = _turnPicker.SelectedIndex;
            if (idx >= 0 && idx < _turns.Count)
            {
                _selectedTurnId = _turns[idx].TurnId;
                _pinnedByUser = idx < _turns.Count - 1;
                RenderSelectedTurn();
            }
        };

        _turnBar = new Border
        {
            Padding = new Thickness(10, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = DshTheme.Brush(DshTheme.BorderL1),
            IsVisible = false,
            Child = _turnPicker,
        };

        _timeline = new AgentTimeline
        {
            AutoPlay = false,
            Speeds = [1.0, 2.0],
            Labels = BuildChineseLabels(),
            Margin = new Thickness(8),
            IsVisible = false,
        };
        _timeline.SpanSelected += OnSpanSelected;

        _emptyHint = new Border
        {
            Padding = new Thickness(14, 18),
            Child = new TextBlock
            {
                Text = "还没有运行轨迹。\n\n发送一条消息后，这里会按时间轴展示思考、工具调用、审批等待与子智能体协作。",
                FontSize = 11,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
            },
        };

        _detailTitle = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = DshTheme.Brush(DshTheme.LabelPrimary),
            TextWrapping = TextWrapping.Wrap,
        };

        _detailBody = new TextBlock
        {
            FontSize = 11,
            FontFamily = DshTheme.MonoFont,
            Foreground = DshTheme.Brush(DshTheme.LabelSecondary),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
        };

        _detailStrip = new Border
        {
            Margin = new Thickness(10, 0, 10, 10),
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            Background = DshTheme.Brush(DshTheme.Layer2),
            BorderThickness = new Thickness(1),
            BorderBrush = DshTheme.Brush(DshTheme.BorderL1),
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { _detailTitle, _detailBody },
            },
        };

        _scroll = new ScrollArea
        {
            Content = new StackPanel
            {
                Spacing = 4,
                Children = { _emptyHint, _timeline, _detailStrip },
            },
            Orientation = ScrollAreaOrientation.Vertical,
            OverlayScrollbarMode = ScrollAreaScrollbarMode.Always,
            FadeLength = 0,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                _turnBar,
                PlaceRow(_scroll, 1),
            },
        };
    }

    /// <summary>内部 Arc <see cref="AgentTimeline"/> 控件（诊断与无头自检用）。</summary>
    public AgentTimeline Timeline => _timeline;

    /// <summary>轮次选择器（诊断与无头自检用）。</summary>
    public SegmentedControl TurnPicker => _turnPicker;

    /// <summary>当前展示的轮次总数。</summary>
    public int TurnCount => _turns.Count;

    /// <summary>当前选中的轮次视图；无数据时为 null。</summary>
    public TrajectoryTurnView? SelectedTurn =>
        _turns.FirstOrDefault(t => string.Equals(t.TurnId, _selectedTurnId, StringComparison.Ordinal))
        ?? _turns.LastOrDefault();

    /// <summary>按序号切换展示的轮次（0-based）。</summary>
    public void SelectTurn(int index)
    {
        if (index < 0 || index >= _turns.Count) return;

        _selectedTurnId = _turns[index].TurnId;
        _pinnedByUser = index < _turns.Count - 1;

        _updatingPicker = true;
        try
        {
            if (index < _turnPicker.Items.Count)
                _turnPicker.SelectedIndex = index;
        }
        finally
        {
            _updatingPicker = false;
        }

        RenderSelectedTurn();
    }

    /// <summary>更新全部轮次的轨迹数据。</summary>
    public void ApplyTurns(IReadOnlyList<TrajectoryTurnView> turns)
    {
        _turns = turns;

        if (_turns.Count == 0)
        {
            _selectedTurnId = null;
            _pinnedByUser = false;
            _turnBar.IsVisible = false;
            _emptyHint.IsVisible = true;
            _timeline.IsLive = false;
            _timeline.Run = null;
            _timeline.IsVisible = false;
            _detailStrip.IsVisible = false;
            return;
        }

        // 未手工钉选旧轮次（或原轮次已不存在）时自动跟随最新一轮。
        if (!_pinnedByUser
            || _selectedTurnId is null
            || !_turns.Any(t => string.Equals(t.TurnId, _selectedTurnId, StringComparison.Ordinal)))
        {
            _selectedTurnId = _turns[^1].TurnId;
        }

        RebuildTurnPicker();
        RenderSelectedTurn();
    }

    private void RebuildTurnPicker()
    {
        _turnBar.IsVisible = _turns.Count > 1;
        if (_turns.Count <= 1) return;

        _updatingPicker = true;
        try
        {
            _turnPicker.Items.Clear();
            var selectedIdx = _turns.Count - 1;

            for (var i = 0; i < _turns.Count; i++)
            {
                var turn = _turns[i];
                _turnPicker.Items.Add(new SegmentedOption
                {
                    Value = turn.TurnId,
                    Label = $"第 {turn.TurnNumber} 轮",
                });

                if (string.Equals(turn.TurnId, _selectedTurnId, StringComparison.Ordinal))
                    selectedIdx = i;
            }

            _turnPicker.SelectedIndex = selectedIdx;
        }
        finally
        {
            _updatingPicker = false;
        }
    }

    private void RenderSelectedTurn()
    {
        var turn = SelectedTurn;
        if (turn is null)
        {
            _emptyHint.IsVisible = true;
            _timeline.IsVisible = false;
            _detailStrip.IsVisible = false;
            return;
        }

        _emptyHint.IsVisible = false;
        _timeline.IsVisible = true;
        _detailStrip.IsVisible = false;

        var run = BuildArcRun(turn);
        _timeline.Run = run;
        _timeline.IsLive = turn.Status == TurnStatus.Running;
    }

    private static AgentTimelineRun BuildArcRun(TrajectoryTurnView turn)
    {
        var run = new AgentTimelineRun
        {
            Title = turn.Title,
            StartedAt = turn.StartedAt,
            Duration = turn.Status == TurnStatus.Running ? null : turn.DurationSeconds,
        };

        foreach (var agent in turn.Agents)
        {
            run.Agents.Add(new AgentTimelineAgent
            {
                Id = agent.Id,
                Name = agent.Name,
                Model = string.IsNullOrWhiteSpace(agent.ModelName)
                    ? null
                    : new AgentModel(agent.ModelName, null, null),
                Role = agent.Role,
            });
        }

        foreach (var span in turn.Spans)
        {
            run.Spans.Add(new AgentTimelineSpan
            {
                Id = span.Id,
                AgentId = span.AgentId,
                Kind = span.Kind switch
                {
                    TrajectorySpanKind.Thinking => AgentTimelineSpanKind.Thinking,
                    TrajectorySpanKind.Waiting => AgentTimelineSpanKind.Waiting,
                    _ => AgentTimelineSpanKind.Tool,
                },
                Start = span.StartSeconds,
                End = span.EndSeconds,
                Label = span.Label,
                Tool = span.ToolName,
                Detail = span.Detail,
                Status = span.Status == TrajectorySpanStatus.Failed
                    ? AgentTimelineSpanStatus.Failed
                    : AgentTimelineSpanStatus.Ok,
            });
        }

        foreach (var handoff in turn.Handoffs)
        {
            run.Handoffs.Add(new AgentTimelineHandoff(
                handoff.FromAgentId,
                handoff.ToAgentId,
                handoff.AtSeconds,
                handoff.Label));
        }

        return run;
    }

    private void OnSpanSelected(object? sender, AgentTimelineSpanSelectedEventArgs e)
    {
        if (e.Span is not { } span)
        {
            _detailStrip.IsVisible = false;
            return;
        }

        var kindLabel = span.Kind switch
        {
            AgentTimelineSpanKind.Thinking => "思考",
            AgentTimelineSpanKind.Waiting => "等待",
            _ => "工具",
        };

        var range = span.End is { } end
            ? $"{FormatSeconds(span.Start)} → {FormatSeconds(end)}（耗时 {FormatSeconds(Math.Max(0, end - span.Start))}）"
            : $"{FormatSeconds(span.Start)} 起 · 进行中";

        _detailTitle.Text = $"[{kindLabel}] {span.Label}";

        var lines = new List<string> { range };
        if (!string.IsNullOrWhiteSpace(span.Tool))
            lines.Add($"工具：{span.Tool}");
        if (span.Status == AgentTimelineSpanStatus.Failed)
            lines.Add($"状态：失败{(string.IsNullOrWhiteSpace(span.Detail) ? "" : $"（{span.Detail}）")}");
        else if (!string.IsNullOrWhiteSpace(span.Detail))
            lines.Add(span.Detail);

        _detailBody.Text = string.Join("\n", lines);
        _detailStrip.IsVisible = true;
    }

    /// <summary>构造完整中文化的 <see cref="AgentTimelineLabels"/>（消除默认英文文案）。</summary>
    public static AgentTimelineLabels BuildChineseLabels() => new()
    {
        DefaultTitle = "运行轨迹",
        Running = "回放中",
        Paused = "已暂停",
        Ready = "就绪",
        Live = "运行中",
        Thinking = "思考",
        ToolCall = "工具调用",
        Waiting = "等待",
        Handoff = "交接",
        Failed = "失败",
        TokensUnit = "tokens",
        FinishedPrefix = "已完成：",
        Play = "播放",
        Pause = "暂停",
        Replay = "重播",
        Restart = "从头开始",
        PlaybackSpeed = "回放倍速",
        Legend = "图例",
        RunTime = "时间轴",
        Spans = "执行片段",
        Time = FormatSeconds,
        SpeedOption = s => $"{s.ToString("0.#", CultureInfo.InvariantCulture)}x",
        Agents = n => $"{n} 个智能体",
        Speedup = s => s < 1.05
            ? "串行执行"
            : $"{s.ToString("0.0", CultureInfo.InvariantCulture)}x 并行加速",
        AgentsWorking = n => $"，{n} 个智能体执行中",
        ScrubberValue = (cur, total) => $"{FormatSeconds(cur)} / {FormatSeconds(total)}",
        PlayingAt = s => $"以 {s.ToString("0.#", CultureInfo.InvariantCulture)}x 回放中",
        PausedAt = t => $"暂停于 {FormatSeconds(t)}",
        HandoffFrom = (agent, label) => string.IsNullOrWhiteSpace(label) ? $"来自 {agent}" : $"来自 {agent}：{label}",
        HandoffTo = (agent, label) => string.IsNullOrWhiteSpace(label) ? $"交给 {agent}" : $"交给 {agent}：{label}",
        HandoffsDuring = n => $"此阶段发生 {n} 次交接",
        FailedNote = detail => string.IsNullOrWhiteSpace(detail) ? "执行失败" : $"失败：{detail}",
        RetryNote = "失败后重试",
    };

    private static string FormatSeconds(double seconds)
    {
        if (seconds < 0) seconds = 0;
        if (seconds < 60)
            return $"{seconds.ToString("0.0", CultureInfo.InvariantCulture)}s";

        var mins = (int)(seconds / 60);
        var rem = seconds - mins * 60;
        return $"{mins}m {rem.ToString("00.0", CultureInfo.InvariantCulture)}s";
    }

    private static Control PlaceRow(Control c, int row)
    {
        Grid.SetRow(c, row);
        return c;
    }
}
