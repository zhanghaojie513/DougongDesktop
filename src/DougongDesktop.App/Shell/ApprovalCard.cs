using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DougongDesktop.Ipc;
using TKCode.Arc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 内联审批卡片：工具预览 + 四档决策 + 倒计时。
/// </summary>
/// <remarks>
/// <b>为什么在消息流里而不是弹窗</b>：弹窗会遮住用户正要判断的上下文——
/// 你要判断"这个 rm 该不该跑"，需要能看到模型刚说了什么。dsh 与 Codex
/// 都把审批做成消息流里的一行。
/// <para>
/// <b>四档决策的原因</b>：只给"允许/拒绝"会让用户在两难间被迫选"拒绝后重来"。
/// 「本会话允许」解决"同一个读文件调用问了十遍"，「拒绝并停止」解决
/// "模型方向整个错了，别再往下跑"。
/// </para>
/// <para>
/// <b>倒计时必须显示</b>：审批 5 分钟过期，不显示的话用户会以为
/// "点了没反应"，实际是已经过期了。过期后按钮变灰不可点，且写明原因——
/// 不能只把按钮禁用而不说为什么。
/// </para>
/// </remarks>
public sealed class ApprovalCard : UserControl
{
    private readonly ApprovalRequest _request;
    private readonly TextBlock _countdown;
    private readonly DispatcherTimer _timer;
    private readonly StackPanel _buttons;
    private readonly TextBlock _resolved;

    private bool _answered;

    /// <summary>用户做出决策。参数是决策与理由。</summary>
    public event Action<ApprovalDecision, string?>? Decided;

    /// <summary>请求 Id。宿主用它把答复配对回引擎。</summary>
    public string RequestId => _request.RequestId;

    /// <summary>是否已答复或已过期。</summary>
    public bool IsResolved => _answered || IsExpired;

    /// <summary>是否已过期。</summary>
    public bool IsExpired => DateTimeOffset.Now >= _request.ExpiresAt;

    public ApprovalCard(ApprovalRequest request)
    {
        _request = request;

        _countdown = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _resolved = new TextBlock
        {
            FontSize = 12,
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
        };

        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
        };

        BuildButtons();

