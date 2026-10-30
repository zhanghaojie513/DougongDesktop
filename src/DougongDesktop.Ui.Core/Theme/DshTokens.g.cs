using Avalonia.Media;

namespace DougongDesktop.Ui.Core.Theme;

/// <summary>
/// dsh 的 <c>--dsw-alias-*</c> 语义 token，light / dark 各一套。
/// </summary>
/// <remarks>
/// <b>本文件是生成物，不要手改。</b>
/// 由 <c>scripts/sync-dsh-tokens.py</c> 从 dsh 的
/// <c>packages/client/ui-theme/src/styles/design-platform.css</c> 生成。
/// 配色有变请重跑脚本，手改会在下次生成时丢失。
/// <para>
/// <b>数值已把 <c>var()</c> 与 <c>color-mix()</c> 求值成具体色值</b>——运行期不做混色，
/// 避免每帧算一遍。求值时的两个坑：
/// <list type="bullet">
/// <item><c>color-mix(in srgb, A p%, B)</c> 中 <b>A 占 p%</b></item>
/// <item><c>color-mix(in srgb, A, B p%)</c> 中 <b>B 占 p%</b>（与上一条相反）</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么不用 XAML ResourceDictionary</b>：这些值要按 light/dark 在代码里切换，
/// 强类型常量拼错在编译期就报；字符串查资源拼错要等运行期。
/// 给 Arc 用时再转 <c>IBrush</c>。
/// </para>
/// </remarks>
public static class DshTokens
{
    /// <summary>dsh 的 light 配色（108 个 token）。</summary>
    public static class Light
    {
        public static readonly Color DswAliasBgBase = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasBgDocumentPreview = Color.Parse("#FFEBEEF2");
        public static readonly Color DswAliasBgDocumentSelection = Color.Parse("#66183462");
        public static readonly Color DswAliasBgLayer1 = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasBgLayer2 = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasBgLayer3 = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasBgMask1 = Color.Parse("#3D000000");
        public static readonly Color DswAliasBgMask2 = Color.Parse("#1F000000");
        public static readonly Color DswAliasBgMask3 = Color.Parse("#7A000000");
        public static readonly Color DswAliasBgMaskDrop = Color.Parse("#B2FFFFFF");
        public static readonly Color DswAliasBgMaskPhoto = Color.Parse("#E0000000");
        public static readonly Color DswAliasBgModulePlatform = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasBgMultiSelect = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasBgOverlay = Color.Parse("#FFE9ECF2");
        public static readonly Color DswAliasBgSkeleton = Color.Parse("#0A000000");
        public static readonly Color DswAliasBorderInverted = Color.Parse("#00000000");
        public static readonly Color DswAliasBorderInverted2 = Color.Parse("#00000000");
        public static readonly Color DswAliasBorderL1 = Color.Parse("#0A000000");
        public static readonly Color DswAliasBorderL2 = Color.Parse("#1A000000");
        public static readonly Color DswAliasBorderL2DarkmodeThin = Color.Parse("#1A000000");
        public static readonly Color DswAliasBorderL3 = Color.Parse("#1F000000");
        public static readonly Color DswAliasBorderL4 = Color.Parse("#29000000");
        public static readonly Color DswAliasBrandPrimary = Color.Parse("#FF0F1115");
        public static readonly Color DswAliasBrandPrimaryInvert = Color.Parse("#FF0F1115");
        public static readonly Color DswAliasBrandPrimaryNewColorprimaryNewColor = Color.Parse("#FF4176E6");
        public static readonly Color DswAliasBrandText = Color.Parse("#FF0F1115");
        public static readonly Color DswAliasButtonContrastFill = Color.Parse("#FF61666B");
        public static readonly Color DswAliasButtonElevatedFill = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasButtonFloatingFill = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasButtonFloatingHover = Color.Parse("#FFF1F3F5");
        public static readonly Color DswAliasButtonGhostActiveBorder = Color.Parse("#FF979DA6");
        public static readonly Color DswAliasButtonGhostActiveFill = Color.Parse("#FFEBEEF2");
        public static readonly Color DswAliasButtonGhostActiveHover = Color.Parse("#FFE9ECF2");
        public static readonly Color DswAliasButtonInfoFill = Color.Parse("#FF4176E6");
        public static readonly Color DswAliasButtonInfoHover = Color.Parse("#FF7AAAFF");
        public static readonly Color DswAliasButtonPrimaryDimmed = Color.Parse("#FFEBEEF2");
        public static readonly Color DswAliasButtonPrimaryFill = Color.Parse("#FF0F1115");
        public static readonly Color DswAliasButtonPrimaryHover = Color.Parse("#FF43454A");
        public static readonly Color DswAliasButtonToolBarFill = Color.Parse("#80545557");
        public static readonly Color DswAliasButtonToolBarFillInvisible = Color.Parse("#5C1F1F1F");
        public static readonly Color DswAliasButtonToolBarHover = Color.Parse("#99545557");
        public static readonly Color DswAliasCodeDiffAdded = Color.Parse("#1422C55E");
        public static readonly Color DswAliasCodeDiffDeleted = Color.Parse("#14EC1313");
        public static readonly Color DswAliasFileDiffAddedBg = Color.Parse("#FFE6F4E7");
        public static readonly Color DswAliasFileDiffAddedGutter = Color.Parse("#FFEDF7ED");
        public static readonly Color DswAliasFileDiffAddedMarker = Color.Parse("#FF01A241");
        public static readonly Color DswAliasFileDiffDeletedBg = Color.Parse("#FFFCE6E2");
        public static readonly Color DswAliasFileDiffDeletedGutter = Color.Parse("#FFFDECE9");
        public static readonly Color DswAliasFileDiffDeletedMarker = Color.Parse("#FFBA2723");
        public static readonly Color DswAliasIllustrationBackground = Color.Parse("#FFF5F7FA");
        public static readonly Color DswAliasIllustrationBacking = Color.Parse("#FFCAD2DB");
        public static readonly Color DswAliasIllustrationDetail = Color.Parse("#FFCAD2DB");
        public static readonly Color DswAliasIllustrationInset = Color.Parse("#FFEDF0F4");
        public static readonly Color DswAliasIllustrationShadow = Color.Parse("#FFEDF0F4");
        public static readonly Color DswAliasIllustrationSurface = Color.Parse("#FFEDF0F4");
        public static readonly Color DswAliasInteractiveBgActive = Color.Parse("#1A263148");
        public static readonly Color DswAliasInteractiveBgHover = Color.Parse("#0F263148");
        public static readonly Color DswAliasInteractiveBgHoverAccent = Color.Parse("#24263148");
        public static readonly Color DswAliasInteractiveBgHoverDanger = Color.Parse("#0DEC1313");
        public static readonly Color DswAliasInteractiveBgHoverSolid = Color.Parse("#FFF1F3F5");
        public static readonly Color DswAliasLabelCaption = Color.Parse("#FFADB2B8");
        public static readonly Color DswAliasLabelDeepDiving = Color.Parse("#FF345EBA");
        public static readonly Color DswAliasLabelDeepDivingShimmer = Color.Parse("#FF243D80");
        public static readonly Color DswAliasLabelDimmed = Color.Parse("#FFE1E5EE");
        public static readonly Color DswAliasLabelDocumentPreview = Color.Parse("#FF61666B");
        public static readonly Color DswAliasLabelPrimary = Color.Parse("#FF0F1115");
        public static readonly Color DswAliasLabelPrimaryBluish = Color.Parse("#FF0E3074");
        public static readonly Color DswAliasLabelPrimaryDimmed = Color.Parse("#FF151517");
        public static readonly Color DswAliasLabelPrimaryForeground = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasLabelPrimaryInverted = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasLabelSecondary = Color.Parse("#FF61666B");
        public static readonly Color DswAliasLabelShimmer = Color.Parse("#4C000000");
        public static readonly Color DswAliasLabelTertiary = Color.Parse("#FF81858C");
        public static readonly Color DswAliasLink = Color.Parse("#FF4176E6");
        public static readonly Color DswAliasMarkdownCitation = Color.Parse("#FFEBEEF2");
        public static readonly Color DswAliasMarkdownCodeBlock = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasMarkdownCodeBlockBanner = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasMarkdownCodeSegmentSelected = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasMarkdownCodeSegmentUnselected = Color.Parse("#FFF1F3F5");
        public static readonly Color DswAliasMarkdownInlineCode = Color.Parse("#FFFAFAFA");
        public static readonly Color DswAliasMarkdownPlaceholder = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasMarkdownTag = Color.Parse("#FFF1F3F5");
        public static readonly Color DswAliasMenuGroupHeaderFill = Color.Parse("#F0F8F9FA");
        public static readonly Color DswAliasMenuIcon = Color.Parse("#FF353638");
        public static readonly Color DswAliasScrollbarBgL1 = Color.Parse("#FFE5E5E5");
        public static readonly Color DswAliasScrollbarBgL2 = Color.Parse("#FFE5E5E5");
        public static readonly Color DswAliasScrollbarHoverL1 = Color.Parse("#FFD4D4D4");
        public static readonly Color DswAliasScrollbarHoverL2 = Color.Parse("#FFD4D4D4");
        public static readonly Color DswAliasStateBusinessPrimary = Color.Parse("#FF4176E6");
        public static readonly Color DswAliasStateBusinessTertiary = Color.Parse("#FFE4EDFD");
        public static readonly Color DswAliasStateDonePrimary = Color.Parse("#FF7C3AED");
        public static readonly Color DswAliasStateErrorPrimary = Color.Parse("#FFEC1313");
        public static readonly Color DswAliasStateErrorSecondary = Color.Parse("#FFF25A5A");
        public static readonly Color DswAliasStateIdlePrimary = Color.Parse("#FFD4D4D4");
        public static readonly Color DswAliasStateSuccessPrimary = Color.Parse("#FF22C55E");
        public static readonly Color DswAliasStateSuccessSecondary = Color.Parse("#FF4ED17E");
        public static readonly Color DswAliasStateSuccessTertiary = Color.Parse("#FFE6FAED");
        public static readonly Color DswAliasStateWarnLabel = Color.Parse("#FFDD8629");
        public static readonly Color DswAliasStateWarnPrimary = Color.Parse("#FFF59E0B");
        public static readonly Color DswAliasStateWarnSecondary = Color.Parse("#FFF7AD31");
        public static readonly Color DswAliasStateWarnTertiary = Color.Parse("#FFFEF5E7");
        public static readonly Color DswAliasSwitchThumb = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasToastBg = Color.Parse("#FF353638");
        public static readonly Color DswAliasToastLabel = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasTooltipBg = Color.Parse("#FF2C2C2E");
        public static readonly Color DswAliasTooltipKeyBg = Color.Parse("#FF525254");
        public static readonly Color DswAliasTurnTriggerBg = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasTurnTriggerBgHover = Color.Parse("#0F263148");
    }

