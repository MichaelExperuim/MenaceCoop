# MenaceCoop developer documentation

Everything a developer needs to understand, change, test and release MenaceCoop.
The files are numbered in reading order. For your first day, check 00, then read 01-06 in order
and get a 2-player test running before touching code.

| # | Document | Purpose | Read it when |
|---|---|---|---|
| 00 | [Requirements](00-REQUIREMENTS.md) | What you need to build, test (one PC or two) and release: Windows, MENACE version, MelonLoader, .NET SDK, RAM, disk. Check a machine with `tools/Check-Requirements.ps1`. | Before anything else. |
| 01 | [Overview](01-OVERVIEW.md) | What the mod is, the repository layout, how to build, dev vs release builds. | First. |
| 02 | [How co-op works](02-HOW-COOP-WORKS.md) | The core architecture: why every machine simulates its own battle, who decides what (authority), orders vs state, how machines stay in step, sessions and seats, and the mission-entry sequence step by step. | Before any change to sync behaviour. |
| 03 | [Design rules](03-DESIGN-RULES.md) | Ten rules with the history behind them: wait on state not time, prevent rather than repair, the input and broadcast gates must agree, per-seat thinking, identity not from position, probe before fixing, dev tools never ship. | Before writing code; when reviewing a change. |
| 04 | [Engine laws](04-ENGINE-LAWS.md) | Facts about MENACE's engine found the hard way (turn activations, damage timing, regeneration streams, save loading, Steam callbacks, Harmony registration). Not written down anywhere else. | When something behaves "impossibly"; before fixing any bug. |
| 05 | [Code map](05-CODE-MAP.md) | Every source file, grouped by area (network, battle replication, sessions/strategy, test/diagnosis), with size and one-line role. | When looking for where something lives. |
| 06 | [Testing](06-TESTING.md) | Setting up 2-4 game copies ("rigs") on one PC, running unattended missions, harness scenarios, the simulated slow link, reading logs, probes, keeping test saves out of your own save list, the pre-release checklist. | Before your first change, and for every change after. |
| 07 | [Making changes](07-MAKING-CHANGES.md) | The workflow from bug to proven fix; how to add Harmony patches, settings and network messages correctly. | When starting a change. |
| 08 | [Releasing](08-RELEASING.md) | Version bump, release build and its checks, packaging rules, what to do after a game update. | When shipping. |
| 09 | [Decisions log](09-DECISIONS.md) | The major design decisions per version, in one table. Full reasoning and evidence are in [CHANGELOG.md](../CHANGELOG.md). | To understand why the mod is shaped as it is. |
| 10 | [N-player design](10-N-PLAYER-DESIGN.md) | The original design note for going from 2 to 4 players (seats, authority inversion, star topology), written before v5.0. Implemented; kept for its reasoning. Mentions `DESIGN-REVIEW.md` and `V5-PLAN.md`, which are internal notes not in the repo. | When working on seats, ownership or the network topology. |
| 11 | [Update resilience](11-UPDATE-RESILIENCE.md) | How the mod should behave when the game updates under it. | After a MENACE update. |

## Scripts that go with these docs

All in the repository root or `tools/`; each explains itself in its header comment.

| Script | What it does | Doc |
|---|---|---|
| `tools/Check-Requirements.ps1` | Checks this machine against 00-REQUIREMENTS (game, MelonLoader, .NET SDK, RAM, rig location). Read-only. | 00 |
| `tools/RigPaths.ps1` | Resolves where the game and the test rigs live (env vars, `rig.local.ps1`, or defaults). Every script uses it. | 06 |
| `tools/New-Rig.ps1` | Creates or refreshes a test rig (`MenaceM1..M4`) from your game install. | 06 |
| `tools/Deploy-ToRigs.ps1` | Copies the built DLL into every rig (optionally builds first). | 06 |
| `tools/Test-Saves.ps1` | Backs up the shared Saves folder before a run and restores it after. | 06 |
| `tools/check-run.sh` | Summarises a run from the rigs' logs (desyncs, bounces, errors). | 06 |
| `Launch-MelonCoopTest.ps1` | Starts 2-4 rigs, connects them, optionally plays a mission unattended with scenarios and simulated latency. | 06 |
| `Build-MelonRelease.ps1` | Builds the release DLL with dev tools off, verifies it, and packages the player zip. | 08 |

## Other files at the repository root

| File | Audience |
|---|---|
| `README.md` | Everyone: what the project is, links here. |
| `MenaceCoop-README.md` | **Players.** Shipped in the zip as `README.txt`: install, connect, features, known limitations. Update it for anything a player sees. |
| `CHANGELOG.md` | Everyone: every release, with the reasoning and evidence behind each change. |
| `LICENSE` | MIT. |

## Keeping these docs true

- A new engine fact goes in 04; a new rule with history in 03; a new file in 05.
- A new harness scenario or launcher flag goes in 06.
- Every release gets a CHANGELOG entry and, for a design decision, a row in 09.
