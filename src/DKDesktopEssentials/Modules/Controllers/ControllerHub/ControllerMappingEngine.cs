namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed class ControllerMappingEngine
{
    private readonly Dictionary<string, bool> _toggleStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _previousPressedStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _layerToggleStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _previousLayerPressedStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GestureRuntimeState> _gestureStates = new(StringComparer.Ordinal);

    public void ResetRuntimeState()
    {
        _toggleStates.Clear();
        _previousPressedStates.Clear();
        _layerToggleStates.Clear();
        _previousLayerPressedStates.Clear();
        _gestureStates.Clear();
    }

    public MappedControllerState Map(
        ControllerSnapshot snapshot,
        ControllerProfile profile,
        DateTimeOffset now)
    {
        var output = new MappedControllerState();

        if (profile.PassThroughUnmappedControls)
        {
            foreach (var pair in snapshot.StandardControls)
                output[pair.Key] = pair.Value;
        }

        var tuningByControl = profile.Tuning
            .GroupBy(x => x.Control.StableKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

        foreach (var layer in GetActiveLayers(snapshot, profile))
        {
            foreach (var binding in layer.Bindings)
                ApplyBinding(snapshot, output, profile, layer, binding, tuningByControl, now);

            foreach (var stickMapping in layer.StickMappings)
                ApplyStickMapping(snapshot, output, stickMapping);

            foreach (var radialMapping in layer.RadialMappings)
                ApplyRadialMapping(snapshot, output, radialMapping);
        }

        return output;
    }

    private IReadOnlyList<ControllerLayer> GetActiveLayers(
        ControllerSnapshot snapshot,
        ControllerProfile profile)
    {
        var ordered = profile.Layers.OrderBy(x => x.Priority).ToArray();
        var intrinsic = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var layer in ordered)
        {
            var active = layer.IsBaseLayer || layer.Activation is null || layer.Activation.Controls.Count == 0;

            if (!active && layer.Activation is not null)
            {
                var pressed = IsChordActive(snapshot, layer.Activation);

                if (layer.ActivationMode == LayerActivationMode.Held)
                {
                    active = pressed;
                }
                else
                {
                    var key = $"{profile.Id:N}:layer:{layer.Id}";
                    var wasPressed = _previousLayerPressedStates.TryGetValue(key, out var previous) && previous;

                    if (pressed && !wasPressed)
                    {
                        var current = _layerToggleStates.TryGetValue(key, out var toggled) && toggled;
                        _layerToggleStates[key] = !current;
                    }

                    _previousLayerPressedStates[key] = pressed;
                    active = _layerToggleStates.TryGetValue(key, out var enabled) && enabled;
                }
            }

            intrinsic[layer.Id] = active;
        }

        var activeIds = new HashSet<string>(
            ordered.Where(layer => layer.IsBaseLayer && intrinsic[layer.Id]).Select(layer => layer.Id),
            StringComparer.Ordinal);

        var changed = true;
        for (var pass = 0; pass < ordered.Length && changed; pass++)
        {
            changed = false;

            foreach (var layer in ordered)
            {
                if (activeIds.Contains(layer.Id) || !intrinsic[layer.Id])
                    continue;

                if (layer.RequiredLayerIds.Any(required => !activeIds.Contains(required)))
                    continue;

                if (layer.BlockedLayerIds.Any(blocked => activeIds.Contains(blocked)))
                    continue;

                if (activeIds.Add(layer.Id))
                    changed = true;
            }
        }

        return ordered.Where(layer => activeIds.Contains(layer.Id)).ToArray();
    }

    private void ApplyBinding(
        ControllerSnapshot snapshot,
        MappedControllerState output,
        ControllerProfile profile,
        ControllerLayer layer,
        ControllerBinding binding,
        IReadOnlyDictionary<string, ControlTuning> tuningByControl,
        DateTimeOffset now)
    {
        if (binding.Target.Kind == ControllerActionKind.Disabled && binding.ValueZones.Count == 0)
            return;

        if (binding.RequiredChord is not null && !IsChordActive(snapshot, binding.RequiredChord))
            return;

        var value = snapshot.GetValue(binding.Source);
        if (tuningByControl.TryGetValue(binding.Source.StableKey, out var tuning))
            value = ApplyTuning(value, tuning);

        if (binding.Invert)
            value = -value;

        value *= Math.Clamp(binding.Scale, 0d, 4d);

        var selectedTarget = binding.Target;
        var effectiveValue = value;
        var sourcePressed = IsPressed(binding.Source, value, binding.ActivationThreshold);

        if (binding.ValueZones.Count > 0)
        {
            var magnitude = Math.Abs(value);
            var zone = binding.ValueZones.FirstOrDefault(candidate =>
                magnitude >= candidate.Minimum && magnitude <= candidate.Maximum);

            if (zone is null)
                sourcePressed = false;
            else
            {
                selectedTarget = zone.Target;
                sourcePressed = true;
                effectiveValue = zone.UseSourceValue
                    ? value
                    : Math.CopySign(Math.Clamp(Math.Abs(zone.OutputValue), 0d, 1d), value == 0d ? 1d : value);
            }
        }

        var runtimeKey = $"{profile.Id:N}:{layer.Id}:{binding.Id}";
        var pressEvaluation = EvaluatePressBehavior(binding, runtimeKey, sourcePressed, now);
        var bindingActive = pressEvaluation.Active;
        var wasActive = _previousPressedStates.TryGetValue(runtimeKey, out var previousPressed) && previousPressed;

        if (binding.SuppressOriginal &&
            binding.Source.Kind == ControllerControlKind.Standard &&
            binding.Source.Standard is not null)
        {
            output.Clear(binding.Source.Standard.Value);
        }

        if (selectedTarget.Kind == ControllerActionKind.MacroReference || selectedTarget.Kind == ControllerActionKind.Disabled)
        {
            _previousPressedStates[runtimeKey] = bindingActive;
            return;
        }

        if (selectedTarget.ControllerControl is null)
        {
            _previousPressedStates[runtimeKey] = bindingActive;
            return;
        }

        var target = selectedTarget.ControllerControl.Value;
        double mappedValue;

        switch (binding.Mode)
        {
            case ControllerBindingMode.Toggle:
                if (bindingActive && !wasActive)
                {
                    var current = _toggleStates.TryGetValue(runtimeKey, out var toggled) && toggled;
                    _toggleStates[runtimeKey] = !current;
                }

                mappedValue = _toggleStates.TryGetValue(runtimeKey, out var enabled) && enabled ? 1d : 0d;
                break;

            case ControllerBindingMode.Turbo:
                if (!bindingActive)
                {
                    mappedValue = 0d;
                    break;
                }

                var hz = Math.Clamp(binding.TurboHz, 1d, 30d);
                var cycleMilliseconds = 1000d / hz;
                mappedValue = now.ToUnixTimeMilliseconds() % cycleMilliseconds < cycleMilliseconds / 2d ? 1d : 0d;
                break;

            default:
                var valueForMapping = pressEvaluation.IsPulse ? 1d : effectiveValue;
                mappedValue = bindingActive
                    ? ConvertForTarget(binding.Source, target, valueForMapping, true)
                    : 0d;
                break;
        }

        SetStrongestValue(output, target, mappedValue);
        _previousPressedStates[runtimeKey] = bindingActive;
    }

    private PressEvaluation EvaluatePressBehavior(
        ControllerBinding binding,
        string runtimeKey,
        bool pressed,
        DateTimeOffset now)
    {
        if (binding.PressBehavior == ControllerPressBehavior.Immediate)
            return new PressEvaluation(pressed, false);

        if (!_gestureStates.TryGetValue(runtimeKey, out var state))
        {
            state = new GestureRuntimeState();
            _gestureStates[runtimeKey] = state;
        }

        var nowMs = now.ToUnixTimeMilliseconds();
        var rising = pressed && !state.WasPressed;
        var falling = !pressed && state.WasPressed;
        var pulse = false;
        var active = false;

        if (rising)
        {
            state.PressedAtMilliseconds = nowMs;

            if (binding.PressBehavior == ControllerPressBehavior.Tap &&
                state.PendingTapAtMilliseconds is not null &&
                nowMs - state.PendingTapAtMilliseconds.Value <= binding.DoublePressWindowMilliseconds)
            {
                state.PendingTapAtMilliseconds = null;
                state.SuppressNextTap = true;
            }

            if (binding.PressBehavior == ControllerPressBehavior.DoublePress &&
                state.LastReleaseAtMilliseconds is not null &&
                nowMs - state.LastReleaseAtMilliseconds.Value <= binding.DoublePressWindowMilliseconds)
            {
                pulse = true;
                state.LastReleaseAtMilliseconds = null;
            }
        }

        if (binding.PressBehavior == ControllerPressBehavior.Hold && pressed && state.PressedAtMilliseconds is not null)
        {
            active = nowMs - state.PressedAtMilliseconds.Value >= binding.HoldMilliseconds;
        }

        if (falling)
        {
            var duration = state.PressedAtMilliseconds is null
                ? long.MaxValue
                : nowMs - state.PressedAtMilliseconds.Value;

            if (binding.PressBehavior == ControllerPressBehavior.Tap && duration <= binding.TapMaxMilliseconds)
            {
                if (state.SuppressNextTap)
                    state.SuppressNextTap = false;
                else
                    state.PendingTapAtMilliseconds = nowMs;
            }

            if (binding.PressBehavior == ControllerPressBehavior.DoublePress)
            {
                state.LastReleaseAtMilliseconds = duration <= binding.TapMaxMilliseconds
                    ? nowMs
                    : null;
            }

            state.PressedAtMilliseconds = null;
        }

        if (binding.PressBehavior == ControllerPressBehavior.Tap &&
            !pressed &&
            state.PendingTapAtMilliseconds is not null &&
            nowMs - state.PendingTapAtMilliseconds.Value >= binding.DoublePressWindowMilliseconds)
        {
            pulse = true;
            state.PendingTapAtMilliseconds = null;
        }

        if (binding.PressBehavior is ControllerPressBehavior.Tap or ControllerPressBehavior.DoublePress)
            active = pulse;

        state.WasPressed = pressed;
        return new PressEvaluation(active, pulse);
    }

    private static void ApplyStickMapping(
        ControllerSnapshot snapshot,
        MappedControllerState output,
        ControllerStickMapping mapping)
    {
        var x = snapshot.GetValue(mapping.SourceX);
        var y = snapshot.GetValue(mapping.SourceY);
        var magnitude = Math.Sqrt((x * x) + (y * y));
        var deadzone = Math.Clamp(mapping.Deadzone, 0d, 0.95d);
        var outerDeadzone = Math.Clamp(mapping.OuterDeadzone, 0d, 0.95d);

        if (mapping.SuppressOriginal)
        {
            ClearStandardSource(output, mapping.SourceX);
            ClearStandardSource(output, mapping.SourceY);
        }

        if (magnitude <= deadzone || magnitude <= double.Epsilon)
        {
            SetStrongestValue(output, mapping.TargetX, 0d);
            SetStrongestValue(output, mapping.TargetY, 0d);
            return;
        }

        var usableRange = Math.Max(0.001d, 1d - deadzone - outerDeadzone);
        var normalizedMagnitude = Math.Clamp((magnitude - deadzone) / usableRange, 0d, 1d);
        normalizedMagnitude = Math.Pow(normalizedMagnitude, Math.Clamp(mapping.CurveExponent, 0.1d, 5d));
        normalizedMagnitude *= Math.Clamp(mapping.Scale, 0d, 4d);

        var unitX = x / magnitude;
        var unitY = y / magnitude;
        var radians = mapping.RotationDegrees * Math.PI / 180d;
        var rotatedX = (unitX * Math.Cos(radians)) - (unitY * Math.Sin(radians));
        var rotatedY = (unitX * Math.Sin(radians)) + (unitY * Math.Cos(radians));

        var mappedX = rotatedX * normalizedMagnitude * (mapping.InvertX ? -1d : 1d);
        var mappedY = rotatedY * normalizedMagnitude * (mapping.InvertY ? -1d : 1d);

        if (mapping.ClampToCircle)
        {
            var outputMagnitude = Math.Sqrt((mappedX * mappedX) + (mappedY * mappedY));
            if (outputMagnitude > 1d)
            {
                mappedX /= outputMagnitude;
                mappedY /= outputMagnitude;
            }
        }

        SetStrongestValue(output, mapping.TargetX, Math.Clamp(mappedX, -1d, 1d));
        SetStrongestValue(output, mapping.TargetY, Math.Clamp(mappedY, -1d, 1d));
    }

    private static void ApplyRadialMapping(
        ControllerSnapshot snapshot,
        MappedControllerState output,
        ControllerRadialMapping mapping)
    {
        var x = snapshot.GetValue(mapping.SourceX);
        var y = snapshot.GetValue(mapping.SourceY);
        var magnitude = Math.Sqrt((x * x) + (y * y));

        if (magnitude < Math.Clamp(mapping.Deadzone, 0d, 1d))
            return;

        if (mapping.SuppressOriginal)
        {
            ClearStandardSource(output, mapping.SourceX);
            ClearStandardSource(output, mapping.SourceY);
        }

        var angle = NormalizeDegrees(Math.Atan2(y, x) * 180d / Math.PI);
        var sector = mapping.Sectors.FirstOrDefault(candidate => AngleIsInSector(
            angle,
            NormalizeDegrees(candidate.StartDegrees),
            NormalizeDegrees(candidate.EndDegrees)));

        if (sector is null || sector.Target.Kind != ControllerActionKind.ControllerControl || sector.Target.ControllerControl is null)
            return;

        var target = sector.Target.ControllerControl.Value;
        var outputValue = Math.Clamp(sector.OutputValue, -1d, 1d);
        if (!IsAnalog(target))
            outputValue = outputValue > 0d ? 1d : 0d;

        SetStrongestValue(output, target, outputValue);
    }

    private static bool AngleIsInSector(double angle, double start, double end)
    {
        if (Math.Abs(start - end) < 0.0001d)
            return true;

        return start < end
            ? angle >= start && angle < end
            : angle >= start || angle < end;
    }

    private static double NormalizeDegrees(double degrees)
    {
        var normalized = degrees % 360d;
        return normalized < 0d ? normalized + 360d : normalized;
    }

    private static void ClearStandardSource(MappedControllerState output, ControllerControlRef source)
    {
        if (source.Kind == ControllerControlKind.Standard && source.Standard is not null)
            output.Clear(source.Standard.Value);
    }

    private static bool IsChordActive(ControllerSnapshot snapshot, ControllerChord chord)
    {
        if (chord.Controls.Count == 0)
            return false;

        var threshold = Math.Clamp(chord.Threshold, 0d, 1d);
        return chord.Controls.All(control => IsPressed(control, snapshot.GetValue(control), threshold));
    }

    private static bool IsPressed(ControllerControlRef source, double value, double threshold)
    {
        threshold = Math.Clamp(threshold, 0d, 1d);

        return source.Kind switch
        {
            ControllerControlKind.RawSwitch => Math.Abs(value) > double.Epsilon,
            ControllerControlKind.RawAxis or ControllerControlKind.Advanced => Math.Abs(value) >= threshold,
            ControllerControlKind.Standard when source.Standard is not null && IsAnalog(source.Standard.Value) =>
                Math.Abs(value) >= threshold,
            _ => value >= threshold
        };
    }

    private static double ConvertForTarget(
        ControllerControlRef source,
        StandardControl target,
        double sourceValue,
        bool sourcePressed)
    {
        var targetIsAnalog = IsAnalog(target);
        var sourceIsAnalog = source.Kind is ControllerControlKind.RawAxis or ControllerControlKind.Advanced ||
            (source.Kind == ControllerControlKind.Standard &&
             source.Standard is not null &&
             IsAnalog(source.Standard.Value));

        if (targetIsAnalog)
            return sourceIsAnalog ? sourceValue : sourcePressed ? 1d : 0d;

        return sourcePressed ? 1d : 0d;
    }

    internal static bool IsAnalog(StandardControl control) => control is
        StandardControl.LeftTrigger or
        StandardControl.RightTrigger or
        StandardControl.LeftStickX or
        StandardControl.LeftStickY or
        StandardControl.RightStickX or
        StandardControl.RightStickY;

    private static double ApplyTuning(double value, ControlTuning tuning)
    {
        var sign = Math.Sign(value);
        var magnitude = Math.Clamp(Math.Abs(value), 0d, 1d);
        var inner = Math.Clamp(tuning.Deadzone, 0d, 0.95d);
        var outer = Math.Clamp(tuning.OuterDeadzone, 0d, 0.95d);

        if (magnitude <= inner)
            return 0d;

        var usableRange = Math.Max(0.001d, 1d - inner - outer);
        var normalized = Math.Clamp((magnitude - inner) / usableRange, 0d, 1d);
        var exponent = Math.Clamp(tuning.CurveExponent, 0.1d, 5d);
        var curved = Math.Pow(normalized, exponent);
        var antiDeadzone = Math.Clamp(tuning.AntiDeadzone, 0d, 0.95d);
        var adjusted = antiDeadzone + ((1d - antiDeadzone) * curved);
        adjusted *= Math.Clamp(tuning.Scale, 0d, 4d);

        if (tuning.Invert)
            sign *= -1;

        return Math.Clamp(sign * adjusted, -1d, 1d);
    }

    private static void SetStrongestValue(MappedControllerState output, StandardControl target, double value)
    {
        var existing = output[target];
        if (Math.Abs(value) >= Math.Abs(existing))
            output[target] = value;
    }

    private sealed class GestureRuntimeState
    {
        public bool WasPressed { get; set; }
        public long? PressedAtMilliseconds { get; set; }
        public long? LastReleaseAtMilliseconds { get; set; }
        public long? PendingTapAtMilliseconds { get; set; }
        public bool SuppressNextTap { get; set; }
    }

    private readonly record struct PressEvaluation(bool Active, bool IsPulse);
}
