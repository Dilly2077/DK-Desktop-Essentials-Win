using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

/// <summary>One owner for one physical source, mapper and virtual sink.</summary>
public sealed class ControllerOutputSession : IAsyncDisposable
{
    private readonly ControllerDeviceDescriptor _device;
    private readonly ControllerProfile _profile;
    private readonly IControllerProvider _provider;
    private readonly Func<IControllerAdvancedFeatureProvider> _advanced;
    private readonly Func<CancellationToken, Task<IControllerOutputSink>> _createSink;
    private readonly ControllerMappingEngine _mapper = new();
    private readonly CancellationTokenSource _stop = new();
    private IControllerOutputSink? _sink;
    private Task? _loop;
    private int _disposed;
    private int _reset;
    private volatile string _state = "stopped";
    private volatile string? _error;
    private int _recoveries;

    public ControllerOutputSession(ControllerDeviceDescriptor device, ControllerProfile profile, int updateHz,
        IControllerProvider provider, Func<IControllerAdvancedFeatureProvider> advanced,
        Func<CancellationToken, Task<IControllerOutputSink>> createSink)
    {
        _device = device; _profile = profile; _provider = provider; _advanced = advanced; _createSink = createSink;
        UpdateHz = Math.Clamp(updateHz, 60, 1000);
    }

    public string DeviceId => _device.DeviceId;
    public string? AdvancedDeviceId => _profile.AdvancedDeviceId;
    public int UpdateHz { get; }
    public bool Running => _loop is { IsCompleted: false };
    public string State => _state;
    public object GetStatus() => new { deviceId = DeviceId, profileId = _profile.Id,
        updateHz = UpdateHz, running = Running, state = _state, error = _error, recoveryAttempts = _recoveries };

    public static string Identity(string deviceId, Guid profileId) =>
        $"dk-desktop-essentials:{profileId:N}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))}";

    public void Reset() => Interlocked.Exchange(ref _reset, 1);

    public async Task StartAsync(CancellationToken token = default)
    {
        if (_loop is not null || _disposed != 0) throw new InvalidOperationException("Session has already been started or disposed.");
        _sink = await _createSink(token);
        await SendAsync(new MappedControllerState(), token);
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        var clock = Stopwatch.StartNew();
        var epoch = DateTimeOffset.UtcNow;
        var lastTick = clock.Elapsed;
        var healthySince = clock.Elapsed;
        var needsRelease = true;
        var failures = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / UpdateHz));
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_sink is null)
                    {
                        _sink = await _createSink(token);
                        await SendAsync(new MappedControllerState(), token);
                        healthySince = clock.Elapsed;
                    }
                    var elapsed = clock.Elapsed;
                    if (elapsed - lastTick > TimeSpan.FromMilliseconds(500) || Interlocked.Exchange(ref _reset, 0) != 0)
                    { _mapper.ResetRuntimeState(); needsRelease = true; }
                    lastTick = elapsed;
                    var connected = _provider.GetConnectedDevices().Any(d => d.DeviceId == DeviceId &&
                        d.VendorId == _device.VendorId && d.ProductId == _device.ProductId);
                    if (!connected || !_provider.TryRead(DeviceId, out var snapshot) || snapshot is null || snapshot.DeviceId != DeviceId)
                    {
                        _mapper.ResetRuntimeState(); needsRelease = true; _state = "waitingForReconnect";
                        await SendAsync(new MappedControllerState(), token);
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(_profile.AdvancedDeviceId))
                        {
                            if (!_advanced().TryReadState(_profile.AdvancedDeviceId, out var advanced) || advanced is null)
                            {
                                _mapper.ResetRuntimeState(); needsRelease = true; _state = "waitingForAdvancedDevice";
                                await SendAsync(new MappedControllerState(), token);
                                await timer.WaitForNextTickAsync(token);
                                continue;
                            }
                            snapshot = snapshot.WithAdvancedState(advanced);
                        }
                        // A held control after reconnect/recovery must be released before output resumes.
                        if (needsRelease)
                        {
                            needsRelease = !IsReleased(snapshot);
                            _state = needsRelease ? "waitingForRelease" : "active";
                            await SendAsync(new MappedControllerState(), token);
                        }
                        else
                        {
                            _state = "active";
                            await SendAsync(_mapper.Map(snapshot, _profile, epoch + elapsed), token);
                        }
                    }
                    // Require sustained healthy output before replenishing the recovery budget.
                    if (clock.Elapsed - healthySince > TimeSpan.FromSeconds(10)) failures = 0;
                    if (!await timer.WaitForNextTickAsync(token)) break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _error = ex.Message; _state = "recovering";
                    _mapper.ResetRuntimeState(); needsRelease = true;
                    await CloseSinkAsync();
                    if (++failures > 3) { _state = "faulted"; break; }
                    Interlocked.Increment(ref _recoveries);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << (failures - 1))), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            await CloseSinkAsync();
            _mapper.ResetRuntimeState();
            if (_state != "faulted") _state = "stopped";
        }
    }

    public static bool IsReleased(ControllerSnapshot snapshot) =>
        snapshot.StandardControls.Values.All(v => double.IsFinite(v) && Math.Abs(v) < 0.15) &&
        snapshot.RawButtons.All(v => !v) && snapshot.RawSwitches.All(v => v == 0) &&
        // Raw axes have device-specific ranges; gate mapped standard axes and digital raw inputs only.
        snapshot.AdvancedState.GetValue(ControllerAdvancedControl.Touchpad0Contact) < 0.5 &&
        snapshot.AdvancedState.GetValue(ControllerAdvancedControl.Touchpad1Contact) < 0.5;

    private async Task SendAsync(MappedControllerState state, CancellationToken token)
    {
        if (_sink is null || !_sink.IsAvailable) throw new InvalidOperationException("Output backend is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        await _sink.SendAsync(state, timeout.Token);
    }

    private async Task CloseSinkAsync()
    {
        var sink = _sink; _sink = null;
        if (sink is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await sink.SendAsync(new MappedControllerState(), timeout.Token);
        }
        catch (Exception ex) { _error = ex.Message; }
        finally
        {
            try { await sink.DisposeAsync(); }
            catch (Exception ex) { _error = ex.Message; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (_loop is not null) await _loop;
        else await CloseSinkAsync();
        _stop.Dispose();
    }
}
