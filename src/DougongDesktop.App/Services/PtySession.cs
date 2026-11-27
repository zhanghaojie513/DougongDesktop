using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DougongDesktop.App.Services;

/// <summary>
/// 跨平台伪终端（PTY）会话契约（P8f F3）。
/// </summary>
/// <remarks>
/// <b>为什么把 PTY 放在 <c>Services/</c> 适配层</b>：本仓库的架构规矩是
/// "平台调用留在适配器，UI 控件只面对抽象"（与 <see cref="IEngineLocator"/>、
/// <see cref="ISettingsStore"/> 同一条线）。<c>TerminalPanel</c> 只读写字节流和尺寸，
/// 不直接碰 <c>forkpty</c> 或 <c>ConPTY</c>——也方便在无头测试里注入假 PTY。
/// </remarks>
public interface IPtySession : IDisposable
{
    /// <summary>子进程是否仍在运行。</summary>
    bool IsRunning { get; }

    /// <summary>子进程退出码；运行中为 null。</summary>
    int? ExitCode { get; }

    /// <summary>实际启动的 shell 名称（用于终端工具栏显示）。</summary>
    string ShellName { get; }

    /// <summary>PTY 读出原始字节（在后台读线程触发）。</summary>
    event Action<byte[]>? OutputReceived;

    /// <summary>子进程已退出（在后台线程触发，至多一次）。</summary>
    event Action<int>? Exited;

    /// <summary>往 PTY 写入字节（键盘输入、粘贴、VT 应答）。已退出时静默忽略。</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>通知内核/ConPTY 新的网格尺寸，触发子进程 SIGWINCH。</summary>
    void Resize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0);
}

/// <summary>启动 PTY 的参数。</summary>
public sealed record PtyStartOptions(
    string? WorkingDirectory = null,
    string? Shell = null,
    string[]? Arguments = null,
    int Columns = 80,
    int Rows = 24,
    int PixelWidth = 0,
    int PixelHeight = 0);

/// <summary>按当前操作系统创建真实 PTY 会话。</summary>
public static class PtySessionFactory
{
    public static IPtySession Start(PtyStartOptions? options = null)
    {
        var opts = options ?? new PtyStartOptions();
        return OperatingSystem.IsWindows()
            ? WindowsConPtySession.Start(opts)
            : UnixForkPtySession.Start(opts);
    }
}

/// <summary>
/// macOS / Linux 伪终端实现：基于 POSIX <c>forkpty</c> + <c>ioctl(TIOCSWINSZ)</c>。
/// </summary>
/// <remarks>
/// <b>为什么所有非托管参数必须在 <c>forkpty</c> 之前分配好</b>：.NET 进程是多线程的，
/// <c>fork</c> 后子进程里只剩调用线程，此时任何托管分配或 GC 锁都可能死锁。
/// 所以在父进程提前把 <c>shell</c>、<c>argv</c>、<c>envp</c>、<c>cwd</c> 编码成原始
/// UTF-8 指针，子进程只调用纯非托管的 <c>chdir</c> → <c>execve</c> → <c>_exit</c>，
/// 不发生任何托管分配。
/// <para>
/// <b>Apple Silicon ARM64 的 <c>ioctl</c> 变参 ABI</b>：macOS ARM64 上 C 变参通过栈
/// 传递，不走 <c>x2</c> 寄存器。直接声明 <c>ioctl(int, nuint, ref Winsize)</c> 会把指针
/// 放进 <c>x2</c>，导致内核读栈上垃圾并返回 <c>EFAULT</c>。这里用 6 个寄存器占位参数把
/// <c>ref Winsize</c> 推到栈槽 0，并用无头测试里的 <c>stty size</c> 实测验证。
/// </para>
/// </remarks>
internal sealed class UnixForkPtySession : IPtySession
{
    private readonly int _masterFd;
    private readonly int _pid;
    private readonly object _writeLock = new();
    private readonly object _eventLock = new();
    private readonly Thread _readThread;
    private Action<byte[]>? _outputReceived;
    private List<byte[]>? _earlyOutput;
    private Action<int>? _exited;
    private int _closed;
    private int _exitedSignaled;
    private int? _exitCode;

