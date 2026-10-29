using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Fonts;

namespace DougongDesktop.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()      // Avalonia.Fonts.Inter：正文字体
            .WithArcFonts()       // TKCode.Arc.Fonts：Geist 显示体 + Geist Mono 代码体
            .UseArc(arc => arc
                .UseArcControls())  // 注册控件、主题、令牌（0.3.2 入口，不再是 UseAtomUI）
            .LogToTrace();
}
