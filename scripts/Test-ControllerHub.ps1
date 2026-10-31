param(
    [string]$HelperPath,
    [string]$ReportDirectory = 'artifacts/controller-tests',
    [switch]$ManagedOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$reportRoot = if ([IO.Path]::IsPathRooted($ReportDirectory)) { $ReportDirectory } else { Join-Path $repositoryRoot $ReportDirectory }
$reportRoot = [IO.Path]::GetFullPath($reportRoot)

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Force -Path $reportRoot | Out-Null
    $completionArguments = @('--report', (Join-Path $reportRoot 'completion.json'))
    if ($ManagedOnly) {
        $completionArguments += '--managed-only'
    } else {
        if ([string]::IsNullOrWhiteSpace($HelperPath)) {
            $HelperPath = Join-Path $reportRoot 'helper/DKControllerOutputHost.exe'
            Invoke-DotNet -Arguments @('publish', 'src/DKControllerOutputHost/DKControllerOutputHost.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishReadyToRun=false', '-o', (Split-Path $HelperPath -Parent))
        }
        $HelperPath = [IO.Path]::GetFullPath($HelperPath)
        if (!(Test-Path -LiteralPath $HelperPath -PathType Leaf)) { throw 'The published output helper is missing.' }
        $fakeDirectory = Join-Path $reportRoot 'fake-sdk'
        Invoke-DotNet -Arguments @('build', 'tests/FakeHidMaestro/FakeHidMaestro.csproj', '-c', 'Release', '-o', $fakeDirectory)
        $fakeSdk = Join-Path $fakeDirectory 'HIDMaestro.Core.dll'
        $fixtureDigest = (Get-FileHash -LiteralPath $fakeSdk -Algorithm SHA256).Hash.ToLowerInvariant()
        $fixturePins = Join-Path $reportRoot 'ProviderPins.Generated.cs'
        # Only the separate test project compiles this anchor. Production has no test-pin switch.
        Set-Content -LiteralPath $fixturePins -Value "namespace DKDesktopEssentials.Security; internal static class ProviderPins { internal const string HidMaestroSdkSha256 = `"$fixtureDigest`"; }"
        $fixtureHostDirectory = Join-Path $reportRoot 'fixture-host'
        Invoke-DotNet -Arguments @('publish', 'tests/ControllerOutputHost.TestFixture/ControllerOutputHost.TestFixture.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishReadyToRun=false', "-p:FixturePinsPath=$fixturePins", '-o', $fixtureHostDirectory)
        $completionArguments += @('--helper', $HelperPath, '--fixture-helper', (Join-Path $fixtureHostDirectory 'DKControllerOutputHost.TestFixture.exe'), '--fake-sdk', $fakeSdk)
    }
    # Run both suites even when one fails so both structured reports are available.
    & dotnet run --project tests/ControllerReliability.Tests/ControllerReliability.Tests.csproj -c Release -- --report (Join-Path $reportRoot 'reliability.json')
    $reliabilityExit = $LASTEXITCODE
    & dotnet run --project tests/ControllerHub.Tests/ControllerHub.Tests.csproj -c Release -- @completionArguments
    $completionExit = $LASTEXITCODE
    if ($reliabilityExit -ne 0 -or $completionExit -ne 0) { throw 'Controller Hub tests failed. Inspect the JSON reports.' }
} finally {
    Pop-Location
}
