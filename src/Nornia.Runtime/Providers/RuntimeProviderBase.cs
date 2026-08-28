using CoreRuntime = Nornia.Core.Models.Runtime;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using System.Security.Cryptography;
using System.Text;

namespace Nornia.Runtime.Providers;

public abstract class RuntimeProviderBase(string name, IProcessRunner processRunner) : IRuntimeProvider
{
    public string Name { get; } = name;
    protected IProcessRunner ProcessRunner { get; } = processRunner;

    public abstract Task<RuntimeDetectionResult> DetectAsync(CancellationToken cancellationToken = default);

    public async Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        var result = await DetectAsync(cancellationToken);
        return result.InstalledRuntimes
            .Select(runtime => runtime.Version)
            .Concat(result.BrokenRuntimes.Select(b => b.Runtime.Version))
            .Distinct()
            .ToArray();
    }

    public virtual Task InstallAsync(string version, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Use a package provider to install {Name} {version}.");

    public virtual Task RemoveAsync(string version, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Use a package provider to remove {Name} {version}.");

    public virtual Task UpdateAsync(string version, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Use a package provider to update {Name} {version}.");

    protected static RuntimeBrokenInfo CreateBroken(CoreRuntime runtime, string reason) =>
        new(runtime with { DetectionStatus = DetectionStatus.Broken, DetectionError = reason, Status = RuntimeStatus.Error },
            reason, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}

internal static class RuntimeIdentity
{
    public static Guid Create(string name, string version, string path)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{name}|{version}|{path}".ToUpperInvariant()));
        return new Guid(hash);
    }

    public static long GetInstallTimestamp(string path)
    {
        try
        {
            var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
            {
                return new DateTimeOffset(Directory.GetCreationTimeUtc(target)).ToUnixTimeSeconds();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Discovery should still succeed when file metadata cannot be read.
        }

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}