namespace Nornia.Core.Models;

public sealed record Runtime(
    Guid Id,
    string Name,
    string Version,
    string InstallPath,
    string Architecture,
    string Provider,
    long InstallDate,
    RuntimeStatus Status,
    DetectionStatus DetectionStatus = DetectionStatus.Installed,
    string? DetectionError = null)
{
    public bool IsBroken => DetectionStatus == DetectionStatus.Broken;
}

public enum RuntimeStatus { Unknown, Installed, Missing, UpdateAvailable, Error }

public enum DetectionStatus
{
    NotInstalled,
    Installed,
    Broken
}

public sealed record RuntimeDetectionResult
{
    public IReadOnlyList<Runtime> InstalledRuntimes { get; init; } = [];
    public IReadOnlyList<RuntimeBrokenInfo> BrokenRuntimes { get; init; } = [];

    public IReadOnlyList<Runtime> AllRuntimes =>
        InstalledRuntimes.Concat(BrokenRuntimes.Select(broken => broken.Runtime)).ToArray();

    public static RuntimeDetectionResult Create(IEnumerable<Runtime> installed, IEnumerable<RuntimeBrokenInfo> broken) =>
        new() { InstalledRuntimes = installed.ToArray(), BrokenRuntimes = broken.ToArray() };

    public static RuntimeDetectionResult None => new();
}

public sealed record RuntimeBrokenInfo(
    Runtime Runtime,
    string ErrorReason,
    long DetectedAt);

public enum RollbackStrategy
{
    Automatic,
    Manual,
    Unsupported
}

public sealed record EnvironmentRepairLogEntry(
    Guid Id,
    long Timestamp,
    Guid CorrelationId,
    int Sequence,
    string Component,
    string PreviousVersion,
    string TargetVersion,
    string PackageId,
    string? PackageVersion,
    string Provider,
    bool Succeeded,
    long CompletedAt,
    RollbackStrategy RollbackStrategy,
    string? FailureReason = null,
    string? RollbackHint = null);

/// <summary>One actionable failure captured while applying an environment repair batch.</summary>
public sealed record EnvironmentRepairFailure(
    int Sequence,
    int Total,
    string Component,
    string PreviousVersion,
    string TargetVersion,
    string PackageId,
    string? PackageVersion,
    string Provider,
    string ExceptionType,
    string FailureReason,
    string? DiagnosticOutput,
    int? ExitCode,
    RollbackStrategy RollbackStrategy,
    string? RollbackHint);

/// <summary>Raised after a repair batch has attempted every step but one or more steps failed.</summary>
public sealed class EnvironmentRepairException : InvalidOperationException
{
    public EnvironmentRepairException(Guid correlationId, IReadOnlyList<EnvironmentRepairFailure> failures)
        : base(BuildMessage(correlationId, failures))
    {
        CorrelationId = correlationId;
        Failures = failures;
    }

    public Guid CorrelationId { get; }
    public IReadOnlyList<EnvironmentRepairFailure> Failures { get; }

    private static string BuildMessage(Guid correlationId, IReadOnlyList<EnvironmentRepairFailure> failures) =>
        $"环境修复失败：{failures.Count} 个步骤失败（correlation_id={correlationId:N}）。";
}

public sealed record EnvironmentRollbackPlan(
    Guid CorrelationId,
    long SourceTimestamp,
    IReadOnlyList<EnvironmentRollbackAction> Actions)
{
    public bool RequiresManualIntervention =>
        Actions.Any(action => action.Strategy != RollbackStrategy.Automatic);
}

public sealed record EnvironmentRollbackAction(
    int OriginalSequence,
    string Component,
    string FromVersion,
    string ToVersion,
    string PackageId,
    string? PackageVersion,
    RollbackStrategy Strategy,
    string? Hint = null);

/// <summary>Lightweight reference from the Dashboard to a project that requires attention; used so a
/// metric-card "Go to details" click can jump straight to the right project instead of the page root.</summary>
public sealed record DashboardProjectReference(
    Guid ProjectId,
    string ProjectName,
    string ProjectPath,
    EnvironmentHealthStatus Status);

/// <summary>Aggregated dashboard counters read from the database in a single query, plus the first few
/// entries that back the "查看详情" quick-navigation shortcuts.</summary>
public sealed record DashboardSummary(
    int ProjectCount,
    int EnvironmentIssueCount,
    int AvailableUpdateCount)
{
    public IReadOnlyList<DashboardProjectReference> ProjectsNeedingAttention { get; init; } = [];
}

public enum CacheConfidence
{
    Review,
    High
}

public sealed record CacheCandidate(
    string Id,
    string Source,
    string Path,
    long SizeBytes,
    CacheConfidence Confidence,
    string Reason,
    string? PackageId = null,
    string? PackageName = null,
    string? PackageProvider = null,
    string? CacheType = null,
    string? UserDirectory = null)
{
    public bool IsRecommended => Confidence == CacheConfidence.High;
    public string PackageDisplayName => PackageName ?? "未关联的软件缓存";
    public string CacheTypeDisplay => CacheType ?? "其他";
}

/// <summary>Cache-type dimension within a package summary: how many candidates and how much space a
/// single tool ecosystem (npm, nuget, gradle, …) contributes to the package.</summary>
public sealed record CacheTypeCount(string Type, int Count, long SizeBytes);

public sealed record CachePackageSummary(
    string PackageName,
    string? PackageId,
    string? Provider,
    long SizeBytes,
    int CandidateCount,
    CacheConfidence Confidence,
    IReadOnlyList<CacheTypeCount> CacheTypes);

public enum CacheCleanupStatus
{
    Cleaned,
    Skipped,
    Failed
}

public sealed record CacheCleanupResult(
    string Id,
    string Path,
    CacheCleanupStatus Status,
    long ReclaimedBytes,
    string Message);

/// <summary>Snapshot-level metadata for one persisted inventory scan (kind "runtimes" / "packages").
/// The timestamp plus the cheap environment fingerprint let inventory services serve a fresh snapshot
/// from the database without re-running any provider process or winget.</summary>
public sealed record InventoryScanState(
    string Kind,
    long ScannedAt,
    long DurationMs,
    string Fingerprint);
