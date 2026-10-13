# Controller Hub Slice 6 — Reliability, Safety & Compatibility

Built on merged Slice 5 (PR #5, main `39b199bd6092eedfd31d6182884e0ab36fc91bff`).

## Runtime contract

- `startOutputSession` now owns a session per exact physical Windows device ID. Starting the same ID replaces only that session. Each session owns its mapping runtime and helper process. A shared profile cannot share toggle/gesture state or virtual identity across controllers.
- `stopOutputSession` accepts optional `deviceId`; absent means stop all. `getOutputStatus` exposes `sessions[]` (device/profile, requested rate, state, error and recovery attempts) plus compatibility information. Consumers should use this array instead of the old single-session fields.
- Disconnect/read failure sends neutral output, resets mapping state and retains the selected profile/session. Reconnect requires the exact ID plus original vendor/product. USB/Bluetooth ID changes, absent serials and identical models are never guessed. Re-select explicitly when identity changes. Profile/preferences files remain local; active sessions do not auto-start after an app restart.
- Startup, reconnect, recovery, explicit reset and a polling gap over 500 ms require releasing digital controls and centering standard axes before output resumes. Raw axes have device-specific neutral ranges and are not included in this release gate. Missing explicitly linked advanced input neutralizes the whole session until it returns. Physical switches that are mechanically stuck cannot be distinguished from intentionally held switches during normal operation.
- Each helper acknowledges successful submission. Sends have a 500 ms deadline; helper input inactivity over one second releases controls and exits. Stop, EOF, malformed input and submission failures also attempt neutralization before disposing the virtual device. A failed driver can reject even neutral output; device disposal/process termination is the fallback, not a claim of guaranteed hardware recovery.
- Output recovery uses three retries with 250/500/1000 ms backoff, neutral replacement state and release gating. Ten seconds of healthy output replenishes that budget. Terminal failure is visible as `faulted`; explicitly start again after resolving the backend problem. No driver installation or global cleanup occurs automatically.
- PeriodicTimer coalesces missed ticks; mapping gesture time uses a monotonic clock. 60–1000 Hz is a requested cadence, not a real-time guarantee. IPC acknowledgment and Windows scheduling can reduce throughput.
- Backend repair/removal and package replacement serialize against session start and stop active sessions first. Window closing awaits service cleanup before allowing WPF to exit. SDL disposal stops rumble/trigger rumble and attempts to disable adaptive triggers. Reconnected SDL devices replace stale handles when their instance changes.

## Compatibility and privacy

Device hiding/exclusive mode remains unavailable: no new filter driver has been vetted or installed. Physical controllers remain visible, so a game may see both physical and virtual input. Use the game's explicit controller selection where available. This slice does not promise anti-cheat, every game, Bluetooth transport, sleep/resume or driver-crash compatibility. Do not infer compatibility from CI.

No telemetry, account, cloud state or background downloads are added. Existing provider downloads remain explicit and hash-pinned. Optional device hiding would require a separately reviewed provider/version, installation and rollback paths, user opt-in, and physical hardware/game testing before enabling it.

## Validation

Windows CI builds .NET 8 app and .NET 10 output helper separately, runs the hardware-free reliability executable, then publishes and bundles both. Run the same suite with:

```powershell
dotnet run --project tests/ControllerReliability.Tests/ControllerReliability.Tests.csproj -c Release
```

The tests cover shared-profile/concurrent mapping isolation; two independent sessions; disconnect neutralization and exact reconnect; held-button gating; helper replacement and retry exhaustion; blocked output shutdown; helper stop/EOF/malformed input/submit failure/watchdog; non-finite values and stable per-device identity.

Still required on physical hardware: two identical controllers with one profile, independent disconnect/reconnect under load, USB/Bluetooth identity transitions, sleep/resume, forcibly killed or stalled app/helper, driver rejection/removal, DualSense feedback reset, and target-game duplicate-input/anti-cheat behavior. Never install or change a hiding/filter driver as part of these automated tests.
