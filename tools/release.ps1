<#
.SYNOPSIS
  Builds, signs and publishes a Sentinel release to GitHub.

.DESCRIPTION
  1. Reads the version from Directory.Build.props.
  2. Publishes self-contained builds for win-x64 and win-arm64.
  3. Zips each one as Sentinel-<version>-<rid>.zip and signs it with the release key (writes .zip.sig).
  4. Verifies every signature against the public key built into the app.
  5. Creates a GitHub release v<version> with the packages attached (requires the GitHub CLI, signed in).

  The private key stays on this machine (default: %USERPROFILE%\.sentinel\release-signing-key.pem).

.EXAMPLE
  ./tools/release.ps1 -Notes "Fixes the Processes list flicker."
  ./tools/release.ps1 -NoPublish     # build, zip and sign only
#>
param(
    [string]$Notes = "",
    [string]$KeyPath = "$env:USERPROFILE\.sentinel\release-signing-key.pem",
    [string[]]$Runtimes = @("win-x64", "win-arm64"),
    [switch]$NoPublish
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content "Directory.Build.props"
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "Version not found in Directory.Build.props" }
if (-not (Test-Path $KeyPath)) { throw "Signing key not found at $KeyPath" }
Write-Host "Releasing Sentinel $version" -ForegroundColor Cyan

$out = Join-Path $root "publish\$version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$assets = @()
foreach ($rid in $Runtimes) {
    $dir = Join-Path $out $rid
    Write-Host "Publishing $rid..."
    dotnet publish src\Sentinel.App -c Release -r $rid -o $dir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }
    Get-ChildItem $dir -Filter *.pdb -Recurse | Remove-Item -Force
    $zip = Join-Path $out "Sentinel-$version-$rid.zip"
    dotnet run --project tools\Sentinel.ReleaseTool -c Release -- pack $dir $zip
    if ($LASTEXITCODE -ne 0) { throw "Packing failed for $rid" }
    dotnet run --project tools\Sentinel.ReleaseTool -c Release -- sign $zip $KeyPath
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $rid" }
    dotnet run --project tools\Sentinel.ReleaseTool -c Release -- verify $zip
    if ($LASTEXITCODE -ne 0) { throw "Signature does not verify with the app's built-in key for $rid" }
    $assets += $zip, "$zip.sig"
}

if ($NoPublish) {
    Write-Host "Packages are in $out (not published)." -ForegroundColor Yellow
    return
}

$body = @"
$Notes

**Install:** download ``Sentinel-$version-win-x64.zip`` (or ``win-arm64`` for ARM PCs), extract it anywhere (for example ``%LOCALAPPDATA%\Programs\Sentinel``) and run ``Sentinel.exe``. Existing installs update themselves.

Each package has a detached signature (``.zip.sig``, ECDSA P-256). Sentinel verifies it against the release key built into the app before installing an update. Sentinel is not code-signed with a certificate, so Windows SmartScreen may warn you the first time you run it.
"@
gh release create "v$version" @assets --title "Sentinel $version" --notes $body
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Host "Published v$version" -ForegroundColor Green
