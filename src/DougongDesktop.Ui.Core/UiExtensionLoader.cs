using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;

namespace DougongDesktop.Ui.Core;

/// <summary>
/// UI 插件加载器：从目录加载外部程序集，注册进 slot。
/// </summary>
/// <remarks>
/// <b>三件事必须由加载器做，不能让插件自报</b>：
/// <list type="number">
/// <item><b>来源</b>——一律标 <see cref="SlotOccupantOrigin.Installed"/>。
/// 插件说"我是内置的"不能信。</item>
/// <item><b>保留 key 拦截</b>——<c>conversation</c> 不许外部占。</item>
/// <item><b>独立 ALC</b>——插件间不共享类型。但同时要能回收：
/// 卸载后不能有引用残留在注册表里。</item>
/// </list>
/// <para>
/// <b>不用 <c>Dougong.Kernel</c> 的加载器</b>：那套契约绑着
/// "内核不感知 UI"的整条约束，而 UI 插件天生要感知 UI。两套各自演进。
/// </para>
/// </remarks>
public sealed class UiExtensionLoader : IDisposable
{
    private readonly SlotRegistry _slots;
    private readonly Action<string> _log;
    private readonly List<LoadedExtension> _loaded = [];

    public UiExtensionLoader(SlotRegistry slots, Action<string>? log = null)
    {
        _slots = slots;
        _log = log ?? (_ => { });
    }

    /// <summary>已加载的插件概要，供插件管理页展示。</summary>
    public IReadOnlyList<LoadedExtension> Loaded => _loaded;

