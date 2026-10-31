<#
MenaceCoop multi-instance test launcher: starts 2-4 copies of the game (the "rigs"
MenaceM1..M4, made with tools\New-Rig.ps1) that connect to each other on this
machine. Full guide: DeveloperDocs\06-TESTING.md. Rig locations come from tools\RigPaths.ps1.

    settings   <rig>\UserData\MelonPreferences.cfg   [MenaceCoop*] sections
    log        <rig>\MelonLoader\Latest.log

Order matters: the HOST (MenaceM1) starts FIRST and is fully up before the guests
start. The first instance holds Steam's "app running" slot and a second one
silently fails if it goes first.

    .\Launch-MelonCoopTest.ps1 -AutoPlay -Players 3 -Save building_test -Scenario prout
    .\Launch-MelonCoopTest.ps1 -Manual -Players 3 -Save pirate_test   # you play; auto-connects
    .\Launch-MelonCoopTest.ps1 -NoConnect -Players 3                  # main menus, connect by hand
    .\Launch-MelonCoopTest.ps1 -HostOnly                              # just prove the mod loads

-Force is required to touch an already-running MENACE. Without it this refuses:
a play-test must never be killed to start a dev test.
#>
param(
    [switch]$AutoPlay,
    # The user plays by hand: no harness, no scripted debug spawns. Logging and
    # probes stay on so the hand test still leaves full logs.
    [switch]$Manual,
    # Every rig starts at the main menu with co-op OFF: host on M1 and join
    # 127.0.0.1 on the others by hand. Implies -Manual.
    [switch]$NoConnect,
    [switch]$HostOnly,
    [switch]$Force,
    [switch]$Loop,          # chain into the next mission instead of stopping after one
    [switch]$NoCombatCorrect, # snapshot logs suppression/morale/stance/facing drift but does not write it
    [string]$GuestPassword = "", # give the guest a WRONG session password: the host must refuse it
    [switch]$Fullscreen,    # opt back OUT of windowed mode
    [int]$MaxRounds = 30,
    # Simulated slow link (NetLag, dev builds): each machine holds received
    # messages this long (+ up to -JitterMs random). Round trip ~ 2x LatencyMs.
    [int]$LatencyMs = 0,
    [int]$JitterMs = 0,
    # Which testsave\<name>.save the harness starts from (copied in as coop_test.save,
    # the name UiAutopilot loads): building_test, pirate_test or alien_test.
    [string]$Save = "building_test",
    # v5: 2-4 players. 3 adds MenaceM3, 4 adds MenaceM4 (user 2026-10-02: the
    # machine can run 4 after all; tests run at 4).
    [ValidateRange(2, 4)][int]$Players = 2,
    # DebugSync harness scenario: all | vip | none ("none" = clean mission, for
    # AutoCombat capture tests -- the injected enemy changes the fight).
    [ValidatePattern("^(none|revive|structure|reload|battlesave|rejoin|rejoinlate|rejoin2|rejoinmany|rename|spectator|oci|grenades|drones|dronegrant|strike|ocimix|wreck|queen|rout|prout|perks)(,(revive|structure|reload|battlesave|rejoin|rejoinlate|rejoin2|rejoinmany|rename|spectator|oci|grenades|drones|dronegrant|strike|ocimix|wreck|queen|rout|prout|perks))*$")][string]$Scenario = "none",
    # Sized for this machine: three 1920x1080 monitors, 1040px of working height
    # each. 1600x900 leaves room for the title bar and to grab the window, and is
    # meant to be dragged onto a monitor of its own rather than tiled beside the
    # other instance.
    # Windows open wherever Windows puts them (user, 2026-09-27: the forced
    # second-monitor placement is removed). Place-GameWindows.ps1 still exists
    # to run by hand.
    [int]$Width = 960,
    [int]$Height = 540
)
$ErrorActionPreference = "Stop"

