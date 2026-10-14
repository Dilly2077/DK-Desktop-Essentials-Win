# Codex task — complete Controller Hub security remediation

Implementation on latest main: see [issue #12 implementation and remaining acceptance limits](controller-hub-security-remediation.md). The instructions below are the original brief; PR #11's branch work is already merged. Production driver/hardware certification remains pending.

**Work against:** `Dilly2077/DK-Desktop-Essentials-Win`, branch `security/controller-hub-remediation-2026-10-14` (rebase onto main as necessary). **Audit source:** `docs/controller-hub-security-audit-2026-10-14.md` from audit PR #10. **Do not merge unsafe or failing changes**.

## Work already completed in this branch

- `ControllerHubOriginPolicy.cs`: exact HTTPS `dk.local` origin checks, no credentials or non-default ports, document restricted to SPA.
- `MainWindow.xaml.cs`: validate WebMessageReceived.Source, restrict document/frame navigation and external WebView requests, catch async native-handler exceptions; native confirmation for driver install and global maintenance.
- `ui/index.html`: restrictive CSP.
- `ControllerHubWebBridge.cs`: structured cancellation/network/unexpected exception handling, explicit global-cleanup acknowledgment.
- `ControllerProfileStore.cs` and `ControllerHubService.cs`: portable profile exports by default, explicit opt-in for controller-bound identifiers.
- `tests/ControllerHub.Tests`: origin, failure, portable-export and global-cleanup regression coverage.

**First verify the current branch passes `scripts/Test-ControllerHub.ps1` on Windows CI. Fix any compile/test failures before proceeding.**

## Remaining implementation — highest priority

### 1. SEC-01: Privileged SDK loading trust boundary

Current `HidMaestroPackageManager.RunElevatedHelperAsync` passes an SDK path under current user's `%LOCALAPPDATA%` to a `runas` helper. `DKControllerOutputHost/Program.cs` loads it using `Assembly.LoadFrom` **before** installing/repairing/removing drivers. Hash checking of the downloaded ZIP during normal-user installation is insufficient; the extracted DLL remains user-writable.

Implement a secure, TOCTOU-resistant design in which **the elevated process itself** authenticates the exact bytes before executing them. Do NOT trust a user-writable manifest or a SHA-256 string supplied by the unprivileged caller. For example, the helper can verify the fixed expected official SHA-256 of a pinned archive, then extract verified bytes to memory and `Assembly.Load(byte[])` only that verified content. Alternatively, use an administrator-protected signed or hash-pinned maintenance helper that never loads user-controlled DLLs. Do not sacrifice safe rollback or accidental driver/system changes. Preserve actual HIDMaestro compatibility and supported runtime dependencies; tests must prove the loaded bytes are the verified ones. **Do not just hash a user-writable file then `Assembly.LoadFrom(path)`: a race remains.**

- Verify secure install, cleanup and removal paths; refuse modified zip, modified extracted DLL, malformed archive, unexpected path and incorrect version.
- Include offline operation when the optional verified provider is already installed.
- Avoid introducing network contact at application startup.
- Ensure cleanup/removal is never automatic during session recovery.
- Add security-regression tests; document any real driver test blocked by lack of physical Windows/test device.

### 2. SEC-03: SDL3 native DLL / provider integrity

The SDL3 backend loads `%LOCALAPPDATA%` SDL3.dll via `NativeLibrary.Load`. Consider a fixed, verified digest for installed SDL3.dll and verify content before load, with care for TOCTOU. A user-writable DLL cannot be treated as trusted just because `source.json` claims it came from an official release. The trusted package hash already exists. Test post-install tampering and safe error reporting. Validate native SDL behaviour on actual controller when available.

### 3. SAFE-01: Global HIDMaestro cleanup / other clients

Current `HMContext.RemoveAllVirtualControllers(bool)` is global. The UI and bridge now warn/acknowledge this but cannot isolate other apps. Prefer a scoped DK-only cleanup API if upstream supports it. Otherwise preserve global maintenance only with explicit native warning + acknowledgment, documented impact and recovery; never present it as DK-only repair. Test against another HIDMaestro client in a controlled Windows test if possible.

### 4. REL-01/SEC-02/PRIV-01 regression review

Review the branch's new checks for:
- Local WebView2 `e.Source` validation (not merely navigation host), frame and resource security, and CSP compatibility with UI.
- Caught `HttpRequestException`, cancellation, unexpected backend errors. `async void` must not crash WPF. Attempt offline provider setup.
- Default exports redact `DeviceMatch.DeviceId` and `AdvancedDeviceId` (SDL IDs can embed serial/path), with explicit opt-in for device-bound exports. Saved data must not be mutated by export.
- No false confirmations: privileged operations may require UAC and are not considered successful until helper completes.

## Acceptance and delivery

1. Preserve local-first operation, no account, telemetry, unwanted downloads, anti-cheat bypass or silent driver installations. No automatic physical-device hiding.
2. Windows build/test command: `./scripts/Test-ControllerHub.ps1`, both named suites, all helper fixture tests, `./scripts/Verify-WindowsBundle.ps1`; add negative-security tests rather than only happy paths.
3. Check `main` remains clean; create security-remediation PR(s), compare against audit, wait for green CI and do not silently merge incomplete/unsafe elevated code.
4. State clearly what is fixed, what only has mitigation, and what still needs physical-driver/game acceptance. Link PR, commit and CI. **Visible Controller Hub UI and hardware acceptance remain separate tasks.**

## Files to inspect

`docs/controller-hub-security-audit-2026-10-14.md`, `src/DKDesktopEssentials/Modules/Controllers/ControllerHub/HidMaestroVirtualOutput.cs`, `src/DKControllerOutputHost/Program.cs`, `src/DKDesktopEssentials/Modules/Controllers/ControllerHub/Sdl3AdvancedControllerProvider.cs`, `src/DKDesktopEssentials/MainWindow.xaml.cs`, `src/DKDesktopEssentials/Modules/Controllers/ControllerHub/ControllerHubWebBridge.cs`, `tests/ControllerHub.Tests/*`, `.github/workflows/build-windows.yml`.
