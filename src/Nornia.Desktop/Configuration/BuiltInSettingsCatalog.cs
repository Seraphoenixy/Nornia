using System.Collections.Immutable;
using Nornia.Desktop.Services;

namespace Nornia.Desktop.Configuration;

public sealed class BuiltInSettingsCatalog
{
    public static readonly SettingKey<bool> EnablePreview = new("workbench.editor.enablePreview");
    public static readonly SettingKey<bool> EditorLimitEnabled = new("workbench.editor.limit.enabled");
    public static readonly SettingKey<int> EditorLimitValue = new("workbench.editor.limit.value");
    public static readonly SettingKey<double> EditorFontSize = new("editor.fontSize");
    public static readonly SettingKey<string> EditorFontFamily = new("editor.fontFamily");
    public static readonly SettingKey<string> EditorWordWrap = new("editor.wordWrap");
    public static readonly SettingKey<bool> MinimapEnabled = new("editor.minimap.enabled");
    public static readonly SettingKey<string> LineNumbers = new("editor.lineNumbers");
    public static readonly SettingKey<bool> IndentationGuides = new("editor.guides.indentation");
    public static readonly SettingKey<bool> Folding = new("editor.folding");
    public static readonly SettingKey<bool> DiffSideBySide = new("diffEditor.renderSideBySide");
    public static readonly SettingKey<bool> DiffHideUnchanged = new("diffEditor.hideUnchangedRegions.enabled");
    public static readonly SettingKey<double> TerminalFontSize = new("terminal.integrated.fontSize");
    public static readonly SettingKey<string> TerminalFontFamily = new("terminal.integrated.fontFamily");
    public static readonly SettingKey<string> TerminalDefaultProfile = new("nornia.terminal.defaultProfile");
    public static readonly SettingKey<string[]> TerminalCustomShells = new("nornia.terminal.customShells");
    public static readonly SettingKey<bool> GitAutoRefresh = new("git.autorefresh");
    public static readonly SettingKey<Dictionary<string, bool>> FilesExclude = new("files.exclude");
    public static readonly SettingKey<string> ExternalEditor = new("nornia.general.externalEditor");
    public static readonly SettingKey<bool> RestoreLastWorkspace = new("nornia.general.restoreLastWorkspace");
    public static readonly SettingKey<string> Theme = new("nornia.appearance.theme");
    public static readonly SettingKey<string> Accent = new("nornia.appearance.accent");
    public static readonly SettingKey<double> UiScale = new("nornia.appearance.uiScale");
    public static readonly SettingKey<bool> DiffIntraline = new("nornia.diff.showIntralineChanges");
    public static readonly SettingKey<bool> DiffOverview = new("nornia.diff.showOverviewRuler");
    public static readonly SettingKey<bool> DiffSyncScroll = new("nornia.diff.synchronizeScrolling");
    public static readonly SettingKey<bool> TerminalSidebarVisible = new("nornia.terminal.sessionSidebar.visible");
    public static readonly SettingKey<double> TerminalSidebarWidth = new("nornia.terminal.sessionSidebar.width");
    public static readonly SettingKey<bool> PanelStartExpanded = new("nornia.workbench.panel.startExpanded");
    public static readonly SettingKey<double> SidebarDefaultWidth = new("nornia.workbench.sidebar.defaultWidth");
    public static readonly SettingKey<double> PanelDefaultHeight = new("nornia.workbench.panel.defaultHeight");
    public static readonly SettingKey<bool> ExplorerTreeGuides = new("nornia.explorer.treeGuides");
    public static readonly SettingKey<bool> ExplorerCompactFolders = new("nornia.explorer.compactFolders");
    public static readonly SettingKey<bool> StickyScroll = new("editor.stickyScroll.enabled");
    public static readonly SettingKey<bool> MinimapRenderCharacters = new("editor.minimap.renderCharacters");
    public static readonly SettingKey<double> MinimapWidth = new("editor.minimap.width");
    public static readonly SettingKey<string> EditorRulers = new("editor.rulers");
    public static readonly SettingKey<bool> DiffIgnoreTrimWhitespace = new("diffEditor.ignoreTrimWhitespace");
    public static readonly SettingKey<bool> DiffNarrowInline = new("diffEditor.useInlineViewWhenSpaceIsLimited");

    private static readonly SettingScope EditorScopes = SettingScope.User | SettingScope.Workspace | SettingScope.Language;
    private static readonly SettingScope SharedScopes = SettingScope.User | SettingScope.Workspace;

