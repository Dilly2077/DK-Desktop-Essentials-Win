# Module 32, slice 32.3 — Direct HTTP/HTTPS downloads

Historical slice report. [Slice 32.4](downloads-slice-32-4.md) adds protected local
recovery and pause/resume, superseding the session-only lifecycle described here.

Implemented and tested locally on Windows on 14 October 2026. Work continues on
`codex/download-core-32-2` from `faf59e01e3c532d16582d69e7bf9af4aad8e402d`;
32.1–32.3 changes are currently uncommitted. No push, remote CI or merge was run.

## What works

Open **Downloads & Transfers → Download Manager**. Paste a direct HTTP/HTTPS URL,
enter a Windows filename, choose a destination through the native folder picker,
optionally supply an expected SHA-256, and select **Download**. The session list
shows state, byte progress, average speed and estimated time remaining. Unknown
size/ETA remain visibly unknown. Cancel waits for worker acknowledgement; closing
the app cancels and awaits active downloads before disposing the HTTP adapter.

The folder picker returns an opaque native grant. Enqueue commands cannot grant
themselves arbitrary filesystem access by supplying a path. Full source URLs,
source keys and staging/destination paths never appear in job views. URL path and
query are not used to derive a public filename. The user supplies the filename
(default `download.bin`), and submitted URLs are cleared from the form. Nothing
is written to localStorage, logs or a database for downloads.

This coordinator is explicitly **session-only**. Up to three submitted transfers
can run immediately. Additional active work is refused visibly instead of
silently queued; there is one attempt and no automatic retry. The latest 100 job
records are retained in memory, with older finished records retired as new work
arrives. Files are never removed by retirement. Restart does not recover jobs or
partial files; the UI states this before submission. These are implementation
limits of 32.3, not the final queue design from 32.1/32.5.

## Files and safety

- `Modules/Downloads/DownloadCoordinator.cs`: owns worker tasks, session records,
  lifecycle transitions, destination reservations, cancellation, redacted views,
  progress/speed/ETA and bounded state-change subscriptions. Uses the existing
  `IDownloadEngine` and `IDownloadCoordinator`; no separate transfer engine.
- `DownloadStagingFile.cs`: reacquires root-to-leaf directory locks and opens
  the exact staging file exclusively for verification; rejects reparse files and
  hard links. Checks file length, independently rehashes actual bytes against the
  adapter result and optional expected hash. Flushes data, adds fixed Internet
  origin metadata (`ZoneId=3` without source/referrer URLs), and renames the
  verified handle without replacement. Completion is published only after the
  successful rename. Failed/cancelled partials are retained, never executed.
- `DownloadWebBridge.cs`: `downloads:chooseDestination`, `enqueue`, `list`, `get`
  and `cancel`; validates the sender against the existing trusted local origin
  policy and bounds/parses requests. Fixed safe errors contain no transport
  exception text, signed links or filesystem paths. Unsupported commands return
  an honest failure. Native selection remains pending until confirmed/cancelled.
- `DownloadContracts.cs`: adds optional speed/ETA fields to the redacted job view
  and redacts CreateDownloadRequest.ToString. `DownloadEngineException.cs` now
  provides fixed actionable descriptions for the existing typed error codes.
- `MainWindow.xaml.cs`: creates the coordinator/bridge, invokes WPF OpenFolderDialog,
  dispatches only after the existing origin/shutdown gate, and disposes downloads
  before controller shutdown. Origin, CSP and outbound WebView blocking remain.
- `ui/downloads.js`, `downloads.css`, `app2.js`, `index.html`: basic accessible form,
  navigation, local native-message polling and safe textContent job rendering.
  No inline script, remote resources, raw engine controls, credential fields,
  automatic file opening, fake pause/resume controls or automatic capture.
- `tests/DownloadCore.Tests/CoordinatorCases.cs` and `Program.cs`: integration,
  verification, cancellation, admission and native bridge tests.

Rename implementation uses Windows
[SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle)
with [FILE_RENAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info),
with replacement disabled. Windows safety metadata requires a local volume which
supports alternate streams; unsupported destinations fail without promoting the
partial file. This is not antivirus scanning or a claim that a downloaded file is
safe. A checksum without an independently supplied expected value establishes
local transfer consistency, not publisher authenticity.

## Validation

Release app compilation passed locally. The existing NU1900 warning remains:
NuGet vulnerability metadata could not refresh; cached dependencies built and no
new dependency was added. A rebuild during UI testing briefly retried an apphost
copy while the test instance was closing; the build succeeded. Final tests ran
after that instance exited.

From repository root, using the repository-local SDK:

```powershell
$env:DOTNET_ROOT = Join-Path $PWD 'artifacts/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PWD 'artifacts/dotnet-home'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $dotnet run --no-restore --project tests/DownloadCore.Tests/DownloadCore.Tests.csproj -c Release -- --report artifacts/downloads-slice-32-3-tests.json
& $dotnet run --no-restore --project tests/ControllerHub.Tests/ControllerHub.Tests.csproj -c Release -- --managed-only --report artifacts/downloads-controller-regression-32-3.json
& $dotnet run --no-restore --project tests/ControllerReliability.Tests/ControllerReliability.Tests.csproj -c Release -- --report artifacts/downloads-reliability-regression-32-3.json
```

Results: **49/49 download tests, 36/36 managed Controller Hub tests and 9/9
reliability tests passed (94 total).** Tests include actual loopback HTTP transfers,
exclusive Windows verification/rename, Internet origin metadata, checksum failure,
forged engine success, a late rename collision, cancellation, concurrent shutdown,
unknown size, speed/ETA, duplicate active destinations and native destination grants.
Packaged helper and physical controller tests were not rerun for this download slice.

The actual Release WPF/WebView2 app was also exercised through its UI:

1. Navigate category/tool and inspect the rendered form and session limits.
2. Open native folder picker; select `artifacts/download-ui-check`.
3. Submit a generated localhost text fixture as `ui-verified.txt`.
4. Observe Downloading, live bytes/speed/ETA and Cancel, then Completed.
5. Independently confirm the output is **612,000 bytes**, SHA-256
   `d1a476d134e486d0b381a914d7eeb9421e1b5b7ca168d8991afb1a4110dba4a6`,
   matching the fixture, with `ZoneId=3` retained.

UI testing found and fixed a 30-second timeout incorrectly applied to the native
folder picker. The first fixture server was inaccessible across the execution
sandbox; the app displayed a failed job without terminating. Serving the generated
fixture outside that sandbox completed successfully. Both fixture servers and the
test app were closed afterwards. The output/report remain under ignored artifacts.
UI cancellation itself was not manually clicked; acknowledgement and file retention
are covered by automated coordinator tests. Public Internet/TLS interoperability,
slow/full disks, non-NTFS volumes and a full security assessment remain unverified.

## Completion and next slice

32.3 direct submission, basic transfers, progress, errors and cancellation are
implemented and tested. The basic functional UI is connected; the full Download
Manager design and acceptance suite remain 32.6. This is not a release-ready
Category 7 or a durable download manager.

**Next: 32.4 (Codex)** — validated HTTP ranges, pause/resume, optional parallel
segments and restart recovery. Inspect 200/206/416 handling and strong validators;
never append a full or changed entity to a partial file. Recovery requires
durable source/partial metadata, so introduce and test the minimum protected
checkpoint needed by that slice rather than claiming this session store survives
restart. 32.5 then completes the transactional store, queues, retry/backoff,
configurable concurrency and collision/disk policies. Browser/media features
remain separate Modules 33/34.
