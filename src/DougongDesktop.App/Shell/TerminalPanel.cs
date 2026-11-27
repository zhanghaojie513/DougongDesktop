using System.Buffers;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DougongDesktop.App.Services;
using TKCode.Arc;
using TKCode.Terminal.Avalonia;
using TKCode.Terminal.Core;
using ArcButton = TKCode.Arc.Button;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 交互式终端面板（P8f F1–F4）：libghostty-vt 状态机 + <see cref="TerminalView"/> + 跨平台 PTY。
/// </summary>
/// <remarks>
/// <b>四段链路全部打通</b>：
/// <list type="bullet">
/// <item><b>F1 VT 解析</b>：<see cref="TerminalCore"/>（libghostty-vt）负责全部转义序列与回滚缓冲。</item>
/// <item><b>F2 渲染</b>：<see cref="TerminalView"/> 绘制 <see cref="FrameSnapshot"/> 与 IME preedit。</item>
/// <item><b>F3 跨平台 PTY</b>：通过 <see cref="IPtySession"/> 适配 macOS/Linux <c>forkpty</c> 与 Windows <c>ConPTY</c>。</item>
/// <item><b>F4 Dock 面板</b>：接入右栏 <see cref="RightDock"/>，随视图尺寸自动同步行列与 <c>SIGWINCH</c>，窗口关闭时回收子进程。</item>
/// </list>
/// <para>
/// <b>线程边界</b>：<see cref="TerminalCore"/> 与 <see cref="InputEncoder"/> 都不是线程安全的，
/// 全部调用严格留在 UI 线程；PTY 后台读线程只把字节块入队并合并派发到 <see cref="Dispatcher.UIThread"/>。
/// </para>
/// </remarks>
public sealed class TerminalPanel : UserControl, IDisposable
{
    private const int DefaultColumns = 80;
    private const int DefaultRows = 24;
    private const int DefaultCellWidth = 8;
    private const int DefaultCellHeight = 16;

    private readonly Func<PtyStartOptions, IPtySession> _ptyFactory;
    private readonly TerminalView _view;
    private readonly TextBlock _status;
    private readonly ArcButton _clearButton;
    private readonly ArcButton _restartButton;
    private readonly object _pendingLock = new();
    private readonly Queue<byte[]> _pendingChunks = new();

    private string? _workingDirectory;
    private TerminalCore? _core;
    private InputEncoder? _encoder;
    private IPtySession? _pty;
    private TerminalSize _size = new(DefaultColumns, DefaultRows, DefaultCellWidth, DefaultCellHeight);
    private bool _drainPending;
    private bool _nativeLoadFailed;
    private bool _disposed;
    private string? _lastTitle;

