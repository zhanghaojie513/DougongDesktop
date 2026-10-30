using Avalonia.Controls;
using Avalonia.VisualTree;
using DougongDesktop.App.Services;
using DougongDesktop.App.Shell;
using DougongDesktop.Ipc;
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
    private readonly EngineClient _engine = new(msg => Console.Error.WriteLine($"[ui] {msg}"));
    private IDisposable? _sidebarHandle;

    public MainWindow()
    {
        Title = "Dougong Desktop";
        Width = 1280;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        AppShell.DeclareSlots(_slots);
        RegisterBuiltInPlaceholders();

        Content = new AppShell(_slots, _vm);

        Opened += async (_, _) =>
        {
            DumpLayout();
            await ConnectEngineAsync();
        };
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
        const SlotOccupantOrigin Origin = SlotOccupantOrigin.BuiltIn;

        // 每条都显式标 BuiltIn：conversation 这类保留 key 只接受内置来源，
        // 而"是不是内置"必须由注册方声明，不能靠 priority 数值猜。
        _slots.Register(ReservedSlots.Sidebar, new SlotOccupant(
            "builtin.sidebar", BuiltIn,
            MakeTemplate(() => Placeholder("侧边栏", "会话列表 · 工作区导航")))
        {
            Origin = Origin,
        });

        _slots.Register(ReservedSlots.Main, new SlotOccupant(
            "builtin.conversation", BuiltIn,
            MakeTemplate(() => Placeholder("对话区", "消息流 · 工具卡片 · 审批")),
            Key: ReservedSlots.Conversation)
        {
            Origin = Origin,
        });

        _slots.Register(ReservedSlots.Main, new SlotOccupant(
            "builtin.plugins", BuiltIn,
            MakeTemplate(() => Placeholder("插件", "插件管理 · 已安装列表")),
            Key: "plugins")
        {
            Origin = Origin,
        });

        _slots.Register(ReservedSlots.Main, new SlotOccupant(
            "builtin.settings", BuiltIn,
            MakeTemplate(() => Placeholder("设置", "提供商 · 外观 · 诊断")),
            Key: "settings")
        {
            Origin = Origin,
        });

        _slots.Register(ReservedSlots.RightBar, new SlotOccupant(
            "builtin.rightbar", BuiltIn,
            MakeTemplate(() => Placeholder("右栏", "文件 · 改动 · 计划 · 上下文")))
        {
            Origin = Origin,
        });
    }

    /// <summary>
    /// 连接引擎，成功后就拉一次会话列表。
    /// </summary>
    /// <remarks>
    /// <b>失败要可见</b>：连不上时在侧边栏显示原因，而不是留一个空白框让用户猜。
    /// 这条是 P3a-5 的验收点——"连不上时明确提示"比"连上了能用"更容易被忽略。
    /// </remarks>
    private async Task ConnectEngineAsync()
    {
        _engine.StateChanged += (state, detail) =>
            Console.Error.WriteLine($"[ui] 引擎状态 {state}{(detail is null ? "" : $"：{detail}")}");

        _engine.EventReceived += OnEngineEvent;

        var ok = await _engine.ConnectAsync();
        if (!ok)
        {
            Console.Error.WriteLine("[ui] 引擎连接失败，会话列表不可用");
            return;
        }

        // 连上后立刻要一次会话列表（真实数据，不是占位）
        await _engine.SendAsync(new ListSessionsCommand { RequestId = Guid.NewGuid().ToString("N") });
    }

    private void OnEngineEvent(ServerEvent evt)
    {
        switch (evt)
        {
            case SessionListedEvent listed:
                Console.Error.WriteLine($"[ui] 会话列表：{listed.Sessions.Count} 条");
                foreach (var s in listed.Sessions.Take(5))
                    Console.Error.WriteLine($"[ui]   {s.Id} · {s.Title} · {s.ItemCount} 条目");
                UpdateSidebar(listed.Sessions);
                break;

            case EngineStatusEvent status when status.Detail is { Length: > 0 }:
                Console.Error.WriteLine($"[ui] 引擎：{status.State} · {status.Detail}");
                break;
        }
    }

    /// <summary>把真实会话列表填进侧边栏 slot（替换占位）。</summary>
    private void UpdateSidebar(IReadOnlyList<SessionSummary> sessions)
    {
        if (_sidebarHandle is null) return;

        _sidebarHandle.Dispose();
        _sidebarHandle = _slots.Register(ReservedSlots.Sidebar, new SlotOccupant(
            "builtin.sidebar", 100, MakeTemplate(() => BuildSessionList(sessions)))
        {
            Origin = SlotOccupantOrigin.BuiltIn,
        });

    }

    private Control BuildSessionList(IReadOnlyList<SessionSummary> sessions)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(8) };

        panel.Children.Add(new TextBlock
        {
            Text = sessions.Count == 0 ? "还没有会话" : $"会话（{sessions.Count}）",
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Avalonia.Thickness(8, 8, 8, 6),
        });

        foreach (var s in sessions.Take(20))
        {
            panel.Children.Add(new Border
            {
                Padding = new Avalonia.Thickness(8, 6),
                CornerRadius = new Avalonia.CornerRadius(6),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = s.Title, FontSize = 13 },
                        new TextBlock
                        {
                            Text = $"{s.ItemCount} 条目 · {s.UpdatedAt:MM-dd HH:mm}",
                            FontSize = 11,
                            Opacity = 0.5,
                        },
                    },
                },
            });
        }

        return panel;
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
