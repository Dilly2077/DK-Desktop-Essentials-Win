# Module 32, Slice 32.6B — native integration and Windows acceptance

Updated: 25 October 2026. Scope: Module 32 only.

**Implemented:** the 32.6A interface is connected to the real WPF/WebView2 host,
coordinator, HTTP engine and protected persistent queue. **Automated-tested:**
111/111 download tests and 31/31 frontend tests; existing controller regressions
9/9 and 36/36. **Manual Windows-tested:** the native scenarios below.
**Release-ready: no.** U24's complete content-width range, U25 at 150%/200%,
and U29 with a spoken screen reader remain unverified. Windows blocks display
changes in this remote session. These are acceptance gaps, not passing results.

## Integration and source preservation

The working branch is `codex/download-core-32-2`, based at
`a4330d6c094bbc01697d0912584ca21c6f8ba164`. Frontend source came from
`origin/feature/downloads-slice-32-6a-ui` at
`cf78435d14e3d674d008db845ee8090c981666f4`. Selected UI, tests and handoff
documentation were imported; the divergent frontend branch was not merged over
the existing uncommitted 32.2–32.5 implementation or root policy.

Before integration, 43 existing files, hashes and the tracked diff were saved in
`artifacts/before-32-6b-33c47f7531a2433c86b000ca8731e810/`. Extracted frontend
source remains under `artifacts/frontend-32-6a-cf78435/`. Both are ignored local
artifacts. At the time of the original integration no commit, push or PR had been created. Much of the earlier
download implementation was already untracked; Git status alone cannot assign
all those files to 32.6B.

### Files changed for this slice

| Files | Purpose |
|---|---|
| `src/DKDesktopEssentials/MainWindow.xaml.cs` | Secure downloads routing, native picker, subscribed refresh notifications, orderly shutdown; internal coordinator/profile injection for isolated acceptance |
| `src/DKDesktopEssentials/Properties/AssemblyInfo.cs` | Friend access for the separate acceptance executable |
| `src/DKDesktopEssentials/Modules/Downloads/DownloadContracts.cs` | Held-queue intent, action capabilities and coordinator commands |
| `src/DKDesktopEssentials/Modules/Downloads/DownloadCoordinator.cs` | Start/remove/reveal, persistent held jobs, unsafe continuation gates, stable newest-first ordering |
| `src/DKDesktopEssentials/Modules/Downloads/DownloadWebBridge.cs` | Handshake, bounded correlated command contract, native folder grants, safe responses |
| `src/DKDesktopEssentials/Modules/Downloads/DownloadStagingFile.cs` | Verified folder reveal, read-only completed-file handles, validated extended Win32 paths for long filenames |
| `src/DKDesktopEssentials/Modules/Downloads/DownloadFolderOpener.cs` | Fixed Explorer executable with a validated directory argument |
| `src/DKDesktopEssentials/ui/downloads-core.js`, `downloads-mock.js`, `downloads-ui.js`, `downloads.css` | Imported 32.6A design, native action mapping, dialogs, focus/selection retention, paging, unchanged-snapshot suppression; narrow rows retain byte totals |
| `src/DKDesktopEssentials/ui/downloads-native.js` | Native adapter, projection, capability handshake, request timeout/correlation, subscriptions and disposal; explicit development demo |
| `src/DKDesktopEssentials/ui/app2.js`, `index.html` | Existing-shell navigation/card keyboard support and bundled asset loading |
| `tests/DownloadCore.Tests/NativeIntegrationCases.cs`, `Program.cs` | Native contract, held persistence, remove retention, unsafe resume, folder reveal, large queue, long filename and ordering regressions |
| `tests/DownloadUI.Tests/downloads.test.cjs`, `native.test.cjs`, `render.test.cjs` | Pure/mock tests, native transport/projection tests and a minimal rendering harness |
| `tests/DownloadHost.Acceptance/DownloadHost.Acceptance.csproj`, `Program.cs`, `README.md` | Separate generated-data fixture using the production native host, UI and download stack |
| `docs/downloads-slice-32-6a.md`, this report, `docs/roadmaps/downloads-and-transfers.md`, `README.md` | Handoff provenance, actual status, limitations and reproduction |

