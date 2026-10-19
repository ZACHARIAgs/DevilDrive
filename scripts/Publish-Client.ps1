<#
.SYNOPSIS
    Builds CapstonePDM.exe (self-contained, so teammates don't need .NET installed) and packs
    dist\CapstonePDM-Setup.zip with Setup.cmd, the setup script and the exe.

.DESCRIPTION
    Share the zip on GitHub Releases or Discord. Teammates unzip it and double-click Setup.cmd.
#>
[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'CapstonePDM-Setup'

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

& dotnet publish (Join-Path $root 'src\CapstonePdm.Client\CapstonePdm.Client.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o (Join-Path $dist 'publish')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item (Join-Path $dist 'publish\CapstonePDM.exe') $stage
Copy-Item (Join-Path $PSScriptRoot 'Setup.cmd') $stage
Copy-Item (Join-Path $PSScriptRoot 'Setup-CapstonePdm.ps1') $stage

# SolidWorks add-in (needs SolidWorks installed on this PC to build).
$addinProject = Join-Path $root 'src\CapstonePdm.SwAddin\CapstonePdm.SwAddin.csproj'
if (Test-Path 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SolidWorks.Interop.sldworks.dll') {
    & dotnet build $addinProject -c $Configuration -o (Join-Path $dist 'publish-addin')
    if ($LASTEXITCODE -ne 0) { throw 'Add-in build failed.' }
    $addinStage = Join-Path $stage 'SolidWorksAddin'
    New-Item -ItemType Directory -Force $addinStage | Out-Null
    Copy-Item (Join-Path $dist 'publish-addin\*.dll') $addinStage
    Copy-Item (Join-Path $PSScriptRoot 'Install-SolidWorksAddin.ps1') $stage
    Copy-Item (Join-Path $PSScriptRoot 'Install SolidWorks Add-in.cmd') $stage
}
else {
    Write-Warning 'SolidWorks is not installed on this PC, so the add-in was left out of the zip.'
}

$zip = Join-Path $dist 'CapstonePDM-Setup.zip'
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

Write-Host "Created $zip" -ForegroundColor Green
