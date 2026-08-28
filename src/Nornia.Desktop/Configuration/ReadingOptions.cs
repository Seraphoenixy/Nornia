using Nornia.Desktop.Services;

namespace Nornia.Desktop.Configuration;

public sealed record CodeReadingOptions(bool WordWrap = false,
    bool ShowMinimap = false, double FontSize = 14, bool ShowLineNumbers = true,
    bool ShowIndentGuides = true, bool ShowFoldingControls = true, bool StickyScroll = true,
    bool LineNumbersRelative = false, bool MinimapRenderCharacters = false,
    double MinimapWidth = 130, double[]? RulerColumns = null,
    string FontFamily = FontCatalog.DefaultEditorFamily);

public enum DiffLayoutMode { Inline, SideBySide }

public sealed record DiffReadingOptions(DiffLayoutMode DefaultLayout = DiffLayoutMode.Inline,
    bool CollapseUnchangedContext = true, bool ShowIntralineChanges = true,
    bool ShowOverviewRuler = true, bool SynchronizeScrolling = true,
    bool IgnoreWhitespaceEndOfLine = false, bool UseInlineWhenNarrow = true);
