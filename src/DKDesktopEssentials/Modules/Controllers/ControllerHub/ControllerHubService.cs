namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed partial class ControllerHubService : IAsyncDisposable
{
    private readonly IControllerProvider _provider;
    private readonly ControllerMappingEngine _mappingEngine;
    private readonly ControllerProfileStore _profileStore;
    private readonly ControllerHubPreferencesStore _preferencesStore;
    private readonly HidMaestroPackageManager _outputPackageManager;
    private readonly Sdl3PackageManager _advancedPackageManager;
    private IControllerAdvancedFeatureProvider _advancedProvider;
    private IControllerOutputSink _outputSink;
    private readonly SemaphoreSlim _legacyOutputGate = new(1, 1);
    private readonly Func<ControllerOutputMode, string, CancellationToken, Task<IControllerOutputSink>> _sessionSinkFactory;
    private bool _disposed;

    public ControllerHubService(
        IControllerProvider? provider = null,
        ControllerMappingEngine? mappingEngine = null,
        ControllerProfileStore? profileStore = null,
        ControllerHubPreferencesStore? preferencesStore = null,
        IControllerOutputSink? outputSink = null,
        HidMaestroPackageManager? outputPackageManager = null,
        Sdl3PackageManager? advancedPackageManager = null,
        IControllerAdvancedFeatureProvider? advancedProvider = null,
        Func<ControllerOutputMode, string, CancellationToken, Task<IControllerOutputSink>>? sessionSinkFactory = null)
    {
        _provider = provider ?? new WindowsGamingInputControllerProvider();
        _mappingEngine = mappingEngine ?? new ControllerMappingEngine();
        _profileStore = profileStore ?? new ControllerProfileStore();
        _preferencesStore = preferencesStore ?? new ControllerHubPreferencesStore();
        _outputSink = outputSink ?? new NoOutputSink();
        _outputPackageManager = outputPackageManager ?? new HidMaestroPackageManager();
        _sessionSinkFactory = sessionSinkFactory ?? ((mode, identity, token) =>
            HidMaestroOutputSink.CreateForSessionAsync(mode, _outputPackageManager, identity, token));
        _advancedPackageManager = advancedPackageManager ?? new Sdl3PackageManager();
        _advancedProvider = advancedProvider ?? new Sdl3AdvancedControllerProvider(_advancedPackageManager);

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

        if (!string.IsNullOrWhiteSpace(profile.AdvancedDeviceId) &&
            _advancedProvider.TryReadState(profile.AdvancedDeviceId, out var advancedState) &&
            advancedState is not null)
        {
            snapshot = snapshot.WithAdvancedState(advancedState);
        }

        mappedState = _mappingEngine.Map(snapshot, profile, DateTimeOffset.UtcNow);
        return true;
    }

    public async ValueTask<bool> ProcessAndSendAsync(
        string deviceId,
        ControllerProfile profile,
        CancellationToken cancellationToken = default)
    {
        await _legacyOutputGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!TryReadMappedState(deviceId, profile, out var state) || state is null)
            {
                await _outputSink.SendAsync(new MappedControllerState(), cancellationToken);
                _mappingEngine.ResetRuntimeState();
                return false;
            }

            if (profile.OutputMode is ControllerOutputMode.None or ControllerOutputMode.PassThroughOnly)
                return true;

            if (!_outputSink.IsAvailable)
                return false;

            await _outputSink.SendAsync(state, cancellationToken);
            return true;
        }
        finally { _legacyOutputGate.Release(); }
    }

    public ControllerFeatureBackendStatus GetAdvancedFeatureBackendStatus()
    {
        if (_advancedProvider is Sdl3AdvancedControllerProvider sdl)
            return sdl.GetStatus();

        return new ControllerFeatureBackendStatus(
            _advancedProvider.GetType().Name,
            "custom",
            Installed: _advancedProvider.IsAvailable,
            Loaded: _advancedProvider.IsAvailable,
            Source: "custom provider",
            PackageSha256: string.Empty,
            Message: _advancedProvider.LastError);
    }

    public async Task<ControllerFeatureBackendStatus> InstallAdvancedFeatureBackendAsync(CancellationToken cancellationToken = default)
    {
        await _outputSessionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopSessionsCoreAsync();
            var status = await _advancedPackageManager.InstallAsync(cancellationToken);
            _advancedProvider.Dispose();
            _advancedProvider = new Sdl3AdvancedControllerProvider(_advancedPackageManager);
            _ = _advancedProvider.IsAvailable;
            return GetAdvancedFeatureBackendStatus() with { Message = status.Message ?? _advancedProvider.LastError };
        }
        finally { _outputSessionGate.Release(); }
    }

    public async Task RemoveAdvancedFeatureBackendAsync(CancellationToken cancellationToken = default)
    {
        await _outputSessionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopSessionsCoreAsync();
            _advancedProvider.Dispose();
            await _advancedPackageManager.RemoveAsync(cancellationToken);
            _advancedProvider = new Sdl3AdvancedControllerProvider(_advancedPackageManager);
        }
        finally { _outputSessionGate.Release(); }
    }

    public IReadOnlyList<ControllerAdvancedDeviceDescriptor> GetAdvancedFeatureDevices() =>
        _advancedProvider.GetDevices();

    public bool TryReadAdvancedState(string backendId, out ControllerAdvancedState? state) =>
        _advancedProvider.TryReadState(backendId, out state);

    public bool TryRumbleAdvancedDevice(string backendId, double low, double high, TimeSpan duration) =>
        _advancedProvider.TryRumble(backendId, low, high, duration);

    public bool TryRumbleAdvancedTriggers(string backendId, double left, double right, TimeSpan duration) =>
        _advancedProvider.TryRumbleTriggers(backendId, left, right, duration);

    public bool TrySetAdvancedDeviceLed(string backendId, byte red, byte green, byte blue) =>
        _advancedProvider.TrySetLed(backendId, red, green, blue);

    public bool TrySetAdvancedDevicePlayerLed(string backendId, int playerIndex) =>
        _advancedProvider.TrySetPlayerLed(backendId, playerIndex);

    public bool TrySetAdvancedAdaptiveTriggers(string backendId, ControllerAdaptiveTriggerState state) =>
        _advancedProvider.TrySetAdaptiveTriggers(backendId, state);

    public async Task<ControllerProfile> SetProfileAdvancedDeviceAsync(
        Guid profileId,
        string? backendId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _profileStore.LoadAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException($"Controller profile '{profileId}' does not exist.");

        if (!string.IsNullOrWhiteSpace(backendId))
        {
            var advancedDevice = _advancedProvider.GetDevices()
                .FirstOrDefault(device => string.Equals(device.BackendId, backendId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Advanced controller device '{backendId}' is not available.");

            if (profile.DeviceMatch.VendorId is not null && profile.DeviceMatch.VendorId.Value != advancedDevice.VendorId)
                throw new InvalidOperationException("The selected advanced-feature device has a different vendor ID than this profile.");

            if (profile.DeviceMatch.ProductId is not null && profile.DeviceMatch.ProductId.Value != advancedDevice.ProductId)
                throw new InvalidOperationException("The selected advanced-feature device has a different product ID than this profile.");

            profile.AdvancedDeviceId = backendId;
        }
        else
        {
            profile.AdvancedDeviceId = null;
        }

        await _profileStore.SaveAsync(profile, cancellationToken);
        _mappingEngine.ResetRuntimeState();
        return profile;
    }

    public ControllerOutputBackendStatus GetOutputBackendStatus() =>
        _outputPackageManager.GetStatus();

    public async Task<ControllerOutputBackendStatus> InstallOutputProviderPackageAsync(CancellationToken cancellationToken = default)
    {
        await MaintainOutputAsync(async () => { await _outputPackageManager.InstallProviderPackageAsync(cancellationToken); }, cancellationToken);
        return GetOutputBackendStatus();
    }

    public Task InstallOutputDriverAsync(CancellationToken cancellationToken = default) =>
        MaintainOutputAsync(() => _outputPackageManager.InstallDriverAsync(cancellationToken), cancellationToken);

    public Task RepairOutputBackendAsync(CancellationToken cancellationToken = default) =>
        MaintainOutputAsync(() => _outputPackageManager.RepairAsync(cancellationToken), cancellationToken);

    private async Task MaintainOutputAsync(Func<Task> operation, CancellationToken token)
    {
        await _outputSessionGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopSessionsCoreAsync();
            await operation();
        }
        finally { _outputSessionGate.Release(); }
    }

    public async Task RemoveOutputBackendAsync(bool removeProviderPackage, CancellationToken cancellationToken = default)
    {
        await MaintainOutputAsync(async () =>
        {
            await _outputPackageManager.RemoveDriverAsync(cancellationToken);
            if (removeProviderPackage) await _outputPackageManager.RemoveProviderPackageAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task EnableVirtualOutputAsync(
        ControllerOutputMode mode,
        string? identityKey = null,
        CancellationToken cancellationToken = default)
    {
        await _outputSessionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (mode is not ControllerOutputMode.VirtualXbox360 and not ControllerOutputMode.VirtualDualShock4)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Select Xbox 360 or DualShock 4 virtual output.");

            var key = string.IsNullOrWhiteSpace(identityKey)
                ? "dk-desktop-essentials:primary"
                : identityKey.Trim();

            var sink = await HidMaestroOutputSink.CreateAsync(mode, _outputPackageManager, key, cancellationToken);
            try
            {
                await SetOutputSinkAsync(sink);
            }
            catch
            {
                await sink.DisposeAsync();
                throw;
            }
        }
        finally { _outputSessionGate.Release(); }
    }

    public ValueTask DisableVirtualOutputAsync() =>
        SetOutputSinkAsync(new NoOutputSink());

    public object GetActiveOutputStatus() => new
    {
        name = _outputSink.Name,
        available = _outputSink.IsAvailable,
        isVirtual = _outputSink is HidMaestroOutputSink
    };

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
        await _legacyOutputGate.WaitAsync();
        try
        {
            var previous = _outputSink;
            if (_disposed && outputSink is not NoOutputSink)
                throw new ObjectDisposedException(nameof(ControllerHubService));
            if (ReferenceEquals(previous, outputSink)) return;
            _outputSink = outputSink;
            _mappingEngine.ResetRuntimeState();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await previous.SendAsync(new MappedControllerState(), timeout.Token);
            }
            catch (Exception) { /* A failed helper still must be disposed/killed. */ }
            finally { await previous.DisposeAsync(); }
        }
        finally { _legacyOutputGate.Release(); }
    }

    public void ResetRuntimeState()
    {
        _mappingEngine.ResetRuntimeState();
        foreach (var session in _sessions.Values) session.Reset();
    }

    private void OnDeviceAdded(object? sender, ControllerDeviceEventArgs e)
    {
        if (_sessions.TryGetValue(e.Device.DeviceId, out var session)) session.Reset();
        DeviceAdded?.Invoke(this, e);
    }

    private void OnDeviceRemoved(object? sender, ControllerDeviceEventArgs e)
    {
        _mappingEngine.ResetRuntimeState();
        if (_sessions.TryGetValue(e.Device.DeviceId, out var session)) session.Reset();
        DeviceRemoved?.Invoke(this, e);
    }

    public async ValueTask DisposeAsync()
    {
        await _outputSessionGate.WaitAsync();
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _provider.DeviceAdded -= OnDeviceAdded;
            _provider.DeviceRemoved -= OnDeviceRemoved;
            try { await StopSessionsCoreAsync(); }
            finally
            {
                try { _advancedProvider.Dispose(); }
                finally { _provider.Dispose(); }
            }
        }
        finally { _outputSessionGate.Release(); }
    }
}
