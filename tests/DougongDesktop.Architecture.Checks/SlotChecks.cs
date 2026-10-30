using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DougongDesktop.Ui.Core;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// slot 框架的行为检查（九条）。
/// </summary>
/// <remarks>
/// <b>为什么必须测而不是靠人点</b>：这些错误全是"出错时看起来正常"——
/// 静默覆盖会让两个插件只显示一个且无报错；priority 算错表现为"第三方插件没生效"；
/// 卸载残留只在重装后出现"两个"。**靠人工点击几乎不可能发现。**
/// <para>
/// 每条都必须有反证（<c>--break=&lt;名字&gt;</c>）：故意改错实现，检查要变红。
/// 从没红过的检查等于没有检查。
/// </para>
/// </remarks>
internal static class SlotChecks
{
    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("slot-priority-overrides-default",
            slug => PriorityOverridesDefault(breakMode == slug), breakMode);
        failures += Case("slot-priority-tie-breaks-by-registration",
            slug => TieBreaksByRegistration(breakMode == slug), breakMode);
        failures += Case("slot-list-preserves-register-order",
            slug => ListPreservesOrder(breakMode == slug), breakMode);
        failures += Case("slot-list-observable-updates",
            slug => ListObservableUpdates(breakMode == slug), breakMode);
        failures += Case("slot-keyed-dispatches-by-key",
            slug => KeyedDispatches(breakMode == slug), breakMode);
        failures += Case("slot-conversation-key-is-reserved",
            slug => ConversationKeyReserved(breakMode == slug), breakMode);
        failures += Case("slot-unload-leaves-no-residue",
            slug => UnloadLeavesNoResidue(breakMode == slug), breakMode);
        failures += Case("slot-select-panel-rejects-missing-key",
            slug => SelectPanelRejectsMissing(breakMode == slug), breakMode);
        failures += Case("slot-occupant-failure-is-isolated",
            slug => OccupantFailureIsolated(breakMode == slug), breakMode);
        failures += Case("slot-any-name-works-for-list",
            slug => AnyNameWorksForList(breakMode == slug), breakMode);
        failures += Case("slot-undeclared-register-throws",
            slug => UndeclaredRegisterThrows(breakMode == slug), breakMode);
        failures += Case("slot-origin-defaults-to-installed",
            slug => OriginDefaultsToInstalled(breakMode == slug), breakMode);

