using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Fonts;

namespace DougongDesktop.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 桌面端判别器要在任何序列化之前登记（两边都要：UI 与引擎进程）。
        // 漏了这一句的表现是"消息发得出去、收不回来"——反序列化时抛
        // "not supported by polymorphic type"，而那发生在收到第一条
        // fs/listed 时，看起来像协议不兼容而不是漏了登记。
        DougongDesktop.Ipc.DesktopProtocol.RegisterMessages();

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
                .UseArcSpecial()     // agent 组件：ToolCall/AgentStatus/AgentPlan/ContextWindow
                .UseArcBlocks())     // Blocks：Sidebar 等复合控件。漏了它 Sidebar 会
                                     // 有尺寸但没有视觉子节点（ControlTheme 没应用）——实测踩过
            .LogToTrace();
}
