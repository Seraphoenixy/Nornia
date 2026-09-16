using Nornia.Core.Models;
using Nornia.Package.Services;

namespace Nornia.Tests;

public sealed class AppDataCacheServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nornia-cache-{Guid.NewGuid():N}");

    [Fact]
    public async Task ScanAsync_DetectsUserRootAndKnownDevelopmentCaches()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".cache"));
        Directory.CreateDirectory(Path.Combine(_root, ".nuget", "packages"));
        Directory.CreateDirectory(Path.Combine(_root, ".gradle", "caches"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".cache", "item.bin"), "cache-data");

        var candidates = await new AppDataCacheService([_root]).ScanAsync();

        Assert.Contains(candidates, candidate => candidate.Path == Path.Combine(_root, ".cache") && candidate.Confidence == CacheConfidence.High);
        var nuget = Assert.Single(candidates, candidate => candidate.Path == Path.Combine(_root, ".nuget", "packages"));
        Assert.Equal(CacheConfidence.High, nuget.Confidence);
        Assert.Equal("nuget", nuget.CacheType);
        Assert.Equal(Path.GetFullPath(_root), nuget.UserDirectory);
        var gradle = Assert.Single(candidates, candidate => candidate.Path == Path.Combine(_root, ".gradle", "caches"));
        Assert.Equal(CacheConfidence.High, gradle.Confidence);
        Assert.Equal("gradle", gradle.CacheType);
        Assert.Equal(Path.GetFullPath(_root), gradle.UserDirectory);
        Assert.Equal("其他", Assert.Single(candidates, candidate => candidate.Path == Path.Combine(_root, ".cache")).CacheType);
    }

    [Fact]
    public async Task CleanAsync_ClearsContentButKeepsUserCacheRoot()
    {
        var cachePath = Path.Combine(_root, ".npm");
        Directory.CreateDirectory(cachePath);
        await File.WriteAllTextAsync(Path.Combine(cachePath, "archive.bin"), "downloaded-package");
        var service = new AppDataCacheService([_root]);
        var candidate = Assert.Single(await service.ScanAsync());

        var result = Assert.Single(await service.CleanAsync([candidate.Id]));

        Assert.Equal(CacheCleanupStatus.Cleaned, result.Status);
        Assert.True(Directory.Exists(cachePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cachePath));
    }

    [Fact]
    public async Task ConcurrentScans_ShareOneInFlightWalk()
    {
        var cachePath = Path.Combine(_root, ".cache");
        Directory.CreateDirectory(cachePath);
        await File.WriteAllTextAsync(Path.Combine(cachePath, "item.bin"), "cache-data");
        var service = new AppDataCacheService([_root]);

        var scans = await Task.WhenAll(
            service.ScanForcedAsync(),
            service.ScanForcedAsync(),
            service.ScanAsync());

        Assert.Same(scans[0], scans[1]);
        Assert.Same(scans[1], scans[2]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
