# Module 32, slice 32.2 — HTTP engine adapter

Implemented locally on 14 October 2026 on `codex/download-core-32-2`.
Changes are uncommitted; starting commit: `faf59e01e3c532d16582d69e7bf9af4aad8e402d`.
The imported 32.1 baseline is `feature/downloads-slice-32-1` at
`d55ac00fa6206c975004f13ba82af92ede4105c1`. Project orientation, source documents,
roadmap and AGENTS.md were imported from main at `dd8b26243d5cd144a4fd22f7bd360d52778a7911`.
Existing controller security changes are preserved.

## Engine decision

Use `HttpDownloadEngine`, implementing the existing `IDownloadEngine`. This is
an in-process module isolated by an interface, not an operating-system process
sandbox. No new package, executable, service, listener, installer, elevation or
automatic update is introduced. Construction and application startup perform no
download. The next coordinator owns a single engine and disposes it after its
operations have stopped:

```csharp
using var ownedEngine = new HttpDownloadEngine();
IDownloadEngine engine = ownedEngine;
// Call ProbeAsync/TransferAsync only for an explicitly authorised job.
```

The WPF project includes these files through its existing compile glob. No
controller code, local WebView origin/CSP policy or UI routing has been changed.
An adapter-level transfer is exercised to prove the integration; the user-facing
submission/coordinator flow still belongs to 32.3.

| Candidate | Fit and packaging assessment | Decision |
|---|---|---|
| .NET HttpClient/SocketsHttpHandler | Already part of the host runtime; streaming/cancellation and replaceable transport boundary. Runtime source is MIT-licensed; preserve applicable .NET licence and third-party notices when distributing the runtime. Range validation, segmentation and durable queue ownership remain application work. | Selected for the first HTTP(S) implementation. |
| aria2 | Mature multi-protocol transfer engine with segmentation and JSON-RPC. Introduces a separately versioned Windows binary, build-specific library licences, process/RPC ownership, secret/config/log handling and an additional queue/control-file lifecycle. GPL redistribution requires an explicit notice/source compliance plan before bundling. | Evaluated, not bundled or installed. Reconsider behind IDownloadEngine for 32.4/37 if justified. |
| Windows BITS | Windows background transfer component with COM/service integration and transfer job lifecycle. No separate third-party engine binary; would require a Windows-specific adapter and reconciliation with DK's opt-in queue/restart model. | Reasonable alternative for service-backed transfers; not selected for the initial in-process lifecycle. |

