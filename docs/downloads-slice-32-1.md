# Category 7 — Downloads & Transfers
## Module 32, Slice 32.1 — Contract and lifecycle foundation

**Status:** contracts and deterministic tests added on feature branch; Windows build/tests not executed here.
**Scope:** define job states, persistence schema, queue behaviour, error codes and engine-neutral APIs. Not an HTTP downloader.
**Dependencies:** Windows 10/11, existing .NET 8 WPF host and local WebView2 UI.
**Next:** 32.2 engine evaluation/integration; 32.3 first ordinary HTTP/HTTPS transfers.

### 1. Stable lifecycle

States (persist as exact string enum names):
`Queued`, `Resolving`, `Downloading`, `Paused`, `Retrying`,
`Verifying`, `Completed`, `Failed`, `Cancelled`.

Normal route:

```text
Queued --dispatch--> Resolving --probe succeeds--> Downloading
                         |                        |
                         +--retryable error--+    +--transfer ends--> Verifying
                         |                   |                         |
                         v                   v                         +--verified--> Completed
                       Failed             Retrying                      +--invalid--> Failed
                                            |
                                      retry becomes due
                                            |
                                            +--> Resolving
```

Paused is entered only on explicit user pause from any nonterminal runnable/waiting
state; resume returns to Queued (not directly to Downloading).
Failed requires explicit user retry (Failed -> Queued); it is not automatically
runnable. Cancelled and Completed cannot be restarted: create a new job if desired.
Cancel is allowed from every non-Completed/non-Cancelled state.
An engine's completion callback only permits Downloading -> Verifying.
Only successful length/hash checks and durable file finalisation permit Completed.

Startup reconciliation of Resolving/Downloading/Retrying/Verifying -> Queued is
a deliberate `StartupRecovery` transition. Preserve all partial bytes and remote
validators until they have been inspected. Queued remains queued; Paused, Failed,
Completed and Cancelled retain their states.

State changes, associated safe errors/events and revision increments must be
committed as **one database transaction**. Revision is an optimistic-concurrency
guard: stale updates must fail rather than overwrite more recent state.
Progress updates are separate, monotonic within one transfer attempt and may be
coalesced before DB writes; do not use progress events as state transitions.

### 2. Persistence

Schema: [downloads-schema-v1.sql](downloads-schema-v1.sql).

Proposed local database:
`%LOCALAPPDATA%\\DKDesktopEssentials\\Downloads\\downloads.db`.

- `download_jobs`: GUID, timestamps UTC, state, priority, destination/staging
  paths, transferred and expected bytes, optional expected/verified SHA-256,
  ETag/Last-Modified validators, resume capability, retries, next retry instant,
  typed last error, revision.
- `download_sources`: opaque source key, non-sensitive display host, encrypted
  URL bytes. Protect the actual URL (including query string) with Windows DPAPI
  CurrentUser before persisting; plaintext URLs can embed signed tokens.
- `download_events`: bounded, user-clearable local audit trail with safe codes,
  not raw HTTP headers, cookies, signed URLs or exception strings.
- `download_queue_settings`: one row containing concurrency/attempt defaults.

SQLite's transactional guarantees are appropriate for metadata, but **do not**
make filesystem writes and DB writes one atomic operation. On restart, reconcile
actual staging-file length with saved metadata; validate the remote entity before
reuse and never silently concatenate mismatched content. Keep the DB, -wal and
-shm files together during backup when present. Configure busy_timeout, foreign
keys, WAL and sensible durability policy in the future store implementation.

Schema version 1 is a *proposal*; do not claim the DB has been created/migrated.
Schema migration must occur in an explicit transaction and be tested with backups.

### 3. Queue contract (implementation scheduled for 32.5)

- Default maximum simultaneously active jobs: **3**. A job holds a slot during
  Resolving, Downloading or Verifying. Queued, Paused, Retrying, Failed, Completed
  and Cancelled never hold an active slot.
- Priority: High > Normal > Low; within each tier, dispatch in created-time
  ascending order, then GUID ascending for deterministic ties.
- Dispatch only Queued jobs and Retrying jobs whose next-attempt timestamp
  is due. Retrying -> Resolving is legal only after backoff policy says due.
- Default max attempt count: **5 including the initial attempt**.
  After a retryable error: exponential backoff, starting at 2 seconds, capped
  at 120 seconds, bounded random jitter. HTTP Retry-After can impose a longer
  wait where policy permits. The actual algorithm belongs in 32.5.
- Pausing stops new dispatch immediately and asks the active engine to stop
  before persistently marking Paused; cancellation additionally confirms
  whether staging data is retained or removed. Avoid UI claiming an operation
  has finished before the engine acknowledges.
- Auto-start after Windows/app restart is **off by default**; reconciled jobs
  become Queued, not implicitly transferred. A later user preference may allow
  automatic starts. This keeps transfers opt-in and user-visible.
- One in-process coordinator is the single writer/owner of engine leases.
  Never spawn concurrent duplicate jobs writing the same staging path.

### 4. Integrity, partial content and filesystem rules

- Require explicit user-provided HTTP/HTTPS URLs for the Module 32 core.
  No ftp:, file:, data:, javascript:, magnet:, or executable/shell schemes.
  No content is downloaded merely because it appears in a document or page.