    public string ShellName { get; }
    public bool IsRunning => Volatile.Read(ref _closed) == 0 && _exitCode is null;
    public int? ExitCode => _exitCode;

    /// <summary>
    /// 订阅时自动回放"启动到订阅间隙"已经到达的首批输出，避免交互式 shell 的首行提示符丢失。
    /// </summary>
    public event Action<byte[]>? OutputReceived
    {
        add
        {
            List<byte[]>? replay = null;
            lock (_eventLock)
            {
                _outputReceived += value;
                if (value is not null && _earlyOutput is { Count: > 0 })
                {
                    replay = _earlyOutput;
                    _earlyOutput = null;
                }
            }

            if (replay is not null && value is not null)
            {
                foreach (var chunk in replay)
                    value(chunk);
            }
        }
        remove
        {
            lock (_eventLock) _outputReceived -= value;
        }
    }

    public event Action<int>? Exited
    {
        add
        {
            int? alreadyExited = null;
            lock (_eventLock)
            {
                _exited += value;
                if (value is not null && _exitCode is { } code && Volatile.Read(ref _exitedSignaled) != 0)
                    alreadyExited = code;
            }

            if (alreadyExited is { } c && value is not null)
                value(c);
        }
        remove
        {
            lock (_eventLock) _exited -= value;
        }
    }