    public BuiltInSettingsCatalog()
    {
        Definitions = new SettingDefinition[]
        {
            Bool(EnablePreview, true, "启用预览标签", "单击文件时复用预览标签。", "工作台", SharedScopes, "preview", "tab"),
            Bool(EditorLimitEnabled, true, "限制打开的编辑器", "限制同时打开的标签数量。", "工作台", SharedScopes, "limit", "tabs"),
            Number(EditorLimitValue, 30, 10, 100, "最大打开标签数", "超过限制时回收最早的预览标签。", "工作台", SharedScopes),
            Number(EditorFontSize, 14d, 8, 28, "编辑器字号", "源码与 Diff 阅读器使用的独立字号。", "编辑器", EditorScopes),
            Font(EditorFontFamily, FontCatalog.DefaultEditorFamily, "编辑器字体", "源码与 Diff 阅读器使用的等宽字体；下拉框列出该应用支持的全部字体。", "编辑器", EditorScopes),
            Enum(EditorWordWrap, "off", ["off", "on", "wordWrapColumn", "bounded"], "自动换行", "控制长行的换行方式。", "编辑器", EditorScopes),
            Bool(MinimapEnabled, false, "显示迷你地图", "在阅读器右侧显示内容概览。", "编辑器", EditorScopes),
            Enum(LineNumbers, "on", ["off", "on", "relative", "interval"], "行号", "控制行号栏的显示模式。", "编辑器", EditorScopes),
            Bool(IndentationGuides, true, "缩进参考线", "显示缩进层级参考线。", "编辑器", EditorScopes),
            Bool(Folding, true, "折叠控件", "显示代码折叠控件。", "编辑器", EditorScopes),
            Bool(StickyScroll, true, "吸顶滚动（Sticky Scroll）", "滚动时把覆盖视口顶端的折叠段头钉在编辑器顶部。", "编辑器", EditorScopes),
            Bool(MinimapRenderCharacters, false, "迷你地图字符模式", "以字符格渲染迷你地图(关闭为块模式)。", "编辑器", EditorScopes),
            Number(MinimapWidth, 130d, 60, 320, "迷你地图宽度", "右侧迷你地图的像素宽度。", "编辑器", EditorScopes),
            new SettingDefinition<string>(EditorRulers, "80", "垂直标尺", "在指定列绘制垂直参考线(逗号分隔的列号,如 80,120)。",
                "编辑器", ["ruler", "columns"], EditorScopes, SettingEditorKind.String,
                value => string.IsNullOrWhiteSpace(value) ? null : null),
            Bool(DiffSideBySide, true, "并排显示 Diff", "在两个同步阅读面中显示旧版本和新版本。宽屏默认并排，窄窗口可自动回退为内联。", "Diff", SharedScopes),
            Bool(DiffHideUnchanged, true, "折叠未修改区域", "默认隐藏较长的未修改上下文。", "Diff", SharedScopes),
        Bool(DiffIntraline, true, "行内差异", "突出显示行内单词、标识符或标点的变化。", "Diff", SharedScopes),
            Bool(DiffOverview, true, "Diff 概览尺", "显示完整变更分布。", "Diff", SharedScopes),
            Bool(DiffSyncScroll, true, "同步滚动", "并排模式下同步两侧视图。", "Diff", SharedScopes),
            Bool(DiffIgnoreTrimWhitespace, false, "忽略行尾空白", "对比时忽略行尾空白差异。", "Diff", SharedScopes),
            Bool(DiffNarrowInline, true, "窄窗自动内联", "窗口过窄时自动以内联模式显示 Diff。", "Diff", SharedScopes),
            Number(TerminalFontSize, 13d, 8, 28, "终端字号", "终端内容使用的独立字号。", "终端", SharedScopes),
            Font(TerminalFontFamily, FontCatalog.DefaultTerminalFamily, "终端字体", "终端使用的等宽字体；下拉框列出该应用支持的全部字体。", "终端", SharedScopes),
            Text(TerminalDefaultProfile, "pwsh", "默认 Shell", "仅影响之后新建的终端会话。", "终端", SettingScope.User, SettingEditorKind.Shell),
            new SettingDefinition<string[]>(TerminalCustomShells, [], "自定义 Shell", "仅用户级；仓库配置不能启动外部进程。",
                "终端", ["shell", "profile"], SettingScope.User, SettingEditorKind.Shell),
            Bool(TerminalSidebarVisible, true, "显示会话侧栏", "显示终端会话列表。", "终端", SettingScope.User),
            Number(TerminalSidebarWidth, 190d, 140, 320, "会话侧栏宽度", "终端会话侧栏的默认宽度。", "终端", SettingScope.User),
            Bool(GitAutoRefresh, true, "Git 自动刷新", "文件变化后刷新源代码管理状态。", "工作区与源代码管理", SharedScopes),
            new SettingDefinition<Dictionary<string, bool>>(FilesExclude, new(), "排除的文件", "影响资源管理器、快速打开和全局搜索，不改变 Git 或文件系统。",
                "工作区与源代码管理", ["glob", "exclude"], SharedScopes, SettingEditorKind.Glob),
            Bool(ExplorerTreeGuides, true, "资源管理器树状线", "显示文件树层级参考线。", "工作区与源代码管理", SharedScopes),
            Bool(ExplorerCompactFolders, true, "紧凑文件夹", "压缩只有单个子目录的文件夹链。", "工作区与源代码管理", SharedScopes),
            Text(ExternalEditor, "code", "外部编辑器命令", "仅用户级；用于启动外部进程，仓库设置不能覆盖。", "常用", SettingScope.User, SettingEditorKind.Path),
            Bool(RestoreLastWorkspace, true, "恢复上次工作区", "启动时重新打开本机记录的上次工作区。", "常用", SettingScope.User),
            Enum(Theme, "Dark", ["Dark", "Light", "HighContrast"], "主题", "选择工作台颜色主题。", "外观", SettingScope.User),
            new SettingDefinition<string>(Accent, "Default", "强调色", "设置活动控件和焦点的强调色。",
                "外观", ["color", "accent"], SettingScope.User, SettingEditorKind.Color,
                value => value is "Default" or "Teal" or "Iris" || ThemeFactory.TryParseHex(value, out _)
                    ? null : "请输入 Default、Teal、Iris 或 #RRGGBB/#AARRGGBB。"),
            Number(UiScale, 1d, .85, 1.3, "界面缩放", "缩放界面字体和图标，布局间距保持稳定。", "外观", SettingScope.User),
            Bool(PanelStartExpanded, false, "启动时展开面板", "打开工作台时显示底部面板。", "布局", SettingScope.User),
            Number(SidebarDefaultWidth, 300d, 170, 720, "侧栏默认宽度", "没有本机拖拽状态时使用。", "布局", SettingScope.User),
            Number(PanelDefaultHeight, 240d, 96, 1600, "面板默认高度", "没有本机拖拽状态时使用；终端打开时会自动满足终端最小高度。", "布局", SettingScope.User),
        }.ToImmutableDictionary(item => item.Id, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, SettingDefinition> Definitions { get; }
    public bool TryGet(string id, out SettingDefinition definition) => Definitions.TryGetValue(id, out definition!);

    private static SettingDefinition<bool> Bool(SettingKey<bool> key, bool value, string title, string description,
        string category, SettingScope scopes, params string[] keywords) =>
        new(key, value, title, description, category, keywords, scopes, SettingEditorKind.Boolean);

    private static SettingDefinition<int> Number(SettingKey<int> key, int value, int min, int max,
        string title, string description, string category, SettingScope scopes) =>
        new(key, value, title, description, category, [], scopes, SettingEditorKind.Number,
            item => item is < 0 || item < min || item > max ? $"值必须介于 {min} 和 {max} 之间。" : null);

    private static SettingDefinition<double> Number(SettingKey<double> key, double value, double min, double max,
        string title, string description, string category, SettingScope scopes) =>
        new(key, value, title, description, category, [], scopes, SettingEditorKind.Number,
            item => double.IsFinite(item) && item >= min && item <= max ? null : $"值必须介于 {min} 和 {max} 之间。");

    private static SettingDefinition<string> Enum(SettingKey<string> key, string value, IReadOnlyList<string> options,
        string title, string description, string category, SettingScope scopes) =>
        new(key, value, title, description, category, options, scopes, SettingEditorKind.Enumeration, null, options);

    /// <summary>字体选择与 <see cref="Enum"/> 同构(值必须取自候选列表),但编辑器显示为带
    /// 字体预览的下拉框,明确列出本应用支持的字体(<see cref="SettingEditorKind.Font"/>)。</summary>
    private static SettingDefinition<string> Font(SettingKey<string> key, string value, string title,
        string description, string category, SettingScope scopes) =>
        new(key, value, title, description, category, [key.Id], scopes, SettingEditorKind.Font, null,
            FontCatalog.SupportedMonoFamilies);

    private static SettingDefinition<string> Text(SettingKey<string> key, string value, string title,
        string description, string category, SettingScope scopes, SettingEditorKind kind = SettingEditorKind.String) =>
        new(key, value, title, description, category, [], scopes, kind,
            item => string.IsNullOrWhiteSpace(item) ? "值不能为空。" : null);
}
