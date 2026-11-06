using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DougongDesktop.App;

/// <summary>
/// 应用入口。
/// </summary>
/// <remarks>
/// <b>不需要 <c>partial</c></b>：0.3.1 时代 AtomUI 的源生成器要求 partial（ATOMUILOC008），
/// 0.3.2 移除了 AtomUI 依赖，这个约束也一并消失。照着旧文档加 partial 不会报错，
/// 但也没有任何作用。
/// </remarks>
public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 先初始化主题：MainWindow 构造时就要读 dsh 的底色
        Shell.DshTheme.Initialize(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // DOUGONG_UI_PROBE=1 打开 P2 的组件探针窗口：单独验证某个 Arc 控件
            // 的行为，不必启动整个应用、也不必造会话数据。
            //
            // **存在的理由**（2026-11-06 加）：验证 M12 时，从产品界面里
            // 根本看不到代码块——消息流虚拟化 + 视口只有 353px，带代码的
            // 那条永远在视口外。`CodeBlock 0 个` 因此既可能是"没渲染"，
            // 也可能是"在视口外"，和当初 `ToolCall 0 张` 是同一个陷阱。
            // 探针窗口把控件单独摆出来，一次运行就能定论。
            desktop.MainWindow = Environment.GetEnvironmentVariable("DOUGONG_UI_PROBE") is "1"
                ? new Views.ArcProbeWindow()
                : new Views.MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
