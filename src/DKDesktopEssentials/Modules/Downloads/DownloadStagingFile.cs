using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DKDesktopEssentials.Modules.Downloads;

/// <summary>Locks checked directories and exact files during transfer, verification and promotion.</summary>
internal sealed class DownloadStagingFile : IDisposable
{
    private readonly List<SafeFileHandle> _directories = new();
    public FileStream Stream { get; private set; } = null!;
    public string? VerifiedSha256 { get; private set; }

    public static DownloadStagingFile Create(DownloadJobRecord job)
    {
        var lease = Acquire(job);
        try
        {
            if (Path.Exists(job.TemporaryPath))
                throw new DownloadEngineException(DownloadErrorCode.PartialFileInvalid);
            lease.Stream = new FileStream(job.TemporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static void ValidateDestination(DownloadJobRecord job)
    {
        using var lease = Acquire(job);
    }

    internal static DownloadStagingFile LockDirectory(string directory)
    {
        ValidatePath(Path.Combine(directory, "guard"));
        var lease = new DownloadStagingFile();
        try { lease.LockParents(directory); return lease; }
        catch { lease.Dispose(); throw; }
    }

    private void LockParents(string parent)
    {
        var chain = new Stack<string>();
        for (var directory = new DirectoryInfo(parent); directory is not null; directory = directory.Parent)
            chain.Push(directory.FullName);
        foreach (var directory in chain)
        {
            var handle = CreateFileW(Extended(directory), 0, 3, 0, 3, 0x02200000, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error(); handle.Dispose();
                throw new DownloadEngineException(error == 5 ? DownloadErrorCode.PermissionDenied : DownloadErrorCode.DestinationInvalid);
            }
            _directories.Add(handle);
            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
                throw new DownloadEngineException(DownloadErrorCode.DestinationInvalid);
        }
    }

    private static DownloadStagingFile Acquire(DownloadJobRecord job, bool allowDestination = false)
    {
        var lease = new DownloadStagingFile();
        try
        {
            var destination = ValidatePath(job.DestinationPath);
            var staging = ValidatePath(job.TemporaryPath);
            var parent = Path.GetDirectoryName(destination)!;
            if (job.Id == Guid.Empty ||
                string.Equals(destination, staging, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parent, Path.GetDirectoryName(staging), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(staging), $".{job.Id:N}.dkdownload.part", StringComparison.Ordinal))
                throw new DownloadEngineException(DownloadErrorCode.DestinationInvalid);

            // Lock root-to-leaf before inspecting/creating files. OPEN_REPARSE_POINT means
            // junctions/symlinks are inspected rather than followed; omit FILE_SHARE_DELETE
            // so a checked directory cannot be renamed away while the transfer is active.
            lease.LockParents(parent);

            if (!allowDestination && Path.Exists(destination))
                throw new DownloadEngineException(DownloadErrorCode.DestinationExists);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private void OpenExact(string path, bool readOnly = false)
    {
        var file = CreateFileW(Extended(path), readOnly ? 0x80000000u : 0xC0010000u, 0, 0, 3, 0x00200000, 0);
        if (file.IsInvalid) { file.Dispose(); throw new DownloadEngineException(DownloadErrorCode.PartialFileInvalid); }
        if (!GetFileInformationByHandle(file, out var info) || (info.Attributes & 0x410) != 0 || info.Links != 1)
        { file.Dispose(); throw new DownloadEngineException(DownloadErrorCode.PartialFileInvalid); }
        Stream = new FileStream(file, readOnly ? FileAccess.Read : FileAccess.ReadWrite);
    }

    internal static void CheckDatabaseFile(string path)
    {
        if (!Path.Exists(path)) return;
        using var handle = CreateFileW(Extended(path), 0, 7, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x410) != 0 || info.Links != 1)
            throw new DownloadEngineException(DownloadErrorCode.StorageFailure);
    }

    internal static DownloadStagingFile OpenCompleted(DownloadJobRecord job)
    {
        if (job.State != DownloadJobState.Completed || job.VerifiedSha256 is null)
            throw new DownloadEngineException(DownloadErrorCode.DestinationInvalid);
        var lease = Acquire(job, allowDestination: true);
        try
        {
            lease.OpenExact(job.DestinationPath, readOnly: true);
            if (lease.Stream.Length != job.CheckpointBytes) throw new DownloadEngineException(DownloadErrorCode.ContentLengthMismatch);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static DownloadStagingFile OpenResume(DownloadJobRecord job, long offset, IncrementalHash hash, CancellationToken token)
    {
        var lease = Acquire(job);
        try
        {
            lease.OpenExact(job.TemporaryPath);
            if (offset < 0 || lease.Stream.Length < offset || offset != job.CheckpointBytes || job.CheckpointSha256 is null)
                throw new DownloadEngineException(DownloadErrorCode.PartialFileInvalid);
            var buffer = new byte[64 * 1024];
            long remaining = offset;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = lease.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) throw new DownloadEngineException(DownloadErrorCode.PartialFileInvalid);
                hash.AppendData(buffer, 0, count); remaining -= count;
            }
            if (!Convert.ToHexString(hash.GetCurrentHash()).Equals(job.CheckpointSha256, StringComparison.OrdinalIgnoreCase))
                throw new DownloadEngineException(DownloadErrorCode.PartialFileInvalid);
            // Any suffix beyond the durable checkpoint stays untouched until the remote
            // response has been validated. Only then may TransferAsync discard that tail.
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static bool ReconcilePromotion(DownloadJobRecord job)
    {
        if (job.State != DownloadJobState.Verifying || job.VerifiedSha256 is null ||
            Path.Exists(job.TemporaryPath) || !Path.Exists(job.DestinationPath)) return false;
        using var lease = Acquire(job, allowDestination: true);
        lease.OpenExact(job.DestinationPath);
        if (lease.Stream.Length != job.CheckpointBytes ||
            !Convert.ToHexString(SHA256.HashData(lease.Stream)).Equals(job.VerifiedSha256, StringComparison.OrdinalIgnoreCase))
            throw new DownloadEngineException(DownloadErrorCode.ChecksumMismatch);
        return true;
    }

    internal static DownloadStagingFile Verify(DownloadJobRecord job, DownloadExecutionResult result, CancellationToken token)
    {
        var lease = Acquire(job);
        try
        {
            // Read/write/delete access on this exact file permits a handle-based rename
            // while denying replacement or writes through other handles during verification.
            lease.OpenExact(job.TemporaryPath);
            if (lease.Stream.Length != result.TotalBytesWritten ||
                (job.ExpectedBytes is { } size && size != lease.Stream.Length))
                throw new DownloadEngineException(DownloadErrorCode.ContentLengthMismatch);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = lease.Stream.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            lease.VerifiedSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(lease.VerifiedSha256, result.ComputedSha256, StringComparison.OrdinalIgnoreCase) ||
                (job.ExpectedSha256 is { } expected && !string.Equals(expected, lease.VerifiedSha256, StringComparison.OrdinalIgnoreCase)))
                throw new DownloadEngineException(DownloadErrorCode.ChecksumMismatch);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal void Promote(DownloadJobRecord job)
    {
        // Mark as Internet-origin without storing a secret-bearing HostUrl/ReferrerUrl.
        // Fail closed on volumes which cannot retain this Windows metadata.
        using (var zone = new FileStream(job.TemporaryPath + ":Zone.Identifier", FileMode.Create,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            zone.Write(Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"));
            zone.Flush(flushToDisk: true);
        }
        Stream.Flush(flushToDisk: true);
        var name = Encoding.Unicode.GetBytes(Extended(job.DestinationPath) + "\0");
        var header = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName)).ToInt32();
        var memory = Marshal.AllocHGlobal(header + name.Length);
        try
        {
            Marshal.Copy(new byte[header], 0, memory, header); // ReplaceIfExists = false; RootDirectory = NULL
            Marshal.WriteInt32(memory, Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.Length)).ToInt32(), name.Length - 2);
            Marshal.Copy(name, 0, memory + header, name.Length);
            if (!SetFileInformationByHandle(Stream.SafeFileHandle, 3, memory, (uint)(header + name.Length)))
            {
                var error = Marshal.GetLastWin32Error();
                throw new DownloadEngineException(error is 80 or 183 ? DownloadErrorCode.DestinationExists :
                    error == 5 ? DownloadErrorCode.PermissionDenied : DownloadErrorCode.StorageFailure);
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    // Prefix only at the Win32 call boundary, after ordinary local paths have been
    // validated. Never accept device/extended paths from the caller.
    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    private static string ValidatePath(string path)
    {
        // Module 32 starts with ordinary local drive paths only. UNC/device paths, ADS,
        // dot segments and Windows aliases are rejected, never silently normalised.
        if (string.IsNullOrWhiteSpace(path) || path.Length < 4 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.Contains('/') || path[3..].Contains(':'))
            throw new DownloadEngineException(DownloadErrorCode.DestinationInvalid);
        foreach (var segment in path[3..].Split('\\'))
            if (!IsSafeFileName(segment))
                throw new DownloadEngineException(DownloadErrorCode.DestinationInvalid);
        return Path.GetFullPath(path);
    }

    internal static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('\\') || name.Contains('/'))
            return false;
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
              "123456789¹²³".Contains(stem[3]));
    }

    public void Dispose()
    {
        try { Stream?.Dispose(); }
        finally
        {
            for (var i = _directories.Count - 1; i >= 0; i--) _directories[i].Dispose();
            _directories.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation
    {
        public uint Replace;
        public nint Root;
        public uint Length;
        public char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, nint information, uint size);
}
