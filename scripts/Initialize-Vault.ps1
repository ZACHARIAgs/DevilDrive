<#
.SYNOPSIS
    ADMIN ONLY, run once: seeds an empty GitHub repo as the Capstone vault.
    Easier: in the app, Vaults > New team vault does all of this (and creates the repo for you).

.DESCRIPTION
    Clones the empty repo to C:\CapstoneVault, adds the vault rules from vault-template
    (.gitattributes for LFS + locking, .gitignore for SolidWorks temp files, pdm.json for
    team settings) and pushes the first commit to 'main'.

    Before you run it:
      1. Create a PRIVATE, EMPTY repo on GitHub (no README, no .gitignore).
      2. Add each teammate as a collaborator with Write access.
      3. Edit vault-template\pdm.json: admins, team names and the Discord webhook URL.
         (You can also edit it later and check it in from Jarvis.)

.EXAMPLE
    .\Initialize-Vault.ps1 -RepoUrl https://github.com/your-team/capstone-vault.git
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RepoUrl,
    [string]$VaultPath = 'C:\CapstoneVault'
)

$ErrorActionPreference = 'Stop'
$template = Join-Path $PSScriptRoot '..\vault-template'

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed (exit code $LASTEXITCODE)." }
}

if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) { throw 'Git is not installed. Run Setup.cmd first.' }
& git lfs version *> $null
if ($LASTEXITCODE -ne 0) { throw 'Git LFS is not installed. Run Setup.cmd first.' }
Invoke-Git lfs install --skip-repo | Out-Null

if (-not (Test-Path (Join-Path $VaultPath '.git'))) {
    if ((Test-Path $VaultPath) -and (Get-ChildItem -Force $VaultPath | Select-Object -First 1)) {
        throw "$VaultPath exists and isn't empty."
    }
    Invoke-Git clone $RepoUrl $VaultPath
}

Push-Location $VaultPath
try {
    if ((& git rev-parse --verify --quiet HEAD) -and -not (Test-Path '.gitattributes')) {
        Write-Warning 'This repo already has commits. The vault files are added on top of them.'
    }

    foreach ($file in '.gitattributes', '.gitignore', 'pdm.json') {
        if (Test-Path $file) {
            Write-Host "Keeping existing $file"
        }
        else {
            Copy-Item (Join-Path $template $file) $file
            if ($file -eq 'pdm.json') {
                # Record this vault's folder so members who add it from the app get the same one.
                $json = Get-Content $file -Raw
                $json = $json -replace '"localPath":\s*"[^"]*"', ('"localPath": ' + (ConvertTo-Json $VaultPath))
                [IO.File]::WriteAllText((Join-Path (Get-Location) $file), $json)
            }
            Write-Host "Added $file"
        }
    }

    if (-not (Test-Path '.github')) {
        Copy-Item (Join-Path $template '.github') '.github' -Recurse
        Write-Host 'Added the vault guard (.github)'
    }

    Invoke-Git config lfs.locksverify true
    Invoke-Git add .gitattributes .gitignore pdm.json .github
    & git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) { Invoke-Git commit -m 'Initialize Capstone vault (LFS + locking rules, team settings)' }
    Invoke-Git branch -M main
    Invoke-Git push -u origin main

    # Confirms GitHub's LFS locking API is reachable with your credentials.
    Invoke-Git lfs locks | Out-Null
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host "Vault initialized at $VaultPath and pushed to $RepoUrl." -ForegroundColor Green
Write-Host 'Next: in Jarvis, Vaults > Invite teammates, and send them the message it gives you (see README).'
