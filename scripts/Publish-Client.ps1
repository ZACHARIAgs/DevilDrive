<#
.SYNOPSIS
    Builds Jarvis.exe (self-contained, so teammates don't need .NET installed) and packs
    dist\Jarvis-Setup.zip with Setup.cmd, the setup script and the exe. Also writes the files
    a GitHub release needs for automatic updates: dist\Jarvis.exe,
    dist\Jarvis-SolidWorksAddin.zip and dist\SHA256SUMS (see Release-Client.ps1).

.DESCRIPTION
    Share the zip on GitHub Releases or Discord. Teammates unzip it and double-click Setup.cmd.
#>
[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'Jarvis-Setup'

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
# Start clean, so files from an earlier build (e.g. renamed DLLs) never end up in the zips.
foreach ($old in 'publish', 'publish-addin') {
    if (Test-Path (Join-Path $dist $old)) { Remove-Item -Recurse -Force (Join-Path $dist $old) }
}
New-Item -ItemType Directory -Force $stage | Out-Null

& dotnet publish (Join-Path $root 'src\Jarvis.Client\Jarvis.Client.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o (Join-Path $dist 'publish')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item (Join-Path $dist 'publish\Jarvis.exe') $stage
Copy-Item (Join-Path $PSScriptRoot 'Setup.cmd') $stage
Copy-Item (Join-Path $PSScriptRoot 'Setup-Jarvis.ps1') $stage

# SolidWorks add-in (needs SolidWorks installed on this PC to build).
$addinProject = Join-Path $root 'src\Jarvis.SwAddin\Jarvis.SwAddin.csproj'
if (Test-Path 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SolidWorks.Interop.sldworks.dll') {
    & dotnet build $addinProject -c $Configuration -o (Join-Path $dist 'publish-addin')
    if ($LASTEXITCODE -ne 0) { throw 'Add-in build failed.' }
    $addinStage = Join-Path $stage 'SolidWorksAddin'
    New-Item -ItemType Directory -Force $addinStage | Out-Null
    Copy-Item (Join-Path $dist 'publish-addin\*.dll') $addinStage
    Copy-Item (Join-Path $PSScriptRoot 'Install-SolidWorksAddin.ps1') $stage
    Copy-Item (Join-Path $PSScriptRoot 'Install SolidWorks Add-in.cmd') $stage

    # Add-in on its own, for automatic updates.
    $addinZipStage = Join-Path $dist 'Jarvis-SolidWorksAddin'
    if (Test-Path $addinZipStage) { Remove-Item -Recurse -Force $addinZipStage }
    New-Item -ItemType Directory -Force (Join-Path $addinZipStage 'SolidWorksAddin') | Out-Null
    Copy-Item (Join-Path $dist 'publish-addin\*.dll') (Join-Path $addinZipStage 'SolidWorksAddin')
    Copy-Item (Join-Path $PSScriptRoot 'Install-SolidWorksAddin.ps1') $addinZipStage
    $addinZip = Join-Path $dist 'Jarvis-SolidWorksAddin.zip'
    if (Test-Path $addinZip) { Remove-Item -Force $addinZip }
    Compress-Archive -Path (Join-Path $addinZipStage '*') -DestinationPath $addinZip
}
else {
    Write-Warning 'SolidWorks is not installed on this PC, so the add-in was left out of the zip.'
}

$zip = Join-Path $dist 'Jarvis-Setup.zip'
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

Copy-Item (Join-Path $dist 'publish\Jarvis.exe') $dist -Force

# Installs from before the rename (Capstone PDM 0.4.0 and older) look for these names to update.
# They get the new files and move themselves to the Jarvis folders on first start.
Copy-Item (Join-Path $dist 'Jarvis.exe') (Join-Path $dist 'CapstonePDM.exe') -Force
if (Test-Path (Join-Path $dist 'Jarvis-SolidWorksAddin.zip')) {
    Copy-Item (Join-Path $dist 'Jarvis-SolidWorksAddin.zip') (Join-Path $dist 'CapstonePDM-SolidWorksAddin.zip') -Force
}

# Checksums for the release: the app only installs an update whose SHA-256 matches.
$released = @('Jarvis.exe', 'Jarvis-Setup.zip', 'Jarvis-SolidWorksAddin.zip', 'CapstonePDM.exe', 'CapstonePDM-SolidWorksAddin.zip') |
    Where-Object { Test-Path (Join-Path $dist $_) }
$sums = foreach ($name in $released) { '{0}  {1}' -f (Get-FileHash (Join-Path $dist $name) -Algorithm SHA256).Hash.ToLowerInvariant(), $name }
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS'), (($sums -join "`n") + "`n"))

Write-Host "Created $zip" -ForegroundColor Green
$sums | ForEach-Object { Write-Host "  $_" }
