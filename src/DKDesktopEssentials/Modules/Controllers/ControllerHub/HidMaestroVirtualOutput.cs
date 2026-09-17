using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed record ControllerOutputBackendStatus(
    string Backend,
    string Version,
    bool HelperPresent,
    bool ProviderPackagePresent,
    bool ReadyForLaunch,
    bool RequiresElevation,
    string Source,
    string PackageSha256,
    string? Message = null);

public sealed class HidMaestroPackageManager
{
    public const string BackendName = "HIDMaestro";
    public const string BackendVersion = "1.8.0";
    public const string ReleaseUrl = "https://github.com/hifihedgehog/HIDMaestro/releases/download/v1.8.0/HIDMaestro-v1.8.0.zip";
    public const string ReleasePage = "https://github.com/hifihedgehog/HIDMaestro/releases/tag/v1.8.0";
    public const string ReleaseSha256 = "1e5f5019c20e4be8f922c7aa5a86ee87eb01f7aa851fe38daea14d0ce4fd8240";

    private readonly string _providerDirectory;
    private readonly string _helperPath;

    public HidMaestroPackageManager(string? providerDirectory = null, string? helperPath = null)
    {
        _providerDirectory = providerDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DKDesktopEssentials",
            "Controllers",
            "Providers",
            BackendName,
            BackendVersion);

