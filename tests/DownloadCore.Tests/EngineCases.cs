using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DKDesktopEssentials.Modules.Downloads;

internal static class EngineCases
{
    private static readonly Uri Source = new("https://example.org/file?token=secret-test-token");
    public static IEnumerable<(string Name, Func<Task> Run)> All => new (string, Func<Task>)[]
    {
        ("Probe preserves metadata without claiming resume", Probe),
        ("HEAD unsupported uses headers-only GET fallback", Fallback),
        ("Unknown length remains unknown; unsafe name ignored", UnknownLength),
        ("Fresh transfer writes staging only and returns hash", Transfer),
        ("Existing destination and staging bytes preserved", Collisions),
        ("Invalid paths rejected before any request", UnsafePaths),
        ("Reparse directory rejected before any request", Reparse),
        ("Directory cannot be renamed during transfer", DirectoryLease),
        ("Unsupported resume never touches network or file", Resume),
        ("Unexpected partial HTTP responses rejected", Partial),
        ("Changed probe identity rejected before body write", Changed),
        ("Short and oversized bodies never report success", LengthMismatch),
        ("Cancellation preserves partial bytes and releases leases", Cancel),
        ("Idle body timeout releases the staging file", IdleTimeout),
        ("Typed HTTP errors contain no source secrets", StatusErrors),
        ("Network TLS DNS and timeout errors redacted", TransportErrors),
        ("Invalid source protocols rejected without requests", InvalidSources),
        ("Redirects bounded and do not forward credentials", Redirect),
        ("Downgrade and unsafe redirects rejected", UnsafeRedirects),
        ("Non-identity encoding rejected", EncodingRejected),
        ("Disposed engine reports unavailable", Disposed),
        ("Real loopback HTTP probe transfer and broken body", Loopback)
    };

    private static void Check(bool valid, string message = "Engine assertion failed.")
    {
        if (!valid) throw new Exception(message);
    }

