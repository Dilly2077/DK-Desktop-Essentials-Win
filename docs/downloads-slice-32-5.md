# Module 32, slice 32.5 — Durable queue, retries and storage handling

Implemented and tested on the user's Windows computer on **16 October 2026**.
Branch: `codex/download-core-32-2`; latest local commit: `a4330d6` (build policy).
The download changes for 32.1–32.5 remain uncommitted. No push, merge, remote
build or GitHub Actions dispatch was performed. Build-policy files were preserved.

Status: **implemented, automated-tested, and basic Windows UI-tested**. Full
32.6 acceptance, broader server/storage testing and release readiness remain
separate. The previous slices' contracts and validated transfer/finalization
rules remain in use.

## User-visible behavior

- **Download** adds an authorized link to the local queue, including when all
  active slots are occupied. Default concurrency is three; queue settings permit
  1–16 simultaneous downloads and 1–20 attempts (default five, including the
  initial attempt). Reducing concurrency drains existing work before dispatching
  more. Queued, paused and backoff jobs do not hold transfer slots.
- High priority precedes Normal, then Low. Within a priority, created time and
  GUID provide deterministic ordering. Existing active jobs are not preempted.
  Waiting rows show their queue position; retry rows show attempt count and time.
- Temporary connection, timeout, DNS, rate-limit, server and truncated-body errors
  can retry automatically. Delay starts at two seconds, doubles, adds 0–25% jitter,
  and caps at 120 seconds. A valid Retry-After may increase the delay up to one
  day; a longer requested wait requires user intervention. Failed HEAD probes
  count toward the attempt budget. Backoff is persisted before releasing a slot.
- Retries reuse only a validated committed prefix. An unsafe partial, changed
  representation, checksum error, TLS/authentication error, permission failure
  or full disk requires user intervention. Retry on a Failed job starts a new
  bounded attempt cycle; it preserves bytes and rechecks the destination and
  remote identity. It does not silently restart changed content from zero.
- Pause and Cancel stop queued/backoff dispatch immediately and await active
  workers. Cancel is terminal and retains partial files. A stop arriving during
  the worker's transition to backoff is committed before worker completion.
- Reopening restores the queue/settings. Automatic starts are **off by default**.
  Recovered jobs wait for Resume. The visible preference can explicitly authorize
  future launches to start queued work; paused/failed/cancelled/completed jobs
  never auto-start. Recovered jobs already at their attempt budget also wait for
  an explicit action. Resume/retry explicitly starts a new attempt cycle.

The native bridge now handles `retry`, `getSettings` and `setSettings`, and bounded
priority/connection selection. Origin checks and native destination-folder grants
remain mandatory. Full URLs/paths never appear in job-list responses. The basic
UI preserves scroll position and restores button focus during polling refreshes.

## Persistence, migration and privacy

