using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DKDesktopEssentials.Modules.Downloads;

/// <summary>Transactional queue, protected sources/checkpoints, revision guards and bounded safe events.</summary>
internal sealed class DownloadJobStore : IDownloadPersistence, IDownloadJobStore
{
    private readonly object _gate = new();
    private readonly DownloadCheckpointStore _legacy;
    private readonly DownloadSqlite _db;
    private readonly IDisposable _directories;
    private static readonly byte[] Entropy = "DKDesktopEssentials.DownloadQueue.v2"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    internal const string Schema = """
        CREATE TABLE download_sources(source_key TEXT PRIMARY KEY, protected_url BLOB NOT NULL);
        CREATE TABLE download_jobs(id TEXT PRIMARY KEY, source_key TEXT NOT NULL REFERENCES download_sources(source_key),
            revision INTEGER NOT NULL CHECK(revision>=0), state TEXT NOT NULL, priority INTEGER NOT NULL CHECK(priority BETWEEN 0 AND 2),
            created_utc TEXT NOT NULL, protected_record BLOB NOT NULL);
        CREATE INDEX download_dispatch ON download_jobs(state, priority DESC, created_utc, id);
        CREATE TABLE download_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id TEXT NOT NULL REFERENCES download_jobs(id) ON DELETE CASCADE,
            revision INTEGER NOT NULL, occurred_utc TEXT NOT NULL, from_state TEXT, to_state TEXT NOT NULL, error_code TEXT);
        CREATE TABLE download_queue_settings(singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            max_concurrent INTEGER NOT NULL CHECK(max_concurrent BETWEEN 1 AND 16),
            max_attempts INTEGER NOT NULL CHECK(max_attempts BETWEEN 1 AND 20),
            auto_start INTEGER NOT NULL CHECK(auto_start IN(0,1)));
        INSERT INTO download_queue_settings VALUES(1,3,5,0);
        CREATE TABLE download_metadata(name TEXT PRIMARY KEY, value TEXT NOT NULL);
        PRAGMA user_version=2;
        """;

