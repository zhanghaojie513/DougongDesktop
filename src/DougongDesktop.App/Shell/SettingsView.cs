using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;
using TKCode.Arc;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 设置面板（P8c）：主区 <c>settings</c> 面板的真实实现。
/// </summary>
/// <remarks>
/// <b>外壳用 Arc 的 <see cref="SettingsPage"/></b>，不自建：左栏分区导航
/// （图标 + 标题 + 徽标，选中胶囊会滑动）、窄容器自动退化为单列、
/// 脏标记时底部浮出保存条，这些都是现成的。
/// <para>
/// <b>分区先只做能填真数据的</b>：提供商、权限、外观、诊断、关于。
/// 每个分区里**只放有真实来源的行**——没数据的分区宁可不出现，
/// 也不要摆一排"即将推出"，那种界面会训练用户忽略整页。
/// </para>
/// <para>
/// <b>密钥这一轮不落盘</b>（C3 的一半）：当前 provider 的 key 只能来自
/// 环境变量 <c>DOUGONG_PROVIDER_KEY</c>，界面上**如实显示"由环境变量提供"
/// 而不是给一个输入框**。给输入框但没有持久化，用户填完重启就丢，
/// 那比不显示更糟——他会以为自己保存了。
/// </para>
/// </remarks>
public sealed class SettingsView : UserControl
{
    private readonly SettingsPage _page;
    private readonly TextBlock _providerSummary;
    private readonly TextBlock _modelSummary;

    /// <summary>当前模型的能力详情（窗口/输出）。</summary>
    private readonly TextBlock _currentModelDetail = Body("—");

    /// <summary>可浏览的模型清单，按 provider 分组。</summary>
    private readonly StackPanel _modelList = new() { Spacing = 0 };
    private readonly TextBlock _engineSummary;
    private readonly TextBlock _checkCount;
    private readonly TextBlock _pluginSummary;
    private readonly StackPanel _pluginList;

    /// <summary>构造设置面板。</summary>
    public SettingsView()
    {
        _providerSummary = Body("—");
        _modelSummary = Body("—");
        _engineSummary = Body("—");
        _checkCount = Body("—");
        _pluginSummary = Body("—");
        _pluginList = new StackPanel { Spacing = 0 };

        _page = new SettingsPage
        {
            Title = "设置",
            CompactWidth = 640,
            Items =
            {
                BuildProvidersSection(),
                BuildPluginsSection(),
                BuildPermissionsSection(),
                BuildAppearanceSection(),
                BuildDiagnosticsSection(),
            },
        };

        Content = _page;

        // 自检用：直接选中某个分区（0=提供商 1=权限 2=外观 3=诊断），
        // 省得截图时还得手动点。只在设了环境变量时生效。
        if (int.TryParse(
                Environment.GetEnvironmentVariable("DOUGONG_SETTINGS_SECTION"), out var section)
            && section >= 0 && section < _page.ItemCount)
        {
            _page.SelectedIndex = section;
        }
    }

