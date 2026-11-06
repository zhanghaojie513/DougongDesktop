using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 右栏 dock：文件 / 改动 / 计划 / 上下文 / 终端。
/// </summary>
/// <remarks>
/// <b>五个 tab 里只有三个有真实数据</b>，另外两个必须如实标注"未实现"，
/// 不放假内容。方案文档的原话是"本期只做占位面板，明确标注未实现，
/// 不做假功能"——假功能比没有更糟：用户会以为它坏了。
/// <para>
/// 各 tab 的数据来源与限制：
/// <list type="table">
/// <item><term>文件</term><description>真实：新增 <c>fs/list</c> IPC 命令</description></item>
/// <item><term>改动</term><description>真实：比对工具调用记录里出现过的写操作路径</description></item>
/// <item><term>计划</term><description>真实：复用 P5 的 <c>PlanItem</c></description></item>
/// <item><term>上下文</term><description><b>未实现</b>：SDK 只给累计消耗（<c>UsageLedger</c>），
/// 不给"当前上下文占用多少"</description></item>
/// <item><term>终端</term><description><b>未实现</b>：PTY 是独立进程 + 独立契约，工作量约等于一个阶段</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class RightDock : UserControl
{
    private readonly TKCode.Arc.Tabs _tabs = new();
    private readonly Dictionary<string, TKCode.Arc.TabItem> _tabItems = new(StringComparer.Ordinal);

    /// <summary>各 tab 的 key。</summary>
    public static class Keys
    {
        public const string Files = "files";
        public const string Changes = "changes";
        public const string Plan = "plan";
        public const string Context = "context";
        public const string Terminal = "terminal";
    }

    /// <summary>当前选中的 tab。</summary>
    public string ActiveTab { get; private set; } = Keys.Files;

    /// <summary>用户切换 tab。</summary>
    public event Action<string>? TabChanged;

    /// <summary>用户点开了某个文件。</summary>
    public event Action<string>? FileSelected;

    /// <summary>
    /// 用户点了某个目录，要进入它。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要这个</b>：之前目录是不可点的文字，
    /// 导致 "@" 引用只能看到根目录一层——深层文件引用不到。
    /// </remarks>
    public event Action<string>? DirectoryRequested;

    /// <summary>用户点了面包屑上的某一级，要回到那里。空串表示工作区根。</summary>
    /// <remarks>
    /// <b>和 <see cref="DirectoryRequested"/> 是同一个下游动作，但语义不同</b>：
    /// 进入是"往下走一层"，这里是"跳到某个已知祖先"。分成两个事件是为了
    /// 让订阅方能区分——将来若要做"进入时记住滚动位置、返回时恢复"，
    /// 这两种导航的期望不一致。
    /// </remarks>
    public event Action<string>? DirectoryUpRequested;

    /// <summary>
    /// 当前文件 tab 正在显示哪个目录（相对工作区的路径，根为 <c>""</c>）。
    /// </summary>
    /// <remarks>
    /// <b>要有这个状态才能画面包屑</b>。上一版没有——目录可点之后，
    /// 点进 <c>src/</c> 就再也回不到根目录了：界面上没有任何返回入口，
    /// 只能重启应用。能进不能出，等于把单向门当成了导航。
    /// </remarks>
    public string CurrentDirectory { get; private set; } = string.Empty;

    public RightDock()
    {
        BuildTabs();
        Content = _tabs;
        ShowTab(Keys.Files);
    }

    /// <summary>
    /// 建五个 tab。
    /// </summary>
    /// <remarks>
    /// <b>未实现的两个用 <c>IsEnabled=false</c></b>：Arc 的 <c>TabItem</c>
    /// 会把禁用的 tab 从方向键序列里跳过、也点不中。标题上再写一遍
    /// "（未实现）"，让人不必点进去才发现。
    /// </remarks>
    private void BuildTabs()
    {
        foreach (var (key, label, implemented) in new[]
        {
            (Keys.Files, "文件", true),
            (Keys.Changes, "改动", true),
            (Keys.Plan, "计划", true),
            (Keys.Context, "上下文（未实现）", false),
            (Keys.Terminal, "终端", true),
        })
        {
            var item = new TKCode.Arc.TabItem
            {
                Header = label,
                IsEnabled = implemented,
            };

            _tabItems[key] = item;
            _tabs.Items.Add(item);
        }

        // 切换时按 key 懒加载内容：文件 tab 要等引擎回话，
        // 一次全建出来会白白构造五棵控件树
        _tabs.SelectionChanged += (_, _) =>
        {
            foreach (var (key, item) in _tabItems)
            {
                if (!item.IsSelected) continue;
                if (key == ActiveTab) return;
                ShowTab(key);
                return;
            }
        };
    }

    /// <summary>
    /// 终端面板（P8f F1/F2）。惰性创建——不点这个 tab 就不加载原生库。
    /// </summary>
    private TerminalPanel? _terminal;

    /// <summary>终端面板实例（诊断与自检用）。</summary>
    public TerminalPanel? Terminal => _terminal;

    /// <summary>
    /// 按 key 选中某个 tab（同步 Arc 的选中状态）。
    /// </summary>
    /// <remarks>
    /// <b>不能只调 <see cref="ShowTab"/></b>：那只改内部的 <c>ActiveTab</c>
    /// 并建内容，Arc 的 <c>Tabs</c> 仍高亮原来那个——用户看到的是另一页。
    /// 自检要走真实的选中路径，否则验证的不是用户会走的代码。
    /// </remarks>
    public void SelectTab(string key)
    {
        if (!_tabItems.TryGetValue(key, out var item)) return;
        item.IsSelected = true;
    }

    private Control BuildTerminalTab()
    {
        if (_terminal is null)
        {
            _terminal = new TerminalPanel();

            // 自检：灌一段带转义序列的内容，验证 VT 真的在解析。
            // 不灌的话面板是空白的，"空白"与"坏了"看起来一样。
            if (Environment.GetEnvironmentVariable("DOUGONG_UI_TERMINAL") is "1")
                _terminal.Probe();
        }

        return _terminal;
    }

    /// <summary>切换 tab 并加载内容。</summary>
    public void ShowTab(string key)
    {
        ActiveTab = key;

        if (_tabItems.TryGetValue(key, out var item))
            item.Content = BuildBody(key);

        TabChanged?.Invoke(key);
    }

    private Control BuildBody(string key) => key switch
    {
        Keys.Files => BuildFilesTab(),
        Keys.Changes => BuildChangesTab(),
        Keys.Plan => BuildPlanTab(),
        Keys.Terminal => BuildTerminalTab(),
        _ => BuildNotImplemented(key),
    };

    /// <summary>把引擎回来的目录内容填进文件 tab。</summary>
    public void ApplyDirectory(DirectoryListedEvent listed)
    {
        // 出错时不推进 CurrentDirectory：用户点了 src/ 但读失败，
        // 面包屑若跟着变成 src，他会以为自己已经在 src 里
        if (listed.Error is not { Length: > 0 })
            CurrentDirectory = listed.Path ?? string.Empty;

        if (_tabItems.TryGetValue(Keys.Files, out var item))
            item.Content = BuildFilesTab(listed);
    }

    /// <summary>把最新的计划填进计划 tab。</summary>
    public void ApplyPlan(PlanItem? plan)
    {
        _latestPlan = plan;
        if (_tabItems.TryGetValue(Keys.Plan, out var item))
            item.Content = BuildPlanTab();
    }

    private PlanItem? _latestPlan;

    private Control BuildFilesTab(DirectoryListedEvent? listed = null)
    {
        if (listed is null)
        {
            return Hint("正在读取工作区目录…");
        }

        if (listed.Error is { Length: > 0 })
        {
            // 读失败时仍画面包屑：用户停在一个读不了的目录里，
            // 更需要一条回去的路，否则又成了死胡同
            return new StackPanel
            {
                Children = { BuildBreadcrumb(), Hint(listed.Error) },
            };
        }

        if (listed.Entries.Count == 0)
        {
            return new StackPanel
            {
                Children = { BuildBreadcrumb(), Hint("（空目录）") },
            };
        }

        var list = new StackPanel { Spacing = 1, Margin = new Thickness(9, 4) };

        foreach (var entry in listed.Entries)
        {
            var label = new TextBlock
            {
                Text = entry.IsDirectory ? $"{entry.Name}/" : entry.Name,
                FontSize = 12,
                Opacity = entry.IsDirectory ? 0.9 : 0.75,
                FontFamily = entry.IsDirectory
                    ? FontFamily.Default
                    : DshTheme.MonoFont,
            };

            // 目录与文件都可点，但**点击含义不同**：
            //   目录 → 进入（发 DirectoryRequested）
            //   文件 → 选中（发 FileSelected）
            //
            // 这一点之前做错了：目录只是不可点的文字，
            // 于是 "@" 引用只能看到根目录那一层，用户引用不了
            // src/App/Shell/X.cs 这种深层文件——功能等于半残。
            var button = new Button
            {
                Content = label,
                Padding = new Thickness(4, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };

            var path = string.IsNullOrEmpty(listed.Path)
                ? entry.Name
                : $"{listed.Path}/{entry.Name}";

            if (entry.IsDirectory)
                button.Click += (_, _) => DirectoryRequested?.Invoke(path);
            else
                button.Click += (_, _) => FileSelected?.Invoke(path);

            list.Children.Add(button);
        }

        if (listed.Truncated is { Length: > 0 })
            list.Children.Add(Hint(listed.Truncated));

        return new ScrollViewer
        {
            Content = new StackPanel { Children = { BuildBreadcrumb(), list } },
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    /// <summary>
    /// 文件 tab 顶部的路径面包屑：每一级可点，点了回到那一级。
    /// </summary>
    /// <remarks>
    /// <b>这是"能进不能出"的修法</b>。上一版把目录改成可点之后，
    /// 点进 <c>src/</c> 就没有任何返回入口了——界面上没有向上按钮，
    /// 也没有路径显示，用户只能重启应用才能回到根目录。
    /// 加这一条同时解决两件事：显示当前位置、提供回退。
    /// <para>
    /// <b>为什么用面包屑而不是"← 返回"单按钮</b>：在
    /// <c>src/App/Shell/</c> 这种三层深的位置，返回一次只退一层，
    /// 想回根要点三次；面包屑一次到位，而且明确告诉用户"你在哪"。
    /// </para>
    /// </remarks>
    private Control BuildBreadcrumb()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(9, 6, 9, 2),
            Spacing = 1,
        };

        row.Children.Add(BreadcrumbSegment("工作区", string.Empty));

        var accumulated = string.Empty;
        foreach (var part in CurrentDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = accumulated.Length == 0 ? part : $"{accumulated}/{part}";

            row.Children.Add(new TextBlock
            {
                Text = "/",
                FontSize = 11,
                Opacity = 0.35,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0),
            });

            row.Children.Add(BreadcrumbSegment(part, accumulated));
        }

        return row;
    }

    private Control BreadcrumbSegment(string text, string path)
    {
        var isCurrent = path == CurrentDirectory;

        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 11,
                // 当前所在层加重，其他层淡一些——用户扫一眼就知道自己在哪
                Opacity = isCurrent ? 0.95 : 0.6,
            },
            Padding = new Thickness(4, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Columns.RadiusSm),
        };

        // 当前层不可点：点了也不会发生任何事，做成可点是在骗用户
        if (isCurrent)
        {
            button.IsEnabled = false;
            return button;
        }

        button.Click += (_, _) => DirectoryUpRequested?.Invoke(path);
        return button;
    }

    /// <summary>
    /// 改动 tab：显示回合前后由 SDK 版本比对得出的真实差异。
    /// </summary>
    /// <remarks>
    /// <b>这里原来是自己猜"哪些工具碰过哪些路径"</b>——绕路了。
    /// SDK 有 <c>FileVersionSet.Capture</c> / <c>Diff</c>，按内容哈希比对，
    /// 比"猜"可靠得多：猜的方法会把"工具尝试写但失败"也算成改动，
    /// 而漏掉工具之外的变化（比如编辑器的外部修改）。
    /// <para>
    /// <b>显示的是变更描述，不是行级 diff</b>：SDK 的 <c>FileVersion</c> 只存
    /// 长度、时间、内容哈希，<b>不存原内容</b>——做不到"第几行改成什么"。
    /// 这一点在界面上写明，否则用户会以为 diff 视图坏了。
    /// </para>
    /// </remarks>
    private Control BuildChangesTab()
    {
        if (_latestDiff is null)
        {
            return Hint("还没有运行过回合。\n\n" +
                        "回合结束后这里会显示本回合的文件改动。");
        }

        if (!_latestDiff.HasChanges)
        {
            return Hint("本回合没有改动文件。\n\n" +
                        "（比对的是文件内容哈希，改动后又改回去不会被误报。）");
        }

        var list = new StackPanel { Spacing = 6, Margin = new Thickness(9, 8) };

        // SDK 的 Diff 是多行文本，一行一个文件。按行拆开展示，
        // 让长路径能换行而不是横向撑爆右栏。
        var lines = _latestDiff.Diff.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            list.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 11,
                FontFamily = DshTheme.MonoFont,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17,
            });
        }

        list.Children.Add(new TextBlock
        {
            Text = "只显示变更描述（长度/存在性变化），没有行级差异——" +
                   "SDK 的文件版本只保存内容哈希，不保存原文。",
            FontSize = 10,
            Opacity = 0.45,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        return new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    private WorkspaceChangedEvent? _latestDiff;

    /// <summary>把回合结束时的改动填进改动 tab。</summary>
    public void ApplyWorkspaceChanges(WorkspaceChangedEvent changed)
    {
        _latestDiff = changed;
        if (_tabItems.TryGetValue(Keys.Changes, out var item))
            item.Content = BuildChangesTab();
    }

    private static IEnumerable<(string Key, string Value)> ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) yield break;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                yield return (prop.Name, prop.Value.GetString() ?? "");
            }
        }
    }

    private Control BuildPlanTab()
    {
        if (_latestPlan is null || _latestPlan.Steps.Count == 0)
        {
            return Hint("还没有计划。\n\n模型在开始多步骤任务时会声明计划，之后每完成一步更新。");
        }

        var steps = new Avalonia.Collections.AvaloniaList<TKCode.Arc.AgentPlanStep>();
        foreach (var step in _latestPlan.Steps)
        {
            steps.Add(new TKCode.Arc.AgentPlanStep
            {
                Title = step.Content,
                State = step.Status switch
                {
                    "completed" => TKCode.Arc.AgentPlanStepState.Done,
                    "in_progress" => TKCode.Arc.AgentPlanStepState.Active,
                    _ => TKCode.Arc.AgentPlanStepState.Pending,
                },
            });
        }

        return new ScrollViewer
        {
            Content = new Border
            {
                Margin = new Thickness(6),
                Child = new TKCode.Arc.AgentPlan { Steps = steps },
            },
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    /// <summary>
    /// 未实现的 tab：如实说明缺什么，以及为什么缺。
    /// </summary>
    /// <remarks>
    /// <b>写清"为什么未实现"而不是只写"未实现"</b>：前者让用户知道这是
    /// 有意的取舍、以及卡在哪；后者看起来像坏了。
    /// </remarks>
    private static Control BuildNotImplemented(string key)
    {
        var (title, reason) = key switch
        {
            Keys.Context => (
                "上下文用量面板未实现",
                "SDK 目前只提供累计 token 消耗（UsageLedger 按调用记账），\n" +
                "没有提供「当前上下文占用了多少」这个数据。\n\n" +
                "窗口大小是已知的（能力表），但已用量需要运行时按当前消息列表估算——\n" +
                "等 SDK 给出该数据后接上，不在这里用估算值冒充实际用量。"),
            _ => ("未知面板", key),
        };

        return new Border
        {
            Padding = new Thickness(14, 18),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = reason,
                        FontSize = 11,
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18,
                    },
                },
            },
        };
    }

    private static Control Hint(string text) => new Border
    {
        Padding = new Thickness(14, 18),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        },
    };
}
