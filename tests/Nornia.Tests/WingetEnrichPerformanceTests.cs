using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Providers;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Backfill performance guarantees: bounded concurrency, per-Id deduplication, and graceful
/// degradation when a full-name lookup fails. Concurrency is asserted deterministically with a
/// blocking runner instead of wall-clock timing.</summary>
public sealed class WingetEnrichPerformanceTests
{
    [Fact]
    public async Task ListInstalledAsync_BackfillsTruncatedNamesWithBoundedConcurrency()
    {
        var runner = new BlockingBackfillRunner(BuildTruncatedListOutput(12), expectedShowCalls: 12);
        var provider = new WingetProvider(runner);

        var listing = provider.ListInstalledAsync();
        Assert.True(runner.WaitUntilAllStarted(TimeSpan.FromSeconds(10)), "backfill winget show calls did not start");
        Assert.Equal(4, runner.ShowCalls);
        Assert.Equal(4, runner.MaxConcurrent);
        Assert.True(runner.MaxConcurrent <= 4);
        runner.Release();

        var packages = await listing;

        Assert.Equal(12, packages.Count);
        Assert.All(packages, package => Assert.DoesNotContain('…', package.Name));
        Assert.Equal(12, runner.ShowCalls);
    }

    [Fact]
    public async Task ListInstalledAsync_DeduplicatesBackfillPerId()
    {
        const string output = """
            Name                                  Id                              Version  Source
            ---------------------------------------------------------------------------------------
            Same Long Package Name…               Example.Duplicate               1.0.0    winget
            Same Long Package Name…               Example.Duplicate               1.0.0    winget
            """;
        var runner = new BlockingBackfillRunner(output, expectedShowCalls: 1);
        var provider = new WingetProvider(runner);

        var listing = provider.ListInstalledAsync();
        Assert.True(runner.WaitUntilAllStarted(TimeSpan.FromSeconds(10)), "backfill show did not start");
        runner.Release();

        var packages = await listing;

        // The two identical rows collapse into one package during deduplication (existing behavior),
        // and the shared Id is backfilled with exactly one winget show call.
        var package = Assert.Single(packages);
        Assert.Equal("Complete Package Name", package.Name);
        Assert.Equal(1, runner.ShowCalls);
    }

    [Fact]
    public async Task ListInstalledAsync_FailedBackfillKeepsTruncatedNameAndReportsDiagnostic()
    {
        var runner = new FakeProcessRunner((_, arguments) => arguments[0] == "show"
            ? new ProcessResult(1, "", "show failed")
            : new ProcessResult(0, SingleTruncatedListOutput, ""));
        var progress = new CapturingProgress();
        var provider = new WingetProvider(runner);

        var packages = await provider.ListInstalledAsync(progress);

        var package = Assert.Single(packages);
        Assert.Contains('…', package.Name);
        Assert.Contains(progress.Entries, entry => entry.IsError && entry.Text.Contains(package.Id));
    }

    [Fact]
    public async Task ListInstalledAsync_SecondRefreshReusesBackfillCache()
    {
        var runner = new FakeProcessRunner((_, arguments) => arguments[0] == "show"
            ? new ProcessResult(0, "Found Full Cached Name [Example.Truncated]", "")
            : new ProcessResult(0, SingleTruncatedListOutput, ""));
        var provider = new WingetProvider(runner);

        var first = await provider.ListInstalledAsync();
        var showsAfterFirst = runner.Calls.Count(call => call.Arguments.Count > 0 && call.Arguments[0] == "show");
        var second = await provider.ListInstalledAsync();

        Assert.Equal("Full Cached Name", Assert.Single(second).Name);
        Assert.Equal(showsAfterFirst, runner.Calls.Count(call => call.Arguments.Count > 0 && call.Arguments[0] == "show"));
        Assert.Equal("Full Cached Name", Assert.Single(first).Name);
    }

    private const string SingleTruncatedListOutput = """
        Name                                  Id                              Version  Source
        ---------------------------------------------------------------------------------------
        Partial Package Name…                 Example.Truncated               1.0.0    winget
        """;

    private static string BuildTruncatedListOutput(int count)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Name".PadRight(32) + "Id".PadRight(40) + "Version".PadRight(12) + "Source");
        builder.AppendLine(new string('-', 32 + 40 + 12 + 8));
        for (var index = 0; index < count; index++)
        {
            var name = $"Package Number {index} Name…";
            var id = $"Example.Package.{index}.x64";
            builder.AppendLine(name.PadRight(32) + id.PadRight(40) + "1.0.0".PadRight(12) + "winget");
        }

        return builder.ToString();
    }

    /// <summary>Runner that parks every <c>winget show</c> call until the test releases them, while
    /// counting concurrent in-flight calls so the concurrency cap can be asserted deterministically.
    /// <see cref="WaitUntilAllStarted"/> signals when the in-flight batch saturates the limit
    /// (min(expected calls, 4)), not when every call started — the rest are queued on the semaphore.</summary>
    private sealed class BlockingBackfillRunner(string listOutput, int expectedShowCalls) : IProcessRunner
    {
        private const int SaturationLevel = 4;
        private readonly ManualResetEventSlim _allStarted = new(false);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _concurrent;
        private int _maxConcurrent;
        private int _showCalls;

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public int ShowCalls => Volatile.Read(ref _showCalls);

        public bool WaitUntilAllStarted(TimeSpan timeout) => _allStarted.Wait(timeout);

        public void Release() => _release.TrySetResult();

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IProgress<ProcessOutput>? progress = null,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            int? maximumOutputBytes = null)
        {
            if (arguments is [var first, ..] && first == "show")
            {
                var concurrent = Interlocked.Increment(ref _concurrent);
                UpdateMax(concurrent);
                if (Interlocked.Increment(ref _showCalls) == Math.Min(expectedShowCalls, SaturationLevel))
                {
                    _allStarted.Set();
                }

                return WaitForReleaseAsync(cancellationToken);
            }

            return Task.FromResult(new ProcessResult(0, listOutput, ""));
        }

        private async Task<ProcessResult> WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref _concurrent);
            return new ProcessResult(0, "Found Complete Package Name [Some.Id]\r\n", "");
        }

        private void UpdateMax(int candidate)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxConcurrent);
                if (candidate <= observed || Interlocked.CompareExchange(ref _maxConcurrent, candidate, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class CapturingProgress : IProgress<ProcessOutput>
    {
        public List<ProcessOutput> Entries { get; } = [];

        public void Report(ProcessOutput value) => Entries.Add(value);
    }
}