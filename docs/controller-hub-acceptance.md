# Controller Hub hardware acceptance

Status: **pending**. CI and test substitutes do not certify physical hardware or target games.

Use a Windows 10/11 x64 machine with controllers you can unplug/reconnect. Record the tested commit, Windows version, controller model, connection mode, driver version, HIDMaestro/SDL version, test time and observations. Keep reports local unless you choose to share them. This procedure does not install or hide devices automatically.

## Input discovery evidence

With the .NET 8 SDK installed, run from the repository root:

```powershell
dotnet run --project tests/ControllerHub.Tests/ControllerHub.Tests.csproj -c Release -- --hardware-inspect --report artifacts/hardware-discovery.json
```

This command enumerates Windows physical input and attempts one sample per device. It writes hashed device IDs and capability counts; it issues no feedback/output commands and does not load optional SDL or output providers. Exit code 0 means at least one readable sample, 2 means no readable controller, and neither means full acceptance. The report always retains `physicalHardwareValidated: false`. Discovery does not replace the tests below.

## Acceptance record

Copy this table into a local test record and fill the result column with pass/fail/not applicable plus evidence. Until performed, every row remains **not run**.

| Case | Action and expected result | Result |
|---|---|---|
| Xbox wired/wireless | Compare face buttons, d-pad, sticks and triggers against known physical input; connect/disconnect without restarting the app. | Not run |
| Generic/raw controller | Confirm raw button/axis/switch indices and intentional raw-axis neutral ranges; do not assume an Xbox layout. | Not run |
| Two identical controllers | Use one compatible profile on both. Each creates its own virtual identity and mapping state; stopping/unplugging one leaves the other active. | Not run |
| Reconnect held button | Unplug while a toggle/turbo/button is active. Output neutralizes; reconnect the exact device with controls held. Output resumes only after release. | Not run |
| Changed USB/Bluetooth ID | Change connection transport. The old session must wait rather than guess the new ID. Explicitly select/relink when identity changes. | Not run |
| Xbox360 / DS4 output | After explicit provider/driver setup, verify each virtual layout in an OS controller viewer and a target game. Record whether elevation is required. | Not run |
| Failed/stalled parent | With an active output, terminate or stall the app. The helper should neutralize on pipe EOF or input watchdog and exit; record any driver rejection/orphan device. | Not run |
| Failed helper | Terminate a helper under load. Confirm bounded recreation, neutral replacement state, release gating and terminal error after repeated failures. | Not run |
| Sleep/resume | Suspend while output is active. On resume, controls should remain safe and require release after a polling gap. Record whether devices retain their IDs. | Not run |
| SDL sensors/touch | Explicitly install the verified optional backend and link the intended device. Verify gyro, touch contact/release and unsupported-capability reporting. | Not run |
| Feedback reset | On supported DualSense transport, apply bounded rumble and adaptive triggers, then close the app. Rumble/trigger effects should clear. | Not run |
| Driver maintenance | Stop outputs before explicit install/repair/removal; verify the documented reinstall/rollback path. All three use upstream global cleanup and can affect other HIDMaestro clients. Validate private elevated cache ownership/permissions, UAC cancellation and offline operation with the real provider. | Not run |
| Target games | Record physical+virtual duplicate input, layout detection and each game's anti-cheat policy/behavior. Hiding is unavailable and compatibility is never universal. | Not run |
| Privacy/offline | With optional providers already installed, disconnect the network. Profiles, mapping and status work locally; startup does not download providers or require an account. | Not run |

Do not use the fake SDK for hardware acceptance. It contains no virtual device implementation. Do not install a hiding/filter driver as a side effect of this checklist.

## Failures and recovery

Save local status/error and test results. Stop the affected session; release controls and explicitly restart after resolving the source/backend problem. Output recovery never automatically installs or repairs a driver. If upstream cleanup is required, stop all clients first and review its global scope. Report unsupported transports or capabilities honestly; do not mark a failed or unperformed test as passing.
