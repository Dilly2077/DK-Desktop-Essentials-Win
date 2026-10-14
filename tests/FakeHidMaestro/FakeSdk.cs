using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using System.Security.AccessControl;

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
        var directory = Environment.GetEnvironmentVariable("DK_TEST_LOG_DIRECTORY")
            ?? throw new InvalidOperationException("Test SDK requires its isolated log directory.");
        _log = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".jsonl");
        AssertElevatedStaging();
        Write(new { type = "created", profile = profile.Id, identity });
    }
    private static void AssertElevatedStaging()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        if (Path.GetDirectoryName(temp) != Environment.GetFolderPath(Environment.SpecialFolder.Windows) ||
            !Path.GetFileName(temp).StartsWith("DKControllerMaintenance-", StringComparison.Ordinal) ||
            Environment.GetEnvironmentVariable("PATH") != Environment.GetFolderPath(Environment.SpecialFolder.System) ||
            Environment.CurrentDirectory != Environment.GetFolderPath(Environment.SpecialFolder.System))
            throw new InvalidOperationException("Elevated SDK inherited an unsafe cache or executable search path.");
        var child = Path.Combine(temp, "test-owner-rights-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(child, "test-only inherited ACL probe");
            var rules = new FileInfo(child).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
            if (!rules.Any(rule => rule.IdentityReference.Value == "S-1-3-4" && rule.IsInherited &&
                rule.FileSystemRights == FileSystemRights.ReadPermissions))
                throw new InvalidOperationException("SDK-created children did not inherit the owner WRITE_DAC restriction.");
        }
        finally { File.Delete(child); }
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
