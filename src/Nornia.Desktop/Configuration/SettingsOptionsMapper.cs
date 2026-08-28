using Nornia.Desktop.Services;

namespace Nornia.Desktop.Configuration;

public static class SettingsOptionsMapper
{
    public static CodeReadingOptions CodeReading(SettingsSnapshot snapshot) => new(
        snapshot.Effective(BuiltInSettingsCatalog.EditorWordWrap) != "off",
        snapshot.Effective(BuiltInSettingsCatalog.MinimapEnabled),
        // 源码/Diff 阅读器跟随界面缩放(分辨率适配):显示字号 = 作者字号(editor.fontSize)× uiScale。
        // editor.fontSize 仍以作者字号持久化(EditorAreaViewModel 写回时除以当前倍率,见
        // UiFontService.CurrentScale),避免跨会话重复放大。
        snapshot.Effective(BuiltInSettingsCatalog.EditorFontSize)
            * UiFontService.ClampScale(snapshot.Effective(BuiltInSettingsCatalog.UiScale)),
        snapshot.Effective(BuiltInSettingsCatalog.LineNumbers) != "off",
        snapshot.Effective(BuiltInSettingsCatalog.IndentationGuides),
        snapshot.Effective(BuiltInSettingsCatalog.Folding),
        snapshot.Effective(BuiltInSettingsCatalog.StickyScroll),
        snapshot.Effective(BuiltInSettingsCatalog.LineNumbers) == "relative",
        snapshot.Effective(BuiltInSettingsCatalog.MinimapRenderCharacters),
        snapshot.Effective(BuiltInSettingsCatalog.MinimapWidth),
        ParseRulerColumns(snapshot.Effective(BuiltInSettingsCatalog.EditorRulers)),
        FontFamilyOrDefault(snapshot.Effective(BuiltInSettingsCatalog.EditorFontFamily)));

    /// <summary>空值(手工编辑遗留)回退默认字体,避免渲染路径拿到空字体族。</summary>
    private static string FontFamilyOrDefault(string family) =>
        string.IsNullOrWhiteSpace(family) ? FontCatalog.DefaultEditorFamily : family.Trim();

    /// <summary>把 "80, 120" 解析为标尺列号数组;空/非法项忽略。</summary>
    private static double[]? ParseRulerColumns(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var columns = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => double.TryParse(part, out var value) && value > 0 ? (double?)value : null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return columns.Length == 0 ? null : columns;
    }

    public static DiffReadingOptions Diff(SettingsSnapshot snapshot) => new(
        snapshot.Effective(BuiltInSettingsCatalog.DiffSideBySide) ? DiffLayoutMode.SideBySide : DiffLayoutMode.Inline,
        snapshot.Effective(BuiltInSettingsCatalog.DiffHideUnchanged),
        snapshot.Effective(BuiltInSettingsCatalog.DiffIntraline),
        snapshot.Effective(BuiltInSettingsCatalog.DiffOverview),
        snapshot.Effective(BuiltInSettingsCatalog.DiffSyncScroll),
        snapshot.Effective(BuiltInSettingsCatalog.DiffIgnoreTrimWhitespace),
        snapshot.Effective(BuiltInSettingsCatalog.DiffNarrowInline));
}
