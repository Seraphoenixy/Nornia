using Nornia.Desktop.Code;

namespace Nornia.Tests;

/// <summary>File-type icon semantics: every extension resolves to a VS Code-style language
/// monogram (C#/JS/PY/{}…) plus the existing per-language hue, so files are recognizable at a
/// glance instead of collapsing into four generic glyphs.</summary>
public sealed class FileTypeIconTests
{
    [Theory]
    [InlineData("cs", "C#")]
    [InlineData("js", "JS")]
    [InlineData("ts", "TS")]
    [InlineData("py", "PY")]
    [InlineData("java", "JAVA")]
    [InlineData("cpp", "C++")]
    [InlineData("json", "{}")]
    [InlineData("xml", "XML")]
    [InlineData("yaml", "YML")]
    [InlineData("html", "HTML")]
    [InlineData("css", "CSS")]
    [InlineData("sql", "SQL")]
    [InlineData("md", "MD")]
    [InlineData("txt", "TXT")]
    [InlineData("ps1", "PS")]
    [InlineData("sln", "SLN")]
    [InlineData("csproj", "PRJ")]
    [InlineData("ini", "INI")]
    [InlineData("png", "IMG")]
    [InlineData("doc", "DOC")]
    [InlineData("docx", "DOC")]
    [InlineData("xls", "XLS")]
    [InlineData("ppt", "PPT")]
    [InlineData("pdf", "PDF")]
    [InlineData("pyc", "PYC")]
    [InlineData("csv", "CSV")]
    [InlineData("zip", "ZIP")]
    [InlineData("tar", "TAR")]
    [InlineData("dll", "BIN")]
    [InlineData("mp3", "AUD")]
    [InlineData("mp4", "VID")]
    [InlineData("db", "DB")]
    [InlineData("ttf", "FNT")]
    [InlineData("env", "ENV")]
    [InlineData("gradle", "KT")]
    [InlineData("patch", "DIFF")]
    public void FromExtension_AssignsLanguageMonogram(string extension, string expectedMonogram)
    {
        Assert.Equal(expectedMonogram, CodeFileTypeRegistry.Instance.FromExtension(extension).IconMonogram);
    }

    [Fact]
    public void Registry_UsesManyDistinctMonograms()
    {
        var monograms = new[]
            {
                "cs", "js", "ts", "py", "java", "go", "rs", "rb", "php", "json", "yaml", "xml",
                "html", "css", "sql", "md", "log", "txt", "ps1", "bat", "ini", "toml", "sln",
                "csproj", "png", "kt", "fs", "vb",
            }
            .Select(extension => CodeFileTypeRegistry.Instance.FromExtension(extension).IconMonogram)
            .ToHashSet();

        Assert.True(monograms.Count >= 8, $"expected >=8 distinct monograms, got {monograms.Count}");
    }

    [Fact]
    public void Registry_EveryEntry_HasNonEmptyMonogramAndGlyphFallback()
    {
        var extensions = new[]
        {
            "cs", "csx", "vb", "fs", "csproj", "fsproj", "vbproj", "props", "targets", "sln",
            "xml", "xaml", "config", "resx", "svg", "json", "jsonc", "yaml", "yml", "html",
            "htm", "css", "scss", "less", "js", "mjs", "cjs", "ts", "tsx", "jsx", "ps1",
            "psm1", "psd1", "sh", "bat", "cmd", "ini", "toml", "editorconfig", "md",
            "markdown", "txt", "log", "py", "pyc", "java", "kt", "kts", "go", "rs", "c", "h",
            "cpp", "cc", "cxx", "hpp", "hxx", "sql", "rb", "php", "gradle",
            "png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "tiff",
            "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf", "csv", "properties", "env",
            "dll", "exe", "so", "dylib", "bin", "pdb", "o",
            "zip", "jar", "war", "whl", "rar", "7z", "tar", "gz", "bz2", "xz",
            "mp3", "wav", "flac", "ogg", "aac", "m4a",
            "mp4", "avi", "mov", "mkv", "webm", "m4v",
            "db", "sqlite", "sqlite3", "ttf", "otf", "woff", "woff2", "eot",
            "patch", "diff", "lock",
        };

        foreach (var extension in extensions)
        {
            var type = CodeFileTypeRegistry.Instance.FromExtension(extension);
            Assert.False(string.IsNullOrWhiteSpace(type.IconMonogram), $"{extension} 缺少 Monogram");
            Assert.False(string.IsNullOrEmpty(type.IconGlyph), $"{extension} 缺少字形回退");
        }
    }

    [Fact]
    public void UnknownExtension_FallsBackToQuestionMarkMonogram()
    {
        // 未识别扩展不再冒充 TXT:徽标为“?”,渲染仍是纯文本(安全显示)。
        var type = CodeFileTypeRegistry.Instance.FromPath("archive.zzz");
        Assert.Equal("?", type.IconMonogram);
        Assert.Equal("plaintext", type.LanguageId);
        Assert.Same(CodeFileTypeRegistry.Unknown, type);
    }

    [Fact]
    public void NoExtension_FallsBackToQuestionMarkMonogram()
    {
        // 无扩展名(如 LICENSE / Makefile / .gitignore)同样属于未识别类型。
        var type = CodeFileTypeRegistry.Instance.FromPath("LICENSE");
        Assert.Equal("?", type.IconMonogram);
        Assert.Equal("plaintext", type.LanguageId);
    }
}
