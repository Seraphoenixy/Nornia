using System.Globalization;
using Nornia.CLI;

namespace Nornia.Tests;

/// <summary>Dispatcher routing and global argument parsing. Commands are fakes so routing and the
/// longest-prefix rule are exercised without any real service wiring.</summary>
public sealed class CliDispatcherTests
{
    [Fact]
    public async Task Dispatch_RoutesExactCommandName()
    {
        var command = new FakeCommand("hello world");
        var dispatcher = new CommandDispatcher([new FakeCommand("other"), command]);

        var exitCode = await dispatcher.DispatchAsync(["hello", "world"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(command.ReceivedArguments);
    }

    [Fact]
    public async Task Dispatch_LongestRoutePrefixWins()
    {
        var shortCommand = new FakeCommand("alpha");
        var longCommand = new FakeCommand("alpha beta");
        var dispatcher = new CommandDispatcher([shortCommand, longCommand]);

        await dispatcher.DispatchAsync(["alpha", "beta", "gamma"], CancellationToken.None);

        Assert.Equal(["gamma"], longCommand.ReceivedArguments);
        Assert.Empty(shortCommand.ReceivedArguments);
    }

    [Fact]
    public async Task Dispatch_RoutesPackageCacheSubcommandsOverPackagePrefix()
    {
        var packageCommand = new FakeCommand("package list");
        var cacheScan = new FakeCommand("package cache scan");
        var cacheClean = new FakeCommand("package cache clean");
        var dispatcher = new CommandDispatcher([packageCommand, cacheScan, cacheClean]);

        await dispatcher.DispatchAsync(["package", "cache", "scan"], CancellationToken.None);
        Assert.Empty(cacheScan.ReceivedArguments);

        await dispatcher.DispatchAsync(["package", "cache", "clean", "--id", "abc"], CancellationToken.None);
        Assert.Equal(["--id", "abc"], cacheClean.ReceivedArguments);

        Assert.Empty(packageCommand.ReceivedArguments);
    }

    [Fact]
    public async Task Dispatch_IsCaseInsensitiveOnRouteSegments()
    {
        var command = new FakeCommand("runtime list");
        var dispatcher = new CommandDispatcher([command]);

        var exitCode = await dispatcher.DispatchAsync(["RUNTIME", "List"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(command.ReceivedArguments);
    }

    [Fact]
    public async Task Dispatch_UnknownCommandReturnsTwo()
    {
        var dispatcher = new CommandDispatcher([new FakeCommand("known")]);

        var exitCode = await dispatcher.DispatchAsync(["unknown", "thing"], CancellationToken.None);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void CliArguments_RemovesLanguageOptionAndKeepsCommand()
    {
        var invocation = CliArguments.Parse(["--lang", "zh-Hans", "runtime", "list"]);

        Assert.Equal("zh-Hans", invocation.Culture.Name);
        Assert.Equal(["runtime", "list"], invocation.Arguments);
    }

    [Fact]
    public void CliArguments_UsesSystemCultureWhenLanguageMissing()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            var invocation = CliArguments.Parse(["runtime", "list"]);
            Assert.Equal("de-DE", invocation.Culture.Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void CliArguments_RejectsUnknownLanguage()
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--lang", "not-a-culture", "help"]));
    }

    private sealed class FakeCommand(string name) : ICommand
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Aliases => [];
        public string Summary => "fake";
        public string Usage => $"Nornia {Name}";
        public List<string> ReceivedArguments { get; } = [];

        public Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            ReceivedArguments.AddRange(args);
            return Task.FromResult(0);
        }
    }
}