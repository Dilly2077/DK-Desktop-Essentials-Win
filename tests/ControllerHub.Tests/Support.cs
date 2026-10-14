using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;

internal static class Support
{
    public static readonly JsonSerializerOptions Json = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter()); return options;
    }
    public static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public static void Near(double expected, double actual, string message)
    { Check(double.IsFinite(actual) && Math.Abs(expected - actual) < 0.00001, $"{message}: expected {expected}, got {actual}"); }
    public static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    public static async Task Reject(Func<Task> operation)
    {
        var rejected = false;
        try { await operation(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { rejected = true; }
        Check(rejected, "Invalid input was accepted");
    }
    public static ControllerProfile Profile(params ControllerBinding[] bindings)
    {
        var profile = new ControllerProfile { PassThroughUnmappedControls = false };
        profile.Layers[0].Bindings.AddRange(bindings); return profile;
    }
    public static ControllerBinding Binding(StandardControl source, StandardControl target) => new()
    { Source = ControllerControlRef.ForStandard(source), Target = ControllerAction.ToController(target) };
    public static ControllerSnapshot Snapshot(params (StandardControl Control, double Value)[] values) =>
        new("a", 1, values.ToDictionary(p => p.Control, p => p.Value), null, null, null);
    public static MappedControllerState Map(ControllerProfile profile, ControllerSnapshot snapshot) =>
        new ControllerMappingEngine().Map(snapshot, profile, DateTimeOffset.UnixEpoch);
}

internal sealed class Fixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "DK-hub-tests-" + Guid.NewGuid().ToString("N"));
    public ControllerProfileStore Profiles { get; }
    public ControllerHubPreferencesStore Preferences { get; }
    public InputStub Input { get; } = new();
    public AdvancedStub Advanced { get; } = new();
    public ConcurrentDictionary<string, OutputStub> Outputs { get; } = new();
    public ControllerHubService Service { get; }
    public ControllerHubWebBridge Bridge { get; }

    public Fixture()
    {
        Profiles = new(Path.Combine(Root, "profiles")); Preferences = new(Path.Combine(Root, "preferences.json"));
        Service = new(provider: Input, profileStore: Profiles, preferencesStore: Preferences, advancedProvider: Advanced,
            outputPackageManager: new(Path.Combine(Root, "absent-provider"), Path.Combine(Root, "absent-helper.exe")),
            advancedPackageManager: new(Path.Combine(Root, "absent-sdl")),
            sessionSinkFactory: (_, identity, _) =>
            {
                var output = new OutputStub(); Outputs[identity] = output; return Task.FromResult<IControllerOutputSink>(output);
            });
        Bridge = new(Service);
    }

    public async Task<JsonElement> Command(string type, object? payload = null, bool ok = true)
    {
        var response = await Bridge.HandleAsync(JsonSerializer.Serialize(new { type = "controllerHub:" + type, requestId = "test-id", payload }, Support.Json));
        using var document = JsonDocument.Parse(response ?? throw new Exception("Missing bridge response"));
        var root = document.RootElement;
        Support.Check(root.GetProperty("requestId").GetString() == "test-id" && root.GetProperty("command").GetString() == "controllerHub:" + type, "Bridge correlation lost");
        Support.Check(root.GetProperty("ok").GetBoolean() == ok, response!);
        return root.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal sealed class InputStub : IControllerProvider
{
    public ConcurrentDictionary<string, ControllerSnapshot> States { get; } = new();
    public Exception? EnumerationFailure { get; set; }
    public event EventHandler<ControllerDeviceEventArgs>? DeviceAdded { add { } remove { } }
    public event EventHandler<ControllerDeviceEventArgs>? DeviceRemoved { add { } remove { } }
    public static ControllerDeviceDescriptor Descriptor(string id) => new(id, "Test controller", ControllerFamily.PlayStation, 0x054c, 0x0ce6, false, true, 2, 4, 1);
    public IReadOnlyList<ControllerDeviceDescriptor> GetConnectedDevices()
    {
        if (EnumerationFailure is not null) throw EnumerationFailure;
        return States.Keys.Select(Descriptor).ToArray();
    }
    public bool TryRead(string id, out ControllerSnapshot? snapshot) => States.TryGetValue(id, out snapshot);
    public void Set(string id, double south) => States[id] = new(id, 1, new Dictionary<StandardControl, double> { [StandardControl.South] = south }, null, null, null);
    public void Dispose() { }
}

internal sealed class OutputStub : IControllerOutputSink
{
    public volatile bool Disposed;
    private MappedControllerState _last = new();
    private int _frames;
    public int Frames => Volatile.Read(ref _frames);
    public MappedControllerState Last => Volatile.Read(ref _last);
    public string Name => "Simulated output";
    public bool IsAvailable => !Disposed;
    public ValueTask SendAsync(MappedControllerState state, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); Volatile.Write(ref _last, state.Clone()); Interlocked.Increment(ref _frames); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

internal sealed class AdvancedStub : IControllerAdvancedFeatureProvider
{
    public volatile bool Connected = true;
    public bool IsAvailable => Connected;
    public string? LastError => null;
    public ControllerAdvancedState State { get; set; } = new();
    public IReadOnlyList<ControllerAdvancedDeviceDescriptor> GetDevices() => Connected
        ? new[] { new ControllerAdvancedDeviceDescriptor("advanced-a", "Test advanced controller", 0x054c, 0x0ce6, null, null, ControllerFeatureCapability.Gyroscope | ControllerFeatureCapability.Touchpad, 1, 250, 0) }
        : Array.Empty<ControllerAdvancedDeviceDescriptor>();
    public bool TryReadState(string id, out ControllerAdvancedState? state)
    { state = Connected && id == "advanced-a" ? State : null; return state is not null; }
    public bool TryRumble(string id, double low, double high, TimeSpan duration) => false;
    public bool TryRumbleTriggers(string id, double left, double right, TimeSpan duration) => false;
    public bool TrySetLed(string id, byte r, byte g, byte b) => false;
    public bool TrySetPlayerLed(string id, int index) => false;
    public bool TrySetAdaptiveTriggers(string id, ControllerAdaptiveTriggerState state) => false;
    public void Dispose() { }
}
