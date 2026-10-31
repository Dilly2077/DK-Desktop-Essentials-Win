# Controller Hub issue #12 implementation

Based on main `9ecfd526d258452223a9b04679d69ec887123e51` (PR #13). This implementation follows the audit/remediation brief while preserving the later PR #11 hardening. It does not certify physical controllers, Windows drivers, games or anti-cheat compatibility.

## SDK execution boundary (SEC-01)

The production helper authenticates the exact installed DLL snapshot against a **compiled extracted-file SHA-256**, inside the process that executes it. The file is opened without write/delete sharing, size-limited, copied into memory and verified; `Assembly.Load(byte[])` consumes that same verified snapshot. The path is never reopened for assembly execution. No caller-provided digest, writable manifest, alternate test pin, duplicate option or unknown option is accepted. Relative/UNC/device/alternate-stream/wrong-filename paths are rejected. A different provider version or modified DLL fails closed with a reinstall message.

This also authenticates normal output/probe loads. Existing authentic 1.8.0 installations continue offline, without re-downloading an archive. Package installation authenticates a single ZIP snapshot, selects the exact root DLL entry, checks its compiled digest and writes through a unique temporary file. Malformed/modified packages and substituted library versions cannot become executable providers.

The application, helper, packaged runtime and compiled trust anchors remain part of the trusted application distribution. This change does not authenticate a modified application/helper or protect against an already privileged attacker modifying that distribution.

### Upstream tool-cache isolation

Pinned HIDMaestro source at `a396741f3945a89e3df9a077e4fff14bf01a95c4` embeds driver/tools/profiles, so an in-memory assembly retains those resources. However, `DriverBuilder.EnsureExtracted` and `SwdDeviceFactory.EnsureHelperExtracted` reuse temporary executables based on file size. An elevated process therefore isolates `TEMP`, `TMP`, `SystemTemp`, current directory and executable search paths before loading the SDK.

The helper creates/validates a digest-specific cache directly beneath the trusted Windows directory. Its owner is Administrators; its protected, inheritable DACL permits Administrators/System full access, with OWNER RIGHTS limited to read-control so a linked unelevated owner cannot implicitly change a child file's DACL. Unexpected ownership, ACLs, inheritance or reparse points fail closed. Executable search resolves only in System32. The environment stays isolated until process exit, including SDK background warmup.

This private cache is retained for offline reuse. SDK upgrades use a different digest-specific cache. For manual rollback/cache removal, close all DK output helpers, then use administrator access to remove only the exact `DKControllerMaintenance-<SDK digest>` directory created by this build; do not delete the Windows directory or other applications' HIDMaestro caches. Provider removal still separately controls the user-local package. Actual SDK installation, signing, driver-store behavior and rollback require controlled Windows acceptance.

## SDL library integrity (SEC-03)

SDL3's exact installed-file digest is also compiled into the application. Windows cannot load native DLLs from an ordinary managed byte array, so the loader locks the DLL **and every directory component** against writes/replacement/rename, refuses junctions/reparse points and pre-existing writable handles, verifies bytes and retains those handles until unload. The native load uses the canonical DOS path returned from the locked file handle and restricts dependency resolution to System32. Windows rejects volume-GUID/NT-device paths for this load; the actual official DLL smoke test verifies the supported path.

Tampering produces an unavailable provider with an integrity/reinstall error rather than falling through to another DLL. Provider locations with junctions/links are deliberately unsupported. This addresses ordinary library/directory substitution; it is not protection against a compromised process, privileged volume/drive remapping or arbitrary administrator changes. Status file-presence fields remain availability hints; authentication occurs at the execution boundary.

## Global maintenance (SAFE-01)

The pinned public upstream maintenance API remains global: `RemoveAllVirtualControllers(bool)` offers no ownership selector. `InstallDriver()` itself calls global cleanup. Installation, repair and removal therefore all require bridge `confirmGlobalCleanup: true`, a native warning/confirmation, helper `--acknowledge-global true` and explicit administrator approval. This mitigates unintended cross-client impact; it does **not** make cleanup DK-only. Session recovery never invokes maintenance. Dispose still uses the SDK's per-context/per-controller lifecycle.

## Validation and CI

`scripts/Test-ControllerHub.ps1` runs 45 completion cases and nine reliability cases (54 total), retaining all baseline tests and adding 12 provider-security cases. The five positive packaged-helper cases use a **separate test project** compiling the same helper sources with a build-generated test-only pin. Production accepts only its official pin. Production-helper process tests reject the fake SDK in every mode, digest overrides, duplicate/unknown options, bad paths and unacknowledged maintenance. Elevated fixture sessions additionally check private staging/search paths and inherited OWNER RIGHTS on a test-created child.

Negative tests exercise DLL mutation/truncation/growth/size limits, replacement after a verified snapshot, corrupt/modified ZIPs, wrong versions/entry paths, forged manifests, existing writers, locked-file deletion, directory rename and junction substitution. Existing origin/CSP-related policy, structured network/cancellation failures, portable export privacy, stored-profile preservation and legacy profile/session behavior remain covered by the controller suites/source review.

`scripts/Test-ProviderReleasePins.ps1` performs two additional official-provider checks using the production anchors: root DLL extraction plus an offline metadata-only SDK probe, and SDL loading/unloading without initializing controllers. It explicitly downloads the pinned archives only when a developer/CI run omits local archive paths. It never installs a provider into the application's user-local directory or invokes driver/device APIs. JSON reports always retain `physicalHardwareValidated: false`.

Windows CI builds/publishes both applications, executes all suites and official-pin checks, uploads reports and verifies the release bundle excludes test SDKs, fixture executables and generated anchors. GitHub Actions are pinned to immutable commits. CI does not validate UAC interaction, real driver install/repair/removal, another client's devices, transport changes, sensors/effects or games. Keep the PR unmerged pending green checks and security review.

## Trust-anchor provenance

Both archive digests were checked against the pinned official downloads; DLL digests below derive from the **root entries**, not the similarly named SDK copies elsewhere in HIDMaestro's ZIP. CI verifies both levels.

| Provider | Official archive SHA-256 | Root DLL SHA-256 |
|---|---|---|
| [HIDMaestro 1.8.0](https://github.com/hifihedgehog/HIDMaestro/releases/tag/v1.8.0) | `1e5f5019c20e4be8f922c7aa5a86ee87eb01f7aa851fe38daea14d0ce4fd8240` | `4c4ad3c2a70837566794f4c2f2b671fb5a0a2b4523fe180a0f7e4ec6d482bd18` |
| [SDL3 3.4.16](https://github.com/libsdl-org/SDL/releases/tag/release-3.4.16) | `4217944b4e51457af4a59c82d883f8443b3e65964b2acd8943484c492756c4b6` | `1f98969319302a100931f4385e5918a0bd53ab07773040682d22e7edb54858c0` |

Reference semantics: [Assembly.Load(byte[])](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.assembly.load), [CreateFile sharing/reparse flags](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew), [LoadLibraryEx dependency search](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-loadlibraryexw), and [well-known SID/OWNER RIGHTS behavior](https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids).
