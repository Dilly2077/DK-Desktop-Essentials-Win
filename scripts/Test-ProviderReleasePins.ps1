param(
    [Parameter(Mandatory)][string]$HelperPath,
    [string]$ReportDirectory = 'artifacts/controller-tests',
    [string]$HidMaestroArchive,
    [string]$SdlArchive
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    $reportRoot = [IO.Path]::GetFullPath($ReportDirectory)
    $downloads = Join-Path $reportRoot 'official-pin-inputs'
    New-Item -ItemType Directory -Force -Path $downloads | Out-Null
    # Explicit CI/developer validation download. Application startup never runs this script.
    if (!$HidMaestroArchive) {
        $HidMaestroArchive = Join-Path $downloads 'HIDMaestro-v1.8.0.zip'
        Invoke-WebRequest -Uri 'https://github.com/hifihedgehog/HIDMaestro/releases/download/v1.8.0/HIDMaestro-v1.8.0.zip' -OutFile $HidMaestroArchive
    }
    if (!$SdlArchive) {
        $SdlArchive = Join-Path $downloads 'SDL3-3.4.16-win32-x64.zip'
        Invoke-WebRequest -Uri 'https://github.com/libsdl-org/SDL/releases/download/release-3.4.16/SDL3-3.4.16-win32-x64.zip' -OutFile $SdlArchive
    }
    & dotnet run --project tests/ControllerHub.Tests/ControllerHub.Tests.csproj -c Release -- --provider-pin-inspect --helper ([IO.Path]::GetFullPath($HelperPath)) --hid-archive ([IO.Path]::GetFullPath($HidMaestroArchive)) --sdl-archive ([IO.Path]::GetFullPath($SdlArchive)) --report (Join-Path $reportRoot 'provider-pins.json')
    if ($LASTEXITCODE -ne 0) { throw 'Official provider pin validation failed. Inspect provider-pins.json.' }
} finally { Pop-Location }
