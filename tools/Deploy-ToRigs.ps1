<#
Copy the built MenaceCoop.dll into every test rig's Mods\ folder.

    .\tools\Deploy-ToRigs.ps1             # MenaceM1..M4, whichever exist
    .\tools\Deploy-ToRigs.ps1 -Build      # build first (dev build, no auto-deploy), then copy

A plain `dotnet build` also deploys to the rigs (DeployToMelonClones in the
csproj); this script is for when you built with -p:NoDeploy=true -- e.g. while a
run was going -- and want the rigs updated afterwards. It refuses while a rig is
running, because Windows locks a loaded DLL and the copy would silently fail.
#>
param([switch]$Build)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\RigPaths.ps1"

$proj = Join-Path $RepoRoot "MenaceCoopMelon\MenaceCoopMelon.csproj"
$dll  = Join-Path $RepoRoot "MenaceCoopMelon\bin\Release\MenaceCoop.dll"

if (@(Get-Process Menace -ErrorAction SilentlyContinue | Where-Object { $_.Path -match '\\MenaceM[1-4]\\' }).Count -gt 0) {
    throw "A rig is running -- close it first (the loaded DLL is locked)."
}
if ($Build) {
    & (Get-Dotnet) build $proj -c Release --no-incremental -p:NoDeploy=true -p:GameDir="$GameDir"
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}
if (-not (Test-Path $dll)) { throw "No build at $dll -- run with -Build." }

$hash = (Get-FileHash $dll -Algorithm MD5).Hash.Substring(0, 8).ToLower()
$n = 0
foreach ($i in 1..4) {
    $mods = Join-Path (Get-RigPath $i) "Mods"
    if (-not (Test-Path $mods)) { continue }
    Copy-Item $dll (Join-Path $mods "MenaceCoop.dll") -Force
    Write-Host "  MenaceM$i <- MenaceCoop.dll ($hash)"
    $n++
}
if ($n -eq 0) { throw "No rigs found under $RigRoot -- make them with tools\New-Rig.ps1." }
Write-Host "Deployed build $hash to $n rig(s)." -ForegroundColor Green
