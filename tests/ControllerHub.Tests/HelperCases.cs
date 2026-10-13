using System.Diagnostics;
using System.Text.Json;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;
using static Support;

internal static class HelperCases
{
    public static IEnumerable<(string Name, Func<Task> Run)> All(string[] args)
    {
        var helper = TestHarness.Option(args, "--helper") ?? throw new ArgumentException("Use --helper and --fake-sdk, or explicitly choose --managed-only.");
        var sdk = TestHarness.Option(args, "--fake-sdk") ?? throw new ArgumentException("--fake-sdk is required.");
        return new (string, Func<Task>)[]
        {
            ("Published helper applies Xbox buttons, axes, triggers and diagonal hat", () => Sink(helper, sdk, ControllerOutputMode.VirtualXbox360)),
            ("Published helper selects DualShock4 and neutralizes on shutdown", () => Sink(helper, sdk, ControllerOutputMode.VirtualDualShock4)),
            ("Published helper watchdog neutralizes and exits a stalled parent", () => Watchdog(helper, sdk)),
            ("Published helper rejects malformed state and neutralizes before exit", () => Malformed(helper, sdk)),
            ("Published helper surfaces failed submission and still cleans up", () => FailedSubmit(helper, sdk))
        };
    }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DK-helper-tests-" + Guid.NewGuid().ToString("N"));
        public string SdkPath => Path.Combine(Root, "HIDMaestro.Core.dll");
        public Files(string sdk) { Directory.CreateDirectory(Root); File.Copy(sdk, SdkPath); }
        public JsonElement[] Events() => Directory.EnumerateFiles(Root, "*.jsonl").SelectMany(File.ReadAllLines)
            .Select(line => { using var doc = JsonDocument.Parse(line); return doc.RootElement.Clone(); }).ToArray();
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
    private static void Neutralized(Files files)
    {
        var events = files.Events();
        var last = events.Last(e => e.GetProperty("type").GetString() == "state");
        Check(last.GetProperty("buttons").GetUInt32() == 0 && last.GetProperty("hat").GetString() == "None", "Helper left digital controls held");
        var axes = last.GetProperty("axes").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        Check(axes.Take(4).All(v => v == 0.5) && axes.Skip(4).All(v => v == 0), "Helper left analog controls held");
        Check(events.Last().GetProperty("type").GetString() == "disposed", "Virtual context was not disposed");
    }
    private static async Task Sink(string helper, string sdk, ControllerOutputMode mode)
    {
        using var files = new Files(sdk);
        var manager = new HidMaestroPackageManager(files.Root, Path.GetFullPath(helper));
        var sink = await HidMaestroOutputSink.CreateAsync(mode, manager, "integration");
        try
        {
            var state = new MappedControllerState();
            state[StandardControl.South] = 1; state[StandardControl.LeftShoulder] = 1;
            state[StandardControl.DPadUp] = 1; state[StandardControl.DPadRight] = 1;
            state[StandardControl.LeftStickX] = -1; state[StandardControl.RightStickY] = 0.5;
            state[StandardControl.LeftTrigger] = 0.75;
            await sink.SendAsync(state);
        }
        finally { await sink.DisposeAsync(); }
        var events = files.Events();
        Check(events[0].GetProperty("profile").GetString() == (mode == ControllerOutputMode.VirtualXbox360 ? "xbox-360-wired" : "dualshock-4-v2"), "Wrong virtual profile selected");
        var applied = events.First(e => e.GetProperty("type").GetString() == "state");
        Check(applied.GetProperty("buttons").GetUInt32() == 17 && applied.GetProperty("hat").GetString() == "NorthEast", "Button/hat translation mismatch");
        Near(0, applied.GetProperty("axes")[0].GetDouble(), "Signed axis normalization");
        Near(0.75, applied.GetProperty("axes")[3].GetDouble(), "Right-stick Y normalization");
        Near(0.75, applied.GetProperty("axes")[4].GetDouble(), "Trigger translation");
        Neutralized(files);
    }
    private static Process Start(string helper, Files files)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(helper))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--mode", "serve", "--sdk", files.SdkPath, "--profile", "xbox-360-wired", "--identity", "watchdog" }) info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }
    private static async Task Ready(Process process)
    {
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using var ready = JsonDocument.Parse(line ?? throw new Exception("Helper exited before readiness"));
        Check(ready.RootElement.GetProperty("ok").GetBoolean(), "Helper could not start");
    }
    private static async Task Watchdog(string helper, string sdk)
    {
        using var files = new Files(sdk); using var process = Start(helper, files);
        try
        {
            await Ready(process);
            await process.StandardInput.WriteLineAsync("{\"type\":\"state\",\"controls\":{\"South\":1}}"); await process.StandardInput.FlushAsync();
            using var applied = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))!);
            Check(applied.RootElement.GetProperty("type").GetString() == "applied", "Helper did not acknowledge");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(process.ExitCode != 0, "Watchdog did not report the stalled-parent failure"); Neutralized(files);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    private static async Task Malformed(string helper, string sdk)
    {
        using var files = new Files(sdk); using var process = Start(helper, files);
        try
        {
            await Ready(process); await process.StandardInput.WriteLineAsync("not-json"); await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(process.ExitCode != 0, "Malformed state accepted"); Neutralized(files);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    private static async Task FailedSubmit(string helper, string sdk)
    {
        using var files = new Files(sdk);
        await using var sink = await HidMaestroOutputSink.CreateAsync(ControllerOutputMode.VirtualXbox360, new(files.Root, Path.GetFullPath(helper)), "failure");
        var state = new MappedControllerState(); state[StandardControl.Paddle4] = 1;
        var rejected = false;
        try { await sink.SendAsync(state); }
        catch (IOException) { rejected = true; }
        Check(rejected, "Failed submission was acknowledged as success");
        await sink.DisposeAsync(); Neutralized(files);
    }
}
