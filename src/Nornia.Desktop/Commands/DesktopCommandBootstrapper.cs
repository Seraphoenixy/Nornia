using Nornia.Desktop.ViewModels;

namespace Nornia.Desktop.Commands;

/// <summary>
/// Registers immutable command metadata. Runtime execution is routed through a weak workbench target,
/// so registry descriptors never retain a MainViewModel or close over mutable UI state.
/// </summary>
public sealed class DesktopCommandBootstrapper(ICommandRegistry registry, IKeybindingService keybindings)
{
    private WeakReference<MainViewModel>? _target;
    private bool _registered;

    public Func<string, Task>? WindowAction { get; set; }

    public void Register(MainViewModel viewModel)
    {
        _target = new(viewModel);
        if (_registered) return;
        _registered = true;

        Add("workbench.action.showCommands", "显示所有命令", "常用", Codicons.Menu, "ctrl+shift+p");
        Add("workbench.action.quickOpen", "快速打开文件", "文件", Codicons.GoToFile, "ctrl+p");
        Add("workbench.action.findInFiles", "在文件中查找", "文件", Codicons.Search, "ctrl+shift+f");
        Add("workbench.action.openSettings", "打开设置", "首选项", Codicons.Settings, "ctrl+,");
        Add("workbench.action.openGlobalKeybindings", "打开键盘快捷方式", "首选项", Codicons.Keyboard, "ctrl+k ctrl+s");
        Add("nornia.project.open", "打开项目目录", "文件", Codicons.FolderOpened);
        Add("nornia.theme.dark", "切换到深色主题", "视图", Codicons.Gear);
        Add("nornia.theme.light", "切换到浅色主题", "视图", Codicons.Gear);
        Add("nornia.theme.highContrast", "切换到高对比度主题", "视图", Codicons.Gear);
        Add("nornia.about", "关于 Nornia", "帮助", Codicons.Info);
        Add("workbench.action.toggleSidebarVisibility", "切换侧栏可见性", "视图", Codicons.LayoutSidebarLeft, "ctrl+b");
        Add("workbench.action.togglePanel", "切换面板", "视图", Codicons.LayoutPanel, "ctrl+j");
        Add("workbench.action.toggleMaximizedPanel", "最大化/恢复面板", "视图", Codicons.ChromeMaximize);
        // 输入框(TextBox)只让出与文本编辑冲突的按键:全选/复制留给控件本身(原生 Ctrl+A/C),
        // 其余应用快捷键在输入框聚焦时照常生效(VS Code 语义)——关闭标签、切换标签、最近标签、
        // 拆分、切换编辑器组都不得因为"焦点在输入框"而静默失效;终端自己拥有键盘,故排除 terminalFocus。
        Add("workbench.action.splitEditorRight", "向右拆分编辑器", "视图", Codicons.SplitHorizontal, "ctrl+\\",
            "!terminalFocus");
        Add("workbench.action.splitEditorDown", "向下拆分编辑器", "视图", Codicons.SplitVertical, "ctrl+shift+\\",
            "!terminalFocus");
        Add("workbench.action.focusNextEditorGroup", "聚焦下一个编辑器组", "视图", Codicons.ChevronRight, "ctrl+k ctrl+right",
            "!terminalFocus");
        Add("workbench.action.focusPreviousEditorGroup", "聚焦上一个编辑器组", "视图", Codicons.ChevronLeft, "ctrl+k ctrl+left",
            "!terminalFocus");
        Add("workbench.action.closeActiveEditor", "关闭活动编辑器", "视图", Codicons.Close, "ctrl+w");
        Add("workbench.action.reopenClosedEditor", "重新打开已关闭的编辑器", "视图", Codicons.History, "ctrl+shift+t", "!terminalFocus");
        Add("workbench.action.nextEditor", "下一个编辑器", "视图", Codicons.ChevronRight, "ctrl+pagedown");
        Add("workbench.action.previousEditor", "上一个编辑器", "视图", Codicons.ChevronLeft, "ctrl+pageup");
        Add("workbench.action.openNextRecentlyUsedEditor", "下一个最近使用的编辑器", "视图", Codicons.History, "ctrl+tab",
            "!terminalFocus");
        Add("workbench.action.openPreviousRecentlyUsedEditor", "上一个最近使用的编辑器", "视图", Codicons.History, "ctrl+shift+tab",
            "!terminalFocus");
        Add("workbench.action.terminal.toggleTerminal", "切换终端", "终端", Codicons.Terminal, "ctrl+`");
        Add("workbench.action.terminal.new", "新建终端", "终端", Codicons.Terminal, "ctrl+shift+`");
        Add("nornia.action.copyFocused", "复制所选内容", "编辑", Codicons.Copy, "ctrl+c", "!inputFocus && !terminalFocus",
            dispatchOnly: true);
        Add("nornia.action.selectAllFocused", "全选当前列表", "编辑", Codicons.CheckAll, "ctrl+a", "!inputFocus",
            dispatchOnly: true);
        Add("workbench.action.terminal.clear", "清除终端", "终端", Codicons.ClearAll, "ctrl+shift+k", "terminalFocus");
        Add("workbench.action.terminal.focus", "聚焦终端", "终端", Codicons.Terminal, "ctrl+/");
        Add("workbench.action.terminal.kill", "关闭当前终端", "终端", Codicons.Close);
        Add("nornia.output.clear", "清除输出", "视图", Codicons.ClearAll);
        Add("nornia.panel.output", "显示输出面板", "视图", Codicons.Output);
        Add("nornia.panel.problems", "显示问题面板", "视图", Codicons.Error);
        Add("nornia.panel.terminal", "显示终端面板", "视图", Codicons.Terminal);
        Add("workbench.action.refreshActiveView", "刷新活动视图", "视图", Codicons.Refresh, "f5", "!terminalFocus");
        for (var index = 1; index <= 8; index++)
            Add($"nornia.action.navigateActivity.{index}", $"打开活动栏第 {index} 项", "视图", Codicons.Menu, $"ctrl+{index}");
        Add("workbench.action.compareEditor.nextChange", "转到下一个更改", "Diff", Codicons.ArrowDown, "alt+f5", "activeEditor == diff");
        Add("workbench.action.compareEditor.previousChange", "转到上一个更改", "Diff", Codicons.ArrowUp, "shift+alt+f5", "activeEditor == diff");
        Add("editor.foldAll", "折叠全部", "编辑", Codicons.CollapseAll, "ctrl+k ctrl+0", "activeEditor == code && !markdownPreviewFocus");
        Add("editor.unfoldAll", "展开全部", "编辑", Codicons.Unfold, "ctrl+k ctrl+j", "activeEditor == code && !markdownPreviewFocus");
        for (var level = 1; level <= 6; level++)
            Add($"editor.foldLevel{level}", $"折叠到层级 {level}", "编辑", Codicons.CollapseAll, $"ctrl+k ctrl+{level}", "activeEditor == code && !markdownPreviewFocus");
        Add("editor.toggleStickyScroll", "切换吸顶滚动（Sticky Scroll）", "编辑", Codicons.Pin, "", "activeEditor == code && !markdownPreviewFocus");
        Add("nornia.editor.changeEncoding", "为当前文件选择编码", "编辑", Codicons.Eye, "", "activeEditor == code");
        Add("nornia.editor.toggleBlame", "切换 Blame 行注解", "编辑", Codicons.GitCommit, "", "activeEditor == code");
        Add("nornia.git.showActiveFileHistory", "查看当前文件的提交历史", "源代码管理", Codicons.History);
        Add("workbench.action.showAllEditors", "显示全部打开的编辑器…", "视图", Codicons.More);
        Add("workbench.action.gotoSymbol", "转到文件中的符号…", "文件", Codicons.SymbolFile, "ctrl+shift+o", "activeEditor == code && !markdownPreviewFocus");
        Add("workbench.action.gotoLine", "转到行 / 列…", "文件", Codicons.GoToFile, "ctrl+g", "activeEditor == code && !markdownPreviewFocus");
        // 查找三键此前硬编码在视图 code-behind(FilePreviewView/DiffDocumentView),用户无法重绑;
        // 现在作为默认键位进命令平台,执行体路由到活动标签的同名命令。
        Add("editor.action.startFind", "查找", "编辑", Codicons.Search, "ctrl+f",
            "(activeEditor == code || activeEditor == diff) && !terminalFocus");
        Add("editor.action.findNext", "查找下一个", "编辑", Codicons.ArrowDown, "f3",
            "(activeEditor == code || activeEditor == diff) && !terminalFocus");
        Add("editor.action.findPrevious", "查找上一个", "编辑", Codicons.ArrowUp, "shift+f3",
            "(activeEditor == code || activeEditor == diff) && !terminalFocus");

        viewModel.AttachCommandPlatform(registry, keybindings);
    }

