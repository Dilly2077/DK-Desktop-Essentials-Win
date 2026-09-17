using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

public sealed class Sdl3PackageManager
{
    public const string BackendName = "SDL3";
    public const string BackendVersion = "3.4.16";
    public const string ReleaseUrl = "https://github.com/libsdl-org/SDL/releases/download/release-3.4.16/SDL3-3.4.16-win32-x64.zip";
    public const string ReleasePage = "https://github.com/libsdl-org/SDL/releases/tag/release-3.4.16";
    public const string ReleaseSha256 = "4217944b4e51457af4a59c82d883f8443b3e65964b2acd8943484c492756c4b6";

    private readonly string _providerDirectory;

    public Sdl3PackageManager(string? providerDirectory = null)
    {
        _providerDirectory = providerDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DKDesktopEssentials",
            "Controllers",
            "Providers",
            BackendName,
            BackendVersion);
    }

    public string ProviderDirectory => _providerDirectory;
    public string LibraryPath => Path.Combine(_providerDirectory, "SDL3.dll");

    public ControllerFeatureBackendStatus GetStatus(bool loaded = false, string? message = null) => new(
        BackendName,
        BackendVersion,
        File.Exists(LibraryPath),
        loaded,
        ReleasePage,
        ReleaseSha256,
        message ?? (File.Exists(LibraryPath) ? null : "SDL3 advanced controller support is not installed."));

    public async Task<ControllerFeatureBackendStatus> InstallAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_providerDirectory);
        var temporaryZip = Path.Combine(Path.GetTempPath(), $"DKDesktop-SDL3-{Guid.NewGuid():N}.zip");

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
                throw new InvalidDataException($"SDL3 package hash mismatch. Expected {ReleaseSha256}, received {digest}.");

            using var archive = ZipFile.OpenRead(temporaryZip);
            var dllEntry = archive.Entries.FirstOrDefault(entry =>
                string.Equals(Path.GetFileName(entry.FullName), "SDL3.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The verified SDL3 archive does not contain SDL3.dll.");

            ExtractEntry(dllEntry, LibraryPath);

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

    public Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(_providerDirectory))
            Directory.Delete(_providerDirectory, recursive: true);
        return Task.CompletedTask;
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

public sealed class Sdl3AdvancedControllerProvider : IControllerAdvancedFeatureProvider
{
    private const uint SdlInitGamepad = 0x00002000u;
    private const int SdlSensorAccel = 1;
    private const int SdlSensorGyro = 2;
    private const string PropRgbLed = "SDL.joystick.cap.rgb_led";
    private const string PropPlayerLed = "SDL.joystick.cap.player_led";
    private const string PropRumble = "SDL.joystick.cap.rumble";
    private const string PropTriggerRumble = "SDL.joystick.cap.trigger_rumble";
    private const ushort SonyVendorId = 0x054C;
    private const ushort DualSenseProductId = 0x0CE6;
    private const ushort DualSenseEdgeProductId = 0x0DF2;

    private readonly object _gate = new();
    private readonly Sdl3PackageManager _packageManager;
    private readonly Dictionary<string, OpenGamepad> _devices = new(StringComparer.Ordinal);
    private nint _library;
    private SdlApi? _api;
    private bool _initialized;
    private bool _disposed;

