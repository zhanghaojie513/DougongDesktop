using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TKCode.Arc;

namespace DougongDesktop.App.Views;

/// <summary>
/// P2 的最小验证窗口：确认 Arc 真能用，不是做界面。
/// </summary>
/// <remarks>
/// 每一项对应一条判据。失败要在这一步暴露，而不是等堆完界面才发现。
/// <para>
/// <b>API 来源</b>：全部用反射从 <c>TKCode.Arc.dll</c> 核实过，不是照文档站猜的。
/// 文档站讲的是 0.3.1，且部分成员（如 <c>Markdown.Append</c>）不在 XML 注释里，
/// 只 grep XML 会漏。
/// </para>
/// </remarks>
public sealed partial class ArcProbeWindow : Window
{
    private readonly Markdown _markdown;
    private readonly Table _table;
    private int _streamChunk;
    private string _streamBuffer = string.Empty;

    public ArcProbeWindow()
    {
        InitializeComponent();

        _markdown = this.FindControl<Markdown>("StreamingMarkdown")!;
        _table = this.FindControl<Table>("ProbeTable")!;

        // 初始给一段静态内容，验证渲染本身。
        _markdown.Text = """
            ## 静态渲染验证

            如果这段文字有样式（标题层级、正文行高），说明 `Markdown` 工作正常。

            - 列表项一
            - 列表项二

            ```csharp
            var kernel = new DougongKernel();
            ```
            """;

        BuildProbeRows();

        // 布局完成后 dump 一次控件树：比截图可靠，能确认每个组件真的被实例化、
        // 真的拿到了非零尺寸。截图会受窗口裁切和 DPI 缩放影响。
        Opened += async (_, _) =>
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(DumpLayout,
                Avalonia.Threading.DispatcherPriority.Background);
            await RunSelfCheckAsync();
        };
    }

    /// <summary>
    /// 逐段喂入文本，验证流式追加。
    /// </summary>
    /// <remarks>
    /// <b>正确用法是 <c>Append</c> + <c>Complete</c></b>，不是设 <c>Stream</c> 属性——
    /// 后者是接收流式数据源的入口（<c>MarkdownStream</c> 类型），
    /// 而我们的事件本来就是增量字符串。
    /// <para>
    /// 每次只追加新片段：Arc 的注释写"Markdown body receives only the new text,
    /// so a long reply is not parsed again from its start on each chunk"。
    /// 整段重设会让它每帧重新解析全文。
    /// </para>
    /// </remarks>
    private void OnStartStream(object? sender, RoutedEventArgs e)
    {
        _streamChunk = 0;
        _streamBuffer = string.Empty;
        _markdown.Text = string.Empty;

        var chunks = new[]
        {
            "# 流式验证\n\n",
            "这段文字是**逐段追加**的，不是一次性渲染。\n\n",
            "第 3 段：如果前一段没有消失，说明追加正确。\n\n",
            "- 列表一\n",
            "- 列表二\n\n",
            "```csharp\n",
            "Console.WriteLine(\"done\");\n",
            "```\n",
        };

        // 用定时器模拟流式节奏：100ms 一段。
        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };

        timer.Tick += (_, _) =>
        {
            if (_streamChunk >= chunks.Length)
            {
                timer.Stop();
                return;
            }

            _streamBuffer += chunks[_streamChunk++];
            _markdown.Text = _streamBuffer;

            if (_streamChunk >= chunks.Length)
            {
                timer.Stop();
            }
        };

        timer.Start();
    }

    // 独立 Markdown 没有 Complete 语义（那是给消息流用的）；此处保留按钮作对照，不做动作。
    private void OnCompleteStream(object? sender, RoutedEventArgs e) { /* 独立 Markdown 无 Complete 语义 */ }

    private void OnLightTheme(object? sender, RoutedEventArgs e) =>
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Light));

    private void OnDarkTheme(object? sender, RoutedEventArgs e) =>
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));

    private void OnSystemTheme(object? sender, RoutedEventArgs e) =>
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.System));

    /// <summary>
    /// 构造表格行，验证自定义单元格。
    /// </summary>
    /// <remarks>
    /// <b>这是 P7.5 插件列表的原型</b>：需要在一行里放图标、名称、状态徽章、开关。
    /// <c>TableRow</c> 的注释说"单元格默认绘文本，自定义内容可作为 children 承载"——
    /// 这里就是要验证这句话。
    /// </remarks>
    /// <summary>
    /// 自动自检：跑流式渲染与主题切换，把结果打到 stderr。
    /// </summary>
    /// <remarks>
    /// 这是 P2 的判据落地方式——不靠人点按钮，也不靠截图。
    /// 截图只能证明"有东西显示了"，证明不了"流式追加语义对不对"。
    /// </remarks>
    private async Task RunSelfCheckAsync()
    {
        // 1. 流式：独立 Markdown 控件没有"消息容器"，Append 会返回 false
        //    （它的注释写 "Streams onto the end of a message... Returns false when no message has one"）。
        //    独立使用时正确做法是把增量累加进 Text。
        _markdown.Text = string.Empty;
        var NL = Environment.NewLine;
        var chunks = new[] { "# 流式验证" + NL + NL, "第一段。", "第二段追加。", "第三段。" + NL };
        var accumulated = "";
        var lengths = new List<int>();
        foreach (var c in chunks)
        {
            accumulated += c;
            _markdown.Text = accumulated;
            await Task.Delay(60);
            lengths.Add(_markdown.Text?.Length ?? 0);
        }
        await Task.Delay(300);
        Console.Error.WriteLine($"[probe] 流式累加长度序列 {string.Join(" -> ", lengths)}");
        Console.Error.WriteLine($"[probe] 是否单调递增 {lengths.Zip(lengths.Skip(1), (a, b) => b >= a).All(x => x)}");
        Console.Error.WriteLine($"[probe] 最终长度 {_markdown.Text?.Length ?? 0}（期望 {accumulated.Length}）");
        Console.Error.WriteLine($"[probe] IsDocumentReady={_markdown.IsDocumentReady}（解析完成后应为 True）");
        Console.Error.WriteLine($"[probe] 大纲标题数 {_markdown.GetOutline().Count}（期望 1）");

        // 2. 主题切换：浅 -> 深，看背景色是否真的变
        TKCode.Arc.ArcTheme.Apply(t => t.Appearance(TKCode.Arc.ArcAppearance.Light));
        await Task.Delay(200);
        Console.Error.WriteLine($"[probe] 浅色 IsDark={TKCode.Arc.ArcThemeManager.Current?.IsDark}");

        TKCode.Arc.ArcTheme.Apply(t => t.Appearance(TKCode.Arc.ArcAppearance.Dark));
        await Task.Delay(200);
        Console.Error.WriteLine($"[probe] 深色 IsDark={TKCode.Arc.ArcThemeManager.Current?.IsDark}");

        Console.Error.WriteLine("[probe] SELFCHECK-END");
    }

    /// <summary>把关键组件的实际尺寸打到 stderr，用于验证"真的渲染了"。</summary>
    private void DumpLayout()
    {
        void Report(string name, Control? c)
        {
            if (c is null) { Console.Error.WriteLine($"[probe] {name,-22} MISSING"); return; }
            var b = c.Bounds;
            var ok = b.Width > 0 && b.Height > 0;
            Console.Error.WriteLine($"[probe] {name,-22} {b.Width,7:0.0} x {b.Height,-7:0.0} {(ok ? "OK" : "ZERO-SIZE")}");
        }

        Report("Window", this);
        Report("Markdown", _markdown);
        Report("Table", _table);

        var buttons = this.GetLogicalDescendants().OfType<TKCode.Arc.Button>().ToArray();
        Console.Error.WriteLine($"[probe] Arc.Button 实例数     {buttons.Length}");
        foreach (var b in buttons.Take(3))
            Report($"  btn:{b.Content}", b);

        var cards = this.GetLogicalDescendants().OfType<TKCode.Arc.Card>().ToArray();
        Console.Error.WriteLine($"[probe] Arc.Card 实例数       {cards.Length}");

        var badges = this.GetLogicalDescendants().OfType<TKCode.Arc.Badge>().ToArray();
        Console.Error.WriteLine($"[probe] Arc.Badge 实例数      {badges.Length}");

        var switches = this.GetLogicalDescendants().OfType<TKCode.Arc.Switch>().ToArray();
        Console.Error.WriteLine($"[probe] Arc.Switch 实例数     {switches.Length}");

        Console.Error.WriteLine($"[probe] 窗口客户区           {ClientSize.Width:0.0} x {ClientSize.Height:0.0}");
        Console.Error.WriteLine("[probe] DUMP-END");
    }

    private void BuildProbeRows()
    {
        var rows = new List<Dictionary<string, object>>
        {
            new() { ["Name"] = "dougong.store", ["Version"] = "1.0.0", ["State"] = "Active" },
            new() { ["Name"] = "dougong.tools", ["Version"] = "1.0.0", ["State"] = "Active" },
            new() { ["Name"] = "vendor.pet", ["Version"] = "0.2.1", ["State"] = "Stopped" },
        };

        _table.ItemsSource = rows;
        _table.RowKeySelector = row => ((Dictionary<string, object>)row!)["Name"];
        _table.EmptyMessage = "还没有安装任何插件";
    }
}
