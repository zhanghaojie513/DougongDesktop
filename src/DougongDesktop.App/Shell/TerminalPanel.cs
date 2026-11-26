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
            // VT 状态机现在能建起来了（2026-11-26 修的那个 options 构造问题），
            // 但 PTY 还没接——所以能显示内容、打不了字。
            // 如实说明缺的是哪一半，不给一个看起来完整、实际不可交互的假终端。
            Text = "VT 解析与渲染已可用（清屏/定位/着色/换行都按终端协议执行），"
                 + "但 PTY 尚未接入：这里的按键还送不到 shell。",
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
    /// 当前终端缓冲区的纯文本（自检用）。
    /// </summary>
    /// <remarks>
    /// <b>为什么用它而不是看快照</b>：<c>FrameSnapshot</c> 的格子是打包的
    /// <c>UInt64</c> 码点 + 字形字典，自己解码等于重写一遍渲染层。
    /// <c>TerminalCore.FormatPlainText()</c> 是官方给纯文本的入口，
    /// 用它验"转义序列真的被解析了"最直接——清屏后只剩我们写入的那两行。
    /// </remarks>
    public string PlainText => _core?.FormatPlainText() ?? string.Empty;

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
        // **必须用构造函数，不能用对象初始化器。**
        //
        // `TerminalCoreOptions` 只有一个带参构造函数 `(TerminalSize Size)`，
        // 那些"看起来可以初始化"的属性（ScrollbackMaxLines / Replies /
        // TerminfoName …）的默认值**只在构造函数里赋值**。
        // 走 `new TerminalCoreOptions { Size = ... }` 时 C# 会合成
        // `(TerminalSize)default` 的调用，于是只有 Size 被覆盖，
        // 其余字段全停在零值：
        //
        //   Replies = null          ← 原生回调解引用它 → NullReferenceException
        //   TerminfoName = null
        //   ScrollbackMaxLines = null
        //
        // CellWidth/Height 给典型等宽字号的度量；PTY 接上后要按实际字体重算。
        var options = new TerminalCoreOptions(
            new TerminalSize(Columns: 80, Rows: 24, CellWidth: 8, CellHeight: 16));

        // Create 的第一个参数是 in（只读引用），不是值传递。
        return TerminalCore.Create(in options, new NoopEffects());
    }

    /// <summary>
    /// 曾经以为是上游缺陷、实际是我们用法错的那条记录。
    /// </summary>
    /// <remarks>
    /// <b>症状</b>：<c>TerminalCore.Create</c> 抛 <c>NullReferenceException</c>，
    /// 栈顶在 <c>TerminalCore..ctor(IntPtr terminal, ...)</c>——终端指针为 null。
    /// <para>
    /// <b>我当时的误判</b>（2026-11-06 起，连错四个版本 0.4.0 → 1.3.2）：
    /// 用独立控制台工程复现后认定是上游缺陷。逐一排除过原生库缺失、
    /// DllImport 名字、<c>in</c> 传参、库损坏（<c>ctypes</c> 能加载，
    /// <c>GhosttyRuntime.Version</c> 返回 <c>0.1.0-dev</c>），
    /// 但**没有逐字段对比两种构造方式的产物**。
    /// </para>
    /// <para>
    /// <b>真正的原因</b>：<c>TerminalCoreOptions</c> 只有一个带参构造函数
    /// <c>(TerminalSize Size)</c>，其余属性的默认值（<c>Replies</c>、
    /// <c>TerminfoName</c>、<c>ScrollbackMaxLines</c>、<c>ScrollbackMaxBytes</c>、
    /// <c>ContinuationMaxBytes</c>、<c>DefaultCursorStyle</c>）**只在构造函数里赋值**。
    /// 写 <c>new TerminalCoreOptions { Size = ... }</c> 时 C# 合成的是
    /// <c>(TerminalSize)default</c> 调用，于是这些字段全是 null/0，
    /// 原生回调解引用 <c>Replies</c> 就崩。
    /// </para>
    /// <para>
    /// <b>教训</b>：复现"只加包就能复现"只能证明问题不在我的集成代码里，
    /// **不能证明库有缺陷**——同一份复现代码本身可能就是我写错的用法。
    /// 该做的是把两种写法并排跑一次（五分钟），而不是连续四个版本只验"仍然失败"。
    /// </para>
    /// </remarks>
    public static string KnownUpstreamIssue => "（已澄清）曾误判为上游缺陷，实为 TerminalCoreOptions 构造方式错误——见本成员注释";

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
