using Nornia.Desktop.Commands;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;
using NSubstitute;

namespace Nornia.Tests;

/// <summary>回归:设置页激活链(SettingsViewModel → PageViewModel.ActivateAsync)可能在没有
/// 同步上下文的线程上启动(fire-and-forget)。此时 await 之后的续体落在线程池,而绑定集合
/// (Items/ShortcutItems 及其 CollectionView)只能从调度程序线程变更,否则 WPF 抛出
/// "该类型的 CollectionView 不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改"
/// (生产日志中表现为未观察的 Task 异常)。VM 必须像 OnSessionChanged/OnBindingsChanged
/// 事件回调一样把应用回 marshal 到调度程序线程。</summary>
public sealed class SettingsEditorThreadingTests
{
    [Fact]
    public async Task InitializeAsync_FromThreadWithoutSyncContext_AppliesBoundCollectionsOnDispatcher()
    {
        SettingsEditorViewModel editor = null!;

        // 在调度程序线程上构造:_dispatcher 即 UI dispatcher,构造函数创建的默认
        // CollectionView(ShortcutView)也挂在该线程上 —— 源集合跨线程变更会立即抛异常。
        WpfStaContext.Run(() =>
        {
            var keybindings = Substitute.For<IKeybindingService>();
            keybindings.Bindings.Returns(Array.Empty<KeybindingDefinition>());
            keybindings.Conflicts.Returns(Array.Empty<KeybindingConflict>());
            keybindings.Diagnostics.Returns(Array.Empty<KeybindingDiagnostic>());

            var commands = new CommandRegistry();
            commands.Register(new CommandDescriptor("test.command", "测试命令", "常用", "list-tree",
                [], _ => Task.CompletedTask));

            editor = new SettingsEditorViewModel(
                new FakeSettingsService(),
                new BuiltInSettingsCatalog(),
                new FakeProjectWorkspaceService(),
                keybindings,
                commands,
                new FakeApplicationStateStore());
        });

        // Task.Run 线程没有同步上下文:InitializeAsync 的续体全部落在线程池 ——
        // 即生产事故的线程形态(工作区激活链在后台线程触发)。
        var init = Task.Run(() => editor.InitializeAsync());

        // 泵调度程序:BeginInvoke 投递的应用(ApplySnapshot/PopulateShortcutItems)
        // 在调度程序线程落地。
        WpfStaContext.Run(WpfStaContext.PumpQueue);

        var exception = await Record.ExceptionAsync(() => init);
        Assert.Null(exception);
        // 快捷键条目已在调度程序线程上填充(0 → 1);修复前该步跨线程变更直接抛
        // NotSupportedException,init 任务 fault。
        Assert.Single(editor.ShortcutItems);
    }
}
