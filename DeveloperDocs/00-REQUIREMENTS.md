# Requirements

What you need to build, test and release MenaceCoop. Check a machine with:

```powershell
.\tools\Check-Requirements.ps1
```

## To build

| Requirement | Version | Why / notes |
|---|---|---|
| Windows | 10 or 11, 64-bit | MENACE is Windows-only; the tooling is PowerShell. |
| MENACE (Steam) | **v0.7.17** (the version the current source is built against) | The build compiles against the game's generated assemblies, so a different game version can fail to compile or misbehave at runtime. Startup logs `[API] Built against ... running ...`. |
| MelonLoader | **0.7.x** installed into the game (0.7.2 / 0.7.3 used) | Install it with the MENACE Modkit / mod manager. Launch the game once afterwards so it generates `MelonLoader\Il2CppAssemblies\`. |
| .NET SDK | **6.0 or later** (an SDK, not just a runtime) | `dotnet --list-sdks` must list one. The mod targets net6, MelonLoader's runtime. |
| Git | any recent | To clone and contribute. |

Nothing else is downloaded at build time: no NuGet packages; every reference comes
from the game folder.

## To test on one PC (the multi-copy rigs)

| Requirement | Amount | Why / notes |
|---|---|---|
| RAM | **16 GB** for 2 copies; **32 GB** for 3-4 | Each running copy uses about 4 GB. |
| Free disk | ~1 GB | Rigs are hardlinks to the game (no copy of the game files), plus logs. |
| Same drive as the game | required | Hardlinks only work within one drive; put the rigs beside the game folder (the default). |
| Windows PowerShell | 5.1+ (built into Windows) | The rig, launch, save and release scripts. |
| Git Bash (or WSL) | optional | Only for `tools/check-run.sh`, the log summariser. |
| Display | any | Copies start windowed at 960x540 by default. |

One Steam account is enough: every copy starts directly from its own folder.

## To test across two PCs (recommended before a release)

- Both PCs: the same MENACE version and the same mod build.
- A connection: Steam (both players in each other's friends list) or direct IP
  (the host's TCP port reachable, default shown in the co-op panel).

## To release

- Everything under "To build".
- No copy of the game running (the build would otherwise ship a locked, old DLL).
- `Build-MelonRelease.ps1` checks the rest itself (dev tools absent, zip layout,
  no scripts or nested archives).

## For players (not developers)

Players need only MENACE with MelonLoader and the `MenaceCoop.dll` from a release,
and every player in a session must run the same mod version and game version.
See `MenaceCoop-README.md` at the repository root.