    /// <summary>
    /// 从一个目录加载全部 UI 插件。
    /// </summary>
    /// <param name="directory">插件目录，扫 <c>*.dll</c>（不递归）。</param>
    /// <returns>每个 dll 一个结果，不因单个失败中断整批。</returns>
    public IReadOnlyList<ExtensionLoadResult> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"UI 插件目录不存在：{directory}");

        // 排序保证结果稳定：文件系统枚举顺序跨平台不一致
        var assemblies = Directory
            .EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var results = new List<ExtensionLoadResult>(assemblies.Length);

        foreach (var path in assemblies)
            results.Add(LoadOne(path));

        return results;
    }

    /// <summary>
    /// 加载单个程序集。
    /// </summary>
    /// <remarks>
    /// <b>先探类型再注册</b>：辅助 dll（不含 <see cref="IDesktopUiExtension"/> 实现）
    /// 要跳过而不是报错——一个插件包里通常有依赖 dll，那是正常情况。
    /// </remarks>
    private ExtensionLoadResult LoadOne(string assemblyPath)
    {
        var fileName = Path.GetFileName(assemblyPath);

        try
        {
            var context = new ExtensionLoadContext(assemblyPath);

            Assembly assembly;
            try
            {
                assembly = context.LoadFromAssemblyPath(assemblyPath);
            }
            catch (BadImageFormatException)
            {
                // 不是 .NET 程序集（可能是原生 dll）——跳过，不算失败
                context.Unload();
                return ExtensionLoadResult.Skipped(fileName, "不是 .NET 程序集");
            }

            var types = FindExtensionTypes(assembly);
            if (types.Count == 0)
            {
                // 不含插件实现的 dll 是依赖库，正常跳过
                context.Unload();
                return ExtensionLoadResult.Skipped(fileName, "没有 IDesktopUiExtension 实现");
            }

            if (types.Count > 1)
            {
                // 一个 dll 多个实现：不擅自挑一个，让作者显式指定
                context.Unload();
                return ExtensionLoadResult.Ambiguous(fileName, [.. types.Select(t => t.FullName!)]);
            }

            return Instantiate(types[0], context, fileName);
        }
        catch (Exception ex)
        {
            return ExtensionLoadResult.Failed(fileName, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 注册一个已构造好的插件。
    /// </summary>
    /// <remarks>
    /// <b>单独暴露这个方法是为了可测</b>：加载路径要造真程序集，
    /// 而"来源标注""保留 key 拦截""卸载残留"这些规则与程序集无关——
    /// 用假插件直接走这一段，比反射调私有方法稳。
    /// </remarks>
    public ExtensionLoadResult Register(IDesktopUiExtension extension, string sourceFile = "(in-memory)")
    {
        var context = new ExtensionLoadContext(typeof(IDesktopUiExtension).Assembly.Location);
        try
        {
            return RegisterCore(extension, context, sourceFile, ownsContext: true);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private ExtensionLoadResult Instantiate(Type type, ExtensionLoadContext context, string fileName)
    {
        IDesktopUiExtension extension;
        try
        {
            extension = (IDesktopUiExtension)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            context.Unload();
            return ExtensionLoadResult.Failed(fileName, $"构造失败：{ex.Message}");
        }

        return RegisterCore(extension, context, fileName, ownsContext: false);
    }

    private ExtensionLoadResult RegisterCore(
        IDesktopUiExtension extension, ExtensionLoadContext context, string fileName, bool ownsContext)
    {
        // 来源由加载器声明，不看插件怎么说
        const SlotOccupantOrigin origin = SlotOccupantOrigin.Installed;

        // 保留 key 拦截：conversation 只对内内置开放
        if (extension.SlotKey == ReservedSlots.Main
            && extension.SlotKeyValue == ReservedSlots.Conversation)
        {
            if (ownsContext) context.Unload();
            return ExtensionLoadResult.Rejected(
                fileName, $"{extension.Id} 试图占用保留 key conversation");
        }

        IDisposable handle;
        try
        {
            handle = _slots.Register(extension.SlotKey, new SlotOccupant(
                extension.Id,
                Priority: 500,   // 第三方默认层，比内置(100) 低
                new FuncTemplate(extension.Build),
                extension.SlotKeyValue)
            {
                Origin = origin,
            });
        }
        catch (Exception ex)
        {
            if (ownsContext) context.Unload();
            return ExtensionLoadResult.Failed(fileName, $"注册失败：{ex.Message}");
        }

        var loaded = new LoadedExtension(extension, context, handle, fileName, origin);
        _loaded.Add(loaded);
        _log($"[ui-plugin] 已加载 {extension.Id}（{extension.DisplayName}）→ {extension.SlotKey}");

        return ExtensionLoadResult.Loaded(fileName, extension.Id, extension.DisplayName);
    }

    /// <summary>
    /// 找可实例化的插件类型。
    /// </summary>
    /// <remarks>
    /// 遇到 <see cref="ReflectionTypeLoadException"/> 时取能加载的那部分——
    /// 一个类型加载失败不该让整批不可用（依赖缺失是常见情况）。
    /// </remarks>
    private static List<Type> FindExtensionTypes(Assembly assembly)
    {
        Type[] all;
        try
        {
            all = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            all = [.. ex.Types.Where(t => t is not null)!];
        }

        return [.. all.Where(t =>
            typeof(IDesktopUiExtension).IsAssignableFrom(t)
            && t is { IsAbstract: false, IsInterface: false }
            && t.GetConstructor(Type.EmptyTypes) is not null)];
    }

    /// <summary>卸载一个插件：注销 slot + 卸载 ALC。</summary>
    public bool Unload(string extensionId)
    {
        var loaded = _loaded.FirstOrDefault(l => l.Extension.Id == extensionId);
        if (loaded is null) return false;

        // SlotOccupant.Template 持有插件的委托，不释放就阻止 ALC 回收
        loaded.Handle.Dispose();
        loaded.Context.Unload();
        _loaded.Remove(loaded);

        _log($"[ui-plugin] 已卸载 {extensionId}");
        return true;
    }

    public void Dispose()
    {
        foreach (var loaded in _loaded.ToArray())
        {
            loaded.Handle.Dispose();
            loaded.Context.Unload();
        }

        _loaded.Clear();
    }

    /// <summary>
    /// 每个插件一个可回收 ALC。
    /// </summary>
    /// <remarks>
    /// <b>共享契约必须解析到宿主的实例</b>：<see cref="IDesktopUiExtension"/> 与
    /// <c>Avalonia.Controls.Control</c> 若各加载一份，插件返回的控件类型
    /// 与宿主期望的不是同一个 Type，绑定会全失败且报错难以理解。
    /// </remarks>
    private sealed class ExtensionLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 宿主已加载的一律共用，否则跨 ALC 类型同一性丢失
            var shared = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName.Name);

            return shared;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }

    /// <summary>把一个 <c>Func&lt;Control&gt;</c> 包成 <c>IDataTemplate</c>。</summary>
    private sealed class FuncTemplate(Func<Control> build) : Avalonia.Controls.Templates.IDataTemplate
    {
        /// <summary>
        /// 恒真匹配。
        /// </summary>
        /// <remarks>
        /// slot 的 <c>ContentControl.Content</c> 放的是 slot key（string），
        /// 而插件模板不关心数据是什么——恒真匹配让它在任何 Content 下都能构建。
        /// 若这里按类型过滤，会出现"模板在、但控件没构建"的空内容（P3a 踩过）。
        /// </remarks>
        public bool Match(object? data) => true;

        public Control? Build(object? param) => build();
    }
}

/// <summary>已加载的插件。</summary>
public sealed record LoadedExtension(
    IDesktopUiExtension Extension,
    AssemblyLoadContext Context,
    IDisposable Handle,
    string AssemblyFile,
    SlotOccupantOrigin Origin);

/// <summary>单个 dll 的加载结果。</summary>
public sealed record ExtensionLoadResult(
    string AssemblyFile,
    ExtensionLoadStatus Status,
    string? ExtensionId = null,
    string? DisplayName = null,
    string? Reason = null,
    IReadOnlyList<string>? AmbiguousTypeNames = null)
{
    /// <summary>
    /// 是否值得让用户看到。
    /// </summary>
    /// <remarks>
    /// <see cref="ExtensionLoadStatus.Skipped"/> 返回 false：依赖库被扫到是正常的，
    /// 显示出来只会让用户以为出了问题。
    /// </remarks>
    public bool IsUserVisible => Status != ExtensionLoadStatus.Skipped;

    public static ExtensionLoadResult Loaded(string file, string id, string name) =>
        new(file, ExtensionLoadStatus.Loaded, id, name);

    public static ExtensionLoadResult Skipped(string file, string reason) =>
        new(file, ExtensionLoadStatus.Skipped, Reason: reason);

    public static ExtensionLoadResult Failed(string file, string reason) =>
        new(file, ExtensionLoadStatus.Failed, Reason: reason);

    public static ExtensionLoadResult Rejected(string file, string reason) =>
        new(file, ExtensionLoadStatus.Rejected, Reason: reason);

    public static ExtensionLoadResult Ambiguous(string file, IReadOnlyList<string> types) =>
        new(file, ExtensionLoadStatus.Ambiguous,
            Reason: "程序集内有多个 IDesktopUiExtension 实现，需显式指定",
            AmbiguousTypeNames: types);
}

public enum ExtensionLoadStatus
{
    Loaded,
    /// <summary>不是插件（依赖库或非 .NET 程序集）。</summary>
    Skipped,
    Failed,
    /// <summary>违反约束被拒（如占用保留 key）。</summary>
    Rejected,
    /// <summary>一个 dll 多个实现，需显式指定。</summary>
    Ambiguous,
}
