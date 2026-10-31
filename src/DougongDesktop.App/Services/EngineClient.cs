using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using DougongDesktop.Ipc;

namespace DougongDesktop.App.Services;

/// <summary>
/// UI 侧的 IPC 客户端。
/// </summary>
/// <remarks>
/// <b>它只依赖 <c>DougongDesktop.Ipc</c></b>——不认识 SDK、不认识 Framework。
/// 这是"换掉 agent 框架时 UI 一行不改"的具体体现。
/// <para>
/// 三条职责：
/// <list type="number">
/// <item>发现引擎：读 <c>endpoint.json</c>，连不上就尝试拉起 AgentHost</item>
/// <item>收发消息：命令出去、事件回来，按 <c>Sequence</c> 检测缺口</item>
/// <item>故障可见：连不上、序号乱、引擎报错都要让用户看见，不静默重试</item>
/// </list>
/// </para>
/// </remarks>
public sealed class EngineClient : IAsyncDisposable
{
    private readonly IEngineLocator _locator;
    private readonly Action<string> _log;
    private IpcConnection? _connection;
    private CancellationTokenSource? _readLoop;
    private long _lastSequence;

    /// <param name="locator">
    /// 端点发现方式。null 时用桌面实现（读 <c>endpoint.json</c>、必要时拉起 AgentHost）。
    /// </param>
    /// <param name="log">诊断输出。</param>
    /// <remarks>
    /// <b>为什么端点发现要注入</b>：它做的是"读本地文件 + 启动子进程"，
    /// 两件都是桌面才成立的事。web 端两者都做不了（没有文件系统、浏览器
    /// 启动不了进程），届时换一个实现即可——<see cref="EngineClient"/>
    /// 本身只负责"连上之后怎么收发"，那部分与平台无关。
    /// </remarks>
    public EngineClient(IEngineLocator? locator = null, Action<string>? log = null)
    {
        _locator = locator ?? new FileEngineLocator(log);
        _log = log ?? (_ => { });
    }

    /// <summary>连接状态变化。UI 用它更新状态胶囊。</summary>
    public event Action<EngineConnectionState, string?>? StateChanged;

    /// <summary>收到服务端事件。</summary>
    public event Action<ServerEvent>? EventReceived;

    public EngineConnectionState State { get; private set; } = EngineConnectionState.Disconnected;

    public long LastSequence => Interlocked.Read(ref _lastSequence);

    /// <summary>
    /// 连接引擎。失败时按顺序尝试：读端点 → 拉起 AgentHost → 再连。
    /// </summary>
    /// <param name="executablePath">
    /// AgentHost 可执行文件路径。null 时按"与 UI 同目录的 AgentHost"推断。
    /// </param>
    public async Task<bool> ConnectAsync(string? executablePath = null, CancellationToken ct = default)
    {
        SetState(EngineConnectionState.Connecting, null);

        // ① 先试现有端点
        if (await TryConnectEndpointAsync(ct).ConfigureAwait(false))
            return true;

        // ② 连不上：可能没启动，也可能端点文件残留。设法拉起一个再试。
        //    "怎么拉起"由 locator 决定——桌面起子进程，web 起不了。
        _log("端点不可用，尝试启动引擎…");
        SetState(EngineConnectionState.Starting, null);

        if (_locator is FileEngineLocator fileLocator && executablePath is { Length: > 0 })
            fileLocator.ExecutablePath = executablePath;

        if (!await _locator.TryStartEngineAsync(ct).ConfigureAwait(false))
        {
            SetState(EngineConnectionState.Failed,
                "引擎未运行，且当前环境无法自动启动它。请先启动 AgentHost。");
            return false;
        }

        // ③ 等它就绪（最多 15 秒）
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (await TryConnectEndpointAsync(ct).ConfigureAwait(false))
                return true;
        }

