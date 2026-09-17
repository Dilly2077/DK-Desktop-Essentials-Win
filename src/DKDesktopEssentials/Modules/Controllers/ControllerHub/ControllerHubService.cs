namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed class ControllerHubService : IAsyncDisposable
{
    private readonly IControllerProvider _provider;
    private readonly ControllerMappingEngine _mappingEngine;
    private readonly ControllerProfileStore _profileStore;
    private IControllerOutputSink _outputSink;
    private bool _disposed;

    public ControllerHubService(
        IControllerProvider? provider = null,
        ControllerMappingEngine? mappingEngine = null,
        ControllerProfileStore? profileStore = null,
        IControllerOutputSink? outputSink = null)
    {
        _provider = provider ?? new WindowsGamingInputControllerProvider();
        _mappingEngine = mappingEngine ?? new ControllerMappingEngine();
        _profileStore = profileStore ?? new ControllerProfileStore();
        _outputSink = outputSink ?? new NoOutputSink();

        _provider.DeviceAdded += OnDeviceAdded;
        _provider.DeviceRemoved += OnDeviceRemoved;
    }

    public event EventHandler<ControllerDeviceEventArgs>? DeviceAdded;
    public event EventHandler<ControllerDeviceEventArgs>? DeviceRemoved;

    public IReadOnlyList<ControllerDeviceDescriptor> GetConnectedDevices() =>
        _provider.GetConnectedDevices();

    public bool TryReadRawState(string deviceId, out ControllerSnapshot? snapshot) =>
        _provider.TryRead(deviceId, out snapshot);

    public bool TryReadMappedState(
        string deviceId,
        ControllerProfile profile,
        out MappedControllerState? mappedState)
    {
        mappedState = null;

        if (!_provider.TryRead(deviceId, out var snapshot) || snapshot is null)
            return false;

        mappedState = _mappingEngine.Map(snapshot, profile, DateTimeOffset.UtcNow);
        return true;
    }

    public async ValueTask<bool> ProcessAndSendAsync(
        string deviceId,
        ControllerProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadMappedState(deviceId, profile, out var state) || state is null)
            return false;

        if (profile.OutputMode is ControllerOutputMode.None or ControllerOutputMode.PassThroughOnly)
            return true;

        if (!_outputSink.IsAvailable)
            return false;

        await _outputSink.SendAsync(state, cancellationToken);
        return true;
    }

    public Task SaveProfileAsync(ControllerProfile profile, CancellationToken cancellationToken = default) =>
        _profileStore.SaveAsync(profile, cancellationToken);

    public Task<ControllerProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _profileStore.LoadAsync(profileId, cancellationToken);

    public Task<IReadOnlyList<ControllerProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default) =>
        _profileStore.LoadAllAsync(cancellationToken);

    public Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _profileStore.DeleteAsync(profileId, cancellationToken);

    public async ValueTask SetOutputSinkAsync(IControllerOutputSink outputSink)
    {
        ArgumentNullException.ThrowIfNull(outputSink);

        var previous = _outputSink;
        _outputSink = outputSink;
        _mappingEngine.ResetRuntimeState();

        await previous.DisposeAsync();
    }

    public void ResetRuntimeState() => _mappingEngine.ResetRuntimeState();

    private void OnDeviceAdded(object? sender, ControllerDeviceEventArgs e) =>
        DeviceAdded?.Invoke(this, e);

    private void OnDeviceRemoved(object? sender, ControllerDeviceEventArgs e)
    {
        _mappingEngine.ResetRuntimeState();
        DeviceRemoved?.Invoke(this, e);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _provider.DeviceAdded -= OnDeviceAdded;
        _provider.DeviceRemoved -= OnDeviceRemoved;
        _provider.Dispose();
        await _outputSink.DisposeAsync();
    }
}
