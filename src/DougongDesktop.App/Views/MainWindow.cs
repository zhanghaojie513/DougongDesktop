using Avalonia.Controls;
using Avalonia.VisualTree;
using DougongDesktop.App.Shell;
using DougongDesktop.Ui.Core;
using TKCode.Arc;

namespace DougongDesktop.App.Views;

/// <summary>
/// 主窗口：装配 AppShell 并注册内置占位内容。
/// </summary>
/// <remarks>
/// <b>内置内容也走 slot 注册</b>——不是直接塞进布局。这样关掉某块 = 该 slot 空着，
/// 将来换成 UI 插件时也不用改外壳。P3b 只不过是把"注册者"从这段代码换成外部程序集。
/// </remarks>
public sealed class MainWindow : Window
{
    private readonly SlotRegistry _slots = new();
    private readonly AppShellViewModel _vm = new();

    public MainWindow()
    {
        Title = "Dougong Desktop";
        Width = 1280;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        AppShell.DeclareSlots(_slots);
        RegisterBuiltInPlaceholders();

        Content = new AppShell(_slots, _vm);

        Opened += (_, _) => DumpLayout();
    }

    /// <summary>
    /// 注册内置占位内容。
    /// </summary>
    /// <remarks>
    /// 用 priority 100 段（内置标准层）。第三方 UI 插件想覆盖时注册更小的值即可
    /// ——这是《Slot 机制规格》§2 定的约定。
    /// </remarks>
    private void RegisterBuiltInPlaceholders()
    {
        const int BuiltIn = 100;

        _slots.Register(ReservedSlots.Sidebar, new SlotOccupant(
            "builtin.sidebar", BuiltIn, MakeTemplate(() =>
                Placeholder("侧边栏", "会话列表 · 工作区导航")))
        );

        _slots.Register(ReservedSlots.Main, new SlotOccupant(
            "builtin.conversation", BuiltIn,
            MakeTemplate(() => Placeholder("对话区", "消息流 · 工具卡片 · 审批")),
            Key: ReservedSlots.Conversation)
        );

        _slots.Register(ReservedSlots.Main, new SlotOccupant(
            "builtin.plugins", BuiltIn,
            MakeTemplate(() => Placeholder("插件", "插件管理 · 已安装列表")),
            Key: "plugins")
        );

        _slots.Register(ReservedSlots.Main, new SlotOccupant(
            "builtin.settings", BuiltIn,
            MakeTemplate(() => Placeholder("设置", "提供商 · 外观 · 诊断")),
            Key: "settings")
        );

        _slots.Register(ReservedSlots.RightBar, new SlotOccupant(
            "builtin.rightbar", BuiltIn, MakeTemplate(() =>
                Placeholder("右栏", "文件 · 改动 · 计划 · 上下文"))
        ));
    }

    private static Control Placeholder(string title, string subtitle) => new Border
    {
        Padding = new Avalonia.Thickness(20),
        Child = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 16,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = subtitle,
                    Opacity = 0.55,
                    FontSize = 12,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "（P3a 占位，内容在后续阶段接入）",
                    Opacity = 0.35,
                    FontSize = 11,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
            },
        },
    };

    private static Avalonia.Controls.Templates.IDataTemplate MakeTemplate(Func<Control> build) =>
        new Avalonia.Controls.Templates.FuncDataTemplate<object>((_, _) => build());

    /// <summary>
    /// 把布局实测结果打到 stderr。
    /// </summary>
    /// <remarks>
    /// <b>用 dump 不用截图</b>：P2 的经验是截图受裁切与 DPI 影响会误判成布局 bug，
    /// 而 Bounds 非零能确定"真的参与了布局"。
    /// </remarks>
    private void DumpLayout()
    {
        var shell = Content as Control;
        Console.Error.WriteLine($"[shell] 窗口 {ClientSize.Width:0} x {ClientSize.Height:0}");

        void Report(string name, Control? c)
        {
            if (c is null) { Console.Error.WriteLine($"[shell] {name,-22} MISSING"); return; }
            var b = c.Bounds;
            Console.Error.WriteLine($"[shell] {name,-22} {b.Width,7:0.0} x {b.Height,-7:0.0} {(b.Width > 0 && b.Height > 0 ? "OK" : "ZERO")}");
        }

        Report("AppShell", shell);
        Report("ResizablePanels", shell?.GetVisualDescendants().OfType<ResizablePanels>().FirstOrDefault());
        Report("Bottom(list)", shell?.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault());

        var panels = shell?.GetVisualDescendants().OfType<ResizablePanel>().ToArray() ?? [];
        Console.Error.WriteLine($"[shell] ResizablePanel 数量 {panels.Length}（期望 3）");
        foreach (var p in panels)
            Report($"  panel:{p.Id}", p);

        Console.Error.WriteLine("[shell] DUMP-END");
    }
}
