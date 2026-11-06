using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DougongDesktop.Ipc;
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
    private readonly TextBlock _engineSummary;
    private readonly TextBlock _checkCount;

    /// <summary>构造设置面板。</summary>
    public SettingsView()
    {
        _providerSummary = Body("—");
        _modelSummary = Body("—");
        _engineSummary = Body("—");
        _checkCount = Body("—");

        _page = new SettingsPage
        {
            Title = "设置",
            CompactWidth = 640,
            Items =
            {
                BuildProvidersSection(),
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

        foreach (var m in models)
        {
            if (m.Id != currentModelId) continue;
            _modelSummary.Text += $" · 当前 {m.Id}";
            if (m.ContextWindow > 0)
                _modelSummary.Text += $"（窗口 {m.ContextWindow:N0}）";
            break;
        }
    }

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
        var keySource = Body(
            Environment.GetEnvironmentVariable("DOUGONG_PROVIDER_KEY") is { Length: > 0 }
                ? "已通过环境变量 DOUGONG_PROVIDER_KEY 提供"
                : "未配置（环境变量 DOUGONG_PROVIDER_KEY）");

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
                        },
                    },
                    new SettingsGroup
                    {
                        Header = "凭据",
                        Description = "本轮不支持在界面录入密钥——不落盘的话填了也会丢。",
                        Items =
                        {
                            Row("API Key", "只从环境变量读取", keySource),
                        },
                    },
                },
            },
        };
    }

    /// <summary>权限：审批模式的只读说明（C5 的范围）。</summary>
    /// <remarks>
    /// <b>这里不放可切换的开关</b>：权限预设要下发到引擎才算数，而现在
    /// <c>HostProtocol</c> 没有承载它的字段（与 C8 同一类缺口）。
    /// 放一个切了不生效的开关，比不放更糟。
    /// </remarks>
    private static SettingsSection BuildPermissionsSection() => new()
    {
        Header = "权限",
        Description = "审批在消息流里逐次询问；四档决策见审批卡片。",
        Content = new SettingsGroup
        {
            Header = "当前行为",
            Items =
            {
                Row("工具调用审批",
                    "每个需要授权的调用单独询问，不记住本次会话的选择",
                    Body("逐次询问")),
                Row("拒绝后是否停止回合",
                    "由审批卡片上的「拒绝并停止」决定，不设全局默认",
                    Body("每次可选")),
            },
        },
    };

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
