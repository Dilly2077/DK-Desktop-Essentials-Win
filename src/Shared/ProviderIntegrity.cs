using System.IO;
using System.Security.Cryptography;
using System.IO.Compression;

namespace DKDesktopEssentials.Security;

internal static class ProviderIntegrity
{
    internal const int MaximumLibraryBytes = 64 * 1024 * 1024;
    internal const int MaximumPackageBytes = 128 * 1024 * 1024;

    internal static async Task CopyPackageAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            total = checked(total + read);
            if (total > MaximumPackageBytes) throw new InvalidDataException("Provider download exceeds the pinned package size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    internal static byte[] ReadArchiveLibrary(byte[] packageBytes, string packageSha256, string rootEntry, string librarySha256)
    {
        if (packageBytes.Length is <= 0 or > MaximumPackageBytes ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(packageBytes), Convert.FromHexString(packageSha256)))
            throw new InvalidDataException("Provider package integrity check failed.");
        using var archive = new ZipArchive(new MemoryStream(packageBytes, writable: false));
        var entry = archive.GetEntry(rootEntry) ?? throw new InvalidDataException("The pinned provider archive is missing its root library.");
        using var source = entry.Open();
        return ReadVerified(source, librarySha256, entry.Length);
    }

    internal static string ValidatePath(string path, string fileName)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.AsSpan(2).Contains(':') ||
            !string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The provider must be an absolute local path to the expected library.");
        var fullPath = Path.GetFullPath(path);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Provider library links are not supported.");
        return fullPath;
    }

    internal static byte[] ReadVerified(string path, string fileName, string expectedSha256)
    {
        path = ValidatePath(path, fileName);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadVerified(source, expectedSha256);
    }

    // The caller executes this returned snapshot, never reopens the verified path.
    internal static byte[] ReadVerified(Stream source, string expectedSha256, long? length = null)
    {
        var byteCount = length ?? source.Length;
        if (byteCount is <= 0 or > MaximumLibraryBytes)
            throw new InvalidDataException("Provider library size is invalid. Explicitly reinstall the pinned provider.");
        var bytes = new byte[checked((int)byteCount)];
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes), Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("Provider library integrity check failed. Explicitly reinstall the pinned provider.");
        return bytes;
    }
}
