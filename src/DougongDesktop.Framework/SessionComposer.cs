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

    /// <summary>
    /// Anthropic 兼容层的端点路径覆盖。仅对 <see cref="ChatProtocol.AnthropicMessages"/> 有效。
    /// </summary>
    /// <remarks>
    /// 留 null 时用 SDK 默认的 <c>/v1/messages</c>，多数端点如此。
    /// <para>
    /// 什么时候要设：base 地址与服务端实际路径拼不上时。例如
    /// base = <c>https://api.minimaxi.com/anthropic</c>，服务端要 <c>/anthropic/v1/messages</c>
    /// —— 拼出来正好对，所以**不用改**。只有服务端要求别的前缀时才设。
    /// </para>
    /// </remarks>
    public string? MessagesPath { get; init; }
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
    /// 取值走 <see cref="ReadContextWindow"/> 的三层：显式配置 → SDK 能力表
    /// → SDK 的估算回退（262144）。<b>不再有手写的按名推断表</b>——
    /// 它曾把 deepseek 一律判成 128k，而真实值是 1M，差 8 倍。
    /// </para>
    /// </remarks>
    public int ContextWindowTokens { get; init; }

    /// <summary>
    /// 请求预留：本次请求期望的最大输出长度。
    /// </summary>
    /// <remarks>
    /// <b>这是压缩预算的输入，不是模型容量。</b>两者必须分清：
    /// <list type="bullet">
    /// <item>本值（固定 <see cref="RequestOutputReserveTokens"/> 或环境变量覆盖）：
    /// "我这次最多让模型输出多少"，用来从窗口里扣出可用输入空间</item>
    /// <item>能力表的 <c>ModelCapabilities.MaxOutputTokens</c>：
    /// "这个模型最多能输出多少"，实测占窗口 32%–38%</item>
    /// </list>
    /// <para>
    /// <b>用后者当预留的后果</b>：预算被吃掉三分之一，压缩过早触发——
    /// 200k 窗口按 64k 预留只剩 135k 可用输入，白扔三分之二的历史。
    /// 而 SDK 0.1.10 起把这个参数正式改名成 <c>RequestOutputReserve</c>，
    /// 就是在强调这个区分。
    /// </para>
    /// </remarks>
    public int MaxOutputTokens { get; init; } = RequestOutputReserveTokens;

    /// <summary>
    /// 请求预留的默认值。
    /// </summary>
    /// <remarks>
    /// <b>4096 的理由</b>：绝大多数对话回复远短于此；真正需要长输出的场景
    /// （生成大文件）可以显式调高。取一个偏小的值让更多窗口留给输入——
    /// 输入不够会丢历史，输出不够只是这次回复短一些。
    /// <para>
    /// 这与 SDK 回退值的设计意图一致（<c>FallbackMaxOutputTokens = 32768</c>
    /// 是容量估计，不是预留）。要调时用 <c>DOUGONG_MAX_OUTPUT</c>。
    /// </para>
    /// </remarks>
    public const int RequestOutputReserveTokens = 4096;

    /// <summary>
    /// 上下文窗口的取值顺序：显式配置 → SDK 能力表 → SDK 的估算回退。
    /// </summary>
    /// <remarks>
    /// <b>这里原来有一张手写的按名推断表（claude→200k、deepseek→128k…），已删除。</b>
    /// 它错得很具体：对 <c>deepseek</c> 一律给 128k，而 SDK 能力表里
    /// <c>deepseek-flash</c> 是 <b>1M</b>——差 8 倍。压缩会在 128k 就触发，
    /// 白白摘掉四分之三的历史，而用户完全看不出是这里的问题。
    /// <para>
    /// <b>为什么删而不是修数字</b>：改对一家还会有下一家错。这张表的本质问题是
    /// "拿猜测冒充事实"，而 SDK 0.1.8 起有 models.dev 生成的 662 个模型的真实能力表，
    /// 0.1.9 又补了第三层回退（<see cref="ProviderCatalog.FallbackContextWindow"/>，262144）。
    /// 三层都覆盖不到时才真的未知——那时用 SDK 的中位数估算，也比手写档位强：
    /// <list type="bullet">
    /// <item>显式配置：用户说了算</item>
    /// <item>能力表查询到：事实，且 SDK 的生成脚本会跟着上游更新</item>
    /// <item>回退估算：明确标注"这是估计值"，界面上与事实区分开</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>取中位数而不是最大值</b>（SDK 的理由）：填大的代价更糟——压缩永不触发，
    /// 直到撞上 provider 真上限才失败；填小只是多压几次，有代价但可恢复。
    /// </para>
    /// </remarks>
    public static int ReadContextWindow(string model)
    {
        var raw = Environment.GetEnvironmentVariable("DOUGONG_CONTEXT_WINDOW");
        if (int.TryParse(raw, out var explicitValue) && explicitValue > 0)
            return explicitValue;

        var caps = LookupCapabilities(model);
        if (caps is { ContextWindow: > 0 }) return caps.ContextWindow;

        // 第三层：能力表没收录。用 SDK 的估算值，但要让调用方知道这是估计——
        // 返回 0 会让压缩永不启用，长会话撞上真上限时报的是 provider 原始错误，
        // 完全看不出是"配置缺失"。
        return ProviderCatalog.FallbackContextWindow;
    }

    /// <summary>
    /// 请求预留的取值：显式配置 → 默认 <see cref="RequestOutputReserveTokens"/>。
    /// </summary>
    /// <remarks>
    /// <b>不再查能力表</b>：能力表的 <c>MaxOutputTokens</c> 是模型的输出容量上限
    /// （实测占窗口 32%–38%），不是"这次请求期望多少"。用容量当预留会让
    /// 压缩过早触发——200k 窗口按 64k 预留只剩 135k 可用输入，
    /// 白扔三分之二的历史。
    /// <para>
    /// <b>也不需要夹取了</b>：容量退出预算后，就不存在"输出上限吃掉上下文"
    /// 这个问题。原来那条 <c>ClampMaxOutput</c> 是被上游数据逼出来的兜底，
    /// 现在没有存在的理由——留着它只是多一处需要维护的规则。
    /// </para>
    /// </remarks>
    public static int ReadRequestReserve(int contextWindow = 0)
    {
        var raw = Environment.GetEnvironmentVariable("DOUGONG_MAX_OUTPUT");
        var reserve = int.TryParse(raw, out var value) && value > 0
            ? value
            : RequestOutputReserveTokens;

        // 窗口未知（0）时不夹：那是"还没配"，不是"窗口很小"。
        if (contextWindow <= 0) return reserve;

        // **必须夹取**：能力表里有窗口极小的模型——
        // openrouter/openai/gpt-3.5-turbo-0613 的上下文只有 4095，
        // 比固定预留 4096 还小。选它会让 Validate() 抛
        // "上下文窗口配置不可行"，应用起不来。
        //
        // 这是"不取能力表当预留"带来的副作用：躲开了 out == ctx 的
        // 病态大值，却撞上了病态小窗口。两头都要处理。
        //
        // 夹到窗口的 1/4：留四分之三给输入与预留。更小只是输出短些，
        // 不会出错；取到窗口本身就会挤掉输入。
        var ceiling = Math.Max(1, contextWindow / 4);
        return reserve > ceiling ? ceiling : reserve;
    }

    /// <summary>
    /// 按模型名查 SDK 的能力表（models.dev 生成，662 个模型）。
    /// </summary>
    /// <remarks>
    /// <b>遍历 22 家预设找第一个命中的</b>：模型 id 在全表里基本唯一，
    /// 实测单次查询 ≤ 4ms（命中越大越快，未收录要扫完全表）。
    /// <para>
    /// <b>查不到返回 null 而不是抛错</b>：自定义模型、私有部署、刚发布的型号
    /// 都可能不在表里，那是正常情况，由调用方走第三层回退。
    /// </para>
    /// </remarks>
    public static ModelCapabilities? LookupCapabilities(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;

        try
        {
            foreach (var (providerName, _) in ProviderCatalog.AllPresets)
            {
                var caps = ProviderCatalog.FindModelCapabilities(providerName, model);
                if (caps is not null) return caps;
            }
        }
        catch
        {
            // 能力表是锦上添花：查不到不能挡住启动
        }

        return null;
    }

    /// <summary>
    /// 从环境变量读取。
    /// <para>
    /// <b>当前阶段（P0–P2）密钥只从环境变量来。</b>P9 会接入系统凭据管理器，
    /// 那时这一层变成"环境变量优先，否则问凭据通道"。
    /// </para>
    /// <para>
    /// 红线不变：<b>不进配置文件、不进日志、不进会话记录</b>。
    /// SDK 的契约原话是"环境变量<em>或</em>宿主凭据通道"（architecture.md §5.1），
    /// 所以凭据管理器是合规的——别把 CLI 的限制当成通用限制。
    /// </para>
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

        var window = ReadContextWindow(model);

        return new EngineOptions
        {
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = key,
            Protocol = protocol,
            MessagesPath = Environment.GetEnvironmentVariable("DOUGONG_ANTHROPIC_PATH"),
            ContextWindowTokens = window,
            // 夹到不挤占上下文——上游数据里有 out == ctx 的条目，
            // 不夹会让 Validate 抛错、应用起不来
            MaxOutputTokens = ReadRequestReserve(window),
            Workspace = Environment.GetEnvironmentVariable("DOUGONG_WORKSPACE") ?? Environment.CurrentDirectory,
            SessionDirectory = Environment.GetEnvironmentVariable("DOUGONG_SESSION_DIR")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dougong-desktop", "sessions"),
        };
    }

    /// <summary>
    /// 校验容量配置自洽。
    /// </summary>
    /// <remarks>
    /// <b>判定完全走 SDK 的 <see cref="ContextBudgetValidation.Check"/></b>，
    /// 这里不再自己算减法。此前本类手写了一份
    /// <c>ctx - out - 512 &lt;= 0</c> 的判断，其中 <c>512</c> 是从
    /// <c>ContextLimits.OverheadTokens</c> 抄来的副本——那种副本的问题不是
    /// 抄错了值，而是 <b>SDK 改预留值时我们这份不会跟着变</b>，于是
    /// "能过我们这关"与"能过 SDK 那关"变成两个不同的判据。判据有两个真源，
    /// 迟早会分叉，而分叉的表现是"装配通过了，第一次对话才崩"。
    /// <para>
    /// <b>为什么必须在装配时拦</b>：SDK 的 <c>TokenBudget.EffectiveInput</c>
    /// 在预算为负时直接抛 <see cref="ArgumentOutOfRangeException"/>，而压缩发生在
    /// 每轮调模型之前——用户填了「上下文 8000 + 输出 8192」，第一次对话就崩，
    /// 报的错完全看不出是配置问题。
    /// </para>
    /// <para>
    /// <b>窗口未配置是合法状态</b>：<c>IsUsable=true, IsEnabled=false</c>
    /// 表示"配置没问题，只是压缩不启用"。把它当错误拦下来会让"不配窗口
    /// 直接聊天"这种用法不可用。
    /// </para>
    /// </remarks>
    public void Validate()
    {
        // 校验的是**实例上实际的预留值**，不是默认常量——
        // 用常量会让 MaxOutputTokens 被覆盖时校验形同虚设
        var verdict = ContextBudgetValidation.Check(
            ContextWindowTokens,
            MaxOutputTokens,
            TokenBudget.DefaultOverheadTokens);

        if (verdict.IsUsable) return;

        // 文案直接用 SDK 的：它知道自己的预留值，也知道该建议调到多少。
        // 复述一遍只会多一处需要跟着改的地方。
        throw new InvalidOperationException(
            verdict.Hint is { Length: > 0 }
                ? verdict.Detail + Environment.NewLine + verdict.Hint
                : verdict.Detail);
    }

    /// <summary>
    /// 当前的容量校验结论（不抛异常版本，供界面展示）。
    /// </summary>
    /// <remarks>
    /// P6 之后的设置页要显示"当前策略"，它需要的是结论而不是异常。
    /// 与 <see cref="Validate"/> 共用同一次 <c>Check</c> 调用，不另算一套。
    /// </remarks>
    public ContextBudgetVerdict Inspect() => ContextBudgetValidation.Check(
        ContextWindowTokens,
        MaxOutputTokens,
        TokenBudget.DefaultOverheadTokens);
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
            MessagesPath = options.MessagesPath,
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
        int requestOutputReserve,
        CancellationToken cancellationToken = default)
    {
        // 未配置时在建会话这一步失败，而不是在构造时抛——
        // 这样 Host 能起来、UI 能看到"未配置"，用户知道要做什么。
        if (_client is null)
        {
            var detail =
                "未配置模型连接：缺少 DOUGONG_PROVIDER_KEY。" +
                "当前版本密钥从环境变量读（DOUGONG_PROVIDER_KEY），不落盘。" +
                "请设置后重启引擎；P9 之后也可在设置里填，存入系统凭据管理器。";
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

                        // 请求预留 = 这次请求期望的最大输出长度，是**固定值**，
                        // 与模型能力表里的 MaxOutputTokens 无关。
                        // 后者是"模型最多能输出多少"（实测占窗口 32%–38%），
                        // 拿它当预留会把预算吃掉三分之一——压缩因此过早触发。
                        RequestOutputReserve = requestOutputReserve,
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
        dispatcher.Register(PlanTools.Create());
    }

    public void Dispose() => _client?.Dispose();
}
