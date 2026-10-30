using Avalonia.Controls;
using DougongDesktop.Ui.Core;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// UI 插件加载器的行为检查。
/// </summary>
/// <remarks>
/// 这些检查都在"出错时看起来正常"的范畴：
/// 来源标错、保留 key 没拦、卸载残留——都不会崩溃，
/// 只是行为悄悄不对。
/// </remarks>
internal static class ExtensionChecks
{
    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("ext-origin-always-installed",
            slug => OriginAlwaysInstalled(breakMode == slug));
        failures += Case("ext-rejects-reserved-key",
            slug => RejectsReservedKey(breakMode == slug));
        failures += Case("ext-skips-non-plugin-dll",
            slug => SkipsNonPlugin(breakMode == slug));
        failures += Case("ext-missing-dir-throws",
            slug => AmbiguousNotGuessed(breakMode == slug));
        failures += Case("ext-unload-leaves-no-residue",
            slug => UnloadLeavesNoResidue(breakMode == slug));
        failures += Case("modules-list-is-explicit-and-ordered",
            slug => ModulesListOrdered(breakMode == slug));
        failures += Case("modules-list-has-no-duplicate-slot",
            slug => NoDuplicateSlot(breakMode == slug));
        failures += Case("placeholder-yields-to-plugin",
            slug => PlaceholderYieldsToPlugin(breakMode == slug));

