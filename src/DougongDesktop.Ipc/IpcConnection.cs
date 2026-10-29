using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DougongDesktop.Ipc;

/// <summary>
/// 一条 NDJSON 连接：一行一条 JSON 消息，UTF-8，无长度前缀。
/// </summary>
/// <remarks>
/// 选 NDJSON over TCP 而不是命名管道：跨平台一套代码，且能 `nc` 上去手工调试。
/// 本地绑定 127.0.0.1 + 随机端口，端口号由宿主写成端点文件，客户端读取——
/// 不写死端口，避免多实例与端口占用。安全边界是"仅回环 + 端点文件仅当前用户可读"，
/// 这不是对同用户恶意进程的沙箱（与 docs 中的边界说明一致）。
/// </remarks>
public sealed class IpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _buffer = new byte[8192];

    private IpcConnection(Stream stream) => _stream = stream;

    public static async Task<IpcConnection> ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        var (host, port) = ParseEndpoint(endpoint);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new IpcConnection(client.GetStream());
    }

    public static IpcConnection FromStream(Stream stream) => new(stream);

    /// <summary>
    /// 发送一条消息。
    /// </summary>
    /// <remarks>
    /// <b>必须按实际声明的基类类型序列化</b>（<see cref="ServerEvent"/> / <see cref="ClientCommand"/>），
    /// 否则多态判别字段会整条丢失。
    /// <para>
    /// 这不是风格问题：<c>SendAsync(new EngineStatusEvent {...})</c> 时泛型 <c>T</c>
    /// 被推断成<b>派生</b>类型，而 <c>[JsonDerivedType]</c> 的 discriminator 只在
    /// 以基类为静态类型序列化时才写出。结果是对端收到一条没有 <c>method</c> 字段的
    /// 裸对象，无法判别类型——实测到的现象就是 "Read unrecognized type discriminator"。
    /// </para>
    /// <para>
    /// 因此这里的重载接收基类，调用方不需要自己记得转型。
    /// </para>
    /// </remarks>
    public Task SendAsync(ServerEvent message, CancellationToken ct = default) =>
        SendRawAsync(message, typeof(ServerEvent), ct);

    /// <summary>发送一条命令。理由同 <see cref="SendAsync(ServerEvent, CancellationToken)"/>。</summary>
    public Task SendAsync(ClientCommand message, CancellationToken ct = default) =>
        SendRawAsync(message, typeof(ClientCommand), ct);

    private async Task SendRawAsync(object message, Type declaredType, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message, declaredType, IpcProtocol.Json);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 逐条读取消息并交给 <paramref name="onMessage"/>。
    /// 无法解析的行直接跳过并回报——一条坏行不该让整条连接失效。
    /// </summary>
    public async Task ReadLoopAsync(
        Func<JsonElement, Task> onMessage,
        Action<string, Exception>? onMalformed = null,
        CancellationToken ct = default)
    {
        var pending = new List<byte>();
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            if (read == 0) return;

            pending.AddRange(_buffer.AsSpan(0, read).ToArray());

            while (true)
            {
                var newline = pending.IndexOf((byte)'\n');
                if (newline < 0) break;

                var line = Encoding.UTF8.GetString(pending.ToArray(), 0, newline);
                pending.RemoveRange(0, newline + 1);

                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonElement element;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    element = document.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    onMalformed?.Invoke(line, ex);
                    continue;
                }

                await onMessage(element).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0)
            throw new FormatException($"端点格式应为 host:port，实际为 '{endpoint}'。");

        return (endpoint[..separator], int.Parse(endpoint[(separator + 1)..]));
    }
}

/// <summary>
/// 反序列化辅助：按 method/kind 判别字段还原成正确的派生类型。
/// </summary>
/// <remarks>
/// System.Text.Json 的多态反序列化要求基类带 [JsonDerivedType]，本项目已在协议里声明。
/// 但基类不能是 abstract 且无参构造缺失——所以这里统一走基类类型。
/// </remarks>
public static class IpcSerializer
{
    public static ClientCommand? ReadCommand(JsonElement element) =>
        element.Deserialize<ClientCommand>(IpcProtocol.Json);

    public static ServerEvent? ReadEvent(JsonElement element) =>
        element.Deserialize<ServerEvent>(IpcProtocol.Json);
}
