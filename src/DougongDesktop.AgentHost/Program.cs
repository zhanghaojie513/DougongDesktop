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

                环境变量（密钥只从环境变量读，不落盘）：
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

        引擎会在没有配置的情况下启动，但任何对话都会失败。
        这样 UI 仍然能启动并显示"未配置"状态，而不是整个崩掉。
        """);
}

using var composer = new SessionComposer(options ?? new EngineOptions
{
    BaseUrl = "https://invalid.invalid",
    Model = "(未配置)",
    Workspace = workspace,
});

await using var server = new IpcServer(composer, workspace);

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
