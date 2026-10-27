using System.Text;

namespace Nornia.Desktop.Code;

/// <summary>汉字 → 拼音首字母(经典 GB2312/GBK 区位边界算法,零数据依赖):
/// 汉字按 GBK 双字节编码落在 0xB0A1–0xF7FE,各声母首字母的起始码点构成单调边界表,
/// 二分即得首字母。ASCII 字母/数字原样保留(小写),其余字符跳过。
/// 用途:命令面板/快速打开对中文标题的拉丁缩写匹配("dkxml" 命中"打开项目目录")。</summary>
public static class PinyinInitials
{
    private static readonly (char Letter, ushort MinCode)[] Boundaries =
    [
        ('a', 0xB0A1), ('b', 0xB0C5), ('c', 0xB2C1), ('d', 0xB4EE), ('e', 0xB6EA),
        ('f', 0xB7A2), ('g', 0xB8C1), ('h', 0xB9FE), ('j', 0xBBF7), ('k', 0xBFA6),
        ('l', 0xC0AC), ('m', 0xC2E8), ('n', 0xC4C3), ('o', 0xC5B6), ('p', 0xC5BE),
        ('q', 0xC6DA), ('r', 0xC8BB), ('s', 0xC8F6), ('t', 0xCBFA), ('w', 0xCDDA),
        ('x', 0xCEF4), ('y', 0xD1B9), ('z', 0xD4D1),
    ];

    private static readonly Encoding GbEncoding = CreateGbEncoding();

    /// <summary>取标题的匹配用首字母串:汉字 → 声母首字母,ASCII 字母数字保留(小写),其余跳过。</summary>
    public static string GetInitials(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(title.Length);
        var bytes = new byte[2];
        foreach (var ch in title)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (TryEncodeGbk(ch, bytes))
            {
                var letter = LetterFor((ushort)(bytes[0] << 8 | bytes[1]));
                if (letter is not null)
                {
                    builder.Append(letter.Value);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>查询是否适合走首字母档(全为 ASCII 字母数字才有"缩写"语义)。</summary>
    public static bool IsAbbreviationQuery(string query)
    {
        foreach (var ch in query)
        {
            if (!char.IsAsciiLetterOrDigit(ch))
            {
                return false;
            }
        }

        return query.Length > 0;
    }

    private static char? LetterFor(ushort code)
    {
        // 边界表按码点单调:取最后一个 MinCode ≤ code 的字母(线性即可,表仅 23 项)。
        char? result = null;
        foreach (var (letter, minCode) in Boundaries)
        {
            if (code >= minCode)
            {
                result = letter;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private static bool TryEncodeGbk(char ch, byte[] bytes)
    {
        // 只接受落在 GBK 双字节汉字区的结果(两字节且高位字节在 B0–F7)。
        var encoded = GbEncoding.GetBytes([ch]);
        if (encoded.Length == 2 && encoded[0] is >= 0xB0 and <= 0xF7 && encoded[1] is >= 0xA1 and <= 0xFE)
        {
            bytes[0] = encoded[0];
            bytes[1] = encoded[1];
            return true;
        }

        return false;
    }

    private static Encoding CreateGbEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936);
    }
}