This is an engineering choice, not a speed comparison. No acceleration or universal
server/resume compatibility is claimed. Primary sources checked 14 October 2026:
[HttpClient lifetime guidance](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines),
[.NET runtime licence](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT),
[aria2 manual](https://aria2.github.io/manual/en/html/aria2c.html),
[aria2 COPYING](https://github.com/aria2/aria2/blob/master/COPYING),
[BITS overview](https://learn.microsoft.com/en-us/windows/win32/bits/about-bits).
No aria2 release binary/version is approved for future distribution by this evaluation.

## Implemented boundary

- `HttpDownloadEngine.cs`: HEAD metadata probe, headers-only GET fallback on
  405/501, fresh streaming GET, byte progress and SHA-256 result. One owned,
  pooled HttpClient; 30-second connect/header/idle-read timeouts; caller cancellation.
- Explicit HTTP/HTTPS only. Reject embedded user credentials and fragments.
  At most five redirects; validate every target, reject HTTPS downgrade and
  do not forward cookies, authorization or referrer. Use OS TLS validation.
  Compression is not decoded; unexpected content encoding is rejected.
- Return `Unsupported` resume capability for this adapter version regardless
  of Accept-Ranges. Reject nonzero resume offsets and unsolicited 206/416;
  never append partial content or silently restart over an existing staging file.
- Check transfer response length and available ETag/Last-Modified against the
  probe; check actual bytes against known expected length. Unknown remains null.
  Unsafe server filenames are discarded. Hash computation does not mark a job
  verified, compare an expected hash, rename a file or change persisted state.
- `DownloadStagingFile.cs`: existing local destination directory only; reject
  UNC/device paths, ADS, dot traversal, Windows reserved names and reparse
  directories. Staging must be in the same directory and named
  `.{jobId:N}.dkdownload.part`. Lock directory ancestors against rename for the
  attempt; create staging with CreateNew and exclusive sharing. Existing
  destination/staging files are preserved. No file is automatically executed,
  opened, deleted or promoted to its destination. The future coordinator must
  recheck paths/collisions and finalise after verification with fresh leases.
- `DownloadEngineException.cs`: stable error code with fixed safe text and no
  original exception retained. Caller cancellation stays cancellation; timeout,
  HTTP, DNS, TLS, truncated body and filesystem failures have typed mappings.
- `DownloadContracts.cs`: fix missing System.IO import discovered by local
  compilation; redact DownloadSource.ToString to reduce accidental URL logging.
- Tests use an internal transport constructor, exposed only to DownloadCore.Tests.
  Production HTTP policy has no UI-configurable handler/flags/headers.

The protected URL vault and job store are still interfaces/schema only. URLs
exist transiently in native engine requests; this slice never persists or logs
them. In-process code is trusted; hostile code already executing as the same user
is not a supported isolation boundary. Junction/path tests validate ordinary
filesystem substitution controls, not a complete penetration assessment.

## Local validation

The repository-local SDK is `artifacts/dotnet/dotnet.exe` (.NET SDKs 8.0.426 and
10.0.402). No remote CI was requested or triggered. Commands from repository root:

```powershell
$env:DOTNET_ROOT = Join-Path $PWD 'artifacts/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PWD 'artifacts/dotnet-home'
& "$env:DOTNET_ROOT/dotnet.exe" run --project tests/DownloadCore.Tests/DownloadCore.Tests.csproj -c Release -- --report artifacts/downloads-slice-32-2-tests.json
& "$env:DOTNET_ROOT/dotnet.exe" build src/DKDesktopEssentials/DKDesktopEssentials.csproj -c Release
& "$env:DOTNET_ROOT/dotnet.exe" run --project tests/ControllerHub.Tests/ControllerHub.Tests.csproj -c Release -- --managed-only --report artifacts/downloads-controller-regression.json
& "$env:DOTNET_ROOT/dotnet.exe" run --project tests/ControllerReliability.Tests/ControllerReliability.Tests.csproj -c Release -- --report artifacts/downloads-reliability-regression.json
```

Final Release validation: **32/32 download tests, 36/36 managed Controller Hub
tests and 9/9 controller reliability tests passed**. The WPF Release build passed
with zero errors. Reports are the three `artifacts/*.json` files named above.
Packaged helper/physical controller tests were not rerun for this slice.
Download tests include
the ten imported lifecycle/projection cases, injected HTTP/error cases, real
Windows junction/rename/file-sharing checks and a real loopback HTTP server.
Tests use only local generated data; no Internet file is downloaded by the suite.
The loopback and junction tests require ordinary local Windows access outside the
restricted execution sandbox. The initial sandbox attempt failed on those access
restrictions; the unrestricted local run passed all 32 cases, including idle-read
timeout. NuGet vulnerability lookup reported **NU1900** on both sandboxed and
unrestricted restore/build attempts. Cached dependencies sufficed for compilation;
their vulnerability status was not freshly verified. This is the only Release
build warning. No dependency was added or version changed.

## Handoff and completion limits

32.2 provides an implemented engine adapter; this is not a usable download manager
or a release-ready Category 7. UI/manual Internet/TLS interoperability acceptance
has not been performed. There is no native downloads bridge or download page,
source vault, queue, durable store, final verification/finalisation coordinator,
resume, segmentation, retry policy, browser capture, media or optional protocols.
Disk-full/permissions are mapped, but physical drive-full testing and exhaustive
security/restart acceptance remain for later slices.

**Next: 32.3 (Codex)** — add explicit direct URL submission and destination
selection, coordinator ownership, usable progress/speed/ETA and cancellation/error
reporting. Create job-specific staging names and preserve verification-before-
completion. 32.4 handles validated ranges/resume; 32.5 handles durable queues,
concurrency and retry/collision policies; 32.6 integrates the full Download Manager
UI and Windows acceptance. Continue using this interface and one shared job store.
