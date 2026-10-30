using Avalonia.Media;

namespace DougongDesktop.Ui.Core;

/// <summary>
/// 颜色运算：补齐 CSS 里有、Avalonia 没有的几个函数。
/// </summary>
/// <remarks>
/// <b>为什么需要它</b>：dsh 的 108 个 <c>--dsw-alias-*</c> token 里有若干条用
/// CSS 的 <c>color-mix()</c> 表达（例如"品牌色 40% 叠在透明上"）。
/// Avalonia 的 <see cref="Color"/> 只有 <c>ToHsl</c>/<c>ToHsv</c>，
/// 没有混色函数——但混色本身是十行算术，补上即可，不必因此放弃对齐。
/// <para>
/// <b>为什么提供两种混色</b>：CSS 的 <c>color-mix(in srgb, ...)</c> 在 sRGB 空间
/// 直接插值，而人眼感知更接近线性空间或 HSL。dsh 用的是 srgb，所以
/// <see cref="MixSrgb"/> 是**对齐 dsh 的正确选择**；<see cref="MixHsl"/>
/// 只在需要"看起来更自然"的地方用（比如生成 hover 色）。
/// 两者不能混用，否则同一套 token 在不同组件上颜色不一致。
/// </para>
/// </remarks>
public static class ColorMath
{
    /// <summary>
    /// 在 sRGB 空间线性混色，等价于 CSS 的 <c>color-mix(in srgb, a p%, b)</c>。
    /// </summary>
    /// <param name="a">第一个颜色。</param>
    /// <param name="b">第二个颜色。</param>
    /// <param name="ratioOfA">a 的占比，0..1。0 得到 b，1 得到 a。</param>
    /// <remarks>
    /// <b>alpha 也一起插值</b>：CSS 的 color-mix 会把两个颜色的 alpha 按比例混合，
    /// 而不是简单取其中之一。不做这一步会让"40% 品牌色叠在透明上"算出一个
    /// 不透明的颜色——那正好是 dsh 需要的半透明遮罩。
    /// </remarks>
    public static Color MixSrgb(Color a, Color b, double ratioOfA)
    {
        var t = Math.Clamp(ratioOfA, 0, 1);

        static byte Lerp(byte x, byte y, double t) =>
            (byte)Math.Round(x * t + y * (1 - t));

        return Color.FromArgb(
            Lerp(a.A, b.A, t),
            Lerp(a.R, b.R, t),
            Lerp(a.G, b.G, t),
            Lerp(a.B, b.B, t));
    }

    /// <summary>在 HSL 空间混色。色相走最短弧，避免红→紫时绕过整圈色轮。</summary>
    /// <remarks>
    /// <b>色相必须取最短路径</b>：直接线性插值 350° 与 10° 会得到 180°（青色），
    /// 而预期是 0°（红色）。这个错误在灰度色上看不出来，只有跨色相时才发现。
    /// </remarks>
    public static Color MixHsl(Color a, Color b, double ratioOfA)
    {
        var t = Math.Clamp(ratioOfA, 0, 1);
        var ha = a.ToHsl();
        var hb = b.ToHsl();

        var delta = hb.H - ha.H;

        // 取最短弧：差值超过半圈时走另一边
        if (delta > 180) delta -= 360;
        else if (delta < -180) delta += 360;

        var h = (ha.H + delta * (1 - t) + 360) % 360;

        return new HslColor(
            (ha.A * t + hb.A * (1 - t)),
            h,
            ha.S * t + hb.S * (1 - t),
            ha.L * t + hb.L * (1 - t)).ToRgb();
    }

    /// <summary>
    /// 按百分比调整不透明度，等价于 CSS 的 <c>rgb(r g b / p%)</c>。
    /// </summary>
    /// <param name="color">基色。</param>
    /// <param name="opacity">0..1。</param>
    public static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb(
            (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255),
            color.R, color.G, color.B);
}
