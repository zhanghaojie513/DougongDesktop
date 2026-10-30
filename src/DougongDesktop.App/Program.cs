using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Fonts;

namespace DougongDesktop.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // --dump-config：输出装配清单后退出，不启窗。
        // 必须在 BuildAvaloniaApp 之前拦——它的工作不需要 UI 起来。
        if (args.Contains("--dump-config"))
        {
            Console.Out.Write(DougongDesktop.App.Shell.ConfigDump.Describe());
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()      // Avalonia.Fonts.Inter：正文字体
            .WithArcFonts()       // TKCode.Arc.Fonts：Geist 显示体 + Geist Mono 代码体
            .UseArc(arc => arc
                .UseArcControls()    // 基础控件、主题、令牌（0.3.2 入口，不再是 UseAtomUI）
                .UseArcSpecial())    // agent 组件：ToolCall/AgentStatus/AgentPlan/ContextWindow
            .LogToTrace();
}
