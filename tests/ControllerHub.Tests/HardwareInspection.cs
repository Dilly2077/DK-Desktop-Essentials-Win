using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;

internal static class HardwareInspection
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Hardware inspection requires Windows.");
        // Discover/read physical input only. Do not load output/SDL providers, start
        // sessions, issue feedback, install anything or change system device visibility.
        using var input = new WindowsGamingInputControllerProvider();
        // WGI's list is initially empty even with connected devices. Bound the
        // initialization wait instead of recording an immediate false absence.
        var deadline = Environment.TickCount64 + 3000;
        while (input.GetConnectedDevices().Count == 0 && Environment.TickCount64 < deadline)
            await Task.Delay(50);
        var devices = input.GetConnectedDevices().Select(device =>
        {
            var read = input.TryRead(device.DeviceId, out var state);
            return new
            {
                deviceIdHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.DeviceId))),
                device.DisplayName, device.Family, device.VendorId, device.ProductId, device.IsWireless,
                device.SupportsStandardGamepad, device.ButtonCount, device.AxisCount, device.SwitchCount,
                device.InputBackend, device.SupportsVirtualOutput, device.ConnectionType,
                sampleRead = read, standardControlsRead = state?.StandardControls.Count ?? 0,
                rawButtonsRead = state?.RawButtons.Length ?? 0, rawAxesRead = state?.RawAxes.Length ?? 0
            };
        }).ToArray();
        var observationSeconds = int.TryParse(TestHarness.Option(args, "--observe-seconds"), out var seconds) ? Math.Clamp(seconds, 0, 30) : 0;
        var observedChanges = 0; var readableSamples = 0;
        var previous = new Dictionary<string, string>();
        var observationEnd = Environment.TickCount64 + observationSeconds * 1000;
        while (Environment.TickCount64 < observationEnd)
        {
            foreach (var device in input.GetConnectedDevices())
                if (input.TryRead(device.DeviceId, out var sample) && sample is not null)
                {
                    readableSamples++;
                    var signature = JsonSerializer.Serialize(new { sample.RawButtons, sample.RawAxes, sample.RawSwitches, sample.StandardControls });
                    if (previous.TryGetValue(device.DeviceId, out var prior) && signature != prior) observedChanges++;
                    previous[device.DeviceId] = signature;
                }
            await Task.Delay(10);
        }
        var report = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, mode = "physical-input-discovery", capturedUtc = DateTimeOffset.UtcNow,
            windowsVersion = Environment.OSVersion.VersionString,
            physicalHardwareValidated = false, acceptanceStatus = "pending-manual-tests",
            observationSeconds, readableSamples, observedChanges,
            devices, notes = "Discovery and one readable sample are not full hardware, driver, transport, or game acceptance. Follow docs/controller-hub-acceptance.md."
        }, Support.Json);
        var destination = TestHarness.Option(args, "--report");
        if (destination is not null)
        {
            destination = Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, report);
        }
        Console.WriteLine(report);
        return devices.Any(d => d.sampleRead) ? 0 : 2;
    }
}
