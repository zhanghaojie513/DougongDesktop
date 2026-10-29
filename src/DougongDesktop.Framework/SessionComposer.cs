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
    /// 模型的最大上下文窗口（token）。
    /// </summary>
    /// <remarks>
    /// <b>为什么没有固定默认值</b>：当前主流窗口跨度从 32k 到 1M，30 倍差距。
    /// 给固定默认值必然错一半，而且两个方向的代价不对称——
    /// 填小了会过早压缩（静默浪费可用上下文），填大了压缩永不触发（真溢出报错）。
    /// <para>
    /// 从模型名推断的值放在 <see cref="InferContextWindow"/>，但那只用于预填，
    /// 用户必须能看到并可以覆盖。推断不出来时返回 0 = 不压缩。
    /// </para>
    /// </remarks>
    public int ContextWindowTokens { get; init; }

    /// <summary>
    /// 单次回复的最大输出 token。
    /// </summary>
    /// <remarks>
    /// 必须与实际请求用的值一致——否则压缩预留的空间与实际不符，
    /// 算出来的预算就是错的。见 <see cref="SessionComposer"/> 里传给 <c>ChatOptions</c> 的那处。
    /// </remarks>
    public int MaxOutputTokens { get; init; } = 4096;

    /// <summary>
    /// 按模型名推断上下文窗口。**只是预填提示，不是权威。**
    /// </summary>
    /// <remarks>
    /// 这张表一定会过时（模型迭代快），所以返回 0 时不要偷偷用一个默认值——
    /// 那会让"不知道模型多大"这件事被掩盖成"用了某个错的值"。
    /// </remarks>
    public static int InferContextWindow(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return 0;

        var m = model.ToLowerInvariant();

        // 1M 档
        if (m.Contains("gemini-1.5") || m.Contains("gemini-2")) return 1_000_000;

        // 200k 档（Claude 全线）
        if (m.Contains("claude")) return 200_000;

        // 128k 档
        if (m.Contains("gpt-4") || m.Contains("gpt-5")) return 128_000;
        if (m.Contains("deepseek")) return 128_000;
        if (m.Contains("qwen")) return 128_000;
        if (m.Contains("glm") || m.Contains("zhipu")) return 128_000;
        if (m.Contains("kimi") || m.Contains("moonshot")) return 128_000;

        // 推断不出：返回 0，由调用方决定（不猜）
        return 0;
    }

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
            ContextWindowTokens = ReadContextWindow(model),
            MaxOutputTokens = ReadMaxOutput(model),
            Workspace = Environment.GetEnvironmentVariable("DOUGONG_WORKSPACE") ?? Environment.CurrentDirectory,
            SessionDirectory = Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dougong-desktop", "sessions"),
        };
    }

    /// <summary>
    /// 上下文窗口的取值顺序：显式配置 → 按模型名推断 → 0（不压缩）。
    /// </summary>
    private static int ReadContextWindow(string model)
    {
        var raw = Environment.GetEnvironmentVariable("DOUGONG_CONTEXT_WINDOW");
        if (int.TryParse(raw, out var explicitValue) && explicitValue > 0)
            return explicitValue;

        return InferContextWindow(model);
    }

    private static int ReadMaxOutput(string model)
    {
        _ = model;
        var raw = Environment.GetEnvironmentVariable("DOUGONG_MAX_OUTPUT");
        return int.TryParse(raw, out var value) && value > 0 ? value : 4096;
    }

    /// <summary>
    /// 校验容量配置自洽。
    /// </summary>
    /// <remarks>
    /// SDK 的 <c>TokenBudget.EffectiveInput</c> 在预算为负时**直接抛
    /// ArgumentOutOfRangeException**，而压缩发生在每轮调模型之前——
    /// 用户填了「上下文 8000 + 输出 8192」，第一次对话就崩，
    /// 报的错完全看不出是配置问题。
    /// <para>
    /// 所以必须在装配时就拦下来，给出能看懂的话。
    /// </para>
    /// </remarks>
    public void Validate()
    {
        if (ContextWindowTokens <= 0) return;   // 没配窗口 = 不压缩，合法

        // 512 = ContextLimits.OverheadTokens 的默认值（估算误差与工具声明的预留）
        const int overhead = 512;
        var effective = ContextWindowTokens - MaxOutputTokens - overhead;

        if (effective <= 0)
        {
            throw new InvalidOperationException(
                $"上下文窗口配置不可行：窗口 {ContextWindowTokens} 减去输出 {MaxOutputTokens} " +
                $"与预留 {overhead} 后为 {effective}，必须为正。\n" +
                $"请把 DOUGONG_CONTEXT_WINDOW 调到大于 " +
                $"{MaxOutputTokens + overhead}，或用 DOUGONG_MAX_OUTPUT 调小输出上限。");
        }
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
    private readonly IChatClient? _client;

    public SessionComposer(EngineOptions options)
    {
        _options = options;

        // 没有密钥时不建连接器，但也不抛错。
        // 理由：Host 必须能起来，否则 UI 连不上、用户看不到"未配置"提示。
        // 失败推迟到建会话时，并给出明确原因（见 CreateAsync）。
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _client = null;
            return;
        }

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
        _client = ChatClientFactory.Create(config, options.ApiKey);
    }

    /// <summary>是否配置了可用的模型连接。</summary>
    public bool IsConfigured => _client is not null;

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
        int contextWindowTokens,
        int maxOutputTokens,
        CancellationToken cancellationToken = default)
    {
        // 未配置时在建会话这一步失败，而不是在构造时抛——
        // 这样 Host 能起来、UI 能看到"未配置"，用户知道要做什么。
        if (_client is null)
        {
            var detail =
                "未配置模型连接：缺少 DOUGONG_PROVIDER_KEY。" +
                "密钥只从环境变量读，不落盘。请设置后重启引擎。";
            await emitEngineEvent(new EngineStatusEvent
            {
                Sequence = 0,
                State = EngineState.Failed,
                Detail = detail,
            }).ConfigureAwait(false);
            throw new InvalidOperationException(detail);
        }

        var client = _client;
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
            {
                // 三个压缩参数必须一起传。SDK 的 host-layering-decision.md §4.4
                // 把这件事列为"装配容易漏"的实例：缺一个就**静默不压缩**。
                //
                // 之前只传了前三个参数，压缩在生产路径上从未生效——
                // 历史无限增长直到撞 provider 窗口上限报 400，而用户看不到任何征兆。
                if (contextWindowTokens is not > 0)
                {
                    // 没配窗口就不压。这是明确选择，不是漏传：
                    // 没有容量信息时压缩阈值无从计算，宁可如实不压。
                    return new LoopAgentRuntime(client, dispatcherAccessor, resolvedStore);
                }

                return new LoopAgentRuntime(
                    client,
                    dispatcherAccessor,
                    resolvedStore,
                    new ContextLimits
                    {
                        ContextWindowTokens = contextWindowTokens,
                        MaxOutputTokens = maxOutputTokens,
                    },
                    new SlidingWindowCompactor(preservedHead: 2, preservedTail: 6),
                    (transcript, ct) => SummarizeAsync(client, transcript, ct));
            },
            approver: approver,
            diagnostics: Console.Error,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var runtime = kernelHost.ResolveRuntime();

        return new SessionRuntime(
            sessionId, title, workspace, runtime, _options.Model, nextSequence);
    }

    /// <summary>
    /// 用同一个连接器生成摘要。
    /// </summary>
    /// <remarks>
    /// 失败由循环降级为"保留原历史"——SDK 的 <c>TryCompactAsync</c> 会捕获并
    /// 发 <c>ContextCompacted(Applied: false)</c>，不中断回合。
    /// <para>
    /// 走流式：与非流式相比少一次等待，且与主对话路径用的是同一套传输。
    /// </para>
    /// </remarks>
    private static async ValueTask<string> SummarizeAsync(
        IChatClient client, string transcript, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "把下面的对话历史压缩成要点，保留任务目标、已做的决定、失败过的尝试与未完成事项。"
                + "不要编造未出现的内容。只输出摘要正文。"),
            new(ChatRole.User, transcript),
        };

        var builder = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            messages, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (update.Text is { Length: > 0 } chunk) builder.Append(chunk);
        }

        return builder.ToString();
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

    public void Dispose() => _client?.Dispose();
}