    private UnixForkPtySession(int masterFd, int pid, string shellName)
    {
        _masterFd = masterFd;
        _pid = pid;
        ShellName = shellName;

        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"pty-read-{pid}",
        };
        _readThread.Start();
    }

    public static UnixForkPtySession Start(PtyStartOptions options)
    {
        var shell = ResolveUnixShell(options.Shell);
        var args = options.Arguments is { Length: > 0 }
            ? options.Arguments
            : BuildDefaultUnixArgs(shell);

        var cwd = !string.IsNullOrWhiteSpace(options.WorkingDirectory) && Directory.Exists(options.WorkingDirectory)
            ? options.WorkingDirectory
            : Environment.CurrentDirectory;

        var cols = (ushort)Math.Clamp(options.Columns, 2, ushort.MaxValue);
        var rows = (ushort)Math.Clamp(options.Rows, 1, ushort.MaxValue);
        var ws = new Winsize
        {
            ws_col = cols,
            ws_row = rows,
            ws_xpixel = (ushort)Math.Clamp(options.PixelWidth, 0, ushort.MaxValue),
            ws_ypixel = (ushort)Math.Clamp(options.PixelHeight, 0, ushort.MaxValue),
        };

        var envStrings = BuildUnixEnvironment();
        var argvStrings = new string[args.Length + 1];
        argvStrings[0] = shell;
        Array.Copy(args, 0, argvStrings, 1, args.Length);

        var allocations = new List<IntPtr>();
        try
        {
            var isMac = OperatingSystem.IsMacOS();
            EnsureNativePrelinked(isMac);

            var shellPtr = AllocUtf8(shell, allocations);
            var cwdPtr = AllocUtf8(cwd, allocations);
            var argvPtr = AllocStringArray(argvStrings, allocations);
            var envpPtr = AllocStringArray(envStrings, allocations);

            var pid = isMac
                ? MacNative.ForkPty(out var masterFd, IntPtr.Zero, IntPtr.Zero, ref ws)
                : ForkPtyLinuxWithFallback(out masterFd, ref ws);

            if (pid < 0)
            {
                var err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"forkpty 失败（errno={err}）");
            }

            if (pid == 0)
            {
                // 子进程：严禁任何托管分配或锁，只走已预链接的非托管系统调用。
                if (isMac)
                {
                    _ = MacNative.Chdir(cwdPtr);
                    _ = MacNative.Execve(shellPtr, argvPtr, envpPtr);
                    MacNative.Exit(127);
                }
                else
                {
                    _ = LinuxNative.Chdir(cwdPtr);
                    _ = LinuxNative.Execve(shellPtr, argvPtr, envpPtr);
                    LinuxNative.Exit(127);
                }
            }

            // 父进程：给 masterFd 设 FD_CLOEXEC，防止后续子进程继承它导致读端永不 EOF。
            SetCloseOnExec(masterFd);

            return new UnixForkPtySession(masterFd, pid, Path.GetFileName(shell));
        }
        finally
        {
            foreach (var ptr in allocations)
                Marshal.FreeCoTaskMem(ptr);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || Volatile.Read(ref _closed) != 0) return;

        var copy = data.ToArray();
        lock (_writeLock)
        {
            if (Volatile.Read(ref _closed) != 0) return;

            var offset = 0;
            while (offset < copy.Length)
            {
                var written = OperatingSystem.IsMacOS()
                    ? MacNative.Write(_masterFd, ref copy[offset], (nuint)(copy.Length - offset))
                    : LinuxNative.Write(_masterFd, ref copy[offset], (nuint)(copy.Length - offset));

                if (written > 0)
                {
                    offset += (int)written;
                    continue;
                }

                var err = Marshal.GetLastPInvokeError();
                if (err == Eintr) continue;
                break;
            }
        }
    }

    public void Resize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
    {
        if (Volatile.Read(ref _closed) != 0) return;

        var ws = new Winsize
        {
            ws_col = (ushort)Math.Clamp(columns, 2, ushort.MaxValue),
            ws_row = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
            ws_xpixel = (ushort)Math.Clamp(pixelWidth, 0, ushort.MaxValue),
            ws_ypixel = (ushort)Math.Clamp(pixelHeight, 0, ushort.MaxValue),
        };

        lock (_writeLock)
        {
            if (Volatile.Read(ref _closed) != 0) return;

            if (OperatingSystem.IsMacOS())
            {
                const nuint TiocswinszMac = 0x80087467;
                if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                    _ = MacNative.IoctlWinsizeArm64(_masterFd, TiocswinszMac, 0, 0, 0, 0, 0, 0, ref ws);
                else
                    _ = MacNative.IoctlWinsize(_masterFd, TiocswinszMac, ref ws);
            }
            else
            {
                const nuint TiocswinszLinux = 0x5414;
                _ = LinuxNative.IoctlWinsize(_masterFd, TiocswinszLinux, ref ws);
            }
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (Volatile.Read(ref _closed) == 0)
            {
                var n = OperatingSystem.IsMacOS()
                    ? MacNative.Read(_masterFd, buffer, (nuint)buffer.Length)
                    : LinuxNative.Read(_masterFd, buffer, (nuint)buffer.Length);

                if (n > 0)
                {
                    var chunk = new byte[n];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, (int)n);
                    EmitOutput(chunk);
                    continue;
                }

                if (n == 0) break;

                var err = Marshal.GetLastPInvokeError();
                if (err == Eintr) continue;
                // Linux/macOS 在从端关闭后常返回 EIO，属于正常 EOF。
                break;
            }
        }
        finally
        {
            var code = ReapChild(wait: true);
            SignalExited(code);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        // 先给子进程组发 SIGHUP，让交互式 shell 优雅退出。
        SendSignal(-_pid, Sighup);

        lock (_writeLock)
        {
            if (OperatingSystem.IsMacOS()) _ = MacNative.Close(_masterFd);
            else _ = LinuxNative.Close(_masterFd);
        }

        // 等一小段时间让子进程收尾；不退则 SIGKILL，避免残留孤儿进程。
        var reaped = false;
        for (var i = 0; i < 10; i++)
        {
            if (TryReapNoHang(out var code))
            {
                SignalExited(code);
                reaped = true;
                break;
            }
            Thread.Sleep(15);
        }

        if (!reaped)
        {
            SendSignal(-_pid, Sigkill);
            SendSignal(_pid, Sigkill);
            var code = ReapChild(wait: true);
            SignalExited(code);
        }
    }

    private void EmitOutput(byte[] chunk)
    {
        Action<byte[]>? handler;
        lock (_eventLock)
        {
            handler = _outputReceived;
            if (handler is null)
            {
                (_earlyOutput ??= []).Add(chunk);
                return;
            }
        }

        handler(chunk);
    }

    private void SignalExited(int code)
    {
        _exitCode = code;
        if (Interlocked.Exchange(ref _exitedSignaled, 1) != 0) return;

        Action<int>? handler;
        lock (_eventLock) handler = _exited;
        handler?.Invoke(code);
    }

    private bool TryReapNoHang(out int exitCode)
    {
        const int Wnohang = 1;
        var rc = OperatingSystem.IsMacOS()
            ? MacNative.Waitpid(_pid, out var status, Wnohang)
            : LinuxNative.Waitpid(_pid, out status, Wnohang);

        if (rc == _pid)
        {
            exitCode = DecodeWaitStatus(status);
            return true;
        }

        exitCode = 0;
        return rc < 0;
    }

    private int ReapChild(bool wait)
    {
        var flags = wait ? 0 : 1;
        while (true)
        {
            var rc = OperatingSystem.IsMacOS()
                ? MacNative.Waitpid(_pid, out var status, flags)
                : LinuxNative.Waitpid(_pid, out status, flags);

            if (rc == _pid) return DecodeWaitStatus(status);
            if (rc < 0 && Marshal.GetLastPInvokeError() == Eintr) continue;
            return _exitCode ?? 0;
        }
    }

    private static int DecodeWaitStatus(int status)
    {
        // WIFEXITED: (status & 0x7f) == 0 → WEXITSTATUS: (status >> 8) & 0xff
        if ((status & 0x7f) == 0) return (status >> 8) & 0xff;
        // 被信号终止：128 + signal
        return 128 + (status & 0x7f);
    }

    private static void SendSignal(int pid, int sig)
    {
        if (OperatingSystem.IsMacOS()) _ = MacNative.Kill(pid, sig);
        else _ = LinuxNative.Kill(pid, sig);
    }

    private static void SetCloseOnExec(int fd)
    {
        const int FSetfd = 2;
        const int FdCloexec = 1;
        if (OperatingSystem.IsMacOS()) _ = MacNative.Fcntl(fd, FSetfd, FdCloexec);
        else _ = LinuxNative.Fcntl(fd, FSetfd, FdCloexec);
    }

    private static int _prelinked;

    private static void EnsureNativePrelinked(bool isMac)
    {
        if (Interlocked.Exchange(ref _prelinked, 1) != 0) return;

        // 在 forkpty 之前把子进程会调用的 P/Invoke 桩提前解析好，
        // 避免多线程进程 fork 后子进程首次解析原生符号时撞上运行时锁。
        if (isMac)
            Marshal.PrelinkAll(typeof(MacNative));
        else
            Marshal.PrelinkAll(typeof(LinuxNative));
    }

    private static int ForkPtyLinuxWithFallback(out int masterFd, ref Winsize ws)
    {
        try
        {
            return LinuxNative.ForkPty(out masterFd, IntPtr.Zero, IntPtr.Zero, ref ws);
        }
        catch (EntryPointNotFoundException)
        {
            return LinuxUtilNative.ForkPty(out masterFd, IntPtr.Zero, IntPtr.Zero, ref ws);
        }
    }

    private static string ResolveUnixShell(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && File.Exists(requested))
            return requested;

        var envShell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(envShell) && File.Exists(envShell))
            return envShell;

        foreach (var candidate in new[] { "/bin/zsh", "/bin/bash", "/bin/sh" })
            if (File.Exists(candidate))
                return candidate;

        return "/bin/sh";
    }

    private static string[] BuildDefaultUnixArgs(string shell)
    {
        var name = Path.GetFileName(shell);
        // 交互式登录 shell，让用户看到熟悉的提示符和 PATH。
        return name is "zsh" or "bash" or "fish" ? ["-i"] : [];
    }

    private static string[] BuildUnixEnvironment()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
                map[k] = v;
        }

        map["TERM"] = "xterm-256color";
        map["COLORTERM"] = "truecolor";
        if (!map.ContainsKey("LANG"))
            map["LANG"] = "en_US.UTF-8";

        var list = new string[map.Count];
        var i = 0;
        foreach (var (k, v) in map)
            list[i++] = $"{k}={v}";
        return list;
    }

    private static IntPtr AllocUtf8(string s, List<IntPtr> allocations)
    {
        var ptr = Marshal.StringToCoTaskMemUTF8(s);
        allocations.Add(ptr);
        return ptr;
    }

    private static IntPtr AllocStringArray(string[] items, List<IntPtr> allocations)
    {
        var arraySize = (items.Length + 1) * IntPtr.Size;
        var arrayPtr = Marshal.AllocCoTaskMem(arraySize);
        allocations.Add(arrayPtr);

        for (var i = 0; i < items.Length; i++)
        {
            var strPtr = AllocUtf8(items[i], allocations);
            Marshal.WriteIntPtr(arrayPtr, i * IntPtr.Size, strPtr);
        }

        Marshal.WriteIntPtr(arrayPtr, items.Length * IntPtr.Size, IntPtr.Zero);
        return arrayPtr;
    }

    private const int Eintr = 4;
    private const int Sighup = 1;
    private const int Sigkill = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    // ── macOS (libSystem.dylib) ──

    private static class MacNative
    {
        [DllImport("libSystem.dylib", EntryPoint = "forkpty", SetLastError = true)]
        public static extern int ForkPty(out int amaster, IntPtr name, IntPtr termp, ref Winsize winp);

        [DllImport("libSystem.dylib", EntryPoint = "chdir")]
        public static extern int Chdir(IntPtr path);

        [DllImport("libSystem.dylib", EntryPoint = "execve")]
        public static extern int Execve(IntPtr path, IntPtr argv, IntPtr envp);

        [DllImport("libSystem.dylib", EntryPoint = "_exit")]
        public static extern void Exit(int status);

        [DllImport("libSystem.dylib", EntryPoint = "read", SetLastError = true)]
        public static extern nint Read(int fd, [Out] byte[] buf, nuint count);

        [DllImport("libSystem.dylib", EntryPoint = "write", SetLastError = true)]
        public static extern nint Write(int fd, ref byte buf, nuint count);

        [DllImport("libSystem.dylib", EntryPoint = "close")]
        public static extern int Close(int fd);

        [DllImport("libSystem.dylib", EntryPoint = "kill")]
        public static extern int Kill(int pid, int sig);

        [DllImport("libSystem.dylib", EntryPoint = "waitpid", SetLastError = true)]
        public static extern int Waitpid(int pid, out int status, int options);

        [DllImport("libSystem.dylib", EntryPoint = "fcntl")]
        public static extern int Fcntl(int fd, int cmd, int arg);

        [DllImport("libSystem.dylib", EntryPoint = "ioctl", SetLastError = true)]
        public static extern int IoctlWinsize(int fd, nuint request, ref Winsize ws);

        [DllImport("libSystem.dylib", EntryPoint = "ioctl", SetLastError = true)]
        public static extern int IoctlWinsizeArm64(
            int fd, nuint request,
            nint r2, nint r3, nint r4, nint r5, nint r6, nint r7,
            ref Winsize ws);
    }

    // ── Linux (glibc >= 2.34 / musl 在 libc，旧版在 libutil.so.1) ──

    private static class LinuxNative
    {
        [DllImport("libc", EntryPoint = "forkpty", SetLastError = true)]
        public static extern int ForkPty(out int amaster, IntPtr name, IntPtr termp, ref Winsize winp);

        [DllImport("libc", EntryPoint = "chdir")]
        public static extern int Chdir(IntPtr path);

        [DllImport("libc", EntryPoint = "execve")]
        public static extern int Execve(IntPtr path, IntPtr argv, IntPtr envp);

        [DllImport("libc", EntryPoint = "_exit")]
        public static extern void Exit(int status);

        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        public static extern nint Read(int fd, [Out] byte[] buf, nuint count);

        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        public static extern nint Write(int fd, ref byte buf, nuint count);

        [DllImport("libc", EntryPoint = "close")]
        public static extern int Close(int fd);

        [DllImport("libc", EntryPoint = "kill")]
        public static extern int Kill(int pid, int sig);

        [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
        public static extern int Waitpid(int pid, out int status, int options);

        [DllImport("libc", EntryPoint = "fcntl")]
        public static extern int Fcntl(int fd, int cmd, int arg);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        public static extern int IoctlWinsize(int fd, nuint request, ref Winsize ws);
    }

    private static class LinuxUtilNative
    {
        [DllImport("libutil.so.1", EntryPoint = "forkpty", SetLastError = true)]
        public static extern int ForkPty(out int amaster, IntPtr name, IntPtr termp, ref Winsize winp);
    }
}

