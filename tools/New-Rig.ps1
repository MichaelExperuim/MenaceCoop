<#
Make (or refresh) a co-op TEST RIG: a second, third or fourth copy of MENACE on
this machine, so one PC can play host and guests at once. See DeveloperDocs\06-TESTING.md.

    .\tools\New-Rig.ps1 MenaceM1            # host rig
    .\tools\New-Rig.ps1 MenaceM2            # first guest
    .\tools\New-Rig.ps1 MenaceM3,MenaceM4   # 3- and 4-player tests
    .\tools\New-Rig.ps1 MenaceM2 -Refresh   # after a game update: relink game files only

WHAT IT DOES
  - Hardlinks the game files (Menace_Data\, D3D12\, root *.exe / *.dll) from your
    main install. Hardlinks cost no disk space and follow a Steam update after a
    -Refresh, but only work on the SAME DRIVE -- keep the rigs beside the game.
  - Copies the MelonLoader setup (version.dll, doorstop_config.ini, MelonLoader\)
    from the main install and hardlinks MelonLoader\Il2CppAssemblies\, so every
    rig runs the SAME generated assemblies the mod is compiled against.
  - Creates Mods\ with the freshly built MenaceCoop.dll (and nothing else: other
    mods change behaviour and make test runs unreadable), UserData\, and the
    _CoopModDev\ marker folder that the self-test harness requires
    (SelfTest.OnDevRig: autoplay never runs outside a rig).
  - Writes steam_appid.txt so each copy starts without going through Steam's
    launcher.

REQUIREMENTS
  - MENACE installed with MelonLoader, launched once (so Il2CppAssemblies exists).
  - The mod built:  dotnet build MenaceCoopMelon\MenaceCoopMelon.csproj -c Release
  - Run from a normal PowerShell; no admin rights needed for hardlinks.

