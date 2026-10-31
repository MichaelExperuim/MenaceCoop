# Decisions log (short)

The full reasoning, with evidence, is in [CHANGELOG.md](../CHANGELOG.md) under each version.

| Version | Decision |
|---|---|
| 3.x | Mirror-input lockstep: orders relayed and replayed; damage follows the shooter; host runs the AI. |
| 3.9 | Pin `Operation.m_Random`: the two players had been fighting different armies. |
| 3.14 | Pin `MapgenInput.Rng` and add the terrain check: the maps had been different all along. |
| 3.x | Identity from engine ordering, never position. |
| 4.0 | Move from BepInEx to MelonLoader (the community's loader). |
| 4.x | Host-decided fog, loot, objectives, mission end; players deploy their own squads. |
| 5.0 | 2-4 players: seats, star topology, per-seat ownership and readiness. |
| 5.1 | Steam lobbies (experimental). |
| 5.2 | Port to game v0.7.17. |
| 5.3 | Fixes from player reports: fleeing squads, status effects, module damage, kill credit. |
| next | Mission entry state-based (no timers on the partner); per-seat sync acks; STOP WAITING; perk counterattacks run on every machine; simulated slow link for testing. |