    /// <summary>dsh 的 dark 配色（108 个 token）。</summary>
    public static class Dark
    {
        public static readonly Color DswAliasBgBase = Color.Parse("#FF151517");
        public static readonly Color DswAliasBgDocumentPreview = Color.Parse("#FF151517");
        public static readonly Color DswAliasBgDocumentSelection = Color.Parse("#66183462");
        public static readonly Color DswAliasBgLayer1 = Color.Parse("#FF232324");
        public static readonly Color DswAliasBgLayer2 = Color.Parse("#FF2C2C2E");
        public static readonly Color DswAliasBgLayer3 = Color.Parse("#FF353638");
        public static readonly Color DswAliasBgMask1 = Color.Parse("#80000000");
        public static readonly Color DswAliasBgMask2 = Color.Parse("#33000000");
        public static readonly Color DswAliasBgMask3 = Color.Parse("#7A000000");
        public static readonly Color DswAliasBgMaskDrop = Color.Parse("#B2272730");
        public static readonly Color DswAliasBgMaskPhoto = Color.Parse("#E0000000");
        public static readonly Color DswAliasBgModulePlatform = Color.Parse("#FF353638");
        public static readonly Color DswAliasBgMultiSelect = Color.Parse("#FF212123");
        public static readonly Color DswAliasBgOverlay = Color.Parse("#FF61666B");
        public static readonly Color DswAliasBgSkeleton = Color.Parse("#14FFFFFF");
        public static readonly Color DswAliasBorderInverted = Color.Parse("#0FFFFFFF");
        public static readonly Color DswAliasBorderInverted2 = Color.Parse("#14FFFFFF");
        public static readonly Color DswAliasBorderL1 = Color.Parse("#0FFFFFFF");
        public static readonly Color DswAliasBorderL2 = Color.Parse("#1FFFFFFF");
        public static readonly Color DswAliasBorderL2DarkmodeThin = Color.Parse("#0FFFFFFF");
        public static readonly Color DswAliasBorderL3 = Color.Parse("#29FFFFFF");
        public static readonly Color DswAliasBorderL4 = Color.Parse("#33FFFFFF");
        public static readonly Color DswAliasBrandPrimary = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasBrandPrimaryInvert = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasBrandPrimaryNewColorprimaryNewColor = Color.Parse("#FF5686FE");
        public static readonly Color DswAliasBrandText = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasButtonContrastFill = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasButtonElevatedFill = Color.Parse("#FF43454A");
        public static readonly Color DswAliasButtonFloatingFill = Color.Parse("#FF2C2C2E");
        public static readonly Color DswAliasButtonFloatingHover = Color.Parse("#FF353638");
        public static readonly Color DswAliasButtonGhostActiveBorder = Color.Parse("#FF81858C");
        public static readonly Color DswAliasButtonGhostActiveFill = Color.Parse("#FF43454A");
        public static readonly Color DswAliasButtonGhostActiveHover = Color.Parse("#FF61666B");
        public static readonly Color DswAliasButtonInfoFill = Color.Parse("#FF7AAAFF");
        public static readonly Color DswAliasButtonInfoHover = Color.Parse("#FF4176E6");
        public static readonly Color DswAliasButtonPrimaryDimmed = Color.Parse("#FF43454A");
        public static readonly Color DswAliasButtonPrimaryFill = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasButtonPrimaryHover = Color.Parse("#FFEBEEF2");
        public static readonly Color DswAliasButtonToolBarFill = Color.Parse("#80545557");
        public static readonly Color DswAliasButtonToolBarFillInvisible = Color.Parse("#5C1F1F1F");
        public static readonly Color DswAliasButtonToolBarHover = Color.Parse("#99545557");
        public static readonly Color DswAliasCodeDiffAdded = Color.Parse("#1F22C55E");
        public static readonly Color DswAliasCodeDiffDeleted = Color.Parse("#1FF25A5A");
        public static readonly Color DswAliasFileDiffAddedBg = Color.Parse("#FF1F3124");
        public static readonly Color DswAliasFileDiffAddedGutter = Color.Parse("#FF132016");
        public static readonly Color DswAliasFileDiffAddedMarker = Color.Parse("#FF41C977");
        public static readonly Color DswAliasFileDiffDeletedBg = Color.Parse("#FF3C1F1B");
        public static readonly Color DswAliasFileDiffDeletedGutter = Color.Parse("#FF28130E");
        public static readonly Color DswAliasFileDiffDeletedMarker = Color.Parse("#FFFA423E");
        public static readonly Color DswAliasIllustrationBackground = Color.Parse("#FF2C2C2E");
        public static readonly Color DswAliasIllustrationBacking = Color.Parse("#FF43454A");
        public static readonly Color DswAliasIllustrationDetail = Color.Parse("#FF81858C");
        public static readonly Color DswAliasIllustrationInset = Color.Parse("#FF43454A");
        public static readonly Color DswAliasIllustrationShadow = Color.Parse("#FF43454A");
        public static readonly Color DswAliasIllustrationSurface = Color.Parse("#FF61666B");
        public static readonly Color DswAliasInteractiveBgActive = Color.Parse("#24FFFFFF");
        public static readonly Color DswAliasInteractiveBgHover = Color.Parse("#14FFFFFF");
        public static readonly Color DswAliasInteractiveBgHoverAccent = Color.Parse("#3DFFFFFF");
        public static readonly Color DswAliasInteractiveBgHoverDanger = Color.Parse("#26F25A5A");
        public static readonly Color DswAliasInteractiveBgHoverSolid = Color.Parse("#FF353638");
        public static readonly Color DswAliasLabelCaption = Color.Parse("#FF81858C");
        public static readonly Color DswAliasLabelDeepDiving = Color.Parse("#FF7D9ADE");
        public static readonly Color DswAliasLabelDeepDivingShimmer = Color.Parse("#FF8ABCFE");
        public static readonly Color DswAliasLabelDimmed = Color.Parse("#FF43454A");
        public static readonly Color DswAliasLabelDocumentPreview = Color.Parse("#FFCFD3D6");
        public static readonly Color DswAliasLabelPrimary = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasLabelPrimaryBluish = Color.Parse("#FFF9FAFB");
        public static readonly Color DswAliasLabelPrimaryDimmed = Color.Parse("#FFEBEEF2");
        public static readonly Color DswAliasLabelPrimaryForeground = Color.Parse("#FF0F1115");
        public static readonly Color DswAliasLabelPrimaryInverted = Color.Parse("#FF353638");
        public static readonly Color DswAliasLabelSecondary = Color.Parse("#FFCFD3D6");
        public static readonly Color DswAliasLabelShimmer = Color.Parse("#73737373");
        public static readonly Color DswAliasLabelTertiary = Color.Parse("#FFADB2B8");
        public static readonly Color DswAliasLink = Color.Parse("#FF7AAAFF");
        public static readonly Color DswAliasMarkdownCitation = Color.Parse("#FF353638");
        public static readonly Color DswAliasMarkdownCodeBlock = Color.Parse("#FF1B1B1C");
        public static readonly Color DswAliasMarkdownCodeBlockBanner = Color.Parse("#FF2C2C2E");
        public static readonly Color DswAliasMarkdownCodeSegmentSelected = Color.Parse("#FF353638");
        public static readonly Color DswAliasMarkdownCodeSegmentUnselected = Color.Parse("#FF1B1B1C");
        public static readonly Color DswAliasMarkdownInlineCode = Color.Parse("#FF292929");
        public static readonly Color DswAliasMarkdownPlaceholder = Color.Parse("#FF2C2C2E");
        public static readonly Color DswAliasMarkdownTag = Color.Parse("#FF2C2C2E");
        public static readonly Color DswAliasMenuGroupHeaderFill = Color.Parse("#F0303136");
        public static readonly Color DswAliasMenuIcon = Color.Parse("#FFEBEEF2");
        public static readonly Color DswAliasScrollbarBgL1 = Color.Parse("#FF3C3C3D");
        public static readonly Color DswAliasScrollbarBgL2 = Color.Parse("#FF545557");
        public static readonly Color DswAliasScrollbarHoverL1 = Color.Parse("#FF545557");
        public static readonly Color DswAliasScrollbarHoverL2 = Color.Parse("#FF65676B");
        public static readonly Color DswAliasStateBusinessPrimary = Color.Parse("#FF7AAAFF");
        public static readonly Color DswAliasStateBusinessTertiary = Color.Parse("#FF34415B");
        public static readonly Color DswAliasStateDonePrimary = Color.Parse("#FFA78BFA");
        public static readonly Color DswAliasStateErrorPrimary = Color.Parse("#FFF25A5A");
        public static readonly Color DswAliasStateErrorSecondary = Color.Parse("#FFF25A5A");
        public static readonly Color DswAliasStateIdlePrimary = Color.Parse("#FF545557");
        public static readonly Color DswAliasStateSuccessPrimary = Color.Parse("#FF22C55E");
        public static readonly Color DswAliasStateSuccessSecondary = Color.Parse("#FF4ED17E");
        public static readonly Color DswAliasStateSuccessTertiary = Color.Parse("#FF233C2C");
        public static readonly Color DswAliasStateWarnLabel = Color.Parse("#FFDD8629");
        public static readonly Color DswAliasStateWarnPrimary = Color.Parse("#FFF59E0B");
        public static readonly Color DswAliasStateWarnSecondary = Color.Parse("#FFF7AD31");
        public static readonly Color DswAliasStateWarnTertiary = Color.Parse("#FF27241F");
        public static readonly Color DswAliasSwitchThumb = Color.Parse("#FFADB2B8");
        public static readonly Color DswAliasToastBg = Color.Parse("#FF43454A");
        public static readonly Color DswAliasToastLabel = Color.Parse("#FFFFFFFF");
        public static readonly Color DswAliasTooltipBg = Color.Parse("#FF43454A");
        public static readonly Color DswAliasTooltipKeyBg = Color.Parse("#FF65666B");
        public static readonly Color DswAliasTurnTriggerBg = Color.Parse("#14FFFFFF");
        public static readonly Color DswAliasTurnTriggerBgHover = Color.Parse("#24FFFFFF");
    }

}
