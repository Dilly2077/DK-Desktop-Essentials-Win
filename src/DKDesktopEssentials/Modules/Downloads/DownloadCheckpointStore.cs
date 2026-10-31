using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DKDesktopEssentials.Modules.Downloads;

/// <summary>Minimum 32.4 restart journal, not the future transactional queue/database.</summary>
internal sealed class DownloadCheckpointStore : IDownloadPersistence
{
    private readonly string _directory;
    private readonly IDisposable _directories;
    private readonly FileStream _owner;
    private static readonly byte[] Entropy = "DKDesktopEssentials.DownloadCheckpoint.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    internal sealed record SavedJob(int SchemaVersion, DownloadJobRecord Job, string AuthorizedUrl)
    { public override string ToString() => "SavedJob { [protected native data] }"; }

    internal static void Validate(SavedJob saved)
    {
        var job = saved.Job;
        static bool Digest(string? value) => value is null || value.Length == 64 && value.All(Uri.IsHexDigit);
        if (saved.SchemaVersion != 1 || job.Id == Guid.Empty || !Enum.IsDefined(job.State) ||
            !Enum.IsDefined(job.Priority) || !Enum.IsDefined(job.ResumeCapability) ||
            job.LastErrorCode is { } error && !Enum.IsDefined(error) ||
            job.CheckpointBytes < 0 || job.CheckpointBytes > job.ExpectedBytes || job.ExpectedBytes < 0 ||
            job.ParallelSegments is not (1 or 2 or 4) || job.Revision < 0 || job.Attempts < 0 ||
            job.UpdatedUtc.Offset != TimeSpan.Zero || job.CreatedUtc.Offset != TimeSpan.Zero || job.UpdatedUtc < job.CreatedUtc ||
            job.NextAttemptUtc?.Offset is { } offset && offset != TimeSpan.Zero ||
            job.CheckpointSha256 is null || !Digest(job.CheckpointSha256) || !Digest(job.ExpectedSha256) || !Digest(job.VerifiedSha256) ||
            string.IsNullOrEmpty(job.SourceKey) || job.SourceKey.Length > 128 || saved.AuthorizedUrl.Length > 16384)
            throw new InvalidDataException();
        var uri = HttpDownloadEngine.ValidateSource(new(new Uri(saved.AuthorizedUrl)));
        if (job.DisplayHost != uri.IdnHost) throw new InvalidDataException();
    }

    internal DownloadCheckpointStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _directories = DownloadStagingFile.LockDirectory(_directory);
        try
        {
            var owner = Path.Combine(_directory, "owner.lock");
            if (Path.Exists(owner) && (File.GetAttributes(owner) & FileAttributes.ReparsePoint) != 0)
                throw new DownloadEngineException(DownloadErrorCode.StorageFailure);
            _owner = new FileStream(owner, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch { _directories.Dispose(); throw; }
    }

    public IReadOnlyList<SavedJob> Load()
    {
        try
        {
            var files = Directory.GetFiles(_directory, "*.checkpoint");
            if (files.Length > 1000) throw new InvalidDataException();
            var jobs = new List<SavedJob>();
            foreach (var path in files)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 64 * 1024)
                    throw new InvalidDataException();
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    var saved = JsonSerializer.Deserialize<SavedJob>(bytes, Json) ?? throw new InvalidDataException();
                    Validate(saved);
                    if (Path.GetFileName(path) != saved.Job.Id.ToString("N") + ".checkpoint") throw new InvalidDataException();
                    jobs.Add(saved);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            return jobs.OrderBy(j => j.Job.CreatedUtc).ToArray();
        }
        catch { throw new DownloadEngineException(DownloadErrorCode.StorageFailure); }
    }

    public void Save(DownloadJobRecord job, DownloadSource source, long? expectedRevision = null)
    {
        var path = Path.Combine(_directory, job.Id.ToString("N") + ".checkpoint");
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedJob(1, job, source.AuthorizedUrl.AbsoluteUri), Json);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            if (encrypted.Length > 64 * 1024) throw new InvalidDataException();
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(encrypted); file.Flush(flushToDisk: true); }
            // A crash leaves either the old or new complete protected record. Ignore
            // orphan .pending files on load; never interpret them as committed data.
            File.Move(temporary, path, overwrite: true);
        }
        catch { throw new DownloadEngineException(DownloadErrorCode.StorageFailure); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public DownloadQueueSettings LoadSettings() => new();
    public void SaveSettings(DownloadQueueSettings settings) => throw new NotSupportedException();
    public void Forget(Guid id) => File.Delete(Path.Combine(_directory, id.ToString("N") + ".checkpoint"));
    public void Dispose() { _owner.Dispose(); _directories.Dispose(); }
}
