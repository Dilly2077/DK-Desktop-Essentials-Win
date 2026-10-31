using DKDesktopEssentials.Modules.Controllers.ControllerHub;

if (args.Contains("--hardware-inspect")) return await HardwareInspection.RunAsync(args);
if (args.Contains("--provider-pin-inspect")) return await ProviderReleaseValidation.Run(args);
var tests = MappingCases.All.Concat(ProfileCases.All).Concat(BridgeCases.All).Concat(ProviderSecurityCases.All).Concat(WindowsJoystickCases.All).ToList();
if (!args.Contains("--managed-only"))
{
    tests.AddRange(HelperCases.All(args));
    tests.AddRange(ProviderSecurityCases.PublishedHelper(args));
}
else Console.WriteLine("Packaged helper tests explicitly excluded by --managed-only.");
return await TestHarness.RunAsync("Controller Hub completion", tests, args);
