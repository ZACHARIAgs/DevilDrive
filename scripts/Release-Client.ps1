<#
.SYNOPSIS
    Publishes a new Jarvis version as a GitHub release. Teammates' apps find it the next time
    they start and offer to update (checking every file against SHA256SUMS first).

.DESCRIPTION
    1. Bump <Version> in Directory.Build.props, commit and push.
    2. Run this script. It builds everything (Publish-Client.ps1), then creates release v<Version> on
       the code repo with Jarvis.exe, Jarvis-SolidWorksAddin.zip, Jarvis-Setup.zip and
       SHA256SUMS attached.

    Uses the GitHub sign-in Git already saved on this PC; you need write access to the code repo.
    If the code repo is private, teammates need read access to it for updates to download.

.EXAMPLE
    .\Release-Client.ps1 -Notes 'Review step, BOM export, automatic updates.'
#>
[CmdletBinding()]
param(
    [string]$Notes = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dist = Join-Path $root 'dist'

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
$repo = ($props.Project.PropertyGroup | ForEach-Object { $_.UpdateRepo } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version -or -not $repo) { throw 'Version or UpdateRepo is missing from Directory.Build.props.' }
$tag = "v$version"

# The release points at the current commit, so it must be on GitHub already.
$head = (& git -C $root rev-parse HEAD).Trim()
& git -C $root fetch --quiet origin
$pushed = & git -C $root branch -r --contains $head
if (-not $pushed) { throw "Commit $head isn't pushed to GitHub yet. Push it first, then run this again." }
if (& git -C $root status --porcelain --untracked-files=no) { throw 'There are uncommitted changes. Commit (and push) them first.' }

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'Publish-Client.ps1')
}
$files = @('Jarvis.exe', 'Jarvis-SolidWorksAddin.zip', 'Jarvis-Setup.zip', 'CapstonePDM.exe', 'CapstonePDM-SolidWorksAddin.zip', 'SHA256SUMS') |
    ForEach-Object { Join-Path $dist $_ } | Where-Object { Test-Path $_ }
if (-not ($files | Where-Object { $_ -like '*SHA256SUMS' })) { throw 'dist\SHA256SUMS is missing. Run without -SkipBuild.' }

# Token from Git's credential manager (never printed).
# Fed from a file through cmd: piping from PowerShell (or .NET's stdin) adds an encoding mark Git rejects.
$request = Join-Path ([IO.Path]::GetTempPath()) ('capstone-cred-' + [Guid]::NewGuid().ToString('N') + '.txt')
[IO.File]::WriteAllText($request, "protocol=https`nhost=github.com`n`n", (New-Object System.Text.UTF8Encoding $false))
try {
    $fill = & cmd.exe /c "git -c credential.interactive=never credential fill < `"$request`""
}
finally {
    Remove-Item $request -ErrorAction SilentlyContinue
}
$token = ($fill | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
if (-not $token) { throw 'No saved GitHub sign-in found. Run "git fetch" in the repo once to sign in, then try again.' }
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'Jarvis-release' }

try {
    Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$repo/releases/tags/$tag" | Out-Null
    throw "Release $tag already exists. Bump <Version> in Directory.Build.props first."
}
catch [System.Net.WebException] {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
}

$sums = Get-Content (Join-Path $dist 'SHA256SUMS') -Raw
$body = @{
    tag_name         = $tag
    target_commitish = $head
    name             = "Jarvis $version"
    body             = ($Notes.Trim() + "`n`n**SHA-256 checksums**`n``````n$sums``````").Trim()
} | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' -Body $body `
    -Uri "https://api.github.com/repos/$repo/releases"
Write-Host "Created release $tag" -ForegroundColor Green

foreach ($file in $files) {
    $name = Split-Path $file -Leaf
    Write-Host "  uploading $name..."
    $uploadUrl = "https://uploads.github.com/repos/$repo/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($name))"
    Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $file -Uri $uploadUrl | Out-Null
}
Write-Host "Done: $($release.html_url)" -ForegroundColor Green
