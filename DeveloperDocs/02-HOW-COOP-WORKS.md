# How co-op works

### The one fact everything follows from

**A battle cannot be saved.** MENACE's save system captures the strategy layer only
(roster, campaign, operation). There is no way to hand a client the host's battle.
So every machine **builds the battle itself** from the same save and seeds, and then
has to be kept in step for the whole fight. Every system in this mod exists to
make independent simulations agree, or to notice and repair it when they do not.

### Authority: who decides what

| Thing | Decided by |
|---|---|
| The campaign, strategy screens, economy, loot, mission choice | **Host.** Clients mirror it (`StrategyMirror`, `SessionSync`, `LootSync`). |
| Enemy AI, hazards, damage over time, anything random with no shooter | **Host.** Clients replay its results (`AiPacing`, `EnemyBrain`). |
| A player unit's orders | **That unit's owner.** Relayed as orders, replayed by everyone else. |
| A hit (damage, death) | **The shooter's owner's machine.** Others swallow their local copy and apply the relayed result (`ehp` / `edie`). |
| Morale/suppression from a hit | Same as the hit (`SuppressionSync`, `FleeSync`). |
| Whose turn it is | **Board state**, never a timer (`TurnPhases`). |
| Fog of war, objectives, mission end | **Host** (`FogSync`, `ObjectiveSync`, `ObjectiveProgressSync`). |

Authority follows the **actor**, not the network role: "the host's game decides" is
true for AI units and the campaign, not for a guest's squad.

### Orders, not state

Player actions travel as **orders** (`skill`, `move`, `moveskill`, `skip`), stamped
with a stable unit id, the RNG seed, and the shooter's AP and limited uses before
the action. The receiver re-runs the same engine call (`Skill.Use`, `Actor.MoveTo`)
under `Replication.ApplyingRemote`, with damage suppressed: the replay is for the
animation and AP; the HP arrives separately from the machine that rolled it. See
`Replication.cs`, the `"skill"` case of `Apply`.

Relay **causes, not derived state**: send "unit X used skill Y at tile Z", not "unit
X has 40 AP". Derived state (turn-done flags, AP after a turn change) is recomputed
by each engine; relaying it fights the engine.

### Keeping in step

- **State hash** at every faction switch (`DesyncDetector`): every machine hashes
  each actor's tile, HP and AP and exchanges it. A mismatch triggers a soft
  correction from the host's snapshot; one that persists is logged as such.
- **Rosters and terrain** per battle (`BattleRoster`, `TerrainCheck`): same enemies?
  same map? These caught divergences the hash could not.
- **Repair**: FIX DESYNC reloads a client into the host's battle and fast-forwards
  it (`GuestReload`); the same machinery brings a player back after a disconnect.

### Topology and sessions

Star topology: every client links to the host only; the host forwards what other
clients must see (`Relay.cs` lists what it does *not* forward). Every player has a
**seat** (host 0, clients 1-3, `Peers.cs`), and ownership is per seat
(`Ownership.cs`). Transports: TCP with a session password and AES-GCM
(`CoopNet`, `CoopAuth`, `HostLinks`), or Steam P2P with lobbies (`SteamLobby`).

### Entering a mission (the most timing-sensitive path)

1. Host's campaign is synced to every client as a save file (`SessionSync.StartSync`
   -> `SaveTransfer`). Each client acks `synced` **per seat**; the host cannot
   launch until every connected client has acked (or failed, or a person pressed
   STOP WAITING).
2. Host launches; the game writes a battle-start autosave. The host ships that file
   plus `startmission` naming it.
3. Each client loads **that named save**, and only once that exact load has finished
   opens mission prep with the host's seed and deployment and launches.
4. Each client says `ready` once its battle screen is up; the host holds fire until
   every connected client is in.

Failures are reported (`entryfail`, `syncfail`) instead of waited out. No step
waits on a clock -- see [design rule 1](03-DESIGN-RULES.md).
