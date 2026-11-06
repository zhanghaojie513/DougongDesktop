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

    // ── 侧边栏行高与圆角（来自 dsh SidebarRoot.module.css）──

    /// <summary>品牌行高度（展开态）。Windows 原生标题栏下是 40。</summary>
    /// <remarks>
    /// 出处：dsh <c>SidebarRoot.module.css</c> 的 <c>.brandRow { height: 52px }</c>。
    /// 它带负 margin <c>-6px -8px 0</c> 抵消根 padding，让分隔线通栏。
    /// </remarks>
    public const double SidebarBrandRowHeight = 52;

    /// <summary>新建会话按钮高度。</summary>
    public const double SidebarNewSessionHeight = 36;

    /// <summary>会话行高度。</summary>
    public const double SidebarSessionRowHeight = 36;

    /// <summary>普通控件行高度（筛选、折叠按钮等）。</summary>
    public const double SidebarControlRowHeight = 28;

    /// <summary>侧边栏根容器的水平内边距。</summary>
    public const double SidebarInlinePadding = 8;

    /// <summary>侧边栏根容器的垂直内边距。</summary>
    public const double SidebarVerticalPadding = 6;

    /// <summary>
    /// 侧栏内容相对栏边缘的内缩。
    /// </summary>
    /// <remarks>
    /// dsh 的 <c>--dsh-sidebar-paint-inset: 3px</c>。它的作用是让侧栏的
    /// 填充色在内容之外还能露出 3px，行悬停背景因此不会贴到栏边缘。
    /// </remarks>
    public const double SidebarPaintInset = 3;

    /// <summary>收起轨道的顶部内边距（dsh: <c>padding: 18px 10px 6px</c>）。</summary>
    public const double SidebarRailPaddingTop = 18;

    /// <summary>收起轨道的水平内边距。</summary>
    public const double SidebarRailPaddingInline = 10;

    /// <summary>收起轨道的控件盒尺寸（36×36 居中于 56px 轨道）。</summary>
    public const double SidebarRailControlSize = 36;

    // ── 圆角（来自 dsh base.css 的 --dsw-radius-*）──

    /// <summary>小圆角：控件行、标签。</summary>
    public const double RadiusSm = 8;

    /// <summary>中圆角：会话行、新建会话按钮。</summary>
    public const double RadiusMd = 12;

    /// <summary>大圆角。</summary>
    public const double RadiusLg = 16;

    /// <summary>
    /// 超大圆角。
    /// </summary>
    /// <remarks>
    /// 出处 dsh <c>ui-theme/src/styles/base.css:22</c>：
    /// <c>--dsw-radius-xl: 20px</c>。用户消息气泡用它
    /// （<c>MessageItem.module.css:3-8</c>）。
    /// <b>不要拿 <see cref="RadiusLg"/> 顶替</b>——差 4px，气泡会显得"方一点"，
    /// 而气泡是中区出现频率最高的元素，每天要看几十次。
    /// </remarks>
    public const double RadiusXl = 20;

    /// <summary>面板圆角（dsh 的 <c>--dsw-radius-panel</c> 是 28）。</summary>
    public const double RadiusPanel = 28;

    // ── 窗口标题栏（dsh 的 Windows caption row）──

    /// <summary>标题栏高度。dsh 的 <c>--dsh-windows-titlebar-height</c> 实测 40px。</summary>
    public const double TitlebarHeight = 40;

    /// <summary>
    /// macOS 红绿灯（关闭/最小化/最大化）占用的左侧宽度。
    /// </summary>
    /// <remarks>
    /// macOS 上 <c>ExtendClientAreaToDecorationsHint</c> 会让系统按钮**叠在自绘内容上**，
    /// 位置由系统决定，我们挪不动。18px 左边距 + 三个 14px 圆点 + 间距 ≈ 78px，
    /// 留 80 保证不压住第一个自绘按钮。
    /// <para>
    /// <b>这不是"感觉留点空"</b>：dsh 是 Electron，同样把标题栏交给系统，
    /// 它的左侧内容也从未顶到最左。Windows 上用 0——那里的窗口按钮在右边，
    /// 且由我们自绘。
    /// </para>
    /// </remarks>
    public const double MacTrafficLightInset = 80;

    /// <summary>主区左上角的圆角（dsh 的 <c>--dsh-windows-content-radius: 16px</c>）。</summary>
    public const double ContentCornerRadius = RadiusLg;

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

