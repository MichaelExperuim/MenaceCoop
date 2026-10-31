# Testing: rigs, harness, scenarios, slow link, reading logs

Co-op bugs live between machines, so every test needs at least two copies of the
game talking to each other. This guide sets up 2-4 copies ("rigs") on one PC and
runs them unattended through a whole mission, with an AI playing every side.

**One machine hides latency.** Messages between local copies arrive in the same
frame they were sent, so anything that races the network always "wins" in a local
test. Use the simulated slow link (`-LatencyMs`, below) for anything involving
loading, joining or turn handover. A real two-PC test is still worth doing before
a release.

---

## 1. One-time setup

**You need** (full list and a checker: [00-REQUIREMENTS.md](00-REQUIREMENTS.md), `tools\Check-Requirements.ps1`):

- MENACE (Steam) with **MelonLoader** installed (the Menace Modkit / mod manager
  installs it). Launch the game once so MelonLoader generates
  `MelonLoader\Il2CppAssemblies\` -- the build compiles against those.
- **.NET SDK 6+** (`dotnet --version`).
- Windows PowerShell 5.1+ (ships with Windows). Git Bash if you want
  `tools/check-run.sh`.
- Disk: the rigs are **hardlinks** to the game, so they cost almost nothing, but
  they must sit on the **same drive** as the game.

**Tell the scripts where things are** (only if your game is not in Steam's default
library, or the repo is not checked out inside the game folder). Create
`rig.local.ps1` at the repo root (gitignored):

```powershell
$env:MENACE_GAME_DIR = "D:\SteamLibrary\steamapps\common\Menace"
# optional -- defaults to the game's parent folder (the rigs sit beside the game):
# $env:MENACE_RIG_ROOT = "D:\SteamLibrary\steamapps\common"
```

`tools\RigPaths.ps1` is the single place every script resolves these from.

**Build the mod** (pass your game folder if it is not the default):

```powershell
dotnet build MenaceCoopMelon\MenaceCoopMelon.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Menace"
```

**Make the rigs** -- M1 is always the host, M2-M4 the guests:

```powershell
.\tools\New-Rig.ps1 MenaceM1,MenaceM2            # 2 players
.\tools\New-Rig.ps1 MenaceM3,MenaceM4            # add these for 3- and 4-player tests
```

Each rig gets the game files (hardlinked), its own MelonLoader setup, a `Mods\`
holding only `MenaceCoop.dll`, and an empty `_CoopModDev\` folder -- the marker the
harness checks before it will auto-play (it never auto-plays outside a rig).
Other mods are deliberately left out: they change behaviour and make runs
unreadable.

**After a game update:** `.\tools\New-Rig.ps1 MenaceM1,MenaceM2 -Refresh` relinks
the game files, then rebuild the mod. A rig on different generated assemblies
than the main game is a different game; the script refuses to finish in that state.

**After every build:** a plain `dotnet build` copies the DLL into every rig. If you
built with `-p:NoDeploy=true` (e.g. while a run was going), run
`.\tools\Deploy-ToRigs.ps1` afterwards. It refuses while a rig is running --
Windows locks a loaded DLL and the copy would fail silently.

---

## 2. Running a test

```powershell
.\tools\Test-Saves.ps1 -Backup                     # see section 5 -- always first
.\Launch-MelonCoopTest.ps1 -AutoPlay -Players 2 -Save building_test -MaxRounds 6
```

The launcher writes each rig's settings (`UserData\MelonPreferences.cfg`,
sections `[MenaceCoop_Network]` / `[MenaceCoop_Dev]`), copies the chosen test save
into the shared Saves folder as `coop_test.save`, starts the host, waits until it
is listening, then starts the guests. With `-AutoPlay` the host's **UI autopilot**
(`UiAutopilot.cs`) loads the campaign, opens planning, launches the mission, and
every machine's **self-test** (`SelfTest.cs`, or the AutoCombat brain if installed)
plays its own squads. The run stops itself after `-MaxRounds` and parks on the
results screen.

| Parameter | Meaning |
|---|---|
| `-AutoPlay` | Play unattended. Without it the rigs connect and you play by hand. |
| `-Manual` | You play; logging and probes stay on. |
| `-NoConnect` | Every rig starts at the title with co-op off; host and join by hand (127.0.0.1). |
| `-Players 2..4` | How many rigs (M1..M4). |
| `-Save <name>` | `testsave\<name>.save`: `building_test` (urban, garrisons, trucks), `pirate_test`, `alien_test` (forest, 42x42). |
| `-MaxRounds N` | Stop after N rounds. |
| `-Loop` | After a mission, reload and launch the next one (tests the post-battle sync). |
| `-Scenario a,b` | Scripted events, below. |
| `-LatencyMs N -JitterMs J` | Simulated slow link, below. |
| `-Force` | Close running rigs first. Never touches installs other than M1-M4 / Menace / Menace2. |

The launcher **refuses** if a rig or your own game install is already running --
someone may be playtesting. Installs it does not own get a warning and are left alone.

### Scenarios (`-Scenario`, `DebugSync.cs`)

Each stages one hard case on purpose, so a 6-round run exercises it every time.

| Name | What happens |
|---|---|
| `none` | A clean mission (default). |
| `revive` | A hostile beside the host's infantry, so a squad leader goes down. |
| `structure` | The host destroys a structure locally at round 2; clients learn of it only through the relay. |
| `reload` | FIX DESYNC at round 3: the guest is rebuilt and has to catch up. |
| `battlesave` | Save the battle at round 3, resume it at round 4. |
| `rejoin` / `rejoinlate` / `rejoin2` / `rejoinmany` | Guests drop and rejoin mid-battle (one, late, two at once, repeatedly). |
| `rename` | A client changes its display name mid-session. |
| `spectator` | A player with no squads. |
| `oci` / `strike` / `ocimix` | Ship support: shared pool, every ability, mixed use. |
| `grenades` | Grants grenades to a host and a guest squad. |
| `drones` / `dronegrant` | Skybot drones (minions) deployed and commanded. |
| `wreck` | Each side destroys an enemy vehicle. |
| `queen` | An alien queen fight (element revive loop). |
| `rout` / `prout` | An enemy / the host's own squad breaks and flees (rounds 3 and 6). |
| `perks` | Every player squad gets the counterattack + Overtime perks at round 2. |

### Simulated slow link (`-LatencyMs`, `NetLag.cs`)

Each machine holds every message it **receives** for `LatencyMs` plus up to
`JitterMs` of random extra before acting on it, keeping arrival order like TCP.
Both sides delay, so a round trip costs about twice `LatencyMs`.

```powershell
.\Launch-MelonCoopTest.ps1 -AutoPlay -Players 2 -MaxRounds 6 -LatencyMs 250 -JitterMs 150
```

Dev builds only (`COOP_DEVTOOLS`); a release build does not contain it.

---

## 3. Reading a run

Each rig writes `<rig>\MelonLoader\Latest.log` (previous runs in `MelonLoader\Logs\`).

```bash
bash tools/check-run.sh "<RigRoot>/MenaceM1/MelonLoader/Latest.log" "<RigRoot>/MenaceM2/MelonLoader/Latest.log"
```

Reports teleport bounces, state-hash differences and whether they persisted,
building/vehicle moves that left a unit outside, held moves, flee/rally, the round
reached, and a deduplicated WARNING/ERROR sweep.

**Always read both logs side by side.** Every line is stamped `[HH:MM:SS.mmm]`, and
the rigs share a clock. Mirror-image errors on the two sides ("only on HOST: X" /
"only on GUEST: X" for the same event) usually mean the machines *agree* and the
checker raced a transition -- compare before believing either.

Useful log tags:

| Tag | What it is |
|---|---|
| `[SESSION]` | Connect, save sync, mission entry, ready/hold-fire. |
| `[SYNC]` | Orders sent / replayed, refused replays (`REFUSED by the engine`). |
| `[DESYNC]` | State hash per faction switch; corrections; `Difference persists` = a real one. |
| `[TURN]` / `[TRACE:H]` / `[TRACE:G]` | Turn phases; who holds control (`ctrl=me/them`). |
| `[ROSTER]` / `[TERRAIN]` | Same enemies? Same map? (per battle) |
| `[FLEE]` `[CONTAINER]` `[EFX]` `[KILL]` | Subsystem relays. |
| `[PATCHES]` | One line per Harmony patch class at startup -- a `NOT APPLIED` here means a game update moved a target. |
| `[API]` | Built-against vs running game version. |
| `[PROBE:H]` / `[PERKPROBE:G]` ... | Diagnostic probes (only with `ProbeBugs = true`). |

A "Not responding" game with flat CPU is usually a **crash parked behind a hidden
error box**, not a deadlock.

---

## 4. Probes

The game's code ships without method bodies in the decompile (IL2CPP), so behaviour
is learned by **watching it**, not reading it. When a report cannot be explained:

1. Add a probe class (see `BugProbes.cs`, `PerkAttackProbe.cs`): Harmony prefixes/
   postfixes that log the call, its arguments, who owns the unit, and the relay
   flags (`Replication.ApplyingRemote`, `SuppressElementDamage`) at that moment.
   Gate it on `Plugin.ProbeBugs` and give it its own patch class (registered in
   `MelonEntry.cs`) so a moved target disables only that probe.
2. Add a scenario that makes the case happen every run.
3. Run, read both logs, then fix. Leave the probe in (gated) until a run proves the fix.

`ProbeBugs` is set by the launcher on rigs; it defaults to off for players.

---

## 5. Save hygiene

Every copy of the game on a PC shares **one** Saves folder
(`%USERPROFILE%\AppData\LocalLow\Overhype Studios\Menace\Saves`). A run writes
mission autosaves there, evicts your oldest autosaves, and replaces `latest.save`
(your "Continue") with the test campaign.

```powershell
.\tools\Test-Saves.ps1 -Backup      # before every run
.\tools\Test-Saves.ps1 -Restore     # after it
```

`-Restore` deletes only autosaves written after the backup, leaves any other new
file alone (and lists it), and puts back everything the run changed.

---

## 6. Before a release

- A clean 3-player run to the round cap, 0 persistent hash differences.
- The same at `-LatencyMs 250 -JitterMs 150`.
- One `-Loop` run (post-battle sync, next-mission entry).
- Any scenario touching what changed.
- Then `Build-MelonRelease.ps1` (see [08-RELEASING.md](08-RELEASING.md)).
