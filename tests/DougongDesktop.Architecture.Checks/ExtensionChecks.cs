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