The old `ui/downloads.js` is retained but is no longer loaded. No new dependency,
telemetry, advertising, account requirement or other downloads module was added.

## Native command contract and privacy boundary

Requests are `{type:"downloads:<verb>", requestId, payload}`. Replies are
`{type:"downloads:response", command, requestId, ok, result, error, errorCode}`.
Only the existing trusted local document origin is accepted. Messages are limited
to 32 KiB; request IDs are bounded ASCII identifiers and command suffixes are
bounded ASCII letters. Malformed input is ignored or receives a static safe error.

`capabilities` must return protocol version 1, mode `native`, event support,
protected destinations and boolean action capabilities before the adapter mounts.
An unavailable host shows an error with Retry connection. Demo records appear
only after selecting **Development demo (simulated)**; demo never transfers data.

`subscribe {enabled}` controls 750 ms refresh notifications. Notifications contain
only `{type:"downloads:event",reason:"refresh"}`; the adapter fetches redacted
views. Leaving the page unsubscribes, aborts pending work and removes listeners.
Unchanged snapshots do not redraw hundreds of rows or disrupt keyboard focus.

`chooseFolder` (and the older `chooseDestination` alias) opens the Windows picker,
validates an existing ordinary local directory and returns an opaque
`destinationToken` plus its display name. **It does not return an absolute path.**
This deliberately tightens the 32.6A proposed path-bearing contract. `enqueue`
accepts the granted token, explicit HTTP(S) URL, filename and `startNow` flag.
Fabricated tokens and arbitrary frontend paths are rejected. Directory safety is
checked again when opening files; a picker grant cannot bypass backend validation.

`list`, `get`, `start`, `pause`, `resume`, `cancel`, `retry`, `remove`, `openFolder`,
`getSettings`, `setSettings` and `status` reach the existing coordinator. Per-job
capabilities are authoritative. Retained partials without provable remote identity,
or with invalid/changed identity or content, cannot resume/retry through forged
commands. **Add to queue** persists held intent and stays held across restart even
if automatic launch dispatch is enabled. **Start download** requests dispatch.

Cancel waits for the worker to acknowledge cancellation. Remove forgets metadata
and the protected source entry; both actions retain completed/partial files.
Open folder requires a completed verified record and a safe existing file, checks
its current size and directory chain, then starts fixed Windows Explorer with the
directory as an argument. It never executes the downloaded file. Read-only
completed files can be revealed. Reparse points, hard links, UNC paths, device
paths supplied by callers and destination collisions remain rejected. Internal
extended-length paths are constructed only after ordinary local-path validation.

Views contain host, chosen filename, state, counters, speed, retry/queue data,
static errors and capabilities. They contain no full URL, source key, query,
credentials, signed path, absolute destination or engine exception. The JS adapter
projects allowed fields again and maps errors through an own-property whitelist.
Native filenames default to `download.bin` rather than deriving a secret-bearing
URL path. Host and user-chosen filename remain intentionally visible.

Source URLs are retained in a current-user DPAPI-protected local store for restart;
they are not persisted as plaintext. They necessarily exist in the intended input
and native process memory during use. This is not protection against the same
Windows user, an administrator or process-memory inspection. No packet capture or
complete audit of WebView2 runtime background traffic was performed. Bundled page
assets have no external fetch requirement; CSP and host resource policy reject
external page resources. No source URLs were added to frontend logs.

## Local builds and automated tests

All compilation and testing ran on the Windows computer hosting this workspace.
Installed local SDKs: 8.0.426 and 10.0.402 under ignored `artifacts/dotnet/`.
The bundled local Node runtime was used. No GitHub Actions run was dispatched.
The checked-in Windows workflow remains manual-only with the explicit
`authorize_remote_build == true` guard. Remote repository settings/default-branch
workflows were not audited; this is not a claim that all Actions are disabled.

