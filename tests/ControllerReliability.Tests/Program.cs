using System.Collections.Concurrent;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;
using DKControllerOutputHost;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Shared profile mapping is isolated by device and safe under concurrent reset", MappingIsolation),
    ("Two sessions run independently; disconnect neutralizes and exact reconnect requires release", Reconnect),
    ("Backend failure recreates output and gates held buttons", Recovery),
    ("Persistent backend failure exhausts a bounded recovery budget", RecoveryLimit),
    ("Blocked output observes timeout and shutdown cancellation", BlockedOutput),
    ("Helper neutralizes on stop, EOF, malformed input and failed submit", ProtocolCleanup),
    ("Helper watchdog releases a stalled parent", Watchdog),
    ("Non-finite output is neutral and identities are per device/profile", Values)
};
foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}
Console.WriteLine($"Passed {tests.Length} reliability tests.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static ControllerProfile ToggleProfile()
{
    var profile = new ControllerProfile { PassThroughUnmappedControls = false };
    profile.Layers[0].Bindings.Add(new ControllerBinding
    {
        Mode = ControllerBindingMode.Toggle,
        Source = ControllerControlRef.ForStandard(StandardControl.South),
        Target = ControllerAction.ToController(StandardControl.East)
    });
    return profile;
}

static ControllerSnapshot Snapshot(string id, double value) => new(id, 1,
    new Dictionary<StandardControl, double> { [StandardControl.South] = value }, null, null, null);

static Task MappingIsolation()
{
    var mapper = new ControllerMappingEngine(); var profile = ToggleProfile(); var now = DateTimeOffset.UtcNow;
    Check(mapper.Map(Snapshot("a", 1), profile, now)[StandardControl.East] == 1, "A should toggle on");
    Check(mapper.Map(Snapshot("b", 0), profile, now)[StandardControl.East] == 0, "A toggle leaked to B");
    Check(mapper.Map(Snapshot("a", 0), profile, now)[StandardControl.East] == 1, "A lost its toggle");
    Parallel.For(0, 1000, i => { if (i % 3 == 0) mapper.ResetRuntimeState(); else mapper.Map(Snapshot(i % 2 == 0 ? "a" : "b", 0), profile, now); });
    return Task.CompletedTask;
}

static ControllerOutputSession Session(FakeProvider provider, string id, Func<CancellationToken, Task<IControllerOutputSink>> factory) =>
    new(provider.Descriptor(id), ToggleProfile(), 250, provider, () => null!, factory);

static async Task Until(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!predicate()) await Task.Delay(10, timeout.Token);
}

static async Task Reconnect()
{
    var provider = new FakeProvider(); provider.Values["a"] = 0; provider.Values["b"] = 0;
    var a = new FakeSink(); var b = new FakeSink();
    await using var sa = Session(provider, "a", _ => Task.FromResult<IControllerOutputSink>(a));
    await using var sb = Session(provider, "b", _ => Task.FromResult<IControllerOutputSink>(b));
    await sa.StartAsync(); await sb.StartAsync();
    await Until(() => sa.State == "active" && sb.State == "active");
    provider.Values["a"] = 1; provider.Values["b"] = 1;
    await Until(() => a.Last == 1 && b.Last == 1);
    provider.Values.TryRemove("a", out _);
    await Until(() => sa.State == "waitingForReconnect" && a.Last == 0);
    Check(b.Last == 1, "Disconnect changed another controller output");
    provider.Values["lookalike"] = 1;
    await Task.Delay(80);
    Check(sa.State == "waitingForReconnect", "Session guessed a replacement device");
    provider.Values["a"] = 1;
    await Until(() => sa.State == "waitingForRelease");
    Check(a.Last == 0, "Held reconnect button was replayed");
    provider.Values["a"] = 0;
    await Until(() => sa.State == "active");
    provider.Values["a"] = 1;
    await Until(() => a.Last == 1);
    await sa.DisposeAsync();
    Check(a.Disposed && a.Last == 0, "Stop did not neutralize and dispose");
    Check(b.Last == 1 && !b.Disposed, "Stopping A stopped B");
}

static async Task Recovery()
{
    var provider = new FakeProvider(); provider.Values["a"] = 0;
    var first = new FakeSink(); var next = new FakeSink(); var creates = 0;
    await using var session = Session(provider, "a", _ => Task.FromResult<IControllerOutputSink>(Interlocked.Increment(ref creates) == 1 ? first : next));
    await session.StartAsync(); await Until(() => session.State == "active");
    provider.Values["a"] = 1; await Until(() => first.Last == 1);
    first.Fail = true;
    await Until(() => creates == 2 && session.State == "waitingForRelease");
    Check(first.Disposed && next.Last == 0, "Recovery did not retire failed sink and neutralize replacement");
    provider.Values["a"] = 0; await Until(() => session.State == "active");
    provider.Values["a"] = 1; await Until(() => next.Last == 1);
}

