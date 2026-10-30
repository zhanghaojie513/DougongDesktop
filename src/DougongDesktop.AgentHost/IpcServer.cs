using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dougong.Agent;
using DougongDesktop.Framework;
using DougongDesktop.Ipc;

namespace DougongDesktop.AgentHost;

/// <summary>
/// IPC 服务端：接受一条 UI 连接，处理命令，推送事件。
/// </summary>
/// <remarks>
/// 第一版只允许**一条**控制连接。理由与 SDK 的宿主契约一致：
/// 同一会话只允许一个可写控制者，否则两个窗口会互相覆盖。
/// 第二条连接连上来时明确拒绝，而不是让它以为成功了。
/// </remarks>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly SessionComposer _composer;
    private readonly ConcurrentDictionary<string, SessionRuntime> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalResponse>> _pendingApprovals =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly string _workspace;
    private long _statusSequence;

    private IpcConnection? _client;

    private readonly EngineOptions _options;

    public IpcServer(SessionComposer composer, EngineOptions options, string workspace)
    {
        _composer = composer;
        _options = options;
        _workspace = workspace;
    }

    public async Task RunAsync(int port, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(backlog: 1);

        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        EndpointFile.Write(endpoint.Port);

        Console.Error.WriteLine($"[host] 监听 127.0.0.1:{endpoint.Port}，工作区={_workspace}");

        try
        {
            await PushStatusAsync(EngineState.Ready, null).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 只允许一条控制连接。第二条拒绝并说明原因。
                if (!await _connectionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    Console.Error.WriteLine("[host] 拒绝第二条连接：同一会话只允许一个控制者。");
                    await using (var rejected = IpcConnection.FromStream(tcp.GetStream()))
                    {
                        await rejected.SendAsync(new EngineStatusEvent
                        {
                            Sequence = Interlocked.Increment(ref _statusSequence),
                            State = EngineState.Failed,
                            Detail = "已有一个控制连接，第二个连接被拒绝。",
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    tcp.Dispose();
                    continue;
                }

                var connection = IpcConnection.FromStream(tcp.GetStream());
                _client = connection;

                try
                {
                    await connection.ReadLoopAsync(
                        json => HandleAsync(json, connection, cancellationToken),
                        (line, ex) => Console.Error.WriteLine(
                            $"[host] 跳过无法解析的行（{ex.Message}）：{Truncate(line)}"),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Console.Error.WriteLine("[host] 客户端断开。");
                    _client = null;
                    await connection.DisposeAsync().ConfigureAwait(false);
                    tcp.Dispose();
                    _connectionGate.Release();

                    // 客户端走了：所有等待中的审批立即按"拒绝"结案，
                    // 不让它们挂到超时才失败。
                    foreach (var pending in _pendingApprovals)
                    {
                        pending.Value.TrySetResult(
                            new ApprovalResponse(pending.Key, ApprovalDecision.Deny, "客户端已断开。"));
                    }
                }
            }
        }
        finally
        {
            listener.Stop();
            EndpointFile.Delete();
        }
    }

    private async Task HandleAsync(
        JsonElement json, IpcConnection connection, CancellationToken cancellationToken)
    {
        ClientCommand? command;
        try
        {
            command = json.Deserialize<ClientCommand>(IpcProtocol.Json);
        }
        catch (JsonException ex)
        {
            await SendErrorAsync(connection, $"无法解析命令：{ex.Message}", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (command is null)
        {
            await SendErrorAsync(connection, "命令为空。", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            switch (command)
            {
                case ListSessionsCommand:
                    await connection.SendAsync(new SessionListedEvent
                    {
                        Sequence = NextStatusSequence(),
                        Sessions = [.. _sessions.Values
                            .Select(s => s.Summary())
                            .OrderByDescending(s => s.UpdatedAt)],
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                case CreateSessionCommand create:
                    await CreateSessionAsync(create, connection, cancellationToken).ConfigureAwait(false);
                    break;

                case OpenSessionCommand open:
                    await OpenSessionAsync(open, connection, cancellationToken).ConfigureAwait(false);
                    break;

                case ResyncCommand resync:
                    // 重同步就是"再开一次会话"：回一份带当前序号的全量快照。
                    // 复用 OpenSessionAsync 而不是另写一份，避免两条路径的快照
                    // 字段不一致——那种不一致只有在丢事件后才会暴露。
                    if (resync.SessionId is { Length: > 0 } resyncId)
                    {
                        await OpenSessionAsync(
                            new OpenSessionCommand { RequestId = resync.RequestId, SessionId = resyncId },
                            connection, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        // 还没打开会话：回一个基准序号，客户端据此继续判定缺口。
                        await connection.SendAsync(new EngineStatusEvent
                        {
                            Sequence = NextStatusSequence(),
                            State = EngineState.Ready,
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case StartTurnCommand start:
                    await StartTurnAsync(start, connection, cancellationToken).ConfigureAwait(false);
                    break;

                case CancelTurnCommand cancel:
                    if (_sessions.TryGetValue(cancel.SessionId, out var toCancel))
                    {
                        toCancel.Cancel();
                    }
                    break;

                case RespondApprovalCommand respond:
                    if (_pendingApprovals.TryRemove(respond.Response.RequestId, out var pending))
                    {
                        pending.TrySetResult(respond.Response);
                    }
                    else
                    {
                        // 重复答复或已过期的答复：不产生第二次授权。
                        Console.Error.WriteLine(
                            $"[host] 忽略未知或重复的审批答复：{respond.Response.RequestId}");
                    }
                    break;

                default:
                    await SendErrorAsync(
                        connection, $"未实现的命令：{command.GetType().Name}", cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            await SendErrorAsync(
                connection, $"{command.GetType().Name} 失败：{ex.Message}", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task CreateSessionAsync(
        CreateSessionCommand command, IpcConnection connection, CancellationToken cancellationToken)
    {
        var sessionId = $"sess-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..24];
        var workspace = string.IsNullOrWhiteSpace(command.Workspace) ? _workspace : command.Workspace;
        var title = command.Title ?? "新会话";

        var runtime = await _composer.CreateAsync(
            sessionId, title, workspace,
            CreateApprover(connection),
            evt => connection.SendAsync(evt, cancellationToken),
            NextStatusSequence,
            _options.ContextWindowTokens,
            _options.MaxOutputTokens,
            cancellationToken).ConfigureAwait(false);

        _sessions[sessionId] = runtime;

        await connection.SendAsync(new SessionOpenedEvent
        {
            Sequence = NextStatusSequence(),
            Snapshot = runtime.Snapshot(),
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenSessionAsync(
        OpenSessionCommand command, IpcConnection connection, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(command.SessionId, out var runtime))
        {
            await SendErrorAsync(
                connection, $"会话不存在：{command.SessionId}", cancellationToken).ConfigureAwait(false);
            return;
        }

        await connection.SendAsync(new SessionOpenedEvent
        {
            Sequence = NextStatusSequence(),
            Snapshot = runtime.Snapshot(),
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task StartTurnAsync(
        StartTurnCommand command, IpcConnection connection, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(command.SessionId, out var runtime))
        {
            await SendErrorAsync(
                connection, $"会话不存在：{command.SessionId}", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (runtime.IsRunning)
        {
            await SendErrorAsync(
                connection, "该会话已有回合在运行。请先取消或等待完成。", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await PushStatusAsync(EngineState.Busy, null).ConfigureAwait(false);

        // 回合在后台跑：这条命令不阻塞读循环，否则取消命令永远读不到。
        // 这与 SDK 修过的 "session/prompt 阻塞读循环导致 cancel 无效" 是同一个坑。
        _ = Task.Run(async () =>
        {
            try
            {
                await runtime.RunTurnAsync(
                    command.Input,
                    evt => connection.SendAsync(evt, CancellationToken.None),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await PushStatusAsync(EngineState.Ready, null).ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    private IToolApprover CreateApprover(IpcConnection connection) =>
        new IpcApprover(async (approval, ct) =>
        {
            var pending = new TaskCompletionSource<ApprovalResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _pendingApprovals[approval.RequestId] = pending;

            await connection.SendAsync(new ApprovalRequestedEvent
            {
                Sequence = NextStatusSequence(),
                Request = approval,
            }, ct).ConfigureAwait(false);

            using var registration = ct.Register(() => pending.TrySetResult(
                new ApprovalResponse(approval.RequestId, ApprovalDecision.Deny, "审批等待被取消。")));

            try
            {
                return await pending.Task.ConfigureAwait(false);
            }
            finally
            {
                _pendingApprovals.TryRemove(approval.RequestId, out _);
            }
        });

    private Task PushStatusAsync(EngineState state, string? detail)
    {
        var client = _client;
        if (client is null) return Task.CompletedTask;

        return client.SendAsync(new EngineStatusEvent
        {
            Sequence = NextStatusSequence(),
            State = state,
            ModelName = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_MODEL"),
            Detail = detail,
        }, CancellationToken.None);
    }

    private Task SendErrorAsync(IpcConnection connection, string message, CancellationToken ct) =>
        connection.SendAsync(new EngineStatusEvent
        {
            Sequence = NextStatusSequence(),
            State = EngineState.Ready,
            Detail = message,
        }, ct);

    private long NextStatusSequence() => Interlocked.Increment(ref _statusSequence);

    private static string Truncate(string line) =>
        line.Length <= 120 ? line : line[..120] + "…";

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _connectionGate.Dispose();
    }
}
