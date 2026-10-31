namespace DougongDesktop.Ui.Core;

/// <summary>
/// 界面偏好的存储。
/// </summary>
/// <remarks>
/// <b>为什么需要这层抽象</b>：现在 <see cref="AppSettings"/> 直接调
/// <c>File.*</c>，那只在桌面成立。将来 web 端没有文件系统
/// （浏览器的 WASM 环境里 <c>System.IO</c> 不可用或受限），
/// 而"记不记得栏宽"这件事和存哪里无关。
/// <para>
/// <b>不抽象整个文件系统</b>：只暴露"读写一个命名的文本文件"。
/// 开放目录遍历、删除、权限会让接口变成文件系统本身，
/// 而 web 端一个都实现不了。
/// </para>
/// </remarks>
public interface ISettingsStore
{
    /// <summary>读取。不存在或读失败时返回 null，不抛。</summary>
    /// <remarks>
    /// <b>读失败不抛是刻意的</b>：设置坏了应该用默认值起来，
    /// 而不是开不了。这与"加载失败不能让 App 起不来"是同一条原则。
    /// </remarks>
    string? Read(string name);

    /// <summary>写入。失败时静默返回 false，不抛。</summary>
    bool Write(string name, string content);
}