Production metadata now resides at
`%LOCALAPPDATA%\DKDesktopEssentials\Downloads\downloads.db`. The app uses SQLite
from the Windows System32 `winsqlite3.dll`, restricted to that DLL search location.
No new NuGet/native dependency, downloaded database engine or cloud service was
introduced. Missing/incompatible native SQLite blocks downloads safely. Microsoft
lists the Windows SQLite APIs in its [platform API reference](https://learn.microsoft.com/en-us/uwp/win32-and-com/win32-extension-apis).

Runtime schema **version 2** is defined by `DownloadJobStore.Schema`. The old
[version 1 SQL](downloads-schema-v1.sql) remains a historical proposal and was
never a runtime database. Version 0 initializes inside a transaction; unsupported
database versions are refused without upgrading/rebuilding them automatically.

- Sources are DPAPI CurrentUser-protected blobs. Complete job payloads, including
  paths, validators and checkpoint digests, are also protected. The database is
  not wholly encrypted: opaque IDs/source keys, state, priority, revision, time,
  queue settings and safe event codes are indexed/plain metadata. Protection does
  not isolate data from other software running as the same Windows user.
- Job updates and associated state-change events share a BEGIN IMMEDIATE
  transaction. Expected revisions reject stale writers; unchanged or backwards
  revisions cannot overwrite newer records. Settings are transactionally saved.
- WAL, synchronous FULL, foreign keys and a three-second busy timeout are used;
  startup verifies journal mode, database consistency and foreign-key consistency.
  See SQLite's [WAL](https://www.sqlite.org/wal.html) and
  [durability settings](https://www.sqlite.org/pragma.html#pragma_synchronous).
  File data and SQLite commits still require the existing checkpoint/promotion
  reconciliation; they are not one filesystem/database transaction.
- The first successful import reads/decrypts all 32.4 checkpoints, inserts them
  and marks migration complete in one transaction. Existing encrypted checkpoint
  files stay untouched as the migration backup. Failed import preserves them
  and leaves no completion marker; retrying after repair imports once. Later
  launches use SQLite and do not merge stale backup checkpoints again.
- The same exclusive 32.4 owner lock prevents old/new app instances from racing
  migration or dispatch. Directory/reparse and database-file hard-link checks
  reject unsafe locations. Corrupt protected records block all dispatch. A write
  failure stops active workers, preserves the last committed data and blocks new
  work until recovery on a later launch. No repair routine deletes user files.
- Safe local events are capped at 1,000. On new submissions after 100 retained
  jobs, older Completed/Cancelled metadata can retire; files remain untouched.
  Failed/paused/recovered jobs remain available, with a visible 1,000-record
  safety bound. The QueueFull error directs the user to cancel old waiting/failed
  records. Full history/event clearing and repair UI belong to 32.6.

After migration, backups are recovery material, not the active queue. Back up the
database with its WAL/SHM files if open, or close the app before copying it. An
automated downgrade/backup-restoration flow has not been implemented.

## Files and storage behavior

`DownloadCoordinator.cs` owns dispatch, leases, retries, reservations and settings.
`DownloadQueuePolicy.cs` contains deterministic policy plus the Windows capacity
adapter. `DownloadJobStore.cs` implements the persistence/revision contract;
`DownloadSqlite.cs` contains the small parameterized native binding. The legacy
`DownloadCheckpointStore.cs` is retained for migration and regression coverage.
Contracts, safe errors, the HTTP adapter, native bridge and minimal downloads UI
were extended without adding another transfer engine.

Destinations are reserved across queued/paused/running jobs and checked again at
dispatch and explicit retry. Existing files are refused; the final handle rename
still disallows replacement. Late collisions preserve existing bytes without
promoting a partial. Failed jobs rejoining the queue must re-acquire a reservation.

After probing, preflight uses available space for the current Windows user,
remaining bytes, other active reservations on the same local drive, and 64 KiB
headroom per job. Unknown lengths remain unknown. Each real write also checks
available space; disk-full I/O errors map to DiskFull and do not auto-retry.
Preflight is advisory: unrelated writes, quotas and device changes can still
exhaust storage. No files are removed to free space.

## Validation

Local SDKs verified: **8.0.426** and **10.0.402** in ignored `artifacts/dotnet`.
Local Windows SQLite DLL reported **3.53.4**. No SDK/security/signing configuration
was changed. Use the local build environment from [the build policy](local-build-policy.md).

```powershell
$env:DOTNET_ROOT = Join-Path $PWD 'artifacts/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PWD 'artifacts/dotnet-home'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $dotnet run --project tests/DownloadCore.Tests/DownloadCore.Tests.csproj --no-restore -c Release -- --report artifacts/downloads-slice-32-5-tests.json
& $dotnet run --project tests/ControllerHub.Tests/ControllerHub.Tests.csproj --no-restore -c Release -- --managed-only --report artifacts/downloads-controller-regression-32-5.json
& $dotnet run --project tests/ControllerReliability.Tests/ControllerReliability.Tests.csproj --no-restore -c Release -- --report artifacts/downloads-reliability-regression-32-5.json
```

Results: **100/100 download tests, 36/36 managed Controller Hub tests and 9/9
controller reliability tests passed**. Two further focused runs each passed both
legacy and SQLite process-crash cases. JavaScript syntax and Git whitespace
checks passed. Coverage includes queue
ordering, concurrency changes, deterministic backoff clocks, attempt exhaustion,
Retry-After, pause/cancel, explicit retry, reservation/collision rechecks, capacity
accounting, full-disk I/O mapping, revision conflicts, bounded events, DPAPI,
one-time/failed migration, unknown schema, corrupt records and injected write
failures. A real truncated HTTP body retries from its flushed SQLite checkpoint
and completes to the expected hash. Separate child-process kills validate both
legacy and SQLite WAL recovery without automatic network use.

During development, the backoff stop test exposed the acknowledgement race fixed
in this slice. Two early legacy child-checkpoint waits timed out; that did not
recur in the final full suites or the four focused crash repetitions. Earlier
reports remain under ignored artifacts; wider reliability acceptance is still
required. Controller results exclude packaged-helper and physical-device acceptance.

The Windows UI check used the Computer Use skill and generated loopback data:
native folder selection; save concurrency one; submit two files; observe one
Downloading and one Queued; pause the first and observe the second start; close
during transfer; reopen and verify both remain Paused with settings retained and
no new fixture requests; explicitly resume the first at byte **62,554,112**;
observe Completed; cancel the second while retaining its **11,042,816-byte**
partial. The first file is **67,108,864 bytes**, SHA-256:

`98dc891b284e4d84ac25b0c0a24fdbe39a7f0dbd643ad5e8aa06e02fc6258254`

Independent expected-pattern hashing matched the file; Zone.Identifier contains
ZoneId=3 without source URLs. Ignored evidence is in
`artifacts/download-ui-32-5/verification.json`. Generated output/partial files and
their Completed/Cancelled local records remain for inspection. Original settings
(three concurrent, five attempts, auto-start off) were restored. Test app/server
were closed. No downloaded file was executed. This also exercises the basic 32.4
visible pause/restart path that could not be tested while the desktop was locked.

Release build output (normal app files, not a standalone installer):
`C:\Users\dlova\Desktop\Codex\DK's desktop essentials\src\DKDesktopEssentials\bin\Release\net8.0-windows10.0.19041.0\DKDesktopEssentials.exe`.
Verified output: **152,064 bytes**, **Release**, Authenticode **NotSigned**. The
existing NU1900 warning remains: package vulnerability metadata could not refresh;
cached dependencies compile locally. No claim of a fresh vulnerability audit is made.

## Remaining backend, UI and acceptance

Next: **32.6 (Hybrid)** — Chat owns the full Download Manager UX/design; Codex
implements/integrates it and expands Windows acceptance. The current controls
are functional but do not provide full queue/history/event management, repair,
filename changes, explicit discard/restart-from-zero or bulk operations. Browser,
media, scheduling, antivirus/library integration and torrents remain later slices.

Backend acceptance still needs long-duration/large-file runs, independent HTTPS
servers/proxies, real disk/quota/device-removal failures, power-loss tests and
multiple supported Windows versions/architectures. Injected errors/process kills
do not certify every physical storage failure. Per-job parallel reads remain
bounded by the existing HTTP connection pool. The app must be running for queue
dispatch; no background service or OS scheduler has been added. Public-network
server and full 32.6 accessibility/acceptance testing remain pending. This slice
does not declare the download category release-ready.