    private static HttpResponseMessage Response(string body = "payload", HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private static HttpDownloadEngine Engine(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        TimeSpan? timeout = null) => new(new Handler(send), timeout ?? TimeSpan.FromSeconds(2));

    private static async Task Error(DownloadErrorCode expected, Func<Task> action)
    {
        try { await action(); }
        catch (DownloadEngineException ex)
        {
            Check(ex.Code == expected, $"Expected {expected}; got {ex.Code}.");
            Check(ex.InnerException is null && !ex.ToString().Contains("secret-test-token") &&
                !ex.ToString().Contains("private-path"), "Exception leaked source data.");
            return;
        }
        throw new Exception($"Expected {expected} failure.");
    }

    private static async Task Probe()
    {
        using var engine = Engine((request, _) =>
        {
            Check(request.Method == HttpMethod.Head && request.Headers.AcceptEncoding.ToString() == "identity");
            var response = Response();
            response.Headers.ETag = new("\"v1\"");
            response.Headers.AcceptRanges.Add("bytes");
            response.Content.Headers.LastModified = DateTimeOffset.UnixEpoch;
            response.Content.Headers.ContentDisposition = new("attachment") { FileName = "file.zip" };
            return Task.FromResult(response);
        });
        var probe = await engine.ProbeAsync(new(Source), default);
        Check(probe.ContentLength == 7 && probe.ETag == "\"v1\"" && probe.LastModifiedUtc == DateTimeOffset.UnixEpoch &&
            probe.SuggestedFileName == "file.zip" && probe.ResumeCapability == DownloadResumeCapability.Unknown);
        Check(!new DownloadSource(Source).ToString().Contains("secret-test-token"));
    }

    private static async Task Fallback()
    {
        var methods = new List<HttpMethod>();
        using var engine = Engine((request, _) =>
        {
            methods.Add(request.Method);
            return Task.FromResult(request.Method == HttpMethod.Head ? Response(status: HttpStatusCode.MethodNotAllowed) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowOnRead()) });
        });
        await engine.ProbeAsync(new(Source), default);
        Check(methods.SequenceEqual(new[] { HttpMethod.Head, HttpMethod.Get }));
    }

    private static async Task UnknownLength()
    {
        using var engine = Engine((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowOnRead()) };
            response.Content.Headers.ContentDisposition = new("attachment") { FileName = "../escape.exe" };
            return Task.FromResult(response);
        });
        var probe = await engine.ProbeAsync(new(Source), default);
        Check(probe.ContentLength is null && probe.SuggestedFileName is null);
    }

    private static async Task Transfer()
    {
        using var sandbox = new Sandbox();
        using var engine = Engine((_, _) => Task.FromResult(Response()));
        var progress = new List<DownloadProgress>();
        var context = sandbox.Context();
        var result = await engine.TransferAsync(context, new InlineProgress(progress.Add), default);
        Check(result.TotalBytesWritten == 7 && result.ComputedSha256 ==
            Convert.ToHexString(SHA256.HashData("payload"u8)).ToLowerInvariant());
        Check(File.ReadAllText(context.Job.TemporaryPath) == "payload" && !File.Exists(context.Job.DestinationPath));
        Check(context.Job.State == DownloadJobState.Downloading && progress[0].TransferredBytes == 0 &&
            progress[^1].TransferredBytes == 7 && progress.All(p => p.ExpectedBytes == 7));
        await Error(DownloadErrorCode.PartialFileInvalid, () => engine.TransferAsync(context, NoProgress, default));
    }

    private static async Task Collisions()
    {
        using var sandbox = new Sandbox();
        var requests = 0;
        using var engine = Engine((_, _) => { requests++; return Task.FromResult(Response()); });
        var context = sandbox.Context();
        await File.WriteAllTextAsync(context.Job.DestinationPath, "keep-destination");
        await Error(DownloadErrorCode.DestinationExists, () => engine.TransferAsync(context, NoProgress, default));
        Check(File.ReadAllText(context.Job.DestinationPath) == "keep-destination" && !File.Exists(context.Job.TemporaryPath));
        File.Delete(context.Job.DestinationPath);
        await File.WriteAllTextAsync(context.Job.TemporaryPath, "keep-partial");
        await Error(DownloadErrorCode.PartialFileInvalid, () => engine.TransferAsync(context, NoProgress, default));
        Check(File.ReadAllText(context.Job.TemporaryPath) == "keep-partial" && requests == 0);
    }

    private static async Task UnsafePaths()
    {
        using var sandbox = new Sandbox();
        var requests = 0;
        using var engine = Engine((_, _) => { requests++; return Task.FromResult(Response()); });
        var context = sandbox.Context();
        foreach (var file in new[] { "..\\escape", "NUL.txt", "CON .txt", "COM1.zip", "trailing.", "x:stream", "LPT¹.txt" })
            await Error(DownloadErrorCode.DestinationInvalid, () => engine.TransferAsync(context with
            { Job = context.Job with { DestinationPath = Path.Combine(sandbox.Root, file) } }, NoProgress, default));
        await Error(DownloadErrorCode.DestinationInvalid, () => engine.TransferAsync(context with
        { Job = context.Job with { TemporaryPath = context.Job.DestinationPath } }, NoProgress, default));
        await Error(DownloadErrorCode.DestinationInvalid, () => engine.TransferAsync(context with
        { Job = context.Job with { DestinationPath = context.Job.TemporaryPath } }, NoProgress, default));
        await Error(DownloadErrorCode.DestinationInvalid, () => engine.TransferAsync(context with
        { Job = context.Job with { TemporaryPath = @"\\server\share\test.part" } }, NoProgress, default));
        Check(requests == 0 && Directory.GetFiles(sandbox.Root).Length == 0);
    }

    private static async Task Reparse()
    {
        using var sandbox = new Sandbox();
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Root, "target")).FullName;
        var link = Path.Combine(sandbox.Root, "junction");
        // Directory junction creation needs no developer mode/admin. Only the test-owned
        // link is removed, without recursion, before Sandbox disposes its own root.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "Could not create test junction: " + await process.StandardError.ReadToEndAsync());
        try
        {
            using var engine = Engine((_, _) => throw new Exception("Network used for reparse path."));
            var context = sandbox.Context(link);
            await Error(DownloadErrorCode.DestinationInvalid, () => engine.TransferAsync(context, NoProgress, default));
            Check(Directory.GetFiles(target).Length == 0);
        }
        finally { Directory.Delete(link); }
    }

    private static async Task DirectoryLease()
    {
        using var sandbox = new Sandbox();
        using var engine = Engine((_, _) =>
        {
            try { Directory.Move(sandbox.Root, sandbox.Root + "-moved"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Task.FromResult(Response()); }
            throw new Exception("Active directory lease allowed rename.");
        });
        await engine.TransferAsync(sandbox.Context(), NoProgress, default);
        Directory.Move(sandbox.Root, sandbox.Root + "-moved");
        Directory.Move(sandbox.Root + "-moved", sandbox.Root);
    }

    private static async Task Resume()
    {
        using var sandbox = new Sandbox();
        using var engine = Engine((_, _) => throw new Exception("Resume contacted network."));
        await Error(DownloadErrorCode.RangeNotSupported, () => engine.TransferAsync(sandbox.Context() with { ResumeOffset = 12 }, NoProgress, default));
        Check(Directory.GetFiles(sandbox.Root).Length == 0);
    }

    private static async Task Partial()
    {
        foreach (var status in new[] { HttpStatusCode.PartialContent, HttpStatusCode.RequestedRangeNotSatisfiable })
        {
            using var sandbox = new Sandbox();
            using var engine = Engine((_, _) => Task.FromResult(Response(status: status)));
            var context = sandbox.Context();
            await Error(DownloadErrorCode.RangeNotSupported, () => engine.TransferAsync(context, NoProgress, default));
            Check(new FileInfo(context.Job.TemporaryPath).Length == 0);
        }
    }

    private static async Task Changed()
    {
        foreach (var probe in new[]
        {
            new DownloadProbe(99, null, null, DownloadResumeCapability.Unknown, null),
            new DownloadProbe(null, "\"previous\"", null, DownloadResumeCapability.Unknown, null),
            new DownloadProbe(null, null, DateTimeOffset.UnixEpoch, DownloadResumeCapability.Unknown, null)
        })
        {
            using var sandbox = new Sandbox();
            using var engine = Engine((_, _) => Task.FromResult(Response()));
            var context = sandbox.Context() with { Probe = probe };
            await Error(DownloadErrorCode.RemoteContentChanged, () => engine.TransferAsync(context, NoProgress, default));
            Check(new FileInfo(context.Job.TemporaryPath).Length == 0);
        }
    }

    private static async Task LengthMismatch()
    {
        foreach (var length in new long[] { 3, 100 })
        {
            using var sandbox = new Sandbox();
            using var engine = Engine((_, _) =>
            {
                var response = Response();
                response.Content.Headers.ContentLength = length;
                return Task.FromResult(response);
            });
            var context = sandbox.Context();
            await Error(DownloadErrorCode.ContentLengthMismatch, () => engine.TransferAsync(context, NoProgress, default));
            Check(!File.Exists(context.Job.DestinationPath));
        }
    }

    private static async Task Cancel()
    {
        using var sandbox = new Sandbox();
        using var cancel = new CancellationTokenSource();
        using var engine = Engine((_, _) => Task.FromResult(Response()));
        var context = sandbox.Context();
        try
        {
            await engine.TransferAsync(context, new InlineProgress(p => { if (p.TransferredBytes > 0) cancel.Cancel(); }), cancel.Token);
            throw new Exception("Cancellation ignored.");
        }
        catch (OperationCanceledException ex) { Check(ex.CancellationToken == cancel.Token); }
        Check(File.ReadAllText(context.Job.TemporaryPath) == "payload" && !File.Exists(context.Job.DestinationPath));
        using var opened = File.Open(context.Job.TemporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task StatusErrors()
    {
        foreach (var pair in new[]
        {
            (401, DownloadErrorCode.HttpUnauthorized), (403, DownloadErrorCode.HttpForbidden),
            (404, DownloadErrorCode.HttpNotFound), (429, DownloadErrorCode.HttpRateLimited),
            (503, DownloadErrorCode.HttpServerError), (204, DownloadErrorCode.UnexpectedFailure)
        })
        {
            using var engine = Engine((_, _) => Task.FromResult(Response(status: (HttpStatusCode)pair.Item1)));
            await Error(pair.Item2, () => engine.ProbeAsync(new(Source), default));
        }
    }

    private static async Task IdleTimeout()
    {
        using var sandbox = new Sandbox();
        using var engine = Engine((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StallOnRead()) }), TimeSpan.FromMilliseconds(40));
        var context = sandbox.Context();
        await Error(DownloadErrorCode.Timeout, () => engine.TransferAsync(context, NoProgress, default));
        using var file = File.Open(context.Job.TemporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(file.Length == 0);
    }

    private static async Task TransportErrors()
    {
        foreach (var pair in new[]
        {
            (HttpRequestError.NameResolutionError, DownloadErrorCode.DnsFailure),
            (HttpRequestError.SecureConnectionError, DownloadErrorCode.TlsFailure),
            (HttpRequestError.ConnectionError, DownloadErrorCode.NetworkUnavailable)
        })
        {
            using var engine = Engine((_, _) => throw new HttpRequestException(pair.Item1, "secret-test-token private-path"));
            await Error(pair.Item2, () => engine.ProbeAsync(new(Source), default));
        }
        using var timeout = Engine(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response(); }, TimeSpan.FromMilliseconds(40));
        await Error(DownloadErrorCode.Timeout, () => timeout.ProbeAsync(new(Source), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await timeout.ProbeAsync(new(Source), cancelled.Token); throw new Exception("Pre-cancel ignored."); }
        catch (OperationCanceledException) { }
    }

    private static async Task InvalidSources()
    {
        using var engine = Engine((_, _) => throw new Exception("Invalid source sent."));
        foreach (var uri in new[] { "file:///C:/private-path", "ftp://example.org/file", "data:text/plain,x" })
            await Error(DownloadErrorCode.UnsupportedProtocol, () => engine.ProbeAsync(new(new Uri(uri)), default));
        foreach (var uri in new[] { new Uri("relative", UriKind.Relative), new Uri("https://user:secret-test-token@example.org/"),
            new Uri("https://example.org/#fragment") })
            await Error(DownloadErrorCode.InvalidUrl, () => engine.ProbeAsync(new(uri), default));
    }

    private static async Task Redirect()
    {
        var requests = 0;
        using var engine = Engine((request, _) =>
        {
            Check(request.Headers.Authorization is null && request.Headers.Referrer is null && !request.Headers.Contains("Cookie"));
            requests++;
            var response = Response();
            if (requests == 1) { response.StatusCode = HttpStatusCode.Redirect; response.Headers.Location = new("https://other.example.org/new"); }
            else Check(request.RequestUri!.Host == "other.example.org" && request.RequestUri.Query == "");
            return Task.FromResult(response);
        });
        await engine.ProbeAsync(new(Source), default);
        Check(requests == 2);
        requests = 0;
        using var loop = Engine((_, _) =>
        {
            requests++;
            var response = Response(status: HttpStatusCode.Redirect);
            response.Headers.Location = new("/again", UriKind.Relative);
            return Task.FromResult(response);
        });
        await Error(DownloadErrorCode.InvalidUrl, () => loop.ProbeAsync(new(Source), default));
        Check(requests == 6);
    }

    private static async Task UnsafeRedirects()
    {
        foreach (var pair in new[]
        {
            ("http://other.example.org/", DownloadErrorCode.TlsFailure),
            ("file:///C:/private-path", DownloadErrorCode.UnsupportedProtocol),
            ("https://user:secret-test-token@example.org/", DownloadErrorCode.InvalidUrl)
        })
        {
            using var engine = Engine((_, _) =>
            {
                var response = Response(status: HttpStatusCode.Redirect);
                response.Headers.Location = new(pair.Item1);
                return Task.FromResult(response);
            });
            await Error(pair.Item2, () => engine.ProbeAsync(new(Source), default));
        }
    }

    private static async Task EncodingRejected()
    {
        using var engine = Engine((_, _) =>
        {
            var response = Response(); response.Content.Headers.ContentEncoding.Add("gzip"); return Task.FromResult(response);
        });
        await Error(DownloadErrorCode.UnexpectedFailure, () => engine.ProbeAsync(new(Source), default));
    }

    private static async Task Disposed()
    {
        var engine = Engine((_, _) => Task.FromResult(Response())); engine.Dispose();
        await Error(DownloadErrorCode.EngineUnavailable, () => engine.ProbeAsync(new(Source), default));
    }

    private static async Task Loopback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/file");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                string? line;
                do { line = await reader.ReadLineAsync(timeout.Token); } while (!string.IsNullOrEmpty(line));
                var body = i == 0 ? "" : "payload";
                var length = i == 2 ? 100 : 7;
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n{body}"), timeout.Token);
            }
        }, timeout.Token);
        using var sandbox = new Sandbox();
        using var engine = new HttpDownloadEngine();
        var probe = await engine.ProbeAsync(new(address), timeout.Token);
        var context = sandbox.Context() with { Source = new(address), Probe = probe };
        var result = await engine.TransferAsync(context, NoProgress, timeout.Token);
        Check(result.TotalBytesWritten == 7);
        var broken = sandbox.Context() with { Source = new(address) };
        await Error(DownloadErrorCode.ContentLengthMismatch, () => engine.TransferAsync(broken, NoProgress, timeout.Token));
        await server;
    }

    private static readonly IProgress<DownloadProgress> NoProgress = new InlineProgress(_ => { });
    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class ThrowOnRead : MemoryStream
    {
        public override bool CanSeek => false;
        public override int Read(byte[] buffer, int offset, int count) => throw new Exception("Probe read body.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new Exception("Probe read body.");
    }
    private sealed class StallOnRead : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "dk-download-tests-" + Guid.NewGuid().ToString("N"));
        public Sandbox() => Directory.CreateDirectory(Root);
        public DownloadExecutionContext Context(string? directory = null)
        {
            var id = Guid.NewGuid();
            var job = new DownloadJobRecord
            {
                Id = id, SourceKey = "opaque", DisplayHost = "example.org", State = DownloadJobState.Downloading,
                DestinationPath = Path.Combine(directory ?? Root, "file.bin"),
                TemporaryPath = Path.Combine(directory ?? Root, $".{id:N}.dkdownload.part"),
                CreatedUtc = DateTimeOffset.UnixEpoch, UpdatedUtc = DateTimeOffset.UnixEpoch
            };
            return new(job, new(Source), new(null, null, null, DownloadResumeCapability.Unknown, null), 0);
        }
        public void Dispose()
        {
            // Root is a directly generated absolute child of the temp directory.
            if (Path.GetDirectoryName(Root) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Unexpected test cleanup root.");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
