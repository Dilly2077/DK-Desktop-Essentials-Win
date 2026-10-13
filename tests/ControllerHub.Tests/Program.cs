using DKDesktopEssentials.Modules.Controllers.ControllerHub;

if (args.Contains("--hardware-inspect")) return await HardwareInspection.RunAsync(args);
var tests = MappingCases.All.Concat(ProfileCases.All).Concat(BridgeCases.All).ToList();
if (!args.Contains("--managed-only")) tests.AddRange(HelperCases.All(args));
else Console.WriteLine("Packaged helper tests explicitly excluded by --managed-only.");
return await TestHarness.RunAsync("Controller Hub completion", tests, args);
