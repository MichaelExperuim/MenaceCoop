<#
Check this machine against DeveloperDocs\00-REQUIREMENTS.md.

    .\tools\Check-Requirements.ps1

Prints PASS / WARN / FAIL per item and exits 1 if anything required is missing.
Reads only; changes nothing.
#>
$ErrorActionPreference = "Continue"
. "$PSScriptRoot\RigPaths.ps1"
$fail = 0
function Report([string]$state, [string]$what, [string]$detail) {
    $color = @{ PASS = "Green"; WARN = "Yellow"; FAIL = "Red" }[$state]
    Write-Host ("{0,-5} {1,-28} {2}" -f $state, $what, $detail) -ForegroundColor $color
    if ($state -eq "FAIL") { $script:fail++ }
}

# Windows 64-bit
if ([Environment]::Is64BitOperatingSystem) { Report PASS "Windows 64-bit" ([Environment]::OSVersion.VersionString) }
else { Report FAIL "Windows 64-bit" "32-bit Windows cannot run MENACE" }

# PowerShell
Report PASS "PowerShell" "$($PSVersionTable.PSVersion)"

# Game
if (Test-Path "$GameDir\Menace.exe") { Report PASS "MENACE" $GameDir }
else { Report FAIL "MENACE" "not found at $GameDir -- set MENACE_GAME_DIR or rig.local.ps1 (tools\RigPaths.ps1)" }

# Game version vs what the source is built against (ApiCheck.BuiltAgainstGame)
$api = Join-Path $RepoRoot "MenaceCoop\ApiCheck.cs"
$built = $null
if (Test-Path $api) {
    $m = Select-String -Path $api -Pattern 'BuiltAgainstGame\s*=\s*"([^"|]+)\|(\d+)"'
    if ($m) { $built = $m.Matches[0].Groups[1].Value; $builtSize = [long]$m.Matches[0].Groups[2].Value }
}
$meta = "$GameDir\Menace_Data\il2cpp_data\Metadata\global-metadata.dat"
if ($built -and (Test-Path $meta)) {
    $size = (Get-Item $meta).Length
    if ($size -eq $builtSize) { Report PASS "Game version" "matches the source ($built)" }
    else { Report WARN "Game version" "differs from the source's $built (game updated?) -- build, then check the [API] / [PATCHES] log lines" }
}

# MelonLoader
if (Test-Path "$GameDir\version.dll") {
    $ml = Get-ChildItem "$GameDir\MelonLoader\net6\MelonLoader.dll" -ErrorAction SilentlyContinue
    $ver = if ($ml) { $ml.VersionInfo.ProductVersion } else { "?" }
    Report PASS "MelonLoader" "installed ($ver)"
} elseif (Test-Path "$GameDir\winhttp.dll") { Report FAIL "MelonLoader" "this game runs BepInEx (winhttp.dll); MenaceCoop needs MelonLoader" }
else { Report FAIL "MelonLoader" "not installed (no version.dll beside Menace.exe)" }

if (Test-Path "$GameDir\MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll") { Report PASS "Generated assemblies" "present" }
else { Report FAIL "Generated assemblies" "missing -- launch the game once with MelonLoader" }

# .NET SDK
try { $dotnet = Get-Dotnet; $sdk = (& $dotnet --list-sdks | Select-Object -Last 1); Report PASS ".NET SDK" "$sdk" }
catch { Report FAIL ".NET SDK" "no SDK found -- install the .NET 6+ SDK" }

# Git
if (Get-Command git -ErrorAction SilentlyContinue) { Report PASS "Git" ((git --version) -join "") } else { Report WARN "Git" "not on PATH" }

# Bash (optional)
if (Get-Command bash -ErrorAction SilentlyContinue) { Report PASS "bash (optional)" "for tools\check-run.sh" } else { Report WARN "bash (optional)" "not found; tools\check-run.sh needs Git Bash or WSL" }

# RAM
$ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
if ($ramGb -ge 32) { Report PASS "RAM" "$ramGb GB (enough for 4 copies)" }
elseif ($ramGb -ge 16) { Report PASS "RAM" "$ramGb GB (2 copies; 3-4 may swap)" }
else { Report WARN "RAM" "$ramGb GB -- each running copy uses ~4 GB" }

# Rigs on the same drive as the game
if ((Test-Path $GameDir) -and (Split-Path $GameDir -Qualifier) -eq (Split-Path $RigRoot -Qualifier)) { Report PASS "Rig location" "$RigRoot (same drive as the game)" }
else { Report FAIL "Rig location" "$RigRoot is not on the game's drive (hardlinks need one drive)" }

$rigs = @(1..4 | Where-Object { Test-Path (Join-Path (Get-RigPath $_) "Menace.exe") } | ForEach-Object { "MenaceM$_" })
if ($rigs.Count -ge 2) { Report PASS "Test rigs" ($rigs -join ", ") }
else { Report WARN "Test rigs" "fewer than 2 -- make them with tools\New-Rig.ps1 MenaceM1,MenaceM2" }

Write-Host ""
if ($fail -gt 0) { Write-Host "$fail requirement(s) missing." -ForegroundColor Red; exit 1 }
Write-Host "Ready to build and test." -ForegroundColor Green
