using Avalonia.Controls;

namespace DougongDesktop.Ui.Core;

/// <summary>
/// UI 插件必须实现的接口。
/// </summary>
/// <remarks>
/// <b>插件产出的是代码构建的控件树，不是 <c>.axaml</c> 文件。</b>
/// 原因：Avalonia 的 XAML 需要编译期生成（本项目开了
/// <c>AvaloniaUseCompiledBindingsByDefault</c>），运行期解析走反射、
/// 裁剪后会炸。而插件程序集是运行时加载的，容器编译期看不见它。
/// <para>
/// 插件自己的 csproj 可以用 XAML 编译（它有编译期），只是不能把
/// <c>.axaml</c> 当运行期资源交给宿主解析。
/// </para>
/// </remarks>
public interface IDesktopUiExtension
{
    /// <summary>插件 Id。必须全局唯一，用于诊断与冲突报告。</summary>
    string Id { get; }

    /// <summary>显示名。插件管理页用它。</summary>
    string DisplayName { get; }

    /// <summary>
    /// 想注册到哪个 slot。
    /// </summary>
    /// <remarks>
    /// 可用值见 <see cref="ReservedSlots"/>。注册到未声明的 slot 会被拒绝。
    /// </remarks>
    string SlotKey { get; }

    /// <summary>
    /// Keyed slot 的 key；其他 kind 忽略。
    /// </summary>
    /// <remarks>
    /// <b><see cref="ReservedSlots.Conversation"/> 不允许外部插件占用</b>——
    /// 加载器会拒绝，不是插件自己说了算。
    /// </remarks>
    string? SlotKeyValue { get; }

    /// <summary>构建控件。可能被多次调用（比如刷新后重建），不要在里面做带副作用的初始化。</summary>
    Control Build();
}
