using System.Reflection;
using System.Security.Principal;
using System.Text.Json;

namespace DKControllerOutputHost;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);
            var mode = options.GetValueOrDefault("mode") ?? "serve";
            var sdkPath = Require(options, "sdk");

            if (!File.Exists(sdkPath))
                throw new FileNotFoundException("HIDMaestro.Core.dll was not found.", sdkPath);

            var sdk = new HidMaestroReflection(sdkPath);

            switch (mode)
            {
                case "probe":
                    Write(new { type = "probe", ok = true, sdkVersion = sdk.Version, administrator = IsAdministrator() });
                    return 0;

                case "install":
                    sdk.InstallDriver();
                    Write(new { type = "install", ok = true, sdkVersion = sdk.Version });
                    return 0;

                case "cleanup":
                    sdk.RemoveAllVirtualControllers(preserveInstall: true);
                    Write(new { type = "cleanup", ok = true });
                    return 0;

                case "remove":
                    sdk.RemoveAllVirtualControllers(preserveInstall: false);
                    Write(new { type = "remove", ok = true });
                    return 0;

                case "serve":
                    return await ServeAsync(sdk, options);

                default:
                    throw new ArgumentException($"Unknown mode '{mode}'.");
            }
        }
        catch (Exception ex)
        {
            Write(new
            {
                type = "error",
                ok = false,
                error = ex.GetBaseException().Message,
                exception = ex.GetBaseException().GetType().Name,
                administrator = IsAdministrator()
            });
            return 1;
        }
    }

    private static async Task<int> ServeAsync(HidMaestroReflection sdk, Dictionary<string, string> options)
    {
        var profileId = Require(options, "profile");
        var identityKey = options.GetValueOrDefault("identity") ?? "dk-desktop-essentials";

        using var session = sdk.CreateSession(profileId, identityKey);
        Write(new
        {
            type = "ready",
            ok = true,
            profileId,
            identityKey,
            sdkVersion = sdk.Version,
            administrator = IsAdministrator()
        });

        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

                if (type == "stop")
                    break;

                if (type != "state")
                    continue;

                var controls = new Dictionary<string, double>(StringComparer.Ordinal);
                if (root.TryGetProperty("controls", out var controlsElement) && controlsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in controlsElement.EnumerateObject())
                    {
                        if (property.Value.TryGetDouble(out var value))
                            controls[property.Name] = value;
                    }
                }

                session.Submit(controls);
            }
            catch (Exception ex)
            {
                Write(new { type = "stateError", ok = false, error = ex.GetBaseException().Message });
            }
        }

        return 0;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = arg[2..];
            var value = "true";
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                value = args[++i];
            result[key] = value;
        }

        return result;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{key} is required.");

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void Write(object value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        Console.Out.Flush();
    }
}

internal sealed class HidMaestroReflection
{
    private readonly Assembly _assembly;
    private readonly Type _contextType;
    private readonly Type _stateType;
    private readonly Type _buttonType;
    private readonly Type _hatType;
    private readonly Type _helpersType;

    public HidMaestroReflection(string sdkPath)
    {
        _assembly = Assembly.LoadFrom(Path.GetFullPath(sdkPath));
        _contextType = RequireType("HIDMaestro.HMContext");
        _stateType = RequireType("HIDMaestro.HMGamepadState");
        _buttonType = RequireType("HIDMaestro.HMButton");
        _hatType = RequireType("HIDMaestro.HMHat");
        _helpersType = RequireType("HIDMaestro.HMGamepadStateHelpers");
    }

    public string Version => _assembly.GetName().Version?.ToString() ?? "unknown";

    public void InstallDriver()
    {
        using var disposable = CreateContext(out var context);
        Invoke(context, "LoadDefaultProfiles");
        Invoke(context, "InstallDriver");
    }

    public void RemoveAllVirtualControllers(bool preserveInstall)
    {
        var method = _contextType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                candidate.Name == "RemoveAllVirtualControllers" &&
                candidate.GetParameters().Length == 1 &&
                candidate.GetParameters()[0].ParameterType == typeof(bool))
            ?? throw new MissingMethodException("HIDMaestro HMContext.RemoveAllVirtualControllers(bool) was not found.");

        method.Invoke(null, [preserveInstall]);
    }

    public HidMaestroSession CreateSession(string profileId, string identityKey)
    {
        var disposable = CreateContext(out var context);
        try
        {
            Invoke(context, "LoadDefaultProfiles");
            var profile = Invoke(context, "GetProfile", profileId)
                ?? throw new InvalidOperationException($"HIDMaestro profile '{profileId}' was not found.");

            var create = _contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                {
                    if (candidate.Name != "CreateController") return false;
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 2 && parameters[1].ParameterType == typeof(string);
                })
                ?? throw new MissingMethodException("HIDMaestro CreateController(profile, identityKey) was not found.");

            var controller = create.Invoke(context, [profile, identityKey])
                ?? throw new InvalidOperationException("HIDMaestro did not return a virtual controller.");

            return new HidMaestroSession(
                disposable,
                controller,
                profile,
                _stateType,
                _buttonType,
                _hatType,
                _helpersType);
        }
        catch
        {
            disposable.Dispose();
            throw;
        }
    }

    private IDisposable CreateContext(out object context)
    {
        context = Activator.CreateInstance(_contextType)
            ?? throw new InvalidOperationException("Could not create HIDMaestro HMContext.");
        return context as IDisposable
            ?? throw new InvalidOperationException("HIDMaestro HMContext is not disposable.");
    }

    private Type RequireType(string name) =>
        _assembly.GetType(name, throwOnError: true)
        ?? throw new TypeLoadException(name);

    private object? Invoke(object target, string methodName, params object?[] args)
    {
        var candidates = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == methodName && method.GetParameters().Length == args.Length)
            .ToArray();

        foreach (var candidate in candidates)
        {
            try { return candidate.Invoke(target, args); }
            catch (ArgumentException) { }
        }

        throw new MissingMethodException(target.GetType().FullName, methodName);
    }
}

