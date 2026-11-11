#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$changelog = Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md'
$wanted = "## [$Version] - YYYY-MM-DD"
if (-not (Test-Path -LiteralPath $changelog -PathType Leaf)) {
    throw "CHANGELOG.md is missing: the release notes are its '$wanted' section"
}
$lines = @(Get-Content -LiteralPath $changelog -Encoding utf8)
$heading = "^## \[$([regex]::Escape($Version))\] - \d{4}-\d{2}-\d{2}\s*$"
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $heading) {
        $start = $i
        break
    }
}
if ($start -lt 0) {
    throw "CHANGELOG.md has no '$wanted' heading for this release"
}
# A section ends at the next version heading or at the link definitions Keep a Changelog puts at the bottom.
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## ' -or $lines[$i] -match '^\[[^\]]+\]:\s') {
        $end = $i
        break
    }
}
$body = (@($lines | Select-Object -Skip ($start + 1) -First ($end - $start - 1)) -join "`n").Trim()
if ($body.Length -eq 0) {
    throw "CHANGELOG.md's '## [$Version]' section is empty"
}
[System.IO.File]::WriteAllText($OutputPath, $body + "`n", [System.Text.UTF8Encoding]::new($false))