/// <summary>
/// 中区会话域的几何与行为常量。数值逐条对应 dsh 的 CSS 与 TS。
/// </summary>
/// <remarks>
/// 出处：dsh `0.2.2-alpha.2`（commit `ce7073d`），
/// 具体文件与行号见 <c>docs/dsh-parity-spec.md</c> §7.5。
/// <para>
/// <b>放在 Ui.Core 而不是 App</b>：这些数字要能被检查工程断言。
/// 检查工程不引用 App（与"App 不引 SDK"同向的隔离），放进 Ui.Core
/// 才能写"贴底阈值必须等于 25"这种能失败的检查——写死在 XAML/代码里就查不到。
/// </para>
/// </remarks>
public static class ConversationMetrics
{
    /// <summary>会话内容列宽下限。</summary>
    public const double ContentWidthMin = 680;

    /// <summary>会话内容列宽上限。</summary>
    public const double ContentWidthMax = 920;

    /// <summary>内容列宽占中栏的比例。</summary>
    public const double ContentWidthRatio = 0.64;

    /// <summary>用户气泡最大宽占内容列宽的比例。</summary>
    public const double UserBubbleRatio = 0.702;

    /// <summary>用户气泡最大宽占容器宽的比例（另一条约束，取二者更小的）。</summary>
    public const double UserBubbleMaxPercent = 0.82;

    /// <summary>消息区左右留白。</summary>
    public const double Gutter = 32;

    /// <summary>
    /// 距底部多少像素以内算"贴底"（自动跟随新内容）。
    /// </summary>
    /// <remarks>
    /// dsh `use-chat-reading.ts:7` 是 <c>FOLLOW_THRESHOLD = 24</c>，
    /// 但传入采样函数时写的是 <c>FOLLOW_THRESHOLD + 1</c>（同文件 :277）。
    /// 也就是说**实际生效值是 25**。<b>这里取 25，不取 24</b>——
    /// 照常量名抄 24 会差 1px。
    /// </remarks>
    public const double FollowThreshold = 25;

    /// <summary>消息流默认行间距。</summary>
    public const double GapDefault = 4;

    /// <summary>相邻 assistant 消息之间的间距。</summary>
    public const double GapAssistant = 12;

    /// <summary>轮次处理行之后的间距。</summary>
    public const double GapAfterTurnProcess = 16;

    /// <summary>"回到底部"按钮直径。</summary>
    public const double ScrollToBottomSize = 34;

    /// <summary>"回到底部"按钮距底距离。</summary>
    public const double ScrollToBottomInset = 16;

    /// <summary>输入区上方渐变遮罩高度。</summary>
    public const double ComposerMaskHeight = 36;

    /// <summary>代码块复制按钮成功态的复原延时（毫秒）。</summary>
    public const int CopyFeedbackMs = 1000;

    /// <summary>
    /// 会话内容列宽：<c>clamp(680, 中栏宽 × 0.64, 920)</c>。
    /// </summary>
    /// <remarks>
    /// 出处 <c>ConversationRoot.module.css:461-467</c>。
    /// 用户可拖拽覆盖（min 640），那是 M17，未做。
    /// </remarks>
    public static double ContentWidth(double centerWidth) =>
        Math.Clamp(centerWidth * ContentWidthRatio, ContentWidthMin, ContentWidthMax);

    /// <summary>
    /// 用户气泡的最大宽度。
    /// </summary>
    /// <remarks>
    /// dsh 是两条约束取小：<c>min(内容列宽 × 0.702, 容器宽 × 82%)</c>
    /// （<c>MessageItem.module.css:10-21</c>）。
    /// <para>
    /// <b>为什么必须两条都要</b>：窄窗口下容器宽先到线（82%），
    /// 宽窗口下 0.702 那条先到线。只留一条，另一边必然错——
    /// 这就是"改一处坏一处"的典型。
    /// </para>
    /// </remarks>
    public static double UserBubbleWidth(double contentWidth, double containerWidth) =>
        Math.Min(contentWidth * UserBubbleRatio, containerWidth * UserBubbleMaxPercent);
}
