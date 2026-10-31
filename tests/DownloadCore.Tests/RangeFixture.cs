using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>Generated data only; real loopback HTTP fixture, never an external service.</summary>
internal sealed class RangeFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _clients = new();
    private readonly Task _accept;
    private readonly bool _breakFirstBody;
    private int _broken;
    internal byte[] Data { get; }
    internal Uri Url { get; }
    internal ConcurrentQueue<long?> Requests { get; } = new();
    internal RangeFixture(int bytes = 8 * 1024 * 1024, bool breakFirstBody = false)
    {
        _breakFirstBody = breakFirstBody;
        Data = Enumerable.Range(0, bytes).Select(i => (byte)(i % 251)).ToArray();
        _listener.Start();
        Url = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/fixture?token=generated-fixture");
        _accept = Accept();
    }
    private async Task Accept()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_clients) _clients.Add(Handle(client));
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task Handle(TcpClient client)
    {
        using (client)
        try
        {
            using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
            var first = await reader.ReadLineAsync(_stop.Token) ?? "";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
            { var colon = line.IndexOf(':'); if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim(); }
            var head = first.StartsWith("HEAD ");
            long start = 0, end = Data.Length - 1;
            var partial = headers.TryGetValue("Range", out var range) && headers.GetValueOrDefault("If-Range") == "\"v1\"";
            if (partial)
            {
                var parts = range![6..].Split('-'); start = long.Parse(parts[0]);
                if (parts.Length > 1 && parts[1].Length > 0) end = Math.Min(end, long.Parse(parts[1]));
            }
            Requests.Enqueue(partial ? start : null);
            var status = partial ? start >= Data.Length ? "416 Range Not Satisfiable" : "206 Partial Content" : "200 OK";
            var contentRange = partial ? start >= Data.Length ? $"Content-Range: bytes */{Data.Length}\r\n" : $"Content-Range: bytes {start}-{end}/{Data.Length}\r\n" : "";
            var length = start >= Data.Length ? 0 : end - start + 1;
            var response = $"HTTP/1.1 {status}\r\nETag: \"v1\"\r\nAccept-Ranges: bytes\r\nContent-Length: {length}\r\n{contentRange}Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            if (head) return;
            if (_breakFirstBody && !partial && Interlocked.Exchange(ref _broken, 1) == 0) end = Math.Min(end, 2 * 1024 * 1024 - 1);
            while (start <= end && start < Data.Length)
            {
                var count = (int)Math.Min(32 * 1024, end - start + 1);
                await stream.WriteAsync(Data.AsMemory((int)start, count), _stop.Token); start += count;
                await Task.Delay(10, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _accept;
        Task[] tasks; lock (_clients) tasks = _clients.ToArray();
        await Task.WhenAll(tasks); _stop.Dispose();
    }
}
