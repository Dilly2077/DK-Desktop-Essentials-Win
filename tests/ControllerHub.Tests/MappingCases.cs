using DKDesktopEssentials.Modules.Controllers.ControllerHub;
using static Support;

internal static class MappingCases
{
    public static IEnumerable<(string Name, Func<Task> Run)> All => new (string, Func<Task>)[]
    {
        ("Direct remapping suppresses source and preserves unmapped controls", Direct),
        ("Generic raw buttons, signed axes and switches map to standard output", Raw),
        ("Deadzone, response curve, inversion and scale obey golden values", Tuning),
        ("2D stick rotation preserves direction and circle magnitude", Sticks),
        ("Held chords and nested layers activate only with required controls", Layers),
        ("Toggle layers persist until the next rising edge", ToggleLayers),
        ("Turbo duty cycle releases immediately when source releases", Turbo),
        ("Tap resolves after the double-press window and emits one pulse", Tap),
        ("Hold starts at its threshold and releases immediately", Hold),
        ("Double press emits one pulse and suppresses single tap", DoublePress),
        ("Trigger zones and wrapping radial sectors respect boundaries", Zones),
        ("Touch input is centered, oriented and neutral without contact", Touch),
        ("Macro references remain inert until separate executor integration", Macros)
    };
    private static Task Direct()
    {
        var profile = Profile(Binding(StandardControl.South, StandardControl.East)); profile.PassThroughUnmappedControls = true;
        var mapped = Map(profile, Snapshot((StandardControl.South, 1), (StandardControl.LeftShoulder, 1)));
        Near(0, mapped[StandardControl.South], "Source suppression"); Near(1, mapped[StandardControl.East], "Mapped button");
        Near(1, mapped[StandardControl.LeftShoulder], "Unmapped pass-through"); return Task.CompletedTask;
    }
    private static Task Raw()
    {
        var profile = Profile(
            new() { Source = ControllerControlRef.ForRawButton(0), Target = ControllerAction.ToController(StandardControl.South) },
            new() { Source = ControllerControlRef.ForRawAxis(0), ActivationThreshold = 0, Target = ControllerAction.ToController(StandardControl.RightStickX) },
            new() { Source = ControllerControlRef.ForRawSwitch(0), Target = ControllerAction.ToController(StandardControl.DPadUp) });
        var mapped = Map(profile, new("a", 1, null, new[] { true }, new[] { -0.4 }, new[] { 1 }));
        Near(1, mapped[StandardControl.South], "Raw button"); Near(-0.4, mapped[StandardControl.RightStickX], "Raw signed axis");
        Near(1, mapped[StandardControl.DPadUp], "Raw switch"); return Task.CompletedTask;
    }
    private static Task Tuning()
    {
        var binding = Binding(StandardControl.LeftStickX, StandardControl.RightStickX); binding.ActivationThreshold = 0;
        var profile = Profile(binding);
        profile.Tuning.Add(new() { Control = binding.Source, Deadzone = 0.2, CurveExponent = 2, Scale = 2, Invert = true });
        Near(0, Map(profile, Snapshot((StandardControl.LeftStickX, 0.19)))[StandardControl.RightStickX], "Deadzone");
        Near(-0.5, Map(profile, Snapshot((StandardControl.LeftStickX, 0.6)))[StandardControl.RightStickX], "Curve and scale");
        Near(1, Map(profile, Snapshot((StandardControl.LeftStickX, -1)))[StandardControl.RightStickX], "Saturation"); return Task.CompletedTask;
    }
    private static Task Sticks()
    {
        var profile = Profile(); profile.PassThroughUnmappedControls = true;
        profile.Layers[0].StickMappings.Add(new() { RotationDegrees = 90, Scale = 2 });
        var state = Map(profile, Snapshot((StandardControl.LeftStickX, 1), (StandardControl.LeftStickY, 0)));
        Near(0, state[StandardControl.RightStickX], "Rotated X"); Near(1, state[StandardControl.RightStickY], "Circle clamp");
        Near(0, state[StandardControl.LeftStickX], "Stick suppression"); return Task.CompletedTask;
    }
    private static Task Layers()
    {
        var profile = Profile();
        profile.Layers.Add(new() { Id = "shift", Activation = new() { Controls = new() { ControllerControlRef.ForStandard(StandardControl.LeftShoulder), ControllerControlRef.ForStandard(StandardControl.RightShoulder) } } });
        profile.Layers.Add(new() { Id = "nested", RequiredLayerIds = new() { "shift" }, Bindings = new() { Binding(StandardControl.South, StandardControl.North) } });
        Near(0, Map(profile, Snapshot((StandardControl.South, 1), (StandardControl.LeftShoulder, 1)))[StandardControl.North], "Incomplete chord");
        Near(1, Map(profile, Snapshot((StandardControl.South, 1), (StandardControl.LeftShoulder, 1), (StandardControl.RightShoulder, 1)))[StandardControl.North], "Nested shift"); return Task.CompletedTask;
    }
    private static Task ToggleLayers()
    {
        var profile = Profile(); var engine = new ControllerMappingEngine();
        profile.Layers.Add(new() { Id = "toggle", ActivationMode = LayerActivationMode.Toggle,
            Activation = new() { Controls = new() { ControllerControlRef.ForStandard(StandardControl.LeftShoulder) } },
            Bindings = new() { Binding(StandardControl.South, StandardControl.North) } });
        double At(double shoulder) => engine.Map(Snapshot((StandardControl.South, 1), (StandardControl.LeftShoulder, shoulder)), profile, DateTimeOffset.UnixEpoch)[StandardControl.North];
        Near(1, At(1), "Toggle on"); Near(1, At(1), "Held toggle"); Near(1, At(0), "Persisted toggle"); Near(0, At(1), "Toggle off"); return Task.CompletedTask;
    }
    private static Task Turbo()
    {
        var binding = Binding(StandardControl.South, StandardControl.East); binding.Mode = ControllerBindingMode.Turbo; binding.TurboHz = 10;
        var profile = Profile(binding); var engine = new ControllerMappingEngine();
        double At(double source, int ms) => engine.Map(Snapshot((StandardControl.South, source)), profile, DateTimeOffset.UnixEpoch.AddMilliseconds(ms))[StandardControl.East];
        Near(1, At(1, 20), "Turbo on"); Near(0, At(1, 75), "Turbo off"); Near(0, At(0, 120), "Turbo released"); return Task.CompletedTask;
    }
    private static (ControllerMappingEngine Engine, ControllerProfile Profile) Gesture(ControllerPressBehavior behavior)
    {
        var binding = Binding(StandardControl.South, StandardControl.East); binding.PressBehavior = behavior;
        binding.HoldMilliseconds = 350; binding.DoublePressWindowMilliseconds = 280; binding.TapMaxMilliseconds = 220;
        return (new(), Profile(binding));
    }
    private static double At(ControllerMappingEngine engine, ControllerProfile profile, double source, int ms) =>
        engine.Map(Snapshot((StandardControl.South, source)), profile, DateTimeOffset.UnixEpoch.AddMilliseconds(ms))[StandardControl.East];
    private static Task Tap()
    {
        var (engine, profile) = Gesture(ControllerPressBehavior.Tap);
        Near(0, At(engine, profile, 1, 0), "Tap down"); Near(0, At(engine, profile, 0, 100), "Tap pending");
        Near(0, At(engine, profile, 0, 379), "Before window"); Near(1, At(engine, profile, 0, 380), "Tap pulse");
        Near(0, At(engine, profile, 0, 381), "No repeated pulse"); return Task.CompletedTask;
    }
    private static Task Hold()
    {
        var (engine, profile) = Gesture(ControllerPressBehavior.Hold);
        Near(0, At(engine, profile, 1, 0), "Hold begins"); Near(0, At(engine, profile, 1, 349), "Before threshold");
        Near(1, At(engine, profile, 1, 350), "Hold threshold"); Near(0, At(engine, profile, 0, 351), "Hold release"); return Task.CompletedTask;
    }
    private static Task DoublePress()
    {
        var (engine, profile) = Gesture(ControllerPressBehavior.DoublePress);
        Near(0, At(engine, profile, 1, 0), "First down"); Near(0, At(engine, profile, 0, 80), "First release");
        Near(1, At(engine, profile, 1, 200), "Second down pulse"); Near(0, At(engine, profile, 1, 201), "No repeated double pulse");
        var (tap, tapProfile) = Gesture(ControllerPressBehavior.Tap);
        At(tap, tapProfile, 1, 0); At(tap, tapProfile, 0, 80); At(tap, tapProfile, 1, 200); At(tap, tapProfile, 0, 250);
        Near(0, At(tap, tapProfile, 0, 800), "Double press suppressed tap"); return Task.CompletedTask;
    }
    private static Task Zones()
    {
        var binding = Binding(StandardControl.LeftTrigger, StandardControl.East);
        binding.ValueZones.Add(new() { Minimum = 0.2, Maximum = 0.7, Target = ControllerAction.ToController(StandardControl.South) });
        var profile = Profile(binding);
        profile.Layers[0].RadialMappings.Add(new() { Sectors = new() { new() { StartDegrees = 315, EndDegrees = 45, Target = ControllerAction.ToController(StandardControl.North) } } });
        var state = Map(profile, Snapshot((StandardControl.LeftTrigger, 0.2), (StandardControl.LeftStickX, 1)));
        Near(1, state[StandardControl.South], "Zone boundary"); Near(1, state[StandardControl.North], "Wrapping sector");
        Near(0, Map(profile, Snapshot((StandardControl.LeftTrigger, 0.8)))[StandardControl.South], "Outside zone"); return Task.CompletedTask;
    }
    private static Task Touch()
    {
        var profile = Profile(new ControllerBinding { Source = ControllerControlRef.ForAdvanced(ControllerAdvancedControl.Touchpad0X), ActivationThreshold = 0,
            Target = ControllerAction.ToController(StandardControl.RightStickX) });
        ControllerSnapshot TouchSnapshot(double contact) => Snapshot().WithAdvancedState(new(new Dictionary<ControllerAdvancedControl, double>
        { [ControllerAdvancedControl.Touchpad0Contact] = contact, [ControllerAdvancedControl.Touchpad0X] = 0.75, [ControllerAdvancedControl.Touchpad0Y] = 0.25 }));
        Near(0.5, Map(profile, TouchSnapshot(1))[StandardControl.RightStickX], "Centered touch X");
        Near(0.5, TouchSnapshot(1).GetValue(ControllerControlRef.ForAdvanced(ControllerAdvancedControl.Touchpad0Y)), "Touch Y orientation");
        Near(0, Map(profile, TouchSnapshot(0))[StandardControl.RightStickX], "Released touch"); return Task.CompletedTask;
    }
    private static Task Macros()
    {
        var binding = Binding(StandardControl.South, StandardControl.East);
        binding.Target = ControllerAction.ToMacro("example-macro");
        var state = Map(Profile(binding), Snapshot((StandardControl.South, 1)));
        Check(state.Controls.Values.All(value => value == 0), "Macro reference emitted unexpected controller output");
        return Task.CompletedTask;
    }
}
