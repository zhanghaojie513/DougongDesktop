using DougongDesktop.AgentHost;
using DougongDesktop.Framework;

// AgentHost：内嵌 Dougong SDK 的引擎进程。
//
// 与 UI 的关系：这个进程不知道 UI 长什么样，UI 也不知道 SDK 长什么样。
// 两边只认 DougongDesktop.Ipc 里的消息类型。
//
// 启动方式：
//   dougong-desktop-host                  ← 默认端口（0 = 由 OS 分配，写进 endpoint.json）
//   dougong-desktop-host --port 45123     ← 固定端口，便于调试
//   dougong-desktop-host --workspace C:\work\myProject

var port = 0;
var workspace = Environment.CurrentDirectory;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--workspace" when i + 1 < args.Length:
            workspace = args[++i];
            break;
        case "--help":
            Console.Error.WriteLine(
                """
                dougong-desktop-host —— Dougong Desktop 引擎宿主

                用法：
                  dougong-desktop-host [--port <端口>] [--workspace <目录>]

                环境变量（当前阶段密钥只从这里读，不落盘）：
                  DOUGONG_PROVIDER_BASE      服务地址，如 https://api.deepseek.com
                  DOUGONG_PROVIDER_MODEL     模型名
                  DOUGONG_PROVIDER_KEY       密钥
                  DOUGONG_PROVIDER_PROTOCOL  openai（默认）/ anthropic / responses
                  DOUGONG_SESSION_DIR        会话目录，默认 ~/.dougong-desktop/sessions
                """);
            return 0;
    }
}

var options = EngineOptions.FromEnvironment();
if (options is null)
{
    Console.Error.WriteLine(
        """
        [host] 缺少 provider 配置。需要：
          DOUGONG_PROVIDER_BASE      服务地址
          DOUGONG_PROVIDER_MODEL     模型名
          DOUGONG_PROVIDER_KEY       密钥

        可选（启用上下文压缩）：
          DOUGONG_CONTEXT_WINDOW     模型窗口 token 数；不设则按模型名推断
          DOUGONG_MAX_OUTPUT         单次输出上限，默认 4096

        引擎会在没有配置的情况下启动，但任何对话都会失败。
        这样 UI 仍然能启动并显示"未配置"状态，而不是整个崩掉。
        """);
}

// 未配置时不构造真实连接器——SessionComposer 会在缺密钥时抛错，
// 那会让 Host 直接退出，UI 连不上。用占位配置让引擎活着，
// 代价是任何会话都会在创建时失败并回报明确原因。
options ??= new EngineOptions
{
    BaseUrl = "https://invalid.invalid",
    Model = "(未配置)",
    ApiKey = null,
    ContextWindowTokens = 0,
    Workspace = workspace,
};

// 容量配置自洽性校验：SDK 的预算计算在负数时直接抛
// ArgumentOutOfRangeException，而那发生在每轮调模型之前——
// 用户看到的是"第一次对话就崩"，完全看不出是配置问题。
// 所以在这里提前拦下，给出能看懂的话。
try
{
    options.Validate();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"[host] 配置错误：{ex.Message}");
    return 2;
}

using var composer = new SessionComposer(options);

await using var server = new IpcServer(composer, options, workspace);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

try
{
    await server.RunAsync(port, shutdown.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[host] 致命错误：{ex.GetType().Name}: {ex.Message}");
    return 1;
}

return 0;
