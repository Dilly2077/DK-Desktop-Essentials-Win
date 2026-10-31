using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DKDesktopEssentials.Modules.Downloads;

internal static class CoordinatorCases
{
    private static readonly Uri Source = new("https://example.org/private-path?token=secret-source-token");
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData("payload"u8)).ToLowerInvariant();
    public static IEnumerable<(string Name, Func<Task> Run)> All => new (string, Func<Task>)[]
    {
        ("Coordinator verifies and promotes with Internet origin metadata", Complete),
        ("Expected checksum mismatch retains staging and fails", WrongHash),
        ("Engine success without authentic bytes cannot complete", ForgedResult),
        ("Late native rename collision never overwrites destination", LateCollision),
        ("Coordinator cancellation acknowledges stopped worker", Cancel),
        ("Progress exposes honest speed ETA and revision", Progress),
        ("Unknown size has unknown ETA during transfer", UnknownSize),
        ("Shutdown pauses and waits; rejects further work", Shutdown),
        ("Explicit input validation precedes engine use", InputValidation),
        ("Active destination reservation rejects duplicate", Reservation),
        ("Unexpected engine errors are redacted in job views", RedactedFailure),
        ("Failed jobs can be explicitly cancelled", CancelFailed),
        ("Pre-cancelled cancel request leaves worker running", PreCancelledRequest),
        ("Admission queues work beyond the active transfer limit", Admission),
        ("Bridge enforces origin and native destination grants", Bridge),
        ("Bridge rejects malformed and unsupported commands", BadBridge),
        ("Coordinator real HTTP transfer completes on disk", Loopback)
    };
    private static void Check(bool value, string message = "Coordinator assertion failed.")
    { if (!value) throw new Exception(message); }
    private static async Task<DownloadJobView> Done(DownloadCoordinator coordinator, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var job = (await coordinator.GetAsync(id))!;
            if (job.State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Cancelled) return job;
            await Task.Delay(10, timeout.Token);
        }
    }
    private static async Task Error(DownloadErrorCode code, Func<Task> operation)
    {
        try { await operation(); }
        catch (DownloadEngineException ex) { Check(ex.Code == code, $"Expected {code}; got {ex.Code}."); return; }
        throw new Exception("Expected rejection.");
    }
    private static async Task Complete()
    {
        using var folder = new Folder();
        await using var coordinator = new DownloadCoordinator(new Fake());
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin", ExpectedSha256: Digest.ToUpperInvariant()));
        var done = await Done(coordinator, job.Id);
        Check(done.State == DownloadJobState.Completed, $"Completion failed: {done.ErrorCode}");
        Check(File.ReadAllText(System.IO.Path.Combine(folder.Path, "output.bin")) == "payload");
        Check(File.ReadAllText(System.IO.Path.Combine(folder.Path, "output.bin:Zone.Identifier")).Contains("ZoneId=3"));
        Check(!File.Exists(System.IO.Path.Combine(folder.Path, $".{job.Id:N}.dkdownload.part")));
        var json = JsonSerializer.Serialize(done);
        Check(!json.Contains("secret-source-token") && !json.Contains("private-path") && !json.Contains(folder.Path));
        Check((await coordinator.CancelAsync(job.Id)).State == DownloadJobState.Completed);
    }
    private static async Task WrongHash()
    {
        using var folder = new Folder();
        await using var coordinator = new DownloadCoordinator(new Fake());
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin", ExpectedSha256: new string('0', 64)));
        var done = await Done(coordinator, job.Id);
        Check(done.State == DownloadJobState.Failed && done.ErrorCode == DownloadErrorCode.ChecksumMismatch);
        Check(!File.Exists(System.IO.Path.Combine(folder.Path, "output.bin")) && File.Exists(System.IO.Path.Combine(folder.Path, $".{job.Id:N}.dkdownload.part")));
    }
    private static async Task ForgedResult()
    {
        using var folder = new Folder();
        var fake = new Fake { Transfer = async (context, _, token) =>
        {
            await File.WriteAllTextAsync(context.Job.TemporaryPath, "altered", token);
            return new(7, Digest);
        } };
        await using var coordinator = new DownloadCoordinator(fake);
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin"));
        var done = await Done(coordinator, job.Id);
        Check(done.State == DownloadJobState.Failed && done.ErrorCode == DownloadErrorCode.ChecksumMismatch);
    }
    private static async Task LateCollision()
    {
        using var folder = new Folder();
        var id = Guid.NewGuid();
        var job = new DownloadJobRecord { Id = id, SourceKey = "opaque", DisplayHost = "example.org",
            DestinationPath = System.IO.Path.Combine(folder.Path, "output.bin"),
            TemporaryPath = System.IO.Path.Combine(folder.Path, $".{id:N}.dkdownload.part"),
            CreatedUtc = DateTimeOffset.UnixEpoch, UpdatedUtc = DateTimeOffset.UnixEpoch };
        await File.WriteAllTextAsync(job.TemporaryPath, "payload");
        using var verified = DownloadStagingFile.Verify(job, new(7, Digest), default);
        await File.WriteAllTextAsync(job.DestinationPath, "keep-existing");
        await Error(DownloadErrorCode.DestinationExists, () => { verified.Promote(job); return Task.CompletedTask; });
        Check(File.ReadAllText(job.DestinationPath) == "keep-existing");
    }
    private static Fake Blocking(TaskCompletionSource started, bool known = true) => new()
    {
        Probe = _ => Task.FromResult(new DownloadProbe(known ? 10 : null, null, null, DownloadResumeCapability.Unsupported, null)),
        Transfer = async (context, progress, token) =>
        {
            await File.WriteAllTextAsync(context.Job.TemporaryPath, "part", token);
            progress.Report(new(context.Job.Id, 4, known ? 10 : null, 2));
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new Exception("Unreachable.");
        }
    };
    private static async Task Cancel()
    {
        using var folder = new Folder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DownloadCoordinator(Blocking(started));
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin"));
        await started.Task;
        var cancelled = await coordinator.CancelAsync(job.Id);
        Check(cancelled.State == DownloadJobState.Cancelled && cancelled.BytesPerSecond == 0);
        Check(!File.Exists(System.IO.Path.Combine(folder.Path, "output.bin")));
        using var stream = File.Open(System.IO.Path.Combine(folder.Path, $".{job.Id:N}.dkdownload.part"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private static async Task Progress()
    {
        using var folder = new Folder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DownloadCoordinator(Blocking(started));
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin"));
        await started.Task;
        var active = (await coordinator.GetAsync(job.Id))!;
        Check(active.State == DownloadJobState.Downloading && active.TransferredBytes == 4 && active.BytesPerSecond == 2 && active.EtaSeconds == 3 && active.Revision >= 3);
        await coordinator.CancelAsync(job.Id);
    }
    private static async Task UnknownSize()
    {
        using var folder = new Folder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DownloadCoordinator(Blocking(started, false));
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin"));
        await started.Task;
        var active = (await coordinator.GetAsync(job.Id))!;
        Check(active.ExpectedBytes is null && active.EtaSeconds is null);
        await coordinator.CancelAsync(job.Id);
    }
    private static async Task Shutdown()
    {
        using var folder = new Folder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = Blocking(started);
        var coordinator = new DownloadCoordinator(fake);
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin"));
        await started.Task;
        await Task.WhenAll(coordinator.DisposeAsync().AsTask(), coordinator.DisposeAsync().AsTask());
        Check(fake.Disposed && (await coordinator.GetAsync(job.Id))!.State == DownloadJobState.Paused);
        await Error(DownloadErrorCode.EngineUnavailable, () => coordinator.EnqueueAsync(new(Source, folder.Path, "other.bin")));
    }
    private static async Task InputValidation()
    {
        using var folder = new Folder();
        var fake = new Fake();
        await using var coordinator = new DownloadCoordinator(fake);
        await Error(DownloadErrorCode.UnsupportedProtocol, () => coordinator.EnqueueAsync(new(new("file:///C:/secret"), folder.Path, "x")));
        await Error(DownloadErrorCode.DestinationInvalid, () => coordinator.EnqueueAsync(new(Source, folder.Path, "../x")));
        await Error(DownloadErrorCode.ChecksumMismatch, () => coordinator.EnqueueAsync(new(Source, folder.Path, "x", ExpectedSha256: "bad")));
        Check(fake.Probes == 0 && Directory.GetFiles(folder.Path).Length == 0);
    }
    private static async Task Reservation()
    {
        using var folder = new Folder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DownloadCoordinator(Blocking(started));
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin"));
        await started.Task;
        await Error(DownloadErrorCode.DestinationExists, () => coordinator.EnqueueAsync(new(Source, folder.Path, "OUTPUT.bin")));
        await coordinator.CancelAsync(job.Id);
    }
    private static async Task RedactedFailure()
    {
        using var folder = new Folder();
        await using var coordinator = new DownloadCoordinator(new Fake { Probe = _ => throw new Exception("secret-source-token private-path") });
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin"));
        var done = await Done(coordinator, job.Id);
        Check(done.State == DownloadJobState.Failed && done.ErrorCode == DownloadErrorCode.UnexpectedFailure);
        Check(!JsonSerializer.Serialize(done).Contains("secret-source-token"));
    }
    private static async Task CancelFailed()
    {
        using var folder = new Folder();
        await using var coordinator = new DownloadCoordinator(new Fake { Probe = _ => throw new Exception() });
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin"));
        Check((await Done(coordinator, job.Id)).State == DownloadJobState.Failed);
        Check((await coordinator.CancelAsync(job.Id)).State == DownloadJobState.Cancelled);
    }
    private static async Task PreCancelledRequest()
    {
        using var folder = new Folder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DownloadCoordinator(Blocking(started));
        var job = await coordinator.EnqueueAsync(new(Source, folder.Path, "output.bin"));
        await started.Task;
        using var token = new CancellationTokenSource(); token.Cancel();
        try { await coordinator.CancelAsync(job.Id, token.Token); throw new Exception("Expected operation cancellation."); }
        catch (OperationCanceledException) { }
        Check((await coordinator.GetAsync(job.Id))!.State == DownloadJobState.Downloading);
        await coordinator.CancelAsync(job.Id);
    }
    private static async Task Admission()
    {
        using var folder = new Folder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = Blocking(started);
        await using var coordinator = new DownloadCoordinator(fake);
        for (var i = 0; i < 3; i++) await coordinator.EnqueueAsync(new(Source, folder.Path, $"output-{i}.bin"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (fake.Probes < 3) await Task.Delay(10, timeout.Token);
        var fourth = await coordinator.EnqueueAsync(new(Source, folder.Path, "fourth.bin"));
        Check(fourth.State == DownloadJobState.Queued && (await coordinator.ListAsync()).Count == 4 && fake.Probes == 3);
    }
    private static string Command(string command, object? payload = null) => JsonSerializer.Serialize(new { type = "downloads:" + command, requestId = "test-id", payload });
    private const string Origin = "https://dk.local/index.html";
    private static async Task Bridge()
    {
        using var folder = new Folder();
        await using var coordinator = new DownloadCoordinator(new Fake());
        var picks = 0;
        var bridge = new DownloadWebBridge(coordinator, () => { picks++; return Task.FromResult<string?>(folder.Path); });
        Check(await bridge.HandleAsync(Command("chooseDestination"), "https://evil.example/index.html") is null && picks == 0);
        using var grant = JsonDocument.Parse((await bridge.HandleAsync(Command("chooseDestination"), Origin))!);
        var key = grant.RootElement.GetProperty("result").GetProperty("destinationToken").GetString();
        var reply = (await bridge.HandleAsync(Command("enqueue", new { destinationToken = key, url = Source.ToString(), fileName = "output.bin" }), Origin))!;
        Check(!reply.Contains("secret-source-token") && !reply.Contains("private-path") && !reply.Contains("temporaryPath"));
        using var submitted = JsonDocument.Parse(reply);
        Check(submitted.RootElement.GetProperty("ok").GetBoolean());
        var id = submitted.RootElement.GetProperty("result").GetProperty("id").GetGuid();
        Check((await Done(coordinator, id)).State == DownloadJobState.Completed);
        var denied = (await bridge.HandleAsync(Command("enqueue", new { destinationToken = "made-up", destinationDirectory = folder.Path, url = Source.ToString(), fileName = "x" }), Origin))!;
        Check(denied.Contains("DestinationInvalid"));
    }
    private static async Task BadBridge()
    {
        await using var coordinator = new DownloadCoordinator(new Fake());
        var bridge = new DownloadWebBridge(coordinator, () => Task.FromResult<string?>(null));
        foreach (var body in new[] { "[]", "null", "{", "\"text\"", "{}" }) Check(await bridge.HandleAsync(body, Origin) is null);
        Check((await bridge.HandleAsync(Command("bandwidth"), Origin))!.Contains("not available"));
        Check((await bridge.HandleAsync(Command("enqueue"), Origin))!.Contains("\"ok\":false"));
        Check((await bridge.HandleAsync(Command("chooseDestination"), Origin))!.Contains("\"result\":null"));
    }
    private static async Task Loopback()
    {
        using var folder = new Folder();
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await server.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
                string? line;
                do { line = await reader.ReadLineAsync(timeout.Token); } while (!string.IsNullOrEmpty(line));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\n" + (i == 0 ? "" : "payload")), timeout.Token);
            }
        });
        await using var coordinator = new DownloadCoordinator(new HttpDownloadEngine());
        var url = new Uri($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/data");
        var job = await coordinator.EnqueueAsync(new(url, folder.Path, "output.bin"));
        var done = await Done(coordinator, job.Id);
        Check(done.State == DownloadJobState.Completed, $"Real HTTP finalisation failed: {done.ErrorCode}");
        await serve;
    }
    private sealed class Fake : IDownloadEngine, IDisposable
    {
        public int Probes;
        public bool Disposed;
        public Func<CancellationToken, Task<DownloadProbe>> Probe = _ => Task.FromResult(new DownloadProbe(7, null, null, DownloadResumeCapability.Unsupported, null));
        public Func<DownloadExecutionContext, IProgress<DownloadProgress>, CancellationToken, Task<DownloadExecutionResult>> Transfer = async (context, progress, token) =>
        {
            await File.WriteAllTextAsync(context.Job.TemporaryPath, "payload", token);
            progress.Report(new(context.Job.Id, 7, 7, 7));
            return new(7, Digest);
        };
        public Task<DownloadProbe> ProbeAsync(DownloadSource source, CancellationToken token) { Probes++; return Probe(token); }
        public Task<DownloadExecutionResult> TransferAsync(DownloadExecutionContext context, IProgress<DownloadProgress> progress, CancellationToken token) => Transfer(context, progress, token);
        public void Dispose() => Disposed = true;
    }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dk-download-session-tests-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (System.IO.Path.GetDirectoryName(Path) != System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar)) throw new Exception("Unexpected cleanup root.");
            Directory.Delete(Path, true);
        }
    }
}
