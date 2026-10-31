using System.Runtime.InteropServices;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

// Legacy Windows input is useful when WGI exposes no devices. Slot IDs are not
// physical identities: expose raw inspection only, never reconnect/output by slot.
internal sealed class WindowsJoystickFallback
{
    internal interface IApi { bool Read(uint slot, out Caps caps, out Reading reading); }
    private readonly IApi _api;
    private readonly Dictionary<uint, string> _ids = new();
    internal WindowsJoystickFallback(IApi? api = null) => _api = api ?? new NativeApi();
    internal void Reset() => _ids.Clear();
    internal IReadOnlyList<ControllerDeviceDescriptor> GetDevices()
    {
        var result = new List<ControllerDeviceDescriptor>();
        for (uint slot = 0; slot < 16; slot++)
        {
            if (!_api.Read(slot, out var caps, out _)) { _ids.Remove(slot); continue; }
            if (!_ids.TryGetValue(slot, out var id)) _ids[slot] = id = "winmm:" + Guid.NewGuid().ToString("N");
            result.Add(new(id, string.IsNullOrWhiteSpace(caps.Name) ? "Windows joystick" : caps.Name,
                ControllerFamily.Generic, 0, 0, false, false, (int)Math.Min(caps.Buttons, 32),
                AxisIndices(caps.Flags).Length, (caps.Flags & 16) != 0 ? 1 : 0)
            { InputBackend = "windows-joystick", SupportsVirtualOutput = false, ConnectionType = "unknown" });
        }
        return result;
    }
    internal bool TryRead(string id, out ControllerSnapshot? snapshot)
    {
        snapshot = null;
        var pair = _ids.FirstOrDefault(pair => pair.Value == id);
        if (pair.Value is null) return false;
        if (!_api.Read(pair.Key, out var caps, out var reading)) { _ids.Remove(pair.Key); return false; }
        snapshot = ConvertReading(id, caps, reading);
        return true;
    }
    internal static int[] AxisIndices(uint flags) => new[] { 0, 1 }.Concat(
        Enumerable.Range(0, 4).Where(i => (flags & (1u << i)) != 0).Select(i => i + 2)).ToArray();
    internal static double Normalize(uint value, uint min, uint max) => max <= min ? 0 :
        Math.Clamp(2 * ((double)value - min) / ((double)max - min) - 1, -1, 1);
    internal static ControllerSnapshot ConvertReading(string id, Caps caps, Reading reading)
    {
        var values = new[] { reading.X, reading.Y, reading.Z, reading.R, reading.U, reading.V };
        var minima = new[] { caps.Xmin, caps.Ymin, caps.Zmin, caps.Rmin, caps.Umin, caps.Vmin };
        var maxima = new[] { caps.Xmax, caps.Ymax, caps.Zmax, caps.Rmax, caps.Umax, caps.Vmax };
        return new(id, (ulong)Environment.TickCount64, null,
            Enumerable.Range(0, (int)Math.Min(caps.Buttons, 32)).Select(i => (reading.Buttons & (1u << i)) != 0).ToArray(),
            AxisIndices(caps.Flags).Select(i => Normalize(values[i], minima[i], maxima[i])).ToArray(),
            (caps.Flags & 16) == 0 ? Array.Empty<int>() : new[] { reading.Pov == 65535 ? 0 : (int)((reading.Pov % 36000 + 2250) / 4500 % 8) + 1 });
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Caps
    {
        public ushort Manufacturer, Product;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Xmin, Xmax, Ymin, Ymax, Zmin, Zmax, Buttons, PeriodMin, PeriodMax;
        public uint Rmin, Rmax, Umin, Umax, Vmin, Vmax, Flags, MaxAxes, Axes, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegistryKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Oem;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Reading { public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2; }
    private sealed class NativeApi : IApi
    {
        public bool Read(uint slot, out Caps caps, out Reading reading)
        {
            reading = new() { Size = (uint)Marshal.SizeOf<Reading>(), Flags = 255 };
            return joyGetDevCapsW(slot, out caps, (uint)Marshal.SizeOf<Caps>()) == 0 && joyGetPosEx(slot, ref reading) == 0;
        }
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint joyGetDevCapsW(nuint slot, out Caps caps, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint joyGetPosEx(uint slot, ref Reading reading);
    }
}
