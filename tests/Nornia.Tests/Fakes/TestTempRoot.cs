using System.Diagnostics;

namespace Nornia.Tests.Fakes;

/// <summary>测试进程共享的临时目录:测试进程产生的临时文件统一收敛到
/// <c>%TEMP%\nornia-tests-pid-{pid}\</c> 下,进程退出时整体尽力清理。
/// 此前 FakeSettingsService 等直接在 %TEMP% 根下创建 GUID 文件且从不删除——
/// 单日十余轮全量回归积累 5000+ 孤儿文件,拖慢文件 IO 并触发杀毒实时扫描,
/// 设置应用链(settings commit → watch → snapshot 读)因此偶发超出等待预算
/// (SettingsLiveEffectTests 等),文件替换偶发被锁(无法删除要被替换的文件)。</summary>
internal static class TestTempRoot
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), $"nornia-tests-pid-{Environment.ProcessId}");

    static TestTempRoot()
    {
        Directory.CreateDirectory(Root);
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* 尽力而为:退出阶段清理失败不影响测试结果 */ }
        };
    }

    public static string NewDirectory(string prefix)
    {
        var path = Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string NewFile(string prefix, string extension = ".jsonc") =>
        Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}{extension}");
}
