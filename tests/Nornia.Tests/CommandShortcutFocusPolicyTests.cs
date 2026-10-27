using Nornia.Desktop.Commands;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>输入框(TextBox)聚焦时的快捷键归属(VS Code 语义):
/// <list type="bullet">
/// <item>只有与文本编辑**冲突**的按键让位给控件本身:Ctrl+A 全选、Ctrl+C 复制——分发器必须不消费它们
/// (返回 false),否则输入框里的原生编辑快捷键就失效了;</item>
/// <item>其余应用快捷键(关闭标签 Ctrl+W、上下一个标签 Ctrl+PgUp/PgDn、最近标签 Ctrl+Tab/Ctrl+Shift+Tab、
/// 拆分 Ctrl+\/Ctrl+Shift+\、切换编辑器组 Ctrl+K Ctrl+←/→)在输入框聚焦时**照常生效**——此前它们都带
/// <c>!inputFocus</c>,于是"焦点在输入框"会让这半套快捷键静默失效;</item>
/// <item>终端自己拥有键盘(它是按键汇,不是文本输入框):terminalFocus 时上述导航/拆分按键不得被劫持;</item>
/// <item>焦点上下文只用于按键分发,不用于命令可用性:命令面板(独立 Popup)自己的输入框也会让
/// <c>inputFocus</c> 为真,把它写进 Enablement 会让 copyFocused/selectAllFocused 在面板里永远灰着。</item>
/// </list>
/// 断言用的是生产注册表与生产绑定表(真实 <see cref="DesktopCommandBootstrapper"/>),
/// 不是测试里另抄一份 when 子句。</summary>
public sealed class CommandShortcutFocusPolicyTests
{
    private static Fixture Create()
    {
        var fixture = WorkbenchMainViewModelTests.Create();
        var context = new ContextKeyService();
        var registry = new CommandRegistry(context);
        var keybindings = new KeybindingService(registry, context, TestTempRoot.NewFile("keybindings-focus", ".json"));
        new DesktopCommandBootstrapper(registry, keybindings).Register(fixture.Main);
        return new Fixture(fixture.Main, registry, keybindings, context);
    }

    private sealed record Fixture(MainViewModel Main, CommandRegistry Registry, KeybindingService Keybindings,
        ContextKeyService Context);

    /// <summary>复现主窗口每次按键前的焦点刷新(MainWindow.UpdateFocusContext)。</summary>
    private static void Focus(Fixture fixture, bool textBox = false, bool terminal = false, bool editorText = false)
    {
        fixture.Context.Set("inputFocus", textBox || terminal || editorText);
        fixture.Context.Set("terminalFocus", terminal);
        fixture.Context.Set("editorTextFocus", editorText);
    }

    [Theory]
    [InlineData("ctrl+w")]
    [InlineData("ctrl+pagedown")]
    [InlineData("ctrl+pageup")]
    [InlineData("ctrl+tab")]
    [InlineData("ctrl+shift+tab")]
    [InlineData("ctrl+\\")]
    [InlineData("ctrl+shift+\\")]
    public async Task TextBoxFocus_KeepsNonEditingShortcutsDispatched(string stroke)
    {
        var fixture = Create();
        Focus(fixture, textBox: true);

        Assert.True(await fixture.Keybindings.DispatchAsync(stroke),
            $"输入框聚焦时 '{stroke}' 必须仍然由应用快捷键消费(不得静默失效)");
    }

    [Fact]
    public async Task TextBoxFocus_KeepsEditorGroupChordDispatched()
    {
        var fixture = Create();
        Focus(fixture, textBox: true);

        Assert.True(await fixture.Keybindings.DispatchAsync("ctrl+k"), "和弦前缀必须被消费");
        Assert.True(await fixture.Keybindings.DispatchAsync("ctrl+right"),
            "输入框聚焦时 Ctrl+K Ctrl+→ 必须仍然切换编辑器组");
    }

