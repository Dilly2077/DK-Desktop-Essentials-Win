using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using DKDesktopEssentials;
using DKDesktopEssentials.Modules.Downloads;

// Local, generated-data acceptance fixture. Uses the production window, UI,
// dispatcher, coordinator, HTTP engine, DPAPI and SQLite. Never points at the
// user's production store and never exposes fixture controls through the bridge.
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        var root = Path.GetFullPath(args[0]);
        var artifacts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
        if (!string.Equals(Path.GetDirectoryName(root), artifacts, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("downloads-32-6b-acceptance-", StringComparison.Ordinal)) return 2;
        if (Directory.Exists(root) && !File.Exists(Path.Combine(root,"fixture-owned.txt"))) return 2;
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,"fixture-owned.txt"), "Module 32.6B generated-data fixture; retain for inspection.");
        Directory.CreateDirectory(Path.Combine(root,"output"));
        try
        {
            using var fixture = new Fixture(root);
            var coordinator = new DownloadCoordinator(new HttpDownloadEngine(), new DownloadJobStore(Path.Combine(root,"queue")),
                availableSpace: path => File.Exists(Path.Combine(root,"disk-full.flag")) ? 0 : new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow(coordinator,Path.Combine(root,"webview"))
                { Title = "DK Desktop Essentials — isolated acceptance", Width = 1180, Height = 820 };
            var busy = false; string? last = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (_,_) =>
            {
                if (busy) return;
                busy = true;
                try
                {
                    var commandPath = Path.Combine(root,"control.txt");
                    var command = File.Exists(commandPath) ? File.ReadAllText(commandPath).Trim() : "";
                    if (command != last)
                    {
                        last = command;
                        switch (command)
                        {
                            case "seed":
                                foreach (var (name, mode) in new[] { ("alpha-slow.bin","slow"),("beta-slow.bin","slow"),("unknown-size.bin","unknown"),("unsafe-partial.bin","unsafe"),("changed-entity.bin","changed"),("retry-after-error.bin","retry"),("completed-fast.bin","fast") })
                                    await coordinator.EnqueueAsync(new(new Uri(fixture.BaseUrl+mode+"?token=fixture-secret"),Path.Combine(root,"output"),name,StartNow:false));
                                break;
                            case "large":
                                for(var n=0;n<250;n++)
                                    await coordinator.EnqueueAsync(new(new Uri(fixture.BaseUrl+"fast"),Path.Combine(root,"output"), n==0 ? new string('x',236)+".bin" : $"large-queue-{n:D3}.bin",StartNow:false));
                                break;
                            case "disk":
                                File.WriteAllText(Path.Combine(root,"disk-full.flag"),"controlled zero-space preflight");
                                await coordinator.EnqueueAsync(new(new Uri(fixture.BaseUrl+"fast"),Path.Combine(root,"output"),"disk-preflight.bin"));
                                break;
                            case "space": File.Delete(Path.Combine(root,"disk-full.flag")); break;
                            case "changed": File.WriteAllText(Path.Combine(root,"changed.flag"),"v2"); break;
                            case "retry-ready": File.WriteAllText(Path.Combine(root,"retry-ready.flag"),"ready"); break;
                        }
                    }
                    var view = await coordinator.ListAsync();
                    File.WriteAllText(Path.Combine(root,"snapshot.json"),JsonSerializer.Serialize(new { jobs=view, recoveryBlocked=coordinator.RecoveryBlocked, dpi=System.Windows.Media.VisualTreeHelper.GetDpi(window).PixelsPerDip },Json));
                }
                catch (Exception e) { File.AppendAllText(Path.Combine(root,"fixture-errors.txt"),e.GetType().Name+Environment.NewLine); }
                finally { busy = false; }
            };
            window.Closed += (_,_) => timer.Stop(); timer.Start();
            File.WriteAllText(Path.Combine(root,"endpoint.txt"),fixture.BaseUrl);
            app.Run(window); return 0;
        }
        catch (Exception e) { File.AppendAllText(Path.Combine(root,"fixture-errors.txt"),e.GetType().Name+Environment.NewLine); return 1; }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly string _root; private readonly object _log = new();
        internal string BaseUrl { get; }
        internal Fixture(string root)
        {
            _root = root;
            var portFile = Path.Combine(root,"port.txt"); int port;
            if (File.Exists(portFile)) port=int.Parse(File.ReadAllText(portFile));
            else { var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();File.WriteAllText(portFile,port.ToString()); }
            BaseUrl=$"http://127.0.0.1:{port}/";_listener.Prefixes.Add(BaseUrl);_listener.Start();
            _ = Task.Run(async () => { while(!_stop.IsCancellationRequested) { try {var context=await _listener.GetContextAsync();_ = Respond(context);}catch(HttpListenerException) when(_stop.IsCancellationRequested){break;}catch(ObjectDisposedException){break;} } });
        }
        private async Task Respond(HttpListenerContext context)
        {
            var request=context.Request;var response=context.Response;
            var mode=request.Url!.AbsolutePath.Trim('/');
            try
            {
                if (!new[]{"slow","unknown","unsafe","changed","retry","fast"}.Contains(mode)) { response.StatusCode=404; return; }
                if(mode=="retry"&&!File.Exists(Path.Combine(_root,"retry-ready.flag"))) {response.StatusCode=404;return;}
                var size=mode is "fast" or "retry" ? 1024*1024 : 64*1024*1024;
                var etag=mode=="changed"&&File.Exists(Path.Combine(_root,"changed.flag")) ? "\"fixture-v2\"" : "\"fixture-v1\"";
                var ranged=mode is not ("unsafe" or "unknown")&&request.Headers["Range"] is not null&&request.Headers["If-Range"]==etag;
                long offset=0,end=size-1;
                if(ranged) { var parts=request.Headers["Range"]![6..].Split('-'); offset=long.Parse(parts[0]);if(parts[1].Length>0)end=Math.Min(long.Parse(parts[1]),end); }
                response.StatusCode=offset>=size?416:ranged?206:200;
                if(mode is not ("unknown" or "unsafe")) { response.Headers["ETag"]=etag; response.Headers["Accept-Ranges"]="bytes"; }
                if(mode=="unknown")response.SendChunked=true;else response.ContentLength64=Math.Max(0,end-offset+1);
                if(ranged)response.Headers["Content-Range"]=offset>=size?$"bytes */{size}":$"bytes {offset}-{end}/{size}";
                lock(_log)File.AppendAllText(Path.Combine(_root,"requests.jsonl"),JsonSerializer.Serialize(new { method=request.HttpMethod, mode, offset, ranged, etag })+Environment.NewLine);
                if(request.HttpMethod=="HEAD"||offset>=size)return;
                var buffer=new byte[32768];
                while(offset<=end&&!_stop.IsCancellationRequested)
                {
                    var count=(int)Math.Min(buffer.Length,end-offset+1);for(var n=0;n<count;n++)buffer[n]=(byte)((offset+n)%251);
                    await response.OutputStream.WriteAsync(buffer.AsMemory(0,count),_stop.Token);await response.OutputStream.FlushAsync(_stop.Token);offset+=count;
                    if(mode is not ("fast" or "retry"))await Task.Delay(70,_stop.Token);
                }
            }
            catch(Exception e) when(e is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
            finally {response.Close();}
        }
        public void Dispose(){_stop.Cancel();_listener.Close();_stop.Dispose();}
    }
}
