using System.Text.Json;

namespace DougongDesktop.AgentHost;

/// <summary>
/// 端点发现：宿主写入 <c>endpoint.json</c>，客户端读取后连接。
/// </summary>
/// <remarks>
/// 不写死端口：多实例、端口占用、重启换端口都会让写死的端口变成"连不上但不知道原因"。
/// <para>
/// 端点的三件事：端口、<c>hostInstanceId</c>、启动时间。
/// 客户端用 <c>hostInstanceId</c> 判断"这是不是同一台引擎"——
/// 只比对 PID 不行，PID 会被复用。
/// </para>
/// <para>
/// 残留文件是正常现象（进程被杀时没机会清理）。客户端连不上就当作"没有引擎"，
/// 然后自己启动一个；不能因为文件存在就认为引擎活着。
/// </para>
/// </remarks>
internal static class EndpointFile
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dougong-desktop");

    public static string EndpointPath => System.IO.Path.Combine(DirectoryPath, "endpoint.json");

    public static void Write(int port)
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);

        var payload = new
        {
            port,
            hostInstanceId = Guid.NewGuid().ToString("N"),
            startedAt = DateTimeOffset.UtcNow,
            pid = Environment.ProcessId,
            protocolVersion = Ipc.IpcProtocol.Version,
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        // 先写临时文件再移动：避免客户端读到写了一半的内容。
        var temp = EndpointPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, EndpointPath, overwrite: true);
    }

    public static void Delete()
    {
        try
        {
            File.Delete(EndpointPath);
        }
        catch (IOException)
        {
            // 删不掉也不影响：残留文件本来就是允许的。
        }
    }
}
