using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using DKDesktopEssentials.Modules.Downloads;

internal static class NativeIntegrationCases
{
    private static readonly Uri Source = new("https://example.org/private/signed-path?token=integration-secret");
    internal static IEnumerable<(string Name, Func<Task> Run)> All => new (string, Func<Task>)[]
    {
        ("Native held queue persists and starts only the selected job", Held),
        ("Native removal forgets metadata and retains completed and partial files", Remove),
        ("Native active removal is rejected until cancellation acknowledges", ActiveRemoval),
        ("Native removal while start awaits worker cannot resurrect a job", RemoveDuringRequeue),
        ("Native unsafe partial disables resume and rejects forged resume", Unsafe),
        ("Native folder reveal requires verified existing file and rejects replacement links", Reveal),
        ("Native handshake subscription and folder grants expose no source or path", Bridge),
        ("Native cancelled picker and forged folder grants cannot create work", Picker),
        ("Native large persistent queue never dispatches held jobs", LargeQueue),
        ("Native newest record stays first after a removed history slot is reused", NewestFirst),
        ("Native long filename completes without truncating or overwriting", LongName)
    };
    private static void Check(bool value, string message = "Native integration assertion failed") { if (!value) throw new Exception(message); }
    private static bool Cap(DownloadJobView job, string verb) => job.Capabilities![verb];
    private static async Task<DownloadJobView> State(DownloadCoordinator c, Guid id, DownloadJobState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var job = (await c.GetAsync(id))!;
            if (job.State == expected) return job;
            if (job.State == DownloadJobState.Failed && expected != DownloadJobState.Failed) throw new Exception($"Unexpected failure {job.ErrorCode}");
            await Task.Delay(5, timeout.Token);
        }
    }
    private static async Task Reject(DownloadErrorCode code, Func<Task> action)
    { try { await action(); } catch (DownloadEngineException e) { Check(e.Code == code, $"Expected {code}, got {e.Code}"); return; } throw new Exception("Expected native rejection"); }
    private static async Task Held()
    {
        using var f = new Folder(); Guid first, second;
        await using (var c = f.Coordinator(new Engine()))
        {
            first = (await c.EnqueueAsync(f.Request("first"))).Id; second = (await c.EnqueueAsync(f.Request("second"))).Id;
            await c.SetSettingsAsync(new(AutoStartOnLaunch:true)); await Task.Delay(30);
            Check(Cap((await c.GetAsync(first))!, "start"));
        }
        var engine = new Engine(); await using var restored = f.Coordinator(engine);
        await Task.Delay(30); Check(engine.Probes == 0 && (await restored.ListAsync()).Count == 2);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await restored.StartAsync(first, cancelled.Token); throw new Exception("Ignored cancellation"); } catch (OperationCanceledException) { }
        Check(engine.Probes == 0);
        await restored.StartAsync(first); await State(restored, first, DownloadJobState.Completed);
        Check(engine.Probes == 1 && Cap((await restored.GetAsync(second))!, "start"));
    }
    private static async Task Remove()
    {
        using var f = new Folder(); Guid completed, cancelled;
        await using (var c = f.Coordinator(new Engine()))
        {
            completed = (await c.EnqueueAsync(f.Request("complete") with { StartNow = true })).Id;
            await State(c, completed, DownloadJobState.Completed);
            cancelled = (await c.EnqueueAsync(f.Request("partial"))).Id;
            await c.CancelAsync(cancelled);
            await File.WriteAllTextAsync(Path.Combine(f.Output, $".{cancelled:N}.dkdownload.part"), "retain");
            await c.RemoveAsync(completed); await c.RemoveAsync(cancelled);
            Check((await c.ListAsync()).Count == 0 && File.ReadAllText(Path.Combine(f.Output,"complete")) == "payload");
            Check(File.ReadAllText(Path.Combine(f.Output,$".{cancelled:N}.dkdownload.part")) == "retain");
        }
        await using var restored = f.Coordinator(new Engine()); Check((await restored.ListAsync()).Count == 0);
    }
    private static async Task ActiveRemoval()
    {
        using var f = new Folder(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine { Write = async (_, _, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(0, ""); } };
        await using var c = f.Coordinator(engine); var job = await c.EnqueueAsync(f.Request("busy") with { StartNow = true });
        await started.Task; Check(!Cap((await c.GetAsync(job.Id))!, "remove"));
        await Reject(DownloadErrorCode.EngineUnavailable, () => c.RemoveAsync(job.Id));
        await c.CancelAsync(job.Id); await c.RemoveAsync(job.Id); Check(await c.GetAsync(job.Id) is null);
    }
    private static async Task RemoveDuringRequeue()
    {
        using var f = new Folder(); var engine = new Engine(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid id;
        await using (var c = f.Coordinator(engine))
        {
            id = (await c.EnqueueAsync(f.Request("race"))).Id;
            // Hold the previous worker deterministically so StartAsync suspends between its two locks.
            var jobs = (System.Collections.IDictionary)typeof(DownloadCoordinator).GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(c)!;
            var entry = jobs[id]!;
            entry.GetType().GetField("Work")!.SetValue(entry, release.Task);
            var start = c.StartAsync(id);
            Check(!start.IsCompleted, "Start did not wait for the previous worker");
            await c.RemoveAsync(id); release.SetResult();
            await Reject(DownloadErrorCode.InvalidUrl, () => start);
            Check(await c.GetAsync(id) is null && engine.Probes == 0, "Removed job was restarted");
        }
        await using var restored = f.Coordinator(engine);
        Check(await restored.GetAsync(id) is null && engine.Probes == 0, "Removed job reappeared after restart");
    }
    private static async Task Unsafe()
    {
        using var f = new Folder(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine { Metadata = new(7,null,null,DownloadResumeCapability.Unsupported,null), Write = async (context, progress, token) =>
        {
            await File.WriteAllTextAsync(context.Job.TemporaryPath, "pay", token);
            progress.Report(new(context.Job.Id,3,7,3,Convert.ToHexString(SHA256.HashData("pay"u8))));
            started.SetResult(); await Task.Delay(Timeout.Infinite,token); return new(0, "");
        } };
        await using var c = f.Coordinator(engine); var job = await c.EnqueueAsync(f.Request("unsafe") with { StartNow = true });
        await started.Task; var paused = await c.PauseAsync(job.Id);
        Check(!Cap(paused,"resume") && !paused.CanResume);
        await Reject(DownloadErrorCode.RangeNotSupported, () => c.ResumeAsync(job.Id));
        Check(File.ReadAllText(Path.Combine(f.Output,$".{job.Id:N}.dkdownload.part")) == "pay");
    }
    private static async Task Reveal()
    {
        using var f = new Folder(); var revealed = new List<string>();
        await using var c = new DownloadCoordinator(new Engine(), new DownloadJobStore(f.Db), openFolder: path => { revealed.Add(path); return Task.CompletedTask; });
        var job = await c.EnqueueAsync(f.Request("reveal"));
        await Reject(DownloadErrorCode.DestinationInvalid, () => c.OpenFolderAsync(job.Id));
        await c.StartAsync(job.Id); await State(c,job.Id,DownloadJobState.Completed);
        await c.OpenFolderAsync(job.Id); Check(revealed.SequenceEqual(new[]{f.Output}));
        var path = Path.Combine(f.Output,"reveal");
        File.SetAttributes(path,FileAttributes.ReadOnly);
        try { await c.OpenFolderAsync(job.Id); Check(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)); }
        finally { File.SetAttributes(path,FileAttributes.Normal); }
        File.Move(path, path+"-saved");
        Check(!Cap((await c.GetAsync(job.Id))!,"openFolder"));
        await Reject(DownloadErrorCode.PartialFileInvalid, () => c.OpenFolderAsync(job.Id));
        Check(CreateHardLinkW(path, path+"-saved", 0), "Could not create test-owned hard link");
        await Reject(DownloadErrorCode.PartialFileInvalid, () => c.OpenFolderAsync(job.Id)); Check(revealed.Count == 2);
    }
    private const string Origin = "https://dk.local/index.html";
    private static string Command(string name, object? payload = null, string id="native-test") => JsonSerializer.Serialize(new { type="downloads:"+name, requestId=id, payload });
    private static async Task<JsonElement> Send(DownloadWebBridge bridge, string name, object? payload=null)
    { using var json = JsonDocument.Parse((await bridge.HandleAsync(Command(name,payload),Origin))!); return json.RootElement.Clone(); }
    private static async Task Bridge()
    {
        using var f = new Folder(); await using var c = f.Coordinator(new Engine());
        var bridge = new DownloadWebBridge(c,()=>Task.FromResult<string?>(f.Output));
        var caps = (await Send(bridge,"capabilities")).GetProperty("result");
        Check(caps.GetProperty("protocolVersion").GetInt32()==1 && caps.GetProperty("protectedDestinations").GetBoolean());
        await Send(bridge,"subscribe",new { enabled=true }); Check(bridge.UpdatesRequested);
        await Send(bridge,"subscribe",new { enabled=false }); Check(!bridge.UpdatesRequested);
        Check(await bridge.HandleAsync(Command("capabilities",id:"https://signed/token"),Origin) is null);
        Check(await bridge.HandleAsync(Command("https://private?token=secret"),Origin) is null);
        Check(await bridge.HandleAsync(Command("chooseFolder"),"https://dk.local.evil/index.html") is null);
        var picked = await Send(bridge,"chooseFolder");
        Check(!picked.ToString().Contains(f.Output) && !picked.ToString().Contains("destinationDirectory"));
        var result = await Send(bridge,"enqueue",new { url=Source.AbsoluteUri,fileName="bridge",destinationToken=picked.GetProperty("result").GetProperty("destinationToken").GetString(),startNow=false });
        Check(result.GetProperty("ok").GetBoolean()); var id = result.GetProperty("result").GetProperty("id").GetGuid();
        Check((await c.GetAsync(id))!.State == DownloadJobState.Queued);
        await Send(bridge,"start",new {id}); await State(c,id,DownloadJobState.Completed);
        var list = (await Send(bridge,"list")).ToString(); Check(!list.Contains("integration-secret")&&!list.Contains("signed-path")&&!list.Contains(f.Output));
        Check((await Send(bridge,"remove",new{id})).GetProperty("ok").GetBoolean());
    }
    private static async Task Picker()
    {
        using var f = new Folder(); var engine = new Engine(); await using var c = f.Coordinator(engine);
        var bridge = new DownloadWebBridge(c,()=>Task.FromResult<string?>(null));
        Check((await Send(bridge,"chooseFolder")).GetProperty("result").ValueKind == JsonValueKind.Null);
        var rejected = await Send(bridge,"enqueue",new {url=Source.AbsoluteUri,fileName="bad",destinationToken=new string('a',32),destinationDirectory=f.Output});
        Check(!rejected.GetProperty("ok").GetBoolean() && engine.Probes==0 && (await c.ListAsync()).Count==0);
    }
    private static async Task LargeQueue()
    {
        using var f = new Folder(); var engine = new Engine();
        await using (var c = f.Coordinator(engine))
        { for(var n=0;n<250;n++) await c.EnqueueAsync(f.Request($"held-{n}.bin")); Check((await c.ListAsync()).Count==250 && engine.Probes==0); }
        await using var restored = f.Coordinator(engine); Check((await restored.ListAsync()).Count==250 && engine.Probes==0);
    }
    private static async Task LongName()
    {
        using var f = new Folder(); await using var c = f.Coordinator(new Engine());
        var name = new string('x',236)+".bin";
        var job = await c.EnqueueAsync(f.Request(name) with {StartNow=true}); await State(c,job.Id,DownloadJobState.Completed);
        Check(File.ReadAllText(Path.Combine(f.Output,name))=="payload");
    }
    private static async Task NewestFirst()
    {
        using var f = new Folder(); await using var c = f.Coordinator(new Engine());
        var removed = await c.EnqueueAsync(f.Request("removed"));
        await c.EnqueueAsync(f.Request("older")); await c.RemoveAsync(removed.Id);
        await Task.Delay(5);
        var newest = await c.EnqueueAsync(f.Request("newest"));
        Check((await c.ListAsync())[0].Id == newest.Id, "Reused dictionary slot hid the newest download");
    }
    private sealed class Engine : IDownloadEngine
    {
        internal int Probes;
        internal DownloadProbe Metadata = new(7,"\"integration-v1\"",null,DownloadResumeCapability.Unknown,null);
        internal Func<DownloadExecutionContext,IProgress<DownloadProgress>,CancellationToken,Task<DownloadExecutionResult>> Write = async (context,_,token) =>
        { await File.WriteAllTextAsync(context.Job.TemporaryPath,"payload",token); return new(7,Convert.ToHexString(SHA256.HashData("payload"u8)).ToLowerInvariant()); };
        public Task<DownloadProbe> ProbeAsync(DownloadSource source,CancellationToken token) { Interlocked.Increment(ref Probes); return Task.FromResult(Metadata); }
        public Task<DownloadExecutionResult> TransferAsync(DownloadExecutionContext context,IProgress<DownloadProgress> progress,CancellationToken token) => Write(context,progress,token);
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, nint security);
    private sealed class Folder : IDisposable
    {
        internal string Root = Path.Combine(Path.GetTempPath(),"dk-native-tests-"+Guid.NewGuid().ToString("N"));
        internal string Output => Path.Combine(Root,"output"); internal string Db=>Path.Combine(Root,"store");
        internal Folder()=>Directory.CreateDirectory(Output);
        internal CreateDownloadRequest Request(string name)=>new(Source,Output,name,StartNow:false);
        internal DownloadCoordinator Coordinator(Engine engine)=>new(engine,new DownloadJobStore(Db));
        public void Dispose() { if(Path.GetDirectoryName(Root)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))throw new Exception("Invalid cleanup root"); Directory.Delete(Root,true); }
    }
}
