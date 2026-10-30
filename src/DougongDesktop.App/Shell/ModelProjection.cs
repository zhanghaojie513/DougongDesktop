using DougongDesktop.Ipc;
using TKCode.Arc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 把 IPC 的 <see cref="ModelInfo"/> 转成 Arc 的 <see cref="AiModel"/>。
/// </summary>
/// <remarks>
/// <b>为什么这个转换在 App 层</b>：架构守卫禁止两个方向——
/// App 不得引用 SDK（<c>app-does-not-reference-sdk</c>），
/// Framework 不得引用 Arc（<c>sdk-side-has-no-ui-dependencies</c>）。
/// 所以"SDK 数据 → UI 数据"这条路只能走 IPC 传到 App，再由 App 转。
/// <para>
/// 一开始我把这段放在 Framework，立刻被守卫拦下。那个拦截是对的：
/// 允许它以后，Framework 就会开始散落 UI 类型，最终变成"UI 逻辑住在
/// 框架层"——那正是换框架时要重写一大片的原因。
/// </para>
/// <para>
/// <b>只填有真实来源的字段</b>：上下文窗口与输出上限来自能力表；
/// 价格、营销文案、effort 档位我们不传（SDK 也没有），留空。
/// 编造这些会让用户看到假信息，而假信息比空白更糟。
/// </para>
/// </remarks>
public static class ModelProjection
{
    /// <summary>把模型清单转成 Arc 的 <see cref="AiModel"/> 列表。</summary>
    public static IReadOnlyList<AiModel> ToAiModels(IReadOnlyList<ModelInfo> models) =>
        [.. models.Select(ToAiModel)];

    /// <summary>把 provider 标识列表转成分组头。</summary>
    /// <remarks>
    /// 预设里没有 provider 的显示名，直接用稳定标识。
    /// 猜一个"Anthropic"式的名字会猜错（<c>minimax</c> 的正确写法是
    /// <c>MiniMax</c>），而错的名字比 id 更难认。
    /// </remarks>
    public static IReadOnlyList<AiModelProvider> ToProviders(IReadOnlyList<string> providers) =>
    [
        .. providers.Select(id => new AiModelProvider { Id = id, Name = id }),
    ];

    private static AiModel ToAiModel(ModelInfo m) => new()
    {
        Id = m.Id,
        Name = m.Id,
        ProviderId = m.ProviderId,

        ContextTokens = m.ContextWindow > 0 ? m.ContextWindow : null,
        MaxOutputTokens = m.MaxOutputTokens > 0 ? m.MaxOutputTokens : null,

        // DisplayName 是只读派生（ShortName ?? Name），不能直接赋。
        // SupportsEffort 也是只读（由 EffortLevels 是否为空推出）——
        // 而我们不传 effort 档位（SDK 没给），所以它自然是 false。
        // 这意味着支持思考的模型暂时不显示 effort 选择器：
        // 宁可不显示，也不要给一个选不了东西的控件。
    };
}
