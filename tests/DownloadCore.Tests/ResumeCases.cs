using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DKDesktopEssentials.Modules.Downloads;

internal static class ResumeCases
{
    private static readonly byte[] Payload = "payload"u8.ToArray();
    private static readonly Uri Source = new("https://example.org/data?token=protected-checkpoint-secret");
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static IEnumerable<(string Name, Func<Task> Run)> All => new (string, Func<Task>)[]
    {
        ("Validated 206 resumes at the durable offset with If-Range", Resume),
        ("Ignored Range 200 cannot alter the partial or uncommitted tail", Ignored),
        ("Range HTTP failures keep actionable errors and preserve the partial", RangeErrors),
        ("Malformed Content-Range and multipart responses preserve bytes", Malformed),
        ("Changed ETag probe and response refuse splicing", Changed),
        ("Strong ETag does not require redundant Last-Modified", RedundantDate),
        ("Changed redirect target refuses saved entity reuse", ChangedTarget),
        ("Weak ETags never become If-Range dates", Weak),
        ("Strong Last-Modified date supports conditional resume", DateValidator),
        ("Ambiguous Last-Modified date refuses resume", AmbiguousDate),
        ("416 completes only matching full prefix and remote size", Unsatisfied),
        ("Partial tampering is detected before network access", Tamper),
        ("Validated resume discards only the uncommitted suffix", Tail),
        ("Parallel ranges assemble out of order with bounded concurrency", Parallel),
        ("Unsupported initial parallel ranges safely fall back to full GET", ParallelFallback),
        ("Changed parallel response cannot publish mixed content", ParallelChanged),
        ("Checkpoint data and URL are protected with Windows DPAPI", ProtectedCheckpoint),
        ("Paused checkpoint survives recreation without automatic network", PausedRecovery),
        ("In-flight checkpoint becomes queued and resumes explicitly", InFlightRecovery),
        ("Corrupt checkpoint blocks downloads and preserves files", Corrupt),
        ("Recovery directory has one owner", SingleOwner),
        ("Rename-before-completion crash reconciles without network", PromotionRecovery),
        ("Real process crash recovers flushed prefix and completes", CrashRecovery),
        ("SQLite WAL process crash recovers flushed prefix and completes", () => CheckpointCases.Crash(true)),
        ("Real pause acknowledges sealed data and resumes", () => CheckpointCases.LivePause(false)),
        ("Graceful shutdown preserves active transfer for explicit resume", () => CheckpointCases.LivePause(true)),
        ("Uncommitted pending journal is ignored without network", CheckpointCases.Pending)
    };
    private static void Check(bool valid, string message = "Resume assertion failed.") { if (!valid) throw new Exception(message); }
    private static async Task Error(DownloadErrorCode code, Func<Task> operation)
    {
        try { await operation(); }
        catch (DownloadEngineException ex)
        {
            Check(ex.Code == code, $"Expected {code}; got {ex.Code}.");
            Check(!ex.ToString().Contains("protected-checkpoint-secret")); return;
        }
        throw new Exception($"Expected {code}.");
    }
    private static HttpDownloadEngine Engine(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send), TimeSpan.FromSeconds(3));
    private static HttpResponseMessage Response(byte[] body, string? tag = "\"v1\"", HttpStatusCode code = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(code) { Content = new ByteArrayContent(body) };
        if (tag is not null) response.Headers.ETag = new(tag); return response;
    }
    private static HttpResponseMessage Part(byte[] all, long start, long end, string? tag = "\"v1\"")
    {
        var response = Response(all.AsSpan((int)start, (int)(end - start + 1)).ToArray(), tag, HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange = new(start, end, all.Length); return response;
    }
    private static DownloadExecutionContext Context(Folder folder, byte[] all, int prefix = 3, bool createPartial = true)
    {
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var job = new DownloadJobRecord
        {
            Id = id, SourceKey = "opaque", DisplayHost = Source.Host, CreatedUtc = now, UpdatedUtc = now,
            DestinationPath = Path.Combine(folder.Root, "output.bin"), TemporaryPath = Path.Combine(folder.Root, $".{id:N}.dkdownload.part"),
            ExpectedBytes = all.Length, ETag = "\"v1\"", CheckpointBytes = prefix,
            CheckpointSha256 = Hash(all.AsSpan(0, prefix)), State = DownloadJobState.Paused
        };
        if (createPartial) File.WriteAllBytes(job.TemporaryPath, all.AsSpan(0, prefix).ToArray());
        return new(job, new(Source), new(all.Length, job.ETag, null, DownloadResumeCapability.Unknown, null), prefix, createPartial);
    }
    private static readonly IProgress<DownloadProgress> Ignore = new ProgressSink(_ => { });
    private static async Task Resume()
    {
        using var folder = new Folder(); var context = Context(folder, Payload);
        using var engine = Engine((request, _) =>
        {
            Check(request.Headers.Range?.Ranges.Single().From == 3 && request.Headers.IfRange?.EntityTag?.Tag == "\"v1\"");
            return Task.FromResult(Part(Payload, 3, 6));
        });
        var result = await engine.TransferAsync(context, Ignore, default);
        Check(result.TotalBytesWritten == 7 && result.ComputedSha256 == Hash(Payload) && File.ReadAllBytes(context.Job.TemporaryPath).SequenceEqual(Payload));
    }
    private static async Task Ignored()
    {
        using var folder = new Folder(); var context = Context(folder, Payload);
        await File.AppendAllTextAsync(context.Job.TemporaryPath, "uncommitted");
        var before = await File.ReadAllBytesAsync(context.Job.TemporaryPath);
        using var engine = Engine((_, _) => Task.FromResult(Response(Payload)));
        await Error(DownloadErrorCode.RangeNotSupported, () => engine.TransferAsync(context, Ignore, default));
        Check(File.ReadAllBytes(context.Job.TemporaryPath).SequenceEqual(before));
    }
    private static async Task RangeErrors()
    {
        foreach (var (status, error) in new[] { (401, DownloadErrorCode.HttpUnauthorized),
            (403, DownloadErrorCode.HttpForbidden), (404, DownloadErrorCode.HttpNotFound),
            (429, DownloadErrorCode.HttpRateLimited), (503, DownloadErrorCode.HttpServerError) })
        {
            using var folder = new Folder();
            var context = Context(folder, Payload);
            var before = await File.ReadAllBytesAsync(context.Job.TemporaryPath);
            using var engine = Engine((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
            await Error(error, () => engine.TransferAsync(context, Ignore, default));
            Check(before.SequenceEqual(await File.ReadAllBytesAsync(context.Job.TemporaryPath)));
        }
    }
    private static async Task Malformed()
    {
        foreach (var kind in new[] { "missing", "start", "end", "size", "unit", "multipart" })
        {
            using var folder = new Folder(); var context = Context(folder, Payload);
            using var engine = Engine((_, _) =>
            {
                var response = Part(Payload, 3, 6);
                switch (kind)
                {
                    case "missing": response.Content.Headers.ContentRange = null; break;
                    case "start": response.Content.Headers.ContentRange = new(2, 5, 7); break;
                    case "end": response.Content.Headers.ContentRange = new(3, 5, 7); break;
                    case "size": response.Content.Headers.ContentRange = new(3, 6, 8); break;
                    case "unit": response.Content.Headers.ContentRange!.Unit = "items"; break;
                    case "multipart": response.Content.Headers.ContentType = new("multipart/byteranges"); break;
                }
                return Task.FromResult(response);
            });
            await Error(DownloadErrorCode.PartialFileInvalid, () => engine.TransferAsync(context, Ignore, default));
            Check(new FileInfo(context.Job.TemporaryPath).Length == 3);
        }
    }
    private static async Task Changed()
    {
        using var folder = new Folder(); var context = Context(folder, Payload);
        using var engine = Engine((_, _) => Task.FromResult(Part(Payload, 3, 6, "\"v2\"")));
        await Error(DownloadErrorCode.RemoteContentChanged, () => engine.TransferAsync(context, Ignore, default));
        await Error(DownloadErrorCode.RemoteContentChanged, () => engine.TransferAsync(context with { Probe = context.Probe with { ETag = "\"v2\"" } }, Ignore, default));
        Check(File.ReadAllText(context.Job.TemporaryPath) == "pay");
    }
    private static async Task Weak()
    {
        using var folder = new Folder(); var context = Context(folder, Payload);
        var modified = DateTimeOffset.UnixEpoch;
        context = context with { Job = context.Job with { ETag = "W/\"v1\"", LastModifiedUtc = modified, ResponseDateUtc = modified.AddMinutes(5) },
            Probe = context.Probe with { ETag = "W/\"v1\"", LastModifiedUtc = modified, ResponseDateUtc = modified.AddMinutes(5) } };
        using var engine = Engine((_, _) => throw new Exception("Weak validator sent."));
        await Error(DownloadErrorCode.RangeNotSupported, () => engine.TransferAsync(context, Ignore, default));
    }
    private static async Task RedundantDate()
    {
        using var folder = new Folder(); var context = Context(folder, Payload);
        context = context with { Job = context.Job with { LastModifiedUtc = DateTimeOffset.UnixEpoch },
            Probe = context.Probe with { LastModifiedUtc = DateTimeOffset.UnixEpoch } };
        using var engine = Engine((_, _) => Task.FromResult(Part(Payload, 3, 6)));
        Check((await engine.TransferAsync(context, Ignore, default)).ComputedSha256 == Hash(Payload));
    }
    private static async Task ChangedTarget()
    {
        using var folder = new Folder(); var context = Context(folder, Payload);
        context = context with { Job = context.Job with { ResourceKey = "previous-resource" },
            Probe = context.Probe with { ResourceKey = "different-resource" } };
        using var engine = Engine((_, _) => throw new Exception("Changed redirect target sent."));
        await Error(DownloadErrorCode.RemoteContentChanged, () => engine.TransferAsync(context, Ignore, default));
    }
    private static async Task DateValidator()
    {
        using var folder = new Folder(); var context = Context(folder, Payload); var modified = DateTimeOffset.UnixEpoch;
        context = context with { Job = context.Job with { ETag = null, LastModifiedUtc = modified, ResponseDateUtc = modified.AddMinutes(5) },
            Probe = context.Probe with { ETag = null, LastModifiedUtc = modified, ResponseDateUtc = modified.AddMinutes(5) } };
        using var engine = Engine((request, _) =>
        {
            Check(request.Headers.IfRange?.Date == modified);
            var response = Part(Payload, 3, 6, null); response.Content.Headers.LastModified = modified;
            return Task.FromResult(response);
        });
        Check((await engine.TransferAsync(context, Ignore, default)).ComputedSha256 == Hash(Payload));
    }
    private static async Task AmbiguousDate()
    {
        using var folder = new Folder(); var context = Context(folder, Payload); var modified = DateTimeOffset.UnixEpoch;
        context = context with { Job = context.Job with { ETag = null, LastModifiedUtc = modified, ResponseDateUtc = modified },
            Probe = context.Probe with { ETag = null, LastModifiedUtc = modified, ResponseDateUtc = modified } };
        using var engine = Engine((_, _) => throw new Exception("Ambiguous validator sent."));
        await Error(DownloadErrorCode.RangeNotSupported, () => engine.TransferAsync(context, Ignore, default));
    }
    private static async Task Unsatisfied()
    {
        foreach (var size in new[] { 7, 8 })
        {
            using var folder = new Folder(); var context = Context(folder, Payload, 7);
            using var engine = Engine((_, _) =>
            {
                var response = Response(Array.Empty<byte>(), code: HttpStatusCode.RequestedRangeNotSatisfiable);
                response.Content.Headers.ContentRange = new(size); return Task.FromResult(response);
            });
            if (size == 7) Check((await engine.TransferAsync(context, Ignore, default)).ComputedSha256 == Hash(Payload));
            else await Error(DownloadErrorCode.RemoteContentChanged, () => engine.TransferAsync(context, Ignore, default));
        }
    }
    private static async Task Tamper()
    {
        using var folder = new Folder(); var context = Context(folder, Payload);
        await File.WriteAllTextAsync(context.Job.TemporaryPath, "bad");
        using var engine = Engine((_, _) => throw new Exception("Tampered prefix sent."));
        await Error(DownloadErrorCode.PartialFileInvalid, () => engine.TransferAsync(context, Ignore, default));
    }
    private static async Task Tail()
    {
        using var folder = new Folder(); var context = Context(folder, Payload);
        await File.AppendAllTextAsync(context.Job.TemporaryPath, "discard-only-after-valid-response");
        using var engine = Engine((_, _) => Task.FromResult(Part(Payload, 3, 6)));
        Check((await engine.TransferAsync(context, Ignore, default)).ComputedSha256 == Hash(Payload));
        Check(File.ReadAllBytes(context.Job.TemporaryPath).SequenceEqual(Payload));
    }
    private static async Task Parallel()
    {
        using var folder = new Folder(); var data = Enumerable.Range(0, 5 * 1024 * 1024 + 17).Select(i => (byte)(i % 251)).ToArray();
        var context = Context(folder, data, 0, false); context = context with { Job = context.Job with { ParallelSegments = 4 } };
        var active = 0; var peak = 0;
        using var engine = Engine(async (request, token) =>
        {
            var count = Interlocked.Increment(ref active); peak = Math.Max(peak, count);
            try
            {
                var range = request.Headers.Range!.Ranges.Single();
                await Task.Delay(15 + (int)(range.From!.Value % 3) * 15, token);
                return Part(data, range.From.Value, range.To!.Value);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var progress = new List<long>();
        var result = await engine.TransferAsync(context, new ProgressSink(p => progress.Add(p.TransferredBytes)), default);
        Check(result.ComputedSha256 == Hash(data) && peak >= 2 && peak <= 4 && progress.SequenceEqual(progress.Order()));
    }
    private static async Task ParallelFallback()
    {
        using var folder = new Folder(); var data = new byte[2 * 1024 * 1024];
        var context = Context(folder, data, 0, false); context = context with { Job = context.Job with { ParallelSegments = 2 } };
        var full = 0;
        using var engine = Engine((request, _) => { if (request.Headers.Range is null) full++; return Task.FromResult(Response(data)); });
        Check((await engine.TransferAsync(context, Ignore, default)).ComputedSha256 == Hash(data) && full == 1);
    }
    private static async Task ParallelChanged()
    {
        using var folder = new Folder(); var data = new byte[2 * 1024 * 1024];
        var context = Context(folder, data, 0, false); context = context with { Job = context.Job with { ParallelSegments = 2 } };
        using var engine = Engine((request, _) =>
        { var range = request.Headers.Range!.Ranges.Single(); return Task.FromResult(Part(data, range.From!.Value, range.To!.Value, "\"changed\"")); });
        await Error(DownloadErrorCode.RemoteContentChanged, () => engine.TransferAsync(context, Ignore, default));
        Check(new FileInfo(context.Job.TemporaryPath).Length == 0);
    }

    // Checkpoint and real-process cases below use an isolated test-owned directory.
    private static Task ProtectedCheckpoint() => CheckpointCases.Protected();
    private static Task PausedRecovery() => CheckpointCases.Recover(false);
    private static Task InFlightRecovery() => CheckpointCases.Recover(true);
    private static Task Corrupt() => CheckpointCases.Corrupt();
    private static Task SingleOwner() => CheckpointCases.SingleOwner();
    private static Task PromotionRecovery() => CheckpointCases.Promotion();
    private static Task CrashRecovery() => CheckpointCases.Crash();
    internal static Task<int> RunCrashWorker(string[] args) => CheckpointCases.Worker(args);

    internal sealed class Folder : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "dk-resume-tests-" + Guid.NewGuid().ToString("N"));
        internal string Journal => Path.Combine(Root, "checkpoints");
        public Folder() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Unexpected test cleanup target.");
            Directory.Delete(Root, true);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class ProgressSink(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }

    private static class CheckpointCases
    {
        internal static async Task Pending()
        {
            using var folder = new Folder(); Directory.CreateDirectory(folder.Journal);
            await File.WriteAllTextAsync(Path.Combine(folder.Journal, "orphan.pending"), "uncommitted generated fixture");
            using var engine = Engine((_, _) => throw new Exception("Pending record used network."));
            await using var coordinator = new DownloadCoordinator(engine, new DownloadCheckpointStore(folder.Journal));
            Check(!coordinator.RecoveryBlocked && (await coordinator.ListAsync()).Count == 0);
        }
        internal static async Task LivePause(bool shutdown)
        {
            using var folder = new Folder(); await using var server = new RangeFixture(4 * 1024 * 1024);
            var coordinator = new DownloadCoordinator(new HttpDownloadEngine(), new DownloadCheckpointStore(folder.Journal));
            try
            {
                var job = await coordinator.EnqueueAsync(new(server.Url, folder.Root, "paused.bin"));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while ((await coordinator.GetAsync(job.Id))!.TransferredBytes < 64 * 1024) await Task.Delay(10, timeout.Token);
                if (shutdown) await coordinator.DisposeAsync(); else await coordinator.PauseAsync(job.Id);
                var paused = (await coordinator.GetAsync(job.Id))!;
                Check(paused.State == DownloadJobState.Paused && paused.TransferredBytes > 0 && paused.CanResume);
                var count = new FileInfo(Path.Combine(folder.Root, $".{job.Id:N}.dkdownload.part")).Length;
                await Task.Delay(30); Check(count == new FileInfo(Path.Combine(folder.Root, $".{job.Id:N}.dkdownload.part")).Length);
                if (shutdown)
                {
                    await using var recovered = new DownloadCoordinator(new HttpDownloadEngine(), new DownloadCheckpointStore(folder.Journal));
                    Check((await recovered.GetAsync(job.Id))!.State == DownloadJobState.Paused);
                    await recovered.ResumeAsync(job.Id);
                    Check((await WaitDone(recovered, job.Id)).State == DownloadJobState.Completed);
                }
                else
                {
                    await coordinator.ResumeAsync(job.Id);
                    Check((await WaitDone(coordinator, job.Id)).State == DownloadJobState.Completed);
                }
                Check(Hash(File.ReadAllBytes(Path.Combine(folder.Root, "paused.bin"))) == Hash(server.Data));
                Check(server.Requests.Any(start => start == count));
            }
            finally { await coordinator.DisposeAsync(); }
        }
        internal static Task Protected()
        {
            using var folder = new Folder(); var context = Context(folder, Payload);
            using var store = new DownloadCheckpointStore(folder.Journal); store.Save(context.Job, context.Source);
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(folder.Journal, "*.checkpoint").Single()));
            Check(!raw.Contains("protected-checkpoint-secret") && !raw.Contains("DestinationPath"));
            var saved = store.Load().Single(); Check(saved.AuthorizedUrl == Source.AbsoluteUri && saved.Job.CheckpointBytes == 3);
            return Task.CompletedTask;
        }
        internal static async Task Recover(bool inFlight)
        {
            using var folder = new Folder(); var context = Context(folder, Payload);
            using (var store = new DownloadCheckpointStore(folder.Journal))
                store.Save(context.Job with { State = inFlight ? DownloadJobState.Downloading : DownloadJobState.Paused }, context.Source);
            var calls = 0;
            using var engine = Engine((request, _) =>
            {
                calls++;
                return Task.FromResult(request.Method == HttpMethod.Head ? Response(Payload) : Part(Payload, 3, 6));
            });
            await using var coordinator = new DownloadCoordinator(engine, new DownloadCheckpointStore(folder.Journal));
            var restored = (await coordinator.ListAsync()).Single();
            Check(calls == 0 && restored.State == (inFlight ? DownloadJobState.Queued : DownloadJobState.Paused) && restored.CanResume);
            await coordinator.ResumeAsync(restored.Id);
            var done = await WaitDone(coordinator, restored.Id);
            Check(done.State == DownloadJobState.Completed, $"Recovered transfer failed: {done.ErrorCode}");
            Check(File.ReadAllBytes(context.Job.DestinationPath).SequenceEqual(Payload));
        }
        internal static async Task Corrupt()
        {
            using var folder = new Folder(); var context = Context(folder, Payload);
            using (var store = new DownloadCheckpointStore(folder.Journal)) store.Save(context.Job, context.Source);
            var path = Directory.GetFiles(folder.Journal, "*.checkpoint").Single(); var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 2] ^= 0x80; File.WriteAllBytes(path, bytes);
            using var engine = Engine((_, _) => throw new Exception("Corrupt checkpoint used network."));
            await using var coordinator = new DownloadCoordinator(engine, new DownloadCheckpointStore(folder.Journal));
            Check(coordinator.RecoveryBlocked && File.ReadAllBytes(path).SequenceEqual(bytes));
            await Error(DownloadErrorCode.StorageFailure, () => coordinator.EnqueueAsync(new(Source, folder.Root, "new.bin")));
        }
        internal static Task SingleOwner()
        {
            using var folder = new Folder(); using var owner = new DownloadCheckpointStore(folder.Journal);
            try { using var second = new DownloadCheckpointStore(folder.Journal); throw new Exception("Duplicate owner allowed."); }
            catch (IOException) { }
            return Task.CompletedTask;
        }
        internal static async Task Promotion()
        {
            using var folder = new Folder(); var context = Context(folder, Payload, 7);
            var job = context.Job with { State = DownloadJobState.Verifying, VerifiedSha256 = Hash(Payload) };
            using (var store = new DownloadCheckpointStore(folder.Journal))
            {
                store.Save(job, context.Source);
                using var verified = DownloadStagingFile.Verify(job, new(7, Hash(Payload)), default); verified.Promote(job);
            }
            using var engine = Engine((_, _) => throw new Exception("Promotion recovery used network."));
            await using var coordinator = new DownloadCoordinator(engine, new DownloadCheckpointStore(folder.Journal));
            Check(!coordinator.RecoveryBlocked && (await coordinator.ListAsync()).Single().State == DownloadJobState.Completed);
        }
        internal static async Task<int> Worker(string[] args)
        {
            IDownloadPersistence store = args[0] == "--queue-crash-worker" ? new DownloadJobStore(args[1]) : new DownloadCheckpointStore(args[1]);
            await using var coordinator = new DownloadCoordinator(new HttpDownloadEngine(), store);
            await coordinator.EnqueueAsync(new(new Uri(args[2]), args[3], "crash-output.bin"));
            await Task.Delay(Timeout.Infinite); return 0;
        }
        internal static async Task Crash(bool database = false)
        {
            using var folder = new Folder(); await using var server = new RangeFixture();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var argument in new[] { database ? "--queue-crash-worker" : "--crash-worker", folder.Journal, server.Url.AbsoluteUri, folder.Root }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            var lastCheckpoint = "No record";
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (true)
                {
                    if (timeout.IsCancellationRequested) throw new Exception("Crash worker did not seal a prefix: " + lastCheckpoint);
                    byte[]? encrypted = null;
                    if (database && File.Exists(Path.Combine(folder.Journal, "downloads.db")))
                    {
                        try
                        {
                            using var db = new DownloadSqlite(Path.Combine(folder.Journal, "downloads.db"));
                            using var query = db.Prepare("SELECT protected_record FROM download_jobs");
                            if (query.Read()) encrypted = query.Bytes(0);
                        }
                        catch (DownloadEngineException) { /* Initial schema transaction may not be committed yet. */ }
                    }
                    else if (!database && Directory.Exists(folder.Journal))
                    {
                        var path = Directory.GetFiles(folder.Journal, "*.checkpoint").SingleOrDefault();
                        if (path is not null)
                        { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var buffer = new MemoryStream(); stream.CopyTo(buffer); encrypted = buffer.ToArray(); }
                    }
                    if (encrypted is not null)
                    {
                        var entropy = database ? "DKDesktopEssentials.DownloadQueue.v2"u8.ToArray() : "DKDesktopEssentials.DownloadCheckpoint.v1"u8.ToArray();
                        var plain = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
                        try
                        {
                            using var json = JsonDocument.Parse(plain);
                            var record = database ? json.RootElement : json.RootElement.GetProperty("Job");
                            lastCheckpoint = $"{record.GetProperty("State")} / {record.GetProperty("LastErrorCode")} / {record.GetProperty("CheckpointBytes")}";
                            if (record.GetProperty("CheckpointBytes").GetInt64() >= 1024 * 1024) break;
                        }
                        finally { CryptographicOperations.ZeroMemory(plain); }
                    }
                    if (child.HasExited) throw new Exception("Crash fixture exited before checkpoint.");
                    await Task.Delay(20);
                }
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
                IDownloadPersistence recoveredStore = database ? new DownloadJobStore(folder.Journal) : new DownloadCheckpointStore(folder.Journal);
                await using var coordinator = new DownloadCoordinator(new HttpDownloadEngine(), recoveredStore);
                Check(!coordinator.RecoveryBlocked);
                var restored = (await coordinator.ListAsync()).Single(); var requests = server.Requests.Count;
                await Task.Delay(50);
                Check(restored.State == DownloadJobState.Queued && restored.TransferredBytes >= 1024 * 1024 && server.Requests.Count == requests);
                await coordinator.ResumeAsync(restored.Id);
                var done = await WaitDone(coordinator, restored.Id);
                Check(done.State == DownloadJobState.Completed, $"Crash recovery failed: {done.ErrorCode}");
                Check(server.Requests.Any(r => r == restored.TransferredBytes));
                Check(Hash(File.ReadAllBytes(Path.Combine(folder.Root, "crash-output.bin"))) == Hash(server.Data));
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        }
        private static async Task<DownloadJobView> WaitDone(DownloadCoordinator coordinator, Guid id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var view = (await coordinator.GetAsync(id))!;
                if (view.State is DownloadJobState.Completed or DownloadJobState.Failed) return view;
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