. "$PSScriptRoot\tools\RigPaths.ps1"
if ($NoConnect) { $Manual = $true }
$h = Get-RigPath 1
$g = Get-RigPath 2
$g2 = Get-RigPath 3
$g3 = Get-RigPath 4
foreach ($r in $h, $g) {
    if (-not (Test-Path "$r\Menace.exe")) { throw "No rig at $r -- make it with tools\New-Rig.ps1 $(Split-Path $r -Leaf) (DeveloperDocs\06-TESTING.md)" }
}
$all = @($h, $g)
if ($Players -ge 3) {
    if (-not (Test-Path "$g2\Menace.exe")) { throw "No rig at $g2 -- make it with tools\New-Rig.ps1 MenaceM3" }
    $all += $g2
}
if ($Players -ge 4) {
    if (-not (Test-Path "$g3\Menace.exe")) { throw "No rig at $g3 -- make it with tools\New-Rig.ps1 MenaceM4" }
    $all += $g3
}

# WINDOWED BY DEFAULT (user preference, 2026-09-17).
#
# Two fullscreen instances fight over the display and cannot be placed side by
# side, so a test run could not be watched -- and watching a run is how most of
# the interesting behaviour in this mod got noticed. Unity takes these on the
# command line, which beats the saved preference in the registry, so it holds
# regardless of what either install last wrote.
$screenArgs = if ($Fullscreen) { @() }
              else { @("-screen-fullscreen", "0", "-screen-width", "$Width", "-screen-height", "$Height") }
# Start-Process refuses an EMPTY -ArgumentList, so -Fullscreen (no args) passes none at all.
$screenSplat = @{}
if ($screenArgs.Count -gt 0) { $screenSplat.ArgumentList = $screenArgs }

# BOTH halves need the dev-rig marker, not just the host. SelfTest.OnDevRig
# gates SelfTestAutoPlay on a _CoopModDev folder beside the game; without it on
# the GUEST, the guest takes control and never moves its units, which presents
# as a host stuck on "ctrl=them mine=0 theirs=2" with no error anywhere.
if ($AutoPlay) {
    foreach ($i in $all) {
        if (Test-Path "$i\_CoopModDev") { continue }
        Write-Host "Creating dev-rig marker in $i (autoplay is gated on it)." -ForegroundColor Yellow
        New-Item -ItemType Directory -Force "$i\_CoopModDev" | Out-Null
    }
}

# Report the mod version being launched (read from source, single source of truth).
$entry = Join-Path $PSScriptRoot "MenaceCoopMelon\MelonEntry.cs"
$modVer = "unknown"
if (Test-Path $entry) {
    $vm = Select-String -Path $entry -Pattern 'PluginVersion\s*=\s*"([^"]+)"'
    if ($vm) { $modVer = $vm.Matches[0].Groups[1].Value }
}
Write-Host "=== MenaceCoop v$modVer (MelonLoader) ===" -ForegroundColor Cyan

# Only THIS rig (MenaceM1-M4) and the user's own installs (Menace, Menace2) block
# a launch. Other installs (e.g. MenaceMC1) are other sessions' rigs: warn, never
# close them -- -Force below only ever stops the blocking set (user, 2026-10-30).
$all = @(Get-Process Menace -ErrorAction SilentlyContinue)
$running = @($all | Where-Object { $_.Path -match '\\(MenaceM[1-4]|Menace|Menace2)\\Menace\.exe$' })
$others = @($all | Where-Object { $running -notcontains $_ })
foreach ($o in $others) { Write-Host "Note: another rig is running ($($o.Path)) -- left alone; it shares the Saves folder." -ForegroundColor DarkYellow }
if ($running.Count -gt 0) {
    if (-not $Force) {
        Write-Host "REFUSING TO LAUNCH: $($running.Count) Menace instance(s) already running." -ForegroundColor Red
        Write-Host "Someone may be play-testing. Close the game yourself, or re-run with -Force." -ForegroundColor Red
        exit 1
    }
    Write-Host "Closing running Menace instances (-Force given)..." -ForegroundColor Yellow
    $running | ForEach-Object { Stop-Process -Id $_.Id -Force }
    Start-Sleep -Seconds 3
}

