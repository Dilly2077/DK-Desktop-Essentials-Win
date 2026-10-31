# DK Desktop Essentials

A local-first Windows power-user suite. The project is intentionally designed around **no account dependency, no telemetry, no subscriptions, and no artificial feature locks**.

## Current phase: UI foundation, Controller Hub backend and direct downloads

This repository contains the application shell, interactive UI foundation, and the Controller Hub backend through its seven planned slices. Controller Hub has automated mapping, persistence, bridge, reliability and packaged-helper tests; visible editor integration and physical hardware/game acceptance remain pending. Other tool modules will be added incrementally.

Downloads & Transfers includes HTTP/HTTPS downloads through **Download Manager**, with a protected local SQLite queue, configurable concurrency, bounded retries, validated pause/resume and optional parallel range reads. The 32.6 interface now uses the native backend after a capability handshake. Choose a native destination folder, enter a URL and filename, then start or retain a held queue entry. Servers must prove a partial file is unchanged before reuse. See [32.6B integration and Windows acceptance](docs/downloads-slice-32-6b.md): native action/restart tests pass; the complete width range, 150%/200% scaling and spoken screen-reader acceptance remain pending. Module 32 is not yet release-ready.

### UI goals

- Dark navy / charcoal Windows desktop interface with cyan-blue accents
- Command Center with searchable tool catalogue
- Pinned tools, favourites, recent tools and quick settings
- Customisable workspace with draggable widgets
- Consistent icon system across all modules
- Local persistence of UI preferences only
- No analytics, tracking, advertising SDKs or account system

## Architecture

The first implementation uses a .NET 8 WPF host with a local WebView2 front end. The UI is shipped with the application and loaded locally; it does not require a web server or network connection. This gives the project a native Windows host for future hardware, driver, controller and network integrations while allowing a highly polished, fluid interface.

## Build

Requirements for local development:

- Windows 10/11
- .NET 8 SDK for the app and .NET 10 SDK for the optional controller output helper
- Microsoft Edge WebView2 Runtime

```powershell
dotnet restore .\src\DKDesktopEssentials\DKDesktopEssentials.csproj
dotnet run --project .\src\DKDesktopEssentials\DKDesktopEssentials.csproj
```

**LOCAL BUILD ONLY:** compile and package on your Windows computer using .NET/MSBuild. GitHub is used for source control and collaboration. The preserved Windows Actions workflow is manual-only and requires explicit authorisation for each exceptional remote build; pushes and pull requests must not start it. See [local build and packaging commands](docs/local-build-policy.md).

## Privacy model

The bundled UI requires no external network assets. Native HTTP/HTTPS downloads run after explicit submission; source URLs are retained in a current-user DPAPI-protected local store for recovery and excluded from returned job views. Preferences such as pins, favourites and layout choices are stored locally in the WebView2 profile/local storage on the user's machine. See the [download privacy boundaries and limitations](docs/downloads-slice-32-6b.md).


## Controller Hub validation

On Windows x64, run .\scripts\Test-ControllerHub.ps1 locally to execute both controller suites and produce local JSON results. The preserved manual workflow can run equivalent validation only for an explicitly authorised remote-build exception.

See [module scope and test commands](docs/controller-hub-completion.md) and [hardware acceptance](docs/controller-hub-acceptance.md). Optional HIDMaestro/SDL packages are installed only on explicit request, with pinned download hashes; hardware compatibility is not certified by CI.

Provider execution uses compiled DLL integrity pins, verified in-memory SDK loading and locked SDL loading. See [security remediation and its remaining acceptance limits](docs/controller-hub-security-remediation.md). Global driver maintenance still requires explicit confirmation and can affect other HIDMaestro clients.