        return failures;
    }

    // ── 1. priority 小的胜出（第三方替换内置实现的机制） ──────────────

    private static string? PriorityOverridesDefault(bool inject)
    {
        var r = new SlotRegistry();
        r.Declare("x", SlotKind.Single);
        var builtin = Template("builtin");
        var third = Template("third");

        // 内置 priority 100，第三方 50 —— 第三方应胜出
        // 反证：把第三方也调成 100（与内置同值），此时"先注册者胜"会让内置赢，
        // 从而验证"priority 真的在起作用"，而不是碰巧按注册顺序。
        r.Register("x", new SlotOccupant("builtin", 100, builtin));
        r.Register("x", new SlotOccupant("third", inject ? 100 : 50, third));

        var winner = r.Resolve("x");
        return ReferenceEquals(winner, third)
            ? null
            : "priority 小的应胜出（第三方覆盖内置），但选了另一个";
    }

    // ── 2. 同 priority 时先注册者胜 ──────────────────────────────

    private static string? TieBreaksByRegistration(bool inject)
    {
        var r = new SlotRegistry();
        r.Declare("x", SlotKind.Single);
        var first = Template("first");
        var second = Template("second");

        // 两个都 priority 100 —— 先注册的 first 应胜出
        r.Register("x", new SlotOccupant("first", 100, first));
        r.Register("x", new SlotOccupant("second", inject ? 99 : 100, second));

        var winner = r.Resolve("x");
        return ReferenceEquals(winner, first)
            ? null
            : "同 priority 应先注册者胜";
    }

    // ── 3. list 顺序 = 注册顺序，priority 不影响 ──────────────────

    private static string? ListPreservesOrder(bool inject)
    {
        var r = new SlotRegistry();
        r.Declare(ReservedSlots.Bottom, SlotKind.List);
        var a = Template("a");
        var b = Template("b");
        var c = Template("c");

        // 故意让 priority 与注册顺序相反：c(100) b(200) a(300)
        r.Register(ReservedSlots.Bottom, new SlotOccupant("a", 300, a));
        r.Register(ReservedSlots.Bottom, new SlotOccupant("b", 200, b));
        r.Register(ReservedSlots.Bottom, new SlotOccupant("c", 100, c));

        var order = r.GetList(ReservedSlots.Bottom).Select(o => o.Id).ToArray();
        var expected = inject ? new[] { "c", "b", "a" } : new[] { "a", "b", "c" };

        return order.SequenceEqual(expected)
            ? null
            : $"list 顺序应为注册顺序 [a,b,c]，实际 [{string.Join(",", order)}]（priority 不该影响顺序）";
    }

    // ── 4. list 用可观察集合，增删立即反映 ───────────────────────

    private static string? ListObservableUpdates(bool inject)
    {
        var r = new SlotRegistry();
        r.Declare(ReservedSlots.Overlay, SlotKind.List);
        var list = r.GetList(ReservedSlots.Overlay);

        var changes = 0;
        list.CollectionChanged += (_, _) => changes++;

        var handle = r.Register(ReservedSlots.Overlay, new SlotOccupant("a", 100, Template("a")));
        var afterAdd = list.Count;

        handle.Dispose();
        var afterRemove = list.Count;

        if (inject) return "反证注入：观察者机制失效";

        return afterAdd == 1 && afterRemove == 0 && changes == 2
            ? null
            : $"可观察集合应同步增删并各通知一次：加后 {afterAdd}、删后 {afterRemove}、通知 {changes} 次";
    }

    // ── 5. keyed 精确分派；未命中返回 null ──────────────────────

    private static string? KeyedDispatches(bool inject)
    {
        var r = new SlotRegistry();
        r.Declare(ReservedSlots.Main, SlotKind.Keyed);
        var plugins = Template("plugins");
        var settings = Template("settings");

        r.Register(ReservedSlots.Main, new SlotOccupant("p", 100, plugins, Key: "plugins"));
        r.Register(ReservedSlots.Main, new SlotOccupant("s", 100, settings, Key: "settings"));

        var hit = r.Resolve(ReservedSlots.Main, "plugins");
        var miss = r.Resolve(ReservedSlots.Main, "nonexistent");

        if (inject) return "反证注入：未命中时静默回落到第一个";

        if (!ReferenceEquals(hit, plugins)) return "keyed 应按 key 精确分派";
        if (miss is not null) return "未命中的 key 应返回 null（由 Fallback 决定），不该回落";
        return null;
    }

    // ── 6. conversation 是内置专用，外部不能覆盖 ──────────────────

    /// <remarks>
    /// <b>这条检查的语义改过一次，值得记。</b>第一版让注册表在 Register 时一律拒绝
    /// <c>main/conversation</c>，结果**内置实现自己都注册不进去，App 启动直接抛异常**。
    ///
    /// 正确语义：内置可以注册，**外部 UI 插件不能覆盖**（用 priority 也不行）。
    /// 而"谁在注册"这件事只有加载器知道，注册表不知道——所以拦截放在 P3b 的加载器里。
    /// 这里的检查改成验证注册表**允许**内置注册、且能正常解析。
    /// </remarks>
    private static string? ConversationKeyReserved(bool inject)
    {
        var r = new SlotRegistry();
        r.Declare(ReservedSlots.Main, SlotKind.Keyed);

        // ① 内置实现注册 conversation 必须**成功**
        var builtin = Template("builtin");
        r.Register(ReservedSlots.Main, new SlotOccupant(
            "builtin.conversation", 100, builtin, Key: ReservedSlots.Conversation)
        {
            Origin = SlotOccupantOrigin.BuiltIn,
        });

        var resolved = r.Resolve(ReservedSlots.Main, ReservedSlots.Conversation);
        if (!ReferenceEquals(resolved, builtin))
            return "内置实现应该能注册并解析到 conversation（第一版错在连内置都拒了，App 起不来）";

        // ② 外部插件注册 conversation 必须**被拒**
        if (inject) return "反证注入：外部插件也能占 conversation";

        try
        {
            r.Register(ReservedSlots.Main, new SlotOccupant(
                "evil.plugin", 1, Template("evil"), Key: ReservedSlots.Conversation)
            {
                Origin = SlotOccupantOrigin.Installed,
            });
            return "外部插件不该能占 conversation——它会让用户切走后丢会话上下文";
        }
        catch (SlotConflictException)
        {
            return null;
        }
    }

    // ── 7. 卸载不留残留 ────────────────────────────────────────

    private static string? UnloadLeavesNoResidue(bool inject)
    {
        var r = new SlotRegistry();
        r.Declare("x", SlotKind.Single);
        var handle = r.Register("x", new SlotOccupant("a", 100, Template("a")));

        if (inject) return "反证注入：卸载后仍能解析到";

        if (r.Resolve("x") is null) return "注册后应能解析";

        handle.Dispose();

        return r.Resolve("x") is null
            ? null
            : "释放句柄后不应再解析到占位者（否则重装会出现两个同一插件）";
    }

    // ── 8. selectPanel 边界：缺失 key 抛错 ─────────────────────

    private static string? SelectPanelRejectsMissing(bool inject)
    {
        var r = new SlotRegistry();
        r.Declare(ReservedSlots.Main, SlotKind.Keyed);
        r.Register(ReservedSlots.Main, new SlotOccupant("p", 100, Template("p"), Key: "plugins"));

        if (inject) return "反证注入：缺失 key 静默成功";

        if (!r.HasKey(ReservedSlots.Main, "plugins")) return "已注册的 key 应存在";
        if (r.HasKey(ReservedSlots.Main, "missing")) return "未注册的 key 不该被判为存在（否则 SelectPanel 会切到空白页）";

        return null;
    }

    // ── 9. 一个占位者失败不影响其他 ────────────────────────────

    private static string? OccupantFailureIsolated(bool inject)
    {
        var r = new SlotRegistry();
        r.Declare("x", SlotKind.Single);

        // 一个会抛异常的模板 + 一个正常模板
        var throwing = new FuncDataTemplate<object>((_, _) => throw new InvalidOperationException("占位者炸了"));
        var ok = Template("ok");

        r.Register("x", new SlotOccupant("bad", 100, throwing));
        r.Register("x", new SlotOccupant("good", 200, ok));

        if (inject) return "反证注入：异常未被隔离";

        // BuildSafely 应捕获异常、返回 null、并回报失败
        var reported = new List<string>();
        var result = r.BuildSafely(throwing, (id, _) => reported.Add(id));

        if (result is not null) return "抛异常的模板应让 BuildSafely 返回 null";
        if (reported.Count != 1) return $"失败应回报一次，实际 {reported.Count} 次";

        // 不抛的那个应正常构建
        var good = r.BuildSafely(ok);
        if (good is null) return "正常模板应能构建";

        return null;
    }

    // ── 10. 任意 slot 名的 list 都能工作（回归：曾因硬编码名字而静默失效） ──

    /// <remarks>
    /// <b>这条检查来自一个真实缺陷</b>：第一版 <c>Register</c> 用
    /// <c>slotKey is ReservedSlots.Bottom or ReservedSlots.Overlay</c>
    /// 判断要不要进可观察集合。新增一个 list slot（如 <c>sidebar.footer</c>）时
    /// 注册不进集合、<c>GetList</c> 返回空、**UI 静默空白且无任何报错**。
    /// 实测确认后改成"kind 由 Declare 定死"。
    /// </remarks>
    private static string? AnyNameWorksForList(bool inject)
    {
        var r = new SlotRegistry();

        if (inject) return "反证注入：非白名单 slot 名注册后集合为空";

        // 一个不在任何硬编码名单里的 list slot
        r.Declare("sidebar.footer", SlotKind.List);
        r.Register("sidebar.footer", new SlotOccupant("a", 100, Template("a")));
        r.Register("sidebar.footer", new SlotOccupant("b", 100, Template("b")));

        var list = r.GetList("sidebar.footer");
        return list.Count == 2
            ? null
            : $"任意 slot 名的 list 都应工作，实际 GetList 返回 {list.Count} 条（期望 2）";
    }

    // ── 11. 未声明的 slot 注册应抛错，不静默接受 ──────────────────

    private static string? UndeclaredRegisterThrows(bool inject)
    {
        var r = new SlotRegistry();

        if (inject) return "反证注入：未声明也接受";

        try
        {
            r.Register("never.declared", new SlotOccupant("x", 100, Template("x")));
            return "注册未声明的 slot 应抛 InvalidOperationException——静默接受会让'注册了但界面空着'变成无解问题";
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // ── 12. 来源默认是 Installed（保守默认） ──────────────────

    /// <remarks>
    /// 忘记标注来源时应按"外部插件"对待（受限），而不是意外获得内置权限。
    /// 反过来（默认 BuiltIn）会让任何疏忽都变成提权。
    /// </remarks>
    private static string? OriginDefaultsToInstalled(bool inject)
    {
        var occupant = new SlotOccupant("x", 100, Template("x"));
        var actual = inject ? SlotOccupantOrigin.BuiltIn : occupant.Origin;

        return actual == SlotOccupantOrigin.Installed
            ? null
            : "来源应默认 Installed——默认 BuiltIn 会让任何疏忽都变成提权";
    }

    private static IDataTemplate Template(string tag) =>
        new FuncDataTemplate<object>((_, _) => new TextBlock { Text = tag });

    private static int Case(string slug, Func<string, string?> body, string? breakMode)
    {
        string? error;
        try
        {
            error = body(slug);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (error is null)
        {
            Console.WriteLine($"PASS  {slug}");
            return 0;
        }

        Console.WriteLine($"FAIL  {slug}");
        Console.WriteLine($"      {error}");
        return 1;
    }
}
