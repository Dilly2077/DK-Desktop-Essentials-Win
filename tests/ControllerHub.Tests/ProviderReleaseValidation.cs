using System.Diagnostics;
using System.Text.Json;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;
using DKDesktopEssentials.Security;
using static Support;

internal static class ProviderReleaseValidation
{
    public static Task<int> Run(string[] args) => TestHarness.RunAsync("Official provider integrity (no driver/device operations)", new (string, Func<Task>)[]
    {
        ("Official HIDMaestro root DLL matches the compiled pin and loads offline from memory", () => HidMaestro(args)),
        ("Official SDL root DLL matches the compiled pin and loads through the locked native path", () => Sdl(args))
    }, args);

    private static async Task HidMaestro(string[] args)
    {
        var archivePath = TestHarness.Option(args, "--hid-archive")!;
        var bytes = ProviderIntegrity.ReadArchiveLibrary(await File.ReadAllBytesAsync(archivePath),
            HidMaestroPackageManager.ReleaseSha256, "HIDMaestro.Core.dll", ProviderPins.HidMaestroSdkSha256);
        var directory = Path.Combine(Path.GetTempPath(), "DK-hid-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "HIDMaestro.Core.dll"); await File.WriteAllBytesAsync(path, bytes);
            var start = new ProcessStartInfo(TestHarness.Option(args, "--helper")!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--mode", "probe", "--sdk", path }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            try
            {
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var probe = JsonDocument.Parse(line ?? throw new Exception("No helper probe response"));
                Check(process.ExitCode == 0 && probe.RootElement.GetProperty("ok").GetBoolean(), "Official in-memory SDK probe failed: " + line);
                Check(probe.RootElement.GetProperty("administrator").GetBoolean() == probe.RootElement.GetProperty("protectedStaging").GetBoolean(), "Elevated helper did not validate private staging ownership/permissions");
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        finally { File.Delete(Path.Combine(directory, "HIDMaestro.Core.dll")); Directory.Delete(directory); }
    }
    private static async Task Sdl(string[] args)
    {
        var bytes = ProviderIntegrity.ReadArchiveLibrary(await File.ReadAllBytesAsync(TestHarness.Option(args, "--sdl-archive")!),
            Sdl3PackageManager.ReleaseSha256, "SDL3.dll", ProviderPins.Sdl3LibrarySha256);
        var directory = Path.Combine(Path.GetTempPath(), "DK-sdl-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "SDL3.dll"); await File.WriteAllBytesAsync(path, bytes);
            using var library = VerifiedNativeLibrary.Open(path, "SDL3.dll", ProviderPins.Sdl3LibrarySha256);
            Check(library.Handle != 0, "Official SDL library did not load");
        }
        finally { File.Delete(Path.Combine(directory, "SDL3.dll")); Directory.Delete(directory); }
    }
}
