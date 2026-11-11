#Requires -Version 7.3
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

$root = Split-Path -Parent $PSScriptRoot
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Find-Text([string] $path, [string] $pattern) {
    $match = [regex]::Match((Get-Content -LiteralPath (Join-Path $root $path) -Raw), $pattern)
    if ($match.Success) { $match.Groups[1].Value } else { $null }
}

function Get-Version {
    [xml] $props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $node = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $node -or $node.InnerText.Trim() -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw 'Directory.Build.props has no Version of the form 1.2.3'
    }
    $version = $node.InnerText.Trim()
    $plugins = [ordered]@{
        'plugin/unity/com.cranevalley.cluewren/package.json'              = Find-Text 'plugin/unity/com.cranevalley.cluewren/package.json' '"version"\s*:\s*"([^"]*)"'
        'plugin/unity/com.cranevalley.cluewren/Runtime/PlaytestClient.cs' = Find-Text 'plugin/unity/com.cranevalley.cluewren/Runtime/PlaytestClient.cs' 'PluginVersion\s*=\s*"([^"]*)"'
        'plugin/godot/addons/cluewren/plugin.cfg'                         = Find-Text 'plugin/godot/addons/cluewren/plugin.cfg' '(?m)^version\s*=\s*"([^"]*)"'
        'plugin/unreal/Cluewren/Cluewren.uplugin'                         = Find-Text 'plugin/unreal/Cluewren/Cluewren.uplugin' '"VersionName"\s*:\s*"([^"]*)"'
    }
    $wrong = @($plugins.GetEnumerator() | Where-Object { $_.Value -ne $version } | ForEach-Object { "$($_.Key) says '$($_.Value)'" })
    if ($wrong.Count -gt 0) {
        throw "Directory.Build.props says $version, but:`n  $($wrong -join "`n  ")"
    }
    $version
}

# The files git would commit: on a clean clone the tracked ones, locally also untracked ones that are not ignored, so a
# build's leftovers in a plugin folder are never packaged by accident.
function Get-SourceFiles([string] $folder, [string[]] $forbidden = @()) {
    $relative = [System.IO.Path]::GetRelativePath($root, $folder).Replace('\', '/')
    $listed = (git -C $root ls-files -z --cached --others --exclude-standard -- $relative) -join ''
    $files = @($listed.Split([char] 0, [System.StringSplitOptions]::RemoveEmptyEntries) |
        Sort-Object -Unique |
        Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
    if ($files.Count -eq 0) {
        throw "no files under $relative"
    }
    foreach ($file in $files) {
        $inside = $file.Substring($relative.Length + 1)
        foreach ($segment in $inside.Split('/')) {
            if ($forbidden -contains $segment) {
                throw "$file would be packaged: $relative must hold source only ($($forbidden -join ', ') are built output)"
            }
        }
    }
    foreach ($file in $files) {
        [pscustomobject] @{ Name = $file.Substring($relative.Length + 1); Source = Join-Path $root $file }
    }
}

function New-Zip([string] $path, [object[]] $entries, [DateTimeOffset] $stamp) {
    $sorted = [System.Collections.Generic.List[object]]::new()
    $entries | ForEach-Object { $sorted.Add($_) }
    $sorted.Sort([System.Comparison[object]] { param($a, $b) [string]::CompareOrdinal($a.Name, $b.Name) })
    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::CreateNew)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in $sorted) {
                $item = $zip.CreateEntry($entry.Name, [System.IO.Compression.CompressionLevel]::Optimal)
                $item.LastWriteTime = $stamp
                $to = $item.Open()
                $from = [System.IO.File]::OpenRead($entry.Source)
                try {
                    $from.CopyTo($to)
                }
                finally {
                    $from.Dispose()
                    $to.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-Folder([string] $folder, [string] $prefix) {
    Get-ChildItem -LiteralPath $folder -Recurse -File | ForEach-Object {
        [pscustomobject] @{
            Name   = $prefix + [System.IO.Path]::GetRelativePath($folder, $_.FullName).Replace('\', '/')
            Source = $_.FullName
        }
    }
}

$encoding = [Console]::OutputEncoding
[Console]::OutputEncoding = $utf8
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('cluewren-package-' + [guid]::NewGuid().ToString('N'))
try {
    $version = Get-Version
    if (Test-Path -LiteralPath $OutputDirectory) {
        if (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1) {
            throw "$OutputDirectory is not empty: give an empty or new folder, so no earlier asset is shipped with these"
        }
    }
    else {
        New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    }
    $out = (Resolve-Path -LiteralPath $OutputDirectory).Path
    # Entries carry the commit's time, not the checkout's, so a rebuild of one commit gives the same archive listing.
    $stamp = [DateTimeOffset]::FromUnixTimeSeconds([long] (git -C $root log -1 --format=%ct))

    $app = Join-Path $stage 'app'
    dotnet publish (Join-Path $root 'src/Cluewren/Cluewren.csproj') -c Release -r win-x64 --self-contained false -o $app `
        -p:SatelliteResourceLanguages=en -p:ContinuousIntegrationBuild=true --nologo
    foreach ($needed in 'cluewren.exe', 'cluewren.dll', 'Cluewren.Child.dll', 'Cluewren.Child.runtimeconfig.json',
        'Cluewren.Child.deps.json', 'Cluewren.Kit.dll', 'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $app $needed) -PathType Leaf)) {
            throw "the published app has no $needed"
        }
    }
    $said = @(& (Join-Path $app 'cluewren.exe') --version)
    if ($said.Count -ne 1 -or $said[0] -notmatch "^cluewren $([regex]::Escape($version))(\+[0-9a-f]+)?$") {
        throw "cluewren.exe --version said '$($said -join ' | ')', not cluewren $version"
    }
    $notices = 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.txt' | ForEach-Object {
        [pscustomobject] @{ Name = $_; Source = Join-Path $root $_ }
    }
    New-Zip (Join-Path $out "cluewren-$version-win-x64.zip") (@(Get-Folder $app '') + @($notices)) $stamp

    $plugins = @(
        @{ Zip = "cluewren-unity-$version.zip"; Folder = 'plugin/unity/com.cranevalley.cluewren'; Prefix = 'com.cranevalley.cluewren/'; Forbidden = @() },
        @{ Zip = "cluewren-godot-$version.zip"; Folder = 'plugin/godot/addons/cluewren'; Prefix = 'addons/cluewren/'; Forbidden = @() },
        @{ Zip = "cluewren-unreal-$version.zip"; Folder = 'plugin/unreal/Cluewren'; Prefix = 'Cluewren/'; Forbidden = @('Binaries', 'Intermediate') }
    )
    foreach ($plugin in $plugins) {
        $entries = Get-SourceFiles (Join-Path $root $plugin.Folder) $plugin.Forbidden | ForEach-Object {
            [pscustomobject] @{ Name = $plugin.Prefix + $_.Name; Source = $_.Source }
        }
        New-Zip (Join-Path $out $plugin.Zip) @($entries) $stamp
    }

    Push-Location $root
    try {
        dotnet tool restore
        dotnet tool run dotnet-CycloneDX (Join-Path $root 'src/Cluewren/Cluewren.csproj') --output (Join-Path $stage 'sbom') `
            --filename "cluewren-$version.cdx.json" --output-format Json --framework net10.0 --runtime win-x64 `
            --recursive --include-project-references --disable-package-restore `
            --set-name cluewren --set-version $version --set-type Application
    }
    finally {
        Pop-Location
    }
    Copy-Item -LiteralPath (Join-Path $stage "sbom/cluewren-$version.cdx.json") -Destination $out

    $assets = [System.Collections.Generic.List[string]]::new()
    Get-ChildItem -LiteralPath $out -File | ForEach-Object { $assets.Add($_.Name) }
    $assets.Sort([System.StringComparer]::Ordinal)
    $sums = foreach ($name in $assets) {
        '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $out $name)).Hash.ToLowerInvariant(), $name
    }
    [System.IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS.txt'), ($sums -join "`n") + "`n", $utf8)

    Get-ChildItem -LiteralPath $out -File | Sort-Object Name | Format-Table Name, Length -AutoSize | Out-String | Write-Host
}
finally {
    [Console]::OutputEncoding = $encoding
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