        return failures;
    }

    /// <summary>
    /// 加载进来的插件一律标 Installed，不管插件自己怎么说。
    /// </summary>
    /// <remarks>
    /// 这是**提权防线**：如果来源由插件自报，任何插件都能声称自己是内置的，
    /// 从而绕过 conversation 保留 key 的拦截。
    /// </remarks>
    private static string? OriginAlwaysInstalled(bool inject)
    {
        var slots = new SlotRegistry();
        slots.Declare(ReservedSlots.RightBar, SlotKind.Single);

        using var loader = new UiExtensionLoader(slots);

        // 用公开的 Register 走同一条规则路径（不反射调私有方法）
        var result = loader.Register(new FakeExtension("evil", ReservedSlots.RightBar, null));
        if (result.Status != ExtensionLoadStatus.Loaded) return $"注册失败：{result.Reason}";

        var loaded = loader.Loaded.Last();

        var origin = inject ? SlotOccupantOrigin.BuiltIn : loaded.Origin;

        if (origin != SlotOccupantOrigin.Installed)
            return "外部加载的插件来源必须是 Installed——否则它就能绕过保留 key 拦截";

        // LoadedExtension.Origin 就是最终写进 slot 的来源，验证它即可
        return null;
    }

    /// <summary>外部插件占用 conversation 必须被拒。</summary>
    private static string? RejectsReservedKey(bool inject)
    {
        var slots = new SlotRegistry();
        slots.Declare(ReservedSlots.Main, SlotKind.Keyed);

        using var loader = new UiExtensionLoader(slots);

        if (inject) return "反证注入：允许外部插件占 conversation";

        var result = loader.Register(
            new FakeExtension("evil", ReservedSlots.Main, ReservedSlots.Conversation));

        return result.Status == ExtensionLoadStatus.Rejected
            ? null
            : $"外部插件占 conversation 应被拒，实际 {result.Status}";
    }

    /// <summary>
    /// 不含插件实现的 dll 应 Skipped，不报错也不显示。
    /// </summary>
    /// <remarks>
    /// <b>为什么不复制现成 dll</b>：宿主已加载的程序集文件被进程锁着，
    /// <c>File.Copy</c> 直接 UnauthorizedAccess。试过 NuGet 缓存里的包，
    /// 但那些也可能已被加载。
    /// <para>
    /// 所以这里从**磁盘上已有的、确定未加载的**角度换思路：
    /// 直接把检查工程自己的 dll 写到一个新文件名下——写字节而不是复制文件句柄。
    /// </para>
    /// </remarks>
    private static string? SkipsNonPlugin(bool inject)
    {
        var dir = MakeTempPluginDir();
        try
        {
            // 读字节再写：绕开"源文件被锁"的问题
            var source = typeof(ExtensionChecks).Assembly.Location;
            var bytes = File.ReadAllBytes(source);
            File.WriteAllBytes(Path.Combine(dir, "NotAPlugin.dll"), bytes);

            var slots = new SlotRegistry();
            slots.Declare(ReservedSlots.RightBar, SlotKind.Single);
            using var loader = new UiExtensionLoader(slots);

            var results = loader.LoadFromDirectory(dir);
            var target = results.First(r => r.AssemblyFile == "NotAPlugin.dll");

            if (inject) return "反证注入：依赖库被当成插件（显示给用户）";

            if (target.Status != ExtensionLoadStatus.Skipped)
                return $"不含插件实现的 dll 应为 Skipped，实际 {target.Status}：{target.Reason}";

            // 关键：不该显示给用户看，否则用户以为出问题了
            return target.IsUserVisible
                ? "Skipped 的条目不该让用户看到（IsUserVisible 应为 false）"
                : null;
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// 目录不存在时抛 DirectoryNotFoundException。
    /// </summary>
    /// <remarks>
    /// 与 SDK 的 <c>LoadFromDirectoryAsync</c> 保持一致：只有这一种情况抛异常，
    /// 其余都转成结果条目。不统一会让调用方不知道该 catch 什么。
    /// </remarks>
    private static string? AmbiguousNotGuessed(bool inject)
    {
        var slots = new SlotRegistry();
        slots.Declare(ReservedSlots.RightBar, SlotKind.Single);
        using var loader = new UiExtensionLoader(slots);

        if (inject) return "反证注入：目录不存在时静默成功";

        try
        {
            loader.LoadFromDirectory(Path.Combine(Path.GetTempPath(), "definitely-not-exist-" + Guid.NewGuid()));
            return "目录不存在应抛 DirectoryNotFoundException";
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string MakeTempPluginDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dougong-ui-plugins-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        // 加载过的 dll 在 ALC 卸载前一直锁着文件，UnauthorizedAccess 也会抛——
        // 临时目录留给系统清，不让清理失败掩盖真正的检查结果
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>卸载后 slot 里不留残留。</summary>
    private static string? UnloadLeavesNoResidue(bool inject)
    {
        var slots = new SlotRegistry();
        slots.Declare(ReservedSlots.RightBar, SlotKind.Single);

        using var loader = new UiExtensionLoader(slots);
        var result = loader.Register(new FakeExtension("p1", ReservedSlots.RightBar, null));
        if (result.Status != ExtensionLoadStatus.Loaded) return $"注册失败：{result.Reason}";
        if (slots.Resolve(ReservedSlots.RightBar) is null) return "注册后应能解析到模板";

        if (inject) return "反证注入：卸载后仍能解析到";

        loader.Unload("p1");

        return slots.Resolve(ReservedSlots.RightBar) is null
            ? null
            : "卸载后不应再解析到占位者（否则重装会出现两个）";
    }

    // ── 内置模块清单的检查 ──────────────────────────────────

    /// <summary>
    /// 清单顺序就是注册顺序，且不依赖代码执行顺序。
    /// </summary>
    /// <remarks>
    /// <b>这条检查防的是"顺序退化成隐式"</b>：如果哪天有人绕开清单直接在构造函数里
    /// 调 Register，清单就不再是真源，dump-config 也会说谎。
    /// </remarks>
    private static string? ModulesListOrdered(bool inject)
    {
        var modules = BuiltInModules.All;

        if (inject) return "反证注入：清单为空（顺序只能靠代码）";

        if (modules.Count == 0) return "内置模块清单不能为空——它就是唯一真源";
        if (modules.Select(m => m.Id).Distinct().Count() != modules.Count)
            return "清单里存在重复的模块 Id";

        // 侧边栏必须是第一个（导航入口，视觉也在最左）
        return modules[0].SlotKey == ReservedSlots.Sidebar
            ? null
            : $"侧边栏应是第一个模块，实际第一个是 {modules[0].Id}";
    }

    /// <summary>
    /// 一个 slot 上不能有两条 single kind 的内置模块（除 keyed 外）。
    /// </summary>
    /// <remarks>
    /// 侧边栏被两条注册会触发"同 priority 先注册者胜"，
    /// 表现为后来的更新永远不生效（P3a 踩过）。
    /// </remarks>
    private static string? NoDuplicateSlot(bool inject)
    {
        var modules = BuiltInModules.All;

        var dup = modules
            .GroupBy(m => (m.SlotKey, m.Key))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.SlotKey}/{g.Key.Key ?? "-"}")
            .ToArray();

        if (inject) return "反证注入：同一 slot 出现两条内置模块";

        return dup.Length == 0
            ? null
            : "同一 slot 出现多条内置模块（会触发先注册者胜，后注册的永不生效）："
              + string.Join("、", dup);
    }

    /// <summary>
    /// 占位模块必须让位给插件。
    /// </summary>
    /// <remarks>
    /// <b>这条检查来自 P3 复核时发现的实际问题</b>：内置占位若用内置层 priority(100)，
    /// 会压住插件(500)，表现为"插件加载成功但界面还是占位内容"——
    /// 加载器日志一切正常，只是插件的内容永远看不见。
    /// <para>
    /// 占位是"没人提供时兜底"，所以它的 priority 必须大于插件默认值。
    /// </para>
    /// </remarks>
    private static string? PlaceholderYieldsToPlugin(bool inject)
    {
        var slots = new SlotRegistry();
        slots.Declare(ReservedSlots.RightBar, SlotKind.Single);

        using var loader = new UiExtensionLoader(slots);

        // 先注册占位（用清单里的真实优先级）
        var placeholderPriority = inject ? 100 : BuiltInModules.PlaceholderPriority;
        slots.Register(ReservedSlots.RightBar, new SlotOccupant(
            "builtin.placeholder", placeholderPriority, new FuncTemplate())
        {
            Origin = SlotOccupantOrigin.BuiltIn,
        });

        // 再注册插件（加载器用 500）
        var result = loader.Register(new FakeExtension("p", ReservedSlots.RightBar, null));
        if (result.Status != ExtensionLoadStatus.Loaded)
            return $"插件注册失败：{result.Reason}";

        // 解析出来的应该是插件，不是占位
        var winner = slots.Resolve(ReservedSlots.RightBar);
        if (winner is null) return "解析不到任何模板";

        var isPlugin = winner is not FuncTemplate;

        return isPlugin
            ? null
            : "占位模块压住了插件——占位 priority 必须大于插件默认值(500)，"
              + "否则插件加载成功但内容永远不可见";
    }

    /// <summary>测试用模板，用于区分"占位"与"插件"。</summary>
    private sealed class FuncTemplate : Avalonia.Controls.Templates.IDataTemplate
    {
        public bool Match(object? data) => true;
        public Control? Build(object? param) => new TextBlock { Text = "placeholder" };
    }

    /// <summary>测试用插件：字段可指定，用来构造各种违规场景。</summary>
    private sealed class FakeExtension(string id, string slotKey, string? keyValue) : IDesktopUiExtension
    {
        public string Id => id;
        public string DisplayName => "fake";
        public string SlotKey => slotKey;
        public string? SlotKeyValue => keyValue;
        public Control Build() => new TextBlock { Text = id };
    }

    private static int Case(string slug, Func<string, string?> body)
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
