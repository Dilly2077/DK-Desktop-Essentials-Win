# DK Desktop Essentials

A local-first Windows power-user suite. The project is intentionally designed around **no account dependency, no telemetry, no subscriptions, and no artificial feature locks**.

## Current phase: UI foundation and Controller Hub backend

This repository contains the application shell, interactive UI foundation, and the Controller Hub backend through its seven planned slices. Controller Hub has automated mapping, persistence, bridge, reliability and packaged-helper tests; visible editor integration and physical hardware/game acceptance remain pending. Other tool modules will be added incrementally.

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

A GitHub Actions workflow publishes an x64 Windows build artifact on every push to `main`.

## Privacy model

The current UI performs no network requests. Preferences such as pins, favourites and layout choices are stored locally in the WebView2 profile/local storage on the user's machine.


## Controller Hub validation

On Windows x64, run .\scripts\Test-ControllerHub.ps1 to execute both controller suites and produce local JSON results. Windows CI runs the suites against its published helper, checks the release bundle, and uploads test reports separately from the app.

See [module scope and test commands](docs/controller-hub-completion.md) and [hardware acceptance](docs/controller-hub-acceptance.md). Optional HIDMaestro/SDL packages are installed only on explicit request, with pinned download hashes; hardware compatibility is not certified by CI.
