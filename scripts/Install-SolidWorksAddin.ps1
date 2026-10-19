<#
.SYNOPSIS
    Installs (or removes) the Capstone PDM add-in for SolidWorks 2024 and newer.

.DESCRIPTION
    Copies the add-in to C:\ProgramData\CapstonePDM\SolidWorksAddin and registers it with
    RegAsm so every installed SolidWorks version loads it. Registration needs administrator
    rights, so Windows asks for permission. Close SolidWorks first.

.EXAMPLE
    .\Install-SolidWorksAddin.ps1
    .\Install-SolidWorksAddin.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Source,
    [switch]$Uninstall,
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$InstallDir = Join-Path $env:ProgramData 'CapstonePDM\SolidWorksAddin'
$Dll = Join-Path $InstallDir 'CapstonePdm.SwAddin.dll'
$RegAsm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$AddinGuid = '{9BD312D2-4FE8-4BEF-AB6C-91F6EA2CFB78}'
$StartupKey = "HKCU:\Software\SolidWorks\AddInsStartup\$AddinGuid"

function Test-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Find the files to install: next to this script (setup zip), or the local Release/Debug build.
if (-not $Uninstall -and -not $Source) {
    $candidates = @(
        (Join-Path $PSScriptRoot 'SolidWorksAddin'),
        (Join-Path $PSScriptRoot '..\src\CapstonePdm.SwAddin\bin\Release\net48'),
        (Join-Path $PSScriptRoot '..\src\CapstonePdm.SwAddin\bin\Debug\net48')
    )
    $Source = $candidates | Where-Object { Test-Path (Join-Path $_ 'CapstonePdm.SwAddin.dll') } | Select-Object -First 1
    if (-not $Source) { throw 'Add-in files not found. Build src\CapstonePdm.SwAddin first, or pass -Source.' }
}
if ($Source) { $Source = (Resolve-Path $Source).Path }

if (-not $Elevated) {
    if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) {
        Write-Host 'SolidWorks is running. Close it, then run this again.' -ForegroundColor Red
        exit 1
    }

    # Per-user "load at startup" flag. Set here, before elevating, so it lands in YOUR registry
    # even if the admin prompt is answered with a different account.
    if ($Uninstall) {
        Remove-Item $StartupKey -ErrorAction SilentlyContinue
    }
    else {
        New-Item $StartupKey -Force | Out-Null
        Set-ItemProperty $StartupKey -Name '(default)' -Value 1 -Type DWord
    }

    if (-not (Test-Admin)) {
        Write-Host 'Windows will ask for administrator permission to register the add-in...'
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated')
        if ($Uninstall) { $argList += '-Uninstall' } else { $argList += @('-Source', "`"$Source`"") }
        $process = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            Write-Host 'Add-in registration failed or was cancelled.' -ForegroundColor Red
            exit $process.ExitCode
        }
        if ($Uninstall) { Write-Host 'Capstone PDM add-in removed.' -ForegroundColor Green }
        else { Write-Host 'Capstone PDM add-in installed. Start SolidWorks and look for the safe icon in the Task Pane.' -ForegroundColor Green }
        exit 0
    }
}

# ---- administrator part ----
try {
    if ($Uninstall) {
        if (Test-Path $Dll) { & $RegAsm $Dll /unregister /nologo | Out-Host }
        Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host 'Capstone PDM add-in removed.'
    }
    else {
        New-Item -ItemType Directory -Force $InstallDir | Out-Null
        Copy-Item (Join-Path $Source '*.dll') $InstallDir -Force
        Get-ChildItem $InstallDir | Unblock-File
        & $RegAsm $Dll /codebase /nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "RegAsm failed (exit code $LASTEXITCODE)." }
        Write-Host "Capstone PDM add-in installed to $InstallDir."
    }
    if ($Elevated) { Start-Sleep -Seconds 2 }
    exit 0
}
catch {
    Write-Host $_ -ForegroundColor Red
    if ($Elevated) { Read-Host 'Press Enter to close' }
    exit 1
}
