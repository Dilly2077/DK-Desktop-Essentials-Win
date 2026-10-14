using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DKDesktopEssentials.Security;

// The pinned SDK extracts executable tools and reuses caches by size. Elevated
// execution must never use the caller's writable TEMP or executable search path.
internal static class PrivilegedProviderEnvironment
{
    internal static void Initialize()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "DKControllerMaintenance-" + ProviderPins.HidMaestroSdkSha256);
        // Administrators own the cache; inheritance is protected. OWNER RIGHTS
        // suppresses implicit owner WRITE_DAC on SDK-created children even if a
        // child's owner is the linked user's SID rather than Administrators.
        const string descriptor = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;RC;;;OW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor, 1, out var security, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create protected provider staging security.");
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = security };
            if (!CreateDirectoryW(directory, ref attributes))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 183) throw new Win32Exception(error, "Cannot create private elevated provider staging.");
            }
        }
        finally { LocalFree(security); }
        AssertProtected(directory);
        foreach (var variable in new[] { "TEMP", "TMP", "SystemTemp" })
            Environment.SetEnvironmentVariable(variable, directory);
        Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
        Environment.SetEnvironmentVariable("COMSPEC", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
        Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Elevated provider temporary path could not be isolated.");
        // Keep the environment isolated until process exit: SDK background warmup
        // tasks can outlive a context. Retain the bounded, digest-specific protected
        // cache for offline reuse; never delete other clients' temporary directories.
    }

    private static void AssertProtected(string directory)
    {
        var security = new DirectoryInfo(directory).GetAccessControl();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var ownerRights = new SecurityIdentifier("S-1-3-4");
        if (!security.AreAccessRulesProtected || !administrators.Equals(security.GetOwner(typeof(SecurityIdentifier))) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Provider staging ownership or inheritance is unsafe.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 3 || rules.Any(rule => rule.AccessControlType != AccessControlType.Allow ||
                rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) ||
                (ownerRights.Equals(rule.IdentityReference)
                    ? rule.FileSystemRights != FileSystemRights.ReadPermissions
                    : ((rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl ||
                       (!administrators.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference))))))
            throw new InvalidOperationException("Provider staging permissions are unsafe.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision, out nint security, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