        _helperPath = helperPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "controller-output-host",
            "DKControllerOutputHost.exe");
    }

    public string ProviderDirectory => _providerDirectory;
    public string SdkPath => Path.Combine(_providerDirectory, "HIDMaestro.Core.dll");
    public string HelperPath => _helperPath;

    public ControllerOutputBackendStatus GetStatus()
    {
        var helperPresent = File.Exists(_helperPath);
        var providerPresent = File.Exists(SdkPath);
        var message = !helperPresent
            ? "The DK controller output helper is not present in this build."
            : !providerPresent
                ? "HIDMaestro is not installed for DK Desktop Essentials."
                : null;

        return new ControllerOutputBackendStatus(
            BackendName,
            BackendVersion,
            helperPresent,
            providerPresent,
            helperPresent && providerPresent,
            RequiresElevation: true,
            ReleasePage,
            ReleaseSha256,
            message);
    }

    public async Task<ControllerOutputBackendStatus> InstallProviderPackageAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_providerDirectory);
        var temporaryZip = Path.Combine(Path.GetTempPath(), $"DKDesktop-HIDMaestro-{Guid.NewGuid():N}.zip");

        try
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(ReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(temporaryZip))
                await source.CopyToAsync(destination, cancellationToken);

            var digest = await ComputeSha256Async(temporaryZip, cancellationToken);
            if (!string.Equals(digest, ReleaseSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"HIDMaestro package hash mismatch. Expected {ReleaseSha256}, received {digest}.");

            using var archive = ZipFile.OpenRead(temporaryZip);
            var sdkEntry = archive.Entries.FirstOrDefault(entry =>
                string.Equals(Path.GetFileName(entry.FullName), "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The verified HIDMaestro archive does not contain HIDMaestro.Core.dll.");

            ExtractEntry(sdkEntry, SdkPath);

            var licenseEntry = archive.Entries.FirstOrDefault(entry =>
                string.Equals(Path.GetFileName(entry.FullName), "LICENSE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(entry.FullName), "LICENSE.txt", StringComparison.OrdinalIgnoreCase));
            if (licenseEntry is not null)
                ExtractEntry(licenseEntry, Path.Combine(_providerDirectory, "LICENSE-HIDMaestro.txt"));

            var sourceManifest = JsonSerializer.Serialize(new
            {
                backend = BackendName,
                version = BackendVersion,
                source = ReleasePage,
                package = ReleaseUrl,
                sha256 = ReleaseSha256,
                installedUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(_providerDirectory, "source.json"), sourceManifest, cancellationToken);

            return GetStatus();
        }
        finally
        {
            try { if (File.Exists(temporaryZip)) File.Delete(temporaryZip); }
            catch (IOException) { }
        }
    }

    public async Task InstallDriverAsync(CancellationToken cancellationToken = default)
    {
        EnsureFilesPresent();
        await RunElevatedHelperAsync("install", cancellationToken);
    }

    public async Task RepairAsync(CancellationToken cancellationToken = default)
    {
        EnsureFilesPresent();
        await RunElevatedHelperAsync("cleanup", cancellationToken);
    }

    public async Task RemoveDriverAsync(CancellationToken cancellationToken = default)
    {
        EnsureFilesPresent();
        await RunElevatedHelperAsync("remove", cancellationToken);
    }

    public Task RemoveProviderPackageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(_providerDirectory))
            Directory.Delete(_providerDirectory, recursive: true);
        return Task.CompletedTask;
    }

    private void EnsureFilesPresent()
    {
        if (!File.Exists(_helperPath))
            throw new FileNotFoundException("The DK controller output helper is missing from this build.", _helperPath);
        if (!File.Exists(SdkPath))
            throw new FileNotFoundException("HIDMaestro.Core.dll is not installed. Install the verified provider package first.", SdkPath);
    }

    private async Task RunElevatedHelperAsync(string mode, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(_helperPath) ?? AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--mode");
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add("--sdk");
        start.ArgumentList.Add(SdkPath);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the elevated controller-output helper.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"HIDMaestro helper exited with code {process.ExitCode} during '{mode}'.");
    }

    private static void ExtractEntry(ZipArchiveEntry entry, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        entry.ExtractToFile(temporary, overwrite: true);
        File.Move(temporary, destination, overwrite: true);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class HidMaestroOutputSink : IControllerOutputSink
{
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    private HidMaestroOutputSink(Process process, StreamWriter writer, string name)
    {
        _process = process;
        _writer = writer;
        Name = name;
    }

    public string Name { get; }
    public bool IsAvailable => !_disposed && !_process.HasExited;

    public static async Task<HidMaestroOutputSink> CreateAsync(
        ControllerOutputMode mode,
        HidMaestroPackageManager packageManager,
        string identityKey,
        CancellationToken cancellationToken = default)
    {
        var status = packageManager.GetStatus();
        if (!status.ReadyForLaunch)
            throw new InvalidOperationException(status.Message ?? "The HIDMaestro backend is not ready.");

        var profileId = mode switch
        {
            ControllerOutputMode.VirtualXbox360 => "xbox-360-wired",
            ControllerOutputMode.VirtualDualShock4 => "dualshock-4-v2",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "A virtual controller output mode is required.")
        };

        var start = new ProcessStartInfo
        {
            FileName = packageManager.HelperPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(packageManager.HelperPath) ?? AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--mode");
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--sdk");
        start.ArgumentList.Add(packageManager.SdkPath);
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add(profileId);
        start.ArgumentList.Add("--identity");
        start.ArgumentList.Add(identityKey);

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the controller-output helper.");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var readyLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(readyLine))
            {
                var error = await process.StandardError.ReadToEndAsync(timeout.Token);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "The controller-output helper exited before becoming ready. Run DK Desktop Essentials as administrator when using virtual output."
                    : error.Trim());
            }

            using var ready = JsonDocument.Parse(readyLine);
            if (!ready.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var error = ready.RootElement.TryGetProperty("error", out var errorElement)
                    ? errorElement.GetString()
                    : "HIDMaestro could not create the virtual controller.";
                throw new InvalidOperationException(error);
            }

            return new HidMaestroOutputSink(process, process.StandardInput, $"HIDMaestro {profileId}");
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(MappedControllerState state, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("The virtual controller output helper is not running.");

        var controls = state.Controls.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal);
        var line = JsonSerializer.Serialize(new { type = "state", controls });

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                await _writer.WriteLineAsync("{\"type\":\"stop\"}");
                await _writer.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
            }
        }
        finally
        {
            _sendLock.Dispose();
            _writer.Dispose();
            _process.Dispose();
        }
    }
}
