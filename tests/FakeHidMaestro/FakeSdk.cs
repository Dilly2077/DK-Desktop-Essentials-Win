using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HIDMaestro;

// Test-only reflection surface. This assembly has no native or driver APIs and must
// never be bundled with the application artifact or used as a production provider.
[Flags]
public enum HMButton : uint
{
    A = 1, B = 2, X = 4, Y = 8, LeftBumper = 16, RightBumper = 32,
    Back = 64, Start = 128, LeftStick = 256, RightStick = 512,
    RightPaddle = 1024, LeftPaddle = 2048, RightPaddle2 = 4096, LeftPaddle2 = 8192
}
public enum HMHat { None, North, South, East, West, NorthEast, NorthWest, SouthEast, SouthWest }
public sealed record HMProfile(string Id);
public sealed class HMGamepadState
{
    public float[]? Axes;
    public HMButton Buttons;
    public HMHat Hat;
}
public static class HMGamepadStateHelpers
{
    public static float[] StandardAxes(HMProfile profile, float lx, float ly, float rx, float ry, float lt, float rt) =>
        new[] { lx, ly, rx, ry, lt, rt };
}
public sealed class HMContext : IDisposable
{
    private HMController? _controller;
    public void LoadDefaultProfiles() { }
    public HMProfile GetProfile(string id) => new(id);
    public HMController CreateController(HMProfile profile, string identityKey) => _controller = new(profile, identityKey);
    public void InstallDriver() => throw new InvalidOperationException("Test SDK cannot install drivers.");
    public static void RemoveAllVirtualControllers(bool preserveInstall) => throw new InvalidOperationException("Test SDK cannot change system devices.");
    public void Dispose() { _controller?.Dispose(); }
}
public sealed class HMController : IDisposable
{
    private readonly string _log;
    private bool _disposed;
    public HMController(HMProfile profile, string identity)
    {
        var directory = Path.GetDirectoryName(typeof(HMController).Assembly.Location)!;
        _log = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".jsonl");
        Write(new { type = "created", profile = profile.Id, identity });
    }
    public void SubmitState(HMGamepadState state)
    {
        // Paddle4 is the explicit failure-injection sentinel for helper tests.
        if (state.Buttons.HasFlag(HMButton.LeftPaddle2)) throw new IOException("Injected test submit failure.");
        Write(new { type = "state", axes = state.Axes, buttons = (uint)state.Buttons, hat = state.Hat.ToString() });
    }
    private void Write(object value) => File.AppendAllText(_log, JsonSerializer.Serialize(value) + Environment.NewLine);
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        Write(new { type = "disposed" });
    }
}