Locations come from tools\RigPaths.ps1 (env MENACE_GAME_DIR / MENACE_RIG_ROOT,
or rig.local.ps1 at the repo root).
#>
param(
    [Parameter(Mandatory)][string[]]$Rig,
    [switch]$Refresh
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\RigPaths.ps1"

if (-not (Test-Path "$GameDir\Menace.exe")) { throw "No MENACE at $GameDir -- set MENACE_GAME_DIR (see tools\RigPaths.ps1)." }
if (-not (Test-Path "$GameDir\version.dll")) { throw "$GameDir has no MelonLoader (version.dll). Install MelonLoader into the game first." }
if (-not (Test-Path "$GameDir\MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll")) { throw "Launch MENACE once with MelonLoader so it generates MelonLoader\Il2CppAssemblies, then retry." }
$rigProcs = @(Get-Process Menace -ErrorAction SilentlyContinue | Where-Object { $_.Path -match '\\MenaceM[1-4]\\' })
if ($rigProcs.Count -gt 0) { throw "A rig is running -- close it first (its files are locked)." }

$dll = Join-Path $RepoRoot "MenaceCoopMelon\bin\Release\MenaceCoop.dll"

function Link-Tree([string]$src, [string]$dst) {
    if (-not (Test-Path $src)) { return 0 }
    $count = 0
    if (Test-Path $dst) {   # files a game update removed
        Get-ChildItem $dst -Recurse -File | Where-Object {
            -not (Test-Path (Join-Path $src $_.FullName.Substring($dst.Length + 1)))
        } | Remove-Item -Force
    }
    New-Item -ItemType Directory -Force $dst | Out-Null
    Get-ChildItem $src -Recurse -Directory | ForEach-Object {
        New-Item -ItemType Directory -Force (Join-Path $dst $_.FullName.Substring($src.Length + 1)) | Out-Null
    }
    Get-ChildItem $src -Recurse -File | ForEach-Object {
        $t = Join-Path $dst $_.FullName.Substring($src.Length + 1)
        if (Test-Path $t) { Remove-Item $t -Force }
        New-Item -ItemType HardLink -Path $t -Target $_.FullName | Out-Null
        $count++
    }
    return $count
}

foreach ($name in $Rig) {
    if ($name -notmatch '^MenaceM[1-4]$') { throw "Rig names are MenaceM1..MenaceM4 (the launcher looks for exactly those)." }
    $dst = Join-Path $RigRoot $name
    $fresh = -not (Test-Path $dst)
    if ($fresh -and $Refresh) { throw "$dst does not exist -- run without -Refresh to create it." }
    if ((Split-Path $GameDir -Qualifier) -ne (Split-Path $RigRoot -Qualifier)) { throw "Rigs must be on the same drive as the game (hardlinks): $RigRoot vs $GameDir" }
    New-Item -ItemType Directory -Force $dst | Out-Null

    Write-Host "== $name ($dst)" -ForegroundColor Cyan
    $n = 0
    $n += Link-Tree "$GameDir\Menace_Data" "$dst\Menace_Data"
    $n += Link-Tree "$GameDir\D3D12" "$dst\D3D12"
    Get-ChildItem $GameDir -File | Where-Object { $_.Extension -in ".exe", ".dll" -and $_.Name -notin "version.dll", "winhttp.dll" } | ForEach-Object {
        $t = Join-Path $dst $_.Name
        if (Test-Path $t) { Remove-Item $t -Force }
        New-Item -ItemType HardLink -Path $t -Target $_.FullName | Out-Null
        $n++
    }
    $n += Link-Tree "$GameDir\MelonLoader\Il2CppAssemblies" "$dst\MelonLoader\Il2CppAssemblies"
    $genCfg = "MelonLoader\Dependencies\Il2CppAssemblyGenerator\Config.cfg"
    if (Test-Path "$GameDir\$genCfg") {
        New-Item -ItemType Directory -Force (Split-Path "$dst\$genCfg") | Out-Null
        Copy-Item "$GameDir\$genCfg" "$dst\$genCfg" -Force
    }
    Write-Host "  linked $n game files"

    if (-not $Refresh) {
        # The loader itself: copied (small), minus the per-run and linked parts.
        foreach ($f in "version.dll", "doorstop_config.ini") { Copy-Item "$GameDir\$f" "$dst\$f" -Force }
        Get-ChildItem "$GameDir\MelonLoader" | Where-Object { $_.Name -notin "Il2CppAssemblies", "Logs", "Latest.log" } |
            ForEach-Object { Copy-Item $_.FullName (Join-Path "$dst\MelonLoader" $_.Name) -Recurse -Force }
        foreach ($d in "Mods", "Plugins", "UserLibs", "UserData", "_CoopModDev") { New-Item -ItemType Directory -Force "$dst\$d" | Out-Null }
        # Steam's app id lets the exe start directly (each rig is its own process).
        $appid = "$GameDir\steam_appid.txt"
        if (Test-Path $appid) { Copy-Item $appid "$dst\steam_appid.txt" -Force }
        elseif (-not (Test-Path "$dst\steam_appid.txt")) { Write-Host "  note: no steam_appid.txt in the game folder; create one in $dst holding MENACE's Steam app id if the exe asks for Steam" -ForegroundColor DarkYellow }
        if (Test-Path $dll) { Copy-Item $dll "$dst\Mods\MenaceCoop.dll" -Force; Write-Host "  deployed MenaceCoop.dll" }
        else { Write-Host "  MenaceCoop.dll not built yet -- build, then run tools\Deploy-ToRigs.ps1" -ForegroundColor DarkYellow }
    }

    # One interop everywhere: a rig on different generated assemblies is a different game.
    $a = "MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll"
    if ((Get-FileHash "$GameDir\$a" -Algorithm MD5).Hash -ne (Get-FileHash "$dst\$a" -Algorithm MD5).Hash) { throw "$name Assembly-CSharp.dll does not match the main install's." }
    Write-Host "  $name ready" -ForegroundColor Green
}