    public Sdl3AdvancedControllerProvider(Sdl3PackageManager? packageManager = null)
    {
        _packageManager = packageManager ?? new Sdl3PackageManager();
    }

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
                return EnsureInitialized();
        }
    }

    public string? LastError { get; private set; }

    public ControllerFeatureBackendStatus GetStatus()
    {
        lock (_gate)
        {
            var loaded = _initialized && _api is not null;
            return _packageManager.GetStatus(loaded, LastError);
        }
    }

    public IReadOnlyList<ControllerAdvancedDeviceDescriptor> GetDevices()
    {
        lock (_gate)
        {
            if (!EnsureInitialized())
                return Array.Empty<ControllerAdvancedDeviceDescriptor>();

            RefreshDevices();
            return _devices.Values
                .Select(device => device.Descriptor)
                .OrderBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.BackendId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool TryReadState(string backendId, out ControllerAdvancedState? state)
    {
        state = null;
        lock (_gate)
        {
            if (!TryGetDevice(backendId, out var device))
                return false;

            _api!.UpdateGamepads();
            var values = new Dictionary<ControllerAdvancedControl, double>();
            var touches = new List<ControllerTouchPoint>();

            if (device.Descriptor.Capabilities.HasFlag(ControllerFeatureCapability.Gyroscope))
            {
                var sensor = new float[3];
                if (_api.GetSensorData(device.Handle, SdlSensorGyro, sensor, sensor.Length))
                {
                    values[ControllerAdvancedControl.GyroPitch] = sensor[0];
                    values[ControllerAdvancedControl.GyroYaw] = sensor[1];
                    values[ControllerAdvancedControl.GyroRoll] = sensor[2];
                }
            }

            if (device.Descriptor.Capabilities.HasFlag(ControllerFeatureCapability.Accelerometer))
            {
                var sensor = new float[3];
                if (_api.GetSensorData(device.Handle, SdlSensorAccel, sensor, sensor.Length))
                {
                    values[ControllerAdvancedControl.AccelX] = sensor[0];
                    values[ControllerAdvancedControl.AccelY] = sensor[1];
                    values[ControllerAdvancedControl.AccelZ] = sensor[2];
                }
            }

            if (device.Descriptor.Capabilities.HasFlag(ControllerFeatureCapability.Touchpad))
            {
                for (var touchpad = 0; touchpad < device.Descriptor.TouchpadCount; touchpad++)
                {
                    var fingers = Math.Max(0, _api.GetTouchpadFingerCount(device.Handle, touchpad));
                    for (var finger = 0; finger < fingers; finger++)
                    {
                        if (!_api.GetTouchpadFinger(device.Handle, touchpad, finger, out var down, out var x, out var y, out var pressure))
                            continue;

                        touches.Add(new ControllerTouchPoint(touchpad, finger, down, x, y, pressure));
                    }
                }

                var activeSlots = touches.Where(point => point.Down).Take(2).ToArray();
                WriteTouchSlot(values, activeSlots.ElementAtOrDefault(0), 0);
                WriteTouchSlot(values, activeSlots.ElementAtOrDefault(1), 1);
            }

            state = new ControllerAdvancedState(values, touches, DateTimeOffset.UtcNow);
            return true;
        }
    }

    public bool TryRumble(string backendId, double lowFrequency, double highFrequency, TimeSpan duration)
    {
        lock (_gate)
        {
            if (!TryGetDevice(backendId, out var device) ||
                !device.Descriptor.Capabilities.HasFlag(ControllerFeatureCapability.Rumble))
                return false;

            return _api!.RumbleGamepad(
                device.Handle,
                ToUShort(lowFrequency),
                ToUShort(highFrequency),
                ToDurationMilliseconds(duration));
        }
    }

    public bool TryRumbleTriggers(string backendId, double left, double right, TimeSpan duration)
    {
        lock (_gate)
        {
            if (!TryGetDevice(backendId, out var device) ||
                !device.Descriptor.Capabilities.HasFlag(ControllerFeatureCapability.TriggerRumble))
                return false;

            return _api!.RumbleTriggers(
                device.Handle,
                ToUShort(left),
                ToUShort(right),
                ToDurationMilliseconds(duration));
        }
    }

    public bool TrySetLed(string backendId, byte red, byte green, byte blue)
    {
        lock (_gate)
        {
            if (!TryGetDevice(backendId, out var device) ||
                !device.Descriptor.Capabilities.HasFlag(ControllerFeatureCapability.RgbLed))
                return false;

            return _api!.SetLed(device.Handle, red, green, blue);
        }
    }

    public bool TrySetPlayerLed(string backendId, int playerIndex)
    {
        lock (_gate)
        {
            if (playerIndex is < -1 or > 15)
                return false;

            if (!TryGetDevice(backendId, out var device) ||
                !device.Descriptor.Capabilities.HasFlag(ControllerFeatureCapability.PlayerLed))
                return false;

            return _api!.SetPlayerIndex(device.Handle, playerIndex);
        }
    }

    public bool TrySetAdaptiveTriggers(string backendId, ControllerAdaptiveTriggerState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            if (!TryGetDevice(backendId, out var device) ||
                !device.Descriptor.Capabilities.HasFlag(ControllerFeatureCapability.AdaptiveTriggers))
                return false;

            var packet = BuildDualSenseTriggerPacket(state);
            return _api!.SendEffect(device.Handle, packet);
        }
    }

    private bool TryGetDevice(string backendId, out OpenGamepad device)
    {
        device = default!;
        if (string.IsNullOrWhiteSpace(backendId) || !EnsureInitialized())
            return false;

        RefreshDevices();
        return _devices.TryGetValue(backendId, out device!);
    }

    private bool EnsureInitialized()
    {
        if (_disposed)
            return false;
        if (_initialized && _api is not null)
            return true;
        if (!File.Exists(_packageManager.LibraryPath))
        {
            LastError = "SDL3 advanced controller support is not installed.";
            return false;
        }

        try
        {
            _library = NativeLibrary.Load(_packageManager.LibraryPath);
            _api = new SdlApi(_library);
            if (!_api.InitSubSystem(SdlInitGamepad))
                throw new InvalidOperationException(ReadSdlError(_api) ?? "SDL3 could not initialize the gamepad subsystem.");

            _initialized = true;
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            LastError = ex.Message;
            _api = null;
            if (_library != 0)
            {
                NativeLibrary.Free(_library);
                _library = 0;
            }
            return false;
        }
    }

    private void RefreshDevices()
    {
        _api!.UpdateGamepads();
        var idsPointer = _api.GetGamepads(out var count);
        if (idsPointer == 0 || count < 0)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var instanceId = unchecked((uint)Marshal.ReadInt32(idsPointer, index * sizeof(uint)));
                var handle = _api.OpenGamepad(instanceId);
                if (handle == 0)
                    continue;

                var keepHandle = false;
                try
                {
                    var descriptor = CreateDescriptor(handle, instanceId);
                    seen.Add(descriptor.BackendId);

                    if (_devices.TryGetValue(descriptor.BackendId, out var existing))
                    {
                        existing.Descriptor = descriptor;
                    }
                    else
                    {
                        EnableSensors(handle, descriptor.Capabilities);
                        _devices[descriptor.BackendId] = new OpenGamepad(handle, descriptor);
                        keepHandle = true;
                    }
                }
                finally
                {
                    if (!keepHandle)
                        _api.CloseGamepad(handle);
                }
            }
        }
        finally
        {
            _api.Free(idsPointer);
        }

        foreach (var staleKey in _devices.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            _api.CloseGamepad(_devices[staleKey].Handle);
            _devices.Remove(staleKey);
        }
    }

    private ControllerAdvancedDeviceDescriptor CreateDescriptor(nint handle, uint instanceId)
    {
        var name = PtrToUtf8(_api!.GetGamepadName(handle)) ?? "Game Controller";
        var path = PtrToUtf8(_api.GetGamepadPath(handle));
        var serial = PtrToUtf8(_api.GetGamepadSerial(handle));
        var vendor = _api.GetGamepadVendor(handle);
        var product = _api.GetGamepadProduct(handle);
        var touchpadCount = Math.Max(0, _api.GetTouchpadCount(handle));
        var capabilities = ControllerFeatureCapability.None;

        var gyro = _api.HasSensor(handle, SdlSensorGyro);
        var accel = _api.HasSensor(handle, SdlSensorAccel);
        if (gyro) capabilities |= ControllerFeatureCapability.Gyroscope;
        if (accel) capabilities |= ControllerFeatureCapability.Accelerometer;
        if (touchpadCount > 0) capabilities |= ControllerFeatureCapability.Touchpad;

        var properties = _api.GetGamepadProperties(handle);
        if (properties != 0)
        {
            if (_api.GetBooleanProperty(properties, PropRumble, false)) capabilities |= ControllerFeatureCapability.Rumble;
            if (_api.GetBooleanProperty(properties, PropTriggerRumble, false)) capabilities |= ControllerFeatureCapability.TriggerRumble;
            if (_api.GetBooleanProperty(properties, PropRgbLed, false)) capabilities |= ControllerFeatureCapability.RgbLed;
            if (_api.GetBooleanProperty(properties, PropPlayerLed, false)) capabilities |= ControllerFeatureCapability.PlayerLed;
        }

        if (vendor == SonyVendorId && product is DualSenseProductId or DualSenseEdgeProductId)
            capabilities |= ControllerFeatureCapability.AdaptiveTriggers;

        var backendId = BuildBackendId(vendor, product, serial, path, instanceId);
        return new ControllerAdvancedDeviceDescriptor(
            backendId,
            name,
            vendor,
            product,
            serial,
            path,
            capabilities,
            touchpadCount,
            gyro ? _api.GetSensorDataRate(handle, SdlSensorGyro) : 0f,
            accel ? _api.GetSensorDataRate(handle, SdlSensorAccel) : 0f);
    }

    private void EnableSensors(nint handle, ControllerFeatureCapability capabilities)
    {
        if (capabilities.HasFlag(ControllerFeatureCapability.Gyroscope))
            _api!.SetSensorEnabled(handle, SdlSensorGyro, true);
        if (capabilities.HasFlag(ControllerFeatureCapability.Accelerometer))
            _api!.SetSensorEnabled(handle, SdlSensorAccel, true);
    }

    private static string BuildBackendId(ushort vendor, ushort product, string? serial, string? path, uint instanceId)
    {
        var identity = !string.IsNullOrWhiteSpace(serial)
            ? $"serial:{serial}"
            : !string.IsNullOrWhiteSpace(path)
                ? $"path:{path}"
                : $"instance:{instanceId}";

        return $"sdl:{vendor:X4}:{product:X4}:{identity}";
    }

    private static void WriteTouchSlot(Dictionary<ControllerAdvancedControl, double> values, ControllerTouchPoint? point, int slot)
    {
        var x = slot == 0 ? ControllerAdvancedControl.Touchpad0X : ControllerAdvancedControl.Touchpad1X;
        var y = slot == 0 ? ControllerAdvancedControl.Touchpad0Y : ControllerAdvancedControl.Touchpad1Y;
        var pressure = slot == 0 ? ControllerAdvancedControl.Touchpad0Pressure : ControllerAdvancedControl.Touchpad1Pressure;
        var contact = slot == 0 ? ControllerAdvancedControl.Touchpad0Contact : ControllerAdvancedControl.Touchpad1Contact;

        values[x] = point?.X ?? 0d;
        values[y] = point?.Y ?? 0d;
        values[pressure] = point?.Pressure ?? 0d;
        values[contact] = point?.Down == true ? 1d : 0d;
    }

    private static byte[] BuildDualSenseTriggerPacket(ControllerAdaptiveTriggerState state)
    {
        var packet = new byte[47];
        if (state.Right is not null)
        {
            packet[0] |= 0x04;
            EncodeAdaptiveTrigger(state.Right, packet.AsSpan(10, 11));
        }

        if (state.Left is not null)
        {
            packet[0] |= 0x08;
            EncodeAdaptiveTrigger(state.Left, packet.AsSpan(21, 11));
        }

        return packet;
    }

    private static void EncodeAdaptiveTrigger(ControllerAdaptiveTriggerEffect effect, Span<byte> destination)
    {
        destination.Clear();
        switch (effect.Mode)
        {
            case ControllerAdaptiveTriggerMode.Off:
                destination[0] = 0x05;
                break;

            case ControllerAdaptiveTriggerMode.Resistance:
                destination[0] = 0x01;
                destination[1] = ToByte(effect.StartPosition);
                destination[2] = ToByte(effect.Strength);
                break;

            case ControllerAdaptiveTriggerMode.Vibration:
                destination[0] = 0x06;
                destination[1] = ToByte(effect.StartPosition);
                destination[2] = ToByte(effect.Strength);
                destination[3] = ToByte(effect.Frequency);
                break;
        }
    }

    private static byte ToByte(double value) =>
        (byte)Math.Round(Math.Clamp(value, 0d, 1d) * byte.MaxValue);

    private static ushort ToUShort(double value) =>
        (ushort)Math.Round(Math.Clamp(value, 0d, 1d) * ushort.MaxValue);

    private static uint ToDurationMilliseconds(TimeSpan duration) =>
        (uint)Math.Clamp(Math.Round(Math.Max(0d, duration.TotalMilliseconds)), 0d, uint.MaxValue);

    private static string? PtrToUtf8(nint pointer) =>
        pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);

    private static string? ReadSdlError(SdlApi api)
    {
        var pointer = api.GetError();
        return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_api is not null)
            {
                foreach (var device in _devices.Values)
                    _api.CloseGamepad(device.Handle);
                _devices.Clear();

                if (_initialized)
                    _api.QuitSubSystem(SdlInitGamepad);
            }

            _initialized = false;
            _api = null;
            if (_library != 0)
            {
                NativeLibrary.Free(_library);
                _library = 0;
            }
        }
    }

    private sealed class OpenGamepad
    {
        public OpenGamepad(nint handle, ControllerAdvancedDeviceDescriptor descriptor)
        {
            Handle = handle;
            Descriptor = descriptor;
        }

        public nint Handle { get; }
        public ControllerAdvancedDeviceDescriptor Descriptor { get; set; }
    }

    private sealed class SdlApi
    {
        private readonly InitSubSystemDelegate _initSubSystem;
        private readonly QuitSubSystemDelegate _quitSubSystem;
        private readonly UpdateGamepadsDelegate _updateGamepads;
        private readonly GetGamepadsDelegate _getGamepads;
        private readonly OpenGamepadDelegate _openGamepad;
        private readonly CloseGamepadDelegate _closeGamepad;
        private readonly GetPointerDelegate _getGamepadName;
        private readonly GetPointerDelegate _getGamepadPath;
        private readonly GetPointerDelegate _getGamepadSerial;
        private readonly GetUShortDelegate _getGamepadVendor;
        private readonly GetUShortDelegate _getGamepadProduct;
        private readonly GetIntDelegate _getTouchpadCount;
        private readonly GetIndexedIntDelegate _getTouchpadFingerCount;
        private readonly GetTouchpadFingerDelegate _getTouchpadFinger;
        private readonly HasSensorDelegate _hasSensor;
        private readonly SetSensorEnabledDelegate _setSensorEnabled;
        private readonly GetSensorDataDelegate _getSensorData;
        private readonly GetSensorDataRateDelegate _getSensorDataRate;
        private readonly GetPropertiesDelegate _getProperties;
        private readonly GetBooleanPropertyDelegate _getBooleanProperty;
        private readonly RumbleDelegate _rumble;
        private readonly RumbleDelegate _rumbleTriggers;
        private readonly SetLedDelegate _setLed;
        private readonly SetPlayerIndexDelegate _setPlayerIndex;
        private readonly SendEffectDelegate _sendEffect;
        private readonly FreeDelegate _free;
        private readonly GetErrorDelegate _getError;

        public SdlApi(nint library)
        {
            _initSubSystem = Load<InitSubSystemDelegate>(library, "SDL_InitSubSystem");
            _quitSubSystem = Load<QuitSubSystemDelegate>(library, "SDL_QuitSubSystem");
            _updateGamepads = Load<UpdateGamepadsDelegate>(library, "SDL_UpdateGamepads");
            _getGamepads = Load<GetGamepadsDelegate>(library, "SDL_GetGamepads");
            _openGamepad = Load<OpenGamepadDelegate>(library, "SDL_OpenGamepad");
            _closeGamepad = Load<CloseGamepadDelegate>(library, "SDL_CloseGamepad");
            _getGamepadName = Load<GetPointerDelegate>(library, "SDL_GetGamepadName");
            _getGamepadPath = Load<GetPointerDelegate>(library, "SDL_GetGamepadPath");
            _getGamepadSerial = Load<GetPointerDelegate>(library, "SDL_GetGamepadSerial");
            _getGamepadVendor = Load<GetUShortDelegate>(library, "SDL_GetGamepadVendor");
            _getGamepadProduct = Load<GetUShortDelegate>(library, "SDL_GetGamepadProduct");
            _getTouchpadCount = Load<GetIntDelegate>(library, "SDL_GetNumGamepadTouchpads");
            _getTouchpadFingerCount = Load<GetIndexedIntDelegate>(library, "SDL_GetNumGamepadTouchpadFingers");
            _getTouchpadFinger = Load<GetTouchpadFingerDelegate>(library, "SDL_GetGamepadTouchpadFinger");
            _hasSensor = Load<HasSensorDelegate>(library, "SDL_GamepadHasSensor");
            _setSensorEnabled = Load<SetSensorEnabledDelegate>(library, "SDL_SetGamepadSensorEnabled");
            _getSensorData = Load<GetSensorDataDelegate>(library, "SDL_GetGamepadSensorData");
            _getSensorDataRate = Load<GetSensorDataRateDelegate>(library, "SDL_GetGamepadSensorDataRate");
            _getProperties = Load<GetPropertiesDelegate>(library, "SDL_GetGamepadProperties");
            _getBooleanProperty = Load<GetBooleanPropertyDelegate>(library, "SDL_GetBooleanProperty");
            _rumble = Load<RumbleDelegate>(library, "SDL_RumbleGamepad");
            _rumbleTriggers = Load<RumbleDelegate>(library, "SDL_RumbleGamepadTriggers");
            _setLed = Load<SetLedDelegate>(library, "SDL_SetGamepadLED");
            _setPlayerIndex = Load<SetPlayerIndexDelegate>(library, "SDL_SetGamepadPlayerIndex");
            _sendEffect = Load<SendEffectDelegate>(library, "SDL_SendGamepadEffect");
            _free = Load<FreeDelegate>(library, "SDL_free");
            _getError = Load<GetErrorDelegate>(library, "SDL_GetError");
        }

        public bool InitSubSystem(uint flags) => _initSubSystem(flags);
        public void QuitSubSystem(uint flags) => _quitSubSystem(flags);
        public void UpdateGamepads() => _updateGamepads();
        public nint GetGamepads(out int count) => _getGamepads(out count);
        public nint OpenGamepad(uint id) => _openGamepad(id);
        public void CloseGamepad(nint handle) => _closeGamepad(handle);
        public nint GetGamepadName(nint handle) => _getGamepadName(handle);
        public nint GetGamepadPath(nint handle) => _getGamepadPath(handle);
        public nint GetGamepadSerial(nint handle) => _getGamepadSerial(handle);
        public ushort GetGamepadVendor(nint handle) => _getGamepadVendor(handle);
        public ushort GetGamepadProduct(nint handle) => _getGamepadProduct(handle);
        public int GetTouchpadCount(nint handle) => _getTouchpadCount(handle);
        public int GetTouchpadFingerCount(nint handle, int touchpad) => _getTouchpadFingerCount(handle, touchpad);
        public bool GetTouchpadFinger(nint handle, int touchpad, int finger, out bool down, out float x, out float y, out float pressure) =>
            _getTouchpadFinger(handle, touchpad, finger, out down, out x, out y, out pressure);
        public bool HasSensor(nint handle, int sensor) => _hasSensor(handle, sensor);
        public bool SetSensorEnabled(nint handle, int sensor, bool enabled) => _setSensorEnabled(handle, sensor, enabled);
        public bool GetSensorData(nint handle, int sensor, float[] data, int count) => _getSensorData(handle, sensor, data, count);
        public float GetSensorDataRate(nint handle, int sensor) => _getSensorDataRate(handle, sensor);
        public uint GetGamepadProperties(nint handle) => _getProperties(handle);
        public bool GetBooleanProperty(uint properties, string name, bool fallback) => _getBooleanProperty(properties, name, fallback);
        public bool RumbleGamepad(nint handle, ushort low, ushort high, uint durationMs) => _rumble(handle, low, high, durationMs);
        public bool RumbleTriggers(nint handle, ushort left, ushort right, uint durationMs) => _rumbleTriggers(handle, left, right, durationMs);
        public bool SetLed(nint handle, byte red, byte green, byte blue) => _setLed(handle, red, green, blue);
        public bool SetPlayerIndex(nint handle, int playerIndex) => _setPlayerIndex(handle, playerIndex);
        public nint GetError() => _getError();
        public void Free(nint pointer) => _free(pointer);

        public bool SendEffect(nint handle, byte[] packet)
        {
            var pinned = GCHandle.Alloc(packet, GCHandleType.Pinned);
            try
            {
                return _sendEffect(handle, pinned.AddrOfPinnedObject(), packet.Length);
            }
            finally
            {
                pinned.Free();
            }
        }

        private static T Load<T>(nint library, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(library, name, out var pointer))
                throw new EntryPointNotFoundException($"SDL3 export '{name}' was not found.");
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool InitSubSystemDelegate(uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void QuitSubSystemDelegate(uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void UpdateGamepadsDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint GetGamepadsDelegate(out int count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint OpenGamepadDelegate(uint instanceId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CloseGamepadDelegate(nint gamepad);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint GetPointerDelegate(nint gamepad);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ushort GetUShortDelegate(nint gamepad);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetIntDelegate(nint gamepad);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetIndexedIntDelegate(nint gamepad, int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetTouchpadFingerDelegate(
            nint gamepad,
            int touchpad,
            int finger,
            [MarshalAs(UnmanagedType.I1)] out bool down,
            out float x,
            out float y,
            out float pressure);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool HasSensorDelegate(nint gamepad, int type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetSensorEnabledDelegate(nint gamepad, int type, [MarshalAs(UnmanagedType.I1)] bool enabled);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetSensorDataDelegate(nint gamepad, int type, [Out] float[] data, int numValues);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate float GetSensorDataRateDelegate(nint gamepad, int type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GetPropertiesDelegate(nint gamepad);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetBooleanPropertyDelegate(uint properties, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.I1)] bool fallback);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool RumbleDelegate(nint gamepad, ushort first, ushort second, uint durationMs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetLedDelegate(nint gamepad, byte red, byte green, byte blue);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetPlayerIndexDelegate(nint gamepad, int playerIndex);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SendEffectDelegate(nint gamepad, nint data, int size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeDelegate(nint memory);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint GetErrorDelegate();
    }
}