    public TerminalPanel(
        Func<PtyStartOptions, IPtySession>? ptyFactory = null,
        string? workingDirectory = null)
    {
        _ptyFactory = ptyFactory ?? PtySessionFactory.Start;
        _workingDirectory = workingDirectory;

        _status = new TextBlock
        {
            Text = "终端就绪 · 点击「重启」启动 shell",
            FontSize = 11,
            FontFamily = DshTheme.MonoFont,
            Foreground = DshTheme.Brush(DshTheme.LabelSecondary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _clearButton = new ArcButton
        {
            Content = "清屏",
            Variant = ButtonVariant.Ghost,
            Size = ControlSize.Small,
        };
        _clearButton.Click += (_, _) => ClearScreen();

        _restartButton = new ArcButton
        {
            Content = "重启",
            Variant = ButtonVariant.Ghost,
            Size = ControlSize.Small,
        };
        _restartButton.Click += (_, _) => RestartShell();

        var toolbar = new Border
        {
            Padding = new Thickness(10, 4),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = DshTheme.Brush(DshTheme.BorderL1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8,
                Children =
                {
                    _status,
                    PlaceCol(
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 4,
                            Children = { _clearButton, _restartButton },
                        },
                        1),
                },
            },
        };

        _view = new TerminalView
        {
            FontFamily = DshTheme.MonoFont,
            FontSize = 13,
            Padding = 8,
            Focusable = true,
            CursorBlink = TerminalCursorBlink.Program,
        };

        _view.PointerPressed += (_, _) => _view.Focus();
        _view.KeyPressed += OnTerminalKeyPressed;
        _view.TextEntered += OnTerminalTextEntered;
        _view.PointerChanged += OnTerminalPointerChanged;
        _view.GotFocus += (_, _) => SendFocus(true);
        _view.LostFocus += (_, _) => SendFocus(false);
        _view.SizeChanged += (_, _) => SyncSizeFromView();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                toolbar,
                PlaceRow(_view, 1),
            },
        };
    }

    /// <summary>终端渲染控件（诊断与无头自检用）。</summary>
    public TerminalView View => _view;

    /// <summary>VT 核心是否已创建（原生库是否加载成功）。</summary>
    public bool IsCoreReady => _core is not null;

    /// <summary>当前 PTY 子进程是否正在运行。</summary>
    public bool IsPtyRunning => _pty is { IsRunning: true };

    /// <summary>当前终端网格尺寸。</summary>
    public TerminalSize GridSize => _size;

    /// <summary>当前 PTY 会话实例（诊断用）。</summary>
    public IPtySession? Session => _pty;

    /// <summary>
    /// 当前终端缓冲区的纯文本（自检用）。
    /// </summary>
    public string PlainText => _core?.FormatPlainText() ?? string.Empty;

    /// <summary>
    /// 启动交互式 shell 并连接双向字节流。
    /// </summary>
    /// <remarks>
    /// 若已有运行中的会话，先解除订阅并回收旧进程，再按当前视图尺寸起新会话。
    /// 原生库或 PTY 启动失败时在工具栏如实报告错误，不让应用崩溃。
    /// </remarks>
    public bool StartShell(string? workingDirectory = null, PtyStartOptions? overrideOptions = null)
    {
        if (_disposed) return false;

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            _workingDirectory = workingDirectory;

        if (!EnsureCore()) return false;

        DetachAndDisposePty();
        ClearPendingChunks();

        if (overrideOptions is not null)
        {
            var cw = _size.CellWidth > 0 ? _size.CellWidth : DefaultCellWidth;
            var ch = _size.CellHeight > 0 ? _size.CellHeight : DefaultCellHeight;
            _size = new TerminalSize(
                Math.Max(2, overrideOptions.Columns),
                Math.Max(1, overrideOptions.Rows),
                cw,
                ch);
            _core!.Resize(_size);
        }
        else if (_view.Bounds.Width > 0 && _view.Bounds.Height > 0)
        {
            var measured = _view.GridSizeFor(_view.Bounds.Size);
            if (measured.Columns >= 2 && measured.Rows >= 1)
            {
                _size = measured;
                _core!.Resize(_size);
            }
        }

        var options = overrideOptions ?? new PtyStartOptions(
            WorkingDirectory: _workingDirectory,
            Columns: _size.Columns,
            Rows: _size.Rows,
            PixelWidth: _size.Columns * _size.CellWidth,
            PixelHeight: _size.Rows * _size.CellHeight);

        try
        {
            var pty = _ptyFactory(options);
            _pty = pty;
            pty.OutputReceived += OnPtyOutput;
            pty.Exited += OnPtyExited;

            _lastTitle = null;
            UpdateStatusText();
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = $"终端启动失败：{ex.Message}";
            Console.Error.WriteLine($"[terminal] PTY 启动失败：{ex}");
            return false;
        }
    }

    /// <summary>重启当前 shell（清空屏幕并重新启动会话）。</summary>
    public void RestartShell()
    {
        if (_disposed) return;

        if (_core is not null && _core.IsParserGround)
            _core.Reset();

        StartShell(_workingDirectory);
        _view.Focus();
    }

    /// <summary>清空终端屏幕与回滚（在解析器处于 ground 时直接清，否则发 Ctrl+L）。</summary>
    public void ClearScreen()
    {
        if (_core is null) return;

        if (_core.IsParserGround)
        {
            _core.Clear(ClearTarget.All);
            Refresh();
        }
        else if (_pty is { IsRunning: true })
        {
            SendInput("\x0c"u8);
        }
    }

    /// <summary>
    /// 往 VT 状态机里写一段原始字节并刷新渲染（自检或回放用）。
    /// </summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (_disposed || !EnsureCore()) return;

        _core!.Write(bytes);
        _encoder?.Apply(_core.ReadInputModes());
        Refresh();
    }

    /// <summary>直接往当前 PTY 发送原始字节（用户输入或测试驱动用）。</summary>
    public void SendInput(ReadOnlySpan<byte> bytes)
    {
        if (_disposed || bytes.IsEmpty) return;
        _pty?.Write(bytes);
    }

    /// <summary>通过 <see cref="InputEncoder"/> 编码一次按键并发送到 PTY。</summary>
    public bool EncodeAndSendKey(KeyInput input)
    {
        if (_disposed || !EnsureCore()) return false;

        _encoder!.Apply(_core!.ReadInputModes());
        Span<byte> buf = stackalloc byte[128];
        var written = _encoder.EncodeKey(in input, buf);
        if (written <= 0) return false;

        SendInput(buf[..written]);
        return true;
    }

    /// <summary>同步调整 VT 状态机与 PTY 的网格尺寸（触发子进程 SIGWINCH）。</summary>
    public void ResizeTo(int columns, int rows, int cellWidth = 0, int cellHeight = 0)
    {
        if (_disposed) return;

        var cols = Math.Max(2, columns);
        var r = Math.Max(1, rows);
        var cw = cellWidth > 0 ? cellWidth : (_size.CellWidth > 0 ? _size.CellWidth : DefaultCellWidth);
        var ch = cellHeight > 0 ? cellHeight : (_size.CellHeight > 0 ? _size.CellHeight : DefaultCellHeight);

        if (cols == _size.Columns && r == _size.Rows && cw == _size.CellWidth && ch == _size.CellHeight)
            return;

        _size = new TerminalSize(cols, r, cw, ch);
        _core?.Resize(_size);
        _pty?.Resize(cols, r, cols * cw, r * ch);
        Refresh();
        UpdateStatusText();
    }

    /// <summary>把当前 VT 状态刷成新的一帧。</summary>
    public void Refresh()
    {
        if (_core is null) return;
        _view.Snapshot = _core.CaptureFrame(true);
    }

    /// <summary>
    /// 自检用：灌一段带转义序列的内容，验证 VT 解析与 <see cref="TerminalView"/> 渲染。
    /// </summary>
    public void Probe()
    {
        // ANSI：清屏 → 定位到 1,1 → 绿色 → 打印 → 复位 → 第二行
        const string script =
            "\u001b[2J\u001b[H" +
            "\u001b[32mF1: VT 解析正常\u001b[0m\r\n" +
            "F2: TerminalView 渲染正常\r\n" +
            "F3/F4: PTY 会话已接入（自检模式直接写入字节）\r\n";

        Write(Encoding.UTF8.GetBytes(script));
        if (_pty is null)
            _status.Text = $"VT 自检 · {_size.Columns}×{_size.Rows}";
    }

    /// <summary>
    /// 曾经以为是上游缺陷、实际是我们用法错的那条记录（保留供历史文档与排错参考）。
    /// </summary>
    public static string KnownUpstreamIssue => "（已澄清）曾误判为上游缺陷，实为 TerminalCoreOptions 构造方式错误——必须走带参构造函数";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DetachAndDisposePty();
        ClearPendingChunks();

        _encoder?.Dispose();
        _encoder = null;

        _core?.Dispose();
        _core = null;
    }

    private bool EnsureCore()
    {
        if (_core is not null) return true;
        if (_nativeLoadFailed) return false;

        try
        {
            // **必须用构造函数，不能用对象初始化器**（见 2026-11-26 记录）：
            // TerminalCoreOptions 只有带参构造 (TerminalSize)，对象初始化器会把 Replies 等留为 null。
            var options = new TerminalCoreOptions(_size);
            _core = TerminalCore.Create(in options, new PtyTerminalEffects(this));
            _encoder = new InputEncoder
            {
                OptionAsAlt = OperatingSystem.IsMacOS() ? OptionAsAlt.Both : OptionAsAlt.None,
            };
            _encoder.Apply(_core.ReadInputModes());
            return true;
        }
        catch (Exception ex)
        {
            _nativeLoadFailed = true;
            _status.Text = $"原生终端库加载失败：{ex.Message}";
            Console.Error.WriteLine($"[terminal] 原生库加载失败：{ex}");
            return false;
        }
    }

    private void OnPtyOutput(byte[] chunk)
    {
        if (_disposed || chunk.Length == 0) return;

        bool schedule;
        lock (_pendingLock)
        {
            _pendingChunks.Enqueue(chunk);
            if (_drainPending)
            {
                schedule = false;
            }
            else
            {
                _drainPending = true;
                schedule = true;
            }
        }

        if (schedule)
            Dispatcher.UIThread.Post(DrainPendingOutput, DispatcherPriority.Render);
    }

    private void DrainPendingOutput()
    {
        if (_disposed || _core is null)
        {
            ClearPendingChunks();
            return;
        }

        List<byte[]> batch;
        lock (_pendingLock)
        {
            if (_pendingChunks.Count == 0)
            {
                _drainPending = false;
                return;
            }

            batch = new List<byte[]>(_pendingChunks.Count);
            while (_pendingChunks.Count > 0)
                batch.Add(_pendingChunks.Dequeue());
        }

        foreach (var chunk in batch)
            _core.Write(chunk);

        _encoder?.Apply(_core.ReadInputModes());

        if (_core.CaptureFrame(false) is { } frame)
            _view.Snapshot = frame;

        var title = _core.Title;
        if (!string.Equals(title, _lastTitle, StringComparison.Ordinal))
        {
            _lastTitle = title;
            UpdateStatusText();
        }

        bool more;
        lock (_pendingLock)
        {
            more = _pendingChunks.Count > 0;
            if (!more) _drainPending = false;
        }

        if (more)
            Dispatcher.UIThread.Post(DrainPendingOutput, DispatcherPriority.Render);
    }

    private void OnPtyExited(int exitCode)
    {
        if (_disposed) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            UpdateStatusText(exitedCode: exitCode);
        });
    }

    private void OnTerminalKeyPressed(object? sender, TerminalKeyEventArgs e)
    {
        if (_disposed) return;

        var isMac = OperatingSystem.IsMacOS();
        var mods = e.Modifiers;
        var key = e.Input.Key;

        // 复制：macOS Cmd+C（有选区时） / Win&Linux Ctrl+Shift+C
        var isCopy = isMac
            ? mods == KeyModifiers.Meta && key == TerminalKey.C && _core is { HasSelection: true }
            : mods == (KeyModifiers.Control | KeyModifiers.Shift) && key == TerminalKey.C && _core is { HasSelection: true };

        if (isCopy)
        {
            _ = CopySelectionAsync();
            e.Handled = true;
            return;
        }

        // 粘贴：macOS Cmd+V / Win&Linux Ctrl+Shift+V
        var isPaste = isMac
            ? mods == KeyModifiers.Meta && key == TerminalKey.V
            : mods == (KeyModifiers.Control | KeyModifiers.Shift) && key == TerminalKey.V;

        if (isPaste)
        {
            _ = PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }

        // 清屏：macOS Cmd+K
        if (isMac && mods == KeyModifiers.Meta && key == TerminalKey.K)
        {
            ClearScreen();
            e.Handled = true;
            return;
        }

        if (EncodeAndSendKey(e.Input))
            e.Handled = true;
    }

    private void OnTerminalTextEntered(object? sender, string text)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        SendInput(Encoding.UTF8.GetBytes(text));
    }

    private void OnTerminalPointerChanged(object? sender, TerminalPointerEventArgs e)
    {
        if (_disposed || _core is null) return;

        if (e.Kind == TerminalPointerKind.Wheel && Math.Abs(e.WheelDelta) > double.Epsilon)
        {
            if (_encoder is not null)
            {
                Span<byte> buf = stackalloc byte[64];
                var mouse = e.Mouse;
                var geo = e.Geometry;
                var n = _encoder.EncodeMouse(in mouse, in geo, false, buf);
                if (n > 0)
                {
                    SendInput(buf[..n]);
                    e.Handled = true;
                    return;
                }
            }

            // WheelDelta 正数向上滚 → ViewportScroll.Lines 负数向上回看历史。
            var delta = (long)Math.Round(-e.WheelDelta);
            if (delta == 0) delta = e.WheelDelta > 0 ? -1 : 1;

            _core.ScrollViewport(ViewportScroll.Lines(delta));
            Refresh();
            e.Handled = true;
        }
    }

    private void SendFocus(bool focused)
    {
        if (_disposed || _core is null || _pty is null) return;
        if (!_core.ReadInputModes().FocusEvents) return;

        Span<byte> buf = stackalloc byte[16];
        var n = InputEncoder.EncodeFocus(focused, buf);
        if (n > 0)
            SendInput(buf[..n]);
    }

    private void SyncSizeFromView()
    {
        if (_disposed || _view.Bounds.Width <= 0 || _view.Bounds.Height <= 0) return;

        var next = _view.GridSizeFor(_view.Bounds.Size);
        if (next.Columns < 2 || next.Rows < 1) return;

        ResizeTo(next.Columns, next.Rows, next.CellWidth, next.CellHeight);
    }

    private async Task CopySelectionAsync()
    {
        if (_core is null || !_core.HasSelection) return;
        var text = _core.GetSelectionText(TextFormat.Plain);
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[terminal] 复制选区失败：{ex.Message}");
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        if (_disposed || _core is null || _pty is null) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;

            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text)) return;

            PasteText(text);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[terminal] 读取剪贴板失败：{ex.Message}");
        }
    }

    /// <summary>按终端当前 bracketed paste (mode 2004) 状态编码文本并写入 PTY。</summary>
    public void PasteText(string text)
    {
        if (_disposed || _core is null || _pty is null || string.IsNullOrEmpty(text)) return;

        var writer = new ArrayBufferWriter<byte>(Encoding.UTF8.GetByteCount(text) + 16);
        var outcome = _core.EncodePaste(text, allowUnsafe: false, writer);
        if (outcome == PasteOutcome.RequiresConfirmation)
        {
            writer.Clear();
            outcome = _core.EncodePaste(text, allowUnsafe: true, writer);
        }

        if (outcome == PasteOutcome.Written && writer.WrittenCount > 0)
            SendInput(writer.WrittenSpan);
    }

    private void UpdateStatusText(int? exitedCode = null)
    {
        if (_pty is null)
        {
            _status.Text = "终端就绪 · 点击「重启」启动 shell";
            return;
        }

        var shell = string.IsNullOrWhiteSpace(_pty.ShellName) ? "shell" : _pty.ShellName;
        var code = exitedCode ?? _pty.ExitCode;
        if (code is { } c)
        {
            _status.Text = $"{shell} 已退出（{c}） · 点击「重启」重新连接";
            return;
        }

        var label = string.IsNullOrWhiteSpace(_lastTitle) ? shell : _lastTitle;
        _status.Text = $"{label} · {_size.Columns}×{_size.Rows}";
    }

    private void DetachAndDisposePty()
    {
        var old = _pty;
        _pty = null;
        if (old is null) return;

        old.OutputReceived -= OnPtyOutput;
        old.Exited -= OnPtyExited;
        old.Dispose();
    }

    private void ClearPendingChunks()
    {
        lock (_pendingLock)
        {
            _pendingChunks.Clear();
            _drainPending = false;
        }
    }

    /// <summary>将 libghostty-vt 的查询应答（DA / DSR / XTVERSION 等）写回当前 PTY。</summary>
    private sealed class PtyTerminalEffects(TerminalPanel owner) : ITerminalEffects
    {
        public void WriteToPty(ReadOnlySpan<byte> bytes)
        {
            owner.SendInput(bytes);
        }
    }

    private static Control PlaceRow(Control c, int row)
    {
        Grid.SetRow(c, row);
        return c;
    }

    private static Control PlaceCol(Control c, int col)
    {
        Grid.SetColumn(c, col);
        return c;
    }
}
