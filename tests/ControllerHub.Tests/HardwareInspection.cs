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
        var devices = input.GetConnectedDevices().Select(device =>
        {
            var read = input.TryRead(device.DeviceId, out var state);
            return new
            {
                deviceIdHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.DeviceId))),
                device.DisplayName, device.Family, device.VendorId, device.ProductId, device.IsWireless,
                device.SupportsStandardGamepad, device.ButtonCount, device.AxisCount, device.SwitchCount,
                sampleRead = read, standardControlsRead = state?.StandardControls.Count ?? 0
            };
        }).ToArray();
        var report = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, mode = "physical-input-discovery", capturedUtc = DateTimeOffset.UtcNow,
            windowsVersion = Environment.OSVersion.VersionString,
            physicalHardwareValidated = false, acceptanceStatus = "pending-manual-tests",
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
