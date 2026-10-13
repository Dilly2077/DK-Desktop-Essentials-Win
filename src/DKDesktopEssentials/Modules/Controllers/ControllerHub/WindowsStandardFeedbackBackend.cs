using Windows.Gaming.Input;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

/// <summary>
/// Implements the standard Windows.Gaming.Input vibration interface only.
/// This is not DualSense HD haptics, adaptive-trigger resistance or LED control.
/// </summary>
public sealed class WindowsStandardFeedbackBackend : IAdvancedControllerBackend
{
    private readonly WindowsGamingInputControllerProvider? _provider;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private bool _disposed;

    public WindowsStandardFeedbackBackend(IControllerProvider provider) =>
        _provider = provider as WindowsGamingInputControllerProvider;

    public string Name => "Windows.Gaming.Input.Gamepad standard vibration";

    public AdvancedControllerCapabilities GetCapabilities(ControllerDeviceDescriptor device)
    {
        var rumble = _provider?.CanSetStandardVibration(device.DeviceId) == true;
        return new AdvancedControllerCapabilities(
            device.DeviceId,
            GyroscopeReadable: false,
            AccelerometerReadable: false,
            TouchpadReadable: false,
            TouchpadClickable: false,
            RumbleWritable: rumble,
            PlayerLedsWritable: false,
            LightbarWritable: false,
            AdaptiveTriggersWritable: false,
            InputBackend: "Windows.Gaming.Input.RawGameController",
            OutputBackend: rumble ? Name : "None",
            Limitations: new[]
            {
                "Gyroscope/accelerometer readings and semantic touch contacts are not exposed by this backend.",
                "Standard Gamepad vibration does not guarantee separate physical trigger motors; the driver may approximate them.",
                "DualSense HD haptics, light bar, player LEDs and adaptive trigger resistance are unsupported.",
                "The device manufacturer alone cannot establish these capabilities."
            });
    }

    public bool TryReadMotion(string deviceId, out ControllerMotionReading? reading)
    {
        reading = null;
        return false;
    }

    public bool TryReadTouchpad(string deviceId, out ControllerTouchpadReading? reading)
    {
        reading = null;
        return false;
    }

    public ValueTask<ControllerFeedbackResult> SetFeedbackAsync(
        string deviceId, ControllerFeedbackRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device ID is required.", nameof(deviceId));

        if (request.LightbarRed is not null || request.LightbarGreen is not null ||
            request.LightbarBlue is not null || request.PlayerLed is not null ||
            request.LeftAdaptiveTrigger is not null || request.RightAdaptiveTrigger is not null)
            return ValueTask.FromResult(new ControllerFeedbackResult(false,
                "Device-specific light bars, player LEDs and adaptive triggers are not supported by the Windows standard feedback backend."));

        if (request.DurationMilliseconds is < 1 or > 5000)
            return ValueTask.FromResult(new ControllerFeedbackResult(false,
                "Vibration duration must be between 1 and 5000 milliseconds."));

        if (!Valid(request.LeftRumble) || !Valid(request.RightRumble) ||
            !Valid(request.LeftTriggerRumble) || !Valid(request.RightTriggerRumble))
            return ValueTask.FromResult(new ControllerFeedbackResult(false,
                "Vibration motor levels must be finite numbers between 0 and 1."));

        if (request.LeftRumble is null && request.RightRumble is null &&
            request.LeftTriggerRumble is null && request.RightTriggerRumble is null)
            return ValueTask.FromResult(new ControllerFeedbackResult(false,
                "Specify at least one vibration motor."));

        if (_provider is null)
            return ValueTask.FromResult(new ControllerFeedbackResult(false, "A Windows physical controller backend is not active."));

        lock (_gate)
        {
            if (_disposed)
                return ValueTask.FromResult(new ControllerFeedbackResult(false, "Feedback backend is disposed."));

            var vibration = new GamepadVibration
            {
                LeftMotor = request.LeftRumble ?? 0d,
                RightMotor = request.RightRumble ?? 0d,
                LeftTrigger = request.LeftTriggerRumble ?? 0d,
                RightTrigger = request.RightTriggerRumble ?? 0d
            };

            if (!_provider.TrySetStandardVibration(deviceId, vibration))
                return ValueTask.FromResult(new ControllerFeedbackResult(false,
                    "The physical controller was disconnected or has no standard Gamepad vibration interface."));

            if (_active.Remove(deviceId, out var old)) old.Cancel();
            var cts = new CancellationTokenSource();
            _active[deviceId] = cts;
            _ = ResetAfterDelayAsync(deviceId, cts, request.DurationMilliseconds);

            return ValueTask.FromResult(new ControllerFeedbackResult(true,
                "Standard vibration activated temporarily; it will reset automatically."));
        }
    }

    public ValueTask<ControllerFeedbackResult> StopFeedbackAsync(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device ID is required.", nameof(deviceId));
        lock (_gate)
        {
            if (_active.Remove(deviceId, out var cts)) cts.Cancel();
            var stopped = _provider?.TrySetStandardVibration(deviceId, new GamepadVibration()) == true;
            return ValueTask.FromResult(new ControllerFeedbackResult(stopped,
                stopped ? "Vibration stopped." : "The physical controller is unavailable or lacks standard vibration."));
        }
    }

    private async Task ResetAfterDelayAsync(string deviceId, CancellationTokenSource cts, int milliseconds)
    {
        try
        {
            await Task.Delay(milliseconds, cts.Token);
            lock (_gate)
            {
                if (_active.TryGetValue(deviceId, out var current) && ReferenceEquals(current, cts))
                {
                    _active.Remove(deviceId);
                    _provider?.TrySetStandardVibration(deviceId, new GamepadVibration());
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { cts.Dispose(); }
    }

    private static bool Valid(double? value) =>
        value is null || double.IsFinite(value.Value) && value.Value is >= 0d and <= 1d;

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            foreach (var pair in _active)
            {
                pair.Value.Cancel();
                _provider?.TrySetStandardVibration(pair.Key, new GamepadVibration());
            }
            _active.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
