using Avalonia;
using Avalonia.Controls;
using TKCode.Terminal.Avalonia;
using TKCode.Terminal.Core;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 终端面板（P8f F1/F2）：VT 解析 + 渲染，**不含 PTY**。
/// </summary>
/// <remarks>
/// <b>本轮做的是 F1 + F2，不是 F3</b>：<see cref="TerminalCore"/> 负责 VT 状态机，
/// <see cref="TerminalView"/> 负责把 <c>FrameSnapshot</c> 画出来。PTY（真正的
/// shell 进程、双向字节流）是 F3，还没接。
/// <para>
/// <b>所以面板里如实说明"未连接 shell"</b>，不用假数据填满屏幕。
/// 但 VT 解析本身是真的——转义序列、清屏、光标移动都由 libghostty-vt 处理，
/// 这部分可以直接验证（见 <see cref="Probe"/>）。
/// </para>
/// <para>
/// <b>为什么要这个中间层</b>：<c>TerminalView</c> 只吃 <c>Snapshot</c>，而
/// <c>FrameCapturer</c> 是 <c>internal</c>（外部拿不到）。public 的入口是
/// <c>TerminalCore.CaptureFrame(bool)</c>，它内部会建 capturer——
/// 这是唯一能把"字节"变成"能画的帧"的合法路径。
/// </para>
/// </remarks>
public sealed class TerminalPanel : UserControl
{
    private readonly TerminalView _view = new();
    private readonly TextBlock _status;

    /// <summary>VT 状态机。点 <see cref="Probe"/> 时惰性创建。</summary>
    /// <remarks>
    /// 惰性是因为 <see cref="TerminalCore"/> 会加载原生库（libghostty-vt）。
    /// 用户不点终端 tab 就不该付这个成本——库加载失败时也只影响这一个面板。
    /// </remarks>
    private TerminalCore? _core;

    private bool _nativeLoadFailed;

