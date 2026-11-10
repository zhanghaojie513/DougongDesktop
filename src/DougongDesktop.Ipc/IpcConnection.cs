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
        var json = JsonSerializer.Serialize(message, declaredType, DesktopProtocol.Json);
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
    /// <remarks>
    /// <b>"无法解析"有两级，两级都必须被接住</b>：
    /// <list type="number">
    /// <item><b>JSON 语法坏</b>：<c>JsonDocument.Parse</c> 抛。<c>onMalformed</c> 收。</item>
    /// <item><b>语法合法但判别器不认识</b>：<c>Deserialize&lt;ServerEvent&gt;</c> 抛
    /// <see cref="JsonException"/>。这是<b>正常演进</b>——新版引擎发来一个
    /// 旧客户端还没实现的事件类型。SDK 文档把这种情况描述成"忽略"，
    /// 但 <c>System.Text.Json</c> 的多态默认行为是抛异常，实测如此。</item>
    /// </list>
    /// <para>
    /// <b>第二级曾经漏了</b>，后果实测过：一条未知事件让
    /// <c>ReadLoopAsync</c> 抛出、整条连接失效，**之后所有正常事件都收不到**。
    /// 那与本文档开头那句"一条坏行不该让整条连接失效"直接矛盾——
    /// 而且是最坏的那种矛盾：断连后用户看到的是"引擎挂了"，实际引擎好好地
    /// 在发消息。
    /// </para>
    /// <para>
    /// <b>两级都走 <paramref name="onMalformed"/></b>：对客户端而言它们
    /// 是同一件事——收到的这一行没法变成已知类型。区别只在失败发生在
    /// 语法阶段还是类型阶段，而那个区别只对诊断有价值（异常消息里已带上）。
    /// </para>
    /// </remarks>
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

                // 第二级：语法合法、但这条消息不能被识别成已知类型。
                //
                // **只接两种，且都用窄判据**：
                //   ① JsonException 且消息是"未知判别器" —— 新版引擎发来
                //      旧客户端还没实现的事件。这是**正常演进**，跳过继续读。
                //   ② NotSupportedException —— 运行时类型不在多态根上，
                //      同样是"这一行的问题"，不会因为跳过它而丢别的消息。
                //
                // **其余一律冒出去**，包括：
                //   - 缺必需字段的 JsonException：那是真错误（对端删了字段、
                //     或两端版本真的不兼容）。当成"未知类型"静默跳过，
                //     用户看到的是消息无声消失，比抛出来难查得多。
                //   - 处理逻辑自己的 bug（NullReference / InvalidOperation）。
                //
                // 这条边界由 readloop-bug-is-not-swallowed 钉住：改成
                // catch (Exception) 会让它变红。曾经写成宽 catch (JsonException)，
                // 那正好会把"缺字段"当成"未知类型"——SDK 的 TryParse 把这条
                // 分界写明了，判据与本方法一致。
                try
                {
                    await onMessage(element).ConfigureAwait(false);
                }
                catch (JsonException ex) when (IpcSerializer.IsUnknownDiscriminator(ex))
                {
                    onMalformed?.Invoke(line, ex);
                }
                catch (NotSupportedException ex)
                {
                    onMalformed?.Invoke(line, ex);
                }
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
/// <para>
/// <b>为什么这里不用 SDK 的 <c>HostProtocolContract.TryParse</c></b>：
/// 本工程是零依赖的契约层（守卫 <c>ipc-depends-on-nothing-but-bcl</c> 钉住），
/// 而 <c>TryParse</c> 在 <c>Dougong.HostProtocol</c> 里。把它引进来等于
/// 让契约层重新依赖 SDK——那正是这一轮解耦刚消掉的东西。
/// </para>
/// <para>
/// <b>所以"未知判别器"的判据在这里有一份自己的实现</b>，见
/// <see cref="IsUnknownDiscriminator"/>。它与 SDK 那份的判据相同（都用
/// 消息文本里的 <c>unrecognized type discriminator</c>），因为
/// <c>System.Text.Json</c> 没给这个错误单独的类型或错误码，文本是唯一抓手。
/// 这条判据若与 SDK 漂移，表现是"新版的事件被当成坏行回报"或
/// "真错误被当成未知类型静默跳过"——两者都由
/// <c>readloop-*</c> 那组检查盯着。
/// </para>
/// </remarks>
public static class IpcSerializer
{
    public static ClientCommand? ReadCommand(JsonElement element) =>
        element.Deserialize<ClientCommand>(DesktopProtocol.Json);

    public static ServerEvent? ReadEvent(JsonElement element) =>
        element.Deserialize<ServerEvent>(DesktopProtocol.Json);

    /// <summary>
    /// 这条 <see cref="JsonException"/> 是不是"判别器不认识"。
    /// </summary>
    /// <remarks>
    /// <b>靠消息文本判断是不得已</b>：<c>System.Text.Json</c> 没有给
    /// "未知判别器"单独的异常类型或错误码。文本匹配会随 .NET 版本变化，
    /// 所以这里用大小写不敏感的子串匹配，宁可多认几个。
    /// <para>
    /// <b>判据必须窄</b>：只有"未知判别器"该被当成可以跳过的消息。
    /// 缺必需字段、JSON 结构损坏同样是 <see cref="JsonException"/>，
    /// 但那是**真错误**——静默跳过会让用户看到消息无声消失，比抛出难查得多。
    /// </para>
    /// </remarks>
    public static bool IsUnknownDiscriminator(JsonException ex) =>
        ex.Message.Contains("unrecognized type discriminator", StringComparison.OrdinalIgnoreCase);
}
