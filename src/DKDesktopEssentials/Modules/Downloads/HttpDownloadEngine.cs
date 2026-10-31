using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DKDesktopEssentials.Modules.Downloads;

/// <summary>
/// Isolated HTTP(S) adapter. Own one instance per coordinator, dispose after stopping
/// active work. No jobs, database writes, shell commands, retries or final renames.
/// Conditional ranges append only validated content; optional bounded parallel reads
/// assemble in order so the durable partial is always a contiguous prefix.
/// </summary>
public sealed class HttpDownloadEngine : IDownloadEngine, IDisposable
{
    private readonly HttpClient _client;
    private readonly TimeSpan _operationTimeout;

    public HttpDownloadEngine() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        MaxResponseHeadersLength = 64,
        MaxResponseDrainSize = 0,
        MaxConnectionsPerServer = 4
    }, TimeSpan.FromSeconds(30)) { }

    // Injection is available only to the local test assembly. Production policy cannot
    // be replaced with an auto-redirecting/cookie-sharing handler by the UI.
    internal HttpDownloadEngine(HttpMessageHandler handler, TimeSpan operationTimeout)
    {
        if (operationTimeout <= TimeSpan.Zero || operationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _operationTimeout = operationTimeout;
    }

    public async Task<DownloadProbe> ProbeAsync(DownloadSource source, CancellationToken cancellationToken)
    {
        try
        {
            var uri = ValidateSource(source);
            cancellationToken.ThrowIfCancellationRequested();
            using var head = await SendAsync(uri, HttpMethod.Head, cancellationToken);
            if (head.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                // Inspect headers only. Disposing the response closes the unconsumed body.
                using var fallback = await SendAsync(uri, HttpMethod.Get, cancellationToken);
                RequireFullResponse(fallback);
                return Describe(fallback);
            }
            RequireFullResponse(head);
            return Describe(head);
        }
        catch (Exception ex) { throw Translate(ex, cancellationToken); }
    }

    public async Task<DownloadExecutionResult> TransferAsync(DownloadExecutionContext context,
        IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        try { return await TransferCoreAsync(context, progress, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { throw Translate(ex, cancellationToken); }
    }

    private const int ChunkBytes = 1024 * 1024;

    private async Task<DownloadExecutionResult> TransferCoreAsync(DownloadExecutionContext context,
        IProgress<DownloadProgress> progress, CancellationToken token)
    {
        var uri = ValidateSource(context.Source);
        token.ThrowIfCancellationRequested();
        if (context.Job.ParallelSegments is not (1 or 2 or 4)) throw new DownloadEngineException(DownloadErrorCode.UnexpectedFailure);
        if (context.ResumeOffset < 0) throw new DownloadEngineException(DownloadErrorCode.PartialFileInvalid);
        if (context.ResumeOffset > 0) DownloadRemoteIdentity.CheckResume(context.Job, context.Probe);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var staging = context.ResumeExistingFile || context.ResumeOffset > 0
            ? DownloadStagingFile.OpenResume(context.Job, context.ResumeOffset, hash, token)
            : DownloadStagingFile.Create(context.Job);
        var watch = Stopwatch.StartNew();
        var expected = context.Job.ExpectedBytes ?? context.Probe.ContentLength;
        var total = context.ResumeOffset;
        var sealedBytes = total;
        var writeReady = false;
        var rangeValidated = false;
        long Speed() => (long)((total - context.ResumeOffset) / Math.Max(watch.Elapsed.TotalSeconds, 0.001));
        void Checkpoint()
        {
            staging.Stream.Flush(flushToDisk: true);
            progress.Report(new(context.Job.Id, total, expected, Speed(),
                Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant(), rangeValidated));
            sealedBytes = total;
        }
        void PrepareWrite()
        {
            if (writeReady) return;
            staging.Stream.SetLength(context.ResumeOffset); staging.Stream.Position = context.ResumeOffset;
            writeReady = true;
            progress.Report(new(context.Job.Id, total, expected, 0));
        }
        async Task Append(ReadOnlyMemory<byte> bytes)
        {
            token.ThrowIfCancellationRequested();
            if (expected is { } limit && bytes.Length > limit - total)
                throw new DownloadEngineException(DownloadErrorCode.ContentLengthMismatch);
            DownloadDiskSpace.Require(context.Job.TemporaryPath, bytes.Length);
            await staging.Stream.WriteAsync(bytes, token).ConfigureAwait(false);
            hash.AppendData(bytes.Span); total = checked(total + bytes.Length);
            progress.Report(new(context.Job.Id, total, expected, Speed(), RangeValidated: rangeValidated));
            if (total - sealedBytes >= ChunkBytes) Checkpoint();
        }
        async Task SingleStream()
        {
            using var response = await SendAsync(uri, HttpMethod.Get, token,
                context.ResumeOffset > 0 ? context.ResumeOffset : null, null, DownloadRemoteIdentity.IfRange(context.Probe));
            if (context.ResumeOffset > 0)
            {
                RequireRangeStatus(response);
                CheckResponseIdentity(response, context.Probe);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (expected == total && range is { HasRange: false } && range.Unit == "bytes" && range.Length == total)
                    { rangeValidated = true; PrepareWrite(); return; }
                    throw new DownloadEngineException(range?.Length != expected ? DownloadErrorCode.RemoteContentChanged : DownloadErrorCode.RangeNotSupported);
                }
                RequireRange(response, context.Probe, total, expected!.Value - 1);
                rangeValidated = true;
            }
            else
            {
                RequireFullResponse(response);
                CheckResponseIdentity(response, context.Probe);
                var length = response.Content.Headers.ContentLength;
                if (context.Probe.ContentLength is { } probed && length is { } actual && probed != actual)
                    throw new DownloadEngineException(DownloadErrorCode.RemoteContentChanged);
                if ((expected is { } known && length is { } size && known != size))
                    throw new DownloadEngineException(DownloadErrorCode.ContentLengthMismatch);
                expected ??= length;
            }
            PrepareWrite();
            using var input = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await ReadAsync(input, buffer, token)) != 0) await Append(buffer.AsMemory(0, count));
        }
        try
        {
            var parallel = context.Job.ParallelSegments > 1 && expected >= 2 * ChunkBytes &&
                DownloadRemoteIdentity.IfRange(context.Probe)?.EntityTag is not null;
            if (parallel && total < expected)
            {
                try
                {
                    while (total < expected)
                    {
                        using var batch = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var tasks = new List<Task<byte[]>>();
                        var start = total;
                        for (var i = 0; i < context.Job.ParallelSegments && start < expected; i++, start += ChunkBytes)
                        {
                            var from = start; var to = Math.Min(expected.Value - 1, from + ChunkBytes - 1);
                            tasks.Add(ReadSegment(uri, context.Probe, from, to, batch));
                        }
                        byte[][] chunks;
                        try { chunks = await Task.WhenAll(tasks); }
                        catch
                        {
                            token.ThrowIfCancellationRequested();
                            var failure = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.Flatten().InnerExceptions)
                                .OfType<DownloadEngineException>().FirstOrDefault();
                            throw failure ?? new DownloadEngineException(DownloadErrorCode.NetworkUnavailable);
                        }
                        rangeValidated = true; PrepareWrite();
                        foreach (var chunk in chunks) await Append(chunk);
                    }
                }
                catch (DownloadEngineException ex) when (ex.Code == DownloadErrorCode.RangeNotSupported && total == 0)
                {
                    // The initial range batch wrote nothing. A fresh normal GET is safe;
                    // a resumed attempt never falls back to overwriting its partial.
                    await SingleStream();
                }
            }
            else await SingleStream();
            if (expected is { } size && total != size) throw new DownloadEngineException(DownloadErrorCode.ContentLengthMismatch);
            Checkpoint(); token.ThrowIfCancellationRequested();
            return new(total, Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (writeReady)
            {
                // A cancelled WriteAsync may have written an unreported suffix. Keep only
                // successfully hashed writes, then flush and seal before acknowledging pause.
                staging.Stream.SetLength(total); Checkpoint();
            }
            throw;
        }
    }

    private async Task<byte[]> ReadSegment(Uri uri, DownloadProbe probe, long from, long to, CancellationTokenSource batch)
    {
        try
        {
            using var response = await SendAsync(uri, HttpMethod.Get, batch.Token, from, to, DownloadRemoteIdentity.IfRange(probe));
            RequireRange(response, probe, from, to);
            using var input = await response.Content.ReadAsStreamAsync(batch.Token);
            var data = new byte[checked((int)(to - from + 1))];
            var offset = 0;
            while (offset < data.Length)
            {
                var count = await ReadAsync(input, data.AsMemory(offset), batch.Token);
                if (count == 0) throw new DownloadEngineException(DownloadErrorCode.ContentLengthMismatch);
                offset += count;
            }
            if (await ReadAsync(input, new byte[1], batch.Token) != 0) throw new DownloadEngineException(DownloadErrorCode.ContentLengthMismatch);
            return data;
        }
        catch (Exception ex) { var safe = Translate(ex, batch.Token); batch.Cancel(); throw safe; }
    }

    private async ValueTask<int> ReadAsync(Stream input, Memory<byte> buffer, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(_operationTimeout);
        try { return await input.ReadAsync(buffer, idle.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new DownloadEngineException(DownloadErrorCode.Timeout); }
    }

    private static void RequireRange(HttpResponseMessage response, DownloadProbe probe, long from, long to)
    {
        RequireRangeStatus(response);
        CheckResponseIdentity(response, probe);
        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new DownloadEngineException(DownloadErrorCode.RangeNotSupported);
        if (response.StatusCode != HttpStatusCode.PartialContent) RequireFullResponse(response);
        RequireIdentityEncoding(response);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range is null || range.Unit != "bytes" ||
            range.From != from || range.To != to || range.Length != probe.ContentLength ||
            (response.Content.Headers.ContentLength is { } count && count != to - from + 1) ||
            response.Content.Headers.ContentType?.MediaType == "multipart/byteranges")
            throw new DownloadEngineException(DownloadErrorCode.PartialFileInvalid);
    }

    private static void RequireRangeStatus(HttpResponseMessage response)
    {
        // Error documents need not carry the representation's validator. Preserve
        // actionable HTTP errors (for example, an expired signed URL) before
        // checking identity on a successful or range-specific response.
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable))
            RequireFullResponse(response);
    }

    private static void CheckResponseIdentity(HttpResponseMessage response, DownloadProbe probe)
    {
        if ((probe.ResourceKey is { } key && ResourceKey(response) != key) ||
            (probe.ETag is { } tag && response.Headers.ETag?.ToString() != tag) ||
            (probe.ETag is null && probe.LastModifiedUtc is { } modified && response.Content.Headers.LastModified != modified))
            throw new DownloadEngineException(DownloadErrorCode.RemoteContentChanged);
    }

    private static string? ResourceKey(HttpResponseMessage response) => response.RequestMessage?.RequestUri is { } uri
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))) : null;

    private async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, CancellationToken token,
        long? from = null, long? to = null, RangeConditionHeaderValue? validator = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_operationTimeout);
        for (var redirect = 0; ; redirect++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (from is { } start)
            { request.Headers.Range = new RangeHeaderValue(start, to); request.Headers.IfRange = validator; }
            // Do not add Referer, credentials, cookies or headers from a previous hop.
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            { response.RequestMessage ??= request; return response; }
            using (response)
            {
                if (redirect >= 5 || response.Headers.Location is not { } location ||
                    !Uri.TryCreate(uri, location, out var next))
                    throw new DownloadEngineException(DownloadErrorCode.InvalidUrl);
                next = ValidateSource(new(next));
                if (uri.Scheme == "https" && next.Scheme != "https")
                    throw new DownloadEngineException(DownloadErrorCode.TlsFailure);
                uri = next;
            }
        }
    }

    internal static Uri ValidateSource(DownloadSource source)
    {
        var uri = source?.AuthorizedUrl;
        if (uri is null || !uri.IsAbsoluteUri)
            throw new DownloadEngineException(DownloadErrorCode.InvalidUrl);
        if (uri.Scheme is not ("http" or "https"))
            throw new DownloadEngineException(DownloadErrorCode.UnsupportedProtocol);
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new DownloadEngineException(DownloadErrorCode.InvalidUrl);
        return uri;
    }

    private static void RequireFullResponse(HttpResponseMessage response)
    {
        var error = (int)response.StatusCode switch
        {
            200 => (DownloadErrorCode?)null,
            401 => DownloadErrorCode.HttpUnauthorized,
            403 => DownloadErrorCode.HttpForbidden,
            404 or 410 => DownloadErrorCode.HttpNotFound,
            429 => DownloadErrorCode.HttpRateLimited,
            206 or 416 => DownloadErrorCode.RangeNotSupported,
            >= 500 => DownloadErrorCode.HttpServerError,
            _ => DownloadErrorCode.UnexpectedFailure
        };
        if (error is { } code)
        {
            var retryAfter = response.Headers.RetryAfter;
            throw new DownloadEngineException(code, retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow));
        }
        RequireIdentityEncoding(response);
    }

    private static void RequireIdentityEncoding(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new DownloadEngineException(DownloadErrorCode.UnexpectedFailure);
    }

    private static DownloadProbe Describe(HttpResponseMessage response)
    {
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ??
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        var probe = new DownloadProbe(response.Content.Headers.ContentLength, response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified, DownloadResumeCapability.Unknown,
            DownloadStagingFile.IsSafeFileName(name) ? name : null, response.Headers.Date, ResourceKey(response));
        // Accept-Ranges is informative. Only an actual validated 206/416 proves support.
        return probe with { ResumeCapability = probe.ContentLength is not null && DownloadRemoteIdentity.IfRange(probe) is not null &&
            !response.Headers.AcceptRanges.Contains("none") ? DownloadResumeCapability.Unknown : DownloadResumeCapability.Unsupported };
    }

    private static Exception Translate(Exception exception, CancellationToken caller)
    {
        if (exception is DownloadEngineException) return exception;
        if (exception is OperationCanceledException)
            return caller.IsCancellationRequested ? new OperationCanceledException(caller) :
                new DownloadEngineException(DownloadErrorCode.Timeout);
        var code = exception switch
        {
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => DownloadErrorCode.DnsFailure,
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => DownloadErrorCode.TlsFailure,
            HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } => DownloadErrorCode.ContentLengthMismatch,
            HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => DownloadErrorCode.ContentLengthMismatch,
            HttpIOException => DownloadErrorCode.NetworkUnavailable,
            HttpRequestException => DownloadErrorCode.NetworkUnavailable,
            UnauthorizedAccessException => DownloadErrorCode.PermissionDenied,
            IOException io when (io.HResult & 0xffff) is 39 or 112 => DownloadErrorCode.DiskFull,
            IOException io when (io.HResult & 0xffff) is 80 or 183 => DownloadErrorCode.PartialFileInvalid,
            IOException => DownloadErrorCode.StorageFailure,
            ObjectDisposedException => DownloadErrorCode.EngineUnavailable,
            ArgumentException or NotSupportedException => DownloadErrorCode.DestinationInvalid,
            _ => DownloadErrorCode.UnexpectedFailure
        };
        return new DownloadEngineException(code);
    }

    public void Dispose() => _client.Dispose();
}