        Content = new Border
        {
            Margin = new Thickness(12, 6),
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(6),
            // 审批用警示色边框而不是填充：填充会让整块看起来像"错误"，
            // 而审批是正常流程的一部分
            BorderBrush = new SolidColorBrush(Color.Parse("#D29922")),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(12, 210, 153, 34)),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    BuildHeader(),
                    BuildPreview(),
                    _buttons,
                    _resolved,
                },
            },
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private Control BuildHeader()
    {
        var title = new TextBlock
        {
            Text = $"需要批准：{_request.ToolName}",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(title, 0);
        row.Children.Add(title);
        Grid.SetColumn(_countdown, 1);
        row.Children.Add(_countdown);

        // 不可撤销的副作用单独标出来：这是用户最该看一眼的信息，
        // 混在正文里等于没说
        if (!_request.MayHaveIrreversibleEffects) return row;

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                row,
                new TextBlock
                {
                    Text = "⚠ 这个操作可能无法撤销。",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#F85149")),
                },
            },
        };
    }

    /// <summary>
    /// 工具预览：显示将要执行的命令 / 路径，不只是工具名。
    /// </summary>
    /// <remarks>
    /// <b>"工具名"不足以让人做判断</b>：<c>read_file</c> 是读 README 还是读
    /// ~/.ssh/id_rsa，风险完全不同。所以参数必须显示出来。
    /// <para>
    /// <b>参数按键值对显示而不是原始 JSON</b>：一行压缩的 JSON 里找一个路径
    /// 比对齐的键值对难得多，而审批要求用户真的看清。
    /// </para>
    /// </remarks>
    private Control BuildPreview()
    {
        var stack = new StackPanel { Spacing = 2 };

        if (_request.Summary is { Length: > 0 })
        {
            stack.Children.Add(new TextBlock
            {
                Text = _request.Summary,
                FontSize = 11,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (var (key, value) in ParseArguments(_request.ArgumentsJson))
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"{key}  {value}",
                FontFamily = new FontFamily("Geist Mono, Consolas, monospace"),
                FontSize = 11,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return stack;
    }

    private static List<(string Key, string Value)> ParseArguments(string json)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // 坏 JSON 也要给出来：审批时"看不到参数"比"参数格式差"更危险
            result.Add(("(无法解析的参数)", json));
            return result;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                result.Add(("(参数)", json));
                return result;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result.Add((prop.Name, prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText()));
            }
        }

        return result;
    }

    /// <summary>
    /// 四档决策按钮。
    /// </summary>
    /// <remarks>
    /// <b>拒绝不是主按钮</b>：审批的默认预期是"允许"，但代价必须可见。
    /// 「拒绝并停止」放在最后且用不同样式，避免误点——它的后果比"拒绝"
    /// 重得多（整个回合终止，模型无法自我纠正）。
    /// </remarks>
    private void BuildButtons()
    {
        _buttons.Children.Add(MakeButton("允许一次", isPrimary: true,
            () => Resolve(ApprovalDecision.AllowOnce, null)));

        _buttons.Children.Add(MakeButton("本会话允许", isPrimary: false,
            () => Resolve(ApprovalDecision.AllowForSession, null)));

        _buttons.Children.Add(MakeButton("拒绝", isPrimary: false,
            () => Resolve(ApprovalDecision.Deny, "用户拒绝")));

        _buttons.Children.Add(MakeButton("拒绝并停止", isPrimary: false,
            () => Resolve(ApprovalDecision.DenyAndStop, "用户拒绝并要求停止")));
    }

    private TKCode.Arc.Button MakeButton(string text, bool isPrimary, Action onClick)
    {
        var button = new TKCode.Arc.Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(12, 5),
        };

        if (isPrimary)
        {
            // 主按钮用实心，其余保持默认——四个同等醒目的按钮等于没有主次
            button.Classes.Add("accent");
        }

        button.Click += (_, _) =>
        {
            // 连点防护第一层：已经答复过的卡片不再产生第二次授权。
            // 这是**界面侧**的保险，引擎侧另有一层（重复答复降级为拒绝）。
            if (_answered || IsExpired) return;
            onClick();
        };

        return button;
    }

    private void Resolve(ApprovalDecision decision, string? reason)
    {
        if (_answered) return;   // 重复点击只授权一次

        _answered = true;
        _timer.Stop();
        _buttons.IsVisible = false;

        _resolved.IsVisible = true;
        _resolved.Text = decision switch
        {
            ApprovalDecision.AllowOnce => "已允许本次调用。",
            ApprovalDecision.AllowForSession => "已允许本会话内的同类调用。",
            ApprovalDecision.Deny => "已拒绝。",
            _ => "已拒绝并停止回合。",
        };
        _resolved.Opacity = 0.7;

        Decided?.Invoke(decision, reason);
    }

    /// <summary>每秒刷新倒计时；过期就禁用按钮并写明原因。</summary>
    private void Tick()
    {
        if (_answered)
        {
            _timer.Stop();
            return;
        }

        var left = _request.ExpiresAt - DateTimeOffset.Now;

        if (left <= TimeSpan.Zero)
        {
            _timer.Stop();

            // 过期：按钮禁用 + 说明原因。只禁用不解释，用户会以为点不动是 bug
            _buttons.IsEnabled = false;
            _resolved.IsVisible = true;
            _resolved.Text = "审批已过期，这条请求不能再答复。";
            _resolved.Opacity = 0.55;
            _countdown.Text = "已过期";
            _countdown.Foreground = new SolidColorBrush(Color.Parse("#F85149"));

            // 不在这里发 Denied：引擎侧的审批器有自己的超时（同一个 5 分钟），
            // 它超时后会按拒绝处理并把结果投影给界面。这里再发一次就是**第二次答复**，
            // 而"重复答复不能产生第二次授权/第二次拒绝"是硬规则。
            // 这一层只负责让界面如实显示"已过期"。
            return;
        }

        _countdown.Text = left.TotalSeconds >= 60
            ? $"{(int)left.TotalMinutes}:{left.Seconds:00} 后过期"
            : $"{left.TotalSeconds:0} 秒后过期";

        // 最后 30 秒变红：还剩多久直接影响用户要不要现在处理
        if (left.TotalSeconds <= 30)
            _countdown.Foreground = new SolidColorBrush(Color.Parse("#F85149"));
    }

    /// <summary>页面卸载时停表，避免定时器持有已移除的控件。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
