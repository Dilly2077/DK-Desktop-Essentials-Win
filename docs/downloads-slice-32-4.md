# Module 32, slice 32.4 — Validated resume and local recovery

Historical slice report. [Slice 32.5](downloads-slice-32-5.md) adds the SQLite
queue/retry backend and records successful basic Windows UI pause/restart checks,
superseding the pending manual check and minimum-journal limits recorded here.

Implemented on the user's local Windows computer on 15 October 2026. Work remains
on `codex/download-core-32-2`; the last local commit is `a4330d6` (the permanent
local-build policy). Download feature changes for 32.1–32.4 remain uncommitted.
No push, merge, GitHub Actions run or remote build was performed.

Status: **implemented and automated-tested**. Windows UI acceptance is pending:
the desktop was locked during the attempted interactive check, so app interaction
stopped. This slice is **not release-ready** or a completed Category 7.

## Changes and use

In **Downloads & Transfers → Download Manager**, a new download defaults to one
connection. The optional Connections selector requests up to two or four parallel
range reads. Pause waits for the worker to stop and flush a hashed checkpoint
before acknowledging. Resume checks both the saved bytes and the current remote
representation before appending. Cancel remains terminal and retains partial data.

Closing the app now pauses active transfers and awaits their workers. Reopening
restores paused jobs and converts interrupted in-flight jobs to Queued. Every
recovered job waits for the user's **Resume**; startup makes no HTTP requests.
Completed/failed/cancelled history also survives reopening. This supersedes the
session-only lifecycle described in the historical 32.3 report.

### Transfer rules

- `HttpDownloadEngine.cs` keeps the existing isolated HTTP(S) adapter, redirects,
  identity encoding, timeouts and credential/cookie restrictions. It validates
  `Range`/`If-Range`, exact `Content-Range` bounds, total length, response length,
  representation validator and final redirect resource identity.
- Resume requires a known unchanged length and a strong ETag, or (only without an
  ETag) Last-Modified with a conservative minimum 60-second Date separation. Weak
  ETags and ambiguous dates refuse resume. A strong ETag does not require a
  redundant Last-Modified header on a range response.
- `Accept-Ranges` alone proves nothing. Only a validated 206 (or the complete-file
  416 case) marks range support as proven. A resumed 200 is never appended. A 416
  completes only when the independently checked local prefix already equals the
  full size, the response states that same total, and identity matches.
- Changed validators, sizes or final redirect targets fail visibly while retaining
  the partial. HTTP failures retain their actionable error codes even if their
  error documents omit an ETag. Expired signed URLs may therefore require a new
  submission; credentials are not refreshed automatically.
- Parallel reads require a strong ETag and at least 2 MiB of known data. Each
  batch has at most 2/4 one-MiB chunks; the entire batch is validated before its
  chunks are written in order. The partial is always a contiguous prefix. A fresh
  transfer may fall back to a normal GET when its initial ranges are unsupported
  and nothing has been committed. Resumed transfers never use that fallback.
- The adapter flushes data before publishing a checkpoint digest (normally every
  MiB and at pause/completion). On resume, `DownloadStagingFile.OpenResume` opens
  the exact file exclusively and rehashes the committed prefix. An uncommitted
  suffix remains untouched until the remote response passes validation, then is
  discarded before appending. Cancellation trims only a potentially unreported
  in-flight write and seals the successfully hashed bytes.

