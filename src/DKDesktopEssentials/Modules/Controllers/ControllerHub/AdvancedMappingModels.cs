namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public enum ControllerPressBehavior
{
    Immediate,
    Tap,
    Hold,
    DoublePress
}

public sealed class ControllerValueZone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 1d;
    public ControllerAction Target { get; set; } = new();
    public double OutputValue { get; set; } = 1d;
    public bool UseSourceValue { get; set; }
}

public sealed class ControllerStickMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ControllerControlRef SourceX { get; set; } = ControllerControlRef.ForStandard(StandardControl.LeftStickX);
    public ControllerControlRef SourceY { get; set; } = ControllerControlRef.ForStandard(StandardControl.LeftStickY);
    public StandardControl TargetX { get; set; } = StandardControl.RightStickX;
    public StandardControl TargetY { get; set; } = StandardControl.RightStickY;
    public double Deadzone { get; set; }
    public double OuterDeadzone { get; set; }
    public double CurveExponent { get; set; } = 1d;
    public double Scale { get; set; } = 1d;
    public double RotationDegrees { get; set; }
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    public bool ClampToCircle { get; set; } = true;
    public bool SuppressOriginal { get; set; } = true;
}

public sealed class ControllerRadialMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ControllerControlRef SourceX { get; set; } = ControllerControlRef.ForStandard(StandardControl.LeftStickX);
    public ControllerControlRef SourceY { get; set; } = ControllerControlRef.ForStandard(StandardControl.LeftStickY);
    public double Deadzone { get; set; } = 0.35d;
    public bool SuppressOriginal { get; set; }
    public List<ControllerRadialSector> Sectors { get; set; } = new();
}

public sealed class ControllerRadialSector
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Sector";
    public double StartDegrees { get; set; }
    public double EndDegrees { get; set; } = 45d;
    public ControllerAction Target { get; set; } = new();
    public double OutputValue { get; set; } = 1d;
}

public enum MappingConflictSeverity
{
    Info,
    Warning,
    Error
}

public sealed record ControllerMappingConflict(
    MappingConflictSeverity Severity,
    string Code,
    string Message,
    string? LayerId = null,
    string? MappingId = null);
