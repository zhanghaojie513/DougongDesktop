namespace DougongDesktop.Ui.Core;

/// <summary>
/// 轮次导航条的几何与刻度状态（dsh 的 <c>TurnNavigator</c>）。
/// </summary>
/// <remarks>
/// <b>这是右侧那条 28px 竖排刻度</b>，不是会话内的目录。读者靠它一眼看出
/// "这段对话有几个来回、我在第几个"，点击跳到对应轮次。
/// <para>
/// 数值全部来自 dsh <c>TurnNavigator.tsx</c> 与 <c>.module.css</c>：
/// <list type="bullet">
/// <item>轨道宽 28px（<c>.rail { width: 28px }</c>）</item>
/// <item>刻度间距 10px（<c>TURN_SPACING_PX</c>）</item>
/// <item>上下内边距各 6px（<c>RAIL_INSET_PX</c>）</item>
/// <item>端点淡出带 24px（<c>FADE_PX</c>）</item>
/// <item>容器宽 ≤900px 时整条隐藏（<c>@container (max-width: 900px)</c>）</item>
/// </list>
/// </para>
/// <para>
/// <b>刻度是一根 20×2 的小横线，靠 <c>scaleX</c> 表达状态</b>，不是圆点：
/// 静止 0.6 / hover 0.9 / 当前 1 / 未加载 0.4。宽度变化比换颜色更容易
/// 在余光里分辨，这是 dsh 选横线不选点的原因。
/// </para>
/// </remarks>
public static class TurnRail
{
    /// <summary>轨道宽度。</summary>
    public const double RailWidth = 28;

    /// <summary>相邻刻度的垂直间距。</summary>
    public const double TurnSpacing = 10;

    /// <summary>轨道上下内边距（首尾刻度到边缘的距离）。</summary>
    public const double RailInset = 6;

    /// <summary>端点淡出带高度。</summary>
    public const double FadeHeight = 24;

    /// <summary>刻度线段宽度。</summary>
    public const double MarkWidth = 20;

    /// <summary>刻度线段高度。</summary>
    public const double MarkHeight = 2;

    /// <summary>容器窄于该宽度时整条隐藏。</summary>
    public const double HideBelowWidth = 900;

    /// <summary>预览卡最大宽度。</summary>
    public const double PreviewMaxWidth = 300;

    /// <summary>刻度在给定状态下的横向缩放。</summary>
    /// <remarks>
    /// 四个值直接对应 dsh 的 CSS：<c>.mark::before</c> 0.6 /
    /// <c>.markUnloaded</c> 0.4 / <c>.markPreview</c> 0.9 / <c>.markActive</c> 1。
    /// <b>优先级</b>也是 dsh 的：active &gt; preview &gt; unloaded &gt; 静止
    /// （CSS 里 <c>markUnloaded</c> 排在 hover/active 之前，以免覆盖更强的状态）。
    /// </remarks>
    public static double ScaleFor(TurnMarkState state) => state switch
    {
        TurnMarkState.Active => 1.0,
        TurnMarkState.Preview => 0.9,
        TurnMarkState.Unloaded => 0.4,
        _ => 0.6,
    };

    /// <summary>该不该显示整条轨道。</summary>
    /// <remarks>
    /// dsh 用容器查询 <c>max-width: 900px</c> 隐藏。窄窗里中栏本来就只剩几百像素，
    /// 再切掉 28px 会更挤——所以这不是"可选的装饰"，是响应式规则。
    /// <para>
    /// <b>少于 2 轮时也不显示</b>：dsh 的虚拟化器 <c>enabled: items.length >= 2</c>，
    /// 一条没有跳转意义的刻度只是噪音。
    /// </para>
    /// </remarks>
    public static bool ShouldShow(int turnCount, double containerWidth) =>
        turnCount >= 2 && containerWidth > HideBelowWidth;

    /// <summary>全部刻度的总高度（含上下内边距）。</summary>
    /// <remarks>
    /// 用于判断是否需要内部滚动：dsh 让轨道自己滚动（<c>overflow</c>），
    /// 不压缩刻度间距——压缩会让"第几个"这个信息失真。
    /// </remarks>
    public static double ContentHeight(int turnCount) =>
        RailInset * 2 + Math.Max(0, turnCount - 1) * TurnSpacing + MarkHeight;

    /// <summary>某个刻度的中心 y 坐标（相对轨道顶部）。</summary>
    public static double CenterOf(int index) => RailInset + index * TurnSpacing + MarkHeight / 2;
}

/// <summary>一条刻度的视觉状态。</summary>
/// <remarks>
/// 顺序有意义：dsh 的 CSS 里 <c>markUnloaded</c> 声明在 hover/active 之前，
/// 所以后两者能覆盖它。这里用"优先级更高者胜"表达同一件事。
/// </remarks>
public enum TurnMarkState
{
    /// <summary>静止：有内容、未加载、未被指向。</summary>
    Resting,

    /// <summary>未加载（分页还没拉到这条轮次的内容）。</summary>
    Unloaded,

    /// <summary>鼠标悬停或键盘聚焦。</summary>
    Preview,

    /// <summary>当前正在读的那一轮。</summary>
    Active,
}
