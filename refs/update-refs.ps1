<#
.SYNOPSIS
    Regenerate the reference assemblies used by CI.

.DESCRIPTION
    The mod compiles against the game's own assemblies, which can't be shipped in a public repo
    or downloaded on a build server. Refasmer strips every method body and leaves only the public
    metadata the compiler needs - small enough to commit, and containing none of the game's code.

    Run this after a game update, or when the code starts referencing a new assembly (the build
    fails with "type or namespace not found" until it's added to the list below).

        .\refs\update-refs.ps1
#>
[CmdletBinding()]
param(
    [string] $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Restory'
)

$ErrorActionPreference = 'Stop'

# Only what the mod actually references. Keep this minimal - every entry is weight in the repo,
# and unused assemblies drift out of date silently.
#
# NOTE: the game's real code is Restory.Assembly, NOT Assembly-CSharp (which has ~16 types).
$needed = @(
    'Restory.Assembly'
    'Assembly-CSharp'
    'DOTween'
    'Newtonsoft.Json'
    'PWSMechanic'
    'Sirenix.OdinInspector.Attributes'
    'Sirenix.Serialization'
    'Sirenix.Utilities'
    'Unity.InputSystem'
    'Unity.TextMeshPro'
    'UnityEngine'
    'UnityEngine.AnimationModule'
    'UnityEngine.CoreModule'
    'UnityEngine.IMGUIModule'
    'UnityEngine.PhysicsModule'
    'UnityEngine.UI'
    'Zenject'
)

$managed = Join-Path $GameDir 'Restory_Data\Managed'
if (-not (Test-Path $managed)) {
    Write-Host "No Managed folder at: $managed" -ForegroundColor Red
    exit 1
}

# Refasmer is a dotnet global tool.
if (-not (Get-Command refasmer -ErrorAction SilentlyContinue)) {
    Write-Host 'Installing Refasmer...' -ForegroundColor Cyan
    dotnet tool install -g JetBrains.Refasmer.CliTool | Out-Null
    $env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
}

$out = Join-Path $PSScriptRoot 'Managed'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Get-ChildItem $out -Filter *.dll -ErrorAction SilentlyContinue | Remove-Item -Force

$missing = @()
# Refasmer writes harmless warnings to stderr. Windows PowerShell turns native-exe stderr into
# terminating errors, so relax that and judge success by whether the output file appeared.
$prevEAP = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
foreach ($name in $needed) {
    $src = Join-Path $managed "$name.dll"
    if (-not (Test-Path $src)) { $missing += $name; continue }
    # --omit-non-api-members=false keeps every member signature; only bodies are dropped.
    refasmer -c --omit-non-api-members=false -O $out $src 2>$null | Out-Null
    if (-not (Test-Path (Join-Path $out "$name.dll"))) { $missing += $name }
}
$ErrorActionPreference = $prevEAP

# Refasmer chokes on some Unity 6 modules; Cecil strips those instead.
$stillMissing = @()
foreach ($name in $missing) {
    $src = Join-Path $managed "$name.dll"
    if (-not (Test-Path $src)) { $stillMissing += $name; continue }
    Write-Host "Refasmer rejected $name; stripping with Cecil instead." -ForegroundColor DarkYellow
    & (Join-Path $PSScriptRoot 'strip-assembly.ps1') -Source $src -OutDir $out
    if (-not (Test-Path (Join-Path $out "$name.dll"))) { $stillMissing += $name }
}

if ($stillMissing.Count) {
    Write-Host "Could not produce references for: $($stillMissing -join ', ')" -ForegroundColor Red
}

$after = (Get-ChildItem $out -Filter *.dll | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Reference assemblies: {0} files, {1:N1} MB" -f `
    (Get-ChildItem $out -Filter *.dll).Count, $after) -ForegroundColor Green
