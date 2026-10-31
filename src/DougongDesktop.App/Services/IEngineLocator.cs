using System.Text.Json;

namespace DougongDesktop.App.Services;

/// <summary>
/// 引擎端点的发现方式。
/// </summary>
/// <remarks>
/// <b>为什么这不是"读文件"的抽象</b>：端点怎么发现，桌面和 web 的答案
/// 完全不同——桌面读 <c>endpoint.json</c>，web 端要么由页面 URL 给出
/// （<c>?port=</c>），要么固定在某个已知地址。把这件事抽象成"读文件"
/// 会让 web 实现被迫假装有个文件。
/// <para>
/// <b>它也负责"引擎不在时怎么办"</b>：桌面可以拉起一个子进程，
/// web 不能（浏览器没有这个能力）。所以接口给的是
/// <see cref="TryStartEngineAsync"/> 而不是"启动进程"。
/// </para>
/// </remarks>
public interface IEngineLocator
{
    /// <summary>
    /// 找到可用端点。找不到返回 null。
    /// </summary>
    /// <remarks>
    /// <b>只做"发现"，不负责"启动"</b>：调用方拿到 null 后自己决定
    /// 要不要 <see cref="TryStartEngineAsync"/>。分开的理由是——
    /// 有时候引擎文件残留但进程已经死了，那时先启动再发现更合理。
    /// </remarks>
    Task<EngineEndpoint?> DiscoverAsync(CancellationToken ct = default);

    /// <summary>
    /// 尝试启动引擎。返回是否成功发起了启动（不代表已就绪）。
    /// </summary>
    /// <remarks>
    /// <b>web 实现直接返回 false</b>，因为浏览器启动不了本地进程。
    /// 那时调用方应当提示"请先启动引擎"，而不是静默等待。
    /// </remarks>
    Task<bool> TryStartEngineAsync(CancellationToken ct = default);
}

/// <summary>端点信息。</summary>
/// <param name="Host">主机。桌面固定 127.0.0.1。</param>
/// <param name="Port">端口。</param>
/// <param name="ProtocolVersion">引擎声明的协议版本。</param>
public sealed record EngineEndpoint(string Host, int Port, int? ProtocolVersion);

/// <summary>
/// 桌面实现：读 <c>~/.dougong-desktop/endpoint.json</c>，必要时拉起 AgentHost。
/// </summary>
/// <remarks>
/// <b>这段逻辑从 <see cref="EngineClient"/> 里搬出来的</b>：原来它把
/// "读端点文件""找 AgentHost 可执行文件""启动子进程""建 TCP 连接"
/// 混在一个类里。前两件是平台相关，后两件是平台无关——
/// 混在一起会让 web 端要么整体重写，要么被迫实现用不上的进程管理。
/// </remarks>
public sealed class FileEngineLocator : IEngineLocator
{
    private readonly string _endpointPath;
    private readonly Action<string> _log;

    /// <summary>AgentHost 可执行文件路径；null 表示按规则推断。</summary>
    public string? ExecutablePath { get; set; }

    public FileEngineLocator(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        _endpointPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dougong-desktop", "endpoint.json");
    }

    public Task<EngineEndpoint?> DiscoverAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_endpointPath)) return Task.FromResult<EngineEndpoint?>(null);

            var json = File.ReadAllText(_endpointPath);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("port", out var portElement))
                return Task.FromResult<EngineEndpoint?>(null);

            int? version = doc.RootElement.TryGetProperty("protocolVersion", out var v)
                ? v.GetInt32()
                : null;

            return Task.FromResult<EngineEndpoint?>(
                new EngineEndpoint("127.0.0.1", portElement.GetInt32(), version));
        }
        catch (Exception ex)
        {
            _log($"[端点] 读取失败：{ex.Message}");
            return Task.FromResult<EngineEndpoint?>(null);
        }
    }

    public Task<bool> TryStartEngineAsync(CancellationToken ct = default)
    {
        var exe = ExecutablePath ?? FindAgentHost();
        if (exe is null)
        {
            _log("[端点] 找不到 AgentHost 可执行文件");
            return Task.FromResult(false);
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                // 不传命令行密钥：那个会出现在 ps 输出里。
                // 子进程继承本进程的环境变量，密钥在里面。
                CreateNoWindow = true,
            });

            _log($"[端点] 已启动 {Path.GetFileName(exe)}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _log($"[端点] 启动失败：{ex.Message}");
            return Task.FromResult(false);
        }
    }

    /// <summary>按"与 UI 同目录"或开发期的已知相对路径找 AgentHost。</summary>
    private string? FindAgentHost()
    {
        var exe = OperatingSystem.IsWindows() ? "DougongDesktop.AgentHost.exe" : "DougongDesktop.AgentHost";

        var beside = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(beside)) return beside;

        // 开发期：bin 目录下没有 AgentHost，往上找仓库结构
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var config in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(
                    dir.FullName, "src", "DougongDesktop.AgentHost", "bin", config, "net10.0", exe);

                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }
}