# NO STEAM, NO MODS -- and the tell is an EMPTY log, not an error, which is
# indistinguishable from "still loading". Check before burning four minutes.
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) {
    Write-Host "Steam is not running. Start Steam first or NO mods will load at all." -ForegroundColor Red
    exit 1
}

# Restore the pinned test save so every run starts from an identical campaign
# state. Without this each run inherits the previous run's progress and the
# operation eventually has no missions left to launch.
# NOTE: every install shares this one Saves folder.
$baseline = Join-Path $PSScriptRoot "testsave\$Save.save"
$savesDir = "$env:USERPROFILE\AppData\LocalLow\Overhype Studios\Menace\Saves"
if (-not (Test-Path $baseline)) {
    Write-Host "No test save $baseline" -ForegroundColor Red
    exit 1
}
if (Test-Path $savesDir) {
    Copy-Item $baseline (Join-Path $savesDir "coop_test.save") -Force
    Write-Host "Pinned test save '$Save' restored as coop_test.save." -ForegroundColor DarkGray
}

# --- port -------------------------------------------------------------------
# Hyper-V / WSL reserve TCP blocks that move on every reboot; 2026-10-04 one
# covered 27482-27581, so the host could not listen on 27555 and every client
# retried forever (looked like a frozen game). Take the first free port from
# 27555 up that no reserved range covers.
$reserved = @(netsh int ipv4 show excludedportrange protocol=tcp |
    ForEach-Object { if ($_ -match '^\s*(\d+)\s+(\d+)') { ,@([int]$Matches[1], [int]$Matches[2]) } })
$coopPort = 27555
while ($reserved | Where-Object { $coopPort -ge $_[0] -and $coopPort -le $_[1] }) { $coopPort++ }
if ($coopPort -ne 27555) { Write-Host "Port 27555 is reserved by Windows -- using $coopPort." -ForegroundColor Yellow }

# --- settings ---------------------------------------------------------------
# MelonPreferences.cfg is shared by every melon, so this rewrites ONLY our
# sections and leaves anything else in the file alone.
function Set-CoopPrefs($install, $mode, $name) {
    $path = Join-Path $install "UserData\MelonPreferences.cfg"
    $ours = @(
        "[MenaceCoop_Network]"
        "Mode = `"$mode`""
        "HostAddress = `"127.0.0.1`""
        "Port = $coopPort"
        "PartnerSteamId = `"`""
        "PlayerName = `"$name`""
        # Direct IP needs a session password (CoopAuth.cs). Rig-only test value;
        # -GuestPassword gives the guest a different one to test the refusal.
        "SessionPassword = `"$(if ($mode -eq 'Guest' -and $GuestPassword) { $GuestPassword } else { 'rig-test-password' })`""
        ""
        "[MenaceCoop_Dev]"
        "SelfTestTrace = true"
        "TraceDamage = true"
        # BugProbes.cs: drone effects, ship support, cross-owned boarding. On for
        # every harness run because a probe that is off during the one run that
        # hit the bug is a run wasted. Dev-bench only -- this script never touches
        # the user's own install, and the setting defaults OFF everywhere else.
        "ProbeBugs = true"
        # DESIGN-REVIEW.md proposal 2: -NoCombatCorrect leaves suppression/morale/
        # stance/facing drift logged but unwritten.
        "CorrectCombatState = $(if ($NoCombatCorrect) { 'false' } else { 'true' })"
        # DebugSync.cs: synced debug buttons + a once-per-battle scripted spawn
        # and refill (host, round 2) so every run proves the sync path.
        "SyncedDebug = $(if ($Manual) { 'false' } else { 'true' })"
        "SyncedDebugScenario = `"$Scenario`""
        "SelfTestAutoPlay = $(if ($AutoPlay) { 'true' } else { 'false' })"
        "SelfTestMaxRounds = $MaxRounds"
        "SelfTestPlayers = $Players"
        "SelfTestLoopMissions = $(if ($Loop) { 'true' } else { 'false' })"
        "SimLatencyMs = $LatencyMs"
        "SimJitterMs = $JitterMs"
        ""
    )

    $kept = @()
    if (Test-Path $path) {
        $skip = $false
        foreach ($line in Get-Content $path) {
            if ($line -match '^\s*\[(.+)\]\s*$') {
                $skip = $matches[1] -in @("MenaceCoop_Network", "MenaceCoop_Dev")
            }
            if (-not $skip) { $kept += $line }
        }
    }
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    Set-Content -Path $path -Value ($kept + $ours) -Encoding utf8
}