| Local check | Result | Local evidence |
|---|---|---|
| Download contracts/engine/coordinator/recovery/native integration | 110/110 | `artifacts/downloads-32-6b-core-final.json` |
| Frontend pure/mock/native transport/rendering | 31/31 | `artifacts/downloads-32-6b-frontend-final.tap` |
| JS syntax: core, mock, UI, native, app2, workspace rail | Passed | Local `node --check` calls |
| Controller reliability regression | 9/9 | `artifacts/downloads-32-6b-controller-reliability-final.json` |
| Controller Hub managed regression | 36/36 | `artifacts/downloads-32-6b-controller-hub-final.json` |
| WPF production app + acceptance host Release build | Passed, zero errors | Local MSBuild output |
| Whitespace diff check | Passed | `git diff --check` |

The minimal JS renderer harness tests generated markup and lifecycle behavior; it
does not emulate browser layout, native accessibility, Windows DPI or real HTTP.
Native integration tests include real Windows filesystem/DPAPI/SQLite boundaries
and controlled engines; the Windows fixture below supplies real HTTP transfers.
Controller hardware and packaged-helper tests were not rerun for this slice.

Reproduction (use local SDK/Node paths if they are not on PATH):

```powershell
$env:DOTNET_ROOT = Join-Path (Get-Location) 'artifacts\dotnet'
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) 'artifacts\dotnet-home'
& "$env:DOTNET_ROOT\dotnet.exe" run --project tests/DownloadCore.Tests -c Release --no-restore -- --report artifacts/downloads-32-6b-core-final.json
node --test tests/DownloadUI.Tests/downloads.test.cjs tests/DownloadUI.Tests/native.test.cjs tests/DownloadUI.Tests/render.test.cjs
& "$env:DOTNET_ROOT\dotnet.exe" run --project tests/ControllerReliability.Tests -c Release --no-restore -- --report artifacts/downloads-32-6b-controller-reliability-final.json
& "$env:DOTNET_ROOT\dotnet.exe" run --project tests/ControllerHub.Tests -c Release --no-restore -- --managed-only --report artifacts/downloads-32-6b-controller-hub-final.json
& "$env:DOTNET_ROOT\dotnet.exe" build tests/DownloadHost.Acceptance/DownloadHost.Acceptance.csproj -c Release --no-restore
```

Builds emitted NU1900 because NuGet vulnerability metadata could not be refreshed.
Cached dependencies built successfully; no fresh vulnerability audit is claimed.

Verified application artifact, Release and **NotSigned**:

`C:\Users\dlova\Desktop\Codex\DK's desktop essentials\src\DKDesktopEssentials\bin\Release\net8.0-windows10.0.19041.0\DKDesktopEssentials.exe`

It is a framework-dependent build, not an installer or portable single-file
package. Keep its adjacent UI/runtime files; .NET 8 and WebView2 are required.

## Actual Windows acceptance

The separate acceptance executable uses the production MainWindow, WebView2 page,
bridge, coordinator, HTTP engine, DPAPI and SQLite with isolated queue/WebView
profiles. Its loopback HTTP server serves generated deterministic bytes, ranges,
ETags, delayed streams and controlled errors. Test controls exist only in this
separate executable, never in the production bridge. See
[the fixture instructions](../tests/DownloadHost.Acceptance/README.md) for reproduction.

Retained evidence: `artifacts/downloads-32-6b-acceptance-20261025/`, including
redacted snapshots, request method/range-offset records and generated files. The
server address was `http://127.0.0.1:55242/`; no external download service or user
credentials were used. Native HTTP is real; the content is generated test data.
DiskFull was a controlled zero-available-space preflight, not a physically full
drive. No production queue data was removed during this resumed acceptance run.

