using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace DKDesktopEssentials.Modules.Downloads;

/// <summary>Single queue owner: persisted intent, bounded dispatch and validated retries.</summary>
public sealed class DownloadCoordinator : IDownloadCoordinator, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IDownloadEngine _engine;
    private readonly Dictionary<Guid, Entry> _jobs = new();
    private readonly HashSet<Channel<DownloadEvent>> _watchers = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _scheduler;
    private readonly TimeProvider _time;
    private readonly Func<double> _random;
    private readonly Func<string, long> _availableSpace;
    private Func<string, Task> _openFolder = DownloadFolderOpener.OpenAsync;
    private IDownloadPersistence? _store;
    private DownloadQueueSettings _settings = new();
    private bool _stopping;
    private Task? _shutdown;
    public bool RecoveryBlocked { get; private set; }

    public DownloadCoordinator() : this(new HttpDownloadEngine())
    {
        lock (_gate)
        {
            try
            {
                _store = new DownloadJobStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DKDesktopEssentials", "Downloads"));
                Restore();
            }
            catch { RecoveryBlocked = true; _store?.Dispose(); _store = null; }
        }
    }
    public DownloadCoordinator(IDownloadEngine engine) : this(engine, TimeProvider.System, Random.Shared.NextDouble, DownloadDiskSpace.Available) { }
    private DownloadCoordinator(IDownloadEngine engine, TimeProvider time, Func<double> random, Func<string, long> available)
    {
        _engine = engine; _time = time; _random = random; _availableSpace = available;
        _scheduler = Task.Run(ScheduleAsync);
    }
    internal DownloadCoordinator(IDownloadEngine engine, IDownloadPersistence store, TimeProvider? time = null, Func<double>? random = null, Func<string, long>? availableSpace = null, Func<string, Task>? openFolder = null)
        : this(engine, time ?? TimeProvider.System, random ?? Random.Shared.NextDouble, availableSpace ?? DownloadDiskSpace.Available)
    {
        lock (_gate)
        {
            _openFolder = openFolder ?? DownloadFolderOpener.OpenAsync;
            _store = store;
            try { Restore(); }
            catch { RecoveryBlocked = true; store.Dispose(); _store = null; }
        }
    }
    private void Restore()
    {
        _settings = _store!.LoadSettings(); DownloadQueuePolicy.Validate(_settings);
        foreach (var saved in _store.Load())
        {
            var job = saved.Job with { TransferredBytes = saved.Job.CheckpointBytes, NextAttemptUtc = null };
            if (DownloadStateMachine.RequiresStartupRecovery(job.State))
            {
                try
                {
                    job = DownloadStagingFile.ReconcilePromotion(job)
                        ? DownloadStateMachine.Advance(job, DownloadJobState.Completed, DownloadTransitionCause.VerificationSucceeded, Now(job))
                        : DownloadStateMachine.Advance(job, DownloadJobState.Queued, DownloadTransitionCause.StartupRecovery, Now(job));
                }
                catch (DownloadEngineException ex)
                {
                    job = DownloadStateMachine.Advance(job with { LastErrorCode = ex.Code, SafeErrorDetail = ex.Message }, DownloadJobState.Failed, DownloadTransitionCause.FatalFailure, Now(job));
                }
            }
            var entry = new Entry(job, new(new Uri(saved.AuthorizedUrl))) { StoredRevision = saved.Job.Revision };
            entry.Job = Save(entry, job);
            entry.Ready = _settings.AutoStartOnLaunch && job.DispatchRequested && job.State == DownloadJobState.Queued && job.Attempts < _settings.MaxAttempts;
            _jobs.Add(job.Id, entry);
        }
        Pulse();
    }
    private DownloadJobRecord Save(Entry entry, DownloadJobRecord job)
    {
        if (_store is null) return job;
        job = job with { Revision = Math.Max(job.Revision, checked(entry.StoredRevision + 1)), UpdatedUtc = Now(job) };
        try
        {
            _store.Save(job with { TransferredBytes = job.CheckpointBytes }, entry.Source, entry.StoredRevision);
            entry.StoredRevision = job.Revision; return job;
        }
        catch
        {
            BlockRecovery();
            throw new DownloadEngineException(DownloadErrorCode.StorageFailure);
        }
    }
    private void BlockRecovery()
    {
        RecoveryBlocked = true;
        foreach (var entry in _jobs.Values.Where(e => e.Active))
        {
            entry.Job = entry.Job with { LastErrorCode = DownloadErrorCode.StorageFailure, SafeErrorDetail = new DownloadEngineException(DownloadErrorCode.StorageFailure).Message };
            if (!entry.Stop.IsCancellationRequested) entry.PauseRequested = true;
            entry.Stop.Cancel();
        }
        Pulse();
    }
    private void Available()
    {
        if (_stopping) throw new DownloadEngineException(DownloadErrorCode.EngineUnavailable);
        if (RecoveryBlocked) throw new DownloadEngineException(DownloadErrorCode.StorageFailure);
    }
    private void Reserve(DownloadJobRecord job)
    {
        DownloadStagingFile.ValidateDestination(job);
        if (_jobs.Values.Any(e => e.Job.Id != job.Id && e.Job.State is not (DownloadJobState.Completed or DownloadJobState.Cancelled or DownloadJobState.Failed) &&
            string.Equals(e.Job.DestinationPath, job.DestinationPath, StringComparison.OrdinalIgnoreCase))) throw new DownloadEngineException(DownloadErrorCode.DestinationExists);
    }
    public Task<DownloadJobView> EnqueueAsync(CreateDownloadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = HttpDownloadEngine.ValidateSource(new(request.SourceUrl));
        var name = request.PreferredFileName ?? "download.bin";
        if (!DownloadStagingFile.IsSafeFileName(name) || !Enum.IsDefined(request.Priority) || request.ParallelSegments is not (1 or 2 or 4)) throw new DownloadEngineException(DownloadErrorCode.DestinationInvalid);
        if (request.ExpectedSha256 is { } sha && (sha.Length != 64 || !sha.All(Uri.IsHexDigit))) throw new DownloadEngineException(DownloadErrorCode.ChecksumMismatch);
        var id = Guid.NewGuid(); var now = _time.GetUtcNow();
        DownloadJobRecord job;
        try
        {
            job = new() { Id = id, SourceKey = Guid.NewGuid().ToString("N"), DisplayHost = uri.IdnHost,
                DestinationPath = Path.Combine(request.DestinationDirectory, name), TemporaryPath = Path.Combine(request.DestinationDirectory, $".{id:N}.dkdownload.part"),
                CreatedUtc = now, UpdatedUtc = now, Priority = request.Priority, ExpectedSha256 = request.ExpectedSha256, ParallelSegments = request.ParallelSegments, DispatchRequested = request.StartNow,
                CheckpointSha256 = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant() };
        }
        catch { throw new DownloadEngineException(DownloadErrorCode.DestinationInvalid); }
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested(); Available(); Reserve(job);
            var retired = _jobs.Values.Where(e => !e.Active && e.Job.State is DownloadJobState.Completed or DownloadJobState.Cancelled).OrderBy(e => e.Job.CreatedUtc).FirstOrDefault();
            if (_jobs.Count >= 100 && retired is not null)
            {
                try { _store?.Forget(retired.Job.Id); }
                catch { BlockRecovery(); throw new DownloadEngineException(DownloadErrorCode.StorageFailure); }
                retired.Stop.Dispose(); _jobs.Remove(retired.Job.Id);
            }
            if (_jobs.Count >= 1000) throw new DownloadEngineException(DownloadErrorCode.QueueFull);
            var entry = new Entry(job, new(uri)) { Ready = request.StartNow };
            entry.Job = Save(entry, job); _jobs.Add(id, entry); Emit(entry.Job); Pulse(); return Task.FromResult(View(entry));
        }
    }
    private void Pulse() => _wake.Writer.TryWrite(true);
    private async Task ScheduleAsync()
    {
        try
        {
            while (true)
            {
                TimeSpan? delay = null;
                lock (_gate)
                {
                    if (_stopping) return;
                    if (!RecoveryBlocked)
                    {
                        var now = _time.GetUtcNow();
                        foreach (var exhausted in _jobs.Values.Where(e => e.Ready && !e.Active && e.Job.State == DownloadJobState.Retrying && e.Job.Attempts >= _settings.MaxAttempts).ToArray())
                        {
                            exhausted.Ready = false; exhausted.Job = exhausted.Job with { NextAttemptUtc = null };
                            Finish(exhausted, DownloadJobState.Failed, DownloadTransitionCause.FatalFailure);
                        }
                        if (RecoveryBlocked) continue;
                        var slots = Math.Max(0, _settings.MaxConcurrentDownloads - _jobs.Values.Count(e => e.Active));
                        var candidates = DownloadQueuePolicy.Order(_jobs.Values.Where(e => e.Ready && !e.Active &&
                            (e.Job.State == DownloadJobState.Queued || e.Job.State == DownloadJobState.Retrying && e.Job.NextAttemptUtc <= now)).Select(e => e.Job));
                        foreach (var job in candidates.Take(slots).ToArray())
                        {
                            var entry = _jobs[job.Id]; entry.Active = true;
                            entry.Stop.Dispose(); entry.Stop = new(); entry.PauseRequested = false; entry.Work = Task.Run(() => RunAsync(entry));
                        }
                        if (_jobs.Values.Count(e => e.Active) < _settings.MaxConcurrentDownloads)
                        {
                            var due = _jobs.Values.Where(e => e.Ready && !e.Active && e.Job.State == DownloadJobState.Retrying).Select(e => e.Job.NextAttemptUtc).Min();
                            if (due is { } instant) delay = instant > now ? instant - now : TimeSpan.Zero;
                        }
                    }
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var signal = _wake.Reader.ReadAsync(wait.Token).AsTask();
                if (delay is { } duration)
                {
                    var timer = Task.Delay(duration, _time, wait.Token); await Task.WhenAny(signal, timer).ConfigureAwait(false); wait.Cancel();
                    try { await signal.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    try { await timer.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                else await signal.ConfigureAwait(false);
                _lifetime.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    private void CheckCapacity(Entry entry)
    {
        var root = Path.GetPathRoot(entry.Job.DestinationPath);
        var reserved = _jobs.Values.Where(e => e.Active && e.Job.Id != entry.Job.Id && string.Equals(Path.GetPathRoot(e.Job.DestinationPath), root, StringComparison.OrdinalIgnoreCase))
            .Aggregate(0L, (sum, e) => checked(sum + Math.Max(0, (e.Job.ExpectedBytes ?? e.Job.TransferredBytes) - e.Job.TransferredBytes) + DownloadDiskSpace.Headroom));
        var required = checked(Math.Max(0, (entry.Job.ExpectedBytes ?? entry.Job.CheckpointBytes) - entry.Job.CheckpointBytes) + DownloadDiskSpace.Headroom);
        if (_availableSpace(Path.GetDirectoryName(entry.Job.DestinationPath)!) < checked(required + reserved)) throw new DownloadEngineException(DownloadErrorCode.DiskFull);
    }
    private async Task RunAsync(Entry entry)
    {
        var token = entry.Stop.Token;
        try
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested(); Reserve(entry.Job);
                if (entry.Job.State == DownloadJobState.Retrying && entry.Job.Attempts >= _settings.MaxAttempts)
                { entry.Ready = false; entry.Job = entry.Job with { NextAttemptUtc = null }; Advance(entry, DownloadJobState.Failed, DownloadTransitionCause.FatalFailure); return; }
                entry.Job = entry.Job with { Attempts = checked(entry.Job.Attempts + 1), NextAttemptUtc = null, TransferredBytes = entry.Job.CheckpointBytes, LastErrorCode = null, SafeErrorDetail = null };
                Advance(entry, DownloadJobState.Resolving, DownloadTransitionCause.SchedulerDispatch);
            }
            var probe = await _engine.ProbeAsync(entry.Source, token).ConfigureAwait(false);
            DownloadExecutionContext context;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Job.CheckpointBytes > 0) DownloadRemoteIdentity.CheckResume(entry.Job, probe);
                entry.Job = entry.Job with { ExpectedBytes = probe.ContentLength, ETag = probe.ETag, LastModifiedUtc = probe.LastModifiedUtc,
                    ResponseDateUtc = probe.ResponseDateUtc, ResourceKey = probe.ResourceKey, ResumeCapability = probe.ResumeCapability };
                CheckCapacity(entry); Advance(entry, DownloadJobState.Downloading, DownloadTransitionCause.ProbeSucceeded);
                context = new(entry.Job, entry.Source, probe, entry.Job.CheckpointBytes, Path.Exists(entry.Job.TemporaryPath));
            }
            var result = await _engine.TransferAsync(context, new InlineProgress(p => Report(entry, p)), token).ConfigureAwait(false);
            lock (_gate) { token.ThrowIfCancellationRequested(); entry.Job = entry.Job with { TransferredBytes = result.TotalBytesWritten }; Advance(entry, DownloadJobState.Verifying, DownloadTransitionCause.TransferFinished); }
            using var verified = DownloadStagingFile.Verify(entry.Job, result, token);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                entry.Job = Save(entry, entry.Job with { CheckpointBytes = result.TotalBytesWritten, CheckpointSha256 = verified.VerifiedSha256, VerifiedSha256 = verified.VerifiedSha256 });
                verified.Promote(entry.Job);
                entry.Job = entry.Job with { TransferredBytes = result.TotalBytesWritten, ExpectedBytes = entry.Job.ExpectedBytes ?? result.TotalBytesWritten };
                entry.Speed = 0; entry.Ready = false; Advance(entry, DownloadJobState.Completed, DownloadTransitionCause.VerificationSucceeded);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                entry.Speed = 0;
                if (!DownloadStateMachine.IsTerminal(entry.Job.State))
                {
                    if (token.IsCancellationRequested)
                    {
                        entry.Ready = false; entry.Job = entry.Job with { NextAttemptUtc = null, DispatchRequested = false };
                        Finish(entry, entry.PauseRequested ? DownloadJobState.Paused : DownloadJobState.Cancelled, entry.PauseRequested ? DownloadTransitionCause.UserPause : DownloadTransitionCause.UserCancel);
                    }
                    else
                    {
                        var code = ex switch { DownloadEngineException error => error.Code, UnauthorizedAccessException => DownloadErrorCode.PermissionDenied,
                            IOException io when (io.HResult & 0xffff) is 39 or 112 => DownloadErrorCode.DiskFull, IOException => DownloadErrorCode.StorageFailure, _ => DownloadErrorCode.UnexpectedFailure };
                        var after = (ex as DownloadEngineException)?.RetryAfter;
                        var retry = !RecoveryBlocked && entry.Job.State is DownloadJobState.Resolving or DownloadJobState.Downloading && DownloadQueuePolicy.Retryable(code) &&
                            entry.Job.Attempts < _settings.MaxAttempts && after is not { TotalDays: > 1 } && (entry.Job.CheckpointBytes == 0 || entry.Job.ExpectedBytes is not null &&
                                DownloadRemoteIdentity.IfRange(entry.Job.ETag, entry.Job.LastModifiedUtc, entry.Job.ResponseDateUtc) is not null);
                        entry.Ready = retry;
                        entry.Job = entry.Job with { LastErrorCode = code, SafeErrorDetail = new DownloadEngineException(code).Message,
                            ResumeCapability = code is DownloadErrorCode.RangeNotSupported or DownloadErrorCode.RemoteContentChanged or DownloadErrorCode.PartialFileInvalid ? DownloadResumeCapability.Unsupported : entry.Job.ResumeCapability,
                            NextAttemptUtc = retry ? _time.GetUtcNow() + DownloadQueuePolicy.Backoff(entry.Job.Attempts, _random(), after) : null,
                            TransferredBytes = retry ? entry.Job.CheckpointBytes : entry.Job.TransferredBytes };
                        Finish(entry, retry ? DownloadJobState.Retrying : DownloadJobState.Failed, retry ? DownloadTransitionCause.RetryableFailure : DownloadTransitionCause.FatalFailure);
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (token.IsCancellationRequested)
                {
                    var target = entry.PauseRequested ? DownloadJobState.Paused : DownloadJobState.Cancelled;
                    var cause = entry.PauseRequested ? DownloadTransitionCause.UserPause : DownloadTransitionCause.UserCancel;
                    if (DownloadStateMachine.CanTransition(entry.Job.State, target, cause))
                    { entry.Ready = false; entry.Job = entry.Job with { NextAttemptUtc = null, DispatchRequested = false }; Finish(entry, target, cause); }
                }
                entry.Active = false; Pulse();
            }
        }
    }
    private void Finish(Entry entry, DownloadJobState state, DownloadTransitionCause cause)
    {
        try { Advance(entry, state, cause); }
        catch (DownloadEngineException)
        {
            entry.Ready = false;
            entry.Job = DownloadStateMachine.Advance(entry.Job with { LastErrorCode = DownloadErrorCode.StorageFailure, SafeErrorDetail = new DownloadEngineException(DownloadErrorCode.StorageFailure).Message }, state, cause, Now(entry.Job)); Emit(entry.Job);
        }
    }
    private void Report(Entry entry, DownloadProgress progress)
    {
        lock (_gate)
        {
            if (entry.Job.State != DownloadJobState.Downloading || progress.JobId != entry.Job.Id || progress.TransferredBytes < entry.Job.TransferredBytes || progress.ExpectedBytes < 0) return;
            entry.Speed = Math.Max(0, progress.BytesPerSecond);
            entry.Job = entry.Job with { TransferredBytes = progress.TransferredBytes, ExpectedBytes = progress.ExpectedBytes ?? entry.Job.ExpectedBytes,
                ResumeCapability = progress.RangeValidated ? DownloadResumeCapability.Supported : entry.Job.ResumeCapability, Revision = checked(entry.Job.Revision + 1), UpdatedUtc = Now(entry.Job) };
            if (progress.CheckpointSha256 is { } digest)
            {
                if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new DownloadEngineException(DownloadErrorCode.PartialFileInvalid);
                entry.Job = Save(entry, entry.Job with { CheckpointBytes = progress.TransferredBytes, CheckpointSha256 = digest });
            }
        }
    }
    private DateTimeOffset Now(DownloadJobRecord job) => _time.GetUtcNow() > job.UpdatedUtc ? _time.GetUtcNow() : job.UpdatedUtc;
    private void Advance(Entry entry, DownloadJobState state, DownloadTransitionCause cause)
    {
        var next = DownloadStateMachine.Advance(entry.Job, state, cause, Now(entry.Job));
        try { next = Save(entry, next); }
        catch (DownloadEngineException) when (state == DownloadJobState.Completed) { /* Rename succeeded; startup rechecks the Verifying record. */ }
        entry.Job = next; Emit(next);
    }
    private void Emit(DownloadJobRecord job) { foreach (var channel in _watchers) channel.Writer.TryWrite(new(job.Id, job.State, job.UpdatedUtc, job.LastErrorCode)); }
    private DownloadJobView View(Entry entry, IReadOnlyDictionary<Guid, int>? positions = null)
    {
        double? eta = entry.Job.State == DownloadJobState.Downloading && entry.Speed > 0 && entry.Job.ExpectedBytes is { } size ? Math.Max(0, size - entry.Job.TransferredBytes) / (double)entry.Speed : null;
        int? position = null;
        if (positions is not null) { if (positions.TryGetValue(entry.Job.Id, out var rank)) position = rank; }
        else if (entry.Ready && !entry.Active && entry.Job.State == DownloadJobState.Queued)
            position = Array.FindIndex(Queued(), job => job.Id == entry.Job.Id) + 1;
        var writable = !_stopping && !RecoveryBlocked;
        var continuation = CanContinue(entry.Job);
        var caps = new Dictionary<string, bool>
        {
            ["start"] = writable && !entry.Active && !entry.Ready && entry.Job.State == DownloadJobState.Queued && continuation,
            ["pause"] = writable && entry.Job.State is DownloadJobState.Queued or DownloadJobState.Resolving or DownloadJobState.Downloading or DownloadJobState.Retrying or DownloadJobState.Verifying,
            ["resume"] = writable && !entry.Active && entry.Job.State == DownloadJobState.Paused && continuation,
            ["retry"] = writable && !entry.Active && entry.Job.State == DownloadJobState.Failed && continuation && !Path.Exists(entry.Job.DestinationPath),
            ["cancel"] = writable && !DownloadStateMachine.IsTerminal(entry.Job.State),
            ["remove"] = writable && !entry.Active,
            ["openFolder"] = writable && entry.Job.State == DownloadJobState.Completed && entry.Job.VerifiedSha256 is not null && File.Exists(entry.Job.DestinationPath)
        };
        return DownloadJobProjection.ToView(entry.Job) with { BytesPerSecond = entry.Speed, EtaSeconds = eta,
            CanResume = !entry.Active && continuation && (entry.Job.State == DownloadJobState.Paused || entry.Job.State == DownloadJobState.Queued && !entry.Ready),
            CanRetry = caps["retry"], Attempts = entry.Job.Attempts,
            ResumeCapability = entry.Job.CheckpointBytes == 0 && continuation ? DownloadResumeCapability.Unknown : entry.Job.ResumeCapability,
            NextAttemptUtc = entry.Job.NextAttemptUtc, QueuePosition = position, Capabilities = caps };
    }
    private static bool CanContinue(DownloadJobRecord job) =>
        job.LastErrorCode is not (DownloadErrorCode.RangeNotSupported or DownloadErrorCode.RemoteContentChanged or DownloadErrorCode.PartialFileInvalid or DownloadErrorCode.ChecksumMismatch) &&
        (job.CheckpointBytes == 0 || job.ExpectedBytes is not null && DownloadRemoteIdentity.IfRange(job.ETag, job.LastModifiedUtc, job.ResponseDateUtc) is not null);
    private DownloadJobRecord[] Queued() => DownloadQueuePolicy.Order(_jobs.Values.Where(e => e.Ready && !e.Active && e.Job.State == DownloadJobState.Queued).Select(e => e.Job)).ToArray();
    public Task<IReadOnlyList<DownloadJobView>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var positions = Queued().Select((job, index) => (job.Id, Rank: index + 1)).ToDictionary(p => p.Id, p => p.Rank);
            return Task.FromResult<IReadOnlyList<DownloadJobView>>(_jobs.Values.OrderByDescending(e => e.Job.CreatedUtc)
                .ThenByDescending(e => e.Job.Id).Select(e => View(e, positions)).ToArray());
        }
    }
    public Task<DownloadJobView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); lock (_gate) return Task.FromResult(_jobs.TryGetValue(id, out var entry) ? View(entry) : null); }
    public Task<DownloadJobView> CancelAsync(Guid id, CancellationToken cancellationToken = default) => StopAsync(id, false, cancellationToken);
    public Task<DownloadJobView> PauseAsync(Guid id, CancellationToken cancellationToken = default) => StopAsync(id, true, cancellationToken);
    private async Task<DownloadJobView> StopAsync(Guid id, bool pause, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Entry entry; Task work;
        lock (_gate)
        {
            if (_stopping) throw new DownloadEngineException(DownloadErrorCode.EngineUnavailable);
            if (!_jobs.TryGetValue(id, out entry!)) throw new DownloadEngineException(DownloadErrorCode.InvalidUrl);
            entry.Ready = false;
            if (!entry.Active)
            {
                if (DownloadStateMachine.IsTerminal(entry.Job.State) || pause && entry.Job.State == DownloadJobState.Paused) return View(entry);
                if (pause && entry.Job.State == DownloadJobState.Failed) throw new NotSupportedException();
                entry.Job = entry.Job with { NextAttemptUtc = null, DispatchRequested = false };
                Advance(entry, pause ? DownloadJobState.Paused : DownloadJobState.Cancelled, pause ? DownloadTransitionCause.UserPause : DownloadTransitionCause.UserCancel); Pulse(); return View(entry);
            }
            if (DownloadStateMachine.IsTerminal(entry.Job.State)) return View(entry);
            if (!entry.Stop.IsCancellationRequested || !pause) entry.PauseRequested = pause;
            entry.Stop.Cancel(); work = entry.Work;
        }
        await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            // A stop may arrive after the worker chose Retrying/Failed but before
            // its finally block releases the slot. Commit that stop after the
            // acknowledgement too, so no waiting retry can escape cancellation.
            var target = entry.PauseRequested ? DownloadJobState.Paused : DownloadJobState.Cancelled;
            var cause = entry.PauseRequested ? DownloadTransitionCause.UserPause : DownloadTransitionCause.UserCancel;
            if (!entry.Active && DownloadStateMachine.CanTransition(entry.Job.State, target, cause))
            { entry.Job = entry.Job with { NextAttemptUtc = null, DispatchRequested = false }; Advance(entry, target, cause); Pulse(); }
            return View(entry);
        }
    }
    public Task<DownloadJobView> ResumeAsync(Guid id, CancellationToken cancellationToken = default) => RequeueAsync(id, false, cancellationToken);
    public Task<DownloadJobView> RetryAsync(Guid id, CancellationToken cancellationToken = default) => RequeueAsync(id, true, cancellationToken);
    public Task<DownloadJobView> StartAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Available();
            if (!_jobs.TryGetValue(id, out var entry) || entry.Job.State != DownloadJobState.Queued) throw new NotSupportedException();
        }
        return RequeueAsync(id, false, cancellationToken);
    }
    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Available();
            if (!_jobs.TryGetValue(id, out var entry)) throw new DownloadEngineException(DownloadErrorCode.InvalidUrl);
            if (entry.Active) throw new DownloadEngineException(DownloadErrorCode.EngineUnavailable);
            try { _store?.Forget(id); }
            catch { BlockRecovery(); throw new DownloadEngineException(DownloadErrorCode.StorageFailure); }
            entry.Ready = false; _jobs.Remove(id); entry.Stop.Dispose(); Emit(entry.Job); Pulse();
            return Task.CompletedTask;
        }
    }
    public async Task OpenFolderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); DownloadStagingFile lease; string directory;
        lock (_gate)
        {
            Available();
            if (!_jobs.TryGetValue(id, out var entry)) throw new DownloadEngineException(DownloadErrorCode.InvalidUrl);
            lease = DownloadStagingFile.OpenCompleted(entry.Job);
            directory = Path.GetDirectoryName(entry.Job.DestinationPath)!;
        }
        using (lease) { cancellationToken.ThrowIfCancellationRequested(); await _openFolder(directory).ConfigureAwait(false); }
    }
    private async Task<DownloadJobView> RequeueAsync(Guid id, bool retry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Entry entry; Task work;
        lock (_gate)
        {
            Available();
            if (!_jobs.TryGetValue(id, out entry!) || (retry ? entry.Job.State != DownloadJobState.Failed : entry.Job.State is not (DownloadJobState.Paused or DownloadJobState.Queued))) throw new NotSupportedException();
            work = entry.Work;
        }
        await work.WaitAsync(token).ConfigureAwait(false);
        lock (_gate)
        {
            token.ThrowIfCancellationRequested(); Available();
            if (!_jobs.TryGetValue(id, out var current) || !ReferenceEquals(current, entry))
                throw new DownloadEngineException(DownloadErrorCode.InvalidUrl);
            if (entry.Active || (retry ? entry.Job.State != DownloadJobState.Failed : entry.Job.State is not (DownloadJobState.Paused or DownloadJobState.Queued))) throw new NotSupportedException();
            Reserve(entry.Job);
            if (!CanContinue(entry.Job)) throw new DownloadEngineException(DownloadErrorCode.RangeNotSupported);
            entry.Job = entry.Job with { Attempts = 0, NextAttemptUtc = null, LastErrorCode = null, SafeErrorDetail = null, DispatchRequested = true };
            if (entry.Job.State != DownloadJobState.Queued) Advance(entry, DownloadJobState.Queued, retry ? DownloadTransitionCause.UserRetry : DownloadTransitionCause.UserResume);
            else entry.Job = Save(entry, entry.Job);
            entry.Ready = true; Pulse(); return View(entry);
        }
    }
    public Task<DownloadQueueSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); lock (_gate) return Task.FromResult(_settings); }
    public Task SetSettingsAsync(DownloadQueueSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); DownloadQueuePolicy.Validate(settings);
        lock (_gate)
        {
            Available();
            try { _store?.SaveSettings(settings); }
            catch { BlockRecovery(); throw new DownloadEngineException(DownloadErrorCode.StorageFailure); }
            _settings = settings; Pulse(); return Task.CompletedTask;
        }
    }
    public async IAsyncEnumerable<DownloadEvent> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<DownloadEvent>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) { if (_stopping) yield break; _watchers.Add(channel); }
        try { await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken)) yield return item; }
        finally { lock (_gate) _watchers.Remove(channel); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdown);
            _stopping = true; _lifetime.Cancel();
            foreach (var entry in _jobs.Values.Where(e => e.Active)) { if (!entry.Stop.IsCancellationRequested) entry.PauseRequested = true; entry.Stop.Cancel(); }
            _shutdown = ShutdownAsync(_jobs.Values.Select(e => e.Work).ToArray()); return new(_shutdown);
        }
    }
    private async Task ShutdownAsync(Task[] tasks)
    {
        await Task.WhenAll(tasks.Append(_scheduler)).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var entry in _jobs.Values) entry.Stop.Dispose();
            foreach (var channel in _watchers) channel.Writer.TryComplete();
            if (_engine is IDisposable disposable) disposable.Dispose(); _store?.Dispose(); _lifetime.Dispose();
        }
    }
    private sealed class Entry(DownloadJobRecord job, DownloadSource source)
    {
        public DownloadJobRecord Job = job;
        public readonly DownloadSource Source = source;
        public long StoredRevision = -1;
        public bool Ready, Active, PauseRequested;
        public CancellationTokenSource Stop = new();
        public Task Work = Task.CompletedTask;
        public long Speed;
    }
    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress> { public void Report(DownloadProgress progress) => report(progress); }
}
