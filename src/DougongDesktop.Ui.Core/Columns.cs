namespace DougongDesktop.Ui.Core;

/// <summary>
/// 三栏几何常量。数值逐条对应 dsh `columns.ts`。
/// </summary>
/// <remarks>
/// <b>这份表是布局的唯一真源</b>。任何硬编码宽度都必须引用这里，
/// 否则改一次 dsh 规格就要满仓库找数字。
/// <para>
/// 出处：dsh `0.2.2-alpha.2`，
/// <c>packages/client/ui-layout/src/client/columns.ts</c>。
/// </para>
/// </remarks>
public static class Columns
{
    /// <summary>中栏保护下限：右栏开着的常态下中心不低于此值。</summary>
    public const double CenterMin = 400;

    /// <summary>侧边栏拖拽下限。</summary>
    public const double SidebarMin = 264;

    /// <summary>侧边栏拖拽上限。</summary>
    public const double SidebarMax = 420;

    /// <summary>侧边栏未拖拽时的默认宽度。</summary>
    public const double SidebarDefault = 280;

    /// <summary>
    /// 收起后的图标轨道宽度。
    /// </summary>
    /// <remarks>
    /// 24px 图标列 + 两侧各 16px padding = 56。
    /// <b>收起不等于消失</b>——这是与原先实现最大的差别。
    /// </remarks>
    public const double SidebarCollapsed = 56;

    /// <summary>低于此框架宽度，侧边栏改为浮层抽屉（不占列宽）。</summary>
    public const double SidebarDrawerBreakpoint = 768;

    /// <summary>低于此视口宽度，侧边栏自动收起到轨道（手动仍可再展开）。</summary>
    public const double SidebarAutoCollapse = 1024;

    /// <summary>右栏拖拽下限。</summary>
    public const double RightbarMin = 300;

    /// <summary>右栏最大宽度占框架的比例。</summary>
    public const double RightbarMaxRatio = 0.7;

    /// <summary>右栏首次打开时占框架的比例。</summary>
    public const double RightbarDefaultRatio = 0.45;

    /// <summary>把宽度夹到契约范围内（四舍五入到整数像素）。</summary>
    /// <remarks>
    /// <b>取整是刻意的</b>：dsh 的 <c>clampWidth</c> 也取整。小数宽度会让
    /// 相邻两栏的小数部分相加不等于整数，出现 1px 的缝。
    /// </remarks>
    public static double ClampWidth(double px, double min, double max) =>
        Math.Min(max, Math.Max(min, Math.Round(px)));

    /// <summary>一帧的三栏解算结果。</summary>
    public readonly record struct Solution(double Sidebar, double Center, double Rightbar);

    /// <summary>
    /// 解算三栏宽度。
    /// </summary>
    /// <param name="viewport">框架可用宽度。</param>
    /// <param name="sidebar">侧边栏偏好宽度，0 表示收起。</param>
    /// <param name="rightbar">右栏请求宽度，0 表示不要轨道。</param>
    /// <param name="collapsedWidth">侧边栏收起后的轨道宽度；0 表示整列隐藏。</param>
    /// <param name="centeredSidebar">展开的侧边栏右边缘是否落到框架中点（折叠屏书本姿态）。</param>
    /// <remarks>
    /// <b>顺序是这条规则的全部意义</b>：
    /// <list type="number">
    /// <item>侧边栏不让步（除以 0 表示收起外）——它只被夹到范围内</item>
    /// <item>右栏先缩到 <see cref="RightbarMin"/></item>
    /// <item>右栏再让就<b>整条轨道消失</b></item>
    /// <item>只有轨道消失后，中栏才允许低于 <see cref="CenterMin"/>，最低到 0</item>
    /// </list>
    /// 顺序写反会得到"中栏先被压缩、右栏还留着"的结果——那是另一种产品。
    /// </remarks>
    public static Solution Compute(
        double viewport,
        double sidebar,
        double rightbar,
        double collapsedWidth = SidebarCollapsed,
        bool centeredSidebar = false)
    {
        var s = sidebar == 0
            ? collapsedWidth
            : centeredSidebar
                ? viewport / 2
                : ClampWidth(sidebar, SidebarMin, SidebarMax);

        var available = viewport - s - CenterMin;

        var r = rightbar == 0 || available < RightbarMin
            ? 0
            : Math.Min(available, ClampWidth(rightbar, RightbarMin, viewport * RightbarMaxRatio));

        return new Solution(s, Math.Max(0, viewport - s - r), r);
    }

    /// <summary>
    /// 右栏首次打开时的宽度。
    /// </summary>
    public static double RightbarDefault(double viewport) => viewport * RightbarDefaultRatio;
}
