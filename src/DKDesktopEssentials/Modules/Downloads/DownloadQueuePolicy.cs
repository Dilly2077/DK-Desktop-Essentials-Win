using System.IO;
using System.Runtime.InteropServices;

namespace DKDesktopEssentials.Modules.Downloads;

internal interface IDownloadPersistence : IDisposable
{
    IReadOnlyList<DownloadCheckpointStore.SavedJob> Load();
    void Save(DownloadJobRecord job, DownloadSource source, long? expectedRevision = null);
    void Forget(Guid id);
    DownloadQueueSettings LoadSettings();
    void SaveSettings(DownloadQueueSettings settings);
}

internal static class DownloadQueuePolicy
{
    internal static void Validate(DownloadQueueSettings settings)
    {
        if (settings.MaxConcurrentDownloads is < 1 or > 16 || settings.MaxAttempts is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(settings));
    }
    internal static IEnumerable<DownloadJobRecord> Order(IEnumerable<DownloadJobRecord> jobs) =>
        jobs.OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedUtc).ThenBy(j => j.Id);
    internal static bool Retryable(DownloadErrorCode code) => code is DownloadErrorCode.NetworkUnavailable or
        DownloadErrorCode.Timeout or DownloadErrorCode.DnsFailure or DownloadErrorCode.HttpRateLimited or
        DownloadErrorCode.HttpServerError or DownloadErrorCode.ContentLengthMismatch;
    internal static TimeSpan Backoff(int attempt, double random, TimeSpan? retryAfter)
    {
        var seconds = Math.Min(120, 2 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 7)) * (1 + Math.Clamp(random, 0, 1) * .25));
        var delay = TimeSpan.FromSeconds(seconds);
        return retryAfter > delay ? retryAfter.Value : delay;
    }
}

internal static class DownloadDiskSpace
{
    internal const long Headroom = 64 * 1024;
    internal static long Available(string path)
    {
        if (!GetDiskFreeSpaceExW(path, out var available, out _, out _))
            throw new DownloadEngineException(Marshal.GetLastWin32Error() == 5 ? DownloadErrorCode.PermissionDenied : DownloadErrorCode.StorageFailure);
        return (long)Math.Min(available, long.MaxValue);
    }
    internal static void Require(string path, long bytes)
    {
        if (Available(Path.GetDirectoryName(path)!) < bytes)
            throw new DownloadEngineException(DownloadErrorCode.DiskFull);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
}
