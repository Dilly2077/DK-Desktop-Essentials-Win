using Windows.Gaming.Input;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed class WindowsGamingInputControllerProvider : IControllerProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RawGameController> _controllers = new(StringComparer.Ordinal);
    private bool _disposed;

    public WindowsGamingInputControllerProvider()
    {
        foreach (var controller in RawGameController.RawGameControllers)
            TrackController(controller, raiseEvent: false);

        RawGameController.RawGameControllerAdded += OnControllerAdded;
        RawGameController.RawGameControllerRemoved += OnControllerRemoved;
    }

    public event EventHandler<ControllerDeviceEventArgs>? DeviceAdded;
    public event EventHandler<ControllerDeviceEventArgs>? DeviceRemoved;

    public IReadOnlyList<ControllerDeviceDescriptor> GetConnectedDevices()
    {
        lock (_gate)
        {
            return _controllers.Values
                .Select(CreateDescriptor)
                .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DeviceId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool TryRead(string deviceId, out ControllerSnapshot? snapshot)
    {
        snapshot = null;
        RawGameController? controller;

        lock (_gate)
        {
            if (!_controllers.TryGetValue(deviceId, out controller))
                return false;
        }

        try
        {
            var buttons = new bool[controller.ButtonCount];
            var switches = new GameControllerSwitchPosition[controller.SwitchCount];
            var axes = new double[controller.AxisCount];
            var timestamp = controller.GetCurrentReading(buttons, switches, axes);
            var standard = ReadStandardGamepad(controller);

            snapshot = new ControllerSnapshot(
                deviceId,
                timestamp,
                standard,
                buttons,
                axes,
                switches.Select(x => (int)x).ToArray());

            return true;
        }
        catch
        {
            // A disconnect can race with a poll. The next device refresh/event will reconcile it.
            return false;
        }
    }

    private void OnControllerAdded(object? sender, RawGameController controller) =>
        TrackController(controller, raiseEvent: true);

    private void OnControllerRemoved(object? sender, RawGameController controller)
    {
        ControllerDeviceDescriptor? descriptor = null;

        lock (_gate)
        {
            if (_controllers.Remove(controller.NonRoamableId))
                descriptor = CreateDescriptor(controller);
        }

        if (descriptor is not null)
            DeviceRemoved?.Invoke(this, new ControllerDeviceEventArgs(descriptor));
    }

    private void TrackController(RawGameController controller, bool raiseEvent)
    {
        ControllerDeviceDescriptor? descriptor = null;

        lock (_gate)
        {
            if (_controllers.ContainsKey(controller.NonRoamableId))
                return;

            _controllers.Add(controller.NonRoamableId, controller);
            descriptor = CreateDescriptor(controller);
        }

        if (raiseEvent && descriptor is not null)
            DeviceAdded?.Invoke(this, new ControllerDeviceEventArgs(descriptor));
    }

    private static ControllerDeviceDescriptor CreateDescriptor(RawGameController controller)
    {
        return new ControllerDeviceDescriptor(
            controller.NonRoamableId,
            string.IsNullOrWhiteSpace(controller.DisplayName) ? "Game Controller" : controller.DisplayName,
            GetFamily(controller.HardwareVendorId),
            controller.HardwareVendorId,
            controller.HardwareProductId,
            controller.IsWireless,
            Gamepad.FromGameController(controller) is not null,
            controller.ButtonCount,
            controller.AxisCount,
            controller.SwitchCount);
    }

    private static ControllerFamily GetFamily(ushort vendorId) => vendorId switch
    {
        0x045E => ControllerFamily.Xbox,
        0x054C => ControllerFamily.PlayStation,
        0x057E => ControllerFamily.Nintendo,
        0 => ControllerFamily.Unknown,
        _ => ControllerFamily.Generic
    };

    private static IReadOnlyDictionary<StandardControl, double> ReadStandardGamepad(RawGameController controller)
    {
        var gamepad = Gamepad.FromGameController(controller);
        if (gamepad is null)
            return new Dictionary<StandardControl, double>();

        var reading = gamepad.GetCurrentReading();
        var buttons = reading.Buttons;

        return new Dictionary<StandardControl, double>
        {
            [StandardControl.South] = Has(buttons, GamepadButtons.A),
            [StandardControl.East] = Has(buttons, GamepadButtons.B),
            [StandardControl.West] = Has(buttons, GamepadButtons.X),
            [StandardControl.North] = Has(buttons, GamepadButtons.Y),
            [StandardControl.DPadUp] = Has(buttons, GamepadButtons.DPadUp),
            [StandardControl.DPadDown] = Has(buttons, GamepadButtons.DPadDown),
            [StandardControl.DPadLeft] = Has(buttons, GamepadButtons.DPadLeft),
            [StandardControl.DPadRight] = Has(buttons, GamepadButtons.DPadRight),
            [StandardControl.LeftShoulder] = Has(buttons, GamepadButtons.LeftShoulder),
            [StandardControl.RightShoulder] = Has(buttons, GamepadButtons.RightShoulder),
            [StandardControl.LeftStickButton] = Has(buttons, GamepadButtons.LeftThumbstick),
            [StandardControl.RightStickButton] = Has(buttons, GamepadButtons.RightThumbstick),
            [StandardControl.Menu] = Has(buttons, GamepadButtons.Menu),
            [StandardControl.View] = Has(buttons, GamepadButtons.View),
            [StandardControl.Paddle1] = Has(buttons, GamepadButtons.Paddle1),
            [StandardControl.Paddle2] = Has(buttons, GamepadButtons.Paddle2),
            [StandardControl.Paddle3] = Has(buttons, GamepadButtons.Paddle3),
            [StandardControl.Paddle4] = Has(buttons, GamepadButtons.Paddle4),
            [StandardControl.LeftTrigger] = reading.LeftTrigger,
            [StandardControl.RightTrigger] = reading.RightTrigger,
            [StandardControl.LeftStickX] = reading.LeftThumbstickX,
            [StandardControl.LeftStickY] = reading.LeftThumbstickY,
            [StandardControl.RightStickX] = reading.RightThumbstickX,
            [StandardControl.RightStickY] = reading.RightThumbstickY
        };
    }

    private static double Has(GamepadButtons value, GamepadButtons flag) =>
        (value & flag) == flag ? 1d : 0d;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        RawGameController.RawGameControllerAdded -= OnControllerAdded;
        RawGameController.RawGameControllerRemoved -= OnControllerRemoved;

        lock (_gate)
            _controllers.Clear();
    }
}
