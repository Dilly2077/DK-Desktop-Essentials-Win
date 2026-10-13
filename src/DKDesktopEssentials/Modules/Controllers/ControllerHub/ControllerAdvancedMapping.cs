namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public enum ControllerStickDestination { Left, Right }
public enum ControllerGyroAxis { X, Y, Z }

public sealed class ControllerGyroStickMapping
{
    public bool Enabled { get; set; }
    public ControllerStickDestination Destination { get; set; } = ControllerStickDestination.Right;
    public ControllerGyroAxis HorizontalAxis { get; set; } = ControllerGyroAxis.Z;
    public ControllerGyroAxis VerticalAxis { get; set; } = ControllerGyroAxis.X;
    // The backend contract requires angular velocity in degrees/second.
    public double DegreesPerSecondForFullStick { get; set; } = 180d;
    public double DeadzoneDegreesPerSecond { get; set; } = 2d;
    public bool InvertHorizontal { get; set; }
    public bool InvertVertical { get; set; }
}

public sealed class ControllerTouchStickMapping
{
    public bool Enabled { get; set; }
    public ControllerStickDestination Destination { get; set; } = ControllerStickDestination.Right;
    // Contact positions must be normalized to [0..1]; centre is [0.5,0.5].
    public double Deadzone { get; set; } = 0.08d;
    public double Sensitivity { get; set; } = 1d;
    public bool InvertHorizontal { get; set; }
    public bool InvertVertical { get; set; }
    public bool MapPhysicalTouchpadClick { get; set; }
    public StandardControl ClickTarget { get; set; } = StandardControl.View;
}

public sealed class ControllerAdvancedMappingSettings
{
    public ControllerGyroStickMapping Gyro { get; set; } = new();
    public ControllerTouchStickMapping Touch { get; set; } = new();
}

public static class ControllerAdvancedMapping
{
    public static IReadOnlyList<string> Validate(ControllerAdvancedMappingSettings? settings)
    {
        if (settings is null) return Array.Empty<string>();
        var errors = new List<string>();
        var gyro = settings.Gyro;
        var touch = settings.Touch;
        if (gyro is null || touch is null)
            return new[] { "Advanced mapping must contain both gyro and touch settings." };
        if (gyro.Enabled)
        {
            if (!Enum.IsDefined(gyro.HorizontalAxis) || !Enum.IsDefined(gyro.VerticalAxis) ||
                !Enum.IsDefined(gyro.Destination))
                errors.Add("Gyro mapping contains an unknown axis or destination.");
            if (gyro.HorizontalAxis == gyro.VerticalAxis)
                errors.Add("Gyro horizontal and vertical axes must differ.");
            if (!double.IsFinite(gyro.DegreesPerSecondForFullStick) ||
                gyro.DegreesPerSecondForFullStick is < 1d or > 2000d)
                errors.Add("Gyro full-stick speed must be between 1 and 2000 degrees/second.");
            if (!double.IsFinite(gyro.DeadzoneDegreesPerSecond) ||
                gyro.DeadzoneDegreesPerSecond < 0d ||
                gyro.DeadzoneDegreesPerSecond >= gyro.DegreesPerSecondForFullStick)
                errors.Add("Gyro deadzone must be nonnegative and lower than full-stick speed.");
        }
        if (touch.Enabled)
        {
            if (!Enum.IsDefined(touch.Destination))
                errors.Add("Touch mapping has an unknown stick destination.");
            if (!double.IsFinite(touch.Deadzone) || touch.Deadzone < 0d || touch.Deadzone >= 1d)
                errors.Add("Touch deadzone must be in [0, 1).");
            if (!double.IsFinite(touch.Sensitivity) || touch.Sensitivity <= 0d || touch.Sensitivity > 10d)
                errors.Add("Touch sensitivity must be greater than zero and at most 10.");
            if (touch.MapPhysicalTouchpadClick && !Enum.IsDefined(touch.ClickTarget))
                errors.Add("Touch click mapping requires a known controller target.");
        }
        if (gyro.Enabled && touch.Enabled && gyro.Destination == touch.Destination)
            errors.Add("Gyro and touch cannot both override the same stick in one profile.");
        return errors;
    }

    public static void ApplyMotion(MappedControllerState mapped, ControllerMotionReading motion, ControllerGyroStickMapping config)
    {
        if (!config.Enabled) return;
        var x = Axis(motion, config.HorizontalAxis);
        var y = Axis(motion, config.VerticalAxis);
        x = RateToStick(x, config.DeadzoneDegreesPerSecond, config.DegreesPerSecondForFullStick);
        y = RateToStick(y, config.DeadzoneDegreesPerSecond, config.DegreesPerSecondForFullStick);
        Write(mapped, config.Destination,
            config.InvertHorizontal ? -x : x, config.InvertVertical ? -y : y);
    }

    public static void ApplyTouch(MappedControllerState mapped, ControllerTouchpadReading touch, ControllerTouchStickMapping config)
    {
        if (!config.Enabled) return;
        // No contact = no virtual stick override (existing physical stick remains intact).
        var contact = touch.Contacts.FirstOrDefault(c => c.Active);
        if (contact is not null &&
            double.IsFinite(contact.X) && double.IsFinite(contact.Y) &&
            contact.X is >= 0d and <= 1d && contact.Y is >= 0d and <= 1d)
        {
            var x = contact.X * 2d - 1d;
            var y = contact.Y * 2d - 1d;
            var radius = Math.Sqrt(x * x + y * y);
            if (radius <= config.Deadzone) x = y = 0d;
            else
            {
                var magnitude = Math.Clamp(
                    (radius - config.Deadzone) / (1d - config.Deadzone) * config.Sensitivity, 0d, 1d);
                x = x / radius * magnitude;
                y = y / radius * magnitude;
            }

            Write(mapped, config.Destination,
                config.InvertHorizontal ? -x : x, config.InvertVertical ? -y : y);
        }

        if (config.MapPhysicalTouchpadClick && touch.Pressed)
            mapped[config.ClickTarget] = 1d;
    }

    private static double Axis(ControllerMotionReading m, ControllerGyroAxis axis) => axis switch
    {
        ControllerGyroAxis.X => m.GyroX,
        ControllerGyroAxis.Y => m.GyroY,
        ControllerGyroAxis.Z => m.GyroZ,
        _ => 0d
    };

    private static double RateToStick(double value, double deadzone, double maximum)
    {
        if (!double.IsFinite(value)) return 0d;
        var absolute = Math.Abs(value);
        if (absolute <= deadzone) return 0d;
        return Math.CopySign(Math.Clamp((absolute - deadzone) / (maximum - deadzone), 0d, 1d), value);
    }

    private static void Write(MappedControllerState state, ControllerStickDestination destination, double x, double y)
    {
        var left = destination == ControllerStickDestination.Left;
        state[left ? StandardControl.LeftStickX : StandardControl.RightStickX] = x;
        state[left ? StandardControl.LeftStickY : StandardControl.RightStickY] = y;
    }
}
