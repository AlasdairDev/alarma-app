#Requires -Version 5.1
<#
.SYNOPSIS
    AlarmaApp workspace self-repair utility.
    Purges stale build artifacts, restores workloads/packages, and compiles for Android 15 (API 35).
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot

Write-Host "`n[1/4] Purging bin/ and obj/ directories..." -ForegroundColor Cyan
Get-ChildItem -Path $root -Recurse -Directory -Include 'bin','obj' | ForEach-Object {
    Write-Host "  Removing $($_.FullName)"
    Remove-Item -Recurse -Force $_.FullName
}
Write-Host "      Cache purge complete." -ForegroundColor Green

Write-Host "`n[2/4] Restoring MAUI Android workload..." -ForegroundColor Cyan
dotnet workload restore
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet workload restore failed (exit $LASTEXITCODE)." }
Write-Host "      Workload alignment complete." -ForegroundColor Green

Write-Host "`n[3/4] Restoring NuGet packages..." -ForegroundColor Cyan
dotnet restore "$root\AlarmaApp.csproj"
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet restore failed (exit $LASTEXITCODE)." }
Write-Host "      Dependency integrity verified." -ForegroundColor Green

Write-Host "`n[4/4] Building for Android 15 (API 35)..." -ForegroundColor Cyan
dotnet build "$root\AlarmaApp.csproj" -f net9.0-android -c Debug
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build failed (exit $LASTEXITCODE)." }
Write-Host "      Compilation output validated." -ForegroundColor Green

Write-Host "`nrepair_build: all steps completed successfully.`n" -ForegroundColor Yellow
