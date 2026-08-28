namespace Nornia.Desktop.Terminal;

/// <summary>Standard xterm-256 palette resolution and the ANSI escape parser that feeds
/// <see cref="TerminalScreen"/>. Pure: colors resolve to ARGB ints through the host palette function
/// (theme tokens for the base 16 in production, the standard table in tests/fallback).</summary>
public static class AnsiPalette
{
    /// <summary>Standard xterm 0-255 → ARGB (0xAARRGGBB). The base 16 use the classic xterm values;
    /// 16-231 are the 6×6×6 color cube; 232-255 the grayscale ramp.</summary>
    public static int ToArgb(int index)
    {
        if (index < 0 || index > 255)
        {
            return 0;
        }

        if (index < 16)
        {
            return Base16[index];
        }

        if (index < 232)
        {
            var value = index - 16;
            var red = Cube(value / 36);
            var green = Cube(value / 6 % 6);
            var blue = Cube(value % 6);
            return Argb(red, green, blue);
        }

        var gray = 8 + (index - 232) * 10;
        return Argb(gray, gray, gray);
    }

    private static int Cube(int step) => step is 0 ? 0 : 55 + step * 40;

    private static int Argb(int red, int green, int blue) =>
        unchecked((int)0xFF000000) | (red << 16) | (green << 8) | blue;

    private static readonly int[] Base16 =
    [
        Argb(0, 0, 0),
        Argb(205, 0, 0),
        Argb(0, 205, 0),
        Argb(205, 205, 0),
        Argb(0, 0, 238),
        Argb(205, 0, 205),
        Argb(0, 205, 205),
        Argb(229, 229, 229),
        Argb(127, 127, 127),
        Argb(255, 0, 0),
        Argb(0, 255, 0),
        Argb(255, 255, 0),
        Argb(92, 92, 255),
        Argb(255, 0, 255),
        Argb(0, 255, 255),
        Argb(255, 255, 255),
    ];
}

/// <summary>
/// Streaming ANSI/VT escape parser. Handles the common terminal feature set: printable text with
/// SGR attributes (16/256/truecolor, bold/underline/inverse), CR/LF/TAB/BS, cursor motion
/// (CUP/CUU/CUD/CUF/CUB/CHA/VPA/save+restore), erase (EL/ED incl. scrollback clear), scrolling
/// (SU/SD + scroll region), cursor visibility and the alternate screen buffer (vim/less).
/// Unsupported sequences degrade to no-ops; malformed input is treated as literal text.
/// </summary>
public sealed class AnsiParser
{
    private readonly TerminalScreen _screen;
    private readonly Func<int, int> _palette;

    // Current SGR state
    private int _foreground;
    private int _background;
    private bool _bold;
    private bool _underline;
    private bool _inverse;
    private bool _foregroundIsDefault = true;
    private bool _backgroundIsDefault = true;

    // Escape state machine
    private enum Phase { Text, Escape, Csi, Osc }
    private Phase _phase = Phase.Text;
    private bool _csiPrivate;
    // T3: CSI 参数原地整数累加(替代每次序列的 Split/ToArray 分配)。
    private int[] _csiParams = new int[8];
    private int _csiParamCount;
    private long _csiSegmentValue;
    private bool _csiSegmentHasDigits;
    private bool _csiSegmentInvalid;
    private bool _csiSegmentTrailingSpace;
    private bool _csiSegmentHasAnyContent;
    private bool _csiHasAnyContent;
    // T3: 打印字符批缓冲(纯文本段 → WriteChars 单次进锁 + 单次 Changed)。
    private char[] _textRunBuffer = new char[2048];
    private int _textRunCount;

    public AnsiParser(TerminalScreen screen, Func<int, int>? palette = null)
    {
        _screen = screen;
        _palette = palette ?? AnsiPalette.ToArgb;
        _foreground = 0;
        _background = 0;
    }

    public void Feed(string text)
    {
        foreach (var character in text)
        {
            FeedCharacter(character);
        }

        FlushTextRun();
    }