/// <summary>
/// Windows 伪终端实现：基于系统原生 ConPTY（<c>CreatePseudoConsole</c>）。
/// </summary>
internal sealed class WindowsConPtySession : IPtySession
{
    private readonly IntPtr _pseudoConsole;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _threadHandle;
    private readonly SafeFileHandle _inputWriteHandle;
    private readonly SafeFileHandle _outputReadHandle;
    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    private readonly object _writeLock = new();
    private readonly object _eventLock = new();
    private readonly Thread _readThread;
    private Action<byte[]>? _outputReceived;
    private List<byte[]>? _earlyOutput;
    private Action<int>? _exited;
    private int _closed;
    private int _exitedSignaled;
    private int? _exitCode;

    public string ShellName { get; }
    public bool IsRunning => Volatile.Read(ref _closed) == 0 && _exitCode is null;
    public int? ExitCode => _exitCode;

    public event Action<byte[]>? OutputReceived
    {
        add
        {
            List<byte[]>? replay = null;
            lock (_eventLock)
            {
                _outputReceived += value;
                if (value is not null && _earlyOutput is { Count: > 0 })
                {
                    replay = _earlyOutput;
                    _earlyOutput = null;
                }
            }

            if (replay is not null && value is not null)
            {
                foreach (var chunk in replay)
                    value(chunk);
            }
        }
        remove
        {
            lock (_eventLock) _outputReceived -= value;
        }
    }

