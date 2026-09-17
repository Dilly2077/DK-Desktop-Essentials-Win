namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

internal static class AdvancedMappingValidator
{
    public static IReadOnlyList<string> CollectErrors(ControllerProfile profile)
    {
        var errors = new List<string>();

        foreach (var layer in profile.Layers)
        {
            foreach (var binding in layer.Bindings)
            {
                if (binding.TapMaxMilliseconds is < 50 or > 2000)
                    errors.Add($"Binding '{binding.Id}' tap maximum must be between 50 and 2000 ms.");

                if (binding.HoldMilliseconds is < 50 or > 10000)
                    errors.Add($"Binding '{binding.Id}' hold threshold must be between 50 and 10000 ms.");

                if (binding.DoublePressWindowMilliseconds is < 100 or > 2000)
                    errors.Add($"Binding '{binding.Id}' double-press window must be between 100 and 2000 ms.");

                var zoneIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var zone in binding.ValueZones)
                {
                    if (string.IsNullOrWhiteSpace(zone.Id) || !zoneIds.Add(zone.Id))
                        errors.Add($"Binding '{binding.Id}' contains an empty or duplicate value-zone ID.");

                    if (zone.Minimum is < 0d or > 1d || zone.Maximum is < 0d or > 1d || zone.Minimum > zone.Maximum)
                        errors.Add($"Binding '{binding.Id}' value zone '{zone.Id}' must use a valid 0-1 minimum/maximum range.");

                    if (zone.OutputValue is < -1d or > 1d)
                        errors.Add($"Binding '{binding.Id}' value zone '{zone.Id}' output must be between -1 and 1.");

                    ValidateAction(zone.Target, $"Binding '{binding.Id}' value zone '{zone.Id}'", errors);
                }
            }

            var mappingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in layer.StickMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.Id) || !mappingIds.Add(mapping.Id))
                    errors.Add($"Layer '{layer.Name}' contains an empty or duplicate stick-mapping ID.");

                if (mapping.Deadzone is < 0d or > 0.95d || mapping.OuterDeadzone is < 0d or > 0.95d)
                    errors.Add($"Stick mapping '{mapping.Id}' deadzones must be between 0 and 0.95.");

                if (mapping.Deadzone + mapping.OuterDeadzone >= 1d)
                    errors.Add($"Stick mapping '{mapping.Id}' inner and outer deadzones must total less than 1.");

                if (mapping.CurveExponent is <= 0d or > 5d)
                    errors.Add($"Stick mapping '{mapping.Id}' curve exponent must be greater than 0 and at most 5.");

                if (mapping.Scale is < 0d or > 4d)
                    errors.Add($"Stick mapping '{mapping.Id}' scale must be between 0 and 4.");

                if (!ControllerMappingEngine.IsAnalog(mapping.TargetX) || !ControllerMappingEngine.IsAnalog(mapping.TargetY))
                    errors.Add($"Stick mapping '{mapping.Id}' targets must both be analog controls.");
            }

            foreach (var radial in layer.RadialMappings)
            {
                if (string.IsNullOrWhiteSpace(radial.Id) || !mappingIds.Add(radial.Id))
                    errors.Add($"Layer '{layer.Name}' contains an empty or duplicate advanced-mapping ID.");

                if (radial.Deadzone is < 0d or > 1d)
                    errors.Add($"Radial mapping '{radial.Id}' deadzone must be between 0 and 1.");

                var sectorIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var sector in radial.Sectors)
                {
                    if (string.IsNullOrWhiteSpace(sector.Id) || !sectorIds.Add(sector.Id))
                        errors.Add($"Radial mapping '{radial.Id}' contains an empty or duplicate sector ID.");

                    if (!double.IsFinite(sector.StartDegrees) || !double.IsFinite(sector.EndDegrees))
                        errors.Add($"Radial sector '{sector.Id}' angles must be finite numbers.");

                    if (sector.OutputValue is < -1d or > 1d)
                        errors.Add($"Radial sector '{sector.Id}' output must be between -1 and 1.");

                    ValidateAction(sector.Target, $"Radial sector '{sector.Id}'", errors);
                }
            }
        }

        foreach (var conflict in ControllerMappingAnalyzer.Analyze(profile).Where(x => x.Severity == MappingConflictSeverity.Error))
            errors.Add(conflict.Message);

        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateAction(ControllerAction action, string owner, List<string> errors)
    {
        if (action.Kind == ControllerActionKind.ControllerControl && action.ControllerControl is null)
            errors.Add($"{owner} has no controller target.");

        if (action.Kind == ControllerActionKind.MacroReference && string.IsNullOrWhiteSpace(action.MacroId))
            errors.Add($"{owner} has no macro reference.");
    }
}
