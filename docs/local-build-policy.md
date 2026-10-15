# Local build policy

All normal compilation, packaging and artifact generation runs on the user's
Windows computer, using .NET/MSBuild. Root [AGENTS.md](../AGENTS.md) is the permanent
instruction source. Existing application and download changes are outside this
configuration task and must remain intact.

## Local tools and checks

Windows 10/11, the .NET 8 and .NET 10 SDKs, and WebView2 Runtime are needed for the
app/helper. This checkout already has SDKs 8.0.426 and 10.0.402 under ignored
`artifacts/dotnet`. A fresh clone needs suitable locally installed SDKs; SDKs and
build outputs are intentionally not distributed in Git.

From repository root:

```powershell
$localSdk = Join-Path $PWD 'artifacts/dotnet'
if (Test-Path (Join-Path $localSdk 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localSdk
    $env:DOTNET_CLI_HOME = Join-Path $PWD 'artifacts/dotnet-home'
    $env:PATH = "$localSdk;$env:PATH"
}
dotnet --list-sdks
if ($LASTEXITCODE -ne 0) { throw 'A local .NET SDK is required.' }
```

Confirm both required major SDK versions before proceeding. Dependency downloads
are allowed locally. No cloud build may be substituted for missing tooling or
failed compilation.

## Compile and test

```powershell
dotnet build src/DKDesktopEssentials/DKDesktopEssentials.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Local app build failed.' }
dotnet build src/DKControllerOutputHost/DKControllerOutputHost.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Local helper build failed.' }
dotnet run --project tests/DownloadCore.Tests/DownloadCore.Tests.csproj -c Release -- --report artifacts/download-tests.json
if ($LASTEXITCODE -ne 0) { throw 'Local download tests failed.' }
./scripts/Test-ControllerHub.ps1
```

The controller script checks its commands and produces ignored JSON reports.
Optional official provider validation is available in
`scripts/Test-ProviderReleasePins.ps1`; it may explicitly download pinned provider
archives when local paths are omitted. It does not certify physical hardware.

## Package a Windows x64 Release bundle

Use a fresh output directory; do not delete existing outputs or user files as an
implicit build step. These commands generate a self-contained app and helper:

```powershell
$bundleRoot = Join-Path $PWD ('artifacts/local-release-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$helperOutput = Join-Path $bundleRoot 'controller-output-host'
dotnet publish src/DKDesktopEssentials/DKDesktopEssentials.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o $bundleRoot
if ($LASTEXITCODE -ne 0) { throw 'Local app publishing failed.' }
dotnet publish src/DKControllerOutputHost/DKControllerOutputHost.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o $helperOutput
if ($LASTEXITCODE -ne 0) { throw 'Local helper publishing failed.' }
./scripts/Verify-WindowsBundle.ps1 -Bundle $bundleRoot
$appArtifact = Join-Path $bundleRoot 'DKDesktopEssentials.exe'
if (!(Test-Path -LiteralPath $appArtifact -PathType Leaf)) { throw 'Published executable missing.' }
Get-Item -LiteralPath $appArtifact | Select-Object FullName, Length
Get-AuthenticodeSignature -LiteralPath $appArtifact | Select-Object Status, SignerCertificate
```

Inspect logs and the verification result. Report the actual absolute output path,
Release configuration and observed signature status; no signing step is configured
by this policy. Supply an artifact link only after verifying it exists. Never
include private signing keys or credentials. `bin/`, `obj/`, `artifacts/` and
`TestResults/` are already ignored.

## Actions protection and activation

The repository contains one workflow: `.github/workflows/build-windows.yml`.
It previously compiled, tested, published and uploaded Windows binaries on pushes
to `main`/`module/**` and pull requests into `main`. It now preserves those steps
behind **workflow_dispatch only**, with a required boolean
`authorize_remote_build` defaulting to false and a matching job guard. Normal
push/PR/scheduled builds have no trigger in this version. A manual false/default
dispatch cannot allocate this build job's Windows runner. Do not dispatch it to
test this policy.

Repository YAML controls take effect on GitHub only after those source changes
reach the relevant remote branches/default branch. Older branch copies may still
have automatic triggers. This change does not cancel previously queued/running
jobs or disable Actions at the account/repository settings level.

For an absolute repository-wide stop, an administrator can disable Actions under
**Settings → Actions → General**. Inspect any branch protection/ruleset requiring
the old `build` check before removing that requirement; otherwise manual-only
builds can leave merges waiting for an intentionally absent check. Organization
policies and external CI integrations also require their own inspection. No such
settings were changed or verified by the local file edits. Never report Actions
as globally disabled without checking the actual configuration.

If working in cloud Codex without access to the user's computer, prepare source
and these commands; the user must compile on their computer. Never label a cloud
artifact a local build or use Actions as a fallback.
