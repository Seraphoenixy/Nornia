using System.Collections.Frozen;
using System.IO;

namespace Nornia.Desktop.Code;

/// <summary>Resolves a previewed file to its <see cref="CodeFileType"/> (language / highlighting /
/// icon / outline kind). Unrecognized types (unknown or missing extension) resolve to the "?"
/// fallback type — still rendered as plain text, but the icon no longer claims a text type.</summary>
public interface ICodeFileTypeRegistry
{
    CodeFileType FromPath(string path);
    CodeFileType FromExtension(string extension);
}

/// <summary>
/// Extension-based language registry for the read-only code workbench. Highlighting names are the
/// AvalonEdit built-in definitions ("C#", "XML", "JavaScript", "HTML", "CSS", "C++", "Java", "SQL",
/// "PowerShell", "Python", "Ruby", "PHP", "VB"); languages without a built-in definition map to
/// plain text so they always render safely. Types we do not recognize (unknown or missing
/// extension) resolve to <see cref="Unknown"/>: plain-text rendering with a "?" monogram, so an
/// unrecognized file never masquerades as a text document.
/// </summary>
public sealed class CodeFileTypeRegistry : ICodeFileTypeRegistry
{
    public static readonly CodeFileTypeRegistry Instance = new();

    private static readonly FrozenDictionary<string, CodeFileType> ByExtension = BuildMap();

    /// <summary>未识别文件类型的兜底:“?”徽标(不冒充 TXT);渲染仍走纯文本,任意内容安全显示。
    /// 不进 <see cref="All"/> 语言列表(它不是一种可选择的语言)。</summary>
    public static readonly CodeFileType Unknown = WithGrammar(new CodeFileType(
        "plaintext", "未知类型", string.Empty, Codicons.File, CodeOutlineKind.None,
        "FileTypeIconGrayBrush", "?"));

    /// <summary>全部已注册语言类型(去重后),供语言选择器/`@` 相关 QuuickInput 使用。</summary>
    public IReadOnlyList<CodeFileType> All => AllTypes;

    private static readonly CodeFileType[] AllTypes = ByExtension.Values.Distinct().OrderBy(t => t.DisplayName).ToArray();

    public CodeFileType FromPath(string path) => FromExtension(Path.GetExtension(path));

