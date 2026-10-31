using System.Collections.Generic;
using System.IO;

namespace DKDesktopEssentials.Modules.Downloads;

/// <summary>Persisted state; never infer success merely from an engine process exiting.</summary>
public enum DownloadJobState
{
    Queued, Resolving, Downloading, Paused, Retrying,
    Verifying, Completed, Failed, Cancelled
}

public enum DownloadErrorCode
{
    InvalidUrl, UnsupportedProtocol, DestinationInvalid, DestinationExists,
    PermissionDenied, DiskFull, NetworkUnavailable, Timeout, DnsFailure,
    TlsFailure, HttpUnauthorized, HttpForbidden, HttpNotFound, HttpRateLimited,
    HttpServerError, RangeNotSupported, RemoteContentChanged,
    PartialFileInvalid, ContentLengthMismatch, ChecksumMismatch,
    StorageFailure, EngineUnavailable, UnexpectedFailure, QueueFull
}

public enum DownloadPriority { Low = 0, Normal = 1, High = 2 }
public enum DownloadResumeCapability { Unknown, Supported, Unsupported }

/// <summary>
/// Internal persistence record. SourceKey is an opaque lookup for the protected
/// URL/credentials vault; it is NEVER a URL or a browser-visible secret.
/// </summary>
public sealed record DownloadJobRecord
{
    public required Guid Id { get; init; }
    public required string SourceKey { get; init; }
    public required string DisplayHost { get; init; }
    public required string DestinationPath { get; init; }
    public required string TemporaryPath { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required DateTimeOffset UpdatedUtc { get; init; }
    public DownloadJobState State { get; init; } = DownloadJobState.Queued;
    public DownloadPriority Priority { get; init; } = DownloadPriority.Normal;
    public long TransferredBytes { get; init; }
    public long? ExpectedBytes { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? VerifiedSha256 { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModifiedUtc { get; init; }
    public DateTimeOffset? ResponseDateUtc { get; init; }
    public string? ResourceKey { get; init; }
    public long CheckpointBytes { get; init; }
    public string? CheckpointSha256 { get; init; }
    public int ParallelSegments { get; init; } = 1;
    public DownloadResumeCapability ResumeCapability { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public DownloadErrorCode? LastErrorCode { get; init; }
    public string? SafeErrorDetail { get; init; }
    public long Revision { get; init; }
    public bool DispatchRequested { get; init; } = true;
}

/// <summary>Only this redacted projection may cross the WebView2/UI boundary.</summary>
public sealed record DownloadJobView(
    Guid Id, DownloadJobState State, string DisplayHost, string FileName,
    long TransferredBytes, long? ExpectedBytes, DownloadPriority Priority,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc,
    DownloadErrorCode? ErrorCode, string? SafeErrorDetail, long Revision,
    long BytesPerSecond = 0, double? EtaSeconds = null,
    DownloadResumeCapability ResumeCapability = DownloadResumeCapability.Unknown, bool CanResume = false,
    bool CanRetry = false, int Attempts = 0, DateTimeOffset? NextAttemptUtc = null, int? QueuePosition = null,
    IReadOnlyDictionary<string, bool>? Capabilities = null);

public static class DownloadJobProjection
{
    public static DownloadJobView ToView(DownloadJobRecord job) => new(
        job.Id, job.State, job.DisplayHost,
        Path.GetFileName(job.DestinationPath), job.TransferredBytes,
        job.ExpectedBytes, job.Priority, job.CreatedUtc, job.UpdatedUtc,
        job.LastErrorCode, job.SafeErrorDetail, job.Revision, ResumeCapability: job.ResumeCapability);
}

public sealed record CreateDownloadRequest(
    Uri SourceUrl, string DestinationDirectory, string? PreferredFileName = null,
    DownloadPriority Priority = DownloadPriority.Normal, string? ExpectedSha256 = null, int ParallelSegments = 1, bool StartNow = true)
{
    public override string ToString() => "CreateDownloadRequest { SourceUrl = [redacted] }";
}

public sealed record DownloadQueueSettings(
    int MaxConcurrentDownloads = 3, int MaxAttempts = 5, bool AutoStartOnLaunch = false);

public sealed record DownloadProgress(
    Guid JobId, long TransferredBytes, long? ExpectedBytes, long BytesPerSecond,
    string? CheckpointSha256 = null, bool RangeValidated = false);

public sealed record DownloadProbe(
    long? ContentLength, string? ETag, DateTimeOffset? LastModifiedUtc,
    DownloadResumeCapability ResumeCapability, string? SuggestedFileName,
    DateTimeOffset? ResponseDateUtc = null, string? ResourceKey = null);

public sealed record DownloadSource(Uri AuthorizedUrl)
{
    public override string ToString() => "DownloadSource { AuthorizedUrl = [redacted] }";
}

public sealed record DownloadExecutionContext(
    DownloadJobRecord Job, DownloadSource Source, DownloadProbe Probe, long ResumeOffset,
    bool ResumeExistingFile = false);

public sealed record DownloadExecutionResult(
    long TotalBytesWritten, string? ComputedSha256);

public sealed record DownloadEvent(Guid JobId, DownloadJobState State,
    DateTimeOffset OccurredUtc, DownloadErrorCode? ErrorCode = null);

/// <summary>
/// Transfer engine boundary, suitable for aria2 or another implementation.
/// The engine cannot create jobs or directly change persisted job states.
/// </summary>
public interface IDownloadEngine
{
    Task<DownloadProbe> ProbeAsync(DownloadSource source, CancellationToken cancellationToken);
    Task<DownloadExecutionResult> TransferAsync(
        DownloadExecutionContext context,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken);
}

/// <summary>Storage operations must be transactional and revision-checked.</summary>
public interface IDownloadJobStore
{
    Task<DownloadJobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloadJobRecord>> ListAsync(CancellationToken cancellationToken = default);
    Task AddAsync(DownloadJobRecord job, CancellationToken cancellationToken = default);
    Task<bool> TryUpdateAsync(DownloadJobRecord job, long expectedRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Shared native coordinator surface for the download category.
/// Never accept raw engine commands or arbitrary host filesystem operations from UI.
/// </summary>
public interface IDownloadCoordinator
{
    Task<DownloadJobView> EnqueueAsync(CreateDownloadRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloadJobView>> ListAsync(CancellationToken cancellationToken = default);
    Task<DownloadJobView?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DownloadJobView> PauseAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DownloadJobView> ResumeAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DownloadJobView> RetryAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DownloadJobView> StartAsync(Guid id, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
    Task OpenFolderAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DownloadJobView> CancelAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DownloadQueueSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SetSettingsAsync(DownloadQueueSettings settings, CancellationToken cancellationToken = default);
    IAsyncEnumerable<DownloadEvent> WatchAsync(CancellationToken cancellationToken = default);
}
