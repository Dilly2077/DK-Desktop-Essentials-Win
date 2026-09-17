namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public enum ControllerBindingMode
{
    Direct,
    Toggle,
    Turbo
}

public enum ControllerActionKind
{
    ControllerControl,
    MacroReference,
    Disabled
}

public enum LayerActivationMode
{
    Held,
    Toggle
}

public sealed class ControllerProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default Controller Profile";
    public ControllerDeviceMatch DeviceMatch { get; set; } = new();
    public ControllerOutputMode OutputMode { get; set; } = ControllerOutputMode.None;
    public bool PassThroughUnmappedControls { get; set; } = true;
    public List<ControlTuning> Tuning { get; set; } = new();
    public List<ControllerLayer> Layers { get; set; } = new() { ControllerLayer.CreateBaseLayer() };

    public static ControllerProfile CreateDefault(ControllerDeviceDescriptor? device = null)
    {
        var profile = new ControllerProfile();

        if (device is not null)
        {
            profile.Name = $"{device.DisplayName} - Default";
            profile.DeviceMatch = new ControllerDeviceMatch
            {
                DeviceId = device.DeviceId,
                VendorId = device.VendorId,
                ProductId = device.ProductId
            };
        }

        return profile;
    }
}

public sealed class ControllerDeviceMatch
{
    public string? DeviceId { get; set; }
    public ushort? VendorId { get; set; }
    public ushort? ProductId { get; set; }

    public bool Matches(ControllerDeviceDescriptor device)
    {
        if (!string.IsNullOrWhiteSpace(DeviceId) &&
            !string.Equals(DeviceId, device.DeviceId, StringComparison.Ordinal))
            return false;

        if (VendorId is not null && VendorId.Value != device.VendorId)
            return false;

        if (ProductId is not null && ProductId.Value != device.ProductId)
            return false;

        return true;
    }
}

public sealed class ControllerLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Layer";
    public int Priority { get; set; }
    public bool IsBaseLayer { get; set; }
    public LayerActivationMode ActivationMode { get; set; } = LayerActivationMode.Held;
    public ControllerChord? Activation { get; set; }
    public List<string> RequiredLayerIds { get; set; } = new();
    public List<string> BlockedLayerIds { get; set; } = new();
    public List<ControllerBinding> Bindings { get; set; } = new();
    public List<ControllerStickMapping> StickMappings { get; set; } = new();
    public List<ControllerRadialMapping> RadialMappings { get; set; } = new();

    public static ControllerLayer CreateBaseLayer() => new()
    {
        Id = "base",
        Name = "Base",
        Priority = 0,
        IsBaseLayer = true,
        Activation = null
    };
}

public sealed class ControllerChord
{
    public List<ControllerControlRef> Controls { get; set; } = new();
    public double Threshold { get; set; } = 0.5d;
}

public sealed class ControllerBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ControllerControlRef Source { get; set; } = ControllerControlRef.ForStandard(StandardControl.South);
    public ControllerAction Target { get; set; } = new();
    public ControllerBindingMode Mode { get; set; } = ControllerBindingMode.Direct;
    public ControllerPressBehavior PressBehavior { get; set; } = ControllerPressBehavior.Immediate;
    public bool SuppressOriginal { get; set; } = true;
    public double ActivationThreshold { get; set; } = 0.5d;
    public double Scale { get; set; } = 1d;
    public bool Invert { get; set; }
    public double TurboHz { get; set; } = 10d;
    public int TapMaxMilliseconds { get; set; } = 220;
    public int HoldMilliseconds { get; set; } = 350;
    public int DoublePressWindowMilliseconds { get; set; } = 280;
    public ControllerChord? RequiredChord { get; set; }
    public List<ControllerValueZone> ValueZones { get; set; } = new();
}

public sealed class ControllerAction
{
    public ControllerActionKind Kind { get; set; } = ControllerActionKind.ControllerControl;
    public StandardControl? ControllerControl { get; set; }
    public string? MacroId { get; set; }

    public static ControllerAction ToController(StandardControl control) => new()
    {
        Kind = ControllerActionKind.ControllerControl,
        ControllerControl = control
    };

    public static ControllerAction ToMacro(string macroId) => new()
    {
        Kind = ControllerActionKind.MacroReference,
        MacroId = macroId
    };
}

public sealed class ControlTuning
{
    public ControllerControlRef Control { get; set; } = ControllerControlRef.ForStandard(StandardControl.LeftStickX);
    public double Deadzone { get; set; }
    public double AntiDeadzone { get; set; }
    public double OuterDeadzone { get; set; }
    public double CurveExponent { get; set; } = 1d;
    public double Scale { get; set; } = 1d;
    public bool Invert { get; set; }
}
