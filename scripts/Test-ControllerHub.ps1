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
        $completionArguments += @('--helper', $HelperPath, '--fake-sdk', (Join-Path $fakeDirectory 'HIDMaestro.Core.dll'))
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