- On a resumed range request, HTTP 206 must have a compatible Content-Range;
  response identity/ETag/Last-Modified must be checked. HTTP 200 can mean the
  server ignored Range: **never append its body** to a partial file.
  HTTP 416 can mean the range is unsatisfiable; reconcile actual size and
  validators before deciding whether to reverify or restart safely.
- Accept-Ranges is informative, not proof a subsequent range will succeed.
  Strong ETag validators are preferable for If-Range; a weak ETag cannot safely
  serve as an If-Range validator. Changed resources require an explicit
  restart or a new job, not splicing different versions.
- Write to a job-specific temporary file in the selected destination volume.
  Validate normalized paths and prohibit traversal; reserve final filename and
  do not overwrite existing user files by default. Final rename/replace must
  occur only after verified size/hash and an explicit collision policy.
- Do not auto-open or execute downloaded files. Expose Defender scanning as an
  opt-in visible later feature; never imply files are safe merely because they
  have finished transferring.
- The UI must distinguish *unknown* size and resume support from *zero*.

### 5. Error contract

Typed `DownloadErrorCode` inventory in DownloadContracts.cs:

| Domain | Codes |
|---|---|
| Input/path | InvalidUrl, UnsupportedProtocol, DestinationInvalid, DestinationExists, PermissionDenied |
| Local storage | DiskFull, StorageFailure, PartialFileInvalid |
| Network | NetworkUnavailable, Timeout, DnsFailure, TlsFailure |
| HTTP | HttpUnauthorized, HttpForbidden, HttpNotFound, HttpRateLimited, HttpServerError |
| Resume/integrity | RangeNotSupported, RemoteContentChanged, ContentLengthMismatch, ChecksumMismatch |
| Engine | EngineUnavailable, UnexpectedFailure |

Recommended classification (not yet implemented):
- Retryable: NetworkUnavailable, Timeout, DnsFailure, HttpRateLimited,
  HttpServerError, and transient EngineUnavailable where explicitly known safe.
- User intervention: authentication/forbidden, invalid URL, destination/permission
  errors, unsupported protocol, content changed and hash mismatch.
- DiskFull is paused/failed with an actionable message, not a blind fast retry.
- UnexpectedFailure is a generic, redacted error; raw exceptions are local
  developer diagnostics only, with secrets removed.

### 6. Native/API boundary

The existing WebView2 UI already communicates with native C# services.
Use a dedicated `downloads:` command family routed only from trusted packaged
`dk.local` pages (do not relax existing origin checks).

Commands to implement in later slices:
`downloads:enqueue`, `downloads:list`, `downloads:get`,
`downloads:pause`, `downloads:resume`, `downloads:retry`,
`downloads:cancel`, `downloads:getSettings`, `downloads:setSettings`.
Push redacted job-change events to the trusted UI when available.

Request envelope follows the current repository convention:

```json
{
  "type": "downloads:enqueue",
  "requestId": "client-generated-id",
  "payload": {
    "url": "https://example.org/file.zip",
    "destinationDirectory": "C:\\Users\\Example\\Downloads",
    "preferredFileName": "file.zip",
    "priority": "Normal"
  }
}
```

Response: `{ "command": "...", "requestId": "...", "ok": true,
"result": { /* DownloadJobView only */ }, "error": null }`.
The full URL may appear *only* in an ephemeral enqueue request and inside the
protected native vault/engine, never in job-list responses.
Do not pass OS command lines, arbitrary engine flags, cookie jars or headers
through the UI bridge. Any later browser-authenticated transfer requires
separate explicit permission and secret handling.

C# contracts included now:
`IDownloadEngine`, `IDownloadJobStore`, `IDownloadCoordinator`,
`DownloadJobRecord`, `DownloadJobView`, `DownloadStateMachine`.
The interfaces are not implementations. Use one store/coordinator across future
Modules 33-36; replace the engine behind `IDownloadEngine` without changing
visible commands.

### 7. Tests / acceptance

Tests are in `tests/DownloadCore.Tests/` and cover happy-path verification,
illegal transitions, pause/requeue, explicit retry, cancellation terminality,
startup recovery, retry queue behaviour, UTC/revision and redacted views.

On user's **local Windows machine**, without GitHub Actions:

```powershell
dotnet run --project .\tests\DownloadCore.Tests\DownloadCore.Tests.csproj
```

To run all tests with an output report:

```powershell
dotnet run --project .\tests\DownloadCore.Tests\DownloadCore.Tests.csproj -- --report .\artifacts\downloads-slice-32-1-tests.json
```

32.1 exit criteria: source contracts reviewed, state machine and test project
compile locally, tests pass, schema/error/API contract approved. There is no
network engine, downloads UI, persistence implementation or Windows acceptance
at this slice.

### 8. Work handoff

| Area | At end of 32.1 |
|---|---|
| State machine and typed contracts | Code added; compilation/test pending |
| SQLite persistence | Versioned SQL schema defined, no DB implementation yet |
| Queue rules | Defined, scheduler not implemented |
| HTTP transfer/resume | Specified constraints only, engine not wired |
| Browser/media downloads | Not started (Modules 33/34) |
| UI | Not started |
| Physical Windows testing | Not run |
| Next slice | 32.2: investigate/test aria2 packaging and implement adapter under `IDownloadEngine` |

References: RFC 9110 HTTP Semantics https://www.rfc-editor.org/rfc/rfc9110 ;
SQLite Transactions https://www.sqlite.org/transactional.html ;
SQLite WAL https://www.sqlite.org/wal.html .