        SetState(EngineConnectionState.Failed, "AgentHost 启动后 15 秒内仍未就绪。");
        return false;
    }

    /// <summary>发命令。未连接时抛错，不排队假装成功。</summary>
    /// <summary>
    /// 当前附着的会话。重同步要知道"重拉哪个会话"。
    /// </summary>
    /// <remarks>
    /// 由 UI 在打开/新建会话时设置。放在客户端而不是 UI 层，是因为重同步由读循环
    /// 触发——那里拿不到 UI 的状态。
    /// </remarks>
    public string? AttachedSessionId { get; set; }

    /// <summary>已请求重同步的次数（诊断用：能看出是不是一直在丢事件）。</summary>
    public int ResyncCount => Volatile.Read(ref _resyncCount);

    private int _resyncCount;

    /// <summary>重同步进行中的标志，避免缺口期间每来一条事件就发一次。</summary>
    private int _resyncing;

    /// <summary>
    /// 请求重同步。
    /// </summary>
    /// <remarks>
    /// <b>用 <see cref="Interlocked.CompareExchange(ref int, int, int)"/> 做单飞</b>：
    /// 缺口通常会连着来一串（丢了一条后面全错位），不加锁会瞬间发出几十条请求，
    /// 而每条都会回一份全量快照——比缺口本身更糟。
    /// <para>
    /// <b>失败不改连接状态</b>：重同步失败说明命令发不出去，那是连接问题，
    /// 由读循环自己的异常路径处理。这里只记日志，让状态栏如实反映连接状态。
    /// </para>
    /// </remarks>
    private async Task RequestResyncAsync(long expected, long received)
    {
        if (Interlocked.CompareExchange(ref _resyncing, 1, 0) != 0) return;

        try
        {
            Interlocked.Increment(ref _resyncCount);

            var connection = _connection;
            if (connection is null) return;

            await connection.SendAsync(new ResyncCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                SessionId = AttachedSessionId,
            }).ConfigureAwait(false);

            _log($"[重同步] 已请求（缺口 {expected}→{received}，" +
                 $"会话 {AttachedSessionId ?? "(无)"}）");
        }
        catch (Exception ex)
        {
            _log($"[重同步] 请求失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 留一点时间让快照回来并对齐序号，再允许下一次重同步
            await Task.Delay(500).ConfigureAwait(false);
            Volatile.Write(ref _resyncing, 0);
        }
    }

    public async Task SendAsync(ClientCommand command, CancellationToken ct = default)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("未连接引擎，无法发送命令。");

        await connection.SendAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 用 locator 找到端点并连上。
    /// </summary>
    /// <remarks>
    /// <b>这里只剩"连"这件事</b>：读文件、找可执行文件、启动进程都已搬到
    /// <see cref="IEngineLocator"/>。剩下的是纯网络操作，与平台无关。
    /// <para>
    /// <b>协议版本不匹配时明确拒绝，不"尽量解析"</b>：尽量解析会把
    /// "字段缺失"变成"字段是默认值"，而那种错误只在某个特定操作上才暴露。
    /// </para>
    /// </remarks>
    private async Task<bool> TryConnectEndpointAsync(CancellationToken ct)
    {
        try
        {
            var endpoint = await _locator.DiscoverAsync(ct).ConfigureAwait(false);
            if (endpoint is null) return false;

            if (endpoint.ProtocolVersion is { } version && version != HostProtocolContract.Version)
            {
                SetState(EngineConnectionState.Failed,
                    $"引擎协议版本 {version} 与客户端 {HostProtocolContract.Version} 不一致。");
                return false;
            }

            var connection = await IpcConnection
                .ConnectAsync($"{endpoint.Host}:{endpoint.Port}", ct)
                .ConfigureAwait(false);

            _connection = connection;
            _readLoop = CancellationTokenSource.CreateLinkedTokenSource(ct);

            _ = Task.Run(() => ReadLoopAsync(connection, _readLoop.Token), CancellationToken.None);

            SetState(EngineConnectionState.Connected, null);
            _log($"已连接 {endpoint.Host}:{endpoint.Port}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or SocketException)
        {
            // 端点文件残留是正常现象：进程被杀时来不及清理。
            // 所以文件存在不代表引擎活着——必须真的连上才算。
            return false;
        }
    }

    private async Task ReadLoopAsync(IpcConnection connection, CancellationToken ct)
    {
        try
        {
            await connection.ReadLoopAsync(
                json =>
                {
                    if (json.Deserialize<ServerEvent>(DesktopProtocol.Json) is not { } evt) return Task.CompletedTask;

                    // 序号缺口检测：不假装完整，明确要求重同步。
                    //
                    // 修过的一处：原来只把状态改成 Failed 就没了下文——客户端停在
                    // "需要重新同步"却不发任何请求，用户看到的是引擎"挂了"，
                    // 实际连接是好的。缺口是**数据**问题，不是连接问题。
                    var expected = Interlocked.Read(ref _lastSequence) + 1;
                    if (evt.Sequence > 0 && evt.Sequence != expected && _lastSequence > 0)
                    {
                        _log($"[序号缺口] 期望 {expected}，收到 {evt.Sequence}，请求重同步");
                        _ = RequestResyncAsync(expected, evt.Sequence);
                    }

                    // 序号基准以事件自带的 Sequence 为准。重同步返回的快照会带一个
                    // 大于等于当前序号的值，所以这里不需要专门"重置"——
                    // 显式重置反而会把并发到达的更新事件算成倒退。
                    if (evt.Sequence > 0) Interlocked.Exchange(ref _lastSequence, evt.Sequence);

                    // 必须回到 UI 线程：读循环跑在线程池上，而订阅者会碰控件。
                    // 直接 Invoke 会抛 "The calling thread cannot access this object"，
                    // 异常从读循环冒出来还会把整条连接判定为断开——实测踩过。
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => EventReceived?.Invoke(evt));

                    return Task.CompletedTask;
                },
                (line, ex) => _log($"[坏行] {ex.Message}：{(line.Length > 80 ? line[..80] : line)}"),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            _log($"[读循环异常] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                SetState(EngineConnectionState.Disconnected, "引擎连接已断开。");
        }
    }


    private void SetState(EngineConnectionState state, string? detail)
    {
        if (State == state && detail is null) return;
        State = state;

        // 同样要回 UI 线程：订阅者会更新控件
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            StateChanged?.Invoke(state, detail);
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => StateChanged?.Invoke(state, detail));
    }

    public async ValueTask DisposeAsync()
    {
        _readLoop?.Cancel();
        _readLoop?.Dispose();

        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);

        _connection = null;
    }
}

/// <summary>引擎连接状态。UI 用它渲染状态胶囊。</summary>
public enum EngineConnectionState
{
    Disconnected,
    Connecting,
    Starting,
    Connected,
    Failed,
}
