using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using DKDesktopEssentials.Security;

namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

// Windows loads native code by path. Keep the file AND its directory chain locked
// against mutation/rename, from verification until the last native call/unload.
internal sealed class VerifiedNativeLibrary : IDisposable
{
    private readonly List<SafeFileHandle> _directories = new();
    private FileStream? _file;
    internal nint Handle { get; private set; }

    internal static VerifiedNativeLibrary Open(string path, string fileName, string expectedSha256)
    {
        var library = LockAndVerify(path, fileName, expectedSha256);
        try
        {
            // Resolve the canonical DOS path from the locked handle. Windows' DLL
            // loader rejects volume-GUID/NT-device paths (ERROR_INVALID_PARAMETER).
            // Dependencies may resolve only in System32, never the provider/current directory.
            library.Handle = LoadLibraryExW(FinalPath(library._file!.SafeFileHandle), 0, 0x00000800);
            if (library.Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Verified native provider could not load.");
            return library;
        }
        catch { library.Dispose(); throw; }
    }

    internal static VerifiedNativeLibrary LockAndVerify(string path, string fileName, string expectedSha256)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        path = ProviderIntegrity.ValidatePath(path, fileName);
        var library = new VerifiedNativeLibrary();
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            var chain = new Stack<string>();
            for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
                chain.Push(current);
            while (chain.TryPop(out var parent))
                library._directories.Add(OpenLocked(parent, directory: true));
            library._file = new FileStream(OpenLocked(path, directory: false), FileAccess.Read);
            ProviderIntegrity.ReadVerified(library._file, expectedSha256);
            return library;
        }
        catch { library.Dispose(); throw; }
    }

    private static SafeFileHandle OpenLocked(string path, bool directory)
    {
        const uint genericRead = 0x80000000, shareRead = 1, openExisting = 3;
        const uint openReparsePoint = 0x00200000, backupSemantics = 0x02000000;
        var handle = CreateFileW(path, genericRead, shareRead, 0, openExisting,
            openReparsePoint | (directory ? backupSemantics : 0), 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new IOException("Provider path cannot be locked for integrity verification.", new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new IOException("Provider path attributes could not be verified.", new Win32Exception(error));
        }
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
        {
            handle.Dispose(); throw new InvalidDataException("Provider paths must contain ordinary files/directories, without links or junctions.");
        }
        return handle;
    }

    private static string FinalPath(SafeFileHandle file)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("Provider volume path could not be resolved.");
        return buffer.ToString();
    }

    public void Dispose()
    {
        if (Handle != 0) { NativeLibrary.Free(Handle); Handle = 0; }
        _file?.Dispose(); _file = null;
        for (var i = _directories.Count - 1; i >= 0; i--) _directories[i].Dispose();
        _directories.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern nint LoadLibraryExW(string path, nint reserved, uint flags);
}
