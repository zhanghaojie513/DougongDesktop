using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DougongDesktop.Ui.Core;

namespace SampleUiPlugin;

/// <summary>
/// 示例 UI 插件：往右栏塞一块内容。
/// </summary>
/// <remarks>
/// 验证点：
/// <list type="bullet">
/// <item>外部程序集能被加载并注册进 slot</item>
/// <item>用更小的 priority 覆盖内置实现的机制</item>
/// <item>卸载后 ALC 能回收</item>
/// </list>
/// </remarks>
public sealed class SampleExtension : IDesktopUiExtension
{
    public string Id => "sample.ui-plugin";
    public string DisplayName => "示例插件";
    public string SlotKey => ReservedSlots.RightBar;
    public string? SlotKeyValue => null;

    public Control Build() => new Border
    {
        Padding = new Thickness(20),
        Child = new StackPanel
        {
            Spacing = 10,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "示例 UI 插件已加载",
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "这个控件来自外部程序集，经独立 ALC 加载",
                    FontSize = 12,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "priority 500（第三方默认层）",
                    FontSize = 11,
                    Opacity = 0.4,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
            },
        },
    };
}
