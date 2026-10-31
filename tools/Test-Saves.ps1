<#
Keep test runs out of your own save list.

EVERY copy of MENACE on a machine -- your install and every rig -- shares ONE
Saves folder (%USERPROFILE%\AppData\LocalLow\Overhype Studios\Menace\Saves).
A harness run writes mission-start autosaves there, evicts your oldest
autosaves to make room, and replaces latest.save (the game's "Continue") with
the test campaign. So: back up before a run, clean up after it.

    .\tools\Test-Saves.ps1 -Backup              # before a run; prints the backup folder
    .\tools\Test-Saves.ps1 -Restore             # after it; uses the newest backup
    .\tools\Test-Saves.ps1 -Restore -From <dir> # a specific backup

-Restore deletes ONLY autosaves (auto_*.save / .jpg, latest.jpg) written AFTER
the backup was taken and not present in it -- anything else new is left alone
and listed, because another program (or you) may have made it -- then puts back
every file the backup holds whose contents changed (evicted autosaves,
latest.save, the mod's coop_* working files).

Backups go to <repo>\_SaveBackups\ (gitignored).
#>
param(
    [switch]$Backup,
    [switch]$Restore,
    [string]$From
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$saves = Join-Path $env:USERPROFILE "AppData\LocalLow\Overhype Studios\Menace\Saves"
$root = Join-Path $repo "_SaveBackups"
if (-not (Test-Path $saves)) { throw "No Saves folder at $saves" }

if ($Backup) {
    $dir = Join-Path $root ("pre-run-" + (Get-Date -Format "yyyy-MM-dd-HHmmss"))
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item "$saves\*" $dir
    Write-Host "Backed up $(@(Get-ChildItem $dir).Count) files to $dir" -ForegroundColor Green
    return
}
if (-not $Restore) { throw "Use -Backup or -Restore." }

if (-not $From) {
    $From = (Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    if (-not $From) { throw "No backups in $root" }
}
$taken = (Get-Item $From).CreationTime
Write-Host "Restoring from $From (taken $taken)"

foreach ($f in Get-ChildItem $saves -File) {
    if (Test-Path (Join-Path $From $f.Name)) { continue }
    $isAuto = $f.Name -match '^auto_.*\.(save|jpg)$' -or $f.Name -eq 'latest.jpg'
    if ($isAuto -and $f.LastWriteTime -ge $taken) { Remove-Item $f.FullName -Force; Write-Host "  removed $($f.Name)" }
    else { Write-Host "  left alone (not clearly the run's): $($f.Name)" -ForegroundColor DarkYellow }
}
foreach ($b in Get-ChildItem $From -File) {
    $t = Join-Path $saves $b.Name
    if ((Test-Path $t) -and ((Get-FileHash $t).Hash -eq (Get-FileHash $b.FullName).Hash)) { continue }
    Copy-Item $b.FullName $t -Force
    Write-Host "  restored $($b.Name)"
}
Write-Host "Saves folder restored." -ForegroundColor Green
