namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

/// <summary>
/// Slice 5 advanced-feature façade. The optional backend is deliberately separate
/// from virtual gamepad output: virtual Xbox/DS4 input does not imply that a physical
/// DualSense feature is available.
/// </summary>
public sealed class ControllerAdvancedFeatures : IAsyncDisposable
{
    private readonly IControllerProvider _devices;
    private readonly IAdvancedControllerBackend _backend;

    public ControllerAdvancedFeatures(IControllerProvider devices, IAdvancedControllerBackend? backend = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _backend = backend ?? new UnsupportedAdvancedControllerBackend();
    }

    public IReadOnlyList<AdvancedControllerCapabilities> ListCapabilities() =>
        _devices.GetConnectedDevices().Select(_backend.GetCapabilities).ToArray();

    public AdvancedControllerCapabilities GetCapabilities(string deviceId)
    {
        var device = _devices.GetConnectedDevices().FirstOrDefault(
            d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));
        if (device is null)
            throw new InvalidOperationException("Controller is not connected.");
        return _backend.GetCapabilities(device);
    }

    public bool TryReadMotion(string deviceId, out ControllerMotionReading? reading)
    {
        reading = null;
        return GetCapabilities(deviceId).GyroscopeReadable &&
            _backend.TryReadMotion(deviceId, out reading);
    }

    public bool TryReadTouchpad(string deviceId, out ControllerTouchpadReading? reading)
    {
        reading = null;
        return GetCapabilities(deviceId).TouchpadReadable &&
            _backend.TryReadTouchpad(deviceId, out reading);
    }

    public ValueTask<ControllerFeedbackResult> SetFeedbackAsync(
        string deviceId, ControllerFeedbackRequest request,
        CancellationToken cancellationToken = default)
    {
        var caps = GetCapabilities(deviceId);
        if (!caps.RumbleWritable && !caps.LightbarWritable &&
            !caps.PlayerLedsWritable && !caps.AdaptiveTriggersWritable)
            return ValueTask.FromResult(new ControllerFeedbackResult(false,
                "No supported feedback feature for this controller/backend."));
        return _backend.SetFeedbackAsync(deviceId, request, cancellationToken);
    }

    public ValueTask DisposeAsync() => _backend.DisposeAsync();
}
