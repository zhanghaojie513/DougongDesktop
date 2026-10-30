using System.Text;
using DougongDesktop.Ui.Core;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 把当前装配导出成可读文本（<c>--dump-config</c>）。
/// </summary>
/// <remarks>
/// <b>为什么需要它</b>：排查"某个 slot 为什么是空的"时，运行时 dump 只能告诉你
/// "现在是空的"，回答不了"本该装什么"。静态清单能看出"清单里根本没有这条"还是
/// "有但被覆盖了"。
/// <para>
/// 借鉴 dsh：它的启动与 <c>--dump-config</c> 走同一条 <c>applyEntryPatches</c> 路径，
/// 所以 dump 出来的就是实际跑的。这里同理——<see cref="Describe"/> 读的是
/// **与注册同一份** <see cref="BuiltInModules.All"/>，不是另抄一份。
/// </para>
/// </remarks>
public static class ConfigDump
{
    /// <summary>
    /// 生成装配清单。
    /// </summary>
    /// <param name="slots">当前 slot 注册表；null 表示只导出内置清单。</param>
    /// <param name="uiPluginResults">
    /// UI 插件加载结果；null 表示没加载插件。
    /// </param>
    public static string Describe(
        SlotRegistry? slots = null,
        IReadOnlyList<ExtensionLoadResult>? uiPluginResults = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Dougong Desktop 装配清单");
        sb.AppendLine();

        // ── slot 声明 ──
        sb.AppendLine("## Slot");
        sb.AppendLine();
        sb.AppendLine("| key | kind |");
        sb.AppendLine("|---|---|");

        foreach (var (key, kind) in DeclaredSlots)
            sb.AppendLine($"| `{key}` | {kind} |");

        sb.AppendLine();

        // ── 内置模块（顺序即注册顺序） ──
        sb.AppendLine("## 内置模块（按注册顺序）");
        sb.AppendLine();
        sb.AppendLine("| # | id | slot | key | 类型 | 说明 |");
        sb.AppendLine("|---|---|---|---|---|---|");

        var index = 1;
        foreach (var m in BuiltInModules.All)
        {
            // 对话区在清单里标的是 Placeholder（占位者角色），但它实际装的是
            // 真实消息流。照着枚举印会让人以为它还是个空框——dump 的意义就是
            // 反映"实际跑的"，所以这里如实标注。
            var note = m.Id == "builtin.conversation"
                ? "真实消息流（已实现）"
                : m.Kind == BuiltInModuleKind.Placeholder
                    ? $"{m.Title} — {m.Subtitle}（占位）"
                    : m.Kind.ToString();

            sb.AppendLine(
                $"| {index++} | `{m.Id}` | `{m.SlotKey}` | {Fmt(m.Key)} | {m.Kind} | {note} |");
        }

        sb.AppendLine();

        // ── UI 插件 ──
        if (uiPluginResults is { Count: > 0 })
        {
            sb.AppendLine("## UI 插件");
            sb.AppendLine();
            sb.AppendLine("| dll | 状态 | id | 原因 |");
            sb.AppendLine("|---|---|---|---|");

            foreach (var r in uiPluginResults)
            {
                sb.AppendLine(
                    $"| {r.AssemblyFile} | {r.Status} | {Fmt(r.ExtensionId)} | {Fmt(r.Reason)} |");
            }

            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## UI 插件");
            sb.AppendLine();
            sb.AppendLine("（未加载。设 `DOUGONG_UI_PLUGINS` 指向插件目录后重跑）");
            sb.AppendLine();
        }

        // ── 运行时状态 ──
        if (slots is not null)
        {
            sb.AppendLine("## 运行时解析结果");
            sb.AppendLine();
            sb.AppendLine("| slot | 解析结果 |");
            sb.AppendLine("|---|---|");

            foreach (var (key, kind) in DeclaredSlots)
            {
                string result = kind switch
                {
                    SlotKind.List => $"{slots.GetList(key).Count} 条",
                    SlotKind.Keyed => DescribeKeyed(slots),
                    _ => slots.Resolve(key) is null ? "**空**" : "有占位者",
                };

                sb.AppendLine($"| `{key}` | {result} |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// slot 声明表。
    /// </summary>
    /// <remarks>
    /// <b>与 <c>AppShell.DeclareSlots</c> 必须一致</b>——这里是文档性的重复，
    /// 但因为只有 5 项且不常变，比为此抽一个共享类型便宜。
    /// 不一致时 <c>dump-config</c> 的运行时段会显示"**空**"，能立刻看出来。
    /// </remarks>
    private static readonly (string Key, SlotKind Kind)[] DeclaredSlots =
    [
        (ReservedSlots.Sidebar, SlotKind.Single),
        (ReservedSlots.Main, SlotKind.Keyed),
        (ReservedSlots.RightBar, SlotKind.Single),
        (ReservedSlots.Bottom, SlotKind.List),
        (ReservedSlots.Overlay, SlotKind.List),
    ];

    private static string DescribeKeyed(SlotRegistry slots)
    {
        // main 面板的三个 key 是已知的，逐个探
        var keys = new[] { ReservedSlots.Conversation, "plugins", "settings" };
        var present = keys.Where(k => slots.HasKey(ReservedSlots.Main, k)).ToArray();

        return present.Length == 0
            ? "**无任何 key**"
            : string.Join("、", present.Select(k => $"`{k}`"));
    }

    private static string Fmt(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;
}
