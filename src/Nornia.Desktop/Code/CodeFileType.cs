namespace Nornia.Desktop.Code;

/// <summary>Kind of structural outline the editor can derive for a file.</summary>
public enum CodeOutlineKind
{
    /// <summary>No structural outline (plain text or unsupported language).</summary>
    None,

    /// <summary>C# declarations via the Roslyn syntax tree.</summary>
    CSharp,

    /// <summary>XML / XAML element tree.</summary>
    Xml,

    /// <summary>JSON object property tree.</summary>
    Json,

    /// <summary>Brace-delimited languages without a dedicated compiler parser.</summary>
    Brace,

    /// <summary>Whitespace-delimited structures such as Python and YAML.</summary>
    Indentation,

    /// <summary>Markdown heading tree.</summary>
    Markdown,
}

/// <summary>
/// Language descriptor for a previewed file: drives syntax highlighting, tab/breadcrumb icon,
/// language status and outline parsing. <see cref="HighlightingName"/> must match an AvalonEdit
/// built-in highlighting definition; the empty string means plain text. <see cref="IconColorToken"/>
/// is the theme resource key for the file-type icon brush (Seti-style per-language-family hue).
/// <see cref="IconMonogram"/> 是 VS Code 风格的语言缩写（C#/JS/PY/{}…），以透明底 + 语言色相文字渲染；
/// <see cref="IconGlyph"/> 保留为无徽标场景（如图片）的回退文档字形。
/// </summary>
public sealed record CodeFileType(
    string LanguageId,
    string DisplayName,
    string HighlightingName,
    string IconGlyph,
    CodeOutlineKind OutlineKind,
    string IconColorToken = "FileTypeIconGrayBrush",
    string IconMonogram = "TXT",
    string GrammarScopeName = "source.nornia")
{
    public static CodeFileType PlainText(string displayName = "纯文本") =>
        new("plaintext", displayName, string.Empty, Codicons.File, CodeOutlineKind.None);
}
