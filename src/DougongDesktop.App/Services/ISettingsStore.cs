using DougongDesktop.Ui.Core;

namespace DougongDesktop.App.Services;

/// <summary>
/// 桌面实现：存到 <c>~/.dougong-desktop/</c>。
/// </summary>
/// <remarks>
/// <b>先写临时文件再改名</b>：直接覆盖时进程被杀会留下半个 JSON，
/// 下次启动读它就变成"布局全丢"。这个细节在抽象后面，web 实现可以
/// 用 localStorage 的原子性代替。
/// </remarks>
public sealed class FileSettingsStore : ISettingsStore
{
    private readonly string _directory;

    public FileSettingsStore(string? directory = null) =>
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dougong-desktop");

    /// <summary>文件路径，供诊断显示。</summary>
    public string DirectoryPath => _directory;

    public string? Read(string name)
    {
        try
        {
            var path = Path.Combine(_directory, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public bool Write(string name, string content)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, name);
            var temp = path + ".tmp";
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[settings] 写入 {name} 失败：{ex.Message}");
            return false;
        }
    }
}
