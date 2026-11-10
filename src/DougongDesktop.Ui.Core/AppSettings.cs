using System.Text.Json;
using System.Text.Json.Serialization;

namespace DougongDesktop.Ui.Core;

/// <summary>
/// 界面偏好的本地持久化（`~/.dougong-desktop/ui.json`）。
/// </summary>
/// <remarks>
/// <b>这个文件里不许出现任何密钥、端点、会话内容</b>。它只存"用户把拖拽条拖到哪了"
/// 这类丢了也无所谓、但丢了很烦的东西。
/// <para>
/// <b>为什么只存 Sizes 而不存整个布局</b>：`Sizes` 是 Arc 定义的双向百分比数组，
/// 已经能完整还原三栏比例。存面板宽度像素值反而会在换 DPI 或改窗口大小时错位。
/// </para>
/// <para>
/// <b>读写失败一律不抛</b>：配置文件损坏或目录只读时，界面应该用默认布局起来，
/// 而不是打不开。这与"加载失败不能让 App 起不来"是同一条原则。
/// </para>
/// </remarks>
public sealed class AppSettings
{
    /// <summary>配置目录，与引擎的端点文件同一个地方。</summary>
    public static string DirectoryPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dougong-desktop");

    /// <summary>存储里的名字（不含目录）。</summary>
    /// <remarks>
    /// 与 <see cref="FilePath"/> 分开：实现层只需要"名字"，
    /// 完整路径是桌面实现的事（web 端没有路径这个概念）。
    /// </remarks>
    public const string FileName = "ui.json";

    /// <summary>界面偏好文件路径（桌面实现用）。</summary>
    /// <remarks>
    /// 名字带 <c>ui</c> 前缀是刻意的：将来如果加了 provider 配置，
    /// 它会写进 <c>providers.json</c>，两者不该混在一个文件里——
    /// 混了就很难保证"界面文件里没有密钥"这条能靠肉眼审计。
    /// </remarks>
    public static string FilePath { get; } = System.IO.Path.Combine(DirectoryPath, "ui.json");

    /// <summary>
    /// 三栏宽度百分比。
    /// </summary>
    /// <remarks>
    /// null 表示"没存过"，用 Arc 面板自己声明的 <c>DefaultSize</c>。
    /// 空数组和 null 要区分：空数组是坏数据，应当忽略。
    /// </remarks>
    [JsonPropertyName("paneSizes")]
    public double[]? PaneSizes { get; init; }

    /// <summary>
    /// 右栏宽度（像素）。null 表示没存过，用 <c>Columns.RightbarDefault</c>。
    /// </summary>
    /// <remarks>
    /// <b>存像素而不是百分比</b>：dsh 的列宽语义就是像素
    /// （见 <c>Columns.Compute</c> 的注释）。存百分比会在换窗口尺寸后漂移。
    /// <para>
    /// <b>存"夹取后的实际值"</b>：用户拖到 200 但被 <c>RightbarMin</c> 挡住时，
    /// 存下来的应该是夹取后的结果，重启后才和当时看到的一致。
    /// </para>
    /// </remarks>
    [JsonPropertyName("rightbarWidth")]
    public double? RightbarWidth { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 用户可能手改这个文件；不认识字段就忽略，不要让整个文件作废
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>读取。不存在或读坏了都返回空设置。</summary>
    /// <param name="store">存储实现。null 时用桌面实现（文件）。</param>
    /// <remarks>
    /// <b>读失败一律回默认值</b>：设置坏了应该用默认布局起来，而不是开不了。
    /// 这条与"加载失败不能让 App 起不来"是同一条原则。
    /// </remarks>
    /// <param name="store">存储实现。必须显式传入——本类不该知道"默认存到哪",
    /// 那是平台的事。</param>
    public static AppSettings Load(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            if (store.Read(FileName) is not { } json) return new AppSettings();

            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                         ?? new AppSettings();

            // **两个字段各自独立校验**。
            //
            // 第一版把 RightbarWidth 的校验嵌在 "PaneSizes 有效" 的分支里，
            // 结果是**只存宽度、没存 PaneSizes 时整份设置被丢弃**——
            // 拖动列宽永远还原不了。检查抓到后才发现（正常值 576 读回来是 null）。
            //
            // PaneSizes：单栏为 0 合法（折叠），全为 0 说明文件坏了。
            var paneSizesOk = loaded.PaneSizes is { Length: 3 } sizes
                              && sizes.All(s => s >= 0 && !double.IsNaN(s) && !double.IsInfinity(s))
                              && sizes.Any(s => s > 0);

            // 右栏宽度：0/负数会让右栏消失，NaN/Infinity 会让布局崩。
            // 坏就单独丢弃这个字段，不牵连 PaneSizes。
            var width = loaded.RightbarWidth;
            var widthOk = width is { } w
                          && !double.IsNaN(w) && !double.IsInfinity(w) && w > 0;

            if (!paneSizesOk && !widthOk) return new AppSettings();

            return new AppSettings
            {
                PaneSizes = paneSizesOk ? loaded.PaneSizes : null,
                RightbarWidth = widthOk ? width : null,
            };
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>写入。失败静默——存不下布局不该打断用户。</summary>
    /// <param name="store">存储实现。null 时用桌面实现（文件）。</param>
    public void Save(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.Write(FileName, JsonSerializer.Serialize(this, JsonOptions));
    }
}
