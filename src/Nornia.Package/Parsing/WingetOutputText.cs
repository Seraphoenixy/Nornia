namespace Nornia.Package.Parsing;

/// <summary>清洗 winget 进程输出供错误详情展示。winget 的交互式进度用回车(\r)在同一行上
/// 反复重绘转轴帧(- \ | /)并填充大量空格;输出被重定向捕获后,这些重绘帧与空格串全部变成
/// 独立的垃圾行,污染异常消息。清洗规则:每个 \r 重绘行只取最终帧、丢弃空白行与纯转轴/分隔
/// 符行,并限制总长(超长时保留尾部——winget 的结论行在末尾)。</summary>
public static class WingetOutputText
{
    private const int MaxDetailLength = 2000;

    public static string SanitizeDetail(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var lines = raw
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(TailAfterCarriageReturn)
            .Where(HasPrintableContent)
            .Where(line => !IsSpinnerOnly(line))
            .Select(line => line.Trim())
            .ToArray();
        var detail = string.Join(Environment.NewLine, lines);
        if (detail.Length > MaxDetailLength)
        {
            detail = "…" + detail[^MaxDetailLength..];
        }

        return detail;
    }

    /// <summary>回车重绘的行只保留最终一帧(如进度条最终状态)。</summary>
    private static string TailAfterCarriageReturn(string line)
    {
        var lastCr = line.LastIndexOf('\r');
        return lastCr >= 0 ? line[(lastCr + 1)..] : line;
    }

    private static bool HasPrintableContent(string line) => line.Any(static character => !char.IsWhiteSpace(character));

    /// <summary>仅由转轴字符(- \ | /)与空格构成的行:进度帧动画与表格分隔线,对错误详情都是噪声。</summary>
    private static bool IsSpinnerOnly(string line) =>
        line.Length > 0 && line.All(static character => character is '-' or '\\' or '|' or '/' or ' ');
}