    public CodeFileType FromExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            // 无扩展名 = 未注册类型:与未知扩展同样走“?”兜底,而不是声称“纯文本”。
            return Unknown;
        }

        var normalized = extension.TrimStart('.').ToLowerInvariant();
        return ByExtension.TryGetValue(normalized, out var type) ? type : Unknown;
    }

    private static CodeFileType WithGrammar(CodeFileType type) =>
        type with { GrammarScopeName = BuiltInGrammarCatalog.Instance.GetProfile(type).ScopeName };

    private static FrozenDictionary<string, CodeFileType> BuildMap()
    {
        const string Blue = "FileTypeIconBlueBrush";
        const string Green = "FileTypeIconGreenBrush";
        const string Yellow = "FileTypeIconYellowBrush";
        const string Orange = "FileTypeIconOrangeBrush";
        const string Purple = "FileTypeIconPurpleBrush";
        const string Red = "FileTypeIconRedBrush";
        const string Teal = "FileTypeIconTealBrush";
        const string Gray = "FileTypeIconGrayBrush";

        // 图标主体是“透明底 + 语言色相的缩写 Monogram”(VS Code 风格语言图标),
        // 字形仅作回退:图片等无徽标场景用图片字形。
        const string DocGlyph = Codicons.File;
        const string ImageGlyph = Codicons.FileMedia;

        var map = new Dictionary<string, CodeFileType>(StringComparer.Ordinal)
        {
            // C# / .NET (purple): 源码与脚本走语言缩写,项目/解决方案文件走 PRJ/SLN
            ["cs"] = Type("csharp", "C#", "C#", CodeOutlineKind.CSharp, Purple, "C#"),
            ["csx"] = Type("csharp", "C#", "C#", CodeOutlineKind.CSharp, Purple, "C#"),
            ["vb"] = Type("vb", "VB", "VB", CodeOutlineKind.None, Purple, "VB"),
            ["fs"] = Type("fsharp", "F#", string.Empty, CodeOutlineKind.None, Purple, "F#"),
            ["csproj"] = Type("xml", "XML", "XML", CodeOutlineKind.Xml, Orange, "PRJ"),
            ["fsproj"] = Type("xml", "XML", "XML", CodeOutlineKind.Xml, Orange, "PRJ"),
            ["vbproj"] = Type("xml", "XML", "XML", CodeOutlineKind.Xml, Orange, "PRJ"),
            ["props"] = Type("xml", "XML", "XML", CodeOutlineKind.Xml, Orange, "PRJ"),
            ["targets"] = Type("xml", "XML", "XML", CodeOutlineKind.Xml, Orange, "PRJ"),
            ["sln"] = Type("plaintext", "解决方案", string.Empty, CodeOutlineKind.None, Gray, "SLN"),

            // XML / XAML family (orange)
            ["xml"] = Type("xml", "XML", "XML", CodeOutlineKind.Xml, Orange, "XML"),
            ["xaml"] = Type("xml", "XAML", "XML", CodeOutlineKind.Xml, Orange, "XML"),
            ["config"] = Type("xml", "XML", "XML", CodeOutlineKind.Xml, Orange, "XML"),
            ["resx"] = Type("xml", "XML", "XML", CodeOutlineKind.Xml, Orange, "XML"),
            ["svg"] = Type("xml", "XML", "XML", CodeOutlineKind.Xml, Orange, "XML"),

            // Web (json/yaml = yellow; js/ts = blue; css = blue; html/scss/less = orange)
            ["json"] = Type("json", "JSON", string.Empty, CodeOutlineKind.Json, Yellow, "{}"),
            ["jsonc"] = Type("jsonc", "JSONC", string.Empty, CodeOutlineKind.Json, Yellow, "{}"),
            ["yaml"] = Type("yaml", "YAML", string.Empty, CodeOutlineKind.Indentation, Yellow, "YML"),
            ["yml"] = Type("yaml", "YAML", string.Empty, CodeOutlineKind.Indentation, Yellow, "YML"),
            ["html"] = Type("html", "HTML", "HTML", CodeOutlineKind.None, Orange, "HTML"),
            ["htm"] = Type("html", "HTML", "HTML", CodeOutlineKind.None, Orange, "HTML"),
            ["css"] = Type("css", "CSS", "CSS", CodeOutlineKind.None, Blue, "CSS"),
            ["scss"] = Type("scss", "SCSS", "CSS", CodeOutlineKind.None, Orange, "CSS"),
            ["less"] = Type("less", "LESS", "CSS", CodeOutlineKind.None, Orange, "CSS"),
            ["js"] = Type("javascript", "JavaScript", "JavaScript", CodeOutlineKind.Brace, Blue, "JS"),
            ["mjs"] = Type("javascript", "JavaScript", "JavaScript", CodeOutlineKind.Brace, Blue, "JS"),
            ["cjs"] = Type("javascript", "JavaScript", "JavaScript", CodeOutlineKind.Brace, Blue, "JS"),
            ["ts"] = Type("typescript", "TypeScript", "JavaScript", CodeOutlineKind.Brace, Blue, "TS"),
            ["tsx"] = Type("typescriptreact", "TSX", "JavaScript", CodeOutlineKind.Brace, Blue, "TS"),
            ["jsx"] = Type("javascriptreact", "JSX", "JavaScript", CodeOutlineKind.Brace, Blue, "JSX"),

            // Scripts / config (ps1/bat = blue; ini/toml/editorconfig = yellow; sh = gray)
            ["ps1"] = Type("powershell", "PowerShell", "PowerShell", CodeOutlineKind.None, Blue, "PS"),
            ["psm1"] = Type("powershell", "PowerShell", "PowerShell", CodeOutlineKind.None, Blue, "PS"),
            ["psd1"] = Type("powershell", "PowerShell", "PowerShell", CodeOutlineKind.None, Blue, "PS"),
            ["sh"] = Type("shellscript", "Shell", string.Empty, CodeOutlineKind.None, Gray, "SH"),
            ["bat"] = Type("bat", "批处理", string.Empty, CodeOutlineKind.None, Blue, "BAT"),
            ["cmd"] = Type("bat", "批处理", string.Empty, CodeOutlineKind.None, Blue, "BAT"),
            ["ini"] = Type("ini", "INI", string.Empty, CodeOutlineKind.None, Yellow, "INI"),
            ["toml"] = Type("toml", "TOML", string.Empty, CodeOutlineKind.None, Yellow, "TOML"),
            ["editorconfig"] = Type("ini", "EditorConfig", string.Empty, CodeOutlineKind.None, Yellow, "INI"),

            // Markdown / docs (gray). AvalonEdit ships the MarkDown definition; keep it enabled
            // in the shared source reader so headings, emphasis, links and code spans are colored
            // in source mode as well as in the TextMate presentation layer.
            ["md"] = Type("markdown", "Markdown", "MarkDown", CodeOutlineKind.Markdown, Gray, "MD"),
            ["markdown"] = Type("markdown", "Markdown", "MarkDown", CodeOutlineKind.Markdown, Gray, "MD"),
            ["txt"] = Type("plaintext", "文本", string.Empty, CodeOutlineKind.None, Gray, "TXT"),
            ["log"] = Type("log", "日志", string.Empty, CodeOutlineKind.None, Gray, "LOG"),

            // Systems languages (c/c++/go = teal; java/kotlin/rust/ruby = red)
            ["py"] = Type("python", "Python", "Python", CodeOutlineKind.Indentation, Green, "PY"),
            ["java"] = Type("java", "Java", "Java", CodeOutlineKind.Brace, Red, "JAVA"),
            ["kt"] = Type("kotlin", "Kotlin", string.Empty, CodeOutlineKind.None, Red, "KT"),
            ["kts"] = Type("kotlin", "Kotlin", string.Empty, CodeOutlineKind.None, Red, "KT"),
            ["go"] = Type("go", "Go", string.Empty, CodeOutlineKind.None, Teal, "GO"),
            ["rs"] = Type("rust", "Rust", string.Empty, CodeOutlineKind.None, Red, "RS"),
            ["c"] = Type("c", "C", "C++", CodeOutlineKind.Brace, Teal, "C"),
            ["h"] = Type("c", "C", "C++", CodeOutlineKind.Brace, Teal, "C"),
            ["cpp"] = Type("cpp", "C++", "C++", CodeOutlineKind.Brace, Teal, "C++"),
            ["cc"] = Type("cpp", "C++", "C++", CodeOutlineKind.Brace, Teal, "C++"),
            ["cxx"] = Type("cpp", "C++", "C++", CodeOutlineKind.Brace, Teal, "C++"),
            ["hpp"] = Type("cpp", "C++", "C++", CodeOutlineKind.Brace, Teal, "C++"),
            ["hxx"] = Type("cpp", "C++", "C++", CodeOutlineKind.Brace, Teal, "C++"),
            ["sql"] = Type("sql", "SQL", "SQL", CodeOutlineKind.None, Yellow, "SQL"),
            ["rb"] = Type("ruby", "Ruby", "Ruby", CodeOutlineKind.None, Red, "RB"),
            ["php"] = Type("php", "PHP", "PHP", CodeOutlineKind.None, Orange, "PHP"),

            // Images (green): 字形回退用图片字形,徽标仍为 IMG 缩写
            ["png"] = Type("plaintext", "图片", string.Empty, CodeOutlineKind.None, Green, "IMG", ImageGlyph),
            ["jpg"] = Type("plaintext", "图片", string.Empty, CodeOutlineKind.None, Green, "IMG", ImageGlyph),
            ["jpeg"] = Type("plaintext", "图片", string.Empty, CodeOutlineKind.None, Green, "IMG", ImageGlyph),
            ["gif"] = Type("plaintext", "图片", string.Empty, CodeOutlineKind.None, Green, "IMG", ImageGlyph),
            ["bmp"] = Type("plaintext", "图片", string.Empty, CodeOutlineKind.None, Green, "IMG", ImageGlyph),
            ["webp"] = Type("plaintext", "图片", string.Empty, CodeOutlineKind.None, Green, "IMG", ImageGlyph),
            ["ico"] = Type("plaintext", "图片", string.Empty, CodeOutlineKind.None, Green, "IMG", ImageGlyph),
            ["tiff"] = Type("plaintext", "图片", string.Empty, CodeOutlineKind.None, Green, "IMG", ImageGlyph),

            // Office 文档 / PDF(无源码渲染,按文档族色相区分:Word 蓝 / Excel 绿 / PPT 橙 / PDF 红)
            ["doc"] = Type("plaintext", "Word 文档", string.Empty, CodeOutlineKind.None, Blue, "DOC"),
            ["docx"] = Type("plaintext", "Word 文档", string.Empty, CodeOutlineKind.None, Blue, "DOC"),
            ["xls"] = Type("plaintext", "Excel 表格", string.Empty, CodeOutlineKind.None, Green, "XLS"),
            ["xlsx"] = Type("plaintext", "Excel 表格", string.Empty, CodeOutlineKind.None, Green, "XLS"),
            ["ppt"] = Type("plaintext", "PowerPoint 演示", string.Empty, CodeOutlineKind.None, Orange, "PPT"),
            ["pptx"] = Type("plaintext", "PowerPoint 演示", string.Empty, CodeOutlineKind.None, Orange, "PPT"),
            ["pdf"] = Type("plaintext", "PDF", string.Empty, CodeOutlineKind.None, Red, "PDF"),

            // 数据 / 配置补充(csv 青;properties 复用 INI 语法;env 黄)
            ["csv"] = Type("plaintext", "CSV", string.Empty, CodeOutlineKind.None, Teal, "CSV"),
            ["properties"] = Type("ini", "属性文件", string.Empty, CodeOutlineKind.None, Yellow, "INI"),
            ["env"] = Type("ini", "环境变量", string.Empty, CodeOutlineKind.None, Yellow, "ENV"),

            // 字节码 / 二进制产物(pyc 归 Python 族绿;二进制一律灰)
            ["pyc"] = Type("plaintext", "Python 字节码", string.Empty, CodeOutlineKind.None, Green, "PYC"),
            ["dll"] = Type("plaintext", "二进制文件", string.Empty, CodeOutlineKind.None, Gray, "BIN"),
            ["exe"] = Type("plaintext", "二进制文件", string.Empty, CodeOutlineKind.None, Gray, "BIN"),
            ["so"] = Type("plaintext", "二进制文件", string.Empty, CodeOutlineKind.None, Gray, "BIN"),
            ["dylib"] = Type("plaintext", "二进制文件", string.Empty, CodeOutlineKind.None, Gray, "BIN"),
            ["bin"] = Type("plaintext", "二进制文件", string.Empty, CodeOutlineKind.None, Gray, "BIN"),
            ["pdb"] = Type("plaintext", "二进制文件", string.Empty, CodeOutlineKind.None, Gray, "BIN"),
            ["o"] = Type("plaintext", "二进制文件", string.Empty, CodeOutlineKind.None, Gray, "BIN"),

            // 归档(zip 族 / tar 族,均为灰)
            ["zip"] = Type("plaintext", "压缩文件", string.Empty, CodeOutlineKind.None, Gray, "ZIP"),
            ["jar"] = Type("plaintext", "压缩文件", string.Empty, CodeOutlineKind.None, Gray, "ZIP"),
            ["war"] = Type("plaintext", "压缩文件", string.Empty, CodeOutlineKind.None, Gray, "ZIP"),
            ["whl"] = Type("plaintext", "压缩文件", string.Empty, CodeOutlineKind.None, Gray, "ZIP"),
            ["rar"] = Type("plaintext", "压缩文件", string.Empty, CodeOutlineKind.None, Gray, "ZIP"),
            ["7z"] = Type("plaintext", "压缩文件", string.Empty, CodeOutlineKind.None, Gray, "ZIP"),
            ["tar"] = Type("plaintext", "压缩文件", string.Empty, CodeOutlineKind.None, Gray, "TAR"),
            ["gz"] = Type("plaintext", "压缩文件", string.Empty, CodeOutlineKind.None, Gray, "TAR"),
            ["bz2"] = Type("plaintext", "压缩文件", string.Empty, CodeOutlineKind.None, Gray, "TAR"),
            ["xz"] = Type("plaintext", "压缩文件", string.Empty, CodeOutlineKind.None, Gray, "TAR"),

            // 媒体(与图片同绿色族;字形回退用图片字形,徽标区分音频 / 视频)
            ["mp3"] = Type("plaintext", "音频", string.Empty, CodeOutlineKind.None, Green, "AUD", ImageGlyph),
            ["wav"] = Type("plaintext", "音频", string.Empty, CodeOutlineKind.None, Green, "AUD", ImageGlyph),
            ["flac"] = Type("plaintext", "音频", string.Empty, CodeOutlineKind.None, Green, "AUD", ImageGlyph),
            ["ogg"] = Type("plaintext", "音频", string.Empty, CodeOutlineKind.None, Green, "AUD", ImageGlyph),
            ["aac"] = Type("plaintext", "音频", string.Empty, CodeOutlineKind.None, Green, "AUD", ImageGlyph),
            ["m4a"] = Type("plaintext", "音频", string.Empty, CodeOutlineKind.None, Green, "AUD", ImageGlyph),
            ["mp4"] = Type("plaintext", "视频", string.Empty, CodeOutlineKind.None, Green, "VID", ImageGlyph),
            ["avi"] = Type("plaintext", "视频", string.Empty, CodeOutlineKind.None, Green, "VID", ImageGlyph),
            ["mov"] = Type("plaintext", "视频", string.Empty, CodeOutlineKind.None, Green, "VID", ImageGlyph),
            ["mkv"] = Type("plaintext", "视频", string.Empty, CodeOutlineKind.None, Green, "VID", ImageGlyph),
            ["webm"] = Type("plaintext", "视频", string.Empty, CodeOutlineKind.None, Green, "VID", ImageGlyph),
            ["m4v"] = Type("plaintext", "视频", string.Empty, CodeOutlineKind.None, Green, "VID", ImageGlyph),

            // 数据库 / 字体 / 构建产物 / 补丁
            ["db"] = Type("plaintext", "数据库", string.Empty, CodeOutlineKind.None, Yellow, "DB"),
            ["sqlite"] = Type("plaintext", "数据库", string.Empty, CodeOutlineKind.None, Yellow, "DB"),
            ["sqlite3"] = Type("plaintext", "数据库", string.Empty, CodeOutlineKind.None, Yellow, "DB"),
            ["ttf"] = Type("plaintext", "字体", string.Empty, CodeOutlineKind.None, Purple, "FNT"),
            ["otf"] = Type("plaintext", "字体", string.Empty, CodeOutlineKind.None, Purple, "FNT"),
            ["woff"] = Type("plaintext", "字体", string.Empty, CodeOutlineKind.None, Purple, "FNT"),
            ["woff2"] = Type("plaintext", "字体", string.Empty, CodeOutlineKind.None, Purple, "FNT"),
            ["eot"] = Type("plaintext", "字体", string.Empty, CodeOutlineKind.None, Purple, "FNT"),
            ["gradle"] = Type("kotlin", "Kotlin", string.Empty, CodeOutlineKind.None, Red, "KT"),
            ["patch"] = Type("plaintext", "补丁", string.Empty, CodeOutlineKind.None, Teal, "DIFF"),
            ["diff"] = Type("plaintext", "补丁", string.Empty, CodeOutlineKind.None, Teal, "DIFF"),
            ["lock"] = Type("plaintext", "锁定文件", string.Empty, CodeOutlineKind.None, Gray, "LCK"),
        };

        return map.ToFrozenDictionary(StringComparer.Ordinal);

        static CodeFileType Type(string languageId, string displayName, string highlighting, CodeOutlineKind outline,
            string colorToken, string monogram, string? glyph = null) =>
            WithGrammar(new CodeFileType(languageId, displayName, highlighting, glyph ?? DocGlyph, outline, colorToken, monogram));
    }
}
