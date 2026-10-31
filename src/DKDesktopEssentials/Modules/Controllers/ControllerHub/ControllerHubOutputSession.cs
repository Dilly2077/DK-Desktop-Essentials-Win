using System.Collections.Concurrent;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed partial class ControllerHubService
{
    private readonly SemaphoreSlim _outputSessionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ControllerOutputSession> _sessions = new(StringComparer.Ordinal);

    public async Task StartOutputSessionAsync(string deviceId, Guid profileId, int updateHz = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await _outputSessionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var device = _provider.GetConnectedDevices().SingleOrDefault(d => d.DeviceId == deviceId)
                ?? throw new InvalidOperationException("The selected controller is not connected.");
            var profile = await _profileStore.LoadAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException("The selected profile does not exist.");
            if (!device.SupportsVirtualOutput)
                throw new InvalidOperationException("Windows joystick fallback supports raw inspection only; virtual output requires a provider with stable physical identity.");
            if (!profile.DeviceMatch.Matches(device))
                throw new InvalidOperationException("The profile is not compatible with this controller.");
            if (profile.OutputMode is not ControllerOutputMode.VirtualXbox360 and not ControllerOutputMode.VirtualDualShock4)
                throw new InvalidOperationException("Select Xbox 360 or DualShock 4 virtual output.");
            if (!string.IsNullOrEmpty(profile.AdvancedDeviceId) && _sessions.Values.Any(s =>
                s.DeviceId != deviceId && s.AdvancedDeviceId == profile.AdvancedDeviceId))
                throw new InvalidOperationException("This advanced device is already assigned to another output session.");
            if (_sessions.TryRemove(deviceId, out var previous)) await previous.DisposeAsync();
            var session = new ControllerOutputSession(device, profile, updateHz, _provider,
                () => _advancedProvider,
                token => _sessionSinkFactory(profile.OutputMode,
                    ControllerOutputSession.Identity(deviceId, profileId), token));
            try
            {
                await session.StartAsync(cancellationToken);
                _sessions[deviceId] = session;
            }
            catch { await session.DisposeAsync(); throw; }
        }
        finally { _outputSessionGate.Release(); }
    }

    // Omitting the ID retains the existing emergency stop-all behavior.
    public async Task StopOutputSessionAsync(string? deviceId = null)
    {
        await _outputSessionGate.WaitAsync();
        try { await StopSessionsCoreAsync(deviceId); }
        finally { _outputSessionGate.Release(); }
    }

    private async Task StopSessionsCoreAsync(string? deviceId = null)
    {
        foreach (var key in _sessions.Keys.Where(k => deviceId is null || k == deviceId))
            if (_sessions.TryRemove(key, out var session)) await session.DisposeAsync();
        if (deviceId is null) await DisableVirtualOutputAsync();
    }

    public object GetOutputSessionStatus() => new
    {
        running = _sessions.Values.Any(s => s.Running),
        sessions = _sessions.Values.OrderBy(s => s.DeviceId).Select(s => s.GetStatus()).ToArray(),
        sink = GetActiveOutputStatus(),
        backend = GetOutputBackendStatus(),
        compatibility = new
        {
            deviceHidingAvailable = false,
            deviceHidingReason = "No device-hiding provider has been vetted. Physical devices remain visible; games may see duplicate input.",
            reconnectPolicy = "Exact Windows device ID and vendor/product only; changed IDs require explicit selection.",
            telemetry = false
        }
    };
}
