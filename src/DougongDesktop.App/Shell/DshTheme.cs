using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using DougongDesktop.Ui.Core.Theme;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 把 dsh 的 <see cref="DshTokens"/> 应用到界面。
/// </summary>
/// <remarks>
/// <b>为什么需要这一层</b>：`DshTokens` 只是 <see cref="Color"/> 常量，
/// 界面不读它们就什么也不会变。生成了不接等于没做。
/// <para>
/// <b>与 Arc 主题的关系</b>：Arc 自己有一套 token（`--arc-*`），负责控件内部
/// 的描边、圆角、动效。这两套**不冲突但会打架**——同一个"容器底色"，
/// Arc 用它的 `Surface`，dsh 用 `bg-layer-2`。当前策略是：
/// <list type="bullet">
/// <item>控件内部细节（按钮、输入框、滚动条）交给 Arc，它做得比我们细</item>
/// <item>**页面级底色、侧边栏填充、主区背景**用 dsh 的值，
/// 因为那是"看起来像不像 dsh"最直接的部分</item>
/// </list>
/// 两者色值不同时，以 dsh 为准的那几块会明显更接近 dsh 的观感。
/// 后续可以把 Arc 的 token 也映射过去（Arc 支持 token 覆盖），但那是 P8a 之后的事。
/// </para>
/// </remarks>
public static class DshTheme
{
    /// <summary>当前是否是深色。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>dsh 的页面底色。</summary>
    public static Color PageBackground => IsDark
        ? DshTokens.Dark.DswAliasBgBase
        : DshTokens.Light.DswAliasBgBase;

    /// <summary>主文字色。</summary>
    public static Color LabelPrimary => IsDark
        ? DshTokens.Dark.DswAliasLabelPrimary
        : DshTokens.Light.DswAliasLabelPrimary;

    /// <summary>二级容器的底色。</summary>
    public static Color Layer2 => IsDark
        ? DshTokens.Dark.DswAliasBgLayer2
        : DshTokens.Light.DswAliasBgLayer2;

    /// <summary>三级容器底色（悬停、选中行）。</summary>
    public static Color Layer3 => IsDark
        ? DshTokens.Dark.DswAliasBgLayer3
        : DshTokens.Light.DswAliasBgLayer3;

    /// <summary>最轻的描边。</summary>
    public static Color BorderL1 => IsDark
        ? DshTokens.Dark.DswAliasBorderL1
        : DshTokens.Light.DswAliasBorderL1;

    /// <summary>第四级描边（最重，用于分隔主区与侧栏）。</summary>
    public static Color BorderL4 => IsDark
        ? DshTokens.Dark.DswAliasBorderL4
        : DshTokens.Light.DswAliasBorderL4;

    /// <summary>品牌主色。</summary>
    public static Color BrandPrimary => IsDark
        ? DshTokens.Dark.DswAliasBrandPrimary
        : DshTokens.Light.DswAliasBrandPrimary;

    /// <summary>错误色（dsh 叫 state-error-primary，不叫 danger）。</summary>
    public static Color Danger => IsDark
        ? DshTokens.Dark.DswAliasStateErrorPrimary
        : DshTokens.Light.DswAliasStateErrorPrimary;

    /// <summary>
    /// 按系统主题初始化，并挂上跟随变化的监听。
    /// </summary>
    /// <remarks>
    /// <b>跟随 <c>RequestedThemeVariant</c> 而不是自己读注册表</b>：
    /// Avalonia 已经在跨平台地解析"系统当前是深色还是浅色"，
    /// 自己再读一遍会得到两套可能不一致的判断。
    /// </remarks>
    public static void Initialize(Application app)
    {
        IsDark = app.ActualThemeVariant == ThemeVariant.Dark;

        // 系统主题变了：重算 IsDark。控件那边由各自的资源绑定重绘，
        // 这里只保证下一次读 PageBackground 之类的属性拿到正确值。
        app.ActualThemeVariantChanged += (_, _) =>
        {
            IsDark = app.ActualThemeVariant == ThemeVariant.Dark;
            Updated?.Invoke();
        };
    }

    /// <summary>主题变化的通知。界面重建时订阅它。</summary>
    public static event Action? Updated;

    /// <summary>转成画刷。每次调用都新建——颜色值会随主题变，缓存反而要失效逻辑。</summary>
    public static IBrush Brush(Color color) => new SolidColorBrush(color);
}
