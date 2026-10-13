namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

// JSON nulls, unknown enums and non-finite values must be rejected before traversal/mapping.
internal static class ControllerProfileShapeValidator
{
    public static List<string> CollectErrors(ControllerProfile profile)
    {
        var errors = new List<string>();
        void Require(object? value, string name) { if (value is null) errors.Add($"'{name}' cannot be null."); }
        void Defined<T>(T value, string name) where T : struct, Enum
        { if (!Enum.IsDefined(value)) errors.Add($"'{name}' has an unsupported value."); }
        void Finite(string name, params double[] values)
        { if (values.Any(v => !double.IsFinite(v))) errors.Add($"'{name}' must contain finite numbers."); }
        void Control(ControllerControlRef? control)
        {
            Require(control, "control"); if (control is null) return;
            Defined(control.Kind, "control.kind");
            if (control.Standard is { } standard) Defined(standard, "control.standard");
            if (control.Advanced is { } advanced) Defined(advanced, "control.advanced");
            if ((control.Kind == ControllerControlKind.Standard && control.Standard is null) ||
                (control.Kind == ControllerControlKind.Advanced && control.Advanced is null) ||
                (control.Kind is ControllerControlKind.RawAxis or ControllerControlKind.RawButton or ControllerControlKind.RawSwitch && control.Index < 0))
                errors.Add("A control reference must identify its input.");
        }
        void Action(ControllerAction? action)
        {
            Require(action, "action"); if (action is null) return;
            Defined(action.Kind, "action.kind");
            if (action.ControllerControl is { } target) Defined(target, "action.controllerControl");
        }
        void Chord(ControllerChord? chord)
        {
            if (chord is null) return;
            Require(chord.Controls, "chord.controls");
            Finite("chord.threshold", chord.Threshold);
            if (chord.Threshold is < 0 or > 1) errors.Add("Chord threshold must be between 0 and 1.");
            if (chord.Controls is not null) foreach (var control in chord.Controls) Control(control);
        }
        Require(profile.DeviceMatch, "deviceMatch"); Require(profile.Tuning, "tuning"); Require(profile.Layers, "layers");
        Defined(profile.OutputMode, "outputMode");
        if (profile.Tuning is not null) foreach (var tuning in profile.Tuning)
        {
            Require(tuning, "tuning entry"); if (tuning is null) continue;
            Control(tuning.Control);
            Finite("tuning", tuning.Deadzone, tuning.AntiDeadzone, tuning.OuterDeadzone, tuning.CurveExponent, tuning.Scale);
        }
        if (profile.Layers is not null) foreach (var layer in profile.Layers)
        {
            Require(layer, "layer"); if (layer is null) continue;
            if (string.IsNullOrWhiteSpace(layer.Id)) errors.Add("Layer IDs cannot be empty.");
            Defined(layer.ActivationMode, "layer.activationMode"); Chord(layer.Activation);
            Require(layer.RequiredLayerIds, "requiredLayerIds"); Require(layer.BlockedLayerIds, "blockedLayerIds");
            Require(layer.Bindings, "bindings"); Require(layer.StickMappings, "stickMappings"); Require(layer.RadialMappings, "radialMappings");
            if (layer.RequiredLayerIds?.Any(string.IsNullOrWhiteSpace) == true || layer.BlockedLayerIds?.Any(string.IsNullOrWhiteSpace) == true)
                errors.Add("Layer dependency IDs cannot be empty.");
            if (layer.Bindings is not null) foreach (var binding in layer.Bindings)
            {
                Require(binding, "binding"); if (binding is null) continue;
                Control(binding.Source); Action(binding.Target); Chord(binding.RequiredChord);
                Defined(binding.Mode, "binding.mode"); Defined(binding.PressBehavior, "binding.pressBehavior");
                Finite("binding", binding.ActivationThreshold, binding.Scale, binding.TurboHz);
                Require(binding.ValueZones, "valueZones");
                if (binding.ValueZones is not null) foreach (var zone in binding.ValueZones)
                {
                    Require(zone, "valueZone"); if (zone is null) continue;
                    Action(zone.Target); Finite("valueZone", zone.Minimum, zone.Maximum, zone.OutputValue);
                }
            }
            if (layer.StickMappings is not null) foreach (var stick in layer.StickMappings)
            {
                Require(stick, "stickMapping"); if (stick is null) continue;
                Control(stick.SourceX); Control(stick.SourceY);
                Defined(stick.TargetX, "stick.targetX"); Defined(stick.TargetY, "stick.targetY");
                Finite("stick", stick.Deadzone, stick.OuterDeadzone, stick.CurveExponent, stick.Scale, stick.RotationDegrees);
            }
            if (layer.RadialMappings is not null) foreach (var radial in layer.RadialMappings)
            {
                Require(radial, "radialMapping"); if (radial is null) continue;
                Control(radial.SourceX); Control(radial.SourceY); Finite("radial", radial.Deadzone);
                Require(radial.Sectors, "sectors");
                if (radial.Sectors is not null) foreach (var sector in radial.Sectors)
                {
                    Require(sector, "sector"); if (sector is null) continue;
                    Action(sector.Target); Finite("sector", sector.StartDegrees, sector.EndDegrees, sector.OutputValue);
                }
            }
        }
        return errors.Distinct(StringComparer.Ordinal).ToList();
    }
}
