param([string]$Bundle = 'artifacts/DK-Desktop-Essentials-win-x64')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
foreach ($relative in @(
    'DKDesktopEssentials.exe', 'DKDesktopEssentials.dll', 'DKDesktopEssentials.runtimeconfig.json',
    'controller-output-host/DKControllerOutputHost.exe', 'controller-output-host/DKControllerOutputHost.dll',
    'controller-output-host/DKControllerOutputHost.runtimeconfig.json',
    'ui/index.html', 'ui/app2.js', 'ui/workspace-rail.js', 'ui/styles.css'
)) {
    if (!(Test-Path -LiteralPath (Join-Path $Bundle $relative) -PathType Leaf)) { throw "Missing bundle file: $relative" }
}
if (@(Get-ChildItem -LiteralPath $Bundle -Recurse -File -Filter 'HIDMaestro.Core.dll').Count -ne 0) {
    throw 'An optional or test SDK leaked into the release bundle.'
}
if (@(Get-ChildItem -LiteralPath $Bundle -Recurse -File -Filter '*Tests.dll').Count -ne 0) {
    throw 'A test harness leaked into the release bundle.'
}
Write-Output 'Windows bundle verified; optional/test SDKs and test executables are excluded.'
