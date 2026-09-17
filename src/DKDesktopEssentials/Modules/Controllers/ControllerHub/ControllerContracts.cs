using System.Collections.ObjectModel;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public enum ControllerFamily
{
    Unknown,
    Xbox,
    PlayStation,
    Nintendo,
    Generic
}

public enum StandardControl
{
    South,
    East,
    West,
    North,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    LeftShoulder,
    RightShoulder,
    LeftStickButton,
    RightStickButton,
    Menu,
    View,
    Paddle1,
    Paddle2,
    Paddle3,
    Paddle4,
    LeftTrigger,
    RightTrigger,
    LeftStickX,
    LeftStickY,
    RightStickX,
    RightStickY
}

public enum ControllerControlKind
{
    Standard,
    RawButton,
    RawAxis,
    RawSwitch,
    Advanced
}

public sealed record ControllerControlRef
{
    public ControllerControlKind Kind { get; init; } = ControllerControlKind.Standard;
    public StandardControl? Standard { get; init; }
    public ControllerAdvancedControl? Advanced { get; init; }
    public int Index { get; init; } = -1;

    public string StableKey => Kind switch
    {
        ControllerControlKind.Standard => $"std:{Standard}",
        ControllerControlKind.Advanced => $"advanced:{Advanced}",
        _ => $"{Kind.ToString().ToLowerInvariant()}:{Index}"
    };

    public static ControllerControlRef ForStandard(StandardControl control) => new()
    {
        Kind = ControllerControlKind.Standard,
        Standard = control
    };

    public static ControllerControlRef ForRawButton(int index) => new()
    {
        Kind = ControllerControlKind.RawButton,
        Index = index
    };

    public static ControllerControlRef ForRawAxis(int index) => new()
    {
        Kind = ControllerControlKind.RawAxis,
        Index = index
    };

    public static ControllerControlRef ForRawSwitch(int index) => new()
    {
        Kind = ControllerControlKind.RawSwitch,
        Index = index
    };

    public static ControllerControlRef ForAdvanced(ControllerAdvancedControl control) => new()
    {
        Kind = ControllerControlKind.Advanced,
        Advanced = control
    };
}

public sealed record ControllerDeviceDescriptor(
    string DeviceId,
    string DisplayName,
    ControllerFamily Family,
    ushort VendorId,
    ushort ProductId,
    bool IsWireless,
    bool SupportsStandardGamepad,
    int ButtonCount,
    int AxisCount,
    int SwitchCount);

public sealed class ControllerDeviceEventArgs : EventArgs
{
    public ControllerDeviceEventArgs(ControllerDeviceDescriptor device) => Device = device;

    public ControllerDeviceDescriptor Device { get; }
}

public sealed class ControllerSnapshot
{
    public ControllerSnapshot(
        string deviceId,
        ulong timestamp,
        IReadOnlyDictionary<StandardControl, double>? standardControls,
        bool[]? rawButtons,
        double[]? rawAxes,
        int[]? rawSwitches,
        ControllerAdvancedState? advancedState = null)
    {
        DeviceId = deviceId;
        Timestamp = timestamp;
        StandardControls = standardControls is null
            ? new ReadOnlyDictionary<StandardControl, double>(new Dictionary<StandardControl, double>())
            : new ReadOnlyDictionary<StandardControl, double>(new Dictionary<StandardControl, double>(standardControls));
        RawButtons = rawButtons ?? Array.Empty<bool>();
        RawAxes = rawAxes ?? Array.Empty<double>();
        RawSwitches = rawSwitches ?? Array.Empty<int>();
        AdvancedState = advancedState ?? ControllerAdvancedState.Empty;
    }

    public string DeviceId { get; }
    public ulong Timestamp { get; }
    public IReadOnlyDictionary<StandardControl, double> StandardControls { get; }
    public bool[] RawButtons { get; }
    public double[] RawAxes { get; }
    public int[] RawSwitches { get; }
    public ControllerAdvancedState AdvancedState { get; }

    public ControllerSnapshot WithAdvancedState(ControllerAdvancedState advancedState) => new(
        DeviceId,
        Timestamp,
        StandardControls,
        RawButtons,
        RawAxes,
        RawSwitches,
        advancedState);

    public double GetValue(ControllerControlRef control)
    {
        return control.Kind switch
        {
            ControllerControlKind.Standard when control.Standard is not null =>
                StandardControls.TryGetValue(control.Standard.Value, out var value) ? value : 0d,
            ControllerControlKind.RawButton when control.Index >= 0 && control.Index < RawButtons.Length =>
                RawButtons[control.Index] ? 1d : 0d,
            ControllerControlKind.RawAxis when control.Index >= 0 && control.Index < RawAxes.Length =>
                RawAxes[control.Index],
            ControllerControlKind.RawSwitch when control.Index >= 0 && control.Index < RawSwitches.Length =>
                RawSwitches[control.Index],
            ControllerControlKind.Advanced when control.Advanced is not null =>
                GetMappedAdvancedValue(control.Advanced.Value),
            _ => 0d
        };
    }

    private double GetMappedAdvancedValue(ControllerAdvancedControl control)
    {
        var value = AdvancedState.GetValue(control);

        return control switch
        {
            ControllerAdvancedControl.Touchpad0X => IsTouchActive(0) ? Math.Clamp((value * 2d) - 1d, -1d, 1d) : 0d,
            ControllerAdvancedControl.Touchpad0Y => IsTouchActive(0) ? Math.Clamp(1d - (value * 2d), -1d, 1d) : 0d,
            ControllerAdvancedControl.Touchpad1X => IsTouchActive(1) ? Math.Clamp((value * 2d) - 1d, -1d, 1d) : 0d,
            ControllerAdvancedControl.Touchpad1Y => IsTouchActive(1) ? Math.Clamp(1d - (value * 2d), -1d, 1d) : 0d,
            _ => value
        };
    }

    private bool IsTouchActive(int slot) =>
        AdvancedState.GetValue(slot == 0
            ? ControllerAdvancedControl.Touchpad0Contact
            : ControllerAdvancedControl.Touchpad1Contact) >= 0.5d;
}

public interface IControllerProvider : IDisposable
{
    event EventHandler<ControllerDeviceEventArgs>? DeviceAdded;
    event EventHandler<ControllerDeviceEventArgs>? DeviceRemoved;

    IReadOnlyList<ControllerDeviceDescriptor> GetConnectedDevices();
    bool TryRead(string deviceId, out ControllerSnapshot? snapshot);
}

public enum ControllerOutputMode
{
    None,
    PassThroughOnly,
    VirtualXbox360,
    VirtualDualShock4
}

public sealed class MappedControllerState
{
    private readonly Dictionary<StandardControl, double> _controls = new();

    public IReadOnlyDictionary<StandardControl, double> Controls => _controls;

    public double this[StandardControl control]
    {
        get => _controls.TryGetValue(control, out var value) ? value : 0d;
        set => _controls[control] = Math.Clamp(value, -1d, 1d);
    }

    public void Clear(StandardControl control) => _controls[control] = 0d;

    public MappedControllerState Clone()
    {
        var clone = new MappedControllerState();
        foreach (var pair in _controls)
            clone[pair.Key] = pair.Value;
        return clone;
    }
}

public interface IControllerOutputSink : IAsyncDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    ValueTask SendAsync(MappedControllerState state, CancellationToken cancellationToken = default);
}

public sealed class NoOutputSink : IControllerOutputSink
{
    public string Name => "No virtual output";
    public bool IsAvailable => true;

    public ValueTask SendAsync(MappedControllerState state, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