    internal DownloadJobStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _directories = DownloadStagingFile.LockDirectory(directory);
        try
        {
            // Shares the 32.4 owner lock, so old/new instances cannot race migration.
            _legacy = new DownloadCheckpointStore(Path.Combine(directory, "Checkpoints"));
            try
            {
                var path = Path.Combine(directory, "downloads.db");
                foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                    DownloadStagingFile.CheckDatabaseFile(path + suffix);
                _db = new DownloadSqlite(path);
                try
                {
                    bool initialize;
                    using (var version = _db.Prepare("PRAGMA user_version"))
                    {
                        if (!version.Read() || version.Number(0) is not (0 or 2)) throw new InvalidDataException();
                        initialize = version.Number(0) == 0;
                    }
                    if (initialize) Transaction(() => { _db.Exec(Schema); return true; });
                    _db.Exec("PRAGMA busy_timeout=3000; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA journal_mode=WAL; PRAGMA trusted_schema=OFF;");
                    using (var mode = _db.Prepare("PRAGMA journal_mode"))
                        if (!mode.Read() || mode.Text(0) != "wal") throw new InvalidDataException();
                    using (var check = _db.Prepare("PRAGMA quick_check"))
                        if (!check.Read() || check.Text(0) != "ok") throw new InvalidDataException();
                    using (var links = _db.Prepare("PRAGMA foreign_key_check"))
                        if (links.Read()) throw new InvalidDataException();
                    ImportLegacy();
                }
                catch { _db.Dispose(); throw; }
            }
            catch { _legacy.Dispose(); throw; }
        }
        catch { _directories.Dispose(); throw; }
    }

    private void ImportLegacy()
    {
        using (var query = _db.Prepare("SELECT value FROM download_metadata WHERE name='legacy_imported'"))
            if (query.Read()) return;
        // Leave original encrypted checkpoints untouched as the migration backup.
        var saved = _legacy.Load();
        Transaction(() =>
        {
            foreach (var item in saved)
                if (!Write(item.Job, new(new Uri(item.AuthorizedUrl)), -1)) throw new InvalidDataException();
            using var marker = _db.Prepare("INSERT INTO download_metadata VALUES('legacy_imported','1')"); marker.Read();
            return true;
        });
    }

    private bool Transaction(Func<bool> operation)
    {
        _db.Exec("BEGIN IMMEDIATE");
        try
        {
            if (!operation()) { _db.Exec("ROLLBACK"); return false; }
            _db.Exec("COMMIT"); return true;
        }
        catch { try { _db.Exec("ROLLBACK"); } catch { } throw; }
    }
    private static byte[] Protect<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            if (protectedBytes.Length > 64 * 1024) throw new InvalidDataException();
            return protectedBytes;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static T Unprotect<T>(byte[] encrypted)
    {
        var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public IReadOnlyList<DownloadCheckpointStore.SavedJob> Load()
    {
        lock (_gate)
        {
            try
            {
                using var query = _db.Prepare("SELECT j.id,j.revision,j.state,j.priority,j.source_key,j.created_utc,j.protected_record,s.protected_url FROM download_jobs j JOIN download_sources s ON s.source_key=j.source_key ORDER BY j.created_utc,j.id LIMIT 1001");
                var result = new List<DownloadCheckpointStore.SavedJob>();
                while (query.Read())
                {
                    var job = Unprotect<DownloadJobRecord>(query.Bytes(6));
                    var saved = new DownloadCheckpointStore.SavedJob(1, job, Unprotect<string>(query.Bytes(7)));
                    DownloadCheckpointStore.Validate(saved);
                    if (query.Text(0) != job.Id.ToString("N") || query.Number(1) != job.Revision || query.Text(2) != job.State.ToString() ||
                        query.Number(3) != (int)job.Priority || query.Text(4) != job.SourceKey || query.Text(5) != job.CreatedUtc.ToString("O"))
                        throw new InvalidDataException();
                    result.Add(saved);
                }
                if (result.Count > 1000) throw new InvalidDataException();
                return result;
            }
            catch { throw new DownloadEngineException(DownloadErrorCode.StorageFailure); }
        }
    }
    private bool Write(DownloadJobRecord job, DownloadSource source, long? expected)
    {
        DownloadCheckpointStore.Validate(new(1, job, source.AuthorizedUrl.AbsoluteUri));
        long oldRevision = -1; string? oldState = null;
        using (var old = _db.Prepare("SELECT revision,state FROM download_jobs WHERE id=?", job.Id.ToString("N")))
            if (old.Read()) { oldRevision = old.Number(0); oldState = old.Text(1); }
        if (expected is { } revision && revision != oldRevision || job.Revision <= oldRevision) return false;
        if (oldRevision < 0)
            using (var insertSource = _db.Prepare("INSERT OR IGNORE INTO download_sources VALUES(?,?)", job.SourceKey, Protect(source.AuthorizedUrl.AbsoluteUri))) insertSource.Read();
        using (var update = _db.Prepare("INSERT INTO download_jobs VALUES(?,?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET source_key=excluded.source_key,revision=excluded.revision,state=excluded.state,priority=excluded.priority,created_utc=excluded.created_utc,protected_record=excluded.protected_record",
            job.Id.ToString("N"), job.SourceKey, job.Revision, job.State.ToString(), (int)job.Priority, job.CreatedUtc.ToString("O"), Protect(job))) update.Read();
        if (oldState != job.State.ToString())
        {
            using var audit = _db.Prepare("INSERT INTO download_events(job_id,revision,occurred_utc,from_state,to_state,error_code) VALUES(?,?,?,?,?,?)",
                job.Id.ToString("N"), job.Revision, job.UpdatedUtc.ToString("O"), oldState, job.State.ToString(), job.LastErrorCode?.ToString()); audit.Read();
            _db.Exec("DELETE FROM download_events WHERE sequence NOT IN(SELECT sequence FROM download_events ORDER BY sequence DESC LIMIT 1000)");
        }
        return true;
    }
    public void Save(DownloadJobRecord job, DownloadSource source, long? expectedRevision = null)
    {
        lock (_gate)
            try { if (!Transaction(() => Write(job, source, expectedRevision))) throw new InvalidDataException(); }
            catch { throw new DownloadEngineException(DownloadErrorCode.StorageFailure); }
    }
    public void Forget(Guid id)
    {
        lock (_gate) Transaction(() =>
        {
            using var remove = _db.Prepare("DELETE FROM download_jobs WHERE id=?", id.ToString("N")); remove.Read();
            _db.Exec("DELETE FROM download_sources WHERE source_key NOT IN(SELECT source_key FROM download_jobs)"); return true;
        });
    }
    public DownloadQueueSettings LoadSettings()
    {
        lock (_gate)
        {
            using var query = _db.Prepare("SELECT max_concurrent,max_attempts,auto_start FROM download_queue_settings WHERE singleton=1");
            if (!query.Read() || query.Number(2) is not (0 or 1)) throw new DownloadEngineException(DownloadErrorCode.StorageFailure);
            var settings = new DownloadQueueSettings(checked((int)query.Number(0)), checked((int)query.Number(1)), query.Number(2) == 1);
            DownloadQueuePolicy.Validate(settings); return settings;
        }
    }
    public void SaveSettings(DownloadQueueSettings settings)
    {
        DownloadQueuePolicy.Validate(settings);
        lock (_gate) Transaction(() =>
        {
            using var update = _db.Prepare("UPDATE download_queue_settings SET max_concurrent=?,max_attempts=?,auto_start=? WHERE singleton=1",
                settings.MaxConcurrentDownloads, settings.MaxAttempts, settings.AutoStartOnLaunch ? 1 : 0); update.Read(); return true;
        });
    }
    public Task<DownloadJobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Load().SingleOrDefault(j => j.Job.Id == id)?.Job); }
    public Task<IReadOnlyList<DownloadJobRecord>> ListAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<DownloadJobRecord>>(Load().Select(j => j.Job).ToArray()); }
    public Task AddAsync(DownloadJobRecord job, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { Save(job, Source(job.SourceKey), -1); return Task.CompletedTask; }
    }
    private DownloadSource Source(string key)
    {
        using var query = _db.Prepare("SELECT protected_url FROM download_sources WHERE source_key=?", key);
        if (!query.Read()) throw new DownloadEngineException(DownloadErrorCode.StorageFailure);
        return new(new Uri(Unprotect<string>(query.Bytes(0))));
    }
    public Task<bool> TryUpdateAsync(DownloadJobRecord job, long expectedRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(Transaction(() => Write(job, Source(job.SourceKey), expectedRevision)));
    }
    internal long EventCount { get { lock (_gate) { using var count = _db.Prepare("SELECT count(*) FROM download_events"); count.Read(); return count.Number(0); } } }
    public void Dispose() { _db.Dispose(); _legacy.Dispose(); _directories.Dispose(); }
}