    /// <summary>The 16 base ANSI colors as a theme-token palette lookup (0-15).</summary>
    public static string BaseColorToken(int index) => (index & 8) == 0
        ? index switch
        {
            0 => "TerminalBlackBrush",
            1 => "TerminalRedBrush",
            2 => "TerminalGreenBrush",
            3 => "TerminalYellowBrush",
            4 => "TerminalBlueBrush",
            5 => "TerminalMagentaBrush",
            6 => "TerminalCyanBrush",
            _ => "TerminalWhiteBrush",
        }
        : (index & 7) switch
        {
            0 => "TerminalBrightBlackBrush",
            1 => "TerminalBrightRedBrush",
            2 => "TerminalBrightGreenBrush",
            3 => "TerminalBrightYellowBrush",
            4 => "TerminalBrightBlueBrush",
            5 => "TerminalBrightMagentaBrush",
            6 => "TerminalBrightCyanBrush",
            _ => "TerminalBrightWhiteBrush",
        };

    private void FeedCharacter(char character)
    {
        switch (_phase)
        {
            case Phase.Text:
                FeedTextCharacter(character);
                break;
            case Phase.Escape:
                FeedEscapeCharacter(character);
                break;
            case Phase.Csi:
                FeedCsiCharacter(character);
                break;
            case Phase.Osc:
                if (character == '\u0007')
                {
                    _phase = Phase.Text;
                }
                else if (character == '\u001b')
                {
                    _oscEscSeen = true;
                }
                else if (_oscEscSeen && character == '\\')
                {
                    _oscEscSeen = false;
                    _phase = Phase.Text;
                }
                else
                {
                    _oscEscSeen = false;
                }

                break;
        }
    }

    private bool _oscEscSeen;

    private void FeedTextCharacter(char character)
    {
        if (character == '\u001b')
        {
            FlushTextRun();
            _phase = Phase.Escape;
            return;
        }

        // 控制字符走单字符路径(光标语义),其余进入批缓冲(T3)。
        if (character is '\r' or '\n' or '\t' or '\b')
        {
            FlushTextRun();
            _screen.WriteChar(character, EffectiveForeground(), EffectiveBackground(), _bold, _underline);
            return;
        }

        if (_textRunCount == _textRunBuffer.Length)
        {
            Array.Resize(ref _textRunBuffer, _textRunBuffer.Length * 2);
        }

        _textRunBuffer[_textRunCount++] = character;
    }

    /// <summary>把积累的纯文本段经 <see cref="TerminalScreen.WriteChars"/> 批量写入
    /// (单次进锁 + 单次 Changed,T3)。</summary>
    private void FlushTextRun()
    {
        if (_textRunCount == 0)
        {
            return;
        }

        _screen.WriteChars(_textRunBuffer.AsSpan(0, _textRunCount),
            EffectiveForeground(), EffectiveBackground(), _bold, _underline);
        _textRunCount = 0;
    }

    private void FeedEscapeCharacter(char character)
    {
        switch (character)
        {
            case '[':
                _phase = Phase.Csi;
                ResetCsiParameters();
                _csiPrivate = false;
                break;
            case ']':
                _phase = Phase.Osc;
                _oscEscSeen = false;
                break;
            case '7':
                _screen.SaveCursor();
                _phase = Phase.Text;
                break;
            case '8':
                _screen.RestoreCursor();
                _phase = Phase.Text;
                break;
            case 'c':
                _screen.Clear();
                ResetSgr();
                _phase = Phase.Text;
                break;
            case '(':
            case ')':
                // Character-set designation: ignore the following byte.
                _phase = Phase.Escape;
                _skipNextEscapeByte = true;
                break;
            default:
                if (_skipNextEscapeByte)
                {
                    _skipNextEscapeByte = false;
                    _phase = Phase.Text;
                }
                else
                {
                    _phase = Phase.Text;
                }

                break;
        }
    }

    private bool _skipNextEscapeByte;

