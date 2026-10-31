using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using DKDesktopEssentials.Security;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;
using static Support;

internal static class ProviderSecurityCases
{
    public static IEnumerable<(string Name, Func<Task> Run)> All => new (string, Func<Task>)[]
    {
        ("SDK snapshot remains authenticated after its source is replaced", Snapshot),
        ("Library verification rejects mutation, truncation, growth and oversized files", InvalidBytes),
        ("Provider paths reject relative paths, UNC, alternate streams and wrong names", InvalidPaths),
        ("Archive verification refuses tampering, malformed ZIP, wrong version and unexpected entry paths", Archive),
        ("SDL rejects post-install tampering regardless of provider manifest", CorruptSdl),
        ("Native verification blocks file writes, deletion and directory replacement until disposal", NativeLocks),
        ("Native verification refuses a pre-existing writable file handle", ExistingWriter),
        ("Native verification refuses a junction in the library directory chain", Junction)
    };

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DK-security-tests-" + Guid.NewGuid().ToString("N"));
        public string DirectoryPath => Path.Combine(Root, "provider");
        public string Sdk => Path.Combine(DirectoryPath, "HIDMaestro.Core.dll");
        public string Sdl => Path.Combine(DirectoryPath, "SDL3.dll");
        public Files() => Directory.CreateDirectory(DirectoryPath);
        public void Dispose()
        {
            var temp = Path.GetFullPath(Path.GetTempPath());
            Check(Path.GetFullPath(Root).StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(Root).StartsWith("DK-security-tests-", StringComparison.Ordinal), "Unexpected test cleanup path");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("verified code snapshot");
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Task Snapshot()
    {
        using var files = new Files();
        File.WriteAllBytes(files.Sdk, Original);
        var snapshot = ProviderIntegrity.ReadVerified(files.Sdk, "HIDMaestro.Core.dll", Digest(Original));
        File.WriteAllText(files.Sdk, "attacker replacement after verification");
        Check(snapshot.SequenceEqual(Original), "Verified bytes changed after path replacement");
        Denied(() => ProviderIntegrity.ReadVerified(files.Sdk, "HIDMaestro.Core.dll", Digest(Original)));
        return Task.CompletedTask;
    }
    private static Task InvalidBytes()
    {
        foreach (var bytes in new[] { Array.Empty<byte>(), Original[..^1], Original.Concat(new byte[] { 1 }).ToArray(), Encoding.UTF8.GetBytes("different version") })
        {
            using var stream = new MemoryStream(bytes);
            Denied(() => ProviderIntegrity.ReadVerified(stream, Digest(Original)));
        }
        using var files = new Files();
        using (var stream = File.Create(files.Sdk)) stream.SetLength(ProviderIntegrity.MaximumLibraryBytes + 1L);
        Denied(() => ProviderIntegrity.ReadVerified(files.Sdk, "HIDMaestro.Core.dll", Digest(Original)));
        // A forged length is also rejected if a stream contains trailing bytes.
        using var growing = new MemoryStream(Original.Concat(new byte[] { 1 }).ToArray());
        Denied(() => ProviderIntegrity.ReadVerified(growing, Digest(Original), Original.Length));
        return Task.CompletedTask;
    }
    private static Task InvalidPaths()
    {
        foreach (var path in new[] { "HIDMaestro.Core.dll", @"\\server\share\HIDMaestro.Core.dll", @"C:\provider\HIDMaestro.Core.dll:stream", @"C:\provider\other.dll", @"\\?\C:\provider\HIDMaestro.Core.dll" })
            Denied(() => ProviderIntegrity.ValidatePath(path, "HIDMaestro.Core.dll"));
        return Task.CompletedTask;
    }
    private static Task CorruptSdl()
    {
        using var files = new Files();
        File.WriteAllBytes(files.Sdl, Original);
        File.WriteAllText(Path.Combine(files.DirectoryPath, "source.json"), JsonSerializer.Serialize(new { sha256 = Digest(Original), version = "3.4.16" }));
        using var provider = new Sdl3AdvancedControllerProvider(new(files.DirectoryPath));
        Check(!provider.IsAvailable && provider.LastError?.Contains("integrity", StringComparison.OrdinalIgnoreCase) == true, "Tampered SDL was loaded or its failure was not surfaced");
        return Task.CompletedTask;
    }
    private static Task Archive()
    {
        byte[] Package(string name)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = archive.CreateEntry(name).Open()) entry.Write(Original);
            return buffer.ToArray();
        }
        var package = Package("HIDMaestro.Core.dll");
        var actual = ProviderIntegrity.ReadArchiveLibrary(package, Digest(package), "HIDMaestro.Core.dll", Digest(Original));
        Check(actual.SequenceEqual(Original), "Verified root library was not extracted");
        var tampered = package.ToArray(); tampered[^1] ^= 1;
        Denied(() => ProviderIntegrity.ReadArchiveLibrary(tampered, Digest(package), "HIDMaestro.Core.dll", Digest(Original)));
        var wrongVersion = Encoding.UTF8.GetBytes("a different provider version");
        Denied(() => ProviderIntegrity.ReadArchiveLibrary(package, Digest(package), "HIDMaestro.Core.dll", Digest(wrongVersion)));
        var nested = Package("unexpected/HIDMaestro.Core.dll");
        Denied(() => ProviderIntegrity.ReadArchiveLibrary(nested, Digest(nested), "HIDMaestro.Core.dll", Digest(Original)));
        Denied(() => ProviderIntegrity.ReadArchiveLibrary(Original, Digest(Original), "HIDMaestro.Core.dll", Digest(Original)));
        return Task.CompletedTask;
    }
    private static Task NativeLocks()
    {
        using var files = new Files(); File.WriteAllBytes(files.Sdl, Original);
        using (var locked = VerifiedNativeLibrary.LockAndVerify(files.Sdl, "SDL3.dll", Digest(Original)))
        {
            Denied(() => File.WriteAllText(files.Sdl, "replacement"));
            Denied(() => File.Delete(files.Sdl));
            Denied(() => Directory.Move(files.DirectoryPath, files.DirectoryPath + "-replacement"));
        }
        File.WriteAllText(files.Sdl, "write succeeds after unload");
        return Task.CompletedTask;
    }
    private static Task ExistingWriter()
    {
        using var files = new Files(); File.WriteAllBytes(files.Sdl, Original);
        using var writer = new FileStream(files.Sdl, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Denied(() => { using var locked = VerifiedNativeLibrary.LockAndVerify(files.Sdl, "SDL3.dll", Digest(Original)); });
        return Task.CompletedTask;
    }
    private static async Task Junction()
    {
        using var files = new Files(); File.WriteAllBytes(files.Sdl, Original);
        var link = Path.Combine(files.Root, "junction");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, files.DirectoryPath }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(process.ExitCode == 0, "Could not create test junction");
        try { Denied(() => { using var locked = VerifiedNativeLibrary.LockAndVerify(Path.Combine(link, "SDL3.dll"), "SDL3.dll", Digest(Original)); }); }
        finally { Directory.Delete(link); }
    }
    private static void Denied(Action operation)
    {
        try { operation(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Unsafe provider operation was accepted.");
    }

    public static IEnumerable<(string Name, Func<Task> Run)> PublishedHelper(string[] args)
    {
        var helper = TestHarness.Option(args, "--helper") ?? throw new ArgumentException("--helper is required.");
        var sdk = TestHarness.Option(args, "--fake-sdk") ?? throw new ArgumentException("--fake-sdk is required.");
        return new (string, Func<Task>)[]
        {
            ("Production helper rejects the test SDK in every execution mode", () => RejectFake(helper, sdk)),
            ("Production helper refuses caller digests, duplicate and unknown arguments", () => RejectArguments(helper, sdk)),
            ("Production helper refuses DLL path substitution", () => RejectPaths(helper, sdk)),
            ("Production helper requires global acknowledgment for all maintenance modes", () => MaintenanceAcknowledgment(helper, sdk))
        };
    }
    private static async Task<JsonElement> Error(string helper, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(helper))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            process.StandardInput.Close();
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var doc = JsonDocument.Parse(line ?? throw new Exception("Missing structured failure"));
            Check(process.ExitCode != 0 && !doc.RootElement.GetProperty("ok").GetBoolean(), "Production helper accepted unsafe input");
            return doc.RootElement.Clone();
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    private static async Task RejectFake(string helper, string sdk)
    {
        using var files = new Files(); File.Copy(sdk, files.Sdk);
        foreach (var mode in new[] { "probe", "serve", "install", "cleanup", "remove" })
        {
            var error = await Error(helper, "--mode", mode, "--sdk", files.Sdk, "--acknowledge-global", "true");
            var message = error.GetProperty("error").GetString()!;
            Check(message.Contains("integrity", StringComparison.OrdinalIgnoreCase) ||
                (mode is "install" or "cleanup" or "remove" && !error.GetProperty("administrator").GetBoolean() && message.Contains("administrator")), "Unexpected rejection; test SDK may have executed");
        }
        Check(!Directory.EnumerateFiles(files.Root, "*.jsonl", SearchOption.AllDirectories).Any(), "Rejected SDK had side effects");
    }
    private static async Task RejectArguments(string helper, string sdk)
    {
        foreach (var extra in new[] { new[] { "--sha256", Digest(File.ReadAllBytes(sdk)) }, new[] { "--sdk", sdk }, new[] { "--unknown", "true" }, new[] { "--mode", "probe" } })
        {
            var error = await Error(helper, new[] { "--mode", "probe", "--sdk", sdk }.Concat(extra).ToArray());
            Check(error.GetProperty("exception").GetString() == "ArgumentException", "Unexpected argument was used during SDK loading");
        }
    }
    private static async Task RejectPaths(string helper, string sdk)
    {
        using var files = new Files(); var renamed = Path.Combine(files.DirectoryPath, "other.dll"); File.Copy(sdk, renamed);
        foreach (var path in new[] { renamed, "HIDMaestro.Core.dll", @"\\server\share\HIDMaestro.Core.dll", files.Sdk + ":alternate" })
        {
            var error = await Error(helper, "--mode", "probe", "--sdk", path);
            Check(error.GetProperty("exception").GetString() == "InvalidDataException", "Provider path was not validated before opening");
        }
    }
    private static async Task MaintenanceAcknowledgment(string helper, string sdk)
    {
        foreach (var mode in new[] { "install", "cleanup", "remove" })
        {
            var error = await Error(helper, "--mode", mode, "--sdk", sdk);
            Check(error.GetProperty("error").GetString()!.Contains("acknowledgment"), "Maintenance did not require global acknowledgment before SDK loading");
        }
    }
}
