namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

/// <summary>
/// Explicit capability reporting for optional controller-specific functionality.
/// A vendor ID alone never proves that gyro, touchpad, haptics or adaptive triggers
/// can be read or controlled by the active Windows input backend.
/// </summary>
public sealed record AdvancedControllerCapabilities(
    string DeviceId,
    bool GyroscopeReadable,
    bool AccelerometerReadable,
    bool TouchpadReadable,
    bool TouchpadClickable,
    bool RumbleWritable,
    bool PlayerLedsWritable,
    bool LightbarWritable,
    bool AdaptiveTriggersWritable,
    string InputBackend,
    string OutputBackend,
    IReadOnlyList<string> Limitations);

public interface IAdvancedControllerBackend : IAsyncDisposable
{
    string Name { get; }
    AdvancedControllerCapabilities GetCapabilities(ControllerDeviceDescriptor device);
    bool TryReadMotion(string deviceId, out ControllerMotionReading? reading);
    bool TryReadTouchpad(string deviceId, out ControllerTouchpadReading? reading);
    ValueTask<ControllerFeedbackResult> SetFeedbackAsync(
        string deviceId, ControllerFeedbackRequest request,
        CancellationToken cancellationToken = default);
    ValueTask<ControllerFeedbackResult> StopFeedbackAsync(string deviceId);
}

public sealed record ControllerMotionReading(
    double GyroX, double GyroY, double GyroZ,
    double AccelerationX, double AccelerationY, double AccelerationZ,
    long TimestampMicroseconds);

public sealed record ControllerTouchContact(int ContactId, double X, double Y, bool Active);
public sealed record ControllerTouchpadReading(
    bool Pressed, IReadOnlyList<ControllerTouchContact> Contacts, long TimestampMicroseconds);

public sealed record ControllerFeedbackRequest(
    double? LeftRumble = null,
    double? RightRumble = null,
    byte? LightbarRed = null,
    byte? LightbarGreen = null,
    byte? LightbarBlue = null,
    int? PlayerLed = null,
    byte[]? LeftAdaptiveTrigger = null,
    byte[]? RightAdaptiveTrigger = null,
    double? LeftTriggerRumble = null,
    double? RightTriggerRumble = null,
    int DurationMilliseconds = 500);

public sealed record ControllerFeedbackResult(bool Applied, string Reason);

/// <summary>
/// Safe default: do not claim or fake device-specific features when the active
/// Windows.Gaming.Input provider exposes only standard/raw gamepad readings.
/// </summary>
public sealed class UnsupportedAdvancedControllerBackend : IAdvancedControllerBackend
{
    public string Name => "Windows standard/raw input (advanced features unavailable)";

    public AdvancedControllerCapabilities GetCapabilities(ControllerDeviceDescriptor device) =>
        new(device.DeviceId, false, false, false, false, false, false, false, false,
            "Windows.Gaming.Input.RawGameController", "None",
            new[] {
                "The current provider does not expose gyro or touch contacts.",
                "Controller-specific output reports are not implemented.",
                "PlayStation family detection alone is not proof of feature support."
            });

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
        string deviceId, ControllerFeedbackRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ControllerFeedbackResult(false,
            "No verified device-specific feedback backend is installed."));

    public ValueTask<ControllerFeedbackResult> StopFeedbackAsync(string deviceId) =>
        ValueTask.FromResult(new ControllerFeedbackResult(false, "No feedback backend is installed."));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
