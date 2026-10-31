# MenaceCoop

Co-op for 2 to 4 players in MENACE (Overhype Studios), as a MelonLoader mod. One player hosts, the others join; the
squads are split between the players and you fight each mission together.

- **Players:** download the mod from the Nexus Mods page or this repo's
  [Releases](../../releases). Install and connection instructions are in
  [MenaceCoop-README.md](MenaceCoop-README.md) (shipped in the zip as `README.txt`).
- **Changes:** [CHANGELOG.md](CHANGELOG.md).
- **License:** MIT -- modify, fork and redistribute freely, with credit. See [LICENSE](LICENSE).

## How it works

The host owns the campaign and runs the enemy AI. In battle, each player's actions
are sent to the others as orders (over direct IP or Steam P2P) and replayed by their
games; the host's game decides anything random or contested, and periodic state
snapshots repair any drift.

**Developers:** start at [DeveloperDocs/README.md](DeveloperDocs/README.md) -- architecture,
design rules, engine facts, a map of the code, and how to set up the multi-copy test
rigs on one PC.

## Building

Requirements:

- MENACE with MelonLoader installed (run the game once so MelonLoader generates
  `MelonLoader\Il2CppAssemblies`).
- .NET SDK 6 or later.

```
dotnet build MenaceCoopMelon/MenaceCoopMelon.csproj -c Release -p:NoDeploy=true -p:GameDir="<path to your MENACE folder>"
```

The DLL is written to `MenaceCoopMelon/bin/Release/MenaceCoop.dll`; copy it into the
game's `Mods` folder. `GameDir` defaults to the author's install path.

The game's assemblies, MelonLoader and Harmony are referenced from your own install
and are not part of this repository.

## Layout

| Folder | Contents |
|---|---|
| `MenaceCoop/` | The mod: networking, order replication, sync and repair, UI |
| `MenaceCoopMelon/` | MelonLoader entry point and the project file |
| `Shared/` | Code shared with the author's other mods (patch report) |
