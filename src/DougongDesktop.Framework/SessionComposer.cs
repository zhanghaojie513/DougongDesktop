using Dougong.Agent;
using Dougong.Ai;
using Dougong.Hosting;
using DougongDesktop.Ipc;
using Microsoft.Extensions.AI;

namespace DougongDesktop.Framework;

/// <summary>
/// 引擎配置。来自环境变量或界面设置，不写死在代码里。
/// </summary>
public sealed record EngineOptions
{
    public required string BaseUrl { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }
    public ChatProtocol Protocol { get; init; } = ChatProtocol.OpenAiChatCompletions;
    public string SessionDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dougong-desktop", "sessions");
    public string Workspace { get; init; } = Environment.CurrentDirectory;

    /// <summary>
    /// 从环境变量读取。密钥只从环境变量来，不落盘、不进日志——
    /// 这条与 SDK 的纪律一致（见 SDK 的 --doctor 输出）。
    /// </summary>
    public static EngineOptions? FromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_BASE");
        var model = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_MODEL");
        var key = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            return null;

        var protocol = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_PROTOCOL") switch
        {
            "anthropic" => ChatProtocol.AnthropicMessages,
            "responses" => ChatProtocol.OpenAiResponses,
            _ => ChatProtocol.OpenAiChatCompletions,
        };

        return new EngineOptions
        {
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = key,
            Protocol = protocol,
            Workspace = Environment.GetEnvironmentVariable("DOUGONG_WORKSPACE") ?? Environment.CurrentDirectory,
            SessionDirectory = Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dougong-desktop", "sessions"),
        };
    }
}

/// <summary>
/// 把一个 SDK 内核装配成可用的会话运行时。
/// </summary>
/// <remarks>
/// 这是**唯一**知道 Dougong SDK 存在的地方。要换掉 SDK，只改这个文件；
/// UI 与 IPC 契约都不受影响。
/// <para>
/// 装配走 <c>KernelAgentHost.CreateAsync</c> 而不是手写 <c>new</c> 序列：
/// 那段代码的失败模式是静默的（注册顺序、accessor vs 裸实例、异常清理），
/// SDK 已把它抽成包正是为了不让每个宿主重写一遍。
/// </para>
/// </remarks>
public sealed class SessionComposer : IDisposable
{
    private readonly EngineOptions _options;
    private readonly IChatClient _client;

    public SessionComposer(EngineOptions options)
    {
        _options = options;

        var config = new ChatProviderConfig
        {
            Name = "dougong-desktop",
            BaseUrl = options.BaseUrl,
            Model = options.Model,
            Protocol = options.Protocol,
        };

        // 必须走"显式传密钥"的重载。
        // 另一个重载 Create(config, HttpClient?) 从 config.ApiKeyEnvironmentVariable
        // 指向的环境变量读密钥——这里那是 null，密钥会变成空字符串，
        // 表现为每次请求都 401 而看不出原因。
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "未配置 DOUGONG_PROVIDER_KEY。密钥只从环境变量读，不落盘。" +
                "这是刻意为之：没有密钥时明确失败，而不是发出一个空的 Authorization 头。");
        }

        _client = ChatClientFactory.Create(config, options.ApiKey);
    }

    /// <summary>
    /// 建一个会话运行时。
    /// </summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="title">显示标题。</param>
    /// <param name="workspace">工作区目录。</param>
    /// <param name="approver">
    /// 审批器。由调用方注入，因此框架层不知道审批是怎么呈现的——
    /// 这是 UI 与框架解耦的关键点。
    /// </param>
    /// <param name="emitEngineEvent">引擎级事件出口（不含会话事件）。</param>
    public async Task<SessionRuntime> CreateAsync(
        string sessionId,
        string title,
        string workspace,
        IToolApprover approver,
        Func<ServerEvent, Task> emitEngineEvent,
        Func<long> nextSequence,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.SessionDirectory);
        var storePath = Path.Combine(_options.SessionDirectory, $"{sessionId}.jsonl");

        // 会话存储：单写者租约，被占用时抛 IOException 而不是静默覆盖。
        JsonlAgentStore? store = null;
        try
        {
            store = await JsonlAgentStore.OpenAsync(storePath, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // 另一个实例正在写这个会话。如实告诉用户，不降级成"不持久化"。
            await emitEngineEvent(new EngineStatusEvent
            {
                Sequence = 0,
                State = EngineState.Failed,
                Detail = $"会话 {sessionId} 已被另一个进程占用：{ex.Message}",
            }).ConfigureAwait(false);
            throw;
        }

        var kernelHost = await KernelAgentHost.CreateAsync(
            store,
            createDispatcher: (resolvedApprover, resolvedStore, ct) =>
            {
                // dispatcher 是唯一执行入口，工具只在这里注册。
                var dispatcher = new ToolDispatcher(
                    approver: resolvedApprover,
                    workspace: workspace);

                RegisterBuiltInTools(dispatcher, workspace, resolvedStore);
                return Task.FromResult<IToolDispatcher>(dispatcher);
            },
            createRuntime: (dispatcherAccessor, resolvedStore) =>
                new LoopAgentRuntime(
                    _client,
                    dispatcherAccessor,
                    resolvedStore),
            approver: approver,
            diagnostics: Console.Error,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var runtime = kernelHost.ResolveRuntime();

        return new SessionRuntime(
            sessionId, title, workspace, runtime, _options.Model, nextSequence);
    }

    /// <summary>
    /// 注册内置工具。
    /// </summary>
    /// <remarks>
    /// 这里刻意只有三个只读/低风险工具。写文件、执行命令属于"产品决策"
    /// （SDK 的 host-layering-decision.md 明确说不进 SDK），
    /// 要等桌面端真正需要时再加，而不是先塞满。
    /// </remarks>
    private static void RegisterBuiltInTools(
        IToolDispatcher dispatcher, string workspace, IStoreAccessor? store)
    {
        dispatcher.Register(new ReadFileTool(workspace));
        dispatcher.Register(new ListDirectoryTool(workspace));
        dispatcher.Register(new PlanToolAdapter());
    }

    public void Dispose() => _client.Dispose();
}
