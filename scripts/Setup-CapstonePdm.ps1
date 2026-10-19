<#
.SYNOPSIS
    One-time Capstone PDM setup for a teammate's PC.

.DESCRIPTION
    1. Installs Git for Windows (includes Git LFS) with winget if it's missing.
    2. Turns on Git LFS.
    3. Asks for your name, email and GitHub username.
    4. Clones the vault to C:\CapstoneVault. A browser window opens so you can sign in to GitHub.
    5. Installs CapstonePDM.exe and adds Desktop and Start menu shortcuts.

    Run it by double-clicking Setup.cmd. It's safe to run again: finished steps are skipped.

.NOTES
    Admins: set $DefaultRepoUrl (and optionally $DefaultClientUrl) below before sharing.
#>
[CmdletBinding()]
param(
    [string]$RepoUrl,
    [string]$VaultPath = 'C:\CapstoneVault',
    [string]$ClientUrl
)

# >>> Admins: fill these in before sharing the script <<<
$DefaultRepoUrl   = 'https://github.com/aturne59-stack/capstone-vault.git'
$DefaultClientUrl = ''   # e.g. https://github.com/YOUR-TEAM/capstone-pdm/releases/latest/download/CapstonePDM.exe

$ErrorActionPreference = 'Stop'
$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\CapstonePDM'
$ExePath    = Join-Path $InstallDir 'CapstonePDM.exe'

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

Write-Host 'Capstone PDM setup' -ForegroundColor White
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
$login = Read-Default 'Your GitHub username' ''
if (-not $name -or -not $email) { Stop-Setup 'Name and email are required.' }
if (-not $currentName)  { Invoke-Git config --global user.name $name }
if (-not $currentEmail) { Invoke-Git config --global user.email $email }

# 4. Clone the vault --------------------------------------------------------
Write-Step "Getting the vault into $VaultPath"
if (-not $RepoUrl) { $RepoUrl = $DefaultRepoUrl }
if ($RepoUrl -like '*YOUR-TEAM*') { $RepoUrl = Read-Default 'Vault repo URL (ask an admin)' '' }
if (-not $RepoUrl) { Stop-Setup 'No vault repo URL.' }

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
if ($login) { Invoke-Git -C $VaultPath config pdm.githubLogin $login }
Write-Ok 'Vault ready.'

# 5. The Capstone PDM app ---------------------------------------------------
Write-Step 'Installing the Capstone PDM app'
New-Item -ItemType Directory -Force $InstallDir | Out-Null
if (-not $ClientUrl) { $ClientUrl = $DefaultClientUrl }
$besideScript = Join-Path $PSScriptRoot 'CapstonePDM.exe'
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
    Write-Warn 'CapstonePDM.exe was not found next to this script and no download link is set.'
    Write-Warn "Put CapstonePDM.exe in $InstallDir yourself, then run Setup again for the shortcuts."
}

$settingsDir = Join-Path $env:APPDATA 'CapstonePDM'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
@{ vaultPath = $VaultPath } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $settingsDir 'settings.json')

if (Test-Path $ExePath) {
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in @([Environment]::GetFolderPath('Desktop'), (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))) {
        $shortcut = $shell.CreateShortcut((Join-Path $folder 'Capstone PDM.lnk'))
        $shortcut.TargetPath = $ExePath
        $shortcut.IconLocation = "$ExePath,0"
        $shortcut.WorkingDirectory = $VaultPath
        $shortcut.Description = 'Check files in and out of the Capstone vault'
        $shortcut.Save()
    }
    Write-Ok 'Shortcuts added to the Desktop and Start menu.'
}

# 6. SolidWorks add-in (optional) ----------------------------------------------
$addinInstaller = Join-Path $PSScriptRoot 'Install-SolidWorksAddin.ps1'
if ((Test-Path $addinInstaller) -and (Test-Path (Join-Path $PSScriptRoot 'SolidWorksAddin'))) {
    Write-Step 'SolidWorks add-in'
    $answer = Read-Default 'Install the Capstone PDM add-in for SolidWorks? (needs admin permission) [Y/n]' 'Y'
    if ($answer -match '^[Yy]') {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $addinInstaller
        if ($LASTEXITCODE -ne 0) { Write-Warn 'The add-in was not installed. Run "Install SolidWorks Add-in.cmd" later to try again.' }
    }
    else {
        Write-Warn 'Skipped. Run "Install SolidWorks Add-in.cmd" any time to add it.'
    }
}

Write-Host ''
Write-Host 'All done! Open "Capstone PDM" from your Desktop.' -ForegroundColor Green
Write-Host "Always open SolidWorks files from $VaultPath, and check out before editing."
