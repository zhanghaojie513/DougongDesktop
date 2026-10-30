using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DougongDesktop.Ipc;

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
            (Keys.Terminal, "终端（未实现）", false),
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
        _ => BuildNotImplemented(key),
    };

    /// <summary>把引擎回来的目录内容填进文件 tab。</summary>
    public void ApplyDirectory(DirectoryListedEvent listed)
    {
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
            return Hint(listed.Error);
        }

        if (listed.Entries.Count == 0)
        {
            return Hint("（空目录）");
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
                    : new FontFamily("Geist Mono, Consolas, monospace"),
            };

            if (entry.IsDirectory)
            {
                list.Children.Add(label);
                continue;
            }

            // 只有文件可点：目录的展开留给后续（需要维护路径栈，
            // 本期先保证"能看到工作区里有什么"这一件事做对）
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
            button.Click += (_, _) => FileSelected?.Invoke(path);

            list.Children.Add(button);
        }

        if (listed.Truncated is { Length: > 0 })
            list.Children.Add(Hint(listed.Truncated));

        return new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
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
                FontFamily = new FontFamily("Geist Mono, Consolas, monospace"),
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
            Keys.Terminal => (
                "终端未实现",
                "PTY 是独立进程 + 独立契约，工作量约等于一个阶段。\n\n" +
                "本期不做假终端：能显示但打不了字的终端比没有更糟。"),
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
