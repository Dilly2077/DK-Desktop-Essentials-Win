namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed class ControllerHubService : IAsyncDisposable
{
    private readonly IControllerProvider _provider;
    private readonly ControllerMappingEngine _mappingEngine;
    private readonly ControllerProfileStore _profileStore;
    private readonly ControllerHubPreferencesStore _preferencesStore;
    private IControllerOutputSink _outputSink;
    private bool _disposed;

    public ControllerHubService(
        IControllerProvider? provider = null,
        ControllerMappingEngine? mappingEngine = null,
        ControllerProfileStore? profileStore = null,
        ControllerHubPreferencesStore? preferencesStore = null,
        IControllerOutputSink? outputSink = null)
    {
        _provider = provider ?? new WindowsGamingInputControllerProvider();
        _mappingEngine = mappingEngine ?? new ControllerMappingEngine();
        _profileStore = profileStore ?? new ControllerProfileStore();
        _preferencesStore = preferencesStore ?? new ControllerHubPreferencesStore();
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

    public async Task<ControllerProfile> CreateProfileAsync(
        string? deviceId = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        ControllerDeviceDescriptor? device = null;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            device = _provider.GetConnectedDevices()
                .FirstOrDefault(candidate => string.Equals(candidate.DeviceId, deviceId, StringComparison.Ordinal));

            if (device is null)
                throw new InvalidOperationException($"Controller '{deviceId}' is not currently connected.");
        }

        var profile = ControllerProfile.CreateDefault(device);
        if (!string.IsNullOrWhiteSpace(name))
            profile.Name = name.Trim();

        await _profileStore.SaveAsync(profile, cancellationToken);
        return profile;
    }

    public Task SaveProfileAsync(ControllerProfile profile, CancellationToken cancellationToken = default) =>
        _profileStore.SaveAsync(profile, cancellationToken);

    public Task<ControllerProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _profileStore.LoadAsync(profileId, cancellationToken);

    public Task<IReadOnlyList<ControllerProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default) =>
        _profileStore.LoadAllAsync(cancellationToken);

    public Task<ControllerProfileLoadResult> LoadProfilesDetailedAsync(CancellationToken cancellationToken = default) =>
        _profileStore.LoadAllDetailedAsync(cancellationToken);

    public Task<ControllerProfile> DuplicateProfileAsync(
        Guid profileId,
        string? newName = null,
        CancellationToken cancellationToken = default) =>
        _profileStore.DuplicateAsync(profileId, newName, cancellationToken);

    public Task<string> ExportProfileJsonAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _profileStore.ExportJsonAsync(profileId, cancellationToken);

    public Task<ControllerProfile> ImportProfileJsonAsync(
        string json,
        bool preserveId = false,
        CancellationToken cancellationToken = default) =>
        _profileStore.ImportJsonAsync(json, preserveId, cancellationToken);

    public IReadOnlyList<string> GetProfileValidationErrors(ControllerProfile profile) =>
        _profileStore.GetValidationErrors(profile);

    public async Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await _profileStore.DeleteAsync(profileId, cancellationToken);
        await _preferencesStore.RemoveProfileReferencesAsync(profileId, cancellationToken);
    }

    public Task<ControllerHubPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default) =>
        _preferencesStore.LoadAsync(cancellationToken);

    public Task SetDeviceNameAsync(string deviceId, string? name, CancellationToken cancellationToken = default) =>
        _preferencesStore.SetDeviceNameAsync(deviceId, name, cancellationToken);

    public async Task SetDefaultDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(deviceId) &&
            !_provider.GetConnectedDevices().Any(candidate => string.Equals(candidate.DeviceId, deviceId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Controller '{deviceId}' is not currently connected.");
        }

        await _preferencesStore.SetDefaultDeviceAsync(deviceId, cancellationToken);
    }

    public async Task SetDefaultProfileAsync(
        string deviceId,
        Guid? profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId is not null)
        {
            var profile = await _profileStore.LoadAsync(profileId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Controller profile '{profileId}' does not exist.");

            var device = _provider.GetConnectedDevices()
                .FirstOrDefault(candidate => string.Equals(candidate.DeviceId, deviceId, StringComparison.Ordinal));

            if (device is not null && !profile.DeviceMatch.Matches(device))
                throw new InvalidOperationException("The selected profile is not compatible with this controller.");
        }

        await _preferencesStore.SetDefaultProfileAsync(deviceId, profileId, cancellationToken);
    }

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
