namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

[Flags]
public enum ControllerFeatureCapability
{
    None = 0,
    Gyroscope = 1 << 0,
    Accelerometer = 1 << 1,
    Touchpad = 1 << 2,
    Rumble = 1 << 3,
    TriggerRumble = 1 << 4,
    RgbLed = 1 << 5,
    PlayerLed = 1 << 6,
    AdaptiveTriggers = 1 << 7
}

public enum ControllerAdvancedControl
{
    GyroPitch,
    GyroYaw,
    GyroRoll,
    AccelX,
    AccelY,
    AccelZ,
    Touchpad0X,
    Touchpad0Y,
    Touchpad0Pressure,
    Touchpad0Contact,
    Touchpad1X,
    Touchpad1Y,
    Touchpad1Pressure,
    Touchpad1Contact
}

public sealed record ControllerTouchPoint(
    int Touchpad,
    int Finger,
    bool Down,
    double X,
    double Y,
    double Pressure);

public sealed class ControllerAdvancedState
{
    private readonly Dictionary<ControllerAdvancedControl, double> _values;

    public ControllerAdvancedState(
        IReadOnlyDictionary<ControllerAdvancedControl, double>? values = null,
        IReadOnlyList<ControllerTouchPoint>? touchPoints = null,
        DateTimeOffset? capturedAt = null)
    {
        _values = values is null
            ? new Dictionary<ControllerAdvancedControl, double>()
            : new Dictionary<ControllerAdvancedControl, double>(values);
        TouchPoints = touchPoints?.ToArray() ?? Array.Empty<ControllerTouchPoint>();
        CapturedAt = capturedAt ?? DateTimeOffset.UtcNow;
    }

    public DateTimeOffset CapturedAt { get; }
    public IReadOnlyDictionary<ControllerAdvancedControl, double> Values => _values;
    public IReadOnlyList<ControllerTouchPoint> TouchPoints { get; }

    public double GetValue(ControllerAdvancedControl control) =>
        _values.TryGetValue(control, out var value) ? value : 0d;

    public static ControllerAdvancedState Empty { get; } = new();
}

public sealed record ControllerAdvancedDeviceDescriptor(
    string BackendId,
    string DisplayName,
    ushort VendorId,
    ushort ProductId,
    string? Serial,
    string? Path,
    ControllerFeatureCapability Capabilities,
    int TouchpadCount,
    float GyroscopeRateHz,
    float AccelerometerRateHz);

public enum ControllerAdaptiveTriggerMode
{
    Off,
    Resistance,
    Vibration
}

public sealed record ControllerAdaptiveTriggerEffect(
    ControllerAdaptiveTriggerMode Mode,
    double StartPosition = 0d,
    double Strength = 0.5d,
    double Frequency = 0.5d);

public sealed record ControllerAdaptiveTriggerState(
    ControllerAdaptiveTriggerEffect? Left,
    ControllerAdaptiveTriggerEffect? Right);

public sealed record ControllerFeatureBackendStatus(
    string Backend,
    string Version,
    bool Installed,
    bool Loaded,
    string Source,
    string PackageSha256,
    string? Message = null);

public interface IControllerAdvancedFeatureProvider : IDisposable
{
    bool IsAvailable { get; }
    string? LastError { get; }

    IReadOnlyList<ControllerAdvancedDeviceDescriptor> GetDevices();
    bool TryReadState(string backendId, out ControllerAdvancedState? state);
    bool TryRumble(string backendId, double lowFrequency, double highFrequency, TimeSpan duration);
    bool TryRumbleTriggers(string backendId, double left, double right, TimeSpan duration);
    bool TrySetLed(string backendId, byte red, byte green, byte blue);
    bool TrySetPlayerLed(string backendId, int playerIndex);
    bool TrySetAdaptiveTriggers(string backendId, ControllerAdaptiveTriggerState state);
}