These rules follow the conditional range and validator semantics in
[RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#name-if-range). The 60-second
date threshold is a deliberately conservative client policy, not a claim that
the RFC mandates that exact threshold or that every server supports resume.

### Minimum protected restart journal

`DownloadCheckpointStore.cs` stores one versioned, atomically replaced record per
job in `%LOCALAPPDATA%\DKDesktopEssentials\Downloads\Checkpoints`. The complete
record, including the user-authorized URL, paths, validators, committed byte count
and digest, is protected with Windows DPAPI **CurrentUser**. Full URLs and paths
remain absent from UI job projections, download logs and browser storage.
DPAPI protects data at rest for the Windows user; it does not isolate it from
other software running as that same user. See Microsoft's
[CryptProtectData documentation](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata).

Records are flushed to unique pending files before replacement. Startup ignores
orphan pending files, validates schema/identity/bounds, and does not interpret
uncommitted bytes as recovered progress. A single-owner lock prevents two app
instances from writing the journal. Corrupt/unavailable protected records block
new/resumed downloads without deleting records or partials; other app modules
remain available. Recovery requires the same Windows user's DPAPI context.

Before final rename, the coordinator records the independently verified full
digest. If a crash occurs after rename but before Completed is saved, startup
opens and hashes the destination to reconcile that narrow window without HTTP.
It does not assume an arbitrary existing destination means completion. Existing
directory/reparse/hard-link checks, non-replacing handle rename, fixed Internet
origin metadata and no automatic file opening remain in force.

This is the minimum restart journal for 32.4, **not** the planned SQLite queue or
the full 32.5 persistence implementation. At most three transfers run immediately;
extra active submissions are refused visibly. The coordinator retires older
terminal records as new work arrives once the retained list reaches 100, while
preserving paused/recovered jobs (up to 1,000 total records). Retirement deletes
only the job's protected metadata, never the user's output or partial file.

### Files touched by this slice

- `src/DKDesktopEssentials/Modules/Downloads/DownloadContracts.cs`: checkpoint,
  validator/resource identity, optional segment count and resume view fields.
- `DownloadRemoteIdentity.cs`: strong validator selection and saved/probed checks.
- `HttpDownloadEngine.cs`, `DownloadStagingFile.cs`: validated appending, bounded
  parallel reads, hashed checkpoints and promotion recovery.
- `DownloadCheckpointStore.cs`: DPAPI-protected, single-owner restart records.
- `DownloadCoordinator.cs`, `DownloadStateMachine.cs`: pause/resume/recovery,
  persisted transitions, explicit restart and graceful shutdown.
- `DownloadWebBridge.cs`: trusted-origin pause/resume/status commands and bounded
  connection selection; destination access still requires a native folder grant.
- `src/DKDesktopEssentials/ui/downloads.js`, `downloads.css`, `app2.js`: real
  Pause/Resume controls, connection selection and honest recovery/expiry text.
- `tests/DownloadCore.Tests/ResumeCases.cs`, `RangeFixture.cs`, `Program.cs` and
  adjusted existing expectations: adversarial response and restart integration
  coverage using generated local data only.

## Local validation

Use the repository's existing ignored .NET SDKs (8.0.426 and 10.0.402), from root:

```powershell
$env:DOTNET_ROOT = Join-Path $PWD 'artifacts/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PWD 'artifacts/dotnet-home'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $dotnet build src/DKDesktopEssentials/DKDesktopEssentials.csproj --no-restore -c Release
& $dotnet run --project tests/DownloadCore.Tests/DownloadCore.Tests.csproj --no-restore -c Release -- --report artifacts/downloads-slice-32-4-tests.json
& $dotnet run --project tests/ControllerHub.Tests/ControllerHub.Tests.csproj --no-restore -c Release -- --managed-only --report artifacts/downloads-controller-regression-32-4.json
& $dotnet run --project tests/ControllerReliability.Tests/ControllerReliability.Tests.csproj --no-restore -c Release -- --report artifacts/downloads-reliability-regression-32-4.json
```

Results: **75/75 download tests passed**, with the report saved to ignored
`artifacts/downloads-slice-32-4-tests.json`. Controller tests first encountered
sandbox-denied temporary-file/junction operations; running those local tests with
the required filesystem access passed **36/36** managed Controller Hub tests and
**9/9** reliability tests. This is not physical-controller or packaged-helper
acceptance. JavaScript syntax checking passed. Release compilation passed with
the existing NU1900 warning: NuGet vulnerability metadata could not refresh from
the package service. Cached dependencies built; no new dependency was added.
One final build attempt encountered MSB3027/MSB3021 because the idle UI test
instance still held the apphost open. Only that instance, launched for this check,
was stopped; the following local build/test run passed. The generated loopback UI
fixture was also stopped without receiving a download request.

The app EXE exists at
`C:\Users\dlova\Desktop\Codex\DK's desktop essentials\src\DKDesktopEssentials\bin\Release\net8.0-windows10.0.19041.0\DKDesktopEssentials.exe`.
Configuration: **Release**. Authenticode status: **NotSigned**. This is the normal
build output alongside its runtime/UI files, not a standalone installer/package.

Download coverage includes 200/206/416, malformed/multipart ranges, changed
validators and redirect targets, weak/ambiguous validators, tampered prefixes,
uncommitted suffixes, out-of-order parallel responses and safe initial fallback.
Windows integration tests exercise DPAPI, journal corruption and exclusive
ownership, rename recovery, real pause, graceful close and explicit restart.
A child test process is killed after a flushed checkpoint; a new coordinator
recovers without HTTP and explicitly resumes to the expected final SHA-256.
These generated loopback fixtures use no external site or user download.

## Remaining work and acceptance

- Backend next: **32.5 (Codex)** — durable queues, configurable concurrency,
  retry/backoff, full persistence/migration policy, collision and capacity/error
  handling. Failed jobs currently have no retry/restart-from-zero action; retained
  partials are not automatically deleted. No SQLite runtime has been introduced.
- UI next: complete 32.6 queue/history management, retry/restart and journal repair
  flows. The current minimal controls cover this slice but do not provide the
  full queue editor, schedules, bandwidth controls or browser/media integration.
- Manual Windows acceptance pending: submit generated data through the native
  picker, pause and resume through visible controls, close/reopen during transfer,
  confirm no automatic request, resume to a known hash, and inspect both 2/4
  connection selection and unavailable/corrupt journal messaging. The attempted
  UI check stopped at the locked desktop before submitting a download.
- Wider acceptance pending: independent HTTP(S) servers/proxies, clock behavior,
  signed/rotating redirect links, long-running/large downloads, power loss and
  real storage failures. Process-crash tests do not establish power-loss durability
  for every filesystem or device. Some safe servers will be refused by the strict
  validator/redirect rules. No universal resume/acceleration guarantee is made.
- Module 36 antivirus/library/security acceptance and release packaging remain
  separate. Verification establishes local consistency; only an independently
  supplied expected digest can establish the intended file's checksum.
