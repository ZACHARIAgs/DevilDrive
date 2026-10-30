<#
.SYNOPSIS
    One-time Jarvis setup for a teammate's PC. Optional: DevilDrive.exe installs itself when you open it
    (Install Jarvis); this script does the same from a console, for the setup zip.

.DESCRIPTION
    1. Installs Git for Windows (includes Git LFS) with winget if it's missing.
    2. Turns on Git LFS.
    3. Asks for your name and email.
    4. Installs DevilDrive.exe, adds Desktop and Start menu shortcuts, and opens it.
    5. Optionally installs the SolidWorks add-in.

    The app does the rest: it signs you in to GitHub and finds the Solar Devils vault (Vaults, Find my
    vaults), or creates a new one for a team lead. So the same setup zip works for every team.

    Run it by double-clicking Setup.cmd. It's safe to run again: finished steps are skipped.

.PARAMETER RepoUrl
    Optional: also download this vault now (to -VaultPath). Normally you leave it out and pick the vault in the app.

.NOTES
    Optionally set $DefaultClientUrl below (a download link for DevilDrive.exe) if the exe isn't in the zip.
#>
[CmdletBinding()]
param(
    [string]$RepoUrl,
    [string]$VaultPath,
    [string]$ClientUrl
)

# Optional: where to download DevilDrive.exe when it isn't next to this script.
$DefaultClientUrl = ''   # e.g. https://github.com/YOUR-TEAM/jarvis/releases/latest/download/DevilDrive.exe
$DefaultVaultPath = 'C:\SolarDevilsVault'   # only used with -RepoUrl

$ErrorActionPreference = 'Stop'
if (-not $VaultPath) { $VaultPath = $DefaultVaultPath }
$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\DevilDrive'
$ExePath    = Join-Path $InstallDir 'DevilDrive.exe'

function Write-Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }
function Stop-Setup([string]$text) { Write-Host ''; Write-Host "SETUP STOPPED: $text" -ForegroundColor Red; exit 1 }

function Read-Default([string]$prompt, [string]$default) {
    $suffix = ''
    if ($default) { $suffix = " [$default]" }
    $answer = Read-Host "$prompt$suffix"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer.Trim()
}

function Update-SessionPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user    = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Find-Git {
    $cmd = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($candidate in @("$env:ProgramFiles\Git\cmd\git.exe", "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe")) {
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

function Invoke-Git {
    # Runs git and stops setup if it fails. Git writes progress to stderr, which is fine.
    & $script:Git @args
    if ($LASTEXITCODE -ne 0) { Stop-Setup "git $($args -join ' ') failed (exit code $LASTEXITCODE)." }
}

Write-Host 'Jarvis setup' -ForegroundColor White
Write-Host '------------------'

# 1. Git ------------------------------------------------------------------
Write-Step 'Checking for Git'
$script:Git = Find-Git
if (-not $script:Git) {
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        Start-Process 'https://git-scm.com/download/win'
        Stop-Setup 'Git is not installed and winget is not available. Install Git from the page that just opened (keep the default options), then run Setup again.'
    }
    Write-Host '    Installing Git for Windows (a Windows permission prompt may appear)...'
    & winget.exe install --id Git.Git --exact --source winget --accept-package-agreements --accept-source-agreements
    Update-SessionPath
    $script:Git = Find-Git
    if (-not $script:Git) { Stop-Setup 'Git installed, but it cannot be found yet. Restart your PC and run Setup again.' }
}
Write-Ok ((& $script:Git --version) -join ' ')

# 2. Git LFS ----------------------------------------------------------------
Write-Step 'Checking for Git LFS'
& $script:Git lfs version *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host '    Installing Git LFS...'
    & winget.exe install --id GitHub.GitLFS --exact --source winget --accept-package-agreements --accept-source-agreements
    Update-SessionPath
    & $script:Git lfs version *> $null
    if ($LASTEXITCODE -ne 0) { Stop-Setup 'Git LFS could not be installed. Ask an admin for help.' }
}
Invoke-Git lfs install --skip-repo | Out-Null
Write-Ok ((& $script:Git lfs version) -join ' ')

# 3. Who are you? -----------------------------------------------------------
Write-Step 'Your details (shown on every check-in)'
$currentName  = (& $script:Git config --global user.name) 2>$null
$currentEmail = (& $script:Git config --global user.email) 2>$null
$name  = Read-Default 'Your full name' $currentName
$email = Read-Default 'Your email (the one on your GitHub account)' $currentEmail
if (-not $name -or -not $email) { Stop-Setup 'Name and email are required.' }
if (-not $currentName)  { Invoke-Git config --global user.name $name }
if (-not $currentEmail) { Invoke-Git config --global user.email $email }