| ID | Outcome and evidence |
|---|---|
| U01 | Passed: existing Command Center category/card opens native manager. |
| U02 | Passed: connecting/loading shown before the native list. |
| U03 | Passed: isolated empty queue shows empty state and enabled New download. |
| U04 | Passed: actual HTTP 1 MiB and 64 MiB transfers, bytes/progress and SHA-256 verified. |
| U05 | Passed: alpha/beta delayed streams simultaneously advance with distinct bytes, speed and ETA. |
| U06 | Passed: starting one held job leaves others held; automated persistence also covers auto-start preference. |
| U07 | Passed: beta paused, app closed/reopened, resumed with actual Range offset 34635776 and completed correctly. |
| U08 | Passed: no-validator partial has visibly disabled Resume and explanation; forged command rejected by native test. |
| U09 | Passed: real Cancel retains partial; user-approved Remove entry deletes only metadata; same partial size/hash afterwards and record stays absent after restart. |
| U10 | Passed: real HTTP 404 has safe error; Retry after server recovery completes. |
| U11 | Passed: Open folder opens Windows Explorer containing completed-fast.bin; no file execution. Read-only/hard-link cases automated. |
| U12 | Passed: malformed input rejected in actual dialog before submission. |
| U13 | Passed: file:// rejected in actual dialog; native scheme policy automated. |
| U14 | Passed: fabricated embedded username/password URL rejected locally; no network request. |
| U15 | Passed: actual chunked response shows bytes and speed, unknown size and no fabricated percentage/ETA. |
| U16 | Passed: held/paused/completed records show unknown speed/ETA; deterministic missing-speed cases pass. |
| U17 | Passed: 240-character filename renders without overflow and actually completes with full name on disk; long combined path supported. |
| U18 | Passed: case-insensitive SLOW matches two real queue records. |
| U19 | Passed: native Queued filter count 250 and paging agree; empty filter behavior automated. |
| U20 | Passed: 250 held persistent jobs, 50 initially rendered, Show next 50 displays 100; no held job HTTP dispatch. No formal frame-time benchmark. |
| U21 | Passed: actual dialog + folder grant + Start download produces form-submitted.bin with verified content. Held queue dispatch/persistence tested natively. |
| U22 | Passed: real Windows picker selects local output directory; frontend shows basename only. Picker cancellation/forged grant automated. |
| U23 | Passed within scope: cold history/UI loads from bundled local assets without an external asset service; source/CSP/resource policy reviewed. No full runtime traffic audit. |
| U24 | Partial: actual host minimum 1180-wide window (about 650px usable list), dialog, wide window and maximize checked; narrow byte totals fixed. Complete 320–700px content-width sweep and border-drag resize not certified. |
| U25 | Partial: native DPI snapshots confirm 100% and 125%, with readable controls/dialogs. 150%/200% blocked: Windows Display settings states it cannot be changed from a remote session. |
| U26 | Passed: Tab/Shift+Tab, visible focus and dialog containment checked through actual Windows input. |
| U27 | Passed: Enter and Space activate focused New download; card keyboard support implemented. |
| U28 | Passed: Escape closes dialog before page navigation and restores New download focus. |
| U29 | Partial: Windows accessibility tree exposes names, states, progress and toggle filters; actual Narrator/NVDA spoken review not performed. |
| U30 | Automated + partial manual: malformed/mismatched replies, unavailable handshake, timeout and disposal pass transport tests; real native HTTP errors are honest. Native host timeout was not forced through the Windows UI. |
| U31 | Automated: hostile filename markup is escaped in the actual renderer; malformed paths/filenames rejected natively. No claim of executing an attack in the native browser. |
| U32 | Passed: leave and return re-handshakes with correct persistent counts; listener/subscription disposal automated. |
| U33 | Passed: real close/relaunch preserves paused beta identity/34635776 bytes and removed alpha absence; resumed output hash matches. Abrupt-process recovery also covered by backend tests. |
| U34 | Passed within fixture scope: real HTTP ranges and changed ETag reject unsafe continuation; retained hash unchanged. Controlled zero-space preflight shows DiskFull, then Retry succeeds after space restored; physical disk exhaustion not attempted. |

Selected independent file checks:

