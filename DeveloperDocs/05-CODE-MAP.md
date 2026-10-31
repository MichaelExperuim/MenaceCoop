# Code map

Sizes are rough line counts. Each file opens with a comment explaining why it
exists; read that first.

### Core loop and network
| File | Lines | Role |
|---|---|---|
| `MenaceCoopMelon/MelonEntry.cs` | 430 | Entry point. Settings (`Plugin.*`), patch registration list, version. |
| `CoopRunner.cs` | 2700 | Injected MonoBehaviour: drains network messages on the main thread each frame, ticks every subsystem, owns the co-op panel UI (UI Toolkit). |
| `CoopNet.cs` | 700 | Transport: TCP/Steam send and receive threads, message type `CoopMsg`, `Incoming` queue. |
| `CoopAuth.cs` | 200 | Session password and AES-GCM for direct IP. |
| `HostLinks.cs` | 260 | Host's per-client links (star topology). |
| `Relay.cs` | 40 | What the host forwards to other clients. |
| `Peers.cs` | 340 | Seats, names, per-seat ready state, roster broadcast. |
| `SteamLobby.cs` | 640 | Steam lobbies and invites. |
| `NetLag.cs` | 60 | Dev only: simulated slow link. |

### Battle replication
| File | Lines | Role |
|---|---|---|
| `Replication.cs` | 6000 | The heart: order broadcast (`Skill.Use`, `MoveTo` prefixes), the input/broadcast gates, message dispatch (`Apply`), replay of every order type, damage authority, action lock. |
| `Ownership.cs` | 490 | Which seat commands which squad; input gating. |
| `TurnPhases.cs` | 480 | Turn control: one human per player activation, handover from board state. |
| `AiPacing.cs` | 100 | Clients play the host's AI moves back one at a time. |
| `DesyncDetector.cs` | 2900 | Coop ids, state hashes, snapshots and corrections, hard restore. |
| `ContainerSync.cs` | 1130 | Buildings and vehicles (enter/leave/eject, riders). |
| `FleeSync.cs` | 290 | Routed squads: escape decided by the host / owner. |
| `SuppressionSync.cs` | 280 | Suppression follows the shooter. |
| `EffectSync.cs` | 180 | Status effects and vehicle module damage. |
| `FacingSync.cs` | 120 | Facing relayed by the owner. |
| `FactionSync.cs` | 140 | Units changing sides (emptied trucks). |
| `KillCredit.cs` | 120 | A partner's kill counts for the partner's squad. |
| `StructureSync.cs` | 300 | Destroyed structures. |
| `ReinforcementSync.cs` | 240 | Mid-battle reinforcements. |
| `FogSync.cs` | 165 | Host-decided fog of war. |
| `Hazards.cs` | 55 | Ground hazards (tile effects). |
| `OffmapSync.cs` / `EnemyOffmapSync.cs` | 715 / 115 | Ship support (shared pool) / enemy off-map abilities. |
| `ObjectiveSync.cs` / `ObjectiveProgressSync.cs` | 170 / 140 | Objectives graded by the host. |
| `DeploySync.cs` | 200 | Players deploy their own squads in planning. |
| `PhantomWatch.cs` | 640 | Enemies that exist on one machine only. |
| `BattleRoster.cs` / `TerrainCheck.cs` | 800 / 300 | Same enemies? Same map? |
| `BattleWatchdog.cs` | 240 | Last-line recovery for a battle that stopped progressing. |

### Sessions and the strategy layer
| File | Lines | Role |
|---|---|---|
| `SessionSync.cs` | 2200 | Save sync, mission entry (state-based), hold-fire, ready, prep mirror, mapgen pinning, `SaveLoadPatches`. |
| `SaveTransfer.cs` | 190 | Ships save files in chunks with a hash. |
| `GuestReload.cs` | 910 | FIX DESYNC as a reload; rejoining a battle in progress. |
| `BattleSave.cs` | 190 | Save and resume a battle (journal on top of the start save). |
| `StrategyMirror.cs` | 1180 | Host's strategy screens and economy mirrored live. |
| `EquipmentSync.cs` | 620 | Loadout changes in planning (host edits, clients mirror). |
| `PerkSync.cs` | 160 | Promotions. |
| `ConversationSync.cs` | 500 | Strategy conversations are the host's; events trigger a resync. |
| `LootSync.cs` | 115 | The host's loot is the loot. |
| `ModList.cs` / `SaveModCheck.cs` / `ApiCheck.cs` | 250 / 80 / 250 | Same mods? Save made with these mods? Did a game update move an API? |
| `OwnerLabels.cs` | 170 | HOST/GUEST tags above units. |

### Test and diagnosis (see 06-TESTING.md)
| File | Role |
|---|---|
| `SelfTest.cs` | Harness: plays a mission unattended (gated to rigs). |
| `UiAutopilot.cs` | Drives the host from the title screen into a battle. |
| `DebugSync.cs` | Synced debug commands and the harness scenarios. |
| `AutoCombatBridge.cs`, `Thinking.cs`, `EnemyBrain.cs`, `SkillPolicy.cs` | Integration with the separate MenaceAutoCombat mod (its brain plays when installed). |
| `BugProbes.cs`, `PerkAttackProbe.cs`, `DamageProbe.cs`, `JamProbe.cs`, `FogInputProbe.cs`, `FogTimingProbe.cs` | Runtime probes, off unless `ProbeBugs`. |
| `InstrumentationPatches.cs` | Logs orders and turn transitions. |
| `DevItemMenu.cs` | Dev only: item spawner. |
