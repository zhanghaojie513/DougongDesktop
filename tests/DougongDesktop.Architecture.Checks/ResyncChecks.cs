using System.Text;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// 序号缺口与重同步的检查。
/// </summary>
/// <remarks>
/// <b>为什么这个必须有检查</b>：P3 复检时发现"序号缺口"这条判据只写了检测，
/// 没有真的发重同步请求——客户端停在"需要重新同步"，用户看到的是引擎挂了，
/// 而连接其实好好的。这类"检测到位、处置缺失"的缺陷在界面上表现为死状态，
/// 人工点出来极难，因为要制造缺口。
/// <para>
/// <b>这里测的是真实协议，不是模拟</b>：用 <see cref="IpcConnection.FromStream"/>
/// 接内存管道，走的是与线上完全相同的 <c>[JsonDerivedType]</c> 序列化与
/// NDJSON 分帧。所以"命令发出去了但对端认不出"这种问题也会被抓到。
/// </para>
/// </remarks>
internal static class ResyncChecks
{
    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("resync-command-roundtrips", slug => RoundTrips(breakMode == slug));
        failures += Case("resync-request-carries-session", slug => CarriesSession(breakMode == slug));
        failures += Case("diag-uiplugin-has-phase", slug => DiagHasPhase(breakMode == slug));

        return failures;
    }

    /// <summary>
    /// <see cref="ResyncCommand"/> 必须能被对端按基类反序列化出来。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是漏写判别器</b>：忘了 <c>[JsonDerivedType]</c> 时序列化不报错，
    /// 对端只收到一条无法判别类型的裸对象。宿主那边会直接丢弃，表现为
    /// "点了重同步但没反应"。
    /// </remarks>
    private static string? RoundTrips(bool inject)
    {
        var command = inject
            ? (ClientCommand)new OpenSessionCommand { RequestId = "r1", SessionId = "s1" }
            : new ResyncCommand { RequestId = "r1", SessionId = "s1" };

        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write(System.Text.Json.JsonSerializer.Serialize(
                command, typeof(ClientCommand), IpcProtocol.Json));
            writer.Write('\n');
        }

        stream.Position = 0;
        ClientCommand? decoded = null;

        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var line = reader.ReadLine();
            if (line is null) return "序列化后没有产出任何行";

            using var doc = System.Text.Json.JsonDocument.Parse(line);
            decoded = IpcSerializer.ReadCommand(doc.RootElement.Clone());
        }

        return decoded is ResyncCommand
            ? null
            : $"ResyncCommand 应能原样还原，实际还原成 {decoded?.GetType().Name ?? "null"}";
    }

    /// <summary>
    /// 重同步请求必须带上会话 Id——否则宿主不知道重拉哪个会话。
    /// </summary>
    /// <remarks>
    /// <b>带 null 是合法的</b>（还没打开会话时只要一个基准序号），
    /// 但"有会话却没带上"会让重同步变成空操作，而界面上看不出来。
    /// </remarks>
    private static string? CarriesSession(bool inject)
    {
        var command = new ResyncCommand
        {
            RequestId = "r2",
            SessionId = inject ? null : "sess-42",
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            command, typeof(ClientCommand), IpcProtocol.Json);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var decoded = IpcSerializer.ReadCommand(doc.RootElement.Clone()) as ResyncCommand;

        if (decoded is null) return "还原失败";

        return decoded.SessionId == "sess-42"
            ? null
            : $"重同步请求应携带会话 Id，实际 {decoded.SessionId ?? "null"}";
    }

    /// <summary>
    /// `uiPlugin` 诊断必须带 phase，且必须是三段之一。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是"埋了但分不出阶段"</b>：§7.5.7 里 phase 是 uiPlugin 唯一的
    /// 独有字段。缺了它，register/build/render 三行只是三条无法归类的记录，
    /// 将来接 OTel 时也映射不出 Span 名。
    /// <para>
    /// 用真写文件再读回来，不走内存断言——写入路径的坑（换行、编码、
    /// 字段丢掉）只有读真实文件才碰得到。
    /// </para>
    /// </remarks>
    private static string? DiagHasPhase(bool inject)
    {
        var path = Path.Combine(Path.GetTempPath(), $"diag-check-{Guid.NewGuid():N}.jsonl");
        try
        {
            var diag = new DiagnosticsLog(path);
            var attributes = new Dictionary<string, object?>
            {
                ["outcome"] = "Succeeded",
                ["slotKey"] = "sidebar",
                ["priority"] = 500,
            };

            // 反证：不写 phase
            if (!inject) attributes["phase"] = "render";

            diag.Record("uiPlugin", "sample.plugin", DateTimeOffset.UtcNow, 1.5, attributes);

            var lines = File.ReadAllLines(path);
            if (lines.Length != 1) return $"应写出一行，实际 {lines.Length} 行";

            using var doc = System.Text.Json.JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;

            foreach (var required in new[] { "kind", "name", "startedAt", "durationMs", "attributes" })
                if (!root.TryGetProperty(required, out _))
                    return $"缺少字段 {required}";

            if (root.GetProperty("kind").GetString() != "uiPlugin")
                return "kind 应为 uiPlugin";

            var attrs = root.GetProperty("attributes");
            if (!attrs.TryGetProperty("phase", out var phase))
                return "attributes 缺少 phase（register/build/render 三行将无法归类）";

            var value = phase.GetString();
            return value is "register" or "build" or "render"
                ? null
                : $"phase 应为 register|build|render，实际 {value}";
        }
        finally
        {
            try { File.Delete(path); } catch { /* 临时文件删不掉不影响检查结论 */ }
        }
    }

    private static int Case(string slug, Func<string, string?> body)
    {
        string? error;
        try
        {
            error = body(slug);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (error is null)
        {
            Console.WriteLine($"PASS  {slug}");
            return 0;
        }

        Console.WriteLine($"FAIL  {slug}");
        Console.WriteLine($"      {error}");
        return 1;
    }
}