| Generated file | Evidence |
|---|---|
| Completed 1 MiB outputs | SHA-256 `631B84027D6B9E52B539C4E8373622D23032DFADC64D60AF87339C9037E4F769` |
| beta-slow.bin, 64 MiB after restart/resume | SHA-256 `98DC891B284E4D84AC25B0C0A24FDBE39A7F0DBD643AD5E8AA06E02FC6258254` |
| Removed alpha partial, 13533184 bytes | SHA-256 `CA97C5115DC7D9510DBA224655A5B81CB064325650C82C0B4C6FA024A74D5736`, unchanged after metadata removal |
| Changed-entity partial, 17891328 bytes | SHA-256 `E008D5C3CBEFFC83206870FCADDE9BF6DB0412D841048C51548464C903C42AC7`, unchanged after refused resume |

Completed output has a Windows Zone.Identifier with ZoneId=3 and no source URL.
Final file hashes, removed-record absence, redaction checks and executable signing
status are recorded in `artifacts/downloads-32-6b-verification-final.json`.
The redacted fixture snapshot contains neither the supplied query token nor a
full HTTP URL. ETag v2 probing did not issue a new body request into the old
partial. Artifacts are retained for inspection rather than automatically deleted.

## Remaining work, limitations and release decision

### Follow-up audit, 31 October 2026

Reviewed the native command boundary, source validation and redirects, queue
admission/removal/requeue and frontend projection/rendering. Fixed a concurrency
race: after waiting for a worker, requeue now verifies that the original entry is
still in the queue before changing it. A concurrent removal can no longer leave a
detached entry being requeued. The local download regression suite passed 110/110
(`artifacts/downloads-32-audit-20261031.json`). A subsequent deterministic test
holds the previous worker, removes the queued record while Start is waiting, then
checks that neither dispatch nor recovery resurrects it; the complete suite passed
111/111 (`artifacts/downloads-32-final-regression.json`). NuGet vulnerability metadata
could not be refreshed (NU1900). This focused review is not a complete security audit.
The previous manual acceptance gaps below have not been newly signed off.

On a subsequent retry Windows allowed selection of 150% scaling. The running
acceptance app displayed the download list and new-download dialog legibly at
that setting, but its WPF snapshot still reported DPI 1.0 after the change, so
this is not a confirmed per-monitor DPI acceptance pass. Windows offered 100%,
125%, 150% and 175%, not 200%. The original 100% setting was restored. Full
width/resize and spoken screen-reader checks remain outstanding.

A later fresh launch with Windows set to 150% reported WPF DPI 1.5. The
download list was readable, but the top of the acceptance window was outside
the captured desktop; this needs investigation and is not a full 150% pass.
The original 100% Windows setting was restored. The 200% and spoken-reader
checks are still outstanding.

Native integration has no known failing automated test or unhandled defect in the
exercised scenarios. The complete manual matrix is **not signed off**. Finish the
remaining width/resize, 150%/200% and spoken-reader checks on a local interactive
Windows desktop; rerun any affected scenarios after fixes. Long-duration transfers,
public HTTPS/proxy behavior and real hardware storage failures have not all been
exercised in this UI acceptance session.

The current UI covers direct HTTP(S) links, not authenticated browser sessions,
cookies, media extraction, scheduling or torrents. Backend settings and optional
parallel segments remain backend capabilities rather than a full settings editor
in this view. Expected SHA-256 is supported by the backend but has no form field.
Queue safety caps remain: old Completed/Cancelled history can be retired on new
admission from 100 records, with a 1000-record hard limit; retirement never deletes
files. The UI renders 50 rows per page.

Cancel/Remove deliberately leave files consuming disk space. There is no automatic
discard/repair workflow. Folder reveal validates the current file's size/type and
locks its directory chain, but does not rehash the entire file, scan for malware
or certify safety. MOTW and a successful checksum are not malware approval.

No remaining native backend or basic action wiring is deferred to Module 33.
Module 32 can now be exercised as a native direct-download candidate, but its
first usable release should remain withheld until the outstanding manual gates
above pass. Category 7 as a whole remains planned beyond this module.
