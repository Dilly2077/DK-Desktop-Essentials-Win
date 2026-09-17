namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed partial class ControllerHubService
{
    private readonly SemaphoreSlim _outputSessionGate = new(1, 1);
    private CancellationTokenSource? _outputSessionCts;
    private Task? _outputSessionTask;
    private string? _outputSessionDeviceId;
    private Guid? _outputSessionProfileId;
    private int _outputSessionHz;
    private string? _outputSessionError;

    public async Task StartOutputSessionAsync(
        string deviceId,
        Guid profileId,
        int updateHz = 250,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("A controller device ID is required.", nameof(deviceId));

        updateHz = Math.Clamp(updateHz, 60, 1000);
        var device = _provider.GetConnectedDevices()
            .FirstOrDefault(candidate => string.Equals(candidate.DeviceId, deviceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected physical controller is not currently connected.");

        var profile = await _profileStore.LoadAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException($"Controller profile '{profileId}' does not exist.");

        if (!profile.DeviceMatch.Matches(device))
            throw new InvalidOperationException("The selected profile is not compatible with this controller.");

        if (profile.OutputMode is not ControllerOutputMode.VirtualXbox360 and not ControllerOutputMode.VirtualDualShock4)
            throw new InvalidOperationException("The selected profile must use Xbox 360 or DualShock 4 virtual output.");

        await _outputSessionGate.WaitAsync(cancellationToken);
        try
        {
            await StopOutputSessionCoreAsync();
            await EnableVirtualOutputAsync(
                profile.OutputMode,
                $"dk-desktop-essentials:{profile.Id:N}",
                cancellationToken);

            _outputSessionError = null;
            _outputSessionDeviceId = deviceId;
            _outputSessionProfileId = profileId;
            _outputSessionHz = updateHz;
            _outputSessionCts = new CancellationTokenSource();
            _outputSessionTask = RunOutputSessionAsync(deviceId, profile, updateHz, _outputSessionCts.Token);
        }
        catch
        {
            await DisableVirtualOutputAsync();
            throw;
        }
        finally
        {
            _outputSessionGate.Release();
        }
    }

    public async Task StopOutputSessionAsync()
    {
        await _outputSessionGate.WaitAsync();
        try
        {
            await StopOutputSessionCoreAsync();
        }
        finally
        {
            _outputSessionGate.Release();
        }
    }

    public object GetOutputSessionStatus() => new
    {
        running = _outputSessionTask is { IsCompleted: false },
        deviceId = _outputSessionDeviceId,
        profileId = _outputSessionProfileId,
        updateHz = _outputSessionHz,
        error = _outputSessionError,
        sink = GetActiveOutputStatus(),
        backend = GetOutputBackendStatus()
    };

    private async Task RunOutputSessionAsync(
        string deviceId,
        ControllerProfile profile,
        int updateHz,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(1d / updateHz);
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sent = await ProcessAndSendAsync(deviceId, profile, cancellationToken);
                if (!sent)
                    throw new InvalidOperationException("The source controller or virtual output backend became unavailable.");

                if (!await timer.WaitForNextTickAsync(cancellationToken))
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _outputSessionError = ex.Message;
        }
    }

    private async Task StopOutputSessionCoreAsync()
    {
        var cts = _outputSessionCts;
        var task = _outputSessionTask;
        _outputSessionCts = null;
        _outputSessionTask = null;

        if (cts is not null)
        {
            cts.Cancel();
            if (task is not null)
            {
                try { await task; }
                catch (OperationCanceledException) { }
            }
            cts.Dispose();
        }

        _outputSessionDeviceId = null;
        _outputSessionProfileId = null;
        _outputSessionHz = 0;
        await DisableVirtualOutputAsync();
    }
}