    private void FeedCsiCharacter(char character)
    {
        if (character == '?')
        {
            _csiPrivate = true;
            return;
        }

        if (character >= '0' && character <= '9' || character == ';' || character == ' ')
        {
            AccumulateCsiParameter(character);
            return;
        }

        if (character == ';')
        {
            AccumulateCsiParameter(character);
            return;
        }

        FlushCsiSegment();
        // T3: 参数经原地 int 累加获得,不再每次 Split(';') + Select(ToArray) 分配。
        var parameters = _csiParamCount == 0 && !_csiHasAnyContent
            ? [0]
            : _csiParams.AsSpan(0, _csiParamCount).ToArray();
        ResetCsiParameters();
        _phase = Phase.Text;

        if (_csiPrivate && character is 'h' or 'l')
        {
            HandlePrivateMode(character, parameters);
            return;
        }

        switch (character)
        {
            case 'A':
                _screen.MoveCursorRelative(-DefaultOne(parameters, 0), 0);
                break;
            case 'B':
                _screen.MoveCursorRelative(DefaultOne(parameters, 0), 0);
                break;
            case 'C':
                _screen.MoveCursorRelative(0, DefaultOne(parameters, 0));
                break;
            case 'D':
                _screen.MoveCursorRelative(0, -DefaultOne(parameters, 0));
                break;
            case 'E':
                _screen.MoveCursorRelative(DefaultOne(parameters, 0), 0);
                _screen.SetCursorColumn(0);
                break;
            case 'F':
                _screen.MoveCursorRelative(-DefaultOne(parameters, 0), 0);
                _screen.SetCursorColumn(0);
                break;
            case 'G':
                _screen.SetCursorColumn(DefaultOne(parameters, 0) - 1);
                break;
            case 'H':
            case 'f':
                _screen.MoveCursorTo(DefaultOne(parameters, 0) - 1, DefaultOne(parameters, 1) - 1);
                break;
            case 'J':
                _screen.EraseInDisplay(ValueAt(parameters, 0));
                break;
            case 'K':
                _screen.EraseInLine(ValueAt(parameters, 0));
                break;
            case 'm':
                ApplySgr(parameters);
                break;
            case 'r':
                if (parameters.Length >= 2)
                {
                    _screen.SetScrollRegion(DefaultOne(parameters, 0) - 1, DefaultOne(parameters, 1) - 1);
                }

                break;
            case 'S':
                _screen.ScrollUp(DefaultOne(parameters, 0));
                break;
            case 'T':
                _screen.ScrollDown(DefaultOne(parameters, 0));
                break;
            case 's':
                _screen.SaveCursor();
                break;
            case 'u':
                _screen.RestoreCursor();
                break;
            case 'd':
                _screen.MoveCursorTo(DefaultOne(parameters, 0) - 1, _screen.Cursor.Column);
                break;
            case 'h':
            case 'l':
                // Unimplemented standard modes: ignore.
                break;
            default:
                // Unsupported CSI (insert/delete, repeat, device status...): ignore.
                break;
        }
    }

    /// <summary>CSI 参数原地累加(T3):逐字符维护当前段的值/有效性;';' 或终结字节处
    /// 收尾一段。语义与旧实现(缓冲字符串 → Split(';', RemoveEmptyEntries) →
    /// int.TryParse 失败记 0)完全一致:空段丢弃、空白段解析为 0、起止空白容忍、内部空白/
    /// 溢出解析失败。';' 分隔符本身不算段内容(连续 ';' 产生空段 → 丢弃)。</summary>
    private void AccumulateCsiParameter(char character)
    {
        _csiHasAnyContent = true;
        if (character == ';')
        {
            FlushCsiSegment();
            return;
        }

        _csiSegmentHasAnyContent = true;
        if (character == ' ')
        {
            if (_csiSegmentHasDigits)
            {
                _csiSegmentTrailingSpace = true; // 数字之后的空格:其后若再有数字 → 段无效
            }

            return;
        }

        // digit
        if (_csiSegmentTrailingSpace)
        {
            _csiSegmentInvalid = true;
            _csiSegmentTrailingSpace = false;
        }

        if (!_csiSegmentInvalid)
        {
            _csiSegmentValue = _csiSegmentValue * 10 + (character - '0');
            if (_csiSegmentValue > int.MaxValue)
            {
                _csiSegmentInvalid = true; // 溢出 → int.TryParse 失败 → 0
            }
        }

        _csiSegmentHasDigits = true;
    }

    private void FlushCsiSegment()
    {
        if (!_csiSegmentHasAnyContent)
        {
            return; // 空段(RemoveEmptyEntries 语义:丢弃)
        }

        if (!_csiSegmentHasDigits || _csiSegmentInvalid)
        {
            _csiSegmentValue = 0; // 无数字(纯空白段)或解析失败 → 0
        }

        if (_csiParamCount == _csiParams.Length)
        {
            Array.Resize(ref _csiParams, _csiParams.Length * 2);
        }

        _csiParams[_csiParamCount++] = (int)_csiSegmentValue;
        _csiSegmentValue = 0;
        _csiSegmentHasDigits = false;
        _csiSegmentInvalid = false;
        _csiSegmentTrailingSpace = false;
        _csiSegmentHasAnyContent = false;
    }

    private void ResetCsiParameters()
    {
        _csiParamCount = 0;
        _csiSegmentValue = 0;
        _csiSegmentHasDigits = false;
        _csiSegmentInvalid = false;
        _csiSegmentTrailingSpace = false;
        _csiSegmentHasAnyContent = false;
        _csiHasAnyContent = false;
    }