internal sealed class HidMaestroSession : IDisposable
{
    private readonly IDisposable _context;
    private readonly object _controller;
    private readonly object _profile;
    private readonly Type _stateType;
    private readonly Type _buttonType;
    private readonly Type _hatType;
    private readonly MethodInfo _standardAxes;
    private readonly MethodInfo _submitState;
    private bool _disposed;

    public HidMaestroSession(
        IDisposable context,
        object controller,
        object profile,
        Type stateType,
        Type buttonType,
        Type hatType,
        Type helpersType)
    {
        _context = context;
        _controller = controller;
        _profile = profile;
        _stateType = stateType;
        _buttonType = buttonType;
        _hatType = hatType;

        _standardAxes = helpersType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "StandardAxes" && method.GetParameters().Length == 7);
        _submitState = controller.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method => method.Name == "SubmitState" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == stateType);
    }

    public void Submit(IReadOnlyDictionary<string, double> controls)
    {
        var lx = Axis(controls, "LeftStickX");
        var ly = Axis(controls, "LeftStickY");
        var rx = Axis(controls, "RightStickX");
        var ry = Axis(controls, "RightStickY");
        var lt = Trigger(controls, "LeftTrigger");
        var rt = Trigger(controls, "RightTrigger");

        var axes = _standardAxes.Invoke(null, [_profile, lx, ly, rx, ry, lt, rt]);
        var state = Activator.CreateInstance(_stateType)
            ?? throw new InvalidOperationException("Could not create HIDMaestro state.");

        _stateType.GetField("Axes")!.SetValue(state, axes);
        _stateType.GetField("Buttons")!.SetValue(state, BuildButtons(controls));
        _stateType.GetField("Hat")!.SetValue(state, BuildHat(controls));
        _submitState.Invoke(_controller, [state]);
    }

    private object BuildButtons(IReadOnlyDictionary<string, double> controls)
    {
        uint mask = 0;
        Add("South", "A");
        Add("East", "B");
        Add("West", "X");
        Add("North", "Y");
        Add("LeftShoulder", "LeftBumper");
        Add("RightShoulder", "RightBumper");
        Add("View", "Back");
        Add("Menu", "Start");
        Add("LeftStickButton", "LeftStick");
        Add("RightStickButton", "RightStick");
        Add("Paddle1", "RightPaddle");
        Add("Paddle2", "LeftPaddle");
        Add("Paddle3", "RightPaddle2");
        Add("Paddle4", "LeftPaddle2");
        return Enum.ToObject(_buttonType, mask);

        void Add(string controlName, string enumName)
        {
            if (!Pressed(controls, controlName)) return;
            var parsed = Enum.Parse(_buttonType, enumName, ignoreCase: false);
            mask |= Convert.ToUInt32(parsed);
        }
    }

    private object BuildHat(IReadOnlyDictionary<string, double> controls)
    {
        var up = Pressed(controls, "DPadUp");
        var down = Pressed(controls, "DPadDown");
        var left = Pressed(controls, "DPadLeft");
        var right = Pressed(controls, "DPadRight");
        var name = (up, down, left, right) switch
        {
            (true, false, true, false) => "NorthWest",
            (true, false, false, true) => "NorthEast",
            (false, true, true, false) => "SouthWest",
            (false, true, false, true) => "SouthEast",
            (true, false, false, false) => "North",
            (false, true, false, false) => "South",
            (false, false, true, false) => "West",
            (false, false, false, true) => "East",
            _ => "None"
        };
        return Enum.Parse(_hatType, name, ignoreCase: false);
    }

    private static bool Pressed(IReadOnlyDictionary<string, double> controls, string name) =>
        controls.TryGetValue(name, out var value) && value >= 0.5d;

    private static float Axis(IReadOnlyDictionary<string, double> controls, string name)
    {
        var value = controls.TryGetValue(name, out var raw) ? raw : 0d;
        return (float)((Math.Clamp(value, -1d, 1d) + 1d) / 2d);
    }

    private static float Trigger(IReadOnlyDictionary<string, double> controls, string name)
    {
        var value = controls.TryGetValue(name, out var raw) ? raw : 0d;
        return (float)Math.Clamp(value, 0d, 1d);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_controller is IDisposable controllerDisposable)
            controllerDisposable.Dispose();
        _context.Dispose();
    }
}