    public TerminalPanel()
    {
        _status = new TextBlock
        {
            // 说实话：终端渲染控件已接，但 VT 状态机现在建不起来（上游缺陷），
            // 而且 PTY 也还没接。不给一个看起来能用、打不了字的假终端。
            Text = "终端暂不可用：VT 状态机无法创建（上游缺陷），PTY 也尚未接入。\n"
                 + "已接入 TerminalView 渲染控件与其原生库，但 TerminalCore.Create 抛异常。",
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(12, 8),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                _status,
                Place(_view, 1),
            },
        };
    }

    /// <summary>终端渲染控件。诊断用。</summary>
    public TerminalView View => _view;

    /// <summary>VT 核心是否已创建（原生库是否加载成功）。</summary>
    public bool IsCoreReady => _core is not null;

    /// <summary>
    /// 往 VT 状态机里写一段字节并刷新渲染。
    /// </summary>
    /// <param name="bytes">要写入的原始字节（终端协议本身就是字节流）。</param>
    /// <remarks>
    /// <b>用字节而不是字符串</b>：<c>TerminalCore.Write</c> 的语义是"PTY 来的原始字节"，
    /// 控制序列里的长度前缀是按字节算的。传字符串会先过一次编码，
    /// 多字节字符的边界就被改写了。
    /// </remarks>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (_nativeLoadFailed) return;

        try
        {
            _core ??= CreateCore();
        }
        catch (Exception ex)
        {
            // 原生库加载失败不能让整个应用起不来——与 UI 插件失败同一条原则。
            _nativeLoadFailed = true;
            _status.Text = $"原生终端库加载失败：{ex.Message}";

            // 界面上只显示一句，控制台打全栈——排查原生库问题必须看栈。
            Console.Error.WriteLine($"[terminal] 原生库加载失败：{ex}");
            return;
        }

        _core.Write(bytes);
        Refresh();
    }

    /// <summary>把当前 VT 状态刷成新的一帧。</summary>
    public void Refresh()
    {
        if (_core is null) return;
        _view.Snapshot = _core.CaptureFrame(true);
    }

    /// <summary>
    /// 自检用：灌一段带转义序列的内容，验证 VT 真的在解析。
    /// </summary>
    /// <remarks>
    /// <b>这是 F1 的判据</b>：光看"控件在、尺寸非零"证明不了 VT 工作——
    /// 一个只画空白格的控件也能满足那些条件。所以要喂**能被解析改变状态**的序列：
    /// 清屏、定位、着色，然后看快照里的文字是否真的按预期出现。
    /// </remarks>
    public void Probe()
    {
        // ANSI：清屏 → 定位到 1,1 → 绿色 → 打印 → 复位 → 第二行
        const string script =
            "\u001b[2J\u001b[H" +
            "\u001b[32mF1: VT 解析正常\u001b[0m\r\n" +
            "F2: TerminalView 渲染正常\r\n" +
            "（PTY 未接入，这里是直接写入的字节）\r\n";

        Write(System.Text.Encoding.UTF8.GetBytes(script));
    }

    private static TerminalCore CreateCore()
    {
        // 尺寸按面板初始值给，后续 resize 由 TerminalView 自己的度量决定。
        var options = new TerminalCoreOptions
        {
            // CellWidth/Height 给典型等宽字号的度量；PTY 接上后要按实际字体重算。
            Size = new TerminalSize(Columns: 80, Rows: 24, CellWidth: 8, CellHeight: 16),
        };

        // Create 的第一个参数是 in（只读引用），不是值传递。
        return TerminalCore.Create(in options, new NoopEffects());
    }

    /// <summary>
    /// 上游缺陷的最小复现（2026-11-06 实测，`TKCode.Arc.Terminal 0.4.0-preview.1`）。
    /// </summary>
    /// <remarks>
    /// <b>症状</b>：<c>TerminalCore.Create</c> 必然抛
    /// <c>NullReferenceException</c>，栈顶在
    /// <c>TerminalCore..ctor(IntPtr terminal, ...)</c>——即拿到的 terminal 指针是 null。
    /// <para>
    /// <b>已排除的</b>（都验过，不是这些原因）：
    /// <list type="bullet">
    /// <item>原生库没落地：<c>runtimes/osx-arm64/native/libghostty-vt.dylib</c> 在输出目录里，
    /// <c>deps.json</c> 的 runtimeTargets 也有它。</item>
    /// <item>库加载不了：用 NuGet 正确引用时 <c>GhosttyRuntime.Version</c> 返回
    /// <c>0.1.0-dev</c>，说明 dlopen 成功、版本握手通了。</item>
    /// <item>我们的调用方式：改用 <c>in</c> 传参、换 <c>TerminalSize</c> 的四个参数都试过。</item>
    /// <item><c>libghostty-vt.dylib</c> 本身：直接用 <c>ctypes.CDLL</c> 能加载，
    /// <c>ghostty_build_info</c> / <c>ghostty_terminal_new</c> 符号都在。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>复现</b>：新建控制台工程，只加
    /// <c>&lt;PackageReference Include="TKCode.Arc.Terminal" Version="0.4.0-preview.1" /&gt;</c>，
    /// 三行就能复现——与桌面端的集成代码无关。
    /// </para>
    /// <para>
    /// <b>所以 F1/F2 不能标完成</b>：控件在、库在、版本号能读，但"字节 → 帧"这一步
    /// 走不通。按本项目的规矩，跑不通就不算接上。等上游修，
    /// 或在 <c>ghostty_terminal_new</c> 上自己封一层。
    /// </para>
    /// </remarks>
    public static string KnownUpstreamIssue =>
        "TerminalCore.Create 抛 NullReferenceException（终端指针为 null），"
        + "原生库与版本握手均正常，疑为 TKCode.Arc.Terminal 0.4.0-preview.1 的上游缺陷";

    /// <summary>不处理任何终端副作用（本轮没有 PTY 可写回）。</summary>
    private sealed class NoopEffects : ITerminalEffects
    {
        public void WriteToPty(ReadOnlySpan<byte> bytes)
        {
            // 没有 PTY：用户的按键输入无处可去。这正是"未连接 shell"的表现。
        }
    }

    private static Control Place(Control c, int row)
    {
        Grid.SetRow(c, row);
        return c;
    }
}
