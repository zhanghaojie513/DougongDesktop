namespace DougongDesktop.Ui.Core;

/// <summary>右栏文档预览的渲染形态（P8e E7）。</summary>
public enum DocumentPreviewKind
{
    Markdown,
    Code,
    Image,
    PlainText,
    UnsupportedBinary,
    Error,
}

/// <summary>
/// 按文件路径与载荷属性判别预览形态与语法高亮语言（纯逻辑，零 UI 依赖）。
/// </summary>
public static class DocumentPreviewClassifier
{
    public static DocumentPreviewKind Classify(
        string? path,
        bool isBinary,
        bool hasImageContent,
        bool hasError)
    {
        if (hasError) return DocumentPreviewKind.Error;
        if (hasImageContent) return DocumentPreviewKind.Image;
        if (isBinary) return DocumentPreviewKind.UnsupportedBinary;

        var ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        if (ext is ".md" or ".markdown" or ".mdx")
            return DocumentPreviewKind.Markdown;

        if (LanguageForPath(path) is not null)
            return DocumentPreviewKind.Code;

        return DocumentPreviewKind.PlainText;
    }

    /// <summary>按扩展名/文件名映射到语法高亮语言标识符；纯文本返回 null。</summary>
    public static string? LanguageForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var fileName = Path.GetFileName(path);
        if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
        if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase)) return "makefile";

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".fs" or ".fsx" => "fsharp",
            ".ts" or ".mts" or ".cts" => "typescript",
            ".tsx" => "tsx",
            ".js" or ".mjs" or ".cjs" => "javascript",
            ".jsx" => "jsx",
            ".json" or ".jsonc" => "json",
            ".xml" or ".axaml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".slnx" or ".svg" => "xml",
            ".yml" or ".yaml" => "yaml",
            ".toml" => "toml",
            ".py" => "python",
            ".rs" => "rust",
            ".go" => "go",
            ".java" => "java",
            ".kt" or ".kts" => "kotlin",
            ".swift" => "swift",
            ".c" or ".h" => "c",
            ".cpp" or ".cc" or ".cxx" or ".hpp" => "cpp",
            ".sh" or ".bash" or ".zsh" => "bash",
            ".ps1" => "powershell",
            ".css" or ".scss" or ".less" => "css",
            ".html" or ".htm" => "html",
            ".sql" => "sql",
            ".md" or ".markdown" or ".mdx" => "markdown",
            ".ini" or ".conf" or ".editorconfig" => "ini",
            ".diff" or ".patch" => "diff",
            _ => null,
        };
    }

    /// <summary>将字节数格式化为紧凑可读大小。</summary>
    public static string FormatFileSize(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }
}
