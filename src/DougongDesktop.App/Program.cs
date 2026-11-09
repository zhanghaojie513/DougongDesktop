using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Fonts;
#if DEBUG
using Spyglass.Agent;
#endif

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
                .UseArcBlocks()      // Blocks：Sidebar 等复合控件。漏了它 Sidebar 会
                                     // 有尺寸但没有视觉子节点（ControlTheme 没应用）——实测踩过
                .UseArcDock())       // Dock：右栏分裂标签树（P8e E4）。漏了它 DockLayout 没模板，窗口直接空白
            // Spyglass：Avalonia 的 out-of-process 调试器（视觉树/样式级联/绑定/性能）。
            //
            // **只在 Debug 构建里挂**：生产包里不该带调试探针——
            // 它会开一个本地 WebSocket 端口，那是额外的攻击面。
            // 包引用也加了同样的 Condition，所以 Release 构建连 dll 都不会有。
            //
            // 用法：跑 Debug 构建，按 Ctrl+Shift+I（macOS Cmd+Option+I）。
            // 也可以让 AI agent 通过 MCP 读：`claude mcp add spyglass -- spyglass mcp`
#if DEBUG
            .UseSpyglass()
#endif
            .LogToTrace();
}
