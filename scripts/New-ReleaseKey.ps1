<#
.SYNOPSIS
    Makes the key Jarvis releases are signed with. Run once per fork of Jarvis.

.DESCRIPTION
    Jarvis only installs an update whose SHA256SUMS is signed with the release key, so someone who took
    over the GitHub account still can't push an update to everyone's PC.

    This writes the PRIVATE key to a file outside the repo (default: %UserProfile%\.jarvis\release-key.xml)
    and prints the PUBLIC key to put in Directory.Build.props as <UpdateKey>.

    KEEP A COPY of the private key somewhere safe that isn't GitHub (a USB stick, a password manager).
    Without it you can't publish an update that installed copies of Jarvis accept: everyone would have to
    download a new Jarvis.exe by hand. Never commit it and never upload it.

.EXAMPLE
    .\New-ReleaseKey.ps1
#>
[CmdletBinding()]
param([string]$Path = (Join-Path $env:USERPROFILE '.jarvis\release-key.xml'))

$ErrorActionPreference = 'Stop'
if (Test-Path $Path) { throw "$Path already exists. Releases are signed with it; delete it yourself only if you really mean to start over." }

$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 3072
try {
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $rsa.ToXmlString($true))
    # Only this Windows account (and administrators) can read it.
    & icacls.exe $Path /inheritance:r /grant:r "$($env:USERNAME):F" '*S-1-5-32-544:F' /Q | Out-Null
    $public = $rsa.ExportParameters($false)
    $key = [Convert]::ToBase64String($public.Modulus) + '.' + [Convert]::ToBase64String($public.Exponent)
}
finally {
    $rsa.Dispose()
}

Write-Host "Private key written to $Path" -ForegroundColor Green
Write-Host 'Back it up somewhere that is NOT GitHub. Put this in Directory.Build.props as <UpdateKey>:' -ForegroundColor Yellow
Write-Output $key
