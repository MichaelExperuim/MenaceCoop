# WHERE THE GAME AND THE TEST RIGS LIVE -- one answer for every script.
#
# Dot-source it:   . "$PSScriptRoot\tools\RigPaths.ps1"   (from the repo root)
#                  . "$PSScriptRoot\RigPaths.ps1"         (from tools\)
#
# Resolution, first match wins:
#   1. rig.local.ps1 at the repo root (gitignored) -- set $env:MENACE_GAME_DIR /
#      $env:MENACE_RIG_ROOT there once and forget about it.
#   2. The environment variables MENACE_GAME_DIR and MENACE_RIG_ROOT.
#   3. The repo checked out INSIDE the game folder (<game>\_CoopModDev) -- the
#      original layout: the game is the repo's parent.
#   4. Steam's default library.
# The rigs are siblings of the game by default (<steamapps>\common\MenaceM1..M4):
# New-Rig.ps1 hardlinks the game files, and hardlinks only work on one drive.

$RepoRoot = Split-Path $PSScriptRoot -Parent
$local = Join-Path $RepoRoot "rig.local.ps1"
if (Test-Path $local) { . $local }

$GameDir = $env:MENACE_GAME_DIR
if (-not $GameDir) {
    $parent = Split-Path $RepoRoot -Parent
    if (Test-Path (Join-Path $parent "Menace.exe")) { $GameDir = $parent }
    else { $GameDir = Join-Path ${env:ProgramFiles(x86)} "Steam\steamapps\common\Menace" }
}
$RigRoot = $env:MENACE_RIG_ROOT
if (-not $RigRoot) { $RigRoot = Split-Path $GameDir -Parent }

# Rig n (1 = host, 2-4 = guests): <RigRoot>\MenaceM<n>.
function Get-RigPath([int]$n) { Join-Path $RigRoot "MenaceM$n" }

# A dotnet that actually has an SDK: a runtime-only dotnet on PATH cannot build.
# Tries PATH, then DOTNET_ROOT, then the per-user install (%LOCALAPPDATA%\Microsoft\dotnet).
function Get-Dotnet {
    $candidates = @()
    $onPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if ($onPath) { $candidates += $onPath }
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT "dotnet.exe") }
    $candidates += (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        $sdks = & $c --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $c }
    }
    throw "No .NET SDK found (tried: $($candidates -join ', ')). Install the .NET 6+ SDK."
}