    [Theory]
    [InlineData("ctrl+a")]
    [InlineData("ctrl+c")]
    public async Task TextBoxFocus_LeavesSelectAllAndCopyToTheTextBox(string stroke)
    {
        var fixture = Create();
        Focus(fixture, textBox: true);

        Assert.False(await fixture.Keybindings.DispatchAsync(stroke),
            $"输入框聚焦时 '{stroke}' 必须留给控件自身的原生编辑行为");

        // 焦点不在输入框(列表/表格)时同一按键仍走应用的列表全选/复制。
        Focus(fixture);
        Assert.True(await fixture.Keybindings.DispatchAsync(stroke),
            $"列表聚焦时 '{stroke}' 仍然是应用的列表命令");
    }

    [Fact]
    public async Task TextBoxFocus_CloseShortcutActuallyClosesTheActiveTab()
    {
        var fixture = Create();
        fixture.Main.NavigateByIndexCommand.Execute(2); // 项目管理 → 打开一个页标签
        Assert.Single(fixture.Main.Workbench.Tabs);
        Focus(fixture, textBox: true);

        Assert.True(await fixture.Keybindings.DispatchAsync("ctrl+w"));

        Assert.Empty(fixture.Main.Workbench.Tabs);
    }

    [Fact]
    public async Task TerminalFocus_KeepsTerminalOwnedKeys()
    {
        var fixture = Create();
        Focus(fixture, textBox: true, terminal: true); // 终端表面同时是 inputFocus

        foreach (var stroke in new[] { "ctrl+tab", "ctrl+shift+tab", "ctrl+\\", "ctrl+a", "ctrl+c" })
        {
            Assert.False(await fixture.Keybindings.DispatchAsync(stroke),
                $"terminalFocus 时 '{stroke}' 属于终端,不得被应用快捷键劫持");
        }

        // 路由到终端会话的两个快捷键仍然有效(由 ViewModel 按 terminalFocus 分流)。
        Assert.True(await fixture.Keybindings.DispatchAsync("ctrl+w"));
        Assert.True(await fixture.Keybindings.DispatchAsync("ctrl+pagedown"));
    }

    [Fact]
    public async Task FocusCondition_DoesNotDisableTheCommandsInThePalette()
    {
        var fixture = Create();
        Focus(fixture, textBox: true); // 命令面板的输入框同样是文本输入

        // Enablement 带 !inputFocus 时这里会因为面板输入框的焦点而拒绝执行(菜单/面板里显示为灰)。
        Assert.True(await fixture.Registry.ExecuteAsync("nornia.action.selectAllFocused"));
        Assert.True(await fixture.Registry.ExecuteAsync("nornia.action.copyFocused"));
    }

    [Fact]
    public async Task EditorFindKeys_DispatchOnCodeAndDiffEditorsButNotTerminal()
    {
        // Ctrl+F / F3 / Ctrl+G / Ctrl+Shift+T 是命令平台的默认键位(此前硬编码在视图 code-behind,
        // 用户无法重绑)。编辑器标签激活时由应用消费;终端聚焦时留给终端。
        var fixture = Create();

        fixture.Context.Set("activeEditor", "code");
        foreach (var stroke in new[] { "ctrl+f", "f3", "shift+f3", "ctrl+g" })
        {
            Assert.True(await fixture.Keybindings.DispatchAsync(stroke),
                $"activeEditor == code 时 '{stroke}' 必须由查找命令消费");
        }

        fixture.Context.Set("activeEditor", "diff");
        foreach (var stroke in new[] { "ctrl+f", "f3", "shift+f3" })
        {
            Assert.True(await fixture.Keybindings.DispatchAsync(stroke),
                $"activeEditor == diff 时 '{stroke}' 必须由查找命令消费");
        }

        Focus(fixture, terminal: true);
        foreach (var stroke in new[] { "ctrl+f", "f3", "shift+f3", "ctrl+shift+t" })
        {
            Assert.False(await fixture.Keybindings.DispatchAsync(stroke),
                $"terminalFocus 时 '{stroke}' 属于终端,不得被应用快捷键劫持");
        }

        // 无编辑器聚焦时 Ctrl+Shift+T(重开已关闭编辑器)在普通输入框聚焦下也照常生效。
        Focus(fixture, textBox: true);
        Assert.True(await fixture.Keybindings.DispatchAsync("ctrl+shift+t"));
    }
}
