namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed class ControllerMappingEngine
{
    private readonly Dictionary<string, bool> _toggleStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _previousPressedStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _layerToggleStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _previousLayerPressedStates = new(StringComparer.Ordinal);

    public void ResetRuntimeState()
    {
        _toggleStates.Clear();
        _previousPressedStates.Clear();
        _layerToggleStates.Clear();
        _previousLayerPressedStates.Clear();
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
        }

        return output;
    }

    private IEnumerable<ControllerLayer> GetActiveLayers(
        ControllerSnapshot snapshot,
        ControllerProfile profile)
    {
        foreach (var layer in profile.Layers.OrderBy(x => x.Priority))
        {
            if (layer.IsBaseLayer || layer.Activation is null || layer.Activation.Controls.Count == 0)
            {
                yield return layer;
                continue;
            }

            var pressed = IsChordActive(snapshot, layer.Activation);

            if (layer.ActivationMode == LayerActivationMode.Held)
            {
                if (pressed)
                    yield return layer;
                continue;
            }

            var key = $"{profile.Id:N}:layer:{layer.Id}";
            var wasPressed = _previousLayerPressedStates.TryGetValue(key, out var previous) && previous;

            if (pressed && !wasPressed)
            {
                var current = _layerToggleStates.TryGetValue(key, out var toggled) && toggled;
                _layerToggleStates[key] = !current;
            }

            _previousLayerPressedStates[key] = pressed;

            if (_layerToggleStates.TryGetValue(key, out var enabled) && enabled)
                yield return layer;
        }
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
        if (binding.Target.Kind == ControllerActionKind.Disabled)
            return;

        if (binding.RequiredChord is not null && !IsChordActive(snapshot, binding.RequiredChord))
            return;

        var value = snapshot.GetValue(binding.Source);
        if (tuningByControl.TryGetValue(binding.Source.StableKey, out var tuning))
            value = ApplyTuning(value, tuning);

        if (binding.Invert)
            value = -value;

        value *= Math.Clamp(binding.Scale, 0d, 4d);

        var sourcePressed = IsPressed(binding.Source, value, binding.ActivationThreshold);
        var runtimeKey = $"{profile.Id:N}:{layer.Id}:{binding.Id}";
        var wasPressed = _previousPressedStates.TryGetValue(runtimeKey, out var previousPressed) && previousPressed;

        if (binding.SuppressOriginal &&
            binding.Source.Kind == ControllerControlKind.Standard &&
            binding.Source.Standard is not null)
        {
            output.Clear(binding.Source.Standard.Value);
        }

        if (binding.Target.Kind == ControllerActionKind.MacroReference)
        {
            // Macro execution is deliberately delegated to the shared Macro Engine module.
            _previousPressedStates[runtimeKey] = sourcePressed;
            return;
        }

        if (binding.Target.ControllerControl is null)
        {
            _previousPressedStates[runtimeKey] = sourcePressed;
            return;
        }

        var target = binding.Target.ControllerControl.Value;
        double mappedValue;

        switch (binding.Mode)
        {
            case ControllerBindingMode.Toggle:
                if (sourcePressed && !wasPressed)
                {
                    var current = _toggleStates.TryGetValue(runtimeKey, out var toggled) && toggled;
                    _toggleStates[runtimeKey] = !current;
                }

                mappedValue = _toggleStates.TryGetValue(runtimeKey, out var enabled) && enabled ? 1d : 0d;
                break;

            case ControllerBindingMode.Turbo:
                if (!sourcePressed)
                {
                    mappedValue = 0d;
                    break;
                }

                var hz = Math.Clamp(binding.TurboHz, 1d, 30d);
                var cycleMilliseconds = 1000d / hz;
                mappedValue = now.ToUnixTimeMilliseconds() % cycleMilliseconds < cycleMilliseconds / 2d ? 1d : 0d;
                break;

            default:
                mappedValue = ConvertForTarget(binding.Source, target, value, sourcePressed);
                break;
        }

        SetStrongestValue(output, target, mappedValue);
        _previousPressedStates[runtimeKey] = sourcePressed;
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
            ControllerControlKind.RawAxis => Math.Abs(value) >= threshold,
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
        var sourceIsAnalog = source.Kind == ControllerControlKind.RawAxis ||
            (source.Kind == ControllerControlKind.Standard &&
             source.Standard is not null &&
             IsAnalog(source.Standard.Value));

        if (targetIsAnalog)
            return sourceIsAnalog ? sourceValue : sourcePressed ? 1d : 0d;

        return sourcePressed ? 1d : 0d;
    }

    private static bool IsAnalog(StandardControl control) => control is
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
}
