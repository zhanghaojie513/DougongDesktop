using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using DougongDesktop.App.Services;
using DougongDesktop.Ipc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 引擎状态条：一个色点 + 状态词 + 模型名。
/// </summary>
/// <remarks>
/// <b>与状态胶囊的分工</b>：胶囊回答"这一轮进行到哪了"（计时、步骤、是否在等我），
/// 状态条回答"引擎本身活着吗、用的是哪个模型"。两者都在底部，但一个是回合级、
/// 一个是进程级，混在一起会让"引擎挂了"和"模型想得久"看起来一样。
/// <para>
/// <b>为什么模型名必须显示</b>：多提供商场景下，同一句话发给不同模型的结果差异很大。
/// 用户排查"回答怎么变差了"时，第一件要确认的事就是"现在到底连的哪个模型"。
/// 让用户去翻配置文件才能看到，等于没有这个信息。
/// </para>
/// </remarks>
public sealed class EngineStatusBar : UserControl
{
    private readonly Ellipse _dot;
    private readonly TextBlock _text;

    public EngineStatusBar()
    {
        _dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        _text = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "未连接",
        };

        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _dot, _text },
        };

        SetConnection(EngineConnectionState.Disconnected, null);
    }

    /// <summary>连接状态变化（还没收到引擎自报状态时用这个）。</summary>
    public void SetConnection(EngineConnectionState state, string? detail)
    {
        var (text, color) = state switch
        {
            EngineConnectionState.Connected => ("已连接", Green),
            EngineConnectionState.Connecting => ("连接中…", Grey),
            EngineConnectionState.Starting => ("启动引擎…", Grey),
            EngineConnectionState.Failed => (detail ?? "连接失败", Red),
            _ => (detail ?? "未连接", Grey),
        };

        Apply(text, color);
    }

    /// <summary>
    /// 引擎自报状态（<see cref="EngineStatusEvent"/>）。
    /// </summary>
    /// <remarks>
    /// <b>模型名为空时保留上一次的值</b>：引擎在 Failed 时可能不上报模型名，
    /// 那时把已显示的模型名抹掉，会让用户失去"失败前用的是哪个模型"这个关键线索。
    /// </remarks>
    public void SetEngineState(EngineState state, string? modelName, string? detail)
    {
        if (modelName is { Length: > 0 }) _modelName = modelName;

        var (word, color) = state switch
        {
            EngineState.Ready => ("就绪", Green),
            EngineState.Busy => ("工作中", Amber),
            EngineState.Failed => ("失败", Red),
            _ => ("启动中", Grey),
        };

        // 失败时优先显示具体原因，否则用户只知道"坏了"不知道为什么
        var text = state == EngineState.Failed && detail is { Length: > 0 }
            ? $"{word}：{detail}"
            : word;

        Apply(text, color);
    }

    /// <summary>最近一次已知的模型名。</summary>
    private string? _modelName;

    private void Apply(string text, Color color)
    {
        _dot.Fill = new SolidColorBrush(color);
        _text.Text = _modelName is { Length: > 0 } ? $"{text} · {_modelName}" : text;
    }

    // 用固定的语义色而不是主题令牌：这三个颜色要跨深浅色都能一眼分辨，
    // 跟随主题会让"失败红"在某个主题下变成灰。
    private static readonly Color Green = Color.Parse("#3FB950");
    private static readonly Color Amber = Color.Parse("#D29922");
    private static readonly Color Red = Color.Parse("#F85149");
    private static readonly Color Grey = Color.Parse("#8B949E");
}
