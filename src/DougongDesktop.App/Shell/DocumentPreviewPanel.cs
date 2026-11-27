using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DougongDesktop.Ipc;
using DougongDesktop.Ui.Core;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace DougongDesktop.App.Shell;

/// <summary>
/// 文档预览面板（P8e E7）：Markdown / 代码高亮 / 图片 / 纯文本预览，
/// 对 PDF/Office/二进制文件如实提示格式与大小，不灌乱码。
/// </summary>
public sealed class DocumentPreviewPanel : UserControl
{
    private readonly TextBlock _pathLabel;
    private readonly TextBlock _metaLabel;
    private readonly ArcButton _backButton;
    private readonly ArcButton _sourceToggleButton;
    private readonly CopyButton _copyButton;
    private readonly ContentControl _bodyHost;

    private FileReadEvent? _current;
    private DocumentPreviewKind _currentKind = DocumentPreviewKind.PlainText;
    private bool _showMarkdownSource;

    /// <summary>用户点击「返回列表」关闭预览。</summary>
    public event Action? CloseRequested;

    public DocumentPreviewPanel()
    {
        _backButton = new ArcButton
        {
            Variant = ButtonVariant.Ghost,
            Size = ControlSize.Small,
            Padding = new Thickness(6, 3),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new LucideIcon { Kind = LucideIconKind.ArrowLeft, Size = 13 },
                    new TextBlock { Text = "返回", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        _backButton.Click += (_, _) => CloseRequested?.Invoke();

        _pathLabel = new TextBlock
        {
            FontSize = 11,
            FontFamily = DshTheme.MonoFont,
            Foreground = DshTheme.Brush(DshTheme.LabelPrimary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _metaLabel = new TextBlock
        {
            FontSize = 10,
            Foreground = DshTheme.Brush(DshTheme.LabelSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        _sourceToggleButton = new ArcButton
        {
            Variant = ButtonVariant.Ghost,
            Size = ControlSize.Small,
            Content = "源码",
            Padding = new Thickness(6, 2),
            IsVisible = false,
        };
        _sourceToggleButton.Click += (_, _) =>
        {
            _showMarkdownSource = !_showMarkdownSource;
            _sourceToggleButton.Content = _showMarkdownSource ? "预览" : "源码";
            RenderCurrent();
        };

        _copyButton = new CopyButton
        {
            IsIconOnly = true,
            Variant = CopyButtonVariant.Plain,
            ResetAfter = TimeSpan.FromMilliseconds(ConversationMetrics.CopyFeedbackMs),
            IsVisible = false,
        };

        var header = new Border
        {
            Padding = new Thickness(8, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = DshTheme.Brush(DshTheme.BorderL1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 6,
                Children =
                {
                    _backButton,
                    PlaceCol(
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children = { _pathLabel, _metaLabel },
                        },
                        1),
                    PlaceCol(
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 4,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children = { _sourceToggleButton, _copyButton },
                        },
                        2),
                },
            },
        };

        _bodyHost = new ContentControl();

        var scroll = new ScrollArea
        {
            Content = _bodyHost,
            Orientation = ScrollAreaOrientation.Vertical,
            OverlayScrollbarMode = ScrollAreaScrollbarMode.Always,
            FadeLength = 0,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                header,
                PlaceRow(scroll, 1),
            },
        };
    }

    /// <summary>当前渲染的预览形态（无头自检用）。</summary>
    public DocumentPreviewKind CurrentKind => _currentKind;

    /// <summary>当前预览的文件相对路径。</summary>
    public string? CurrentPath => _current?.Path;

    /// <summary>显示加载中状态。</summary>
    public void ShowLoading(string path)
    {
        _current = null;
        _showMarkdownSource = false;
        _pathLabel.Text = path;
        _metaLabel.Text = string.Empty;
        _sourceToggleButton.IsVisible = false;
        _copyButton.IsVisible = false;
        _bodyHost.Content = Hint($"正在读取 {path}…");
    }

    /// <summary>渲染服务端返回的文档载荷。</summary>
    public void Apply(FileReadEvent file)
    {
        _current = file;
        _showMarkdownSource = false;
        _sourceToggleButton.Content = "源码";

        _currentKind = DocumentPreviewClassifier.Classify(
            file.Path,
            file.IsBinary,
            hasImageContent: file.Base64Content is { Length: > 0 },
            hasError: file.Error is { Length: > 0 });

        _pathLabel.Text = file.Path;
        ToolTip.SetTip(_pathLabel, file.Path);
        _metaLabel.Text = file.SizeBytes > 0
            ? DocumentPreviewClassifier.FormatFileSize(file.SizeBytes)
            : string.Empty;

        _sourceToggleButton.IsVisible = _currentKind == DocumentPreviewKind.Markdown;

        if (file.TextContent is { Length: > 0 } text)
        {
            _copyButton.Value = text;
            _copyButton.IsVisible = true;
        }
        else
        {
            _copyButton.IsVisible = false;
        }

        RenderCurrent();
    }

    private void RenderCurrent()
    {
        if (_current is not { } file) return;

        var content = _currentKind switch
        {
            DocumentPreviewKind.Error => Hint(file.Error ?? "读取文件失败。"),
            DocumentPreviewKind.Markdown => BuildMarkdownView(file),
            DocumentPreviewKind.Code => BuildCodeView(file),
            DocumentPreviewKind.Image => BuildImageView(file),
            DocumentPreviewKind.UnsupportedBinary => BuildBinaryPlaceholder(file),
            _ => BuildPlainTextView(file),
        };

        if (file.Truncated is { Length: > 0 } note)
        {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new Border
            {
                Margin = new Thickness(10, 8, 10, 0),
                Padding = new Thickness(10, 6),
                CornerRadius = new CornerRadius(6),
                Background = DshTheme.Brush(DshTheme.Layer2),
                Child = new TextBlock
                {
                    Text = note,
                    FontSize = 11,
                    Foreground = DshTheme.Brush(DshTheme.LabelSecondary),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
            stack.Children.Add(content);
            _bodyHost.Content = stack;
            return;
        }

        _bodyHost.Content = content;
    }

    private Control BuildMarkdownView(FileReadEvent file)
    {
        var text = file.TextContent ?? string.Empty;
        if (_showMarkdownSource)
            return BuildCodeBlock(text, "markdown", Path.GetFileName(file.Path));

        // 走 Arc Markdown 默认服务（含语法高亮与代码块工具栏，符合 markdown-codeblock-uses-arc-defaults）。
        return new Border
        {
            Padding = new Thickness(12, 10),
            Child = new Markdown
            {
                Text = text,
            },
        };
    }

    private static Control BuildCodeView(FileReadEvent file)
    {
        var lang = DocumentPreviewClassifier.LanguageForPath(file.Path) ?? "text";
        return BuildCodeBlock(file.TextContent ?? string.Empty, lang, Path.GetFileName(file.Path));
    }

    private static Control BuildPlainTextView(FileReadEvent file) =>
        BuildCodeBlock(file.TextContent ?? string.Empty, "text", Path.GetFileName(file.Path));

    private static Control BuildCodeBlock(string code, string language, string fileName) =>
        new Border
        {
            Padding = new Thickness(8),
            Child = new CodeBlock
            {
                Code = code,
                Language = language,
                FileName = fileName,
                ShowLineNumbers = true,
                IsCollapsible = false,
                CopyLabel = "复制",
                SourceCodeLabel = "源码",
            },
        };

    private static Control BuildImageView(FileReadEvent file)
    {
        try
        {
            var bytes = Convert.FromBase64String(file.Base64Content ?? string.Empty);
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);
            var dims = $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}";
            var size = DocumentPreviewClassifier.FormatFileSize(file.SizeBytes);

            return new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(12),
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Border
                    {
                        Padding = new Thickness(6),
                        CornerRadius = new CornerRadius(8),
                        Background = DshTheme.Brush(DshTheme.Layer2),
                        BorderThickness = new Thickness(1),
                        BorderBrush = DshTheme.Brush(DshTheme.BorderL1),
                        Child = new Image
                        {
                            Source = bitmap,
                            Stretch = Stretch.Uniform,
                            MaxHeight = 460,
                        },
                    },
                    new TextBlock
                    {
                        Text = $"{dims} · {size}",
                        FontSize = 11,
                        FontFamily = DshTheme.MonoFont,
                        Foreground = DshTheme.Brush(DshTheme.LabelSecondary),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            };
        }
        catch (Exception ex)
        {
            return Hint($"图片解码失败：{ex.Message}");
        }
    }

    private static Control BuildBinaryPlaceholder(FileReadEvent file)
    {
        var ext = Path.GetExtension(file.Path);
        var kindLabel = string.IsNullOrWhiteSpace(ext) ? "二进制文件" : $"{ext.TrimStart('.').ToUpperInvariant()} 文件";
        var size = DocumentPreviewClassifier.FormatFileSize(file.SizeBytes);

        return new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(16, 18),
            CornerRadius = new CornerRadius(8),
            Background = DshTheme.Brush(DshTheme.Layer2),
            BorderThickness = new Thickness(1),
            BorderBrush = DshTheme.Brush(DshTheme.BorderL1),
            Child = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new LucideIcon
                    {
                        Kind = LucideIconKind.FileText,
                        Size = 24,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Opacity = 0.65,
                    },
                    new TextBlock
                    {
                        Text = Path.GetFileName(file.Path),
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = $"{kindLabel} · {size}",
                        FontSize = 11,
                        FontFamily = DshTheme.MonoFont,
                        Foreground = DshTheme.Brush(DshTheme.LabelSecondary),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = "该格式暂不支持内嵌预览（不以乱码文本强行展示），请在外部应用中打开。",
                        FontSize = 11,
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 4, 0, 0),
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
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        },
    };

    private static Control PlaceRow(Control c, int row)
    {
        Grid.SetRow(c, row);
        return c;
    }

    private static Control PlaceCol(Control c, int col)
    {
        Grid.SetColumn(c, col);
        return c;
    }
}