    private void HandlePrivateMode(char final, int[] parameters)
    {
        var mode = ValueAt(parameters, 0);
        if (mode == 1049)
        {
            if (final == 'h')
            {
                _screen.EnterAlternateBuffer();
            }
            else
            {
                _screen.ExitAlternateBuffer();
            }

            return;
        }

        if (mode == 25)
        {
            _screen.SetCursorVisible(final == 'h');
            return;
        }

        // Other private modes (mouse reporting, bracketed paste, ...) are deliberately ignored.
    }

    private void ApplySgr(int[] parameters)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            var code = parameters[index];
            switch (code)
            {
                case 0:
                    ResetSgr();
                    break;
                case 1:
                    _bold = true;
                    break;
                case 4:
                    _underline = true;
                    break;
                case 7:
                    _inverse = true;
                    break;
                case 22:
                    _bold = false;
                    break;
                case 24:
                    _underline = false;
                    break;
                case 27:
                    _inverse = false;
                    break;
                case 30:
                case 31:
                case 32:
                case 33:
                case 34:
                case 35:
                case 36:
                case 37:
                    _foreground = _palette(code - 30);
                    _foregroundIsDefault = false;
                    break;
                case 38:
                    index = ApplyExtendedColor(parameters, index, setForeground: true);
                    break;
                case 39:
                    _foregroundIsDefault = true;
                    break;
                case 40:
                case 41:
                case 42:
                case 43:
                case 44:
                case 45:
                case 46:
                case 47:
                    _background = _palette(code - 40);
                    _backgroundIsDefault = false;
                    break;
                case 48:
                    index = ApplyExtendedColor(parameters, index, setForeground: false);
                    break;
                case 49:
                    _backgroundIsDefault = true;
                    break;
                case 90:
                case 91:
                case 92:
                case 93:
                case 94:
                case 95:
                case 96:
                case 97:
                    _foreground = _palette(code - 90 + 8);
                    _foregroundIsDefault = false;
                    break;
                case 100:
                case 101:
                case 102:
                case 103:
                case 104:
                case 105:
                case 106:
                case 107:
                    _background = _palette(code - 100 + 8);
                    _backgroundIsDefault = false;
                    break;
            }
        }
    }

    private int ApplyExtendedColor(int[] parameters, int index, bool setForeground)
    {
        if (index + 1 >= parameters.Length)
        {
            return index;
        }

        var mode = parameters[index + 1];
        if (mode == 5 && index + 2 < parameters.Length)
        {
            var color = _palette(Math.Clamp(parameters[index + 2], 0, 255));
            SetColor(color, setForeground);
            return index + 2;
        }

        if (mode == 2 && index + 4 < parameters.Length)
        {
            var red = Math.Clamp(parameters[index + 2], 0, 255);
            var green = Math.Clamp(parameters[index + 3], 0, 255);
            var blue = Math.Clamp(parameters[index + 4], 0, 255);
            SetColor(unchecked((int)0xFF000000) | (red << 16) | (green << 8) | blue, setForeground);
            return index + 4;
        }

        return index;
    }

    private void SetColor(int argb, bool foreground)
    {
        if (foreground)
        {
            _foreground = argb;
            _foregroundIsDefault = false;
        }
        else
        {
            _background = argb;
            _backgroundIsDefault = false;
        }
    }

    private void ResetSgr()
    {
        _foregroundIsDefault = true;
        _backgroundIsDefault = true;
        _foreground = 0;
        _background = 0;
        _bold = false;
        _underline = false;
        _inverse = false;
    }

    private int EffectiveForeground() => _inverse
        ? (_backgroundIsDefault ? DefaultBackground() : _background)
        : (_foregroundIsDefault ? DefaultForeground() : _foreground);

    private int EffectiveBackground() => _inverse
        ? (_foregroundIsDefault ? DefaultForeground() : _foreground)
        : (_backgroundIsDefault ? DefaultBackground() : _background);

    /// <summary>Default fg/bg (ARGB 0x00 means "host decides" — the host resolves theme tokens).</summary>
    private static int DefaultForeground() => 0;

    private static int DefaultBackground() => 0;

    private static int DefaultOne(int[] parameters, int index) => Math.Max(1, ValueAt(parameters, index));

    private static int ValueAt(int[] parameters, int index) => index < parameters.Length ? parameters[index] : 0;
}