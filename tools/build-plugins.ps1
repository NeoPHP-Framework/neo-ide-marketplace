<#
.SYNOPSIS
    Builds every plugin of plugins/ into dist/<id>-<version>.zip (plugin.json + DLLs), the package NeoIDE installs.

.DESCRIPTION
    The plugins compile against NeoIDE.Sdk from the NeoIDE repository: a checkout in .neoide/ (what the CI does)
    or the folder of the NeoIdeRoot environment variable, e.g.
        $env:NeoIdeRoot = "C:\dev\NeoIDE"; pwsh tools/build-plugins.ps1

.PARAMETER Only
    Build only these plugin ids.
#>
param([string[]]$Only)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if ($env:USERPROFILE -and (Test-Path $userDotnet)) { $dotnet = $userDotnet }
if (-not $dotnet) { throw "dotnet SDK not found." }

$built = @()
foreach ($dir in Get-ChildItem (Join-Path $root "plugins") -Directory | Sort-Object Name) {
    $manifestPath = Join-Path $dir.FullName "plugin.json"
    if (-not (Test-Path $manifestPath)) { continue }
    $m = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($Only -and $Only -notcontains $m.id) { continue }
    $project = Get-ChildItem $dir.FullName -Filter *.csproj | Select-Object -First 1
    if (-not $project) { throw "$($dir.Name): no .csproj" }

    Write-Host "Building $($m.id) $($m.version)..." -ForegroundColor Cyan
    $out = Join-Path $dist "build\$($m.id)"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & $dotnet publish $project.FullName -c Release -r win-x64 --self-contained false -o $out -nologo -v quiet -p:Version=$($m.version)
    if ($LASTEXITCODE -ne 0) { throw "$($m.id): build failed" }
    if (-not (Test-Path (Join-Path $out $m.assembly))) { throw "$($m.id): $($m.assembly) was not produced" }
    # the SDK is provided by NeoIDE: never ship it
    Get-ChildItem $out -Filter "NeoIDE.Sdk.*" | Remove-Item -Force
    Get-ChildItem $out -Filter "*.pdb" | Remove-Item -Force

    $zip = Join-Path $dist "$($m.id)-$($m.version).zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
    Write-Host ("  {0}  ({1:N0} KB)" -f $zip, ((Get-Item $zip).Length / 1KB)) -ForegroundColor Green
    $built += $zip
}
Write-Host "$($built.Count) plugin(s) built." -ForegroundColor Green
