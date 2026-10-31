using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DKDesktopEssentials.Modules.Downloads;

internal static class QueueCases
{
    private static readonly Uri Url = new("https://example.org/data?token=queue-fixture-secret");
    private static readonly byte[] Data = "payload"u8.ToArray();
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    internal static IEnumerable<(string Name, Func<Task> Run)> All => new (string, Func<Task>)[]
    {
        ("Queue ordering is deterministic by priority age and GUID", Ordering),
        ("Retry classification jitter cap and Retry-After are bounded", Policy),
        ("SQLite protects sources and paths and rejects stale revisions atomically", Store),
        ("Legacy checkpoints migrate once without modifying the backup", Migration),
        ("Unknown database versions fail without rewriting the file", FutureSchema),
        ("Queue respects priority and paused or cancelled waiting jobs", Dispatch),
        ("Reducing concurrency drains active work before dispatching another job", Concurrency),
        ("Backoff releases its slot and stops at the configured attempt budget", Retries),
        ("Retry-After is observed and pause or cancel stops delayed dispatch", DelayedStop),
        ("Lowering the retry budget prevents another waiting attempt", LowerBudget),
        ("Manual retry resets the budget and preserves destination collision checks", ManualRetry),
        ("Disk preflight refuses body transfer and succeeds after explicit retry", Capacity),
        ("Capacity accounting includes other active jobs on the same drive", ReservedCapacity),
        ("Queued late collisions preserve existing data without making requests", LateCollision),
        ("Queue and settings survive restart with automatic starts disabled", Recovery),
        ("Explicit startup preference starts queued work but leaves paused jobs alone", AutoStart),
        ("Native bridge exposes validated queue settings priority and manual retry", Bridge),
        ("Broken real HTTP body retries from its committed SQLite checkpoint", BrokenBody),
        ("Corrupt protected database records block all network activity", CorruptRecord),
        ("Failed legacy import leaves records intact and can be retried", FailedMigration),
        ("Queue event history stays bounded without losing the newest transition", BoundedEvents),
        ("Recovery save failure cannot start a partially restored automatic queue", RecoveryWriteFailure),
        ("Runtime journal failure stops work and blocks new dispatch", RuntimeWriteFailure),
        ("Excessive Retry-After unsafe partials and full disk require intervention", Intervention)
    };
    private static void Check(bool value, string message = "Queue assertion failed") { if (!value) throw new Exception(message); }
    private static async Task Reject(DownloadErrorCode code, Func<Task> action)
    { try { await action(); } catch (DownloadEngineException ex) { Check(ex.Code == code, $"Expected {code}, got {ex.Code}"); return; } throw new Exception("Expected rejection"); }
    private static async Task<DownloadJobView> State(DownloadCoordinator c, Guid id, DownloadJobState state, int? attempt = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var job = (await c.GetAsync(id))!;
            if (job.State == state && (attempt is null || job.Attempts == attempt)) return job;
            if (timeout.IsCancellationRequested) throw new Exception($"Expected {state}/{attempt}; got {job.State}/{job.Attempts}/{job.ErrorCode}");
            await Task.Delay(5);
        }
    }
    private static Task Ordering()
    {
        using var f = new Folder(); var seed = f.Seed();
        var first = seed with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Priority = DownloadPriority.High };
        var second = first with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var later = first with { Id = Guid.NewGuid(), CreatedUtc = first.CreatedUtc.AddSeconds(1) };
        var low = first with { Id = Guid.NewGuid(), Priority = DownloadPriority.Low, CreatedUtc = first.CreatedUtc.AddDays(-1) };
        Check(DownloadQueuePolicy.Order(new[] { low, later, second, first }).Select(j => j.Id).SequenceEqual(new[] { first.Id, second.Id, later.Id, low.Id })); return Task.CompletedTask;
    }
    private static Task Policy()
    {
        Check(DownloadQueuePolicy.Backoff(1, 0, null) == TimeSpan.FromSeconds(2));
        Check(DownloadQueuePolicy.Backoff(2, 1, null) == TimeSpan.FromSeconds(5));
        Check(DownloadQueuePolicy.Backoff(20, 1, null) == TimeSpan.FromSeconds(120));
        Check(DownloadQueuePolicy.Backoff(1, 0, TimeSpan.FromSeconds(300)) == TimeSpan.FromSeconds(300));
        foreach (var code in new[] { DownloadErrorCode.DiskFull, DownloadErrorCode.ChecksumMismatch, DownloadErrorCode.RemoteContentChanged, DownloadErrorCode.HttpUnauthorized, DownloadErrorCode.TlsFailure }) Check(!DownloadQueuePolicy.Retryable(code));
        foreach (var settings in new[] { new DownloadQueueSettings(0), new DownloadQueueSettings(17), new DownloadQueueSettings(MaxAttempts: 0), new DownloadQueueSettings(MaxAttempts: 21) })
        { try { DownloadQueuePolicy.Validate(settings); throw new Exception("Invalid settings accepted"); } catch (ArgumentOutOfRangeException) { } }
        return Task.CompletedTask;
    }
    private static async Task Store()
    {
        using var f = new Folder(); var job = f.Seed();
        using (var store = new DownloadJobStore(f.Db))
        {
            store.Save(job, new(Url), -1); Check(store.EventCount == 1);
            Check(await store.TryUpdateAsync(job with { State = DownloadJobState.Resolving, Revision = 1 }, 0));
            Check(store.EventCount == 2);
            Check(!await store.TryUpdateAsync(job with { State = DownloadJobState.Cancelled, Revision = 2 }, 0));
            Check(store.EventCount == 2 && (await store.GetAsync(job.Id))!.State == DownloadJobState.Resolving);
            store.SaveSettings(new(2, 3));
            foreach (var path in Directory.GetFiles(f.Db, "downloads.db*"))
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var bytes = new MemoryStream(); file.CopyTo(bytes);
                foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
                { var text = encoding.GetString(bytes.ToArray()); Check(!text.Contains("queue-fixture-secret") && !text.Contains(f.Output)); }
            }
        }
        using var reopened = new DownloadJobStore(f.Db);
        Check(reopened.Load().Single().AuthorizedUrl == Url.AbsoluteUri && reopened.LoadSettings() == new DownloadQueueSettings(2, 3));
        using var db = new DownloadSqlite(Path.Combine(f.Db, "downloads.db"));
        using var mode = db.Prepare("PRAGMA journal_mode"); Check(mode.Read() && mode.Text(0) == "wal");
    }
    private static Task Migration()
    {
        using var f = new Folder(); var job = f.Seed() with { State = DownloadJobState.Paused };
        var legacy = Path.Combine(f.Db, "Checkpoints");
        using (var old = new DownloadCheckpointStore(legacy)) old.Save(job, new(Url));
        var path = Directory.GetFiles(legacy, "*.checkpoint").Single(); var original = File.ReadAllBytes(path);
        using (var store = new DownloadJobStore(f.Db)) Check(store.Load().Single().Job.State == DownloadJobState.Paused);
        Check(File.ReadAllBytes(path).SequenceEqual(original));
        using (var old = new DownloadCheckpointStore(legacy)) old.Save(job with { State = DownloadJobState.Cancelled, Revision = 2 }, new(Url));
        using (var store = new DownloadJobStore(f.Db)) Check(store.Load().Single().Job.State == DownloadJobState.Paused);
        return Task.CompletedTask;
    }
    private static Task FutureSchema()
    {
        using var f = new Folder(); Directory.CreateDirectory(f.Db); var path = Path.Combine(f.Db, "downloads.db");
        using (var db = new DownloadSqlite(path)) db.Exec("PRAGMA user_version=999; CREATE TABLE future_data(value TEXT); INSERT INTO future_data VALUES('retain');");
        var original = File.ReadAllBytes(path);
        try { using var store = new DownloadJobStore(f.Db); throw new Exception("Future schema accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllBytes(path).SequenceEqual(original)); return Task.CompletedTask;
    }
    private static async Task Dispatch()
    {
        using var f = new Folder(); var gate = new Gate(); await using var c = f.Coordinator(gate.Engine);
        await c.SetSettingsAsync(new(1, 1));
        var blocker = await c.EnqueueAsync(f.Request("blocker")); Check(await gate.Started() == blocker.Id);
        var low = await c.EnqueueAsync(f.Request("low", DownloadPriority.Low));
        var high = await c.EnqueueAsync(f.Request("high", DownloadPriority.High));
        var paused = await c.EnqueueAsync(f.Request("paused", DownloadPriority.High)); await c.PauseAsync(paused.Id);
        var cancelled = await c.EnqueueAsync(f.Request("cancelled", DownloadPriority.High)); await c.CancelAsync(cancelled.Id);
        gate.Release(blocker.Id); Check(await gate.Started() == high.Id); gate.Release(high.Id);
        Check(await gate.Started() == low.Id); gate.Release(low.Id); await State(c, low.Id, DownloadJobState.Completed);
        Check((await c.GetAsync(paused.Id))!.State == DownloadJobState.Paused && gate.Engine.Bodies == 3);
    }
    private static async Task Concurrency()
    {
        using var f = new Folder(); var gate = new Gate(); await using var c = f.Coordinator(gate.Engine);
        var active = new List<Guid>();
        for (var i = 0; i < 3; i++) { await c.EnqueueAsync(f.Request($"active{i}")); active.Add(await gate.Started()); }
        var waiting = await c.EnqueueAsync(f.Request("waiting")); await c.SetSettingsAsync(new(1, 1));
        for (var i = 0; i < 2; i++)
        { gate.Release(active[i]); await State(c, active[i], DownloadJobState.Completed); await Task.Delay(20); Check(gate.Engine.Bodies == 3); }
        gate.Release(active[2]); Check(await gate.Started() == waiting.Id); gate.Release(waiting.Id); await State(c, waiting.Id, DownloadJobState.Completed);
    }
    private static async Task Retries()
    {
        using var f = new Folder(); var clock = new ManualClock();
        var fake = new Fake { Probe = (source, _) => source.AuthorizedUrl.AbsolutePath == "/retry" ? throw new DownloadEngineException(DownloadErrorCode.HttpServerError) : Task.FromResult(Fake.Metadata) };
        await using var c = f.Coordinator(fake, clock); await c.SetSettingsAsync(new(1, 3));
        var job = await c.EnqueueAsync(f.Request("retry", url: new("https://example.org/retry")));
        var retry = await State(c, job.Id, DownloadJobState.Retrying, 1); Check(retry.NextAttemptUtc == clock.GetUtcNow().AddSeconds(2));
        var other = await c.EnqueueAsync(f.Request("other")); await State(c, other.Id, DownloadJobState.Completed);
        clock.Advance(TimeSpan.FromSeconds(2)); await State(c, job.Id, DownloadJobState.Retrying, 2);
        clock.Advance(TimeSpan.FromSeconds(4)); var failed = await State(c, job.Id, DownloadJobState.Failed, 3);
        Check(failed.ErrorCode == DownloadErrorCode.HttpServerError && failed.NextAttemptUtc is null);
    }
    private static async Task DelayedStop()
    {
        using var f = new Folder(); var clock = new ManualClock();
        var fake = new Fake { Probe = (_, _) => throw new DownloadEngineException(DownloadErrorCode.HttpRateLimited, TimeSpan.FromMinutes(2)) };
        await using var c = f.Coordinator(fake, clock);
        var job = await c.EnqueueAsync(f.Request("wait")); var waiting = await State(c, job.Id, DownloadJobState.Retrying);
        Check(waiting.NextAttemptUtc == clock.GetUtcNow().AddMinutes(2));
        clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(20); Check(fake.Probes == 1);
        await c.PauseAsync(job.Id); clock.Advance(TimeSpan.FromMinutes(2)); await Task.Delay(20); Check(fake.Probes == 1);
        await c.ResumeAsync(job.Id); await State(c, job.Id, DownloadJobState.Retrying, 1);
        await c.CancelAsync(job.Id); var count = fake.Probes; clock.Advance(TimeSpan.FromHours(1)); await Task.Delay(20);
        Check(fake.Probes == count && (await c.GetAsync(job.Id))!.State == DownloadJobState.Cancelled);
    }
    private static async Task LowerBudget()
    {
        using var f = new Folder(); var clock = new ManualClock(); var fake = new Fake { Probe = (_, _) => throw new DownloadEngineException(DownloadErrorCode.Timeout) };
        await using var c = f.Coordinator(fake, clock); var job = await c.EnqueueAsync(f.Request("lower")); await State(c, job.Id, DownloadJobState.Retrying);
        await c.SetSettingsAsync(new(MaxAttempts: 1)); await State(c, job.Id, DownloadJobState.Failed); clock.Advance(TimeSpan.FromHours(1)); await Task.Delay(20); Check(fake.Probes == 1);
    }
    private static async Task ManualRetry()
    {
        using var f = new Folder(); var fail = true; var fake = new Fake { Probe = (_, _) => fail ? throw new DownloadEngineException(DownloadErrorCode.HttpUnauthorized) : Task.FromResult(Fake.Metadata) };
        await using var c = f.Coordinator(fake); var job = await c.EnqueueAsync(f.Request("retry")); await State(c, job.Id, DownloadJobState.Failed);
        await File.WriteAllTextAsync(Path.Combine(f.Output, "retry"), "existing");
        await Reject(DownloadErrorCode.DestinationExists, () => c.RetryAsync(job.Id)); Check(fake.Probes == 1);
        File.Delete(Path.Combine(f.Output, "retry")); fail = false; await c.RetryAsync(job.Id);
        Check((await State(c, job.Id, DownloadJobState.Completed)).Attempts == 1);
    }
    private static async Task Capacity()
    {
        using var f = new Folder(); long free = 0; var fake = new Fake(); await using var c = f.Coordinator(fake, available: _ => free);
        var job = await c.EnqueueAsync(f.Request("capacity")); Check((await State(c, job.Id, DownloadJobState.Failed)).ErrorCode == DownloadErrorCode.DiskFull && fake.Bodies == 0);
        free = long.MaxValue; await c.RetryAsync(job.Id); await State(c, job.Id, DownloadJobState.Completed); Check(fake.Bodies == 1);
    }
    private static async Task ReservedCapacity()
    {
        using var f = new Folder(); var gate = new Gate();
        gate.Engine.Probe = (_, _) => Task.FromResult(Fake.Metadata with { ContentLength = 100 });
        await using var c = f.Coordinator(gate.Engine, available: _ => 2 * DownloadDiskSpace.Headroom + 199);
        var first = await c.EnqueueAsync(f.Request("first")); await gate.Started();
        var second = await c.EnqueueAsync(f.Request("second")); Check((await State(c, second.Id, DownloadJobState.Failed)).ErrorCode == DownloadErrorCode.DiskFull && gate.Engine.Bodies == 1);
        await c.CancelAsync(first.Id);
    }
    private static async Task LateCollision()
    {
        using var f = new Folder(); var gate = new Gate(); await using var c = f.Coordinator(gate.Engine); await c.SetSettingsAsync(new(1, 1));
        var first = await c.EnqueueAsync(f.Request("block")); await gate.Started();
        var second = await c.EnqueueAsync(f.Request("collision")); await File.WriteAllTextAsync(Path.Combine(f.Output, "collision"), "retain");
        await c.CancelAsync(first.Id); Check((await State(c, second.Id, DownloadJobState.Failed)).ErrorCode == DownloadErrorCode.DestinationExists);
        Check(gate.Engine.Probes == 1 && File.ReadAllText(Path.Combine(f.Output, "collision")) == "retain");
    }
    private static async Task Recovery()
    {
        using var f = new Folder(); var gate = new Gate(); Guid queued;
        await using (var c = f.Coordinator(gate.Engine))
        { await c.SetSettingsAsync(new(1, 4)); await c.EnqueueAsync(f.Request("active")); await gate.Started(); queued = (await c.EnqueueAsync(f.Request("queued"))).Id; }
        var fake = new Fake(); await using var recovered = f.Coordinator(fake); await Task.Delay(20);
        Check(fake.Probes == 0 && (await recovered.GetSettingsAsync()) == new DownloadQueueSettings(1, 4));
        Check((await recovered.GetAsync(queued))!.CanResume); await recovered.ResumeAsync(queued); await State(recovered, queued, DownloadJobState.Completed);
    }
    private static async Task AutoStart()
    {
        using var f = new Folder(); var queued = f.Seed(); var paused = f.Seed() with { State = DownloadJobState.Paused };
        using (var store = new DownloadJobStore(f.Db)) { store.Save(queued, new(Url), -1); store.Save(paused, new(Url), -1); store.SaveSettings(new(1, 2, true)); }
        var fake = new Fake(); await using var c = f.Coordinator(fake); await State(c, queued.Id, DownloadJobState.Completed);
        Check((await c.GetAsync(paused.Id))!.State == DownloadJobState.Paused && fake.Probes == 1);
    }
    private static async Task Bridge()
    {
        using var f = new Folder(); var first = true;
        var fake = new Fake { Probe = (_, _) => { if (first) { first = false; throw new DownloadEngineException(DownloadErrorCode.HttpUnauthorized); } return Task.FromResult(Fake.Metadata); } };
        await using var c = f.Coordinator(fake); var bridge = new DownloadWebBridge(c, () => Task.FromResult<string?>(f.Output));
        string Command(string cmd, object? body = null) => JsonSerializer.Serialize(new { type = "downloads:" + cmd, requestId = "queue", payload = body });
        const string origin = "https://dk.local/index.html";
        Check(await bridge.HandleAsync(Command("setSettings", new { maxConcurrentDownloads = 1, maxAttempts = 2, autoStartOnLaunch = false }), "https://evil.example/") is null);
        var reply = (await bridge.HandleAsync(Command("setSettings", new { maxConcurrentDownloads = 1, maxAttempts = 2, autoStartOnLaunch = false }), origin))!;
        Check(reply.Contains("\"ok\":true") && (await c.GetSettingsAsync()).MaxConcurrentDownloads == 1);
        Check((await bridge.HandleAsync(Command("setSettings", new { maxConcurrentDownloads = 17, maxAttempts = 2, autoStartOnLaunch = false }), origin))!.Contains("\"ok\":false"));
        using var grant = JsonDocument.Parse((await bridge.HandleAsync(Command("chooseDestination"), origin))!);
        reply = (await bridge.HandleAsync(Command("enqueue", new { destinationToken = grant.RootElement.GetProperty("result").GetProperty("destinationToken").GetString(), url = Url.AbsoluteUri, fileName = "bridge", priority = "High" }), origin))!;
        Check(reply.Contains("\"priority\":\"High\"") && !reply.Contains("queue-fixture-secret") && !reply.Contains(f.Output));
        using var submitted = JsonDocument.Parse(reply); var id = submitted.RootElement.GetProperty("result").GetProperty("id").GetGuid();
        await State(c, id, DownloadJobState.Failed);
        Check((await bridge.HandleAsync(Command("retry", new { id }), origin))!.Contains("\"ok\":true"));
        await State(c, id, DownloadJobState.Completed);
    }
    private sealed class Fake : IDownloadEngine
    {
        internal int Probes, Bodies;
        internal static DownloadProbe Metadata => new(7, "\"v1\"", null, DownloadResumeCapability.Unknown, null);
        internal Func<DownloadSource, CancellationToken, Task<DownloadProbe>> Probe = (_, _) => Task.FromResult(Metadata);
        internal Func<DownloadExecutionContext, CancellationToken, Task>? BeforeWrite;
        internal Func<DownloadExecutionContext, IProgress<DownloadProgress>, CancellationToken, Task<DownloadExecutionResult>>? CustomTransfer;
        public Task<DownloadProbe> ProbeAsync(DownloadSource source, CancellationToken token) { Interlocked.Increment(ref Probes); return Probe(source, token); }
        public async Task<DownloadExecutionResult> TransferAsync(DownloadExecutionContext context, IProgress<DownloadProgress> progress, CancellationToken token)
        {
            Interlocked.Increment(ref Bodies);
            if (CustomTransfer is not null) return await CustomTransfer(context, progress, token);
            if (BeforeWrite is not null) await BeforeWrite(context, token);
            await File.WriteAllBytesAsync(context.Job.TemporaryPath, Data, token); return new(7, Hash(Data));
        }
    }
    private static async Task BrokenBody()
    {
        using var f = new Folder(); await using var server = new RangeFixture(breakFirstBody:true);
        await using var c = new DownloadCoordinator(new HttpDownloadEngine(), new DownloadJobStore(f.Db));
        var job = await c.EnqueueAsync(f.Request("broken-body", url:server.Url));
        // This fixture deliberately crosses a real 2-second backoff plus network transfer time.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        DownloadJobView done;
        do { await Task.Delay(10, timeout.Token); done = (await c.GetAsync(job.Id))!; }
        while (done.State is not (DownloadJobState.Completed or DownloadJobState.Failed));
        Check(done.State == DownloadJobState.Completed && done.Attempts == 2, $"Broken body ended {done.State}/{done.ErrorCode}/{done.Attempts}");
        Check(server.Requests.Any(offset => offset == 2 * 1024 * 1024) && Hash(File.ReadAllBytes(Path.Combine(f.Output, "broken-body"))) == Hash(server.Data));
    }
    private static async Task CorruptRecord()
    {
        using var f = new Folder(); var job = f.Seed();
        using (var store = new DownloadJobStore(f.Db)) { store.Save(job, new(Url), -1); store.SaveSettings(new(AutoStartOnLaunch:true)); }
        using (var db = new DownloadSqlite(Path.Combine(f.Db, "downloads.db")))
        { using var damage = db.Prepare("UPDATE download_jobs SET protected_record=?", new byte[20]); damage.Read(); }
        var fake = new Fake(); await using var c = f.Coordinator(fake); await Task.Delay(30);
        Check(c.RecoveryBlocked && fake.Probes == 0);
        await Reject(DownloadErrorCode.StorageFailure, () => c.EnqueueAsync(f.Request("blocked")));
    }
    private static Task FailedMigration()
    {
        using var f = new Folder(); var job = f.Seed(); var legacy = Path.Combine(f.Db, "Checkpoints");
        using (var old = new DownloadCheckpointStore(legacy)) old.Save(job, new(Url));
        var path = Directory.GetFiles(legacy, "*.checkpoint").Single(); var original = File.ReadAllBytes(path);
        File.WriteAllBytes(path, new byte[20]);
        try { using var store = new DownloadJobStore(f.Db); throw new Exception("Corrupt legacy import succeeded"); }
        catch (DownloadEngineException ex) { Check(ex.Code == DownloadErrorCode.StorageFailure); }
        Check(File.ReadAllBytes(path).SequenceEqual(new byte[20]));
        using (var db = new DownloadSqlite(Path.Combine(f.Db, "downloads.db")))
        { using var query = db.Prepare("SELECT count(*) FROM download_metadata"); Check(query.Read() && query.Number(0) == 0); }
        File.WriteAllBytes(path, original);
        using (var store = new DownloadJobStore(f.Db)) Check(store.Load().Single().Job.Id == job.Id);
        return Task.CompletedTask;
    }
    private static Task BoundedEvents()
    {
        using var f = new Folder(); var job = f.Seed(); using var store = new DownloadJobStore(f.Db); store.Save(job, new(Url), -1);
        using (var db = new DownloadSqlite(Path.Combine(f.Db, "downloads.db")))
        {
            using var fill = db.Prepare("WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1100) INSERT INTO download_events(job_id,revision,occurred_utc,to_state) SELECT ?,0,?,'Queued' FROM n", job.Id.ToString("N"), job.CreatedUtc.ToString("O")); fill.Read();
        }
        store.Save(job with { Revision = 1, State = DownloadJobState.Paused }, new(Url), 0);
        Check(store.EventCount == 1000); return Task.CompletedTask;
    }
    private static async Task RecoveryWriteFailure()
    {
        using var f = new Folder(); var store = new DownloadJobStore(f.Db);
        store.Save(f.Seed(), new(Url), -1); store.Save(f.Seed(), new(Url), -1); store.SaveSettings(new(AutoStartOnLaunch:true));
        var fake = new Fake(); await using var c = new DownloadCoordinator(fake, new FaultStore(store, (_, count) => count >= 2));
        await Task.Delay(30); Check(c.RecoveryBlocked && fake.Probes == 0);
    }
    private static async Task RuntimeWriteFailure()
    {
        using var f = new Folder(); var faulted = false; var fake = new Fake();
        await using var c = new DownloadCoordinator(fake, new FaultStore(new DownloadJobStore(f.Db), (job, _) => faulted |= job.State == DownloadJobState.Downloading));
        var job = await c.EnqueueAsync(f.Request("fault")); var paused = await State(c, job.Id, DownloadJobState.Paused);
        Check(c.RecoveryBlocked && paused.ErrorCode == DownloadErrorCode.StorageFailure && fake.Bodies == 0);
        await Reject(DownloadErrorCode.StorageFailure, () => c.EnqueueAsync(f.Request("another")));
    }
    private static async Task Intervention()
    {
        using (var f = new Folder())
        {
            var fake = new Fake { CustomTransfer = async (context, _, token) =>
            { await File.WriteAllTextAsync(context.Job.TemporaryPath, "pa", token); throw new IOException("generated full-disk fixture", unchecked((int)0x80070070)); } };
            await using var c = f.Coordinator(fake); var job = await c.EnqueueAsync(f.Request("full"));
            Check((await State(c, job.Id, DownloadJobState.Failed)).ErrorCode == DownloadErrorCode.DiskFull && fake.Bodies == 1);
            Check(File.ReadAllText(Path.Combine(f.Output, $".{job.Id:N}.dkdownload.part")) == "pa");
        }
        using (var f = new Folder())
        {
            var fake = new Fake { Probe = (_, _) => throw new DownloadEngineException(DownloadErrorCode.HttpRateLimited, TimeSpan.FromDays(2)) };
            await using var c = f.Coordinator(fake); var job = await c.EnqueueAsync(f.Request("long-wait"));
            Check((await State(c, job.Id, DownloadJobState.Failed)).Attempts == 1 && fake.Probes == 1);
        }
        using (var f = new Folder())
        {
            var fake = new Fake { Probe = (_, _) => Task.FromResult(Fake.Metadata with { ETag = null }), CustomTransfer = async (context, progress, token) =>
            { await File.WriteAllBytesAsync(context.Job.TemporaryPath, "pay"u8.ToArray(), token); progress.Report(new(context.Job.Id, 3, 7, 3, Hash("pay"u8.ToArray()))); throw new DownloadEngineException(DownloadErrorCode.NetworkUnavailable); } };
            await using var c = f.Coordinator(fake); var job = await c.EnqueueAsync(f.Request("unsafe")); await State(c, job.Id, DownloadJobState.Failed);
            Check(fake.Bodies == 1 && File.ReadAllBytes(Path.Combine(f.Output, $".{job.Id:N}.dkdownload.part")).SequenceEqual("pay"u8.ToArray()));
        }
    }
    private sealed class FaultStore(IDownloadPersistence inner, Func<DownloadJobRecord, int, bool> fail) : IDownloadPersistence
    {
        private int _writes;
        public IReadOnlyList<DownloadCheckpointStore.SavedJob> Load() => inner.Load();
        public void Save(DownloadJobRecord job, DownloadSource source, long? expectedRevision = null)
        { if (fail(job, ++_writes)) throw new DownloadEngineException(DownloadErrorCode.StorageFailure); inner.Save(job, source, expectedRevision); }
        public void Forget(Guid id) => inner.Forget(id);
        public DownloadQueueSettings LoadSettings() => inner.LoadSettings();
        public void SaveSettings(DownloadQueueSettings settings) => inner.SaveSettings(settings);
        public void Dispose() => inner.Dispose();
    }
    private sealed class Gate
    {
        internal readonly Fake Engine = new();
        private readonly Channel<Guid> _started = Channel.CreateUnbounded<Guid>();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _release = new();
        internal Gate() => Engine.BeforeWrite = async (context, token) => { var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _release[context.Job.Id] = ready; _started.Writer.TryWrite(context.Job.Id); await ready.Task.WaitAsync(token); };
        internal async Task<Guid> Started() { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); return await _started.Reader.ReadAsync(timeout.Token); }
        internal void Release(Guid id) => _release[id].SetResult();
    }
    private sealed class Folder : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "dk-queue-tests-" + Guid.NewGuid().ToString("N"));
        internal string Db => Path.Combine(Root, "store");
        internal string Output => Path.Combine(Root, "output");
        internal Folder() => Directory.CreateDirectory(Output);
        internal CreateDownloadRequest Request(string name, DownloadPriority priority = DownloadPriority.Normal, Uri? url = null) => new(url ?? Url, Output, name, priority);
        internal DownloadJobRecord Seed()
        {
            var id = Guid.NewGuid(); var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            return new() { Id = id, SourceKey = Guid.NewGuid().ToString("N"), DisplayHost = Url.IdnHost, DestinationPath = Path.Combine(Output, id + ".bin"),
                TemporaryPath = Path.Combine(Output, $".{id:N}.dkdownload.part"), CreatedUtc = now, UpdatedUtc = now, ExpectedBytes = 7, CheckpointSha256 = Hash(Array.Empty<byte>()) };
        }
        internal DownloadCoordinator Coordinator(Fake engine, TimeProvider? clock = null, Func<string, long>? available = null) => new(engine, new DownloadJobStore(Db), clock, () => 0, available);
        public void Dispose() { if (Path.GetDirectoryName(Root) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Unexpected cleanup directory"); Directory.Delete(Root, true); }
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly object _gate = new(); private readonly List<ClockTimer> _timers = new();
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ClockTimer(this, callback, state); lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); } return timer; }
        internal void Advance(TimeSpan elapsed)
        {
            ClockTimer[] due;
            lock (_gate) { _now += elapsed; due = _timers.Where(t => t.Due <= _now).ToArray(); foreach (var timer in due) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? null : _now + timer.Period; }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback; internal readonly object? State = state;
            internal DateTimeOffset? Due; internal TimeSpan Period;
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (clock._gate) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime; Period = period; return true; } }
            public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
