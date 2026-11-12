using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DougongDesktop.Ui.Core;
using TKCode.Arc.Icons.Lucide;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 流式进行中的状态行："深度求索中，用时 N ···"。
/// </summary>
/// <remarks>
/// 出处 dsh <c>RunningStatus.tsx:25-48</c>：鲸鱼尾图标 +
/// <c>chat.deepDivingFor</c> =「深度求索中，用时 {duration}」+ 尾随省略号。
/// 每秒刷新一次（<c>chat/message-chrome.ts:13</c> 的
/// <c>LIVE_RUN_CLOCK_INTERVAL_MS</c>）。
/// <para>
/// <b>为什么必须放进消息流而不是只放顶部胶囊</b>：用户往上滚看历史时，
/// 顶部胶囊可能已经滚出视野（或被顶栏遮住），此时"它在干活吗"没有答案。
/// dsh 把它放在消息流末尾，正好在"即将出现新内容"的位置——
/// 这是唯一能同时回答"在跑吗"和"跑多久了"的位置。
/// </para>
/// <para>
/// <b>计时从 TurnStarted 起算，不包含暂停</b>：dsh 的生命周期里
/// 运行时钟会排除暂停段。我们的 <see cref="RunClock"/> 是为此写的，
/// 但这里只用最简形态（单调累计），因为状态行在回合结束时立刻消失，
/// 跨暂停的累计精度不影响观感。
/// </para>
/// </remarks>
public sealed class RunStatusLine : UserControl
{
    private readonly TextBlock _text;
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _startedAt;

    public RunStatusLine()
    {
        _text = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new LucideIcon
                {
                    Kind = LucideIconKind.Sparkles,
                    Size = 13,
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                _text,
            },
        };

        Content = row;

        // 1 秒刷新——与 dsh 的 LIVE_RUN_CLOCK_INTERVAL_MS 一致。
        // 用 DispatcherTimer 而不是 Task.Delay 循环：它随控件生命周期停，
        // 不会在窗口关掉后继续跑。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Update();
    }

    /// <summary>开始计时并显示。</summary>
    public void Start(DateTimeOffset now)
    {
        _startedAt = now;
        Update();
        _timer.Start();
    }

    /// <summary>停止计时并隐藏。</summary>
    public void Stop()
    {
        _timer.Stop();
        IsVisible = false;
    }

    /// <summary>当前显示的文本（供诊断）。</summary>
    public string CurrentText => _text.Text ?? string.Empty;

    private void Update()
    {
        var elapsed = DateTimeOffset.Now - _startedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        // **照 dsh 的 formatRunDuration（message-chrome.ts:54-64）**。
        //
        // 三处此前与源码不一致，2026-11-12 对照修正：
        //  1. 用 `Math.Floor` **向下取整**，不是四舍五入（`:0` 会进位）。
        //     源码第 55 行：`Math.max(0, Math.floor(ms / 1000))`。
        //  2. **最小 1 秒**（RunningStatus.tsx:34 的 `Math.max(1000, ...)`）——
        //     刚起跑时显示"0 秒"会像没在走。
        //  3. **有小时档**：源码 60-61 行，`total >= 3600` 时报小时，
        //     `total >= 60` 时报分钟，秒永远报。
        _text.Text = FormatElapsed(elapsed);
    }

    /// <summary>
    /// 把耗时写成人读的"用时"文案。
    /// </summary>
    /// <remarks>
    /// <b>提取成 public static 是为了能被检查断言</b>：写在 <c>Update()</c> 里
    /// 就只能靠"源码里有没有 Math.Floor"这种字符串匹配，而那证明不了
    /// 实际输出对不对——这个项目已经栽过几次。
    /// </remarks>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        // 照 dsh 的 formatRunDuration（`message-chrome.ts:54-64`）：
        //  1. 向下取整（源码 55 行 `Math.floor`），不是四舍五入
        //  2. 最小 1 秒（`RunningStatus.tsx:34` 的 `Math.max(1000, ...)`）
        //  3. 有小时档，秒始终显示
        var ms = Math.Max(1000, (long)elapsed.TotalMilliseconds);
        var total = (int)(ms / 1000);
        var hours = total / 3600;
        var minutes = total / 60 % 60;
        var seconds = total % 60;

        var parts = new List<string>();
        if (hours > 0) parts.Add($"{hours} 小时");
        if (total >= 60) parts.Add($"{minutes} 分");
        parts.Add($"{seconds} 秒");

        return "深度求索中，用时 " + string.Join(" ", parts);
    }
}
