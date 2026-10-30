using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 品牌资源加载。
/// </summary>
/// <remarks>
/// <b>为什么单独一层</b>：同一份 logo 在窗口图标、标题栏、侧边栏三处用，
/// 各自拼 <c>avares://</c> URI 容易写错一处（而且写错只会在那一处显示空白，
/// 不报错）。集中在这里，改路径只改一处。
/// <para>
/// <b>深色用反色版</b>：品牌规范里 <c>symbol.png</c> 是纯黑构件、
/// <c>symbol-white.png</c> 是纯白构件。深色主题下用黑色 logo 会看不见。
/// 这不是"额外做的美化"，是必要条件。
/// </para>
/// </remarks>
public static class BrandAssets
{
    private const string BaseUri = "avares://DougongDesktop.App/Assets/";

    /// <summary>
    /// 品牌符号（方形，只含构件，不含文字）。
    /// </summary>
    /// <param name="dark">深色主题时传 true，会加载反色版。</param>
    public static Bitmap Symbol(bool dark) => Load(dark ? "symbol-white.png" : "symbol.png");

    /// <summary>
    /// 横版标志（符号 + DOUGONG 字标）。
    /// </summary>
    /// <param name="dark">深色主题时传 true，会加载反色版。</param>
    public static Bitmap Horizontal(bool dark) => Load(dark ? "horizontal-white.png" : "horizontal.png");

    private static Bitmap Load(string name) =>
        new(AssetLoader.Open(new Uri(BaseUri + name)));
}
