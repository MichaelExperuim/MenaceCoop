# Engine laws

Facts about MENACE's engine that each cost real debugging time. They are not
written down anywhere else.

**Turns and activations**
- The engine alternates **one player activation, then one AI activation** -- not
  "all players, then all enemies".
- `TacticalManager.IsPlayerTurn()` is activation-level and **flickers**. Never key a
  phase decision on it.
- A unit starting its turn with **0 AP was suppressed** in the previous enemy phase.
  That is a game rule, not a bug.
- Morale recovery runs in the unit's engine **turn end**. `SetTurnDone(true)` skips
  it, so a fleeing unit settled that way never rallies; end its turn with `SkipTurn`.

**Units and damage**
- `GetElements()` returns **living members only**; index is not identity. Address
  squad members by `GetSquaddieId()`.
- Hit points do not kill. `Element.Die()` does; an element driven to 0 HP is still
  alive.
- Damage lands **after** `Skill.Use()` returns (projectiles and area weapons
  resolve asynchronously), so a flag cleared in `finally` misses those hits.
- Every element damage write happens inside `Actor.OnDamageReceived`, which names
  the attacker: decide hit ownership there, never with a timing window.
- A **downed** unit moves to an orphan faction, so `GetFactionID()` is not "is this
  ours"; use `Ownership.IsKnownPlayerUnit`.
- A fleeing **player** unit moves to faction 2; set the faction **before** the morale
  on clients, or the engine's own return to faction 1 can throw in `SetFaction`.
- A **counterattack** perk fires when the enemy shot *lands*, on every machine at
  once, flagged `UsageParameter.NoApConsumed` (`Replication.IsEngineReaction`). It is
  an engine reaction, not an order: never hold it, never relay it.
- `UnitStatisticsListener.OnEntityDeath(target, null)` **throws** inside IL2CPP;
  reached through a Harmony patch it fail-fasts the process. Never pass a null killer.

**Maps and regeneration**
- Regeneration is **not deterministic from the mission seed**. The game uses several
  independent random streams (map generation `MapgenInput.Rng`,
  `TacticalManager.s_Random`, `Operation.m_Random`, `Squaddies.m_Random`); the mod
  pins each. Read the divergence: *positions match, composition differs* -> the
  composition stream; *composition matches, positions differ* -> the map stream.
- Map generation hangs off `MapgenInput` (`Mission` + `IMapGenerator` + one `Rng`),
  built per generation run.
- A containment move (into a building/vehicle) reports the **doorstep** as its
  landing tile; never "correct" a contained unit to it.

**Saves and loading**
- A new `StrategyState` object is not a loaded one: the game fills it in its
  `ProcessSaveState` coroutine afterwards. Wait for that coroutine
  (`SaveLoadPatches`), not for the object.
- Two overlapping save loads abort the scene transition and dump the game to the
  main menu. A load must never land on a scene change (e.g. leaving a battle).
- The battle-start autosave is a strategy-layer save from the moment of launch.

**Platform**
- Steam callbacks reach a mod only through the game's own dispatcher: derive from
  the non-generic `Steamworks.Callback` / `CallResult` and register with
  `CallbackDispatcher.Register` right after making the call (`SteamLobby.cs`).
- "Not responding" with flat CPU is a crash parked behind a hidden error box, not a
  deadlock.
- MelonLoader patches every `[HarmonyPatch]` class itself when asked; this mod sets
  `[assembly: HarmonyDontPatchAll]` and patches by hand from the list in
  `MelonEntry.cs` so each class reports separately. **A new patch class must be
  added to that list** or it never applies.
