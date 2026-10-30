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

    /// <summary>把本回合的工具调用记录填进改动 tab。</summary>
    public void ApplyToolActivity(IReadOnlyList<ToolCallItem> tools)
    {
        _latestTools = tools;
        if (_tabItems.TryGetValue(Keys.Changes, out var item))
            item.Content = BuildChangesTab();
    }

    private PlanItem? _latestPlan;
    private IReadOnlyList<ToolCallItem> _latestTools = [];

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
    /// 改动 tab：从工具调用记录里推出现在哪些文件被动过。
    /// </summary>
    /// <remarks>
    /// <b>不是真的 diff</b>：SDK 没有给出改动前的原文，所以这里只能列出
    /// "哪些路径被写操作碰过"。方案文档写的是"需 SDK 提供 diff 或自行比对"，
    /// 自行比对需要保存每个文件的读前快照——那是 P8 持久化的工作量。
    /// <para>
    /// <b>标注清楚"只有路径没有差异"</b>：只显示路径却让人以为是 diff 视图，
    /// 用户点开发现没有差异说明，会认为功能坏了。
    /// </para>
    /// </remarks>
    private Control BuildChangesTab()
    {
        // 写类工具：只有这些会改文件。读类工具碰过的文件不算"改动"
        var writeTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "write_file", "edit_file", "create_file", "delete_file",
            "apply_patch", "shell", "shell_command",
        };

        var touched = new List<(string Tool, string Path)>();

        foreach (var tool in _latestTools)
        {
            if (!writeTools.Contains(tool.ToolName)) continue;

            foreach (var (key, value) in ParseArgs(tool.ArgumentsJson))
            {
                if (key is "path" or "file" or "filename" or "target")
                    touched.Add((tool.ToolName, value));
            }
        }

        if (touched.Count == 0)
        {
            return Hint("本回合还没有改动文件。\n\n" +
                        "（这里只统计写类工具调用过的路径，不提供行级差异——" +
                        "差异需要保存文件改动前的原文，属于持久化的范围。）");
        }

        var list = new StackPanel { Spacing = 6, Margin = new Thickness(9, 6) };
        foreach (var (tool, path) in touched.Distinct())
        {
            list.Children.Add(new TextBlock
            {
                Text = $"{path}",
                FontSize = 12,
                FontFamily = new FontFamily("Geist Mono, Consolas, monospace"),
                TextWrapping = TextWrapping.Wrap,
            });
            list.Children.Add(new TextBlock
            {
                Text = $"由 {tool} 写入",
                FontSize = 10,
                Opacity = 0.5,
                Margin = new Thickness(0, 0, 0, 2),
            });
        }

        return new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
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