    /// <summary>注册一条默认快捷键命令。<paramref name="dispatchOnly"/> 用于"焦点条件只决定按键
    /// 归属、不决定命令可用性"的绑定:把它写进 <c>Enablement</c> 会让命令在命令面板/菜单里被焦点
    /// 上下文置灰——而命令面板自己的输入框也会让 <c>inputFocus</c> 为真(面板是独立 Popup,窗口的
    /// 焦点刷新不会在它上面跑),那两个命令就会在面板里永远灰着。copyFocused/selectAllFocused 的
    /// 焦点判断只在分发时做:执行体本身就是按当前焦点元素决定要不要动作的。</summary>
    private void Add(string id, string title, string category, string glyph, string key = "", string? when = null,
        bool dispatchOnly = false) =>
        registry.Register(new(id, title, category, glyph,
            string.IsNullOrWhiteSpace(key) ? [] : [new(key, id, when, IsDefault: true)],
            execution => ExecuteAsync(id, execution), Enablement: dispatchOnly ? null : when,
            MenuPlacements: [new("commandPalette", category)]));

    private async Task ExecuteAsync(string id, CommandExecutionContext execution)
    {
        if (_target is null || !_target.TryGetTarget(out var viewModel)) return;
        var terminalFocus = execution.ContextKeys.GetValueOrDefault("terminalFocus") is true;

        switch (id)
        {
            case "workbench.action.showCommands": Run(viewModel.ShowCommandPaletteCommand); break;
            case "workbench.action.quickOpen": await RunAsync(viewModel.ShowQuickOpenCommand); break;
            case "workbench.action.findInFiles": Run(viewModel.OpenSearchCommand); break;
            case "workbench.action.openSettings": viewModel.OpenSettings(false); break;
            case "workbench.action.openGlobalKeybindings": viewModel.OpenSettings(true); break;
            case "nornia.project.open": Run(viewModel.OpenProjectDirectoryCommand); break;
            case "nornia.about": Run(viewModel.AboutCommand); break;
            case "workbench.action.toggleSidebarVisibility": Run(viewModel.ToggleSidebarCommand); break;
            case "workbench.action.togglePanel": Run(viewModel.TogglePanelCommand); break;
            case "workbench.action.toggleMaximizedPanel": Run(viewModel.TogglePanelMaximizeCommand); break;
            case "workbench.action.splitEditorRight": viewModel.SplitEditorGroup(EditorSplitOrientation.Vertical); break;
            case "workbench.action.splitEditorDown": viewModel.SplitEditorGroup(EditorSplitOrientation.Horizontal); break;
            case "workbench.action.focusNextEditorGroup": viewModel.FocusNextEditorGroup(); break;
            case "workbench.action.focusPreviousEditorGroup": viewModel.FocusPreviousEditorGroup(); break;
            case "workbench.action.closeActiveEditor": viewModel.RouteCloseShortcut(terminalFocus); break;
            case "workbench.action.reopenClosedEditor": await RunAsync(viewModel.Workbench?.Editor.ReopenClosedTabCommand); break;
            case "workbench.action.nextEditor": viewModel.RouteAdjacentTabShortcut(terminalFocus, 1); break;
            case "workbench.action.previousEditor": viewModel.RouteAdjacentTabShortcut(terminalFocus, -1); break;
            case "workbench.action.openNextRecentlyUsedEditor": viewModel.ShowEditorMruSwitcher(true); break;
            case "workbench.action.openPreviousRecentlyUsedEditor": viewModel.ShowEditorMruSwitcher(false); break;
            case "workbench.action.terminal.toggleTerminal":
            case "workbench.action.terminal.new": await RunAsync(viewModel.OpenNewTerminalCommand); break;
            case "nornia.output.clear": Run(viewModel.ClearOutputCommand); break;
            case "nornia.panel.output": viewModel.SelectPanelCommand.Execute(WorkbenchPanel.Output); break;
            case "nornia.panel.problems": viewModel.SelectPanelCommand.Execute(WorkbenchPanel.Problems); break;
            case "nornia.panel.terminal": viewModel.SelectPanelCommand.Execute(WorkbenchPanel.Terminal); break;
            case "workbench.action.compareEditor.nextChange":
                Run((viewModel.Workbench?.Editor.SelectedTab as DiffTab)?.GoToNextChangeCommand); break;
            case "workbench.action.compareEditor.previousChange":
                Run((viewModel.Workbench?.Editor.SelectedTab as DiffTab)?.GoToPreviousChangeCommand); break;
            case "editor.foldAll":
                Run((viewModel.Workbench?.Editor.SelectedTab as FilePreviewTab)?.CollapseAllFoldCommand); break;
            case "editor.unfoldAll":
                Run((viewModel.Workbench?.Editor.SelectedTab as FilePreviewTab)?.ExpandAllFoldCommand); break;
            case "editor.toggleStickyScroll":
                if (viewModel.Workbench?.Editor.SelectedTab is FilePreviewTab stickyTab)
                    stickyTab.ShowStickyScroll = !stickyTab.ShowStickyScroll;
                break;
            case "nornia.editor.changeEncoding":
                viewModel.OpenEncodingPicker(viewModel.Workbench?.Editor.SelectedTab); break;
            case "nornia.editor.toggleBlame":
                Run((viewModel.Workbench?.Editor.SelectedTab as FilePreviewTab)?.ToggleBlameCommand); break;
            case "nornia.git.showActiveFileHistory":
                Run(viewModel.ShowActiveFileHistoryCommand); break;
            case "workbench.action.showAllEditors":
                Run(viewModel.ShowAllEditorsCommand); break;
            case "workbench.action.gotoSymbol":
                viewModel.OpenSymbolPicker(viewModel.Workbench?.Editor.SelectedTab); break;
            case "workbench.action.gotoLine":
                Run((viewModel.Workbench?.Editor.SelectedTab as FilePreviewTab)?.OpenGoToLineBarCommand); break;
            case "editor.action.startFind":
                switch (viewModel.Workbench?.Editor.SelectedTab)
                {
                    case FilePreviewTab file: Run(file.OpenFindBarCommand); break;
                    case DiffTab diff: Run(diff.OpenDiffFindBarCommand); break;
                }
                break;
            case "editor.action.findNext":
            case "editor.action.findPrevious":
                var findDirection = id == "editor.action.findNext" ? 1 : -1;
                switch (viewModel.Workbench?.Editor.SelectedTab)
                {
                    case FilePreviewTab file:
                        Run(findDirection > 0 ? file.FindNextCommand : file.FindPreviousCommand);
                        break;
                    case DiffTab diff:
                        Run(findDirection > 0 ? diff.FindNextCommand : diff.FindPreviousCommand);
                        break;
                }
                break;
            default:
                if (id.StartsWith("editor.foldLevel", StringComparison.Ordinal) &&
                    int.TryParse(id.AsSpan(id.LastIndexOf('.') + 1), out var foldLevel))
                {
                    var foldCommand = (viewModel.Workbench?.Editor.SelectedTab as FilePreviewTab)?.FoldToLevelCommand;
                    if (foldCommand is not null && foldCommand.CanExecute(foldLevel))
                    {
                        foldCommand.Execute(foldLevel);
                    }
                }
                else if (id.StartsWith("nornia.action.navigateActivity.", StringComparison.Ordinal) &&
                    int.TryParse(id.AsSpan(id.LastIndexOf('.') + 1), out var position))
                    viewModel.NavigateByIndexCommand.Execute(position - 1);
                else if (WindowAction is not null)
                    await WindowAction(id switch
                    {
                        "nornia.theme.dark" => "theme.dark",
                        "nornia.theme.light" => "theme.light",
                        "nornia.theme.highContrast" => "theme.highContrast",
                        "nornia.action.copyFocused" => "copyFocused",
                        "nornia.action.selectAllFocused" => "selectAllFocused",
                        "workbench.action.terminal.clear" => "clearTerminal",
                        "workbench.action.terminal.focus" => "focusTerminal",
                        "workbench.action.terminal.kill" => "closeTerminal",
                        "workbench.action.refreshActiveView" => "refreshActiveView",
                        _ => id,
                    });
                break;
        }
    }

    private static void Run(System.Windows.Input.ICommand? command)
    {
        if (command?.CanExecute(null) == true) command.Execute(null);
    }

    private static Task RunAsync(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? command) =>
        command?.CanExecute(null) == true ? command.ExecuteAsync(null) : Task.CompletedTask;
}
