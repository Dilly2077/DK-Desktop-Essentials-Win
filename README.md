# DK Desktop Essentials

A local-first Windows power-user suite. The project is intentionally designed around **no account dependency, no telemetry, no subscriptions, and no artificial feature locks**.

## Current phase: UI foundation

This repository currently contains the application shell and interactive UI foundation. Tool modules are represented in the interface but their system-level implementations will be added incrementally.

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
- .NET 8 SDK
- Microsoft Edge WebView2 Runtime

```powershell
dotnet restore .\src\DKDesktopEssentials\DKDesktopEssentials.csproj
dotnet run --project .\src\DKDesktopEssentials\DKDesktopEssentials.csproj
```

A GitHub Actions workflow publishes an x64 Windows build artifact on every push to `main`.

## Privacy model

The current UI performs no network requests. Preferences such as pins, favourites and layout choices are stored locally in the WebView2 profile/local storage on the user's machine.