    /// <summary>
    /// 用引擎报回的模型目录填充"提供商"分区。
    /// </summary>
    /// <param name="models">模型清单。</param>
    /// <param name="providers">provider 标识。</param>
    /// <param name="currentModelId">当前模型。</param>
    /// <remarks>
    /// <b>数据来自 <c>models/list</c> 而不是本地常量</b>：模型能力表在 SDK 的
    /// <c>ProviderCatalog</c> 里，而 App 层禁止引用 SDK。这条链路已经是现成的
    /// （composer 的模型 pill 就用它），设置页复用同一份，不新开协议。
    /// </remarks>
    public void SetModelCatalog(
        IReadOnlyList<ModelInfo> models,
        IReadOnlyList<string> providers,
        string? currentModelId)
    {
        _providerSummary.Text = $"{providers.Count} 家";
        _modelSummary.Text = $"{models.Count} 个模型";

        var current = models.FirstOrDefault(m => m.Id == currentModelId);
        if (current is not null)
        {
            _modelSummary.Text += $" · 当前 {current.Id}";
            if (current.ContextWindow > 0)
                _modelSummary.Text += $"（窗口 {current.ContextWindow:N0}）";
        }
        _currentModelDetail.Text = current is null ? "—" : DescribeModel(current);

        // 填充可浏览清单：按 provider 分组，只列能力数据。
        //
        // **不做下拉切换**：模型是引擎启动时从环境变量读入的只读快照
        // （EngineOptions.Model 是 init 属性），会话内没有改它的路径，
        // 契约里也只有 model/list 没有 switch。给一个切不动的下拉
        // 比不给更糟——用户会以为点了就该生效。
        _modelList.Children.Clear();
        foreach (var group in models
                     .GroupBy(m => m.ProviderId)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            _modelList.Children.Add(new TextBlock
            {
                Text = $"{group.Key}（{group.Count()}）",
                Margin = new Thickness(0, 10, 0, 4),
                Opacity = 0.6,
            });

            // **当前模型必须始终可见**：它在列表里排第 20 位时，只取前 8 个
            // 就看不到 ● 标记，用户会以为"我的模型不在目录里"。
            // 所以先按 id 排序，再把当前模型提到最前，然后取前 8 个。
            var ordered = group
                .OrderBy(m => m.Id == currentModelId ? 0 : 1)
                .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var m in ordered.Take(8))
            {
                var isCurrent = m.Id == currentModelId;
                _modelList.Children.Add(Row(
                    (isCurrent ? "● " : "") + m.Id,
                    DescribeModel(m),
                    Body("")));
            }

            if (group.Count() > 8)
            {
                _modelList.Children.Add(new TextBlock
                {
                    Text = $"… 另有 {group.Count() - 8} 个",
                    Margin = new Thickness(0, 2, 0, 0),
                    Opacity = 0.5,
                });
            }
        }
    }

    /// <summary>
    /// 描述密钥的形态，**不暴露内容**。
    /// </summary>
    /// <remarks>
    /// <b>为什么要这个</b>：用户最常见的困惑是"我明明配了，为什么说没配"——
    /// 常见原因是变量名拼错、值带引号、或复制时多了空格。
    /// 报长度与前缀能定位这几类问题，而完整密钥不该渲染到界面上。
    /// </remarks>
    private static string DescribeKeyShape(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "—";

        var notes = new List<string> { $"{key.Length} 字符" };

        if (key.StartsWith("sk-", StringComparison.Ordinal))
            notes.Add("sk- 前缀");
        else
            notes.Add("**不以 sk- 开头**（多半不是 API key，可能是 OAuth token）");

        if (key.Length > 0 && (char.IsWhiteSpace(key[0]) || char.IsWhiteSpace(key[^1])))
            notes.Add("**首尾有空白字符**（复制时带进来的，会导致认证失败）");

        if (key.Contains('"') || key.Contains('\''))
            notes.Add("**含引号**（环境变量里不该再包引号）");

        return string.Join(" · ", notes);
    }

    /// <summary>把模型能力写成人看得懂的一行。</summary>
    /// <remarks>
    /// <b>只写有真实来源的字段</b>：窗口与输出上限来自能力表；
    /// 价格、营销文案 SDK 不给，编造会让用户看到假信息。
    /// 未知就写"—"，不猜。
    /// </remarks>
    private static string DescribeModel(ModelInfo m)
    {
        var parts = new List<string>();
        parts.Add(m.ContextWindow > 0 ? $"窗口 {FormatTokens(m.ContextWindow)}" : "窗口未知");
        parts.Add(m.MaxOutputTokens > 0 ? $"输出 {FormatTokens(m.MaxOutputTokens)}" : "输出未知");
        if (m.SupportsReasoning) parts.Add("支持推理");
        return string.Join(" · ", parts);
    }

    private static string FormatTokens(int n) => n >= 1000 ? $"{n / 1000}K" : n.ToString();

    /// <summary>填充引擎状态与检查数等诊断信息。</summary>
    public void SetDiagnostics(string engineState, string providerProtocol, int checkCount)
    {
        _engineSummary.Text = engineState;
        _checkCount.Text = $"{checkCount} 条";
        _ = providerProtocol;
    }

    // ── 分区 ─────────────────────────────────────────────────

    /// <summary>提供商：模型目录来源 + 当前选择 + key 的存放位置（只读）。</summary>
    private SettingsSection BuildProvidersSection()
    {
        var key = Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY");
        var keySource = Body(
            string.IsNullOrWhiteSpace(key)
                ? "未配置（环境变量 DOUGONG_PROVIDER_KEY）"
                : "已通过环境变量 DOUGONG_PROVIDER_KEY 提供");

        // **只报形态，不报内容**：长度与前缀足以判断"配错了没有"，
        // 而完整密钥不该出现在界面上（截图会带走它）。
        var keyShape = Body(DescribeKeyShape(key));

        var keyHint = Body(
            string.IsNullOrWhiteSpace(key)
                ? "在终端设置后重启应用：\n" +
                  "export DOUGONG_PROVIDER_KEY=...\n" +
                  "export DOUGONG_PROVIDER_BASE=...\n" +
                  "export DOUGONG_PROVIDER_MODEL=..."
                : "改了环境变量需要重启应用才生效（引擎启动时读取）。");

        return new SettingsSection
        {
            Header = "提供商",
            Description = "模型目录来自引擎的能力表；桌面端不缓存、不猜测。",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new SettingsGroup
                    {
                        Header = "模型目录",
                        Items =
                        {
                            Row("提供商数量", "引擎报回的 provider 组数", _providerSummary),
                            Row("模型数量", "含上下文窗口等能力数据", _modelSummary),
                            Row("当前模型", "由引擎启动时的配置决定", _currentModelDetail),
                        },
                    },
                    new SettingsGroup
                    {
                        Header = "全部模型",
                        Description = "按提供商分组。● 标记当前在用的那个。",
                        Items = { _modelList },
                    },
                    new SettingsGroup
                    {
                        Header = "为什么不能在这里切换",
                        Description = "这不是没做，是当前架构做不到。",
                        Items =
                        {
                            Row("模型来源",
                                "引擎启动时从 DOUGONG_PROVIDER_MODEL 读入，是只读快照",
                                Body("启动时固定")),
                            Row("契约支持",
                                "HostProtocol 只有 model/list（查询），没有切换命令",
                                Body("仅查询")),
                        },
                    },
                    new SettingsGroup
                    {
                        Header = "凭据",
                        Description = "本轮不支持在界面录入密钥——不落盘的话填了也会丢。",
                        Items =
                        {
                            Row("API Key", "只从环境变量读取", keySource),
                            Row("形态检查", "只看长度与前缀，不显示内容", keyShape),
                            Row("怎么改", "密钥不进配置文件，也不进界面", keyHint),
                        },
                    },
                },
            },
        };
    }

    /// <summary>
    /// 用已加载的 UI 插件填充"插件"分区。
    /// </summary>
    /// <param name="plugins">已加载的插件。</param>
    /// <remarks>
    /// <b>只显示"已加载"的，不显示"加载失败"的</b>——失败原因走 stderr 诊断，
    /// 放进设置页会让普通用户看到一堆他无法处理的报错。这条与
    /// "加载失败不能让 App 起不来"是同一个取舍。
    /// <para>
    /// <b>按 Origin 分组</b>：内置与第三方安装的混在一起，用户分不清
    /// 哪个能卸。这正是 dsh 插件页的分组依据。
    /// </para>
    /// </remarks>
    public void SetPlugins(IReadOnlyList<LoadedExtension> plugins)
    {
        _pluginList.Children.Clear();
        _pluginSummary.Text = plugins.Count == 0 ? "无" : $"{plugins.Count} 个";

        if (plugins.Count == 0)
        {
            _pluginList.Children.Add(Body(
                "还没有加载任何 UI 插件。用环境变量 DOUGONG_UI_PLUGINS 指定插件目录。"));
            return;
        }

        foreach (var group in plugins
                     .GroupBy(p => p.Origin)
                     .OrderBy(g => g.Key))
        {
            var title = group.Key == SlotOccupantOrigin.BuiltIn ? "内置" : "第三方";
            _pluginList.Children.Add(new TextBlock
            {
                Text = $"{title}（{group.Count()}）",
                Margin = new Thickness(0, 8, 0, 4),
                Opacity = 0.6,
            });

            foreach (var p in group)
            {
                var name = string.IsNullOrWhiteSpace(p.Extension.DisplayName)
                    ? p.Extension.Id
                    : $"{p.Extension.DisplayName}（{p.Extension.Id}）";

                _pluginList.Children.Add(Row(
                    name,
                    $"槽位 {p.Extension.SlotKey}" +
                    (string.IsNullOrWhiteSpace(p.Extension.SlotKeyValue)
                        ? ""
                        : $" / {p.Extension.SlotKeyValue}"),
                    Body(Path.GetFileName(p.AssemblyFile))));
            }
        }
    }

    /// <summary>插件：已加载的 UI 插件清单（C4）。</summary>
    private SettingsSection BuildPluginsSection() => new()
    {
        Header = "插件",
        Description = "当前产品内加载的 UI 插件。加载失败的原因走运行时日志，不在这里显示。",
        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new SettingsGroup
                {
                    Header = "已加载",
                    Items = { Row("插件数量", "含内置与第三方", _pluginSummary) },
                },
                _pluginList,
            },
        },
    };

    /// <summary>权限：审批模式的只读说明（C5 的范围）。</summary>
    /// <remarks>
    /// <b>这里不放可切换的开关</b>：权限预设要下发到引擎才算数，而现在
    /// <c>HostProtocol</c> 没有承载它的字段（与 C8 同一类缺口）。
    /// 放一个切了不生效的开关，比不放更糟。
    /// </remarks>
    /// <summary>预设选择器与当前档位（诊断用）。</summary>
    private readonly ComboBox _presetPicker = new()
    {
        MinWidth = 180,
        PlaceholderText = "（等待引擎）",
    };

    private readonly TextBlock _presetState = Body("—");

    /// <summary>用户切换预设。参数是预设 key。</summary>
    public event Action<string>? PermissionPresetRequested;

    /// <summary>下拉里的 key，与显示名分开存。</summary>
    private readonly List<string> _presetKeys = [];

    /// <summary>抑制回填时的 SelectionChanged 回环。</summary>
    private bool _updatingPresets;

    /// <summary>
    /// 权限分区：预设选择器 + 真实状态。
    /// </summary>
    /// <remarks>
    /// <b>这不是摆设</b>：选中的 key 会经 <c>permission/switch</c> 发给引擎，
    /// 引擎侧走 <c>PermissionPresets.SwitchAsync</c>，审批链按新档位裁决。
    /// 实测（2026-11-10）：interactive 时审批器被问 1 次，切到 unattended 后 0 次。
    /// <para>
    /// <b>回填时的回环要挡</b>：引擎回 <c>permission/listed</c> 后我们要更新
    /// 下拉选中项，而设 <c>SelectedIndex</c> 会再触发一次 <c>SelectionChanged</c>，
    /// 又发一遍 <c>permission/switch</c>——无限往返。用 <c>_updatingPresets</c> 隔开。
    /// </para>
    /// </remarks>
    private SettingsSection BuildPermissionsSection()
    {
        _presetPicker.SelectionChanged += (_, _) =>
        {
            if (_updatingPresets) return;
            var i = _presetPicker.SelectedIndex;
            if (i < 0 || i >= _presetKeys.Count) return;

            PermissionPresetRequested?.Invoke(_presetKeys[i]);
        };

        return new SettingsSection
        {
            Header = "权限",
            Description = "选一档审批策略；切换后引擎立即按新档位裁决。",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new SettingsGroup
                    {
                        Header = "审批档位",
                        Items =
                        {
                            Row("预设", "由引擎裁决，不是只记一个值", _presetPicker),
                            Row("当前生效", "以引擎回报为准", _presetState),
                        },
                    },
                    new SettingsGroup
                    {
                        Header = "不随预设改变的部分",
                        Description = "这几条是每次调用都要看的，预设管不到。",
                        Items =
                        {
                            Row("拒绝后是否停止回合",
                                "由审批卡片上的「拒绝并停止」决定",
                                Body("每次可选")),
                            Row("未知权限的工具",
                                "预设不覆盖：MCP 等来源权限未知时仍逐次询问",
                                Body("始终询问")),
                        },
                    },
                },
            },
        };
    }

    /// <summary>
    /// 用引擎回报的权限状态填充下拉。
    /// </summary>
    /// <param name="state">引擎回的预设状态。</param>
    /// <remarks>
    /// <b>以引擎为准，不用本地猜测</b>：切换失败时引擎回的是**当前真实状态**
    /// （例如 key 不存在，它仍返回 old），界面照它渲染即可。
    /// 本地先改再等服务端确认会让失败时显示成成功。
    /// </remarks>
    public void SetPermissionPresets(PermissionPresetState state)
    {
        _updatingPresets = true;
        try
        {
            _presetKeys.Clear();
            _presetPicker.Items.Clear();

            foreach (var option in state.Options)
            {
                _presetKeys.Add(option.Key);
                _presetPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{option.Name}（{option.Key}）",
                    // 派生出来的 custom 之类不可选：选了也没语义
                    IsEnabled = option.Selectable,
                });
            }

            var index = _presetKeys.FindIndex(k => k == state.CurrentKey);
            _presetPicker.SelectedIndex = index;
            _presetState.Text = state.CurrentName ?? state.CurrentKey ?? "—";
        }
        finally
        {
            _updatingPresets = false;
        }
    }

    /// <summary>当前下拉选中的 key（诊断/自检用）。</summary>
    public string? SelectedPresetKey =>
        _presetPicker.SelectedIndex >= 0 && _presetPicker.SelectedIndex < _presetKeys.Count
            ? _presetKeys[_presetPicker.SelectedIndex]
            : null;

    /// <summary>外观：主题跟随与字体（有真实来源的两项）。</summary>
    private static SettingsSection BuildAppearanceSection() => new()
    {
        Header = "外观",
        Description = "配色与排版来自 dsh 的 design token，不做本地覆盖。",
        Content = new SettingsGroup
        {
            Header = "主题",
            Items =
            {
                Row("配色", "跟随系统深浅色", Body("跟随系统")),
                Row("等宽字体", "内嵌 Geist Mono（跨平台一致）",
                    Body(DshTheme.MonoFont.Name ?? "Geist Mono")),
            },
        },
    };

    /// <summary>诊断：引擎状态、检查数。</summary>
    private SettingsSection BuildDiagnosticsSection() => new()
    {
        Header = "诊断",
        Description = "运行时观测，出问题时先看这里。",
        Content = new SettingsGroup
        {
            Header = "运行状态",
            Items =
            {
                Row("引擎", "AgentHost 的连接状态", _engineSummary),
                Row("行为检查", "架构守卫的条数（每条都能反证变红）", _checkCount),
            },
        },
    };

    // ── 小工具 ───────────────────────────────────────────────

    private static SettingsRow Row(string header, string description, Control value) => new()
    {
        Header = header,
        Description = description,
        Content = value,
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.75,
    };
}
