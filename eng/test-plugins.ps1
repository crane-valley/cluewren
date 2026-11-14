#Requires -Version 7.3
[CmdletBinding()]
param(
    [string] $Godot = $env:CLUEWREN_GODOT,
    [string] $Unity = $env:CLUEWREN_UNITY,
    [string] $Unreal = $env:CLUEWREN_UNREAL,
    [ValidateSet('Godot', 'Unity', 'Unreal')] [string[]] $Engine = @('Godot', 'Unity', 'Unreal'),
    [string] $WorkDirectory = (Join-Path $env:TEMP 'cluewren-plugin-tests'),
    [ValidateRange(1, 1440)] [int] $TimeoutMinutes = 60,
    [switch] $AllowSkip
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$utf8 = [System.Text.UTF8Encoding]::new($false)

# The files git would commit, as package.ps1 packages them, so a build's leftovers in a plugin folder are not tested.
function Copy-Source([string] $folder, [string] $destination, [string[]] $skip = @()) {
    $listed = (git -C $root ls-files -z --cached --others --exclude-standard -- $folder) -join ''
    $files = @($listed.Split([char] 0, [System.StringSplitOptions]::RemoveEmptyEntries) |
        Sort-Object -Unique |
        Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
    if ($files.Count -eq 0) {
        throw "no files under $folder"
    }
    foreach ($file in $files) {
        $inside = $file.Substring($folder.Length + 1)
        if (@($inside.Split('/') | Where-Object { $skip -contains $_ }).Count -gt 0) {
            continue
        }
        $to = Join-Path $destination $inside
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $to
    }
}

function Write-Text([string] $path, [string] $text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

function Invoke-Tool([string] $file, [string] $arguments, [switch] $Quiet) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($file, $arguments)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $Quiet.IsPresent
    $info.RedirectStandardError = $Quiet.IsPresent
    $process = [System.Diagnostics.Process]::Start($info)
    try {
        if ($Quiet) {
            # Read and dropped: a redirected pipe nobody reads stops the tool once it fills.
            $process.BeginOutputReadLine()
            $process.BeginErrorReadLine()
        }
        if (-not $process.WaitForExit([timespan]::FromMinutes($TimeoutMinutes))) {
            $process.Kill($true)
            throw "$(Split-Path -Leaf $file) was still running after $TimeoutMinutes minutes and was stopped"
        }
        $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

function New-Result([int] $passed, [int] $failed, [string[]] $problems, [string[]] $files) {
    [pscustomobject] @{
        Status   = if ($problems.Count -eq 0 -and $failed -eq 0 -and $passed -gt 0) { 'PASSED' } else { 'FAILED' }
        Passed   = $passed
        Failed   = $failed
        Problems = @($problems)
        Files    = @($files)
    }
}

function Test-Godot([string] $folder) {
    if (-not (Test-Path -LiteralPath $Godot -PathType Leaf)) {
        throw "no Godot executable at $Godot"
    }
    # A copy, so nothing a run writes lands in plugin/godot, which package.ps1 ships.
    $project = Join-Path $folder 'project'
    Copy-Source 'plugin/godot' $project
    $log = Join-Path $folder 'godot.log'
    $code = Invoke-Tool $Godot "--headless --path `"$project`" --log-file `"$log`" -s res://tests/rc_test.gd"
    $lines = if (Test-Path -LiteralPath $log -PathType Leaf) { @(Get-Content -LiteralPath $log) } else { @() }
    $problems = [System.Collections.Generic.List[string]]::new()
    if ($code -ne 0) {
        $problems.Add("exit code $code")
    }
    # A script error stops only the check it is in, and rc_test.gd still exits 0, so it must be counted here.
    $errors = @($lines | Where-Object { $_ -match '^(SCRIPT |USER )?ERROR:' })
    if ($errors.Count -gt 0) {
        $problems.Add("$($errors.Count) error lines")
    }
    $summary = @($lines | Where-Object { $_ -match '^\d+ passed, \d+ failed$' })
    $passed = 0
    $failed = 0
    if ($summary.Count -eq 1 -and $summary[0] -match '^(\d+) passed, (\d+) failed$') {
        $passed = [int] $Matches[1]
        $failed = [int] $Matches[2]
    }
    else {
        $problems.Add("$($summary.Count) summary lines, not 1")
    }
    New-Result $passed $failed $problems @($log)
}

function Test-Unity([string] $folder) {
    $editor = if (Test-Path -LiteralPath $Unity -PathType Container) { Join-Path $Unity 'Editor/Unity.exe' } else { $Unity }
    if (-not (Test-Path -LiteralPath $editor -PathType Leaf)) {
        throw "no Unity editor at $Unity"
    }
    $builtIn = Join-Path (Split-Path -Parent $editor) 'Data/Resources/PackageManager/BuiltInPackages'
    $dependencies = [ordered] @{
        'com.cranevalley.cluewren' = 'file:' + (Join-Path $root 'plugin/unity/com.cranevalley.cluewren').Replace('\', '/')
        'com.unity.inputsystem'    = '1.20.1'
    }
    # These differ by editor; a new project gets the versions its editor ships.
    foreach ($name in 'com.unity.test-framework', 'com.unity.ugui') {
        $dependencies[$name] = (Get-Content -LiteralPath (Join-Path $builtIn "$name/package.json") -Raw | ConvertFrom-Json).version
    }
    foreach ($module in 'ai', 'animation', 'assetbundle', 'audio', 'director', 'imageconversion', 'imgui', 'jsonserialize',
        'particlesystem', 'physics', 'physics2d', 'screencapture', 'terrain', 'tilemap', 'ui', 'uielements', 'unitywebrequest',
        'unitywebrequestassetbundle', 'unitywebrequestaudio', 'unitywebrequesttexture', 'unitywebrequestwww', 'video') {
        $dependencies["com.unity.modules.$module"] = '1.0.0'
    }
    $project = Join-Path $folder 'project'
    New-Item -ItemType Directory -Force -Path (Join-Path $project 'Assets') | Out-Null
    # Without it, Unity takes the project for a new one and adds its default packages to the manifest.
    $version, $revision = (Get-Item -LiteralPath $editor).VersionInfo.ProductVersion.Split('_')
    Write-Text (Join-Path $project 'ProjectSettings/ProjectVersion.txt') (
        "m_EditorVersion: $version`nm_EditorVersionWithRevision: $version ($revision)`n")
    # A new project has the Input Manager alone, which leaves ENABLE_INPUT_SYSTEM undefined and the package's Input
    # System code compiled out; 2 is both. Unity fills in the rest of the file on the first open.
    $settings = Join-Path $project 'ProjectSettings/ProjectSettings.asset'
    Write-Text $settings "%YAML 1.1`n%TAG !u! tag:unity3d.com,2011:`n--- !u!129 &1`nPlayerSettings:`n  activeInputHandler: 2`n"
    $manifest = [ordered] @{ dependencies = $dependencies; testables = @('com.cranevalley.cluewren') }
    Write-Text (Join-Path $project 'Packages/manifest.json') ($manifest | ConvertTo-Json -Depth 4)
    $results = Join-Path $folder 'results.xml'
    $log = Join-Path $folder 'editor.log'
    $code = Invoke-Tool $editor ("-batchmode -projectPath `"$project`" -runTests -testPlatform EditMode " +
        "-assemblyNames Cluewren.Tests -testResults `"$results`" -logFile `"$log`"")
    $problems = [System.Collections.Generic.List[string]]::new()
    if ($code -ne 0) {
        $problems.Add("exit code $code")
    }
    if (-not (Select-String -LiteralPath $settings -Pattern '^\s*activeInputHandler: 2\s*$' -Quiet)) {
        $problems.Add("$settings no longer has activeInputHandler: 2")
    }
    $passed = 0
    $failed = 0
    if (Test-Path -LiteralPath $results -PathType Leaf) {
        [xml] $xml = Get-Content -LiteralPath $results -Raw
        $run = $xml.SelectSingleNode('/test-run')
        $passed = [int] $run.GetAttribute('passed')
        $failed = [int] $run.GetAttribute('failed')
        foreach ($case in $xml.SelectNodes('//test-case[@result="Failed"]')) {
            Write-Host "FAIL $($case.GetAttribute('fullname'))"
        }
    }
    else {
        $problems.Add('no results file')
    }
    New-Result $passed $failed $problems @($results, $log)
}

function Test-Unreal([string] $folder) {
    $build = Join-Path $Unreal 'Engine/Build/BatchFiles/Build.bat'
    $editor = Join-Path $Unreal 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
    foreach ($file in $build, $editor) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "no ${file}: give the engine's root folder, the one that holds Engine"
        }
    }
    $project = Join-Path $folder 'project'
    Copy-Source 'plugin/unreal/Cluewren' (Join-Path $project 'Plugins/Cluewren') @('Binaries', 'Intermediate')
    $uproject = Join-Path $project 'CluewrenTests.uproject'
    Write-Text $uproject (@{
            FileVersion       = 3
            EngineAssociation = ''
            Modules           = @(@{ Name = 'CluewrenTests'; Type = 'Runtime'; LoadingPhase = 'Default' })
            Plugins           = @(@{ Name = 'Cluewren'; Enabled = $true })
        } | ConvertTo-Json -Depth 4)
    Write-Text (Join-Path $project 'Source/CluewrenTestsEditor.Target.cs') @'
using UnrealBuildTool;

public class CluewrenTestsEditorTarget : TargetRules
{
	public CluewrenTestsEditorTarget(TargetInfo Target) : base(Target)
	{
		Type = TargetType.Editor;
		DefaultBuildSettings = BuildSettingsVersion.Latest;
		IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
		ExtraModuleNames.Add("CluewrenTests");
	}
}
'@
    Write-Text (Join-Path $project 'Source/CluewrenTests/CluewrenTests.Build.cs') @'
using UnrealBuildTool;

public class CluewrenTests : ModuleRules
{
	public CluewrenTests(ReadOnlyTargetRules Target) : base(Target)
	{
		PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine" });
	}
}
'@
    Write-Text (Join-Path $project 'Source/CluewrenTests/CluewrenTests.cpp') @'
#include "Modules/ModuleManager.h"

IMPLEMENT_PRIMARY_GAME_MODULE(FDefaultGameModuleImpl, CluewrenTests, "CluewrenTests");
'@
    $buildLog = Join-Path $folder 'build.log'
    $code = Invoke-Tool $build "CluewrenTestsEditor Win64 Development -Project=`"$uproject`" -WaitMutex -Log=`"$buildLog`""
    if ($code -ne 0) {
        $problems = @("the build failed with exit code $code")
        if ((Test-Path -LiteralPath $buildLog -PathType Leaf) -and
            (Select-String -LiteralPath $buildLog -Pattern 'longer than 260 characters' -SimpleMatch -Quiet)) {
            $problems += "its paths are too long: give a shorter -WorkDirectory than $WorkDirectory"
        }
        return New-Result 0 0 $problems @($buildLog)
    }
    $report = Join-Path $folder 'report'
    $log = Join-Path $folder 'editor.log'
    # Unreal's -Key="value" form, which is why Invoke-Tool takes the command line as one string.
    $code = Invoke-Tool $editor ("`"$uproject`" -ExecCmds=`"Automation RunTests Cluewren;Quit`" " +
        "-TestExit=`"Automation Test Queue Empty`" -ReportExportPath=`"$report`" -abslog=`"$log`" " +
        '-unattended -nullrhi -nosplash -nosound -nop4') -Quiet
    $problems = [System.Collections.Generic.List[string]]::new()
    if ($code -ne 0) {
        $problems.Add("exit code $code")
    }
    $passed = 0
    $failed = 0
    $index = Join-Path $report 'index.json'
    if (Test-Path -LiteralPath $index -PathType Leaf) {
        $json = Get-Content -LiteralPath $index -Raw | ConvertFrom-Json
        $passed = $json.succeeded + $json.succeededWithWarnings
        $failed = $json.failed + $json.notRun
        foreach ($test in @($json.tests | Where-Object { $_.state -ne 'Success' })) {
            Write-Host "FAIL $($test.fullTestPath) ($($test.state))"
        }
    }
    else {
        $problems.Add('no report')
    }
    New-Result $passed $failed $problems @($index, $log, $buildLog)
}

$work = [System.IO.Path]::GetFullPath($WorkDirectory)
if (($work + '\').StartsWith($root.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "$work is inside the repository: give a -WorkDirectory outside $root"
}
$run = Join-Path $work (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
New-Item -ItemType Directory -Path $run | Out-Null

$paths = @{ Godot = $Godot; Unity = $Unity; Unreal = $Unreal }
$tests = @{ Godot = ${function:Test-Godot}; Unity = ${function:Test-Unity}; Unreal = ${function:Test-Unreal} }
$hints = @{ Godot = '-Godot or CLUEWREN_GODOT'; Unity = '-Unity or CLUEWREN_UNITY'; Unreal = '-Unreal or CLUEWREN_UNREAL' }
$lines = foreach ($name in 'Godot', 'Unity', 'Unreal') {
    if ($Engine -notcontains $name) {
        continue
    }
    if ([string]::IsNullOrWhiteSpace($paths[$name])) {
        [pscustomobject] @{ Status = 'SKIPPED'; Line = "${name}: SKIPPED, no path given ($($hints[$name]))" }
        continue
    }
    $folder = Join-Path $run $name.ToLowerInvariant()
    New-Item -ItemType Directory -Path $folder | Out-Null
    Write-Host "${name}: running in $folder"
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $result = & $tests[$name] $folder
    }
    catch {
        $result = New-Result 0 0 @($_.Exception.Message) @()
    }
    $took = $clock.Elapsed.ToString('hh\:mm\:ss')
    # An Unreal project weighs gigabytes once built; one that failed is kept to look into.
    $project = Join-Path $folder 'project'
    if ($result.Status -eq 'PASSED' -and (Test-Path -LiteralPath $project)) {
        try {
            Remove-Item -LiteralPath $project -Recurse -Force
        }
        catch {
            Write-Warning "could not remove ${project}: $($_.Exception.Message)"
        }
    }
    $files = @($result.Files | Where-Object { Test-Path -LiteralPath $_ })
    $line = "${name}: $($result.Status), $($result.Passed) passed, $($result.Failed) failed, in $took"
    if ($result.Problems.Count -gt 0) {
        $line += "; $($result.Problems -join '; ')"
    }
    $line += "; files: $(if ($files.Count -gt 0) { $files -join ', ' } else { $folder })"
    [pscustomobject] @{ Status = $result.Status; Line = $line }
}

Write-Host ''
foreach ($line in $lines) {
    Write-Host $line.Line
}
$failed = @($lines | Where-Object { $_.Status -eq 'FAILED' }).Count
$skipped = @($lines | Where-Object { $_.Status -eq 'SKIPPED' }).Count
if ($failed -gt 0 -or ($skipped -gt 0 -and -not $AllowSkip)) {
    Write-Host "$failed failed, $skipped skipped$(if ($skipped -gt 0 -and -not $AllowSkip) { ' (pass -AllowSkip to accept skips)' })"
    exit 1
}
