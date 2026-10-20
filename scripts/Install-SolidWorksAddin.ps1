<#
.SYNOPSIS
    Installs (or removes) the Jarvis add-in for SolidWorks 2020 and newer.

.DESCRIPTION
    Copies the add-in to C:\ProgramData\Jarvis\SolidWorksAddin and registers it with
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
$InstallDir = Join-Path $env:ProgramData 'Jarvis\SolidWorksAddin'
$Dll = Join-Path $InstallDir 'Jarvis.SwAddin.dll'
$RegAsm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$AddinGuid = '{9BD312D2-4FE8-4BEF-AB6C-91F6EA2CFB78}'
$StartupKey = "HKCU:\Software\SolidWorks\AddInsStartup\$AddinGuid"
# The add-in from before the rename (Capstone PDM). Same add-in ID, so it's replaced, not doubled.
$LegacyDir = Join-Path $env:ProgramData 'CapstonePDM\SolidWorksAddin'
$LegacyDll = Join-Path $LegacyDir 'CapstonePdm.SwAddin.dll'

function Test-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Find the files to install: next to this script (setup zip), or the local Release/Debug build.
if (-not $Uninstall -and -not $Source) {
    $candidates = @(
        (Join-Path $PSScriptRoot 'SolidWorksAddin'),
        (Join-Path $PSScriptRoot '..\src\Jarvis.SwAddin\bin\Release\net48'),
        (Join-Path $PSScriptRoot '..\src\Jarvis.SwAddin\bin\Debug\net48')
    )
    $Source = $candidates | Where-Object { Test-Path (Join-Path $_ 'Jarvis.SwAddin.dll') } | Select-Object -First 1
    if (-not $Source) { throw 'Add-in files not found. Build src\Jarvis.SwAddin first, or pass -Source.' }
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
        if ($Uninstall) { Write-Host 'Jarvis add-in removed.' -ForegroundColor Green }
        else { Write-Host 'Jarvis add-in installed. Start SolidWorks and look for the safe icon in the Task Pane.' -ForegroundColor Green }
        exit 0
    }
}

# ---- administrator part ----
function Remove-LegacyAddin {
    if (Test-Path $LegacyDll) { & $RegAsm $LegacyDll /unregister /nologo | Out-Host }
    if (Test-Path $LegacyDir) {
        & takeown.exe /F $LegacyDir /A /R /D Y *> $null
        & icacls.exe $LegacyDir /reset /T /C /Q | Out-Null
        Remove-Item (Split-Path $LegacyDir) -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host 'Removed the old Capstone PDM add-in.'
    }
}

try {
    if ($Uninstall) {
        if (Test-Path $InstallDir) {
            & takeown.exe /F $InstallDir /A /R /D Y *> $null
            & icacls.exe $InstallDir /reset /T /C /Q | Out-Null
        }
        if (Test-Path $Dll) { & $RegAsm $Dll /unregister /nologo | Out-Host }
        Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-LegacyAddin
        Write-Host 'Jarvis add-in removed.'
    }
    else {
        Remove-LegacyAddin
        New-Item -ItemType Directory -Force $InstallDir | Out-Null
        # Earlier installers could leave the files with no permissions at all; take them back and reset them.
        & takeown.exe /F $InstallDir /A /R /D Y *> $null
        & icacls.exe $InstallDir /reset /T /C /Q | Out-Null
        Copy-Item (Join-Path $Source '*.dll') $InstallDir -Force
        Get-ChildItem $InstallDir | Unblock-File
        # Only administrators (and Windows) may change the add-in files; everyone else can only read them.
        # Otherwise another account on this PC could drop a DLL here that SolidWorks would load.
        # Set on the folder only; the files then inherit it (inheritance flags on files would leave them unreadable).
        & icacls.exe $InstallDir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /Q | Out-Null
        $folderOk = $LASTEXITCODE -eq 0
        & icacls.exe (Join-Path $InstallDir '*') /reset /C /Q | Out-Null
        if (-not $folderOk -or $LASTEXITCODE -ne 0) { Write-Host 'Warning: could not restrict permissions on the add-in folder.' -ForegroundColor Yellow }
        & $RegAsm $Dll /codebase /nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "RegAsm failed (exit code $LASTEXITCODE)." }
        Write-Host "Jarvis add-in installed to $InstallDir."
    }
    if ($Elevated) { Start-Sleep -Seconds 2 }
    exit 0
}
catch {
    Write-Host $_ -ForegroundColor Red
    if ($Elevated) { Read-Host 'Press Enter to close' }
    exit 1
}