# 4. The vault (optional) -----------------------------------------------------
# Normally the app finds the vault (Vaults, Find my vaults). -RepoUrl downloads one now instead.
$clonedVault = $false
if ($RepoUrl) {
    Write-Step "Getting the vault into $VaultPath"
    if (Test-Path (Join-Path $VaultPath '.git')) {
        Write-Ok 'The vault is already on this PC. Skipping download.'
    }
    else {
        if ((Test-Path $VaultPath) -and (Get-ChildItem -Force $VaultPath | Select-Object -First 1)) {
            Stop-Setup "$VaultPath already exists and isn't empty. Move or rename it, then run Setup again."
        }
        Write-Host '    Downloading. A browser window may open so you can sign in to GitHub...'
        Invoke-Git clone $RepoUrl $VaultPath
    }
    Invoke-Git -C $VaultPath config user.name $name
    Invoke-Git -C $VaultPath config user.email $email
    Invoke-Git -C $VaultPath config lfs.locksverify true
    Invoke-Git -C $VaultPath config lfs.setlockablereadonly true
    Invoke-Git -C $VaultPath config core.longpaths true
    $clonedVault = $true
    Write-Ok 'Vault ready.'
}

# 5. The Jarvis app ---------------------------------------------------
Write-Step 'Installing DevilDrive'
New-Item -ItemType Directory -Force $InstallDir | Out-Null
if (-not $ClientUrl) { $ClientUrl = $DefaultClientUrl }
$besideScript = Join-Path $PSScriptRoot 'DevilDrive.exe'
if (Test-Path $besideScript) {
    Copy-Item $besideScript $ExePath -Force
    Write-Ok "Copied to $ExePath"
}
elseif ($ClientUrl) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $ClientUrl -OutFile $ExePath
    Write-Ok "Downloaded to $ExePath"
}
else {
    Write-Warn 'DevilDrive.exe was not found next to this script and no download link is set.'
    Write-Warn "Put DevilDrive.exe in $InstallDir yourself, then run Setup again for the shortcuts."
}

$settingsDir  = Join-Path $env:APPDATA 'DevilDrive'
$settingsFile = Join-Path $settingsDir 'settings.json'
if ($clonedVault -and -not (Test-Path $settingsFile)) {
    # Only for -RepoUrl, and never over existing settings (they list the user's vaults).
    New-Item -ItemType Directory -Force $settingsDir | Out-Null
    @{ vaultPath = $VaultPath; vaults = @($VaultPath) } | ConvertTo-Json | Set-Content -Encoding UTF8 $settingsFile
}

if (Test-Path $ExePath) {
    # Compare with SHA256SUMS on the release page to be sure this is the real file.
    Write-Ok ('DevilDrive.exe SHA-256: ' + (Get-FileHash $ExePath -Algorithm SHA256).Hash.ToLowerInvariant())
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in @([Environment]::GetFolderPath('Desktop'), (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))) {
        $shortcut = $shell.CreateShortcut((Join-Path $folder 'DevilDrive.lnk'))
        $shortcut.TargetPath = $ExePath
        $shortcut.IconLocation = "$ExePath,0"
        $shortcut.WorkingDirectory = $InstallDir
        $shortcut.Description = 'Check SolidWorks files in and out of your team''s vault'
        $shortcut.Save()
    }
    Write-Ok 'Shortcuts added to the Desktop and Start menu.'
}

# 6. SolidWorks add-in (optional) ----------------------------------------------
$addinInstaller = Join-Path $PSScriptRoot 'Install-SolidWorksAddin.ps1'
if ((Test-Path $addinInstaller) -and (Test-Path (Join-Path $PSScriptRoot 'SolidWorksAddin'))) {
    Write-Step 'SolidWorks add-in'
    $answer = Read-Default 'Install the DevilDrive add-in for SolidWorks? (needs admin permission) [Y/n]' 'Y'
    if ($answer -match '^[Yy]') {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $addinInstaller
        if ($LASTEXITCODE -ne 0) { Write-Warn 'The add-in was not installed. Run "Install SolidWorks Add-in.cmd" later to try again.' }
    }
    else {
        Write-Warn 'Skipped. Run "Install SolidWorks Add-in.cmd" any time to add it.'
    }
}

Write-Host ''
Write-Host 'All done! DevilDrive is opening (it''s also on your Desktop).' -ForegroundColor Green
Write-Host 'In the app: sign in to GitHub, then click Accept and download next to your team''s vault'
Write-Host '(or Find my vaults), or New team vault if you''re setting up a team. Always check out before editing.'
if (Test-Path $ExePath) { Start-Process -FilePath $ExePath }