static async Task RecoveryLimit()
{
    var provider = new FakeProvider(); provider.Values["a"] = 0; var sink = new FakeSink(); var creates = 0;
    await using var session = Session(provider, "a", _ =>
    {
        if (Interlocked.Increment(ref creates) > 1) throw new IOException("Backend unavailable");
        return Task.FromResult<IControllerOutputSink>(sink);
    });
    await session.StartAsync(); sink.Fail = true;
    await Until(() => session.State == "faulted" && !session.Running);
    Check(creates == 4 && sink.Disposed, "Recovery did not stop after three retries");
}

static async Task BlockedOutput()
{
    var provider = new FakeProvider(); provider.Values["a"] = 0; var sink = new FakeSink();
    await using var session = Session(provider, "a", _ => Task.FromResult<IControllerOutputSink>(sink));
    await session.StartAsync(); await Until(() => session.State == "active");
    sink.Block = true;
    await Task.Delay(30);
    await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    Check(sink.Disposed, "Blocked sink was not disposed");
}

static async Task ProtocolCleanup()
{
    foreach (var ending in new[] { "", "{\"type\":\"stop\"}\n", "not-json\n", "{\"type\":\"bad\"}\n" })
    {
        var states = new List<IReadOnlyDictionary<string, double>>();
        var replies = 0;
        try
        {
            await OutputProtocol.RunAsync(new StringReader("{\"type\":\"state\",\"controls\":{\"South\":1}}\n" + ending),
                s => states.Add(s), _ => replies++);
            Check(ending.Length == 0 || ending.Contains("stop"), "Malformed protocol accepted");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { }
        Check(replies == 1 && states.Count == 2 && states[^1].Count == 0, "Protocol failed to acknowledge/neutralize");
    }
    var neutralized = false;
    try
    {
        await OutputProtocol.RunAsync(new StringReader("{\"type\":\"state\",\"controls\":{\"South\":1}}\n"),
            s => { if (s.Count != 0) throw new IOException("Submit failed"); neutralized = true; }, _ => throw new Exception("False acknowledgment"));
    }
    catch (IOException) { }
    Check(neutralized, "Submit failure bypassed neutralization");
}

static async Task Watchdog()
{
    var neutralized = false;
    try { await OutputProtocol.RunAsync(new StalledReader(), s => neutralized = s.Count == 0, _ => { }, TimeSpan.FromMilliseconds(30)); }
    catch (TimeoutException) { }
    Check(neutralized, "Watchdog did not neutralize");
}

static Task Values()
{
    var state = new MappedControllerState(); state[StandardControl.South] = double.NaN;
    state[StandardControl.LeftStickX] = double.PositiveInfinity;
    Check(state.Controls.Values.All(v => v == 0), "Non-finite output escaped");
    var profile = Guid.NewGuid();
    Check(ControllerOutputSession.Identity("a", profile) != ControllerOutputSession.Identity("b", profile), "Identity collision");
    Check(ControllerOutputSession.Identity("a", profile) == ControllerOutputSession.Identity("a", profile), "Unstable identity");
    Check(!ControllerOutputSession.IsReleased(Snapshot("a", 1)), "Held button accepted as released");
    return Task.CompletedTask;
}

sealed class StalledReader : TextReader
{
    public override Task<string?> ReadLineAsync() => new TaskCompletionSource<string?>().Task;
}

sealed class FakeSink : IControllerOutputSink
{
    public volatile bool Fail;
    public volatile bool Block;
    public volatile bool Disposed;
    private double _last;
    public double Last => Volatile.Read(ref _last);
    public string Name => "Fake";
    public bool IsAvailable => !Disposed;
    public async ValueTask SendAsync(MappedControllerState state, CancellationToken token = default)
    {
        if (Block) await Task.Delay(Timeout.Infinite, token);
        if (Fail) throw new IOException("Simulated failed output");
        Volatile.Write(ref _last, state[StandardControl.East]);
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

sealed class FakeProvider : IControllerProvider
{
    public ConcurrentDictionary<string, double> Values { get; } = new();
    public event EventHandler<ControllerDeviceEventArgs>? DeviceAdded { add { } remove { } }
    public event EventHandler<ControllerDeviceEventArgs>? DeviceRemoved { add { } remove { } }
    public ControllerDeviceDescriptor Descriptor(string id) => new(id, "Identical model", ControllerFamily.Xbox, 1, 2, false, true, 1, 0, 0);
    public IReadOnlyList<ControllerDeviceDescriptor> GetConnectedDevices() => Values.Keys.Select(Descriptor).ToArray();
    public bool TryRead(string id, out ControllerSnapshot? snapshot)
    {
        snapshot = Values.TryGetValue(id, out var value) ? new(id, 1,
            new Dictionary<StandardControl, double> { [StandardControl.South] = value }, null, null, null) : null;
        return snapshot is not null;
    }
    public void Dispose() { }
}
