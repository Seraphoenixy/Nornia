using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Git;

namespace Nornia.Tests;

/// <summary>G1/P1 证据:一次 git 状态读取的整条链路(启动子进程 → 等输出 → 解析建模)不得占着
/// 调用方线程,也不得把续延排回调用方上下文。Desktop 从 dispatcher 调用它:同步执行段就是"打开
/// 工作区整个界面一起卡"的成因;而排回 dispatcher 的续延会撞上绑定集合的线程亲和(跨线程改
/// SourceCollection),两条都必须是可断言的红线。</summary>
public sealed class GitStatusThreadingTests
{
    private const string Porcelain = "# branch.head main\0# branch.oid abc123\01 .M N... 100644 100644 100644 0 0 src/A.cs\0";

    [Fact]
    public void GetStatusAsync_KeepsTheCallersThreadOutOfTheGitRead()
    {
        var runner = new DelayedProcessRunner(Porcelain);
        var service = new GitService(runner);
        var context = new RecordingSynchronizationContext();
        int? callerThreadId = null;
        GitRepositoryStatus? status = null;
        Exception? failure = null;

        // 专用调用线程扮演 dispatcher:它发起读取后阻塞等待结果,因此这条线程不可能被线程池
        // 复用成"执行子进程启动的那条线程"——两次线程 id 相同就是真的同步执行。
        var caller = new Thread(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                status = service.GetStatusAsync(@"C:\repo").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(30)), "git 状态读取没有完成。");

        Assert.Null(failure);
        Assert.NotNull(status);
        Assert.True(status!.IsRepository);
        Assert.Equal("main", status.Branch);

        var readThreadId = runner.ExecutionThreadId;
        Assert.True(readThreadId is not null, "替身没有被调用。");
        Assert.NotEqual(callerThreadId!.Value, readThreadId!.Value);
        Assert.Equal(0, context.PostCount);
    }

    /// <summary>Returns an as-yet-incomplete task, so every await in the pipeline has a real
    /// continuation to resume — the point where a missing ConfigureAwait(false) shows up.</summary>
    private sealed class DelayedProcessRunner(string stdout) : IProcessRunner
    {
        public int? ExecutionThreadId { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IProgress<ProcessOutput>? progress = null,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            int? maximumOutputBytes = null)
        {
            ExecutionThreadId = Environment.CurrentManagedThreadId;
            var completion = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() => completion.TrySetResult(new ProcessResult(0, stdout, string.Empty)))
            {
                IsBackground = true,
            }.Start();
            return completion.Task;
        }
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _postCount);
            // 直接内联执行:真实 dispatcher 会排队,这里的目的是"有没有被排回",不是重演调度。
            callback(state);
        }
    }
}