    public event Action<int>? Exited
    {
        add
        {
            int? alreadyExited = null;
            lock (_eventLock)
            {
                _exited += value;
                if (value is not null && _exitCode is { } code && Volatile.Read(ref _exitedSignaled) != 0)
                    alreadyExited = code;
            }

            if (alreadyExited is { } c && value is not null)
                value(c);
        }
        remove
        {
            lock (_eventLock) _exited -= value;
        }
    }

    private WindowsConPtySession(
        IntPtr pseudoConsole,
        IntPtr processHandle,
        IntPtr threadHandle,
        SafeFileHandle inputWriteHandle,
        SafeFileHandle outputReadHandle,
        string shellName)
    {
        _pseudoConsole = pseudoConsole;
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        _inputWriteHandle = inputWriteHandle;
        _outputReadHandle = outputReadHandle;
        _inputStream = new FileStream(inputWriteHandle, FileAccess.Write, 4096, isAsync: false);
        _outputStream = new FileStream(outputReadHandle, FileAccess.Read, 4096, isAsync: false);
        ShellName = shellName;

        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "pty-read-conpty",
        };
        _readThread.Start();
    }

    public static WindowsConPtySession Start(PtyStartOptions options)
    {
        var shell = ResolveWindowsShell(options.Shell);
        var commandLine = BuildWindowsCommandLine(shell, options.Arguments);
        var cwd = !string.IsNullOrWhiteSpace(options.WorkingDirectory) && Directory.Exists(options.WorkingDirectory)
            ? options.WorkingDirectory
            : Environment.CurrentDirectory;

        var sec = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = 1,
        };

        if (!CreatePipe(out var inputRead, out var inputWrite, ref sec, 0))
            throw new InvalidOperationException($"CreatePipe(in) 失败（error={Marshal.GetLastPInvokeError()}）");

        if (!CreatePipe(out var outputRead, out var outputWrite, ref sec, 0))
        {
            CloseHandle(inputRead);
            CloseHandle(inputWrite);
            throw new InvalidOperationException($"CreatePipe(out) 失败（error={Marshal.GetLastPInvokeError()}）");
        }

        var size = new Coord
        {
            X = (short)Math.Clamp(options.Columns, 2, short.MaxValue),
            Y = (short)Math.Clamp(options.Rows, 1, short.MaxValue),
        };

        var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out var hPc);
        // ConPTY 已复制两端句柄，父进程立刻关掉它不用的那两头。
        CloseHandle(inputRead);
        CloseHandle(outputWrite);

        if (hr != 0)
        {
            CloseHandle(inputWrite);
            CloseHandle(outputRead);
            throw new InvalidOperationException($"CreatePseudoConsole 失败（hr=0x{hr:X8}）");
        }

        var attrList = IntPtr.Zero;
        try
        {
            var lpSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
            attrList = Marshal.AllocHGlobal(lpSize);

            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref lpSize))
                throw new InvalidOperationException($"InitializeProcThreadAttributeList 失败（error={Marshal.GetLastPInvokeError()}）");

            if (!UpdateProcThreadAttribute(
                    attrList, 0, (IntPtr)ProcThreadAttributePseudoConsole,
                    hPc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException($"UpdateProcThreadAttribute 失败（error={Marshal.GetLastPInvokeError()}）");
            }

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() },
                lpAttributeList = attrList,
            };

            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: ExtendedStartupInfoPresent,
                    lpEnvironment: IntPtr.Zero,
                    lpCurrentDirectory: cwd,
                    ref startupInfo,
                    out var procInfo))
            {
                throw new InvalidOperationException($"CreateProcessW 失败（error={Marshal.GetLastPInvokeError()}）");
            }

            return new WindowsConPtySession(
                hPc,
                procInfo.hProcess,
                procInfo.hThread,
                new SafeFileHandle(inputWrite, ownsHandle: true),
                new SafeFileHandle(outputRead, ownsHandle: true),
                Path.GetFileName(shell));
        }
        catch
        {
            ClosePseudoConsole(hPc);
            CloseHandle(inputWrite);
            CloseHandle(outputRead);
            throw;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || Volatile.Read(ref _closed) != 0) return;

        var copy = data.ToArray();
        lock (_writeLock)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            try
            {
                _inputStream.Write(copy, 0, copy.Length);
                _inputStream.Flush();
            }
            catch
            {
                // 管道断开时忽略写入错误，由读线程统一回报退出。
            }
        }
    }

    public void Resize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
    {
        if (Volatile.Read(ref _closed) != 0) return;

        var size = new Coord
        {
            X = (short)Math.Clamp(columns, 2, short.MaxValue),
            Y = (short)Math.Clamp(rows, 1, short.MaxValue),
        };

        lock (_writeLock)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            _ = ResizePseudoConsole(_pseudoConsole, size);
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (Volatile.Read(ref _closed) == 0)
            {
                var n = _outputStream.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;

                var chunk = new byte[n];
                Buffer.BlockCopy(buffer, 0, chunk, 0, n);
                EmitOutput(chunk);
            }
        }
        catch
        {
            // 关闭管道导致的 IOException 属正常退出路径。
        }
        finally
        {
            _ = WaitForSingleObject(_processHandle, 1000);
            var code = GetExitCodeProcess(_processHandle, out var exit) ? (int)exit : 0;
            SignalExited(code);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        lock (_writeLock)
        {
            ClosePseudoConsole(_pseudoConsole);
            _inputStream.Dispose();
            _outputStream.Dispose();
            _inputWriteHandle.Dispose();
            _outputReadHandle.Dispose();
        }

        if (WaitForSingleObject(_processHandle, 200) != 0)
            _ = TerminateProcess(_processHandle, 1);

        var code = GetExitCodeProcess(_processHandle, out var exit) ? (int)exit : 0;
        CloseHandle(_threadHandle);
        CloseHandle(_processHandle);
        SignalExited(code);
    }

    private void EmitOutput(byte[] chunk)
    {
        Action<byte[]>? handler;
        lock (_eventLock)
        {
            handler = _outputReceived;
            if (handler is null)
            {
                (_earlyOutput ??= []).Add(chunk);
                return;
            }
        }

        handler(chunk);
    }

    private void SignalExited(int code)
    {
        _exitCode = code;
        if (Interlocked.Exchange(ref _exitedSignaled, 1) != 0) return;

        Action<int>? handler;
        lock (_eventLock) handler = _exited;
        handler?.Invoke(code);
    }

    private static string ResolveWindowsShell(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var pwsh = Path.Combine(dir.Trim(), "pwsh.exe");
            if (File.Exists(pwsh)) return pwsh;
        }

        return Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
    }

    private static string BuildWindowsCommandLine(string shell, string[]? args)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(shell).Append('"');
        if (args is { Length: > 0 })
        {
            foreach (var arg in args)
                sb.Append(' ').Append(arg.Contains(' ') ? $"\"{arg}\"" : arg);
        }
        return sb.ToString();
    }

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SecurityAttributes lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
