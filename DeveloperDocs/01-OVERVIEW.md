# Overview: what the mod is, repo layout, building

MENACE is a single-player turn-based tactics game (Unity, IL2CPP). MenaceCoop makes
it 2-4 player co-op: one player **hosts** their campaign, the others **join**, the
squads are split between the players, and everyone fights each mission together.

It is a **MelonLoader** mod: one DLL (`MenaceCoop.dll`) in the game's `Mods\` folder.
Everything is done with **Harmony** patches on the game's own code plus a network
layer (direct IP over TCP, or Steam P2P).

### Repository layout

| Path | What it is |
|---|---|
| `MenaceCoop/` | All mod source (shared C#, ~60 files). |
| `MenaceCoopMelon/` | The MelonLoader project: `MenaceCoopMelon.csproj`, `MelonEntry.cs` (entry point, settings, patch registration), install hygiene. |
| `Shared/PatchReport.cs` | Logs one line per Harmony patch class at startup. |
| `tools/` | Test-rig scripts: `RigPaths.ps1`, `New-Rig.ps1`, `Deploy-ToRigs.ps1`, `Test-Saves.ps1`, `check-run.sh`. |
| `testsave/` | Campaign saves the harness starts from. |
| `06-TESTING.md` | Rigs, harness, scenarios, slow-link simulation, reading logs. |
| `Launch-MelonCoopTest.ps1` | Starts 2-4 rigs and (optionally) plays a mission unattended. |
| `Build-MelonRelease.ps1` | Builds the release DLL and the player zip, with release checks. |
| `DeveloperDocs/10-N-PLAYER-DESIGN.md` | The design note for going from 2 to N players (seats, star topology). |
| `DeveloperDocs/11-UPDATE-RESILIENCE.md` | What happens when the game updates. |
| `MenaceCoop-README.md` | The **player** readme (shipped in the zip as README.txt). |

### Building

```powershell
dotnet build MenaceCoopMelon\MenaceCoopMelon.csproj -c Release -p:GameDir="<your MENACE folder>"
```

It compiles against `<game>\MelonLoader\net6\*.dll` and
`<game>\MelonLoader\Il2CppAssemblies\*.dll` (MelonLoader generates those the first
time the game runs with it). A normal build also copies the DLL into any test rigs
(`MenaceM1..M4`); add `-p:NoDeploy=true` to skip that. **Never** copy it into your
own game install by script -- that is a manual step.

Dev builds compile in dev tools (`COOP_DEVTOOLS`: item spawner, slow-link
simulator, Steam self-tests). The release build turns them off
(`-p:DevTools=false`) and `Build-MelonRelease.ps1` proves they are absent.

Game namespaces are prefixed by MelonLoader (`Menace.Tactical` becomes
`Il2CppMenace.Tactical`), so files import the `Il2Cpp...` namespaces, and
`using Menace = Il2CppMenace;` keeps `Menace.X.Y` references compiling.

**History:** up to v3.14.0 the mod was a BepInEx plugin built from the same sources.
That build was removed when the mod moved to MelonLoader (v4.0.0); `MenaceCoop/`
is the source folder and `MenaceCoopMelon/` the only project. The frozen BepInEx
release remains on Nexus as v3.14.0.