# Wait for the line that PROVES the mod loaded AND consumed its settings.
# Waiting on anything written earlier matches a STALE log from the previous run
# and sails on while the game is still booting.
function Wait-ModReady($install, $label) {
    $log = Join-Path $install "MelonLoader\Latest.log"
    $deadline = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $log) {
            $m = Select-String -Path $log -Pattern "loaded\. AutoMode=(\w+)" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($m) {
                Write-Host "$label ready (AutoMode=$($m.Matches[0].Groups[1].Value))." -ForegroundColor Green
                return $true
            }
        }
        Start-Sleep -Seconds 3
    }
    Write-Host "$label did not report ready within 5 minutes (check $log)" -ForegroundColor Yellow
    return $false
}

# A stale log is indistinguishable from a run that never started, so clear both
# before launching rather than reasoning about timestamps afterwards.
foreach ($i in $all) {
    Remove-Item (Join-Path $i "MelonLoader\Latest.log") -Force -ErrorAction SilentlyContinue
}

Set-CoopPrefs $h "Host" "M1-host"
Set-CoopPrefs $g "Guest" "M2-guest"
if ($Players -ge 3) { Set-CoopPrefs $g2 "Guest" "M3-guest" }
if ($Players -ge 4) { Set-CoopPrefs $g3 "Guest" "M4-guest" }
Write-Host "Settings written (host=MenaceM1, guest=MenaceM2$(if ($Players -ge 3) { ', guest2=MenaceM3' })$(if ($Players -ge 4) { ', guest3=MenaceM4' }), AutoPlay=$AutoPlay)." -ForegroundColor DarkGray

Write-Host "Starting HOST (MenaceM1)..." -ForegroundColor Cyan
Start-Process (Join-Path $h "Menace.exe") -WorkingDirectory $h @screenSplat
if (-not (Wait-ModReady $h "HOST")) { exit 1 }

if ($HostOnly) {
    Write-Host "Host-only run: stopping here." -ForegroundColor Cyan
    exit 0
}

Write-Host "Starting GUEST (MenaceM2)..." -ForegroundColor Cyan
Start-Process (Join-Path $g "Menace.exe") -WorkingDirectory $g @screenSplat
Wait-ModReady $g "GUEST" | Out-Null

if ($Players -ge 3) {
    Write-Host "Starting GUEST 2 (MenaceM3)..." -ForegroundColor Cyan
    Start-Process (Join-Path $g2 "Menace.exe") -WorkingDirectory $g2 @screenSplat
    Wait-ModReady $g2 "GUEST2" | Out-Null
}
if ($Players -ge 4) {
    Write-Host "Starting GUEST 3 (MenaceM4)..." -ForegroundColor Cyan
    Start-Process (Join-Path $g3 "Menace.exe") -WorkingDirectory $g3 @screenSplat
    Wait-ModReady $g3 "GUEST3" | Out-Null
}

Write-Host ""
Write-Host "Both instances up. Watch with:" -ForegroundColor Green
Write-Host "  bash tools/check-run.sh <rig>\MelonLoader\Latest.log ...   (DeveloperDocs\06-TESTING.md)" -ForegroundColor Gray
