using System.Runtime.InteropServices;
using DKDesktopEssentials.Modules.Controllers.ControllerHub;
using static Support;

internal static class WindowsJoystickCases
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All => new (string, Func<Task>)[]
    {
        ("Windows joystick fallback reads raw controls and declares identity/output limits", Read),
        ("Windows joystick fallback invalidates unplugged IDs and never reuses them", Disconnect),
        ("Windows joystick normalization handles endpoints, invalid ranges and optional axes", Values),
        ("Windows joystick fallback cannot start a virtual-output session", Output)
    };
    private sealed class Api : WindowsJoystickFallback.IApi
    {
        internal bool Connected = true;
        public bool Read(uint slot, out WindowsJoystickFallback.Caps caps, out WindowsJoystickFallback.Reading reading)
        {
            caps = new() { Name="Test controller", Buttons=32, Flags=17, Xmax=100, Ymax=100, Zmax=100 };
            reading = new() { X=100, Y=50, Z=0, Buttons=0x80000001, Pov=9000 };
            return Connected && slot == 1;
        }
    }
    private static Task Read()
    {
        var provider = new WindowsJoystickFallback(new Api()); var device=provider.GetDevices().Single();
        Check(!device.SupportsVirtualOutput && !device.SupportsStandardGamepad && device.ConnectionType=="unknown", "Fallback overstated capabilities");
        Check(provider.TryRead(device.DeviceId, out var state) && state is not null, "Sample missing");
        Check(state!.RawButtons[0] && state.RawButtons[31] && !state.RawButtons[1], "Button mask translation failed");
        Check(state.RawAxes.SequenceEqual(new double[]{1,0,-1}) && state.RawSwitches[0]==3 && state.StandardControls.Count==0, "Raw axes/hat mapping or standard layout failed");
        return Task.CompletedTask;
    }
    private static Task Disconnect()
    {
        var api=new Api(); var provider=new WindowsJoystickFallback(api); var id=provider.GetDevices().Single().DeviceId;
        Check(provider.GetDevices().Single().DeviceId==id, "Connected identity changed");
        api.Connected=false; Check(provider.GetDevices().Count==0 && !provider.TryRead(id,out _), "Disconnected slot remained readable");
        api.Connected=true; Check(provider.GetDevices().Single().DeviceId!=id && !provider.TryRead(id,out _), "Slot was treated as a physical reconnect identity");
        provider.Reset(); Check(!provider.TryRead(id,out _), "Reset retained stale identity");
        return Task.CompletedTask;
    }
    private static Task Values()
    {
        Check(Marshal.SizeOf<WindowsJoystickFallback.Caps>()==728 && Marshal.SizeOf<WindowsJoystickFallback.Reading>()==52, "Native structure ABI mismatch");
        Near(-1,WindowsJoystickFallback.Normalize(0,0,100),"minimum"); Near(1,WindowsJoystickFallback.Normalize(uint.MaxValue,0,100),"clamp");
        Near(0,WindowsJoystickFallback.Normalize(1,10,10),"invalid range");
        Check(WindowsJoystickFallback.AxisIndices(8).SequenceEqual(new[]{0,1,5}),"Optional axis indices shifted");
        return Task.CompletedTask;
    }
    private static async Task Output()
    {
        await using var fixture = new Fixture(); fixture.Input.Set("a",0);
        // Apply the real fallback capability to a stub device while retaining the
        // service/output integration, so rejected sessions cannot reach a sink.
        var device=fixture.Input.GetConnectedDevices().Single() with { SupportsVirtualOutput=false };
        using var provider=new InspectionProvider(device);
        await using var service=new ControllerHubService(provider:provider,profileStore:fixture.Profiles,advancedProvider:fixture.Advanced,
            sessionSinkFactory:(_,_,_)=>throw new Exception("Unsafe output sink started"));
        var profile=new ControllerProfile{OutputMode=ControllerOutputMode.VirtualXbox360}; await fixture.Profiles.SaveAsync(profile);
        try { await service.StartOutputSessionAsync(device.DeviceId,profile.Id); }
        catch(InvalidOperationException ex) when(ex.Message.Contains("raw inspection")) { return; }
        throw new Exception("Read-only fallback started output");
    }
    private sealed class InspectionProvider(ControllerDeviceDescriptor device) : IControllerProvider
    {
        public event EventHandler<ControllerDeviceEventArgs>? DeviceAdded { add {} remove {} }
        public event EventHandler<ControllerDeviceEventArgs>? DeviceRemoved { add {} remove {} }
        public IReadOnlyList<ControllerDeviceDescriptor> GetConnectedDevices()=>new[]{device};
        public bool TryRead(string id,out ControllerSnapshot? snapshot){snapshot=null;return false;}
        public void Dispose(){}
    }
}
