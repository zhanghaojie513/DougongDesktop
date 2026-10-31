using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dougong.Agent;
using Dougong.Ai;
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
            command = json.Deserialize<ClientCommand>(DesktopProtocol.Json);
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

                case SubmitFeedbackCommand feedback:
                    // 反馈只落本地诊断，不进模型历史、不改变会话状态。
                    // 它是产品改进数据，不是 agent 运行的一部分。
                    Console.Error.WriteLine(
                        $"[host] 反馈：{feedback.ItemId} → {feedback.Rating}" +
                        (feedback.Reason is { Length: > 0 } ? $"（{feedback.Reason}）" : ""));
                    break;

                case ListModelsCommand:
                    await connection.SendAsync(BuildModelsEvent(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case ListDirectoryCommand list:
                    await ListDirectoryAsync(list, connection, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 构建模型目录（响应 <see cref="ListModelsCommand"/>）。
    /// </summary>
    /// <remarks>
    /// <b>为什么由宿主算而不是 UI 自己查</b>：模型能力表来自 SDK 的
    /// <c>ProviderCatalog</c>，而 App 层禁止引用 SDK（架构守卫会红）。
    /// 经 IPC 传纯数据，UI 侧再转成 Arc 的 <c>AiModel</c>——分层不破，
    /// UI 也不必知道 SDK 类型。
    /// <para>
    /// <b>当前在用的 provider 必须补进列表</b>：如果用户配的是自建网关
    /// （<c>DOUGONG_PROVIDER_BASE</c> 指向内置 22 家之外），它不在
    /// <c>AllPresets</c> 里。不补的话，用户明明在用，选择器里却找不到它——
    /// 看起来像"配置没生效"，而实际只是列表没列。
    /// </para>
    /// </remarks>
    private ModelsListedEvent BuildModelsEvent()
    {
        var models = new List<ModelInfo>();
        var providers = new List<string>();

        foreach (var (providerId, preset) in ProviderCatalog.AllPresets
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            providers.Add(providerId);

            foreach (var m in preset.Models)
            {
                models.Add(new ModelInfo(
                    providerId,
                    m.Id,
                    m.ContextWindow,
                    m.MaxOutputTokens,
                    m.SupportsReasoning));
            }
        }

        AddCurrentProviderIfMissing(models, providers);

        return new ModelsListedEvent
        {
            Sequence = NextStatusSequence(),
            Models = models,
            Providers = providers,
            CurrentModelId = _options.Model,
        };
    }

    /// <summary>
    /// 把当前配置的 provider 补进目录（若它不在内置表里）。
    /// </summary>
    /// <remarks>
    /// <b>先按 BaseUrl 匹配内置 provider</b>：用户可能配的是内置那家的
    /// 另一个端点（例如网页版地址），那时应当归到同一家，而不是变成
    /// 一个新的"custom"。匹配用的是规范化后的 host + 路径前缀比较——
    /// 精确相等太脆（末尾斜杠、大小写、是否带 /v1 都会让它失配）。
    /// <para>
    /// <b>匹配不上就叫 custom</b>：不猜一个看起来像真名的标签。
    /// 猜错比留空更糟——用户会以为自己在用某家服务，而实际上不是。
    /// </para>
    /// <para>
    /// <b>能力数据未知时给 0</b>：<see cref="ModelInfo"/> 的窗口字段
    /// 约定 0 表示未知，UI 应照此显示，不要编一个数。
    /// </para>
    /// </remarks>
    private void AddCurrentProviderIfMissing(List<ModelInfo> models, List<string> providers)
    {
        if (string.IsNullOrWhiteSpace(_options.Model)) return;

        var providerId = MatchProviderByBaseUrl(_options.BaseUrl) ?? "custom";

        if (providers.Contains(providerId, StringComparer.OrdinalIgnoreCase))
        {
            // 内置 provider 已列出，但当前模型可能不在它的模型表里
            // （用户手填了一个该家新增的模型名）。补一条，否则"当前模型"
            // 在选择器里没有对应项，UI 无法高亮。
            EnsureModelListed(models, providerId);
            return;
        }

        providers.Insert(0, providerId);
        EnsureModelListed(models, providerId, _options.ContextWindowTokens, _options.MaxOutputTokens);
    }

    /// <summary>当前模型必须在 <paramref name="models"/> 里出现一次。</summary>
    private void EnsureModelListed(
        List<ModelInfo> models, string providerId,
        int contextWindow = 0, int maxOutput = 0)
    {
        var exists = models.Any(m =>
            string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.Id, _options.Model, StringComparison.Ordinal));
        if (exists) return;

        // 先按内置能力表查（内置 provider 的新模型可能有数据）。
        var known = ProviderCatalog.FindModelCapabilities(providerId, _options.Model);

        models.Insert(0, new ModelInfo(
            providerId,
            _options.Model,
            known?.ContextWindow ?? contextWindow,
            known?.MaxOutputTokens ?? maxOutput,
            known?.SupportsReasoning ?? false));
    }

    /// <summary>
    /// 按 BaseUrl 找出内置 provider 的标识；匹配不上返回 null。
    /// </summary>
    /// <remarks>
    /// 比较 host 与路径前缀而不是完整字符串：用户可能配
    /// <c>https://api.deepseek.com/v1</c>，而预设里是
    /// <c>https://api.deepseek.com</c>——完整比较会失配，
    /// 那样每家用内置端点的用户都会多出一个 custom 条目。
    /// </remarks>
    private static string? MatchProviderByBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var target)) return null;

        foreach (var (providerId, preset) in ProviderCatalog.AllPresets)
        {
            if (!Uri.TryCreate(preset.BaseUrl, UriKind.Absolute, out var known)) continue;

            if (!string.Equals(known.Host, target.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            // host 相同还不够：同一域名可能挂多家兼容端点
            //（例如 OpenAI 与 Azure OpenAI 不是同域，但不少网关同域多路径）。
            var knownPath = known.AbsolutePath.TrimEnd('/');
            var targetPath = target.AbsolutePath.TrimEnd('/');

            if (knownPath.Length == 0 || targetPath.StartsWith(knownPath, StringComparison.OrdinalIgnoreCase))
                return providerId;
        }

        return null;
    }

    /// <summary>
    /// 列工作区目录（右栏文件树）。
    /// </summary>
    /// <remarks>
    /// <b>路径必须落在工作区内</b>：右栏是只读浏览，但"只读"不等于"可以读任意位置"。
    /// 越界请求返回错误而不是静默截断——用户需要知道自己的路径被拒了。
    /// <para>
    /// <b>500 条上限</b>：与 <c>list_directory</c> 工具一致。列一万个文件会让
    /// IPC 消息变成几兆、右栏卡住，而用户并不需要一次看到全部。
    /// </para>
    /// </remarks>
    private async Task ListDirectoryAsync(
        ListDirectoryCommand command, IpcConnection connection, CancellationToken cancellationToken)
    {
        const int maxEntries = 500;

        var relative = command.Path ?? string.Empty;

        try
        {
            var root = Path.GetFullPath(_workspace);
            var target = string.IsNullOrWhiteSpace(relative)
                ? root
                : Path.GetFullPath(Path.Combine(root, relative));

            // 用边界判断而不是 StartsWith("..")：符号链接、大小写、分隔符混用
            // 都能绕过字符串检查
            var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var inside = target.Equals(root, StringComparison.OrdinalIgnoreCase)
                         || target.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);

            if (!inside)
            {
                await connection.SendAsync(new DirectoryListedEvent
                {
                    Sequence = NextStatusSequence(),
                    Path = relative,
                    Entries = [],
                    Error = $"路径 '{relative}' 在工作区之外，已拒绝。",
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!Directory.Exists(target))
            {
                await connection.SendAsync(new DirectoryListedEvent
                {
                    Sequence = NextStatusSequence(),
                    Path = relative,
                    Entries = [],
                    Error = $"目录不存在：{relative}",
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var all = new DirectoryInfo(target)
                .EnumerateFileSystemInfos()
                .OrderBy(e => e is DirectoryInfo ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var entries = all
                .Take(maxEntries)
                .Select(e => new DirectoryEntry(
                    e.Name,
                    e is DirectoryInfo,
                    e is FileInfo f ? f.Length : 0))
                .ToArray();

            await connection.SendAsync(new DirectoryListedEvent
            {
                Sequence = NextStatusSequence(),
                Path = relative,
                Entries = entries,
                Truncated = all.Length > maxEntries
                    ? $"共 {all.Length} 项，只列出前 {maxEntries} 项。"
                    : null,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await connection.SendAsync(new DirectoryListedEvent
            {
                Sequence = NextStatusSequence(),
                Path = relative,
                Entries = [],
                Error = $"{ex.GetType().Name}: {ex.Message}",
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CreateSessionAsync(
        CreateSessionCommand command, IpcConnection connection, CancellationToken cancellationToken)
    {
        var sessionId = $"sess-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..24];
        var workspace = string.IsNullOrWhiteSpace(command.Workspace) ? _workspace : command.Workspace;
        var title = command.Title ?? "新会话";

        // 审批器要在创建之前交给 composer，而"拒绝并停止"需要拿到运行时才能
        // 取消回合——先有鸡还是先有蛋。用一个延迟赋值的句柄绕开：
        // 审批只会在回合进行中发生，那时 runtime 一定已经赋好值了。
        SessionRuntime? created = null;

        var runtime = await _composer.CreateAsync(
            sessionId, title, workspace,
            CreateApprover(connection, () => created?.Cancel()),
            evt => connection.SendAsync(evt, cancellationToken),
            NextStatusSequence,
            _options.ContextWindowTokens,
            _options.MaxOutputTokens,
            cancellationToken).ConfigureAwait(false);

        created = runtime;
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
            // 回合前后的文件版本快照：右栏"改动" tab 靠它给出真实差异。
            // 在回合开始**之前**拍，因为工具执行期间文件就在变。
            var before = CaptureWorkspace();

            try
            {
                await runtime.RunTurnAsync(
                    command.Input,
                    evt => connection.SendAsync(evt, CancellationToken.None),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await PushWorkspaceChangesAsync(connection, before, cancellationToken)
                    .ConfigureAwait(false);
                await PushStatusAsync(EngineState.Ready, null).ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 工作区文件版本快照的扫描上限。
    /// </summary>
    /// <remarks>
    /// <b>为什么要有上限</b>：<c>FileVersionSet.Capture</c> 要对每个文件算内容哈希，
    /// 几十万文件的工作区会卡住整个回合结束流程——而用户等的是"回合结束"，
    /// 不是"统计变了哪些文件"。超限时如实告知，不静默漏掉。
    /// </remarks>
    private const int MaxSnapshotFiles = 20_000;

    /// <summary>
    /// 一个回合开始时的文件版本快照 + 路径清单。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要保存路径清单</b>：<c>Capture</c> 只对**传入的路径**取快照，
    /// 它不会自己去发现新文件。所以回合中新建的文件如果不在开始时的清单里，
    /// 结束时根本比不到——<b>新增文件会被完全漏报</b>。
    /// 结束时必须用"开始清单 ∪ 结束扫描"的并集去 Capture。
    /// </remarks>
    private sealed record WorkspaceSnapshot(FileVersionSet Versions, string[] Paths);

    /// <summary>
    /// 拍一次工作区文件版本快照。
    /// </summary>
    /// <remarks>
    /// <b>跳过 <c>bin</c> / <c>obj</c> / <c>.git</c> / <c>node_modules</c></b>：
    /// 构建产物每个回合都在变，报出来的"改动"全是噪音，会把真正的
    /// 源码改动淹没。
    /// </remarks>
    private WorkspaceSnapshot CaptureWorkspace()
    {
        try
        {
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", "bin", "obj", "node_modules", ".vs", ".idea",
            };

            var paths = Directory
                .EnumerateFiles(_workspace, "*", SearchOption.AllDirectories)
                .Where(path => !Path
                    .GetRelativePath(_workspace, path)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(seg => skipped.Contains(seg)))
                .Take(MaxSnapshotFiles)
                .ToArray();

            return new WorkspaceSnapshot(FileVersionSet.Capture(paths), paths);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host] 文件快照失败：{ex.GetType().Name}: {ex.Message}");
            return new WorkspaceSnapshot(FileVersionSet.Empty, []);
        }
    }

    /// <summary>
    /// 对比快照，把真实差异推给客户端。
    /// </summary>
    /// <remarks>
    /// <b>三处必须写对，否则会静默漏报</b>：
    /// <list type="number">
    /// <item><b>用两份路径的并集重新 Capture</b>：只 Capture 结束时的扫描结果，
    /// 新增文件没有"之前"可比；只 Capture 开始时的清单，新增文件根本不在里面。
    /// 两个方向都漏，所以必须取并集。</item>
    /// <item><b>调用方向是 <c>before.Diff(after)</c></b>：实测反过来会把新增文件
    /// 报成"从审批时存在变为不可见"，方向反了。SDK 的 Diff 语义是
    /// "以调用者为基准，看参数有什么变化"。</item>
    /// <item><b>删除的文件长度会显示 -1</b>：<c>FileVersion.Exists=false</c> 时
    /// 长度是 -1，SDK 的文案会写成"文件长度变化：X（1 → -1）"。
    /// 界面上要能读懂，别让用户以为文件变成 -1 字节。</item>
    /// </list>
    /// <para>
    /// <b>Diff 返回的是变更描述而不是行级差异</b>：<c>FileVersion</c> 只存长度、
    /// 时间、内容哈希，不存原内容——做不到"第几行改成什么"。界面上必须说清。
    /// </para>
    /// </remarks>
    private async Task PushWorkspaceChangesAsync(
        IpcConnection connection, WorkspaceSnapshot before, CancellationToken cancellationToken)
    {
        try
        {
            var after = CaptureWorkspace();

            // 并集：删掉的文件只在 before 里，新建的只在 after 里，
            // 只取一边都会漏
            var union = before.Paths
                .Union(after.Paths, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // 用并集重新取一次"现在"的快照：删除的文件只在 before 里，
            // 不重新 Capture 就完全看不到它
            var nowSet = FileVersionSet.Capture(union);

            var changes = WorkspaceDiff.Compare(before.Versions.Files, nowSet.Files);
            var diff = WorkspaceDiff.Describe(changes);

            await connection.SendAsync(new WorkspaceChangedEvent
            {
                Sequence = NextStatusSequence(),
                Diff = diff,
                HasChanges = diff.Length > 0,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host] 推送文件改动失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <summary>
    /// 建审批器。
    /// </summary>
    /// <param name="connection">IPC 连接，用来把审批推给客户端。</param>
    /// <param name="stopTurn">
    /// "拒绝并停止"时取消回合的动作。为 null 时该档退化为普通拒绝
    /// （审批器会记日志，不让它静默退化）。
    /// </param>
    private IToolApprover CreateApprover(IpcConnection connection, Action? stopTurn = null) =>
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
        },
        timeout: null,
        stopTurn: _ => stopTurn?.Invoke());

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
