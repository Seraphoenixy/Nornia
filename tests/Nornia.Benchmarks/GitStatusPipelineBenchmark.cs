using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Nornia.Core.Models;
using Nornia.Core.Services;
using Nornia.Git;
using Nornia.Git.Parsing;

namespace Nornia.Benchmarks;

/// <summary>病态工作区的 git status 整链路量尺(P0 复现证据):子进程有界读取 → 解析建模 →
/// 状态对象。仓库形态与 <c>tools/heavy-git-repo.ps1</c> 一致(海量未跟踪 + 少量已修改)。
/// 关注两件事:读取上限命中时的固定成本,以及条目上限(首屏降级)省下多少解析与分配。
///
/// 本仓库全部工程都是 <c>net10.0-windows</c>,BenchmarkDotNet 生成的脚手架工程会把 TFM 降成
/// <c>net10.0</c> 并还原失败(所有基准都受影响),所以要在进程内跑:
///   dotnet run -c Release --project tests/Nornia.Benchmarks -- --filter "*GitStatusPipelineBenchmark*" --job dry --inProcess true</summary>
[MemoryDiagnoser]
public class GitStatusPipelineBenchmark
{
    /// <summary>首屏降级用的条目上限,与 GitViewModel.StatusEntryLimit 的量级一致。</summary>
    private const int FirstPaintEntryLimit = 1_000;

    private const int ModifiedCount = 200;

    [Params(2_000, 10_000)]
    public int UntrackedCount { get; set; }

    private string _repositoryPath = string.Empty;
    private GitService _gitService = null!;
    private string _retainedOutput = string.Empty;

    /// <summary>必须是同步方法:BenchmarkDotNet 的 in-process 工具链用反射调用 GlobalSetup 且
    /// 不等待返回的 Task,异步 setup 会在第一个 await 处就交回控制权,基准方法于是跑在半建仓上。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _repositoryPath = Path.Combine(Path.GetTempPath(), $"nornia-bench-scm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repositoryPath);

        for (var i = 0; i < ModifiedCount; i++)
        {
            Write(Path.Combine(_repositoryPath, "src", "modified", $"modified-{i:D5}.txt"), "original");
        }

        RunGit("init");
        RunGit("config", "user.name", "Nornia Benchmark");
        RunGit("config", "user.email", "benchmark@nornia.local");
        RunGit("add", "src");
        RunGit("commit", "-m", "baseline");

        for (var i = 0; i < ModifiedCount; i++)
        {
            Write(Path.Combine(_repositoryPath, "src", "modified", $"modified-{i:D5}.txt"), $"changed {i}");
        }

        for (var i = 0; i < UntrackedCount; i++)
        {
            // 每 200 个文件一个目录:资源管理器与更改树都要按目录折叠,单目录堆一万条不是真实形态。
            Write(
                Path.Combine(_repositoryPath, "untracked", $"d{i / 200:D4}", $"file-{i:D6}.txt"),
                "u");
        }

        _gitService = new GitService(new ProcessRunner());
        // 解析基准要吃「真实读取留下的那段 stdout」,不是状态对象的文本表示。
        _retainedOutput = new ProcessRunner().RunBoundedAsync(
            "git",
            ["-C", _repositoryPath, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"],
            maximumOutputBytes: 16 * 1024 * 1024,
            cancellationToken: CancellationToken.None).GetAwaiter().GetResult().StandardOutput;
        if (_retainedOutput.Length == 0)
        {
            throw new InvalidOperationException("复现仓库没有产出任何 porcelain 记录,基准会退化成空转。");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            // git 把对象库里的文件写成只读,直接递归删除会撞 UnauthorizedAccessException。
            foreach (var file in Directory.EnumerateFiles(_repositoryPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repositoryPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 基准收尾删不掉临时目录不影响结论,留给系统清理。
        }
    }

    /// <summary>全量首屏:读取 + 解析 + 建模(上限为 StatusMaximumEntries)。基准里同步阻塞等待:
    /// 成本几乎全在子进程与解析上,续延调度的差异不在本量尺的关注点内。</summary>
    [Benchmark(Baseline = true)]
    public GitRepositoryStatusModel FullRead() =>
        Summarize(_gitService.GetStatusAsync(_repositoryPath, CancellationToken.None)
            .GetAwaiter().GetResult());

    /// <summary>首屏降级(小上限):上限命中让解析与建模提前收尾,省下的正是这段。</summary>
    [Benchmark]
    public GitRepositoryStatusModel FirstPaintRead() =>
        Summarize(_gitService.GetStatusAsync(_repositoryPath, FirstPaintEntryLimit, CancellationToken.None)
            .GetAwaiter().GetResult());

    /// <summary>纯解析(不含子进程):把「读」与「解析建模」的成本分开,定位瓶颈在哪一侧。</summary>
    [Benchmark]
    public GitRepositoryStatusModel ParseOnly() =>
        Summarize(GitStatusParser.Parse(_retainedOutput, GitStatusParser.DefaultMaxEntries));

    /// <summary>纯解析,首屏上限。</summary>
    [Benchmark]
    public GitRepositoryStatusModel ParseOnlyFirstPaintLimit() =>
        Summarize(GitStatusParser.Parse(_retainedOutput, FirstPaintEntryLimit));

    private static GitRepositoryStatusModel Summarize(GitRepositoryStatus status) =>
        new(status.UnstagedChanges.Count + status.StagedChanges.Count, status.Truncated);

    private void Write(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }

    private void RunGit(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} 失败(退出码 {process.ExitCode}):{stderr}{stdout}");
        }
    }

    public readonly record struct GitRepositoryStatusModel(int Entries, bool Truncated);
}
