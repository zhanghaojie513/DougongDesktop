using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DougongDesktop.Ui.Core;

/// <summary>
/// 本地诊断日志（`~/.dougong-desktop/diagnostics.jsonl`）。
/// </summary>
/// <remarks>
/// <b>形状按实施方案 §7.5.7 定死，现在就要对</b>：将来接 OTel 时，<c>kind</c> 直接
/// 对应 <c>ActivitySource</c> 的 name、<c>attributes</c> 直接对应 Span 属性。
/// 字段形状定错后改是破坏性变更——已有的日志文件全部作废。
/// <para>
/// <b>为什么写文件而不是只打控制台</b>：控制台输出在用户机器上没有人看。
/// UI 插件的耗时分布是"第三方插件涌入之前"才拿得到的数据，
/// 错过这段时间就只能靠猜。
/// </para>
/// <para>
/// <b>写失败一律静默</b>：诊断不该影响功能。磁盘满或目录只读时，
/// 界面必须照常工作。
/// </para>
/// </remarks>
public sealed class DiagnosticsLog
{
    /// <summary>日志文件路径。</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dougong-desktop", "diagnostics.jsonl");

    private readonly string _path;
    private readonly object _gate = new();

    public DiagnosticsLog(string? path = null) => _path = path ?? FilePath;

    /// <summary>
    /// 记录一条 span 形状的记录。
    /// </summary>
    /// <param name="kind">llm | tool | plugin | uiPlugin</param>
    /// <param name="name">插件 Id / 工具名 / 模型名。</param>
    /// <param name="startedAt">开始时刻。</param>
    /// <param name="durationMs">耗时（毫秒）。</param>
    /// <param name="attributes">扁平属性。不要嵌套——嵌套后改平是破坏性变更。</param>
    /// <param name="sessionId">会话 Id；没有就留 null。</param>
    public void Record(
        string kind,
        string name,
        DateTimeOffset startedAt,
        double durationMs,
        IReadOnlyDictionary<string, object?>? attributes = null,
        string? sessionId = null)
    {
        var record = new DiagRecord
        {
            // 留空占位：因果链需要父子关系，这是 OTel Span 的核心，
            // 也是指标层永远给不了的东西。将来接 OTel 时这里会有值。
            TraceId = null,
            SpanId = null,
            ParentSpanId = null,

            At = DateTimeOffset.UtcNow,
            SessionId = sessionId,
            TurnId = null,

            Kind = kind,
            Name = name,

            StartedAt = startedAt.ToUniversalTime(),
            DurationMs = durationMs,

            Attributes = attributes,
        };

        try
        {
            var json = JsonSerializer.Serialize(record, JsonOptions);

            // 加锁追加：多个线程同时写会交错成半行，那种坏行会让整个文件
            // 无法按行解析——一条坏行毁掉整份诊断。
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, json + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[diag] 写入失败：{ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 不缩进：一条一行才是 jsonl
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文不转义，方便直接 grep 插件显示名
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>一条诊断记录。字段名与 §7.5.7 的示例逐字对应。</summary>
    private sealed class DiagRecord
    {
        [JsonPropertyName("traceId")] public string? TraceId { get; init; }
        [JsonPropertyName("spanId")] public string? SpanId { get; init; }
        [JsonPropertyName("parentSpanId")] public string? ParentSpanId { get; init; }

        [JsonPropertyName("at")] public DateTimeOffset At { get; init; }
        [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
        [JsonPropertyName("turnId")] public string? TurnId { get; init; }

        [JsonPropertyName("kind")] public required string Kind { get; init; }
        [JsonPropertyName("name")] public required string Name { get; init; }

        [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; init; }
        [JsonPropertyName("durationMs")] public double DurationMs { get; init; }

        [JsonPropertyName("attributes")] public IReadOnlyDictionary<string, object?>? Attributes { get; init; }
    }
}
