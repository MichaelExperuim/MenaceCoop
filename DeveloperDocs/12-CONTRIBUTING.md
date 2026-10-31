# Contributing

MenaceCoop is open source (MIT) and contributions are welcome. The repository is
owned and maintained by @MichaelExperuim, who reviews and approves every change.

## How to contribute

1. **Fork** the repository on GitHub (only the maintainer can push to it directly).
2. In your fork, create a **feature branch** from `main`, named for the change:
   `fix/mission-start-crash`, `feature/steam-invite-button`.
3. Make the change following [07-MAKING-CHANGES.md](07-MAKING-CHANGES.md), and test it
   on the rigs ([06-TESTING.md](06-TESTING.md)). Include in the pull request:
   - what was wrong or what is added, and why;
   - how you tested it (players, scenario, latency, which logs show it working);
   - a CHANGELOG line under the unreleased version.
4. Open a **pull request** from your feature branch to `main`.
5. The maintainer reviews it. Changes may be requested; push more commits to the
   same branch (this resets the approval). Only the maintainer merges.

## Rules on `main`

- No direct pushes except by the maintainer.
- Every pull request needs the maintainer's approval (CODEOWNERS), and approval is
  reset by any new commit.
- No force pushes or deletion of `main`.
- Automated workflows do not run on a fork's pull request until the maintainer
  approves them.

## Before you open a pull request

- `dotnet build` is clean.
- The change respects [03-DESIGN-RULES.md](03-DESIGN-RULES.md) (above all: wait on
  state, never on time) and [04-ENGINE-LAWS.md](04-ENGINE-LAWS.md).
- New Harmony patch classes are registered in `MelonEntry.cs`.
- Nothing machine-specific is committed (paths, saves, logs, `rig.local.ps1`).
- Dev-only code is under `COOP_DEVTOOLS`.

## Reporting bugs

Open an issue with `MelonLoader\Latest.log` from **every** player's game (the logs
side by side are what make a co-op bug findable), the mod and game version, and
what happened.
