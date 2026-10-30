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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
