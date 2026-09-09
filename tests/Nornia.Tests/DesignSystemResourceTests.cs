using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Nornia.Tests;

/// <summary>
/// Design-system consistency guards (desktop layer). These are pure text/XML assertions over the
/// XAML sources — no WPF runtime or STA is required — so they run in every CI environment:
///
/// * the three theme dictionaries (Dark / Light / High Contrast) define exactly the same color
///   tokens (a missing key would surface as a broken DynamicResource at runtime after switching),
/// * every DynamicResource token referenced by App.xaml and the Views resolves to a theme token,
/// * StaticResource references in each view resolve against App.xaml or the view's own resources,
/// * business views contain no hard-coded color literals (all colors ride theme tokens),
/// * the interaction-state contract stays in place: every shared control style and every custom
///   template (activity bar, workspace tree, collapsible section, editor/terminal tabs, status bar,
///   window controls) keeps hover + pressed + keyboard-focus feedback, and disabled controls drop
///   contrast and the hand cursor,
/// * the representative layout spec (reflowable Dashboard metrics, wrap-capable toolbars, settings
///   card / status-badge / selection-summary primitives) stays in place.
/// </summary>
public sealed class DesignSystemResourceTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    private static readonly string[] ThemeFiles =
    [
        "src/Nornia.Desktop/Themes/Dark.xaml",
        "src/Nornia.Desktop/Themes/Light.xaml",
        "src/Nornia.Desktop/Themes/HighContrast.xaml",
    ];

    private static readonly string[] RequiredThemeTokens =
    [
        // core surfaces + controls
        "WindowBrush", "EditorBrush", "SideBarBrush", "ActivityBarBrush", "PanelBrush",
        "CardBrush", "InputBrush", "HoverBrush", "SelectionBrush", "BorderBrush", "StrongBorderBrush",
        // hover / tooltip surfaces (VS Code editorHoverWidget): 提交详情悬浮窗背景与边框
        "ToolTipBackgroundBrush", "ToolTipBorderBrush",
        "AccentBrush", "ButtonBrush", "ButtonHoverBrush", "ButtonSecondaryBrush",
        // interaction states (hover / pressed / active / focus / danger-pressed)
        "PressedBrush", "PressedBorderBrush", "ButtonPressedBrush", "DangerPressedBrush",
        "StatusBarItemPressedBackgroundBrush",
        // foregrounds / input cursor
        "TextBrush", "BrightTextBrush", "MutedTextBrush", "SubtleTextBrush", "CaretBrush",
        "SelectionActiveBrush", "TextSelectionBrush", "FocusBorderBrush", "InputBorderBrush",
        // semantic status
        "DangerBrush", "SuccessBrush", "WarningBrush", "InfoBrush", "InfoAccentBrush",
        "ErrorTextBrush", "LinkBrush", "CloseHoverBrush",
        "SidebarBadgeBackgroundBrush", "SidebarBadgeForegroundBrush",
        // status bar (StatusKindToBrushConverter resolves these by name)
        "StatusBarBackgroundBrush", "StatusBarForegroundBrush",
        "StatusBarSuccessBrush", "StatusBarWarningBrush", "StatusBarErrorBrush",
        "StatusBarBorderBrush", "StatusBarItemHoverBackgroundBrush",
        // read-only code editor (line numbers, current line, selection, search, fold, guides)
        "CodeLineNumberBrush", "CodeLineNumberActiveBrush",
        "CodeCurrentLineBrush", "CodeCurrentLineBorderBrush", "CodeSelectionBrush",
        "CodeSearchMatchBrush", "CodeSearchCurrentMatchBrush",
        "CodeFoldMarkerBrush", "CodeFoldMarkerForegroundBrush",
        "CodeIndentationGuideBrush", "CodeIndentationGuideActiveBrush",
        "CodeMinimapBackgroundBrush", "CodeMinimapTextBrush", "CodeMinimapMatchBrush",
        // diff viewer (inline + side-by-side backgrounds and gutters)
        "DiffAddedBrush", "DiffRemovedBrush", "DiffHeaderBrush",
         "DiffModifiedBrush", "DiffModifiedBarBrush",
         "DiffAddedTextBrush", "DiffRemovedTextBrush", "DiffLineNumberBrush",
         "DiffExpandPlaceholderBrush", "DiffEditorBorderBrush", "DiffEditorSashBrush",
         "DiffOverviewRailBrush", "DiffOverviewViewportBrush", "DiffUnchangedRegionBrush",
         "DiffPlaceholderForegroundBrush", "DiffToolbarSeparatorBrush",
        "EditorGutterAddedBrush", "EditorGutterDeletedBrush",
        // scrollbar (VS Code: ~10px translucent rounded thumb, no arrows)
        "ScrollBarThumbBrush", "ScrollBarThumbHoverBrush", "ScrollBarThumbActiveBrush",
        // SCM tree indent guides (vertical line under the collapse chevron)
        "TreeGuideBrush",
        // commit-graph lane palette (GitGraphCell cycles by lane index across the six hues)
        "GraphLane1Brush", "GraphLane2Brush", "GraphLane3Brush",
        "GraphLane4Brush", "GraphLane5Brush", "GraphLane6Brush",
        // terminal palette (ANSI 16 + defaults + cursor; interactive ConPTY surface)
        "TerminalDefaultForegroundBrush", "TerminalDefaultBackgroundBrush", "TerminalCursorBrush",
        "TerminalBlackBrush", "TerminalRedBrush", "TerminalGreenBrush", "TerminalYellowBrush",
        "TerminalBlueBrush", "TerminalMagentaBrush", "TerminalCyanBrush", "TerminalWhiteBrush",
        "TerminalBrightBlackBrush", "TerminalBrightRedBrush", "TerminalBrightGreenBrush",
        "TerminalBrightYellowBrush", "TerminalBrightBlueBrush", "TerminalBrightMagentaBrush",
        "TerminalBrightCyanBrush", "TerminalBrightWhiteBrush",
        // file-type icon hue families (Seti-style per-language color)
        "FileTypeIconBlueBrush", "FileTypeIconGreenBrush", "FileTypeIconYellowBrush",
        "FileTypeIconOrangeBrush", "FileTypeIconPurpleBrush", "FileTypeIconRedBrush",
        "FileTypeIconTealBrush", "FileTypeIconGrayBrush",
        // split-button inner divider
        "ButtonSplitDividerBrush",
    ];

    private static readonly string[] RequiredLayoutKeys =
    [
        // page hierarchy primitives
        "PageRootStyle", "PageTitleStyle", "PageHeroTitleStyle", "SectionHeaderStyle",
        "CaptionStyle", "SelectionSummaryStyle", "ResultStripStyle",
        // intermediate type ladder (rounded-modern): BodyLg / Subtitle / Section title fill the 13→20 gap
        "BodyLgTextStyle", "SubtitleTextStyle", "SectionTitleStyle",
        // action hierarchy
        "PrimaryButton", "DangerButton", "IconButton", "LinkButton", "PanelTabButton",
        "AppFocusRing", "WindowControlButtonStyle",
        // surfaces
        "CardStyle", "MetricCardStyle", "SettingsCardStyle", "ListItemButtonStyle",
        "InfoBannerStyle", "StatusBadgeStyle", "EmptyStateStyle",
        // data-page toolbars
        "ToolbarRowStyle", "ToolbarActionsStyle",
        // sidebar chrome
        "SideBarTitleBarStyle", "SideBarTitleTextStyle",
        "SideBarSectionHeaderStyle", "SideBarSectionHeaderTextStyle", "CellFocusVisual",
    ];

    /// <summary>Font family / font size / measurement tokens: the single source of truth for all
    /// interface typography (views must not hard-code sizes or family literals). The UiFontService
    /// rewrites the scalable doubles at runtime, so every consumer rides DynamicResource.</summary>
    private static readonly string[] RequiredFontKeys =
    [
        // families
        "UiFontFamily", "IconFontFamily", "MonoFontFamily",
        // type scale (text): Body 13 承载全部小字阶;中间档 BodyLg 15 / Subtitle 16 / Heading 18;大字阶 Title/Display/Hero
        "TypeBody", "TypeBadge", "TypeBodyLg", "TypeSubtitle", "TypeHeading", "TypeTitle", "TypeDisplay", "TypeHero",
        // icon glyph scale (one size per glyph role: 行级一律 IconInline,其余按角色分离)
        "IconChevron", "IconInline", "IconNav", "IconClose", "IconActivity", "IconEmpty", "IconBadge",
        // measurement tokens (row/control heights that follow the font scale — VS Code 默认桌面密度:
        // 标题/侧栏标题/标签 35、状态栏 22、紧凑行/表格行 22、活动栏 48)
        "SizeRowSm", "SizeRowCompact", "SizeSidebarBadge", "SizeTableRow", "SizeRowMd", "SizeControlMd",
        "SizeStatusBar", "SizeTitleBar", "SizeActivity", "SizeTabBar", "SizeCommitBox",
    ];

    /// <summary>Semantic spacing roles shared by the workbench and representative business views.
    /// Structural pixels such as borders, splitters, minimap width and data-column widths remain
    /// deliberately local because they are capacity constraints rather than visual rhythm.</summary>
    private static readonly string[] RequiredSpacingKeys =
    [
        "SpaceXs", "SpaceSm", "SpaceMd", "SpaceLg", "SpaceXl", "SpaceXxl",
        "InsetPage", "InsetCard", "InsetToolbar", "InsetSidebarTitle", "InsetCompactRow",
        "InsetBadge", "InsetLink", "InsetIconButton", "InsetStatusItem",
        "GapXs", "GapSm", "GapMd", "GapLg", "GapLeftSm", "GapLeftMd", "GapLeftLg",
        "GapBottomSm", "GapBottomMd", "GapRightSm", "GapRightMd", "GapVerticalSm",
        "MarginBanner", "MarginCard", "MarginFieldRow", "MarginActionsRow",
    ];

    /// <summary>Corner-radius scale for the rounded-modern surfaces. RadiusPill (999) is the fully
    /// rounded capsule used by badges and the scrollbar thumb.</summary>
    private static readonly string[] RequiredRadiusKeys =
    [
        "RadiusSm", "RadiusMd", "RadiusLg", "RadiusXl", "RadiusPill",
    ];

    // 属性形式与 Setter 形式都要覆盖:<... FontSize="12"/> 与 <Setter Property="FontSize" Value="12"/>。
    private static readonly Regex FontSizeLiteralRegex = new(
        @"FontSize=""\d|Property=""FontSize"" Value=""\d", RegexOptions.Compiled);

    private static readonly Regex ColorLiteralRegex = new(
        @"#[0-9A-Fa-f]{6,8}|(?<prop>Foreground|Background|BorderBrush|CaretBrush|SelectionBrush)=\s*""(?<name>White|Black|Gray|Silver|Red|Green|Blue|Yellow|Orange|Purple|Aqua|Navy|Transparent|LightGray|DarkGray|DarkRed|DarkGreen|DarkBlue|Magenta|Cyan)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DynamicResourceRegex = new(@"\{DynamicResource\s+([^}\s,]+)", RegexOptions.Compiled);
    private static readonly Regex StaticResourceRegex = new(@"\{StaticResource\s+([^}\s,]+)", RegexOptions.Compiled);
    private static readonly Regex XKeyRegex = new(@"x:Key=[""']([^""']+)[""']", RegexOptions.Compiled);

    // ===== theme tokens =====

    [Fact]
    public void ThreeThemes_DefineIdenticalTokenSets()
    {
        var sets = ThemeFiles.Select(ReadTokenKeys).ToArray();
        Assert.All(sets, set => Assert.NotEmpty(set));
        for (var i = 1; i < sets.Length; i++)
        {
            Assert.True(sets[i].SetEquals(sets[0]),
                $"主题 {ThemeFiles[i]} 的令牌集合与 {ThemeFiles[0]} 不一致。缺失: {string.Join(", ", sets[0].Except(sets[i]))}；多余: {string.Join(", ", sets[i].Except(sets[0]))}");
        }
    }

    [Fact]
    public void ThreeThemes_DefineControlAndStatusTokens()
    {
        foreach (var path in ThemeFiles)
        {
            var keys = ReadTokenKeys(path);
            foreach (var token in RequiredThemeTokens)
            {
                Assert.Contains(token, keys);
            }
        }
    }

    // ===== SCM / explorer status letters (phase 1 audit) =====

    [Fact]
    public void StatusLetterTriggers_BindToTextInsteadOfRowItem()
    {
        var control = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/FileChangeStatusIndicator.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/FileChangeStatusIndicator.xaml.cs"));
        Assert.Contains("FileChangeStatusIndicator", File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml")));
        Assert.Contains("FileChangeStatusIndicator", File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/WorkspaceView.xaml")));
        Assert.Contains("SidebarFileStatusKind.Added", control);
        Assert.Contains("SidebarFileStatusKind.Conflict", control);
        Assert.Contains("\"A\" or \"?\"", code);
        var workspace = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/WorkspaceView.xaml"));
        Assert.Contains("WorkspaceFileNameStyle", workspace);
        Assert.Contains("WorkspaceFolderNameStyle", workspace);
        Assert.Contains("Value=\"M\"><Setter Property=\"Foreground\" Value=\"{DynamicResource ScmModifiedBrush}\"", workspace);
        Assert.Contains("Value=\"D\"><Setter Property=\"Foreground\" Value=\"{DynamicResource ScmDeletedBrush}\"", workspace);
        Assert.DoesNotContain("WorkspaceStatusLetterStyle", workspace);
        Assert.DoesNotContain("ScmStatusLetterStyle", File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml")));
    }

    [Fact]
    public void DiffSummary_AddedAndRemovedCountsAreColoredSeparately()
    {
        var diff = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml"));
        // +N 绿色、−M 红色,不再整体一个颜色
        Assert.Contains("<Run Foreground=\"{DynamicResource DiffAddedTextBrush}\" Text=\"{Binding AddedCount, Mode=OneWay}\"", diff);
        Assert.Contains("<Run Foreground=\"{DynamicResource DiffRemovedTextBrush}\" Text=\"{Binding RemovedCount, Mode=OneWay}\"", diff);
    }

    [Fact]
    public void DiffToolbar_DoesNotExposeIgnoreWhitespaceButton()
    {
        var diff = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml"));
        Assert.DoesNotContain("ToggleIgnoreWhitespaceCommand", diff);
        Assert.DoesNotContain("忽略行尾空白（重算 Diff）", diff);
    }

    [Fact]
    public void DiffLineNumberMargin_ColorsAddedAndRemovedByKind()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml.cs"));
        Assert.Contains("GitDiffLineKind.Added => _addedNumberBrush", code);
        Assert.Contains("GitDiffLineKind.Removed => _removedNumberBrush", code);
        Assert.Contains("_addedNumberBrush = _owner.Brush(\"SuccessBrush\")", code);
        Assert.Contains("_removedNumberBrush = _owner.Brush(\"ScmDeletedBrush\")", code);
        Assert.Contains("var signWidth = numberWidth + 18", code);
    }

    [Fact]
    public void DiffIntralineHighlight_UsesExactFillWithoutOutline()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml.cs"));
        Assert.Contains("DiffIntralineBackgroundRenderer", code);
        Assert.Contains("BackgroundGeometryBuilder.GetRectsForSegment", code);
        Assert.Contains("geometryBuilder.AddRectangle(textView, rect)", code);
        Assert.DoesNotContain("Inflate(1, 1)", code);
        Assert.Contains("drawingContext.DrawGeometry(fill, null, geometry)", code);
        Assert.DoesNotContain("BorderThickness = 1", code);
        Assert.DoesNotContain("new Pen(", code);
    }

    [Fact]
    public void DiffEditors_DoNotAddSelectionPadding()
    {
        var diff = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml"));
        Assert.Equal(3, Regex.Matches(diff, "<av:TextEditor ").Count);
        Assert.Equal(3, Regex.Matches(diff, "Padding=\"0\"").Count);
        Assert.DoesNotContain("Padding=\"8,4,8,4\"", diff);

        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml.cs"));
        Assert.Contains("editor.TextArea.SelectionBorder = null", code);
        Assert.Contains("editor.TextArea.SelectionCornerRadius = 0", code);
    }

    [Fact]
    public void GitGraphCell_UsesThemeGraphLaneTokens()
    {
        // 提交图形泳道的六色调色板必须来自三主题共有的 GraphLane*Brush 令牌,
        // 不得回退到语义状态刷(蓝×2、深红在暗色侧栏不可见等旧问题)。
        var cell = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/GitGraphCell.cs"));
        foreach (var key in new[] { "GraphLane1Brush", "GraphLane2Brush", "GraphLane3Brush", "GraphLane4Brush", "GraphLane5Brush", "GraphLane6Brush" })
        {
            Assert.Contains(key, cell);
        }

        // 泳道几何与视图列宽同源:模板列宽注释引用 GitGraphLayout.CellWidth,二者不得漂移。
        var gitView = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        Assert.Contains("GitGraphLayout.CellWidth", gitView);
        Assert.Contains("row.DotDashed ? DashStyles.Dash : DashStyles.Solid", cell);
        Assert.Contains("<Run Text=\"{Binding SyncTarget, Mode=OneWay}\"", gitView);
        Assert.Contains("分界位于远端提交之后、共同/本地历史之前", gitView);
    }

    [Fact]
    public void ButtonOpenedContextMenus_ToggleClosedOnSecondClick()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml.cs"));
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));

        Assert.Equal(2, Regex.Matches(code, "ToggleButtonContextMenu\\(button, menu\\);").Count);
        // Toggle 必须在 Preview 阶段完成:下压时菜单仍开着,此刻关闭并吞掉事件。若放行到
        // Click,ButtonBase 在 MouseDown 捕获鼠标时已顺带关闭菜单(Closed 路由事件在该
        // 路径不触发),Click 只能看到一个已关闭的菜单而立即重开(再次点击关不掉)。
        Assert.Equal(5, Regex.Matches(view, "PreviewMouseLeftButtonDown=\"MenuToggleButton_PreviewMouseLeftButtonDown\"").Count);
        Assert.Contains("IsOpen: true } menu", code);
        // 双保险:Click 侧以 IsOpen 变化记录的"刚关闭时刻"兜底(关闭与 Click 的竞态、
        // 或预览阶段被外部关闭处理器抢先时,靠 400ms 时间窗识别"这次按压就是关闭手势")。
        Assert.Contains("DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty", code);
        Assert.Contains("Environment.TickCount64 - closedAt < 400", code);
        Assert.DoesNotContain("_suppressMenuOpenButtons", code);
        Assert.DoesNotContain("button.IsMouseOver", code);
    }

    [Fact]
    public void DiffHighlighting_ContrastOnTintedBackground()
    {
        // Diff 行背景(DiffAddedBrush/DiffRemovedBrush)叠加在语法高亮文本之下:最弱的
        // CodeTokenComment 在两种染色底上的对比度也必须 ≥3.0,否则 Added/Removed 行里的注释不可读。
        // HighContrast 的底色是强色块,由 HighlightingPalette_MeetsEditorContrastFloor 覆盖。
        var keyRegex = new Regex(@"x:Key=""(?<key>CodeTokenCommentBrush|DiffAddedBrush|DiffRemovedBrush)"" Color=""#(?<hex>[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?)""", RegexOptions.Compiled);
        foreach (var path in ThemeFiles.Take(2))
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot, path));
            var colors = new Dictionary<string, (double R, double G, double B, double A)>(StringComparer.Ordinal);
            foreach (Match match in keyRegex.Matches(text))
            {
                colors[match.Groups["key"].Value] = ParseXamlColor(match.Groups["hex"].Value);
            }

            Assert.True(colors.TryGetValue("CodeTokenCommentBrush", out var comment), $"{path} 缺少 CodeTokenCommentBrush");
            var editorMatch = Regex.Match(text, @"x:Key=""EditorBrush"" Color=""#(?<hex>[0-9A-Fa-f]{6})""");
            Assert.True(editorMatch.Success, $"{path} 缺少 EditorBrush");
            var editor = ParseHexColor(editorMatch.Groups["hex"].Value);
            foreach (var backgroundKey in new[] { "DiffAddedBrush", "DiffRemovedBrush" })
            {
                Assert.True(colors.TryGetValue(backgroundKey, out var background), $"{path} 缺少 {backgroundKey}");
                var compositedBackground = CompositeOver(background, editor);
                var ratio = ContrastRatio(Luminance(compositedBackground), Luminance((comment.R, comment.G, comment.B)));
                Assert.True(ratio >= 3.0, $"{path} 的 CodeTokenCommentBrush 与 {backgroundKey} 对比度 {ratio:F2} < 3.0");
            }
        }
    }

    [Fact]
    public void TextEditorImplicitStyle_SetsSharpTextRenderingAndPadding()
    {
        // 代码源视图与 Diff 视图的全部 TextEditor 共用隐式样式:Display 格式化 + ClearType
        // 渲染消除像素模糊、UseLayoutRounding 对齐物理像素、Padding 给代码区呼吸空间;
        // 等宽字体令牌主推 Cascadia Code(Win11 内建连字),兜底 Consolas。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        Assert.Contains("<FontFamily x:Key=\"MonoFontFamily\">Cascadia Code", app);
        Assert.Contains("<Style TargetType=\"av:TextEditor\">", app);
        Assert.Contains("Property=\"TextOptions.TextFormattingMode\" Value=\"Display\"", app);
        Assert.Contains("Property=\"TextOptions.TextRenderingMode\" Value=\"ClearType\"", app);
        Assert.Contains("Property=\"UseLayoutRounding\" Value=\"True\"", app);
        Assert.Contains("Property=\"Padding\" Value=\"4,2,4,2\"", app);
    }

    [Fact]
    public void DiffDocumentView_AppliesHighlightingAndMetaLineTransformer()
    {
        // Diff 视图使用与资源管理器相同的 TextMate token 快照和 CodeToken* 调色板；
        // hunk header/notice 行由 meta 转换器弱化,主题切换经 ApplyHighlightingTheme 即时重着色。
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml.cs"));
        Assert.Contains("CodePresentationService.Instance", source);
        Assert.Contains("CodeFileTypeRegistry.Instance.FromPath", source);
        Assert.Contains("CodeTokenColorizer", source);
        Assert.Contains("SetSnapshot", source);
        Assert.Contains("LineTransformers.Add", source);
        Assert.Contains("DiffMetaLineTransformer", source);
        Assert.Contains("ThemeHighlightingColorizer.ApplyDefinitionTheme", source);
        Assert.Contains("ApplyHighlightingTheme();", source);
    }

    [Fact]
    public void DiffDocumentView_ExposesHunkActionsInBothLayouts()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml.cs"));
        Assert.Contains("InlineHunkActionBar", view);
        Assert.Contains("SideHunkActionBar", view);
        Assert.Contains("DiffHunkActionButtonStyle", view);
        Assert.Contains("Orientation=\"Vertical\"", view);
        Assert.Contains("HunkActionGutterWidth", code);
        Assert.Contains("gutterLeft", code);
        Assert.Contains("Content=\"{StaticResource CodiconAdd}\"", view);
        Assert.Contains("Content=\"{StaticResource CodiconRemove}\"", view);
        Assert.Contains("Content=\"{StaticResource CodiconDiscard}\"", view);
        Assert.Contains("暂存当前 Diff 块", view);
        Assert.Contains("还原当前 Diff 块", view);
        Assert.Contains("OnDiffContextMenuOpening", code);
        Assert.Contains("HunkIndexAtDisplayLine", code);
        Assert.Contains("FindHunkAnchorLine", code);
        Assert.Contains("ApplyHunkBlockAsync", code);
        Assert.Contains("TryResolveHunkBlock", code);
    }

    // ===== Phase 4: interaction / tooltip / font-token guards =====

    [Fact]
    public void SidebarAndPanelTitleBarButtons_NoToolTip_UseAccessibilityNames()
    {
        // 侧栏/面板标题栏图标按钮统一用 AutomationProperties.Name 提供语义,不挂 ToolTip
        // (悬停弹窗会在点击瞬间拦截按钮事件,造成偶发点击失效)。
        var git = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        var titleBar = "视图切换：平铺 ↔ 树状分组（VS Code SCM 标题栏布局切换）";
        var block = git[git.IndexOf(titleBar, StringComparison.Ordinal)..git.IndexOf("</StackPanel>", git.IndexOf(titleBar, StringComparison.Ordinal))];
        Assert.Contains("AutomationProperties.Name=\"切换为树状分组视图\"", block);
        Assert.Contains("AutomationProperties.Name=\"刷新 Git 状态\"", block);
        Assert.Contains("AutomationProperties.Name=\"打开或更换仓库目录\"", block);
        Assert.DoesNotContain("ToolTip=", block);

        var workspace = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/WorkspaceView.xaml"));
        Assert.Contains("AutomationProperties.Name=\"打开项目目录\"", workspace);

        var main = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml"));
        Assert.Contains("AutomationProperties.Name=\"{loc:StringLoc Key=ClearOutput}\"", main);
        Assert.Contains("AutomationProperties.Name=\"{loc:StringLoc Key=CollapsePanel}\"", main);
        Assert.DoesNotContain("ToolTip=\"{loc:StringLoc Key=ClearOutput}\"", main);
    }

    [Fact]
    public void ImplicitListBoxItemStyle_HasFourInteractionStates()
    {
        // 数据页(Runtime/Tools/Packages/Cache/Projects)的行交互统一来自 App 级隐式 ListBoxItem 样式。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var block = StyleBlock(app, "<Style TargetType=\"ListBoxItem\">");
        Assert.Contains("IsMouseOver", block);
        Assert.Contains("IsSelected", block);
        Assert.Contains("IsMouseCaptured", block);
        Assert.Contains("IsKeyboardFocusWithin", block);
        Assert.Contains("FocusVisualStyle", block);
        Assert.Contains("HoverBrush", block);
        Assert.Contains("PressedBrush", block);
        Assert.Contains("SelectionActiveBrush", block);
    }

    [Fact]
    public void Views_UseMonoFontFamilyTokenNotHardCodedFamily()
    {
        // 字体家族统一走 App.xaml 令牌:等宽走 MonoFontFamily、Codicon 字形走 IconFontFamily、
        // 界面文本继承 Window 的 UiFontFamily——视图不得出现任何家族字面量(App.xaml 定义行除外)。
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views"), "*.xaml", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views"), "*.xaml.cs", SearchOption.AllDirectories)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Cascadia Mono", text);
            Assert.DoesNotContain("Segoe MDL2 Assets", text);
            Assert.DoesNotContain("Segoe UI", text);
        }
    }

    [Fact]
    public void Views_ContainNoHardCodedFontSizeLiterals()
    {
        // 界面字号的单一事实来源是 App.xaml 令牌;视图里的 FontSize 只能引用令牌,
        // 这样全局界面字号倍率(UiFontService)才能即时缩放全部文字与字形。
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views"), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in FontSizeLiteralRegex.Matches(text))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0, "业务视图中的硬编码字号：" + string.Join("; ", offenders.Distinct()));
    }

    [Fact]
    public void FontSizeTokenReferences_UseDynamicResource()
    {
        // 字号令牌必须走 DynamicResource,否则运行期改写令牌值(缩放倍率)不会刷新已打开的页面。
        foreach (var file in AllXamlFiles())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("FontSize=\"{StaticResource", text);
        }
    }

    [Fact]
    public void TypographyRoles_StayUnifiedAcrossViews()
    {
        // 角色语义守卫:小字阶全部并入 body 档(13),图标按钮字形走行内档(12),
        // 防止回退到 10/11/14 多档并存。
        var gitView = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        var styleStart = gitView.IndexOf("x:Key=\"ScmFileNameTextStyle\"", StringComparison.Ordinal);
        var styleEnd = gitView.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        var styleBlock = gitView.Substring(styleStart, styleEnd - styleStart);
        Assert.Contains("{DynamicResource TypeBody}", styleBlock);
        // 分支行“图标+文字”垂直中线对齐抽查
        Assert.Contains("Text=\"{StaticResource CodiconSourceControl}\" FontFamily=\"{StaticResource IconFontFamily}\" FontSize=\"{DynamicResource IconInline}\" Foreground=\"{DynamicResource MutedTextBrush}\" VerticalAlignment=\"Center\"", gitView);

        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var buttonStyleStart = app.IndexOf("<Style TargetType=\"Button\">", StringComparison.Ordinal);
        var buttonStyleEnd = app.IndexOf("</Style>", buttonStyleStart, StringComparison.Ordinal);
        var buttonStyle = app.Substring(buttonStyleStart, buttonStyleEnd - buttonStyleStart);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"{DynamicResource TypeBody}\" />", buttonStyle);

        // 图标按钮字形统一行内档(12)
        var iconButtonStart = app.IndexOf("x:Key=\"IconButton\"", StringComparison.Ordinal);
        var iconButtonEnd = app.IndexOf("</Style>", iconButtonStart, StringComparison.Ordinal);
        var iconButtonStyle = app.Substring(iconButtonStart, iconButtonEnd - iconButtonStart);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"{DynamicResource IconInline}\" />", iconButtonStyle);
    }

    [Fact]
    public void ConsolidatedTokens_AreNotReferencedByAnyXaml()
    {
        // 小字阶(10/11/13/14)与行级小图标(10/13)已并入 TypeBody/IconInline,
        // 旧令牌不得在任何 XAML(含 App.xaml)中复活。
        foreach (var file in AllXamlFiles())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("TypeMicro", text);
            Assert.DoesNotContain("TypeCaption", text);
            Assert.DoesNotContain("TypeLg", text);
            Assert.DoesNotContain("TypeSection", text);
            Assert.DoesNotContain("{DynamicResource IconSm}", text);
            Assert.DoesNotContain("{DynamicResource IconGlyph}", text);
        }
    }

    // ---- references resolve =====

    [Fact]
    public void AllDynamicResourceReferences_ResolveToThemeTokens()
    {
        var themeKeys = ThemeFiles.SelectMany(ReadTokenKeys).ToHashSet();
        // 字体/字号/度量令牌与转换器定义在 App.xaml 主字典(不属于三主题字典),同样是合法的解析目标。
        var appKeys = ReadXKeys(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var files = AllXamlFiles().ToList();
        Assert.NotEmpty(files);

        var unresolved = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in DynamicResourceRegex.Matches(text))
            {
                var key = match.Groups[1].Value;
                if (!themeKeys.Contains(key) && !appKeys.Contains(key)) unresolved.Add($"{Path.GetFileName(file)}: {key}");
            }
        }

        Assert.True(unresolved.Count == 0, "未在任何主题字典或 App.xaml 主字典中定义的 DynamicResource 引用：" + string.Join("; ", unresolved));
    }

    [Fact]
    public void StaticResourceReferencesInViews_ResolveToAppOrLocalResources()
    {
        var appKeys = ReadXKeys(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var unresolved = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views"), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var localKeys = XKeyRegex.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
            foreach (Match match in StaticResourceRegex.Matches(text))
            {
                var key = match.Groups[1].Value;
                if (key.StartsWith("{x:", StringComparison.Ordinal)) continue; // {x:Type Button} 等
                if (!appKeys.Contains(key) && !localKeys.Contains(key))
                    unresolved.Add($"{Path.GetFileName(file)}: {key}");
            }
        }

        Assert.True(unresolved.Count == 0, "无法解析的 StaticResource 引用：" + string.Join("; ", unresolved));
    }

    // ---- no hard-coded colors in business views =====

    [Fact]
    public void Views_ContainNoHardCodedColorLiterals()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views"), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in ColorLiteralRegex.Matches(text))
            {
                var value = match.Groups[0].Value.Trim();
                // 允许透明背景（布局需求，不随主题变化且无对比度影响）与方法的命名色
                if (match.Groups["prop"].Success && match.Groups["name"].Value == "Transparent") continue;
                offenders.Add($"{Path.GetFileName(file)}: {value}");
            }
        }

        Assert.True(offenders.Count == 0, "业务视图中的硬编码颜色：" + string.Join("; ", offenders.Distinct()));
    }

    // ---- layout spec =====

    [Fact]
    public void AppXaml_DefinesLayoutPrimitives()
    {
        var keys = ReadXKeys(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        foreach (var key in RequiredLayoutKeys)
        {
            Assert.True(keys.Contains(key), $"App.xaml 缺少布局/样式资源 {key}");
        }

        foreach (var key in RequiredFontKeys)
        {
            Assert.True(keys.Contains(key), $"App.xaml 缺少字体/字号/度量令牌 {key}");
        }

        foreach (var key in RequiredSpacingKeys)
        {
            Assert.True(keys.Contains(key), $"App.xaml 缺少间距/内边距令牌 {key}");
        }

        foreach (var key in RequiredRadiusKeys)
        {
            Assert.True(keys.Contains(key), $"App.xaml 缺少圆角令牌 {key}");
        }
    }

    [Fact]
    public void AppXaml_DefinesMotionDurations()
    {
        // 动效时长令牌:Storyboard.Duration 的单一来源。DurationInstant(0) 供 MotionService 在系统
        // 关闭"显示动画"时把全部时长置零;视图不得硬编码动画毫秒数。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        foreach (var key in new[] { "DurationInstant", "DurationFast", "DurationNormal", "DurationSlow" })
        {
            Assert.Contains($"x:Key=\"{key}\"", app);
            Assert.Matches($@"<wbase:Duration x:Key=""{key}"">", app);
        }
        // WPF seals templates by freezing their storyboards; a DynamicResource inside the
        // animation would make the desktop shell fail while the first ListBox is materialized.
        Assert.DoesNotContain("Duration=\"{DynamicResource Duration", app);
        Assert.Contains("Duration=\"{StaticResource DurationFast}\"", app);
    }

    [Fact]
    public void AppXaml_DefinesElevationShadows()
    {
        // 高程阴影资源:黑色阴影在 Dark 底上柔和、Light 上明显、HighContrast 黑底自动隐去,
        // 因此是 App 级资源而非主题令牌;视图只能经样式引用,不得直接在视图里放置效果。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        Assert.Matches(@"<DropShadowEffect x:Key=""ShadowCardEffect""", app);
        Assert.Matches(@"<DropShadowEffect x:Key=""ShadowPopupEffect""", app);
        Assert.Matches(@"<DropShadowEffect x:Key=""ShadowOverlayEffect""", app);
        Assert.Contains("{StaticResource ShadowCardEffect}", app);
        Assert.Contains("{StaticResource ShadowPopupEffect}", app);
    }

    [Fact]
    public void PopupStyles_ApplyElevationShadowsAndCardTextStaysCrisp()
    {
        // 弹出层(菜单/下拉/子菜单)与工具提示消费阴影令牌(卡片档 / Popup 档),防止令牌空转。
        // 卡片样式与列表行按钮不再挂 DropShadowEffect:Effect 会把承载元素的整棵子树
        // (文字/数字)栅格化再合成,导致任务中心指标卡与设置页卡片文字发糊;卡片层次由边框线承担。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        Assert.DoesNotContain("ShadowCardEffect", StyleBlock(app, "x:Key=\"CardStyle\""));
        Assert.DoesNotContain("ShadowCardEffect", StyleBlock(app, "x:Key=\"ListItemButtonStyle\""));
        Assert.Contains("Value=\"{StaticResource ShadowCardEffect}\"", StyleBlock(app, "<Style TargetType=\"ToolTip\">"));
        Assert.Contains("Effect=\"{StaticResource ShadowPopupEffect}\"", StyleBlock(app, "<Style TargetType=\"ContextMenu\">"));
        Assert.Contains("Effect=\"{StaticResource ShadowPopupEffect}\"", StyleBlock(app, "<Border x:Name=\"SubmenuBorder\""));
        Assert.Contains("Effect=\"{StaticResource ShadowPopupEffect}\"", StyleBlock(app, "x:Name=\"DropDownBorder\""));
    }

    [Fact]
    public void WindowControlButtons_UseIconFontFamilyGlyphs()
    {
        // 窗口控制按钮(最小化/最大化/关闭)必须用 Codicon 字形,经 IconFontFamily 令牌(不得
        // 硬编码字体族),不再回退到 ─ □ × 文本字符及其逐字形的光学 Padding 补偿。
        var main = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml"));
        Assert.Contains("Text=\"{StaticResource CodiconChromeMinimize}\"", main); // minimize
        Assert.Contains("Text=\"{StaticResource CodiconChromeMaximize}\"", main); // maximize
        Assert.Contains("Text=\"{StaticResource CodiconChromeClose}\"", main); // close
        Assert.DoesNotContain("Content=\"─\"", main);
        Assert.DoesNotContain("Content=\"□\"", main);
        Assert.DoesNotContain("Content=\"×\"", main);
        Assert.Contains("Style=\"{StaticResource WindowControlButtonStyle}\"", main);
        Assert.Contains("Style=\"{StaticResource CloseWindowButton}\"", main);

        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var style = StyleBlock(app, "x:Key=\"WindowControlButtonStyle\"");
        Assert.Contains("Value=\"{StaticResource IconFontFamily}\"", style);
        Assert.Contains("Value=\"{DynamicResource IconClose}\"", style);
        Assert.Contains("Value=\"Center\"", style); // button itself and its content share the title-bar center line
        Assert.Contains("VerticalContentAlignment", style);
        Assert.Contains("Grid.Column=\"3\" Orientation=\"Horizontal\" VerticalAlignment=\"Center\"", main);
        Assert.Contains("FontSize=\"{DynamicResource IconClose}\"", main);
    }

    [Fact]
    public void CodiconFont_IsEmbeddedAndOldMdl2GlyphsAreGone()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var csproj = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Nornia.Desktop.csproj"));
        var fontPath = Path.Combine(RepoRoot, "src/Nornia.Desktop/Assets/codicon.ttf");

        Assert.True(File.Exists(fontPath));
        Assert.True(new FileInfo(fontPath).Length > 100_000);
        Assert.Contains("<icons:CodiconFontFamilyExtension x:Key=\"IconFontFamily\" />", app);
        Assert.Contains("./Assets/#codicon", File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/CodiconFontFamilyExtension.cs")));
        Assert.Contains("Assets\\codicon.ttf", csproj);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src/Nornia.Desktop"), "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                    || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Segoe MDL2 Assets", text);
            if (!file.EndsWith("Codicons.cs", StringComparison.OrdinalIgnoreCase))
            {
                Assert.DoesNotMatch(new Regex(@"(?:\\u|&#x)E[0-9A-Fa-f]{3}", RegexOptions.Compiled), text);
            }
        }
    }

    [Fact]
    public void DesktopProject_DeclaresApplicationAndWindowIcon()
    {
        // exe 图标(ApplicationIcon)与窗口图标(Window.Icon)共用同一品牌瓦片,且资产确实存在。
        var csproj = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Nornia.Desktop.csproj"));
        Assert.Contains("<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>", csproj);
        Assert.Contains("<NorniaAppIcon>Assets\\app.ico</NorniaAppIcon>", csproj);
        Assert.Contains("<ApplicationIcon>$(NorniaAppIcon)</ApplicationIcon>", csproj);
        Assert.Contains("<Resource Include=\"$(NorniaAppIcon)\" />", csproj);
        var main = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml"));
        Assert.Contains("Icon=\"/Nornia.Desktop;component/Assets/app.ico\"", main);
        Assert.Contains("Source=\"/Nornia.Desktop;component/Assets/app.ico\"", main);
        Assert.True(File.Exists(Path.Combine(RepoRoot, "src/Nornia.Desktop/Assets/app.ico")), "缺少应用图标资产 app.ico");
    }

    [Fact]
    public void LightTheme_StatusBarIsNeutralSurface()
    {
        // Light 状态栏必须为中性浅面(与 Dark 深条的视觉权重一致),不能再用强调色蓝条;
        // 错误/警告/成功反馈色也已按浅底重调。
        var light = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Themes/Light.xaml"));
        string HexOf(string key) => Regex.Match(light, $@"x:Key=""{key}"" Color=""#(?<hex>[0-9A-Fa-f]{{6}})""").Groups["hex"].Value;

        var bg = HexOf("StatusBarBackgroundBrush");
        Assert.NotEqual("0066B8", bg);
        Assert.NotEqual(HexOf("AccentBrush"), bg);

        var bgLum = Luminance(ParseHexColor(bg));
        var fgLum = Luminance(ParseHexColor(HexOf("StatusBarForegroundBrush")));
        Assert.True(fgLum < bgLum, "Light 状态栏前景应深于背景(中性浅面)");
    }

    [Fact]
    public void NewTypeStyles_AreConsumedByRepresentativeViews()
    {
        // 中间档字阶样式必须被代表视图消费,防止令牌空转。
        Assert.Contains("SubtitleTextStyle", File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/SettingsView.xaml")));
        Assert.Contains("SectionTitleStyle", File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EmptyStateControl.xaml")));
        Assert.Contains("TypeHeading", File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DashboardView.xaml")));
    }

    [Fact]
    public void WorkbenchAndBusinessViews_UseSemanticSpacingTokens()
    {
        // 令牌不只定义在 App.xaml：共享控件、顶层页与嵌入页都要使用它们，避免同一视觉角色
        // 随着页面增加重新散落为 4/8/12/16 的字面量。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        Assert.Contains("{StaticResource InsetToolbar}", app);
        Assert.Contains("{StaticResource InsetCard}", app);
        Assert.Contains("{StaticResource InsetStatusItem}", app);

        var expected = new Dictionary<string, string[]>
        {
            // 操作日志删除后 dashboard 为单列待办布局,不再使用 InsetCompactRow(操作行间距令牌)。
            ["DashboardView.xaml"] = ["{StaticResource MarginBanner}", "{StaticResource MarginCard}"],
            ["SettingsView.xaml"] = ["{StaticResource InsetPage}", "{StaticResource GapLeftMd}"],
            ["PackagesView.xaml"] = ["{StaticResource InsetCard}", "{StaticResource GapVerticalSm}"],
            ["ProjectsView.xaml"] = ["{StaticResource InsetPage}", "{StaticResource MarginFieldRow}"],
            ["TerminalView.xaml"] = ["{StaticResource InsetToolbar}", "{StaticResource GapLeftSm}"],
            ["EmptyStateControl.xaml"] = ["{StaticResource EmptyStateTopInset}", "{StaticResource GapMd}"],
        };

        foreach (var (fileName, tokens) in expected)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views", fileName));
            foreach (var token in tokens)
            {
                Assert.Contains(token, text);
            }
        }
    }

    [Fact]
    public void ShellHeights_RideScalableMeasurementTokens()
    {
        // 关键壳层(标题栏/侧栏标题/标签栏/状态栏/活动栏/分节头/表格行)的高度必须引用度量令牌,
        // 随全局字号倍率(DynamicResource + UiFontService)同步缩放;硬编码高度会使 85%/130%
        // 档位下文字与行高脱节(裁切或双重留白)。
        var main = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml"));
        // 标题栏:Auto 行 + 栏身 Height=SizeTitleBar 令牌(DynamicResource 的 double 无法赋给
        // GridLength 类型的 RowDefinition.Height,令牌必须落在 double 属性上)
        Assert.Contains("Height=\"{DynamicResource SizeTitleBar}\"", main);
        Assert.Contains("<RowDefinition Height=\"Auto\" />", main);
        Assert.Contains("<Grid Grid.Row=\"0\" Height=\"{DynamicResource SizeTabBar}\"", main);
        Assert.Contains("MinHeight=\"{DynamicResource SizeStatusBar}\"", main);
        // 活动栏项(模板 + 容器样式)都随 SizeActivity(48px — VS Code 默认)缩放,不得复活硬编码 48;
        // 尺寸落在 ActivityItemStyle 的 Width/Height 令牌 Setter 上(固定正方形,随倍率缩放)。
        Assert.Contains("<Setter Property=\"Width\" Value=\"{DynamicResource SizeActivity}\"", main);
        Assert.Contains("<Setter Property=\"Height\" Value=\"{DynamicResource SizeActivity}\"", main);
        var activityItem = StyleBlock(main, "x:Key=\"ActivityItemStyle\"");
        Assert.DoesNotContain("Value=\"48\"", activityItem);
        Assert.DoesNotContain("Value=\"35\"", activityItem);

        var group = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EditorGroupView.xaml"));
        Assert.Contains("<Grid Height=\"{DynamicResource SizeTabBar}\">", group);

        var collapsible = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/CollapsibleSection.xaml"));
        Assert.Contains("Property=\"MinHeight\" Value=\"{DynamicResource SizeRowSm}\"", collapsible);

        // 侧栏标题行 35px 与数据表格紧凑行 22px 走 App.xaml 令牌
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var sidebarTitle = StyleBlock(app, "x:Key=\"SideBarTitleBarStyle\"");
        Assert.Contains("Property=\"Height\" Value=\"{DynamicResource SizeTitleBar}\"", sidebarTitle);
        Assert.Contains("Property=\"RowHeight\" Value=\"{DynamicResource SizeTableRow}\"", app);
        Assert.Contains("Property=\"MinRowHeight\" Value=\"{DynamicResource SizeTableRow}\"", app);
        Assert.Contains("Property=\"ColumnHeaderHeight\" Value=\"{DynamicResource SizeTableRow}\"", app);
    }

    [Fact]
    public void QuickInputOverlay_CoversTheWholeShell()
    {
        // 命令面板/快速打开的全窗口遮罩必须覆盖整个壳层:标题栏与状态栏也要压暗(VS Code 行为)。
        // 结构约束(防回归):遮罩层与承载面板都是根 Grid 的最后子元素(RowSpan=3、状态栏之后),
        // 承载面板声明在遮罩之后(Z 序:后者在上,调色板才可见)。
        var main = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml"));
        var backdrop = main.IndexOf("Grid.RowSpan=\"3\" Background=\"{DynamicResource QuickInputBackdropBrush}\"", StringComparison.Ordinal);
        var overlay = main.IndexOf("Grid.RowSpan=\"3\" x:Name=\"QuickInputOverlay\"", StringComparison.Ordinal);
        Assert.True(backdrop >= 0, "缺少全窗口遮罩层");
        Assert.True(overlay > backdrop, "承载面板必须声明在遮罩之后(否则调色板被遮罩盖住)");
        var statusBar = main.LastIndexOf("Background=\"{DynamicResource StatusBarBackgroundBrush}\"", StringComparison.Ordinal);
        Assert.True(statusBar >= 0 && backdrop > statusBar, "遮罩必须声明在状态栏之后(否则状态栏区域不压暗)");
        Assert.Contains("MouseLeftButtonDown=\"QuickInputBackdrop_MouseLeftButtonDown\"", main);
    }

    [Fact]
    public void UiFontFamily_KeepsWindowsChineseFallbackChain()
    {
        // Windows 中文环境后备链:Segoe WPC(VS Code 微软雅黑锯齿修复)→ Segoe UI → Microsoft YaHei;
        // 代码字体保持独立的 MonoFontFamily 策略,不并入界面后备链。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        Assert.Contains("<FontFamily x:Key=\"UiFontFamily\">Segoe WPC, Segoe UI, Microsoft YaHei</FontFamily>", app);
        Assert.Contains("<FontFamily x:Key=\"MonoFontFamily\">Cascadia Code", app);
    }

    [Fact]
    public void Dashboard_MetricCardsReflowInsteadOfFixedColumns()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DashboardView.xaml"));
        Assert.DoesNotContain("<UniformGrid", text);
        Assert.Contains("WrapPanel", text);
    }

    [Fact]
    public void DataPages_UseWrapCapableToolbarActions()
    {
        foreach (var name in new[] { "RuntimeView", "ToolsView", "PackagesView" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot, $"src/Nornia.Desktop/Views/{name}.xaml"));
            Assert.Contains("ToolbarActionsStyle", text);
        }
    }

    [Fact]
    public void DataPages_ExposeBottomSelectionSummary()
    {
        var runtime = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/RuntimeView.xaml"));
        var tools = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/ToolsView.xaml"));
        Assert.Contains("SelectedRuntimeSummary", runtime);
        Assert.Contains("SelectedToolSummary", tools);
    }

    // ---- interaction states (VS Code: hover / pressed / focus / disabled) =====

    [Fact]
    public void GlobalControlStyles_DefineHoverPressedFocusAndDisabledStates()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        // hover
        Assert.Contains("IsMouseOver", app);
        // pressed (buttons) + pressed (rows, immediate mouse-down feedback)
        Assert.Contains("IsPressed", app);
        Assert.Contains("IsMouseCaptured", app);
        // keyboard focus: shared ring + input focus border
        Assert.Contains("FocusVisualStyle", app);
        Assert.Contains("IsKeyboardFocused", app);
        Assert.Contains("IsKeyboardFocusWithin", app);
        // disabled: reduced contrast + no hand cursor
        Assert.Contains("IsEnabled", app);
        Assert.Contains("Opacity", app);
        Assert.Contains("Arrow", app);
        // the shared styles reference the new interaction tokens
        Assert.Contains("PressedBrush", app);
        Assert.Contains("PressedBorderBrush", app);
        Assert.Contains("ButtonPressedBrush", app);
        Assert.Contains("DangerPressedBrush", app);
        Assert.Contains("StatusBarItemPressedBackgroundBrush", app);
    }

    [Fact]
    public void ImplicitButtonStyle_DefinesHoverPressedFocusAndDisabled()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var block = StyleBlock(app, "<Style TargetType=\"Button\">");
        Assert.Contains("IsMouseOver", block);
        Assert.Contains("IsPressed", block);
        Assert.Contains("FocusVisualStyle", block);
        Assert.Contains("IsEnabled", block);
        Assert.Contains("Opacity", block);
        Assert.Contains("Arrow", block);
    }

    [Fact]
    public void MenuAndDropdownStyles_ThemedWithoutWhiteSurfaces()
    {
        // 深色模式下右键/下拉弹出层白底的根因是默认模板钉死 SystemColors,修复靠显式模板:
        // 弹出表面/图标栏/分隔线全部走主题令牌,且不得再引入 SystemColors 造成白底回归。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        Assert.DoesNotContain("SystemColors", app);

        var contextMenu = StyleBlock(app, "<Style TargetType=\"ContextMenu\">");
        Assert.Contains("MenuBackgroundBrush", contextMenu);
        Assert.Contains("StrongBorderBrush", contextMenu);
        Assert.Contains("<ControlTemplate TargetType=\"ContextMenu\">", contextMenu);

        var menuItem = StyleBlock(app, "<Style TargetType=\"MenuItem\">");
        Assert.Contains("MenuBackgroundBrush", menuItem);
        Assert.Contains("MenuSelectionBackgroundBrush", menuItem);
        Assert.Contains("IsHighlighted", menuItem);
        Assert.Contains("IsEnabled", menuItem);
        Assert.Contains("MutedTextBrush", menuItem); // 禁用项降对比度走暗文本令牌
        Assert.Contains("SubmenuHeader", menuItem);
        Assert.Contains("SubmenuItem", menuItem);
        Assert.Contains("PART_Popup", menuItem);

        var separator = StyleBlock(app, "<Style TargetType=\"Separator\">");
        Assert.Contains("BorderBrush", separator);
        Assert.Contains("<ControlTemplate TargetType=\"Separator\">", separator);

        var comboBox = StyleBlock(app, "<Style TargetType=\"ComboBox\">");
        Assert.Contains("MenuBackgroundBrush", comboBox);
        Assert.Contains("PART_Popup", comboBox);
        Assert.Contains("IsMouseOver", comboBox);
        Assert.Contains("IsEnabled", comboBox);
        Assert.Contains("Opacity", comboBox);
        // The closed selector forwards the ComboBox brushes through the outer template into its
        // ToggleButton; relying on an AncestorType lookup from a nested template is not stable
        // after WPF materializes the control in a DataTemplate.
        Assert.Contains("Background=\"{TemplateBinding Background}\"", comboBox);
        Assert.Contains("BorderBrush=\"{TemplateBinding BorderBrush}\"", comboBox);
        Assert.Contains("TextElement.Foreground=\"{TemplateBinding Foreground}\"", comboBox);

        var comboBoxItem = StyleBlock(app, "<Style TargetType=\"ComboBoxItem\">");
        Assert.Contains("IsHighlighted", comboBoxItem);
        Assert.Contains("SelectionActiveBrush", comboBoxItem);
        Assert.Contains("IsEnabled", comboBoxItem);
        Assert.Contains("<ControlTemplate TargetType=\"ComboBoxItem\">", comboBoxItem);
    }

    [Fact]
    public void SettingsBooleanEditor_UsesThemedCheckBoxStyle()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var settings = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/SettingsView.xaml"));
        var checkBox = StyleBlock(app, "<Style TargetType=\"CheckBox\">");

        Assert.Contains("Background=\"{DynamicResource InputBrush}\"", checkBox);
        Assert.Contains("BorderBrush=\"{TemplateBinding BorderBrush}\"", checkBox);
        Assert.Contains("TextElement.Foreground=\"{TemplateBinding Foreground}\"", checkBox);
        Assert.Contains("Style=\"{StaticResource {x:Type CheckBox}}\" Content=\"启用\"", settings);
    }

    [Fact]
    public void FileIcons_RenderThemedMonogramBadges()
    {
        // 文件类型图标统一为“透明底 + 语言色相的 Monogram 缩写”:共享 FileTypeMonogramTextStyle
        // (App.xaml) 提供等宽居中槽位与 MonoFontFamily;字号走 IconMonogram 令牌(随全局
        // 字号倍率缩放),不硬编码字号字面量;文字颜色经转换器走既有 FileTypeIcon*Brush(不新增令牌)。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var monogramStyle = StyleBlock(app, "x:Key=\"FileTypeMonogramTextStyle\"");
        Assert.Contains("MonoFontFamily", monogramStyle);
        Assert.Contains("IconMonogram", monogramStyle); // 缩写字号走令牌
        Assert.Contains("Property=\"Width\"", monogramStyle); // 图标槽位等宽固定(不随缩写长度变化)
        Assert.Contains("TextAlignment", monogramStyle); // 文字居中 —— 同列表内各文件图标等宽、后续文件名纵向对齐

        var sidebarBadge = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/SidebarFileTypeBadge.xaml"));
        Assert.Contains("Width=\"{DynamicResource IconMonogramSlot}\"", sidebarBadge);
        Assert.Contains("Height=\"{DynamicResource IconMonogramCell}\"", sidebarBadge);
        Assert.Contains("ClipToBounds=\"False\"", sidebarBadge); // 共享徽标不得把缩写裁在侧栏图标槽位边缘
        Assert.DoesNotContain("Margin=\"0,0,4,0\"", sidebarBadge); // 20px 行级图标槽位容不下 18px 图标再加 4px 外边距

        // 编辑区标签徽标直接消费共享样式，侧栏通过共享文件类型徽标间接消费。
        // (FilePreviewView 面包屑行的文件类型 Monogram 已移除:类型改由标签徽标 +
        //  主状态栏"当前文档状态"段承载,见 FileIcons_BreadcrumbBarNoLongerShowsFileType。)
        var groupView = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EditorGroupView.xaml"));
        Assert.Contains("FileTypeMonogramTextStyle", groupView);

        var git = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        Assert.Contains("SidebarFileTypeBadge", git);
        // 不再使用彩色底块:语言色相只用于文字前景,不得回退到 Background 底色。
        Assert.DoesNotContain("Background=\"{Binding Path, Converter={StaticResource PathToIconBrushConverter}}\"", git);
        Assert.DoesNotContain("Background=\"{Binding Change.Path, Converter={StaticResource PathToIconBrushConverter}}\"", git);
        var workspace = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/WorkspaceView.xaml"));
        Assert.Contains("SidebarFileTypeBadge", workspace);
        Assert.DoesNotContain("Background=\"{Binding Path, Converter={StaticResource PathToIconBrushConverter}}\"", workspace);
        var preview = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml"));
        Assert.DoesNotContain("{Binding FileType.IconMonogram}", preview); // 面包屑行文件类型 Monogram 已移除
        Assert.DoesNotContain("FileTypeToBrushConverter", preview);
        Assert.DoesNotContain("Background=\"{Binding FileType, Converter={StaticResource FileTypeToBrushConverter}}\"", preview);
        var groupTab = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EditorGroupView.xaml"));
        Assert.Contains("TabBadge", groupTab);
        Assert.Contains("FileTypeToBrushConverter", groupTab);
        Assert.DoesNotContain("Background=\"{Binding FileType, Converter={StaticResource FileTypeToBrushConverter}}\"", groupTab);
    }

    [Fact]
    public void FileIcons_BreadcrumbBarNoLongerShowsFileType()
    {
        // 面包屑行只显示路径 + 符号链 + 编辑器操作:文件类型 Monogram 与语言名均已移除,
        // 文件类型由编辑区标签徽标与主状态栏"当前文档状态"段(可点击语言项)承载。
        var preview = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml"));
        Assert.DoesNotContain("FileTypeMonogramTextStyle", preview);
        Assert.DoesNotContain("IconMonogramSlot", preview);
        Assert.DoesNotContain("{Binding LanguageName}", preview);
        // 面包屑分段链与编辑器操作仍在。
        Assert.Contains("x:Name=\"BreadcrumbBar\"", preview);
        Assert.Contains("BreadcrumbSegments", preview);
    }

    [Fact]
    public void PrimaryDangerAndCloseButtons_DefinePressedStates()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var primary = StyleBlock(app, "x:Key=\"PrimaryButton\"");
        Assert.Contains("ButtonPressedBrush", primary);
        var danger = StyleBlock(app, "x:Key=\"DangerButton\"");
        Assert.Contains("DangerPressedBrush", danger);
        // close keeps the danger feedback (hover + pressed both red)
        var close = StyleBlock(app, "x:Key=\"CloseWindowButton\"");
        Assert.Contains("CloseHoverBrush", close);
        Assert.Contains("DangerPressedBrush", close);
    }

    [Fact]
    public void StatusBarItemTemplate_HasPressedAndKeyboardFocusStates()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var block = StyleBlock(app, "x:Key=\"StatusBarItem\"");
        Assert.Contains("IsMouseOver", block);
        Assert.Contains("StatusBarItemPressedBackgroundBrush", block);
        Assert.Contains("IsKeyboardFocused", block);
        Assert.Contains("FocusRing", block);
    }

    [Fact]
    public void DocumentStatus_MergedIntoMainWindowStatusBar()
    {
        // 视图内独立底栏已移除:文档状态(位置/只读/语言/Diff 模式/编码/行尾/行数/大小/
        // 自动换行/diff 行数)并入窗口主状态栏的"当前文档状态"段,只在选中文档类标签时显示。
        var main = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml"));
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var tabModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/EditorAreaViewModel.cs"));
        var preview = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml"));
        var previewCode = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml.cs"));
        var diffView = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml"));
        var diffCode = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml.cs"));

        // 主状态栏消费选中标签的文档状态(位置文本由标签承载,语言命令走 EditorAreaViewModel)。
        Assert.Contains("Workbench.SelectedEditorTab.EditorTab.CaretPositionText", main);
        Assert.Contains("DocumentTabVisibilityConverter", main);
        Assert.Contains("Workbench.Editor.OpenLanguagePickerCommand", main);
        Assert.Contains("DiffLines.Count, Mode=OneWay", main);
        Assert.Contains("x:Key=\"DocumentTabVisibilityConverter\"", app);
        // 位置文本落到标签 VM(FilePreviewTab 与 DiffTab 各自一份)。
        Assert.Equal(2, Regex.Matches(tabModel, "private string caretPositionText = string.Empty;").Count);

        // 视图内不再保留独立底栏:无本地位置文本块,code-behind 不写视图内 TextBlock。
        Assert.DoesNotContain("Read-only status", preview);
        Assert.DoesNotContain("x:Name=\"PositionText\"", preview);
        Assert.DoesNotContain("PositionText.Text", previewCode);
        Assert.DoesNotContain("Read-only status", diffView);
        Assert.DoesNotContain("x:Name=\"DiffPositionText\"", diffView);
        Assert.DoesNotContain("DiffPositionText", diffCode);
        // 顶部工具栏的自动换行按钮保留(守卫 CodeWorkbench_FindAndNavigationChromeIsWired 依赖)。
        Assert.Contains("ToggleWordWrapCommand", preview);
    }

    [Fact]
    public void ActivityBarTemplate_HasPressedAndFocusStates()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml"));
        var block = StyleBlock(text, "x:Key=\"ActivityItemStyle\"");
        Assert.Contains("IsMouseOver", block);
        Assert.Contains("IsSelected", block);
        Assert.Contains("IsMouseCaptured", block);
        Assert.Contains("FocusVisualStyle", block);
    }

    [Fact]
    public void WorkspaceTreeTemplate_HasRowAndExpanderInteractionStates()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/WorkspaceView.xaml"));
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var tree = StyleBlock(app, "x:Key=\"SidebarRowItemStyle\"");
        Assert.Contains("MinHeight", tree);
        Assert.Contains("HorizontalContentAlignment", tree);
        Assert.Contains("SidebarRowChrome", text);
        Assert.Contains("WorkspaceFolderRowTemplate", text);
        Assert.Contains("SidebarTreeGuideLayer", text);
        Assert.DoesNotContain("WorkspaceGuideTemplate", text);
    }

    [Fact]
    public void CollapsibleSectionTemplate_HasPressedAndFocusStates()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/CollapsibleSection.xaml"));
        var block = StyleBlock(text, "x:Key=\"CollapsibleHeaderButtonStyle\"");
        Assert.Contains("IsMouseOver", block);
        Assert.Contains("IsPressed", block);
        Assert.Contains("PressedBrush", block);
        Assert.Contains("FocusVisualStyle", block);
    }

    [Fact]
    public void SidebarCountBadges_UseBlueSurfaceAndWhiteText()
    {
        var section = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/CollapsibleSection.xaml"));
        var search = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/SearchSidebarView.xaml"));

        foreach (var text in new[] { section, search })
        {
            Assert.Contains("SidebarCountBadgeStyle", text);
            Assert.Contains("SidebarCountBadgeTextStyle", text);
        }
        Assert.Contains("Grid.Column=\"2\"", section);
        Assert.Contains("HorizontalAlignment=\"Right\"", section);
        var git = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        Assert.Contains("SidebarReferenceBadgeStyle", git);
        Assert.Contains("ScmRefBadgeTemplate", git);
        Assert.Contains("分支/远端分支/标签", git);
        Assert.Contains("StackPanel Orientation=\"Horizontal\" VerticalAlignment=\"Center\"", git);
        Assert.DoesNotContain("Title=\"更改\" BadgeCount=", git);
        Assert.DoesNotContain("Title=\"图表\" BadgeCount=", git);
        Assert.DoesNotContain("Title=\"分支\" BadgeCount=", git);
        Assert.DoesNotContain("Title=\"最近提交\" BadgeCount=", git);
        Assert.Contains("Title=\"已暂存的更改\" BadgeCount=\"{Binding StagedChanges.Count}\" AlwaysShowBadge=\"True\"", git);
        Assert.Contains("Title=\"未暂存的更改\" BadgeCount=\"{Binding UnstagedChanges.Count}\" AlwaysShowBadge=\"True\"", git);

        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var countStyle = StyleBlock(app, "x:Key=\"SidebarCountBadgeStyle\"");
        Assert.Contains("SizeSidebarBadge", countStyle);
        Assert.Contains("RadiusPill", countStyle);
        var countTextStyle = StyleBlock(app, "x:Key=\"SidebarCountBadgeTextStyle\"");
        Assert.Contains("TypeBadge", countTextStyle);
        Assert.Contains("Property=\"FontWeight\" Value=\"Normal\"", countTextStyle);
        var referenceStyle = StyleBlock(app, "x:Key=\"SidebarReferenceBadgeStyle\"");
        Assert.Contains("RadiusLg", referenceStyle);
        Assert.Contains("SizeSidebarBadge", referenceStyle);
        var sectionControl = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/CollapsibleSection.xaml"));
        Assert.Contains("BadgeCountVisibilityConverter", sectionControl);
        Assert.Contains("AlwaysShowBadge", sectionControl);
    }

    [Fact]
    public void CommitHover_UsesContentWidthUpToFiveHundredFiftyWithoutHeightCap()
    {
        var git = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        var start = git.IndexOf("<ToolTip Style=\"{StaticResource ScmCommitToolTipStyle}\">", StringComparison.Ordinal);
        var end = git.IndexOf("<!-- 变更统计", start, StringComparison.Ordinal);
        var hover = git[start..end];

        Assert.Contains("<StackPanel MaxWidth=\"550\" HorizontalAlignment=\"Left\">", hover);
        Assert.Contains("MaxWidth=\"550\" HorizontalAlignment=\"Left\"", hover);
        Assert.DoesNotContain("MinWidth=", hover);
        Assert.DoesNotContain("MaxHeight=", hover);
    }

    [Fact]
    public void GitGraph_UsesTopBranchSelectorInsteadOfBranchCollapsibleSection()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml.cs"));

        Assert.Contains("Click=\"CurrentBranchSelector_Click\"", view);
        Assert.Contains("ItemsSource=\"{Binding Branches}\"", view);
        Assert.Contains("SwitchBranchCommand", view);
        Assert.DoesNotContain("Title=\"分支\"", view);
        Assert.DoesNotContain("IsBranchesSectionExpanded", view);
        Assert.Contains("menu.Placement = PlacementMode.Bottom", code);
    }

    [Fact]
    public void GitChangeRowInlineActions_OpenFilesInFlatAndTreeLayouts()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));

        Assert.Equal(2, CountOccurrences(view, "ToolTip=\"打开文件\""));
        Assert.Equal(2, CountOccurrences(view, "Content=\"{StaticResource CodiconGoToFile}\""));
        Assert.Equal(2, CountOccurrences(view,
            "Command=\"{Binding DataContext.OpenFilePreviewCommand, RelativeSource={RelativeSource AncestorType=views:GitView}}\""));
        Assert.DoesNotContain("ToolTip=\"在资源管理器中显示\"", view);
    }

    [Fact]
    public void GitFlatChangeRows_PlaceDirectoryImmediatelyAfterFileName()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        var start = view.IndexOf("<TextBlock x:Name=\"FlatNameAndPath\"", StringComparison.Ordinal);
        var end = view.IndexOf("</TextBlock>", start, StringComparison.Ordinal);
        var flatText = view[start..end];

        var name = flatText.IndexOf("ConverterParameter=Name", StringComparison.Ordinal);
        var directory = flatText.IndexOf("ConverterParameter=Directory", StringComparison.Ordinal);
        Assert.True(name >= 0 && directory > name);
        Assert.Contains("BasedOn=\"{StaticResource ScmFileNameTextStyle}\"", flatText);
        Assert.Contains("Foreground=\"{DynamicResource MutedTextBrush}\"", flatText);
        Assert.Equal(4, Regex.Matches(flatText, "<Run Text=\"\\{Binding Mode=OneWay,").Count);
    }

    [Fact]
    public void GitFlatChangeRows_ReopenDiffWhenTheSelectedRowIsClickedAgain()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml.cs"));

        Assert.Equal(2, CountOccurrences(view, "PreviewMouseLeftButtonDown=\"FlatChangeList_PreviewMouseLeftButtonDown\""));
        Assert.Equal(2, CountOccurrences(view, "PreviewMouseLeftButtonUp=\"FlatChangeList_PreviewMouseLeftButtonUp\""));
        Assert.Contains("_flatChangeReopenCandidate", code);
        Assert.Contains("OpenChangeDiffCommand.Execute(candidate)", code);

        var templateStart = view.IndexOf("<DataTemplate x:Key=\"ScmFileRowTemplate\">", StringComparison.Ordinal);
        var bindingsStart = view.IndexOf("<Grid.InputBindings>", templateStart, StringComparison.Ordinal);
        var bindingsEnd = view.IndexOf("</Grid.InputBindings>", bindingsStart, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenLogFileDiffCommand", view[bindingsStart..bindingsEnd]);
    }

    [Fact]
    public void SearchRows_UseSingleLineLocationColumnAndPathToolTip()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/SearchSidebarView.xaml"));
        var model = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/SearchViewModel.cs"));

        Assert.Contains("MinHeight=\"{DynamicResource SizeRowSm}\"", view);
        Assert.Contains("<ColumnDefinition Width=\"56\" />", view);
        Assert.Contains("Text=\"{Binding LocationLabel}\" TextAlignment=\"Left\"", view);
        Assert.Contains("Text=\"{Binding LocationLabel}\"", view);
        Assert.Contains("FontFamily=\"{StaticResource MonoFontFamily}\"", view);
        Assert.Contains("TextWrapping=\"NoWrap\"", view);
        Assert.Contains("ToolTip=\"{Binding ToolTipText}\"", view);
        Assert.Contains("PreviewMouseLeftButtonUp=\"SearchRows_PreviewMouseLeftButtonUp\"", view);
        Assert.DoesNotContain("SidebarRowChrome.InputBindings", view);
        Assert.Contains("SidebarFileTypeBadge", view);
        Assert.Contains("SidebarTreeGuideLayer", view);
        Assert.Contains("AncestorGuideLefts", view);
        Assert.Contains("<Grid MinWidth=\"0\" MinHeight=\"{DynamicResource SizeRowSm}\" Margin=\"0,0,8,0\" ToolTip=\"{Binding ToolTipText}\">", view);
        Assert.DoesNotContain("{Binding Detail}", view);
        // 引导线层改为纯代码元素(单元素 OnRender),TreeGuideBrush 令牌断言移到代码文件。
        Assert.Contains("TreeGuideBrush", File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/SidebarTreeGuideLayer.xaml.cs")));
        Assert.Contains("public string LocationLabel =>", model);
        Assert.Contains("$\"{Match.Line}:{Match.Column}\"", model);
        Assert.Contains("public string ToolTipText =>", model);
        Assert.DoesNotContain("{RelativePath} · 第 {Match.Line} 行", model);
    }

    [Fact]
    public void WorkbenchTabTemplate_HasSelectedPressedFocusAndDangerClose()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EditorGroupView.xaml"));
        var tab = StyleBlock(text, "x:Key=\"GroupTabItemStyle\"");
        Assert.Contains("IsMouseOver", tab);
        Assert.Contains("IsSelected", tab);
        Assert.Contains("IsMouseCaptured", tab);
        Assert.Contains("FocusVisualStyle", tab);
        Assert.Contains("TabActiveBorderTopBrush", tab);
        var close = StyleBlock(text, "x:Key=\"GroupTabCloseButtonStyle\"");
        Assert.Contains("CloseHoverBrush", close);
        Assert.Contains("DangerPressedBrush", close);
    }

    [Fact]
    public void UnifiedTabTemplate_SlantsPreviewTabTitles()
    {
        // 回归:统一标签条重构时预览标签的斜体渲染丢失(只剩 ViewModel 里的 IsPreview 镜像)。
        // 预览标签标题必须轻微倾斜(VS Code 预览标签语义,数据源为 EditorWorkbenchTab.IsPreview);
        // 用 SkewTransform 实现——字体真斜体斜度太陡,视觉上不可接受。
        var text = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EditorGroupsView.xaml"));
        var tab = StyleBlock(text, "x:Key=\"UnifiedTabItemStyle\"");
        Assert.Contains("Binding IsPreview", tab);
        Assert.Contains("SkewTransform", tab);
        Assert.Contains("AngleX", tab);
    }

    [Fact]
    public void TerminalPanel_HasSurfaceSessionSidebarAndProfileSelector_NoSendBox()
    {
        var terminal = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/TerminalView.xaml"));
        // 单一交互式终端表面:唯一的命令输入入口,绑定当前会话。
        Assert.Contains("controls:TerminalSurfaceControl", terminal);
        Assert.Contains("Session=\"{Binding SelectedSession.Session}\"", terminal);
        // 会话侧栏:列出全部会话,选中项使用 VS Code 风格高亮。
        Assert.Contains("ItemsSource=\"{Binding Sessions}\"", terminal);
        var session = StyleBlock(terminal, "x:Key=\"SessionItemStyle\"");
        Assert.Contains("IsMouseOver", session);
        Assert.Contains("IsSelected", session);
        Assert.Contains("IsMouseCaptured", session);
        Assert.Contains("SelectionActiveBrush", session);
        // 空状态引导与状态展示。
        Assert.Contains("{Binding HasSessions", terminal);
        Assert.Contains("{Binding StatusText}", terminal);
        // 不再有命令发送框与工作目录/自定义 Shell 输入。
        Assert.DoesNotContain("SendCommand", terminal);
        Assert.DoesNotContain("CommandText", terminal);
        Assert.DoesNotContain("TerminalInput", terminal);
        Assert.DoesNotContain("WorkingDirectory", terminal);
        Assert.DoesNotContain("CustomShellPath", terminal);

        var mainWindow = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml"));
        // 终端专属工具在面板标题栏右侧:新建终端 / 配置文件下拉 / 清屏 / 关闭当前终端。
        Assert.Contains("workbench.action.terminal.new", mainWindow);
        Assert.Contains("{Binding Terminal.Profiles}", mainWindow);
        Assert.Contains("workbench.action.terminal.clear", mainWindow);
        Assert.Contains("workbench.action.terminal.kill", mainWindow);
        // "终端：新建终端"命令通过命令注册表访问。
        Assert.Contains("IsTerminalSelected", mainWindow);

        var mainCode = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml.cs"));
        // Ctrl+` 新建终端;焦点交给终端表面而非发送框。
        Assert.Contains("Key.OemTilde", mainCode);
        Assert.Contains("FocusSurface", mainCode);
        Assert.DoesNotContain("TerminalInput", mainCode);
    }

    [Fact]
    public void ListRowTemplates_KeepPressedAndFocusStates()
    {
        // projects, git SCM and environment side-bar rows all share the VS Code row interaction:
        // hover + selection + pressed + keyboard focus ring. (Dashboard's operations rows were
        // removed with the operation-log feature; the dashboard is now a plain to-do column.)
        foreach (var path in new[]
        {
            "src/Nornia.Desktop/Views/ProjectsSidebarView.xaml",
            "src/Nornia.Desktop/Views/GitView.xaml",
            "src/Nornia.Desktop/Views/EnvironmentSidebarView.xaml",
            "src/Nornia.Desktop/Views/MainWindow.xaml", // output / problems lists
        })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot, path));
            Assert.Contains("IsMouseCaptured", text);
            Assert.Contains("FocusVisualStyle", text);
        }

        var scm = StyleBlock(
            File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml")),
            "x:Key=\"ScmRowStyle\"");
        Assert.Contains("IsMouseCaptured", scm);
        Assert.Contains("FocusVisualStyle", scm);
        Assert.DoesNotContain("Focusable", scm); // SCM rows stay keyboard-focusable
    }

    [Fact]
    public void CodeWorkbench_PreviewRendersThroughCodeDocumentView()
    {
        var editorArea = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EditorAreaView.xaml"));
        var preview = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml"));
        var document = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CodeDocumentView.xaml"));

        Assert.Contains("x:Name=\"PersistentFilePreview\"", editorArea);
        Assert.Contains("DataContext=\"{Binding SelectedFileTab}\"", editorArea);
        Assert.Contains("HasSelectedFileTab", editorArea);
        Assert.DoesNotContain("DataType=\"{x:Type vm:FilePreviewTab}\"", editorArea);
        Assert.Contains("<views:FilePreviewView", editorArea);
        Assert.Contains("<views:CodeDocumentView", preview);
        Assert.Contains("IsReadOnly=\"True\"", document);
        Assert.Contains("ShowLineNumbers=\"True\"", document);
        // the viewer is fed from the preview model (not hard-coded content)
        Assert.Contains("SourceText=\"{Binding Content}\"", preview);
        Assert.Contains("HighlightingName=\"{Binding FileType.HighlightingName}\"", preview);
    }

    [Fact]
    public void CodeWorkbench_MarkdownSourceSurfaceIsHiddenByMode_NotByRenderResult()
    {
        var preview = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml"));
        var tabModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/EditorAreaViewModel.cs"));

        // 闪帧修复:源码面可见性跟随"模式"(Markdown 进入渲染模式即隐藏,不等渲染结果就绪),
        // 加载/重载窗口不得渲染出"源码 + 内置语法高亮"帧;预览面仍等结果对象就绪才显示。
        Assert.Contains("{Binding ShowSourceSurface}", preview);
        Assert.Contains("public bool ShowSourceSurface => !(IsMarkdown && MarkdownMode == MarkdownViewMode.Rendered);", tabModel);
        // 渲染不可用的确定性状态(二进制/摘要/窗口/解码失败)显式落到源码模式。
        Assert.Contains("private void EnsureMarkdownSourceMode()", tabModel);
        Assert.Contains("<DataTrigger Binding=\"{Binding IsMarkdownRendered}\" Value=\"True\">", preview);
    }

    [Fact]
    public void CodeWorkbench_PresentationPublishesFirstViewportBatch()
    {
        var lang = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Code/LanguagePresentation.cs"));
        var tabModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/EditorAreaViewModel.cs"));
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CodeDocumentView.xaml.cs"));

        // 闪帧修复:grammar 每 scope 进程级只编译一次(存储);分词推进到首屏行数时先发布部分
        // 快照(IsComplete=false),让首屏 1~2 帧内拿到最终配色;内置兜底只在完整快照到达后关闭。
        Assert.Contains("class TextMateGrammarStore", lang);
        Assert.Contains("FirstBatchLines", lang);
        Assert.Contains("IsComplete", lang);
        Assert.Contains("onFirstBatch", tabModel);
        Assert.Contains("UpdateHighlightingFallback", view);
    }

    [Fact]
    public void CodeWorkbench_FindAndNavigationChromeIsWired()
    {
        var preview = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml"));
        var previewCode = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml.cs"));
        var documentCode = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CodeDocumentView.xaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml.cs"));
        var tabModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/EditorAreaViewModel.cs"));

        Assert.Contains("{Binding SearchText, UpdateSourceTrigger=PropertyChanged}", preview);
        Assert.Contains("{Binding IsFindBarOpen", preview);
        Assert.Contains("{Binding GoToLineInput", preview);
        Assert.Contains("ToggleWordWrapCommand", preview);
        Assert.Contains("SetSearchMatches", documentCode);
        Assert.Contains("JumpToLine", documentCode);
        // Global list/tab shortcuts leave the code area alone (focus isolation).
        Assert.Contains("ICSharpCode.AvalonEdit.Editing.TextArea", mainWindow);
        Assert.Contains("FindAncestor<CodeDocumentView>", mainWindow);
        Assert.Contains("FindAncestor<DiffDocumentView>", mainWindow);
        Assert.Contains("FrameworkContentElement content =>", mainWindow);
        Assert.Contains("ContentOperations.GetParent", mainWindow);
        // Focus must be refreshed before keybinding dispatch. Keep this assertion independent of
        // the exception-handling block's indentation so adding a dispatch guard does not make the
        // source-wiring test fail for formatting alone.
        var focusRefresh = mainWindow.LastIndexOf("UpdateFocusContext();", StringComparison.Ordinal);
        var keybindingDispatch = mainWindow.IndexOf("await _keybindings.DispatchAsync", StringComparison.Ordinal);
        Assert.True(focusRefresh >= 0 && keybindingDispatch > focusRefresh,
            "PreviewKeyDown must refresh focus context before dispatching keybindings.");
        // Ctrl+F with a selection in the document auto-fills the find box; the prev/next
        // buttons re-evaluate their enabled state when the match count changes.
        Assert.Contains("_tab.Content[segment.Offset..(segment.Offset + segment.Length)]", previewCode);
        Assert.Contains("FindNextCommand.NotifyCanExecuteChanged", tabModel);
    }

    [Fact]
    public void CodeWorkbench_OutlinePanelIsWired()
    {
        var preview = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml"));
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var tabModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/EditorAreaViewModel.cs"));

        // Right-hand symbol list: only shown when the file has an outline; filter + jump wired.
        Assert.Contains("{Binding HasOutline, Converter={StaticResource BooleanToVisibilityConverter}}", preview);
        Assert.Contains("x:Name=\"OutlineSplitter\"", preview);
        Assert.Contains("TargetType=\"Thumb\"", preview);
        Assert.Contains("DragDelta=\"OutlineSplitter_DragDelta\"", preview);
        Assert.Contains("DragCompleted=\"OutlineSplitter_DragCompleted\"", preview);
        Assert.Contains("x:Name=\"OutlineToggleButton\"", preview);
        Assert.Contains("Click=\"ToggleOutline_Click\"", preview);
        Assert.Contains("x:Name=\"OutlineColumn\" Width=\"0\"", preview);
        Assert.Contains("OutlineFilterText", preview);
        Assert.Contains("JumpToOutlineCommand", preview);
        Assert.Contains("OutlineIndentConverter", app);
        var previewCode = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml.cs"));
        Assert.Contains("Math.Clamp(_outlineWidth, 190, 320)", previewCode);
        Assert.Contains("OutlineColumn.ActualWidth", previewCode);
        Assert.Contains("OutlineColumn.ActualWidth - e.HorizontalChange", previewCode);
        Assert.Contains("_outlinePanelOpen = !_outlinePanelOpen", previewCode);
        // Reading preferences seed the preview and persist back.
        Assert.Contains("EditorFontSize=\"{Binding FontSize, Mode=TwoWay}\"", preview);
        Assert.Contains("BuildOutline", tabModel);
        Assert.Contains("CommitAsync(SettingScope.User", tabModel);
    }

    [Fact]
    public void CodeWorkbench_DiffDocumentIsWired()
    {
        var editorArea = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EditorAreaView.xaml"));
        var diffView = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml"));
        var diffCode = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml.cs"));

        Assert.Contains("DataType=\"{x:Type vm:DiffTab}\"", editorArea);
        Assert.Contains("<views:DiffDocumentView", editorArea);
        Assert.Contains("IsInlineDiff", diffView);
        Assert.Contains("IsSideBySideDiff", diffView);
        Assert.Contains("GoToPreviousChangeCommand", diffView);
        Assert.Contains("GoToNextChangeCommand", diffView);
        Assert.Contains("CopyAllDiffLinesCommand", diffView);
        Assert.Contains("ToggleDiffModeCommand", diffView);
        // real editors for both layouts + the row-aligned side-by-side synchronization
        Assert.Contains("av:TextEditor x:Name=\"InlineEditor\"", diffView);
        Assert.Contains("av:TextEditor x:Name=\"OldEditor\"", diffView);
        Assert.Contains("av:TextEditor x:Name=\"NewEditor\"", diffView);
        Assert.Contains("HookScrollSync(this, new RoutedEventArgs())", diffCode);
        Assert.Contains("ScrollChanged", diffCode);
        Assert.Contains("_hasExpectedSideOffsets", diffCode);
        Assert.Contains("ScrollToHorizontalOffset", diffCode);
        Assert.Contains("ScrollToVerticalOffset", diffCode);
        Assert.Contains("ScrollProgress", diffCode);
        Assert.Contains("ExtentHeight - source.ViewportHeight", diffCode);
        Assert.Contains("ScheduleInitialSideScrollSync", diffCode);
        Assert.Contains("DispatcherPriority.Render", diffCode);
        Assert.Contains("SynchronizeSideEditors(_oldScroll)", diffCode);
        Assert.Contains("BlockAtIndex", diffCode);
        // diff editors render at the persisted reading font size (aligned with the code preview,
        // independent of the global UI font scale)
        var tabModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/EditorAreaViewModel.cs"));
        Assert.Contains("EditorFontSize = options.FontSize > 0 ? options.FontSize : 14", tabModel);
        Assert.Equal(3, diffView.Split("FontSize=\"{Binding EditorFontSize}\"").Length - 1);
    }

    [Fact]
    public void CodeWorkbench_UsesAvalonEditAndExposesVsCodeStyleTabState()
    {
        var project = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Nornia.Desktop.csproj"));
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var tabs = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EditorGroupView.xaml"));
        var unifiedTabs = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EditorGroupsView.xaml"));
        var codeReader = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CodeDocumentView.xaml.cs"));
        var tabModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/WorkbenchTabViewModel.cs"));
        var editorModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/EditorAreaViewModel.cs"));

        Assert.Contains("PackageReference Include=\"AvalonEdit\" Version=\"6.3.1.120\"", project);
        Assert.DoesNotContain("ActiproSoftware", project);
        Assert.Contains("av:TextEditor", app);
        Assert.Contains("FrameworkPropertyMetadata(14.0", codeReader);
        Assert.Contains("public CodeFileType? FileType", tabModel);
        Assert.Contains("TabStatusMarker", editorModel);
        Assert.Contains("DiffStatus", tabs);
        Assert.Contains("Binding DiffStatus", unifiedTabs);
        Assert.Contains("SelectionActiveBrush", unifiedTabs);
        Assert.Contains("FontSize=\"{DynamicResource TypeBody}\"", unifiedTabs);
        Assert.Contains("Width=\"{DynamicResource IconMonogramSlot}\"", unifiedTabs);
        Assert.Contains("<Grid x:Name=\"DiffStatus\"", unifiedTabs);
        Assert.Contains("HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"", unifiedTabs);
        Assert.Contains("<Grid x:Name=\"DiffStatus\"", tabs);
        Assert.Contains("AutomationProperties.Name=\"关闭标签\"", tabs);
        Assert.Contains("TargetName=\"CloseButton\" Property=\"Visibility\" Value=\"Visible\"", tabs);
    }

    [Fact]
    public void CodeWorkbench_MinimapAndPreviewStateAreWired()
    {
        var document = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CodeDocumentView.xaml"));
        var documentCode = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CodeDocumentView.xaml.cs"));
        var preview = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml"));

        Assert.Contains("MinimapHost", document);
        Assert.Contains("ShowMinimapProperty", documentCode);
        Assert.Contains("MinimapLayout.Compute", documentCode);
        Assert.Contains("ScrollToVerticalOffset", documentCode);
        // visible-window + throttled redraw
        Assert.Contains("RequestMinimapDraw", documentCode);
        Assert.Contains("_minimapThrottle", documentCode);
        Assert.Contains("ShowMinimap=\"{Binding ShowMinimap}\"", preview);
        Assert.Contains("ToggleShowMinimapCommand", preview);
    }

    [Fact]
    public void EditorTabs_ReleaseResourcesOnClose()
    {
        var tabModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/EditorAreaViewModel.cs"));

        Assert.Contains("public virtual void ReleaseResources()", tabModel);
        Assert.Contains("tab.ReleaseResources();", tabModel);
        // FilePreviewTab release(SearchMatches 现为 BulkObservableCollection,经 ReplaceRange 清空)
        Assert.Contains("SearchMatches.ReplaceRange([]);", tabModel);
        Assert.Contains("DiffLines.Clear();", tabModel);          // DiffTab release
    }

    [Fact]
    public void CodeWorkbench_FoldingAndViewStateAreWired()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CodeDocumentView.xaml.cs"));
        var previewCode = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/FilePreviewView.xaml.cs"));
        var tabModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/EditorAreaViewModel.cs"));

        // VS Code 风格折叠栏(FoldGutterMargin chevron 列)替代被隐藏的 AvalonEdit FoldingMargin;
        // FoldingManager.Install 会自动插入默认 FoldingMargin,必须移除,否则旧折叠栏仍可见。
        Assert.Contains("FoldingManager.Install", code);
        Assert.Contains("new FoldGutterMargin", code);
        Assert.Contains("OfType<FoldingMargin>()", code);
        Assert.Contains("SetFoldingSections", code);
        Assert.Contains("BuildCurrentFoldRegions", code);
        Assert.Contains("CaptureFoldedOffsets", code);
        // Per-tab reading-position restore (scroll / caret / folded offsets).
        Assert.Contains("CaptureViewState", code);
        Assert.Contains("RestoreViewState", code);
        Assert.Contains("CodeView.CaptureViewState()", previewCode);
        Assert.Contains("CodeView.RestoreViewState(_pendingRestore)", previewCode);
        Assert.Contains("ComputeFolds", tabModel);
        Assert.Contains("EditorViewState", tabModel);
        Assert.Contains("CodeFoldSection", tabModel);
        // indentation guides render from the theme token (CodeIndentationGuideBrush)
        Assert.Contains("IndentationGuideRenderer", code);
        Assert.Contains("CodeIndentationGuideBrush", code);
    }

    [Fact]
    public void CodeWorkbench_VSCodeParityFeatures()
    {
        // 贴近 VS Code 观感的代码阅读器特性(逐项随实施推进增补)。
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CodeDocumentView.xaml.cs"));
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CodeDocumentView.xaml"));

        // A1: 当前行高亮是淡 tint,无 1px 边框(HS 主题仍实心,边框已移除)。
        Assert.Contains("CurrentLineBorder = null", code);
        Assert.DoesNotContain("CurrentLineBorder = Pen", code);

        // A1b: 选区使用 VS Code 风格的圆角背景、无边框。
        Assert.Contains("SelectionBorder = null", code);
        Assert.Contains("SelectionCornerRadius = 3", code);

        var dark = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Themes/Dark.xaml"));
        var light = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Themes/Light.xaml"));
        var highContrast = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Themes/HighContrast.xaml"));
        Assert.Contains("x:Key=\"EditorBrush\" Color=\"#121314\"", dark);
        Assert.Contains("x:Key=\"SideBarBrush\" Color=\"#191A1B\"", dark);
        Assert.Contains("x:Key=\"AccentBrush\" Color=\"#297AA0\"", dark);
        Assert.Contains("x:Key=\"CodeSelectionBrush\" Color=\"#DD276782\"", dark);
        Assert.Contains("x:Key=\"EditorBrush\" Color=\"#FFFFFF\"", light);
        Assert.Contains("x:Key=\"SideBarBrush\" Color=\"#FAFAFD\"", light);
        Assert.Contains("x:Key=\"AccentBrush\" Color=\"#0069CC\"", light);
        Assert.Contains("x:Key=\"CodeSelectionBrush\" Color=\"#400069CC\"", light);
        Assert.Contains("x:Key=\"EditorBrush\" Color=\"#000000\"", highContrast);
        Assert.Contains("x:Key=\"TextBrush\" Color=\"#FFFFFF\"", highContrast);
        Assert.Contains("x:Key=\"CodeSelectionBrush\" Color=\"#FFFFFF\"", highContrast);
        Assert.Contains("x:Key=\"CodeIndentationGuideBrush\" Color=\"#FFFFFF\"", highContrast);

        // A2: 活动行号随光标变亮(自定义 margin 替换内置单色 margin)。
        Assert.Contains("CodeLineNumberMargin", code);
        Assert.Contains("CodeLineNumberActiveBrush", code);
        Assert.Contains("OfType<LineNumberMargin>", code);

        // A3: 缩进导线贯穿缩进块,并有光标行活动导线。
        Assert.Contains("CodeIndentationGuideActiveBrush", code);

        // A4: 全文档 minimap 使用语义 token 着色 + 查找匹配标记 + 半透明滑条。
        Assert.Contains("CodeMinimapMatchBrush", code);
        Assert.Contains("_minimapTokenKindByLine", code);
        Assert.Contains("map.LineStripHeight", code);

        // A5: 右缘 overview ruler(查找匹配/光标行标记)。
        Assert.Contains("CodeOverviewCanvas", view);
        Assert.Contains("RedrawCodeOverview", code);
    }

    [Fact]
    public void DiffView_VSCodeParityFeatures()
    {
        // 贴近 VS Code 观感的 diff 视图特性(逐项随实施推进增补)。
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml.cs"));
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml"));

        // B1: 折叠未变更区块可点击展开(命中检测 + 持久投影的 ExpandAtDisplayIndex)。
        Assert.Contains("ExpandAtDisplayIndex", code);
        Assert.Contains("GetPositionFloor", code);
        Assert.Contains("DiffExpandPlaceholderBrush", code);

        // B4: 打开即定位首个变更块(不再 ScrollToHome)。
        Assert.DoesNotContain("ScrollToHome", code);
        Assert.Contains("JumpToChange(0)", code);

        // B2: 行号区随字号缩放 + 活动行号变亮 + ± 符号列。
        Assert.Contains("CodeLineNumberActiveBrush", code);
        Assert.Contains("EditorGutterAddedBrush", code);
        Assert.Contains("EditorFontSize", code);

        // B3: split 模式两栏各有 overview ruler。
        Assert.Contains("OldOverviewCanvas", view);
        Assert.Contains("NewOverviewCanvas", view);
    }

    [Fact]
    public void DiffView_UsesVSCodeSurfaceAndLayoutTokens()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml.cs"));

        Assert.Contains("ShadowPopupEffect", view);
        Assert.Contains("DiffToolbarSeparatorBrush", view);
        Assert.Contains("DiffOverviewRailBrush", view);
        Assert.Contains("DiffEditorSashBrush", view);
        Assert.Contains("DiffOverviewViewportBrush", code);
        Assert.Contains("DiffEditorBorderBrush", code);
        Assert.Contains("DiffPlaceholderForegroundBrush", code);
        Assert.Contains("DrawNumber", code);
    }

    [Fact]
    public void DiffThemes_ProvideEditorSurfaceTokens()
    {
        foreach (var path in ThemeFiles)
        {
            var theme = File.ReadAllText(Path.Combine(RepoRoot, path));
            foreach (var key in new[]
            {
                "DiffEditorBorderBrush", "DiffEditorSashBrush", "DiffOverviewRailBrush",
                "DiffOverviewViewportBrush", "DiffUnchangedRegionBrush",
                "DiffPlaceholderForegroundBrush", "DiffToolbarSeparatorBrush"
            })
            {
                Assert.Contains($"x:Key=\"{key}\"", theme);
            }
        }
    }

    // ---- context menus =====

    [Fact]
    public void WorkspaceTree_HasContextMenuWithPathRevealAndRefreshActions()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/WorkspaceView.xaml"));
        Assert.Contains("SidebarRowChrome.ContextMenu", text);
        Assert.Contains("CopyPathCommand", text);
        Assert.Contains("CopyRelativePathCommand", text);
        Assert.Contains("RevealInSystemExplorerCommand", text);
        Assert.Contains("ExpandAllCommand", text);
        Assert.Contains("CollapseAllCommand", text);
        Assert.Contains("RefreshCommand", text);
    }

    [Fact]
    public void TerminalSurface_RightClickCopiesSelectionOrPastesWithoutAMenu()
    {
        // The interactive ConPTY surface never displays a context menu: a right click copies an
        // active selection, otherwise it pastes, matching common terminal behavior.
        var text = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/TerminalSurfaceControl.cs"));
        Assert.Contains("MouseButton.Right", text);
        Assert.Contains("CopySelection", text);
        Assert.Contains("PasteClipboard", text);
        Assert.DoesNotContain("ContextMenu", text);
    }

    [Fact]
    public void CommitGraphRows_HandleExpansionAfterListSelectionHasSettled()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml.cs"));

        Assert.Contains("MouseLeftButtonUp=\"CommitBar_MouseLeftButtonUp\"", view);
        Assert.DoesNotContain("Gesture=\"LeftClick\"\n                          Command=\"{Binding DataContext.ToggleLogRowCommand", view);
        Assert.Contains("ToggleLogRowCommand.Execute(row)", code);
        Assert.Contains("VirtualizingPanel.ScrollUnit=\"Pixel\"", view);
    }

    [Fact]
    public void GitTagContextMenu_ActionsCarryTagNameAndInheritThemedMenuItemStyle()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        var model = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/GitViewModel.cs"));

        // 操作项参数必须取条目自带 TagName:操作项位于标签 MenuItem 的嵌套弹出层内,
        // RelativeSource AncestorType 不跨 Popup 边界,祖先查找恒为 null → 删除/推送/检出
        // 等命令静默无操作("删除标签点击没反应"的根因)。
        Assert.DoesNotContain("AncestorType=MenuItem", view);
        Assert.Contains("<Setter Property=\"CommandParameter\" Value=\"{Binding TagName}\" />", view);
        Assert.Contains("record GitMenuCommandItem(string Header, System.Windows.Input.ICommand Command, string TagName)", model);

        // 嵌套菜单三层样式 + RecentCommitMessages/分支菜单的 ItemContainerStyle 必须以
        // 主题隐式 MenuItem 样式为基,否则弹出层回落系统默认模板(丢失主题)。
        var basedOnCount = view.Split("BasedOn=\"{StaticResource {x:Type MenuItem}}\"").Length - 1;
        Assert.True(basedOnCount >= 5, $"GitView 菜单样式应至少 5 处 BasedOn 主题 MenuItem 样式,实际 {basedOnCount}");
    }

    [Fact]
    public void CommitGraphRows_KeepSelectionBehaviorWithoutPaintingBlueSelection()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/GitView.xaml"));
        var start = view.IndexOf("<Style x:Key=\"ScmGraphRowStyle\"", StringComparison.Ordinal);
        var end = view.IndexOf("</Style>", start, StringComparison.Ordinal) + "</Style>".Length;
        var graphRowStyle = view[start..end];

        Assert.DoesNotContain("BasedOn=\"{StaticResource ScmRowStyle}\"", graphRowStyle);
        Assert.DoesNotContain("Property=\"IsSelected\"", graphRowStyle);
        Assert.DoesNotContain("SelectionBrush", graphRowStyle);
        Assert.Contains("Property=\"IsMouseOver\"", graphRowStyle);
    }

    [Fact]
    public void EnvironmentManagementTables_VerticallyCenterCellContent()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/EnvironmentManagementView.xaml"));
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var runtime = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/RuntimeView.xaml"));
        var tools = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/ToolsView.xaml"));
        var packages = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/PackagesView.xaml"));
        var cache = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CacheView.xaml"));

        Assert.Contains("TargetType=\"DataGridCell\"", view);
        Assert.Contains("Property=\"VerticalContentAlignment\" Value=\"Center\"", view);
        Assert.Contains("BasedOn=\"{StaticResource {x:Type DataGridCell}}\"", view);
        Assert.Contains("x:Key=\"EnvironmentDataGridCellStyle\"", app);
        Assert.Contains("CellStyle=\"{StaticResource EnvironmentDataGridCellStyle}\"", runtime);
        Assert.Contains("CellStyle=\"{StaticResource EnvironmentDataGridCellStyle}\"", tools);
        Assert.Equal(2, CountOccurrences(packages, "CellStyle=\"{StaticResource EnvironmentDataGridCellStyle}\""));
        Assert.Equal(2, CountOccurrences(cache, "CellStyle=\"{StaticResource EnvironmentDataGridCellStyle}\""));
    }

    [Fact]
    public void SearchOptionToggles_UseThemeAwareToolbarStyle()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/SearchSidebarView.xaml"));

        Assert.Contains("x:Key=\"SearchOptionToggleStyle\"", view);
        Assert.Equal(4, Regex.Matches(view, "Style=\"\\{StaticResource SearchOptionToggleStyle\\}\"").Count);
        Assert.Contains("Content=\"{loc:StringLoc Key=Search_UseIgnoreFiles}\"", view);
        Assert.Contains("Value=\"{DynamicResource HoverBrush}\"", view);
        Assert.Contains("Value=\"{DynamicResource SelectionActiveBrush}\"", view);
        Assert.Contains("Value=\"{DynamicResource AccentBrush}\"", view);
        Assert.Contains("Value=\"{DynamicResource FocusBorderBrush}\"", view);
    }

    [Fact]
    public void CachePage_UsesResizableSummaryAndDetailTables()
    {
        var cache = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CacheView.xaml"));

        Assert.Contains("Height=\"2*\" MinHeight=\"120\"", cache);
        Assert.Contains("Height=\"3*\" MinHeight=\"160\"", cache);
        Assert.Contains("Style=\"{StaticResource HorizontalSashStyle}\"", cache);
        Assert.Contains("ResizeBehavior=\"PreviousAndNext\"", cache);
        Assert.Contains("SelectionMode=\"Single\"", cache);
        Assert.Contains("Header=\"已选/总计\"", cache);
        Assert.Contains("Header=\"清理空间/总空间\"", cache);
        Assert.Contains("Header=\"类型\"", cache);
        Assert.Contains("Header=\"占用空间\"", cache);
        Assert.Contains("DataGridTemplateColumn Header=\"清理\"", cache);
        Assert.Contains("CellStyle=\"{StaticResource CacheSelectionCellStyle}\"", cache);
        Assert.Contains("HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Stretch\"", cache);
        Assert.Contains("HorizontalContentAlignment=\"Center\" VerticalContentAlignment=\"Center\"", cache);
        Assert.Contains("Background=\"Transparent\"", cache);
        Assert.Contains("Content=\"全选\" Command=\"{Binding SelectAllInCategoryCommand}\"", cache);
        Assert.Contains("Content=\"全不选\" Command=\"{Binding ClearCategorySelectionCommand}\"", cache);
        Assert.DoesNotContain("b:MultiSelectorBinding.SelectedItems=\"{Binding SelectedCategorySummaries}\"", cache);
    }

    [Fact]
    public void MainWorkbench_UsesStableThreeSlotPanelHeaderAndResponsiveFrame()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/MainWindow.xaml"));
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var startup = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml.cs"));

        Assert.Contains("MinWidth=\"960\"", view);
        Assert.Contains("ShowActivated=\"True\"", view);
        Assert.Contains("x:Name=\"EditorPanelHost\"", view);
        Assert.Contains("<Grid.ColumnDefinitions>", view);
        Assert.Contains("Grid.Column=\"2\" Orientation=\"Horizontal\" HorizontalAlignment=\"Right\"", view);
        Assert.DoesNotContain("<DockPanel Height=\"{DynamicResource SizeTabBar}\"", view);
        Assert.Contains("SizeWorkbenchSplitter", app);
        Assert.Contains("MinEditorWidth", app);
        Assert.Contains("mainWindow.Activate();", startup);
        Assert.Contains("DispatcherPriority.ApplicationIdle", startup);
    }

    [Fact]
    public void SettingsPage_HasAccentConfiguration()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/SettingsView.xaml"));
        Assert.Contains("ItemsSource=\"{Binding Editor.View}\"", view);
        var catalog = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Configuration/BuiltInSettingsCatalog.cs"));
        Assert.Contains("Accent", catalog);
        Assert.Contains("强调色", catalog);
        Assert.Contains("SettingEditorKind.Color", catalog);
    }

    // ---- syntax highlighting palette =====

    [Fact]
    public void HighlightingPalette_MeetsEditorContrastFloor()
    {
        // Syntax-highlighting palette tokens must stay readable on the editor surface of every
        // theme: WCAG contrast vs the theme's EditorBrush must reach the UI/large-text floor (3.0).
        var ratioRegex = new Regex(@"x:Key=""(?<key>EditorBrush|Hl[A-Za-z]+Brush)"" Color=""#(?<hex>[0-9A-Fa-f]{6})""", RegexOptions.Compiled);
        foreach (var path in ThemeFiles)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot, path));
            var colors = new Dictionary<string, (double R, double G, double B)>(StringComparer.Ordinal);
            foreach (Match match in ratioRegex.Matches(text))
            {
                colors[match.Groups["key"].Value] = ParseHexColor(match.Groups["hex"].Value);
            }

            Assert.True(colors.TryGetValue("EditorBrush", out var editor), $"{path} 缺少 EditorBrush");
            foreach (var (key, color) in colors.Where(pair => pair.Key.StartsWith("Hl", StringComparison.Ordinal)))
            {
                var ratio = ContrastRatio(Luminance(editor), Luminance(color));
                Assert.True(ratio >= 3.0, $"{path} 的 {key} 与编辑区背景对比度 {ratio:F2} < 3.0");
            }
        }
    }

    private static (double R, double G, double B) ParseHexColor(string hex)
    {
        var red = Convert.ToInt32(hex[..2], 16) / 255.0;
        var green = Convert.ToInt32(hex.Substring(2, 2), 16) / 255.0;
        var blue = Convert.ToInt32(hex[4..], 16) / 255.0;
        return (red, green, blue);
    }

    private static (double R, double G, double B, double A) ParseXamlColor(string hex)
    {
        if (hex.Length == 6)
        {
            var rgb = ParseHexColor(hex);
            return (rgb.R, rgb.G, rgb.B, 1.0);
        }

        var alpha = Convert.ToInt32(hex[..2], 16) / 255.0;
        var color = ParseHexColor(hex[2..]);
        return (color.R, color.G, color.B, alpha);
    }

    private static (double R, double G, double B) CompositeOver(
        (double R, double G, double B, double A) foreground,
        (double R, double G, double B) background)
    {
        var inverseAlpha = 1.0 - foreground.A;
        return (
            foreground.R * foreground.A + background.R * inverseAlpha,
            foreground.G * foreground.A + background.G * inverseAlpha,
            foreground.B * foreground.A + background.B * inverseAlpha);
    }

    private static double Luminance((double R, double G, double B) color)
    {
        double Linear(double channel) => channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static double ContrastRatio(double first, double second)
    {
        var lighter = Math.Max(first, second);
        var darker = Math.Min(first, second);
        return (lighter + 0.05) / (darker + 0.05);
    }

    // ---- helpers =====

    private static HashSet<string> ReadTokenKeys(string relativePath) =>
        ReadXKeys(Path.Combine(RepoRoot, relativePath));

    private static HashSet<string> ReadXKeys(string path) =>
        XKeyRegex.Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>Extracts one style element (up to its first closing <c>&lt;/Style&gt;</c>) starting at
    /// the given marker, so template assertions are scoped instead of file-wide.</summary>
    private static string StyleBlock(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到样式片段 {marker}");
        var end = text.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"样式片段 {marker} 缺少闭合 </Style>");
        return text[start..(end + "</Style>".Length)];
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

    private static IEnumerable<string> AllXamlFiles() =>
    [
        Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"),
        .. Directory.EnumerateFiles(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views"), "*.xaml", SearchOption.AllDirectories),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ResolveRepoRoot([CallerFilePath] string? sourcePath = null)
    {
        var testsDir = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("无法定位测试源文件路径。");
        return Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
    }
}
