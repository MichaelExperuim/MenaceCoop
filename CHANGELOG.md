# MenaceCoop â€” Changelog

Private 2-player co-op mod for **MENACE** (Overhype Studios). MelonLoader IL2CPP mod from
v4.0.0 on; BepInEx 6 up to and including v3.14.0. Architecture: **host authority** â€” the host owns the campaign/strategy
layer; the guest mirrors it and controls half the squad in battle. Battle
actions replicate as orders over TCP or Steam P2P; the guest re-syncs from the
host's save at key moments.

Newest first. Dates are development dates.

---

## v5.3.1 - unreleased - mission start over real connections

Requires MENACE v0.7.17. Everyone in the session must update.

### Fixed

- A joining player could be thrown out of the game when a mission started, or enter
  it with no bottom panel (no squad buttons, no END TURN), over a real internet
  connection. Their game started the mission before the host's battle save had
  finished loading. Starting a mission now waits for that exact save to load, however
  slow the connection, and never goes ahead on a timer.
- With 3 or 4 players, the first player to finish loading the host's campaign let the
  host start the mission while the others were still loading. The host now waits for
  every player.
- Perk counterattacks now play on every player's screen, not just the owner's.
- A player whose game cannot enter the mission now tells the host, instead of the
  host waiting on them.

### Added

- **STOP WAITING FOR PLAYERS** in the co-op panel (host): appears only while the host
  is waiting for players to load, for a player whose game will never finish.
- Each player's log now records the vehicle loadouts at battle start, to help with
  reports of a weapon missing for one player.

### Changed

- The planning screen note: only the host plans the mission (squad sizes, loadouts,
  deployment); everyone else sees those changes live. Now stated in the readme.
- Developer documentation and portable test tools are in the repository
  (`DeveloperDocs/`, `tools/`), including a simulated slow connection for testing on
  one PC (dev builds only).

---

## v5.3.0 - 25 Oct 2026 - fixes from player reports

Requires MENACE v0.7.17. Everyone in the session must update.

### Fixed

- A squad that broke and fled could hang the turn: the players' games disagreed on
  whether it was fleeing, and the game waited on a unit nobody was allowed to move.
  A fleeing player squad now flees and rallies the same way on every screen, and its
  owner's game ends its turn so play goes on.
- The host's game could crash when a unit died with no attacker.
- Orders sent right at the start of a round could reach a player before that round
  had started on their screen, and were refused.
- Kills by joining players now count after the mission (commander mood, loot and
  "kill no more than X%" objectives).
- Status effects (acid, target markings and others) now reach every player.
- Enemies that were hidden or shown differently per player now follow the host's view.
- Vehicle module damage (destroyed engine or weapon, fires) now reaches every player.
- An enemy destroying a building with a squad inside now kills the squad the same way
  on every screen.
- The workshop now appears for joining players after the workshop event.
- A fleeing enemy that escaped no longer stays half-alive on the other players' screens.
- Enemy units that ended a move on a different tile for a joining player are now
  corrected within a few seconds, and arriving trucks no longer jump back and forth.
- Units garrisoned in buildings no longer get pulled outside on joining players'
  screens, and an enemy that enters a building on the host is put inside it for
  everyone.

## v5.2.0 - 16 Oct 2026 - MENACE v0.7.17 update

Requires MENACE v0.7.17. Everyone in the session must update. Includes the v5.1.0
changes (Steam lobbies, still experimental).

### Fixed

- Co-op on game v0.7.17: updated for engine changes to developer settings, mission
  objective progress, delayed off-map abilities and the save-load coroutine.
- A unit's facing after a move was not sent to the other players. When the game
  turned a vehicle at the end of its own move, the other players kept the old
  facing, which changes cover and armour arcs. The controlling player now sends the
  final facing after each move.
- A player's game could end another player's unit's turn at the start of a round,
  spending its action points locally. The owner's next move with that unit was then
  refused on that machine and the unit was placed afterwards. Only the owner can
  now end that unit's turn.

## v5.1.0 - 11 Oct 2026 - Steam lobbies (experimental)

Requires MENACE v0.7.15. Everyone in the session must update. Includes the v5.0.1
fixes.

Steam lobbies are experimental: tested between two game windows on one Steam
account, not yet between two different accounts. Connecting by typed Steam ID and
by direct IP are unchanged.

### Added

- Steam lobbies. Host (Steam) opens a friends-only Steam lobby. A friend who enters
  it -- by accepting an invite, by Join Game in the Steam friends list, or by the
  panel's JOIN button -- is connected automatically. Accepting an invite with the
  game closed starts the game and joins.
- INVITE FRIENDS (Steam) button for the host (opens the Steam overlay's invite dialog).
- A JOIN button in the panel for each Steam friend who is hosting. Join (Steam) with
  the Partner ID field empty joins the one friend who is hosting.

### Changed

- Partner ID is optional for Steam. Typed IDs still work, with or without the lobby.
- A message Steam refuses to send is logged as an error instead of being dropped
  silently.

### Fixed

- A player who left a Steam session and joined again while the host stayed connected
  did not receive the host's greeting again and could be left half-connected. The
  host now replaces that player's link, as a direct-IP reconnect does.

## v5.0.1 - 11 Oct 2026 - Hotfix

Requires MENACE v0.7.15. Everyone in the session must update.

### Fixed

- A mission-objective object (eggs, supplies, landing ships) destroyed by the host
  could disappear without the kill being counted, so the mission could not be
  completed. A destroyed objective object is now credited once on the host.
- When the host pressed PASS TURN TO PARTNER without ending a unit, a player who
  had just ended a unit's turn could be left on the hourglass. The host now tells
  the other players to carry on with their units.

### Changed

- Automatic desync repair no longer moves a player's own units back or brings dead
  units back. It applies the light correction only; the full restore runs only when
  a player presses FIX DESYNC.

## v5.0.0 - 4 Oct 2026 - 2 to 4 players

### Added

- Up to 4 players (host plus up to three others). Squads are split evenly.
- A player who drops or crashes mid-battle can rejoin. While they are away their
  units are played automatically (needs MenaceAutoCombat) and are handed back on
  return. Everyone is paused while they load back in and resumes together.

### Changed

- The host decides the fog of war, so every player sees the same units.

### Fixed

- Mission loot is the host's on every result screen.
- A fleeing enemy could escape on one machine only.
- Destroyed vehicles' wrecks and destroyed buildings were missing after a rejoin.
- Hosting with no one connected stopped enemies from moving.

## v4.3.0 - 2 Oct 2026 - Hotfix (for MENACE v0.7.15, Mission Selection Overhaul)

Requires MENACE v0.7.15. Both players must update.

### Fixed

- Missions could not be launched in co-op on game v0.7.15 (the game renamed the
  launch step; co-op logged an error every frame on the title screen).
- Battle replication hooks failed to load on v0.7.15 (the same rename broke the
  whole set).
- Equipment changes in mission prep were not mirrored to the guest on v0.7.15
  (the game reworked its item equip calls).
- Equipment changes made in the strategy Squads tab were never mirrored to the
  guest (only mission prep was). Both screens are mirrored now, and the guest's
  open Squads screen redraws.
- The CO-OP title tag overlapped the CONTINUE button on the redesigned title
  screen. It now sits under the MENACE logo.
- Battles could freeze with each player waiting on the other. The guest could
  start the next round while the host's enemies were still acting, after which the
  two turn orders never lined up again. The guest now never ends a round the host
  is still in.
- Battles could freeze when the host had no unit left to move and the guest
  ended one of several units: the guest waited for an enemy turn the host was not
  going to run. The host now tells the guest to carry on with its units.
- A vehicle could take the hit points of a downed squad standing on the same tile
  on the guest's screen (and drop to near zero morale on both).
- Structures could be destroyed on one screen and still standing on the other
  when the two machines' damage to it differed. If either machine's shot brings a
  structure down, it now comes down on both.
- Leftover orders from the previous battle were replayed into the next one at
  its start, logged as "never resolved -- DESYNC".
- A partner connecting while the host was already in a battle it entered alone
  (for example by loading a mission's launch autosave) was never brought in, and
  both games paused behind a sync screen. Both players now get a message instead:
  that battle cannot be joined -- finish or leave it, or connect first and then load.

### Known limitations

- An enemy squad that flees can escape off the map on the guest's machine only,
  while it is still on the board for the host. It stays gone on the guest's screen
  for the rest of the mission. FIX DESYNC (co-op panel) restores it.
- Joining a battle the host started before you connected is not supported (see
  above).
- The two result screens can list different loot. Only the display differs: both
  players end up with the host's items.

### Added

- Enemy off-map abilities (new in v0.7.15, from enemy activities) are synced: the
  host's AI schedules them and the guest gets the same strike zone, timing and
  effect.
- Conversations in the strategy layer (for example the post-operation talks) are
  synced. The host's answers decide; the guest's conversation follows the host's
  line by line and closes when the host's does. Previously the guest could not
  answer or close it until rejoining.
- Guest panel button "Let me click (30s, view only)" in the strategy layer. For 30
  seconds the guest can click through screens the host has open and look at
  commanders, units and perks. Press it again to end early. Campaign changes
  (buying, hiring, equipment, perks, dismissing leaders) stay with the host.

---

## v4.2.0 - 30 Sep 2026

### Fixed

- The guest could move and shoot while the enemy was still acting. When the host
  finished a unit and the enemy took its turn, the guest was handed control at the
  same moment, so a guest's shot could play out against an enemy that was still
  moving. The guest now waits ("Hold on -- the enemy is acting") until the enemy's
  turn is over.

---

## v4.1.0 - 28 Sep 2026

### Changed: auto combat is now the MenaceAutoCombat mod's job

Co-op no longer ships an auto-combat AI of its own. Auto combat in co-op comes
from the separate **MenaceAutoCombat** mod: install it (both players) and the
co-op panel's **Auto combat (your squads)** button drives it. It only ever
commands units you own. Without it, the button is greyed out and says the mod is
needed. Game speed stays locked at 1x while connected.

- If MenaceAutoCombat is installed but cannot connect to co-op (one of the two
  mods is out of date), the button says so and asks you to update both.

---

## v4.0.0 - 27 Sep 2026 - MelonLoader

The mod now runs on **MelonLoader** instead of BepInEx. The rest of the MENACE
modding ecosystem targets MelonLoader, and the two loaders cannot both be active
in one install, so a co-op player previously had to choose between this mod and
everything else.

### Changed

- Loader is MelonLoader. Requires MelonLoader (the MENACE Modkit installs it);
  the mod no longer ships or needs BepInEx.
- Co-op no longer skips the intro video; the `SkipIntro` setting is removed. The
  intro skip is now handled only by the separate **MenaceIntroSkip** mod, which
  includes the fix for content mods losing their graphics. **To skip the intro
  with this version of co-op you need MenaceIntroSkip 2.3.0 or newer.** Older
  versions of MenaceIntroSkip stand down whenever co-op is installed, so with them
  nothing skips the intro. Without MenaceIntroSkip, the intro plays as normal.
- Install location is `Mods\MenaceCoop.dll`, not `BepInEx\plugins\`.
- Settings moved to `UserData\MelonPreferences.cfg`, in `[MenaceCoop]`,
  `[MenaceCoop_Network]`, `[MenaceCoop_UI]` and `[MenaceCoop_Dev]` sections.
  Every setting keeps its name, default and meaning; nothing was renamed.
  Settings do **not** carry over from a BepInEx install and must be set again.
- The install report now names the sections to delete to return to defaults,
  rather than a config file: `MelonPreferences.cfg` belongs to every mod.
- `Network/Mode` is stored as text (`Off`/`Host`/`Guest`) rather than an enum.
- Log output goes to `MelonLoader\Latest.log`.
- The BepInEx build is discontinued; MelonLoader only.

### Fixed: transports did not drop their passengers on the guest

A transport that ejected passengers did so only on the host. The guest kept them
aboard for the whole battle, so the two players fought different armies — 19
enemy positions against 13 in one recorded mission — and every order addressed to
one of those units missed.

- Relayed skills are replayed with `IgnoreUsabilityCheck`. A skill was previously
  judged against the receiving machine's turn state rather than the sender's, so
  it could be refused and silently do nothing while still being logged as
  applied. The action point cost is unchanged, so both machines still agree on AP.
- A passenger ejection whose destination is blocked on the receiving machine is
  now ejected to the nearest tile the game accepts and then moved to the tile the
  sender used.
- A relayed move to a tile that is occupied here but free on the sender's machine
  is now carried out instead of skipped.

### Mod list check

On connecting, both players now exchange the list of mods they have installed --
every loaded MelonLoader mod and every content pack in `Mods\` (`modpack.json` /
`jiangyu.json`), each with its version. If the lists differ, both are told, with
the specific mods named: which ones only you have, which only your partner has,
and which are installed at different versions.

This matters more than it sounds. Content mods add and patch unit templates, so
two players with different mods generate **different armies** from the same
mission, and nothing else catches it: the desync check compares position,
hitpoints and action points, so an enemy that exists for one player and not the
other compares as identical. The first sign was a squad dying to something that
was never on the other player's screen.

It only warns. A mismatch cannot be repaired from inside the game, and refusing
to connect over it would block people whose mods differ only cosmetically.

### Added

- Works alongside MenaceAutoCombat. When both are installed, co-op hands it
  ownership, turn control and pause state, and MenaceAutoCombat drives auto combat;
  its fast forward and timer-based watchdog are off while connected.

### Added: session password and encryption (direct IP)

- Direct-IP sessions require a password, entered in the co-op panel's new
  **Password** field (setting `SessionPassword`). Both players enter the same one.
  The host can type their own or press **NEW CODE** for a random one; **COPY** puts
  it on the clipboard to send to the partner, **SHOW** reveals it.
- The password is never sent. Each side proves it knows it with a
  challenge-response over fresh random values, so a recorded exchange cannot be
  replayed, and the check is mutual: a guest also verifies the host.
- Until the password check passes, the host reads nothing else from the
  connection. A failed attempt is disconnected, and the host waits longer before
  accepting each further connection (2 s, doubling to 30 s).
- A guest refused for a wrong password stops trying instead of reconnecting.
- After the check, all traffic is encrypted and authenticated (AES-GCM, keys per
  session and direction). A frame that has been altered, replayed or dropped ends
  the connection.
- Only the host sends save files; the host ignores any the guest sends.
- Steam sessions are unchanged: Steam verifies the partner's identity, and the mod
  ignores every Steam user except the partner ID entered.

### Added: FIX DESYNC reloads the guest, rejoining, and battle saves

- **FIX DESYNC** now rebuilds the guest's battle instead of pushing unit
  positions onto it. The host pauses; the guest reloads the battle-start save,
  regenerates the mission, advances to the host's round, replays the battle's
  lasting changes (faction changes, destroyed structures, objectives, ship
  support used) and takes the host's board. About 20 seconds.
- **Rejoining:** a guest who disconnects mid-battle and reconnects is brought
  back into the same battle by the same reload.
- **Battle saves:** SAVE BATTLE STATE (host) now also writes a resumable save.
  RESUME SAVED BATTLE (host, co-op panel) relaunches that mission and restores
  the saved round and board, then brings the guest in. Works after quitting the
  game. One saved battle at a time.

### Fixed

- Bodies did not stay on the partner's screen: a unit killed by one player
  vanished without a death on the other player's machine. The partner's machine
  now runs the game's own death for the killed unit, with the shooter.
- Unit facing was not synchronised, so cover and firing arcs could differ
  between the two machines. Facing is now corrected from the host.
- Ship support was a separate pool per player (3 + 3 uses per operation). Both
  players now draw from one pool.
- Ship support strikes (strafing runs, barrages, missile strikes) were carried
  out only on the caller's machine: the partner never saw the strike, and a
  strike called by the guest did no damage at all. Strikes now run on both
  machines, with the damage decided once.
- Ship support that places something -- supply drops, sentry turrets, airdropped
  troops, cover, bunkers -- appeared only for the player who called it. It now
  appears for both players, as the same unit.
- Delayed ship support (turrets, airdrops, supply drops) could arrive a turn
  later and a tile away on the guest's machine. The host now decides when it
  lands, and it lands at the same moment and on the same tile for both players.
- An error in a tile lookup next to a building could abort a position
  correction or a passenger unloading on one machine.
- A relayed attack could be refused on the partner's machine because its copy of
  the shooter had stale action points. The attack now carries the shooter's AP.
- Limited-use attacks (grenades, weapon salvos) could fall out of step between
  the two machines; the attack now carries the shooter's remaining uses.
- Suppression from a hit was applied on both machines and could disagree; it is
  now applied by the machine that decided the hit and relayed.
- The map could differ slightly between the two machines (forest vs road). The
  map generator is now seeded identically on both.
- Transport wrecks blocked a tile on one machine only.
- Skybot drones ("Falcon" Recon, "Wardog" AT) were filed as player units and had
  their turn ended every round. They now appear for both players on the same tile,
  are flown by the game's allied AI as in single player, and the deploying squad's
  Move / Detonate commands are replayed on the partner's machine.
- An enemy squad that broke and fled could escape the battle on the host's machine
  and stay on the guest's. The host's escape is now sent to the guest. (An escape
  that happens only on the guest's machine is not yet prevented; see Known
  limitations.)
- Damage the host decides (burning and other damage over time, hazards) that
  happened during the guest's turn was not sent to the guest.
- A guest disconnecting mid-battle froze the host's enemies until a reload.
- The host is held until the guest has actually loaded into a battle, instead of
  for a fixed 12 seconds.
- Relayed damage packets to squad members, morale, stance, destroyed structures,
  objective progress and abandoned-vehicle faction changes are synchronised.

---

## v3.14.0 - 13 Sep 2026 - the maps were different all along

A tester's v3.13.0 report (host + client logs) turned out to be one root
cause wearing four costumes.

### The map was never being synced

`[TERRAIN] MAPS DIFFER -- 440 of 1764 tiles differ (24.9%)`, logged at round 1
before a shot was fired. Not battle damage: generation.

v3.9.0 pinned `Operation.m_Random` and fixed enemy *composition*, and the note
written at the time assumed the map was already deterministic from the mission
seed. It is not. All three battles in the report generated the **same army** --
18 enemy positions vs 18, 18 vs 18, 9 vs 9, matching templates, matching group
sizes -- standing on **different tiles**.

Map generation runs off a `MapgenInput` (mission + generator + one `Rng`), built
fresh per run. That `Rng` is now re-seeded from the mission seed at every
construction, which also makes it immune to the drift that breaks simpler
schemes: the guest opens mission prep twice and so runs preview generation more
often than the host.

### "You are not fighting the same army" was the wrong accusation

There was a real desync, but the roster check keys entries on `template|x|y`, so
an identical army on different tiles reads as *"host has NOTHING here."* It now
compares composition as a multiset first and, when that matches, says **SAME
ARMY, DIFFERENT PLACEMENT** and points at the terrain report instead.

### The guest's permanent hourglass

The one that ended the battle. `aidone` -- the message that tells the guest an AI
unit has finished acting -- carried only coordinates, and enemy identity was
itself hashed from the spawn tile. With the boards disagreeing about tiles,
every enemy had a different id on each machine and every relayed order missed:

```
[DESYNC] Resync requested: order 'aidone' at (10,19) unresolvable   x7
```

No AI activation ever completed, so the guest sat with a greyed-out pass-turn
button. Identity no longer contains position at all: opening deployment uses the
actor's slot index in its faction list, reinforcements use their arrival
ordinal. Both come from the engine's own ordering and survive a unit moving.

**This is expected to be the fix for the reinforcement and VIP-escort desyncs
too** -- reinforcements arrive mid-battle and an escorted VIP moves, so
position-keyed orders broke for both even on a matching map.

### Both players could control the rescued VIP

Reported: *"during the hostage rescue mission, when you need to control VIP, both
players can control him, which causes significant desyncs."*

Ownership is built once, at battle start, from the deployment list. A VIP becomes
player-controllable **later**, so he was in neither player's set -- and the
selection gate only ever blocked units it already knew about. Neither machine
stopped him being selected, so both could order the same actor. That is worse
than a dropped order: it is two conflicting orders applied in different sequences
on each side.

Units that join the player side after deployment are now adopted, with the
**host** taking anything unassigned. Both machines apply that rule independently,
so they agree with no message and nothing to race. The test is the engine's own
`IsSelectableByPlayer` rather than faction membership, because a rescued hostage
can be controllable while still filed under a civilian faction.

### "I pressed end turn and nothing happened"

The guest's pass-turn was fire-and-forget, and the host could silently discard it:

```csharp
if (_hostControls) return;                 // not theirs to pass
```

The guest only sends that request because it believes it holds control, so
reaching that line means the two sides *disagree about who is acting* -- exactly
the state a guest cannot escape alone, which is why reconnecting was the only way
out. The host now re-broadcasts control instead of staying quiet, correcting the
guest's view. The guest also tracks the request and retries it up to four times,
then says so plainly rather than waiting forever.

### Reinforcements: the wave itself was never synced

Each machine generates its own reinforcement wave locally
(`ReinforcementGroup.HandleReinforcementGroup` draws from `PossibleArmies`),
because a battle cannot be transferred. Two independent draws give two different
waves -- *"half of the reinforcement vehicles do not exist"* for the guest. The
draw is now pinned per (mission seed, round, group index); round-keyed, since the
host can be a round ahead and the two must match **per round**, not merely match.

The vehicles that did arrive never unloaded because unloading is an AI
activation, i.e. a relayed order -- and mid-battle arrivals had no reliable
identity to address. Spawn capture hooked only skill-effect spawns, which
mission-driven waves do not use, so those units got no id until a later sweep
guessed one from their tile. Capture now happens at `BaseFaction.AddActor`, the
one door every arrival comes through, using arrival ordinal rather than tile.

Shipped without a log to confirm against, so the reinforcement path logs
`[REINF] group #N round=… -> result` on both sides. If a paired run shows
matching inputs but different waves, the draw does not use `Operation.m_Random`
and the pin is aimed at the wrong stream.

### Vehicles were never modelled at all

Found by reading the engine while a live co-op battle ran. MENACE treats a
vehicle as a **container** -- `EntityContainerType` is
`{None, Building, Vehicle, Forest, Tower}` -- and a pirate wave arrives as
mounted squads (`enemy.pirate_scavengers_on_heavy_truck`). The mod handled
exactly half of that relationship:

- **Entering** a container was stamped onto move messages, AI included.
- **Leaving** one was replicated nowhere.

That asymmetry is fatal because the guest runs no AI whatsoever -- `AIFaction`'s
`OnTurnStart`, `Process` and `Think` are all skipped, the host runs it and relays
results. So when the host's AI unloads a transport via
`Entity.SpawnContainedEntity`, the guest has no way to reach that decision on its
own and simply never unloads. The transport is not stuck; it was never told.
Unloads now relay like any other order, addressed by stable id with the tile as
fallback.

Two long-standing tests were also asking the wrong question. `Actor.IsInsideTower()`
covers one container type of five, and both callers failed quietly for vehicles:
the desync healer tried to reposition passengers that have no tile to stand on,
and the roster check dropped every vehicle passenger from the manifest -- so a
truck full of pirates compared as an empty tile on both machines, a divergence
the check could not see. Both now use `IsContainedWithinAnotherEntity()`, keeping
the tower test as a fallback.

Finally, a squad's template *changes* when it dismounts
(`..._on_heavy_truck` -> `...`). Ids are cached per actor, so a unit identified
before its dismount is safe -- but two machines first seeing the same unit on
opposite sides of that transition would derive different ids, reintroducing the
exact failure the identity rework removed. Identity now folds the mount suffix
away.

### The reinforcement wave read local board state

The one that mattered, and the reason three earlier fixes all missed.

`ReinforcementGroup.HandleReinforcementGroup` takes `_concurrentEnemyStrength` --
the enemy strength on *that machine's* board at *that machine's* round
transition. The guest replays the host's orders, so its board is always slightly
behind, so its reading is always stale. Same seed, same round, same group index,
**different budget, different army**. Identical shape in every run:

```
round 1-2   host 1105   guest 1105     nothing has died yet
round 3     host 1025   guest 1105     host has killed 80 points
round 4     host  800   guest 1270
```

This was never the guest MISSING kills -- it is two machines sampling the board
at different instants, which no amount of faster syncing can fix. The host now
publishes the budget it drew with, keyed on (round, group), and the guest uses
that instead of its own.

With the budget aligned, waves came out the same SIZE and still differed in
composition -- host 2 chaingun + 3 outcast trucks against guest 1 + 4. Army
selection was reading `TacticalManager.s_Random`, which this mod reseeds on every
order, so its position depended on how many orders each machine had processed.
Pinned at draw time; the next relayed order reseeds it anyway.

Result, first zero-drift run of the project:

```
strength   r1..r6 identical      arrivals   byte-identical
drift      0                     objectives all six agree on both machines
```

Four independent sources feed wave generation -- map RNG, army composition,
wave budget, army selection -- and each fix exposed the next.

### Withdrawn: the element death relay

Built to fix the guest's enemy strength staying high; that was the budget bug
above. Never demonstrated necessary, and demonstrably harmful -- the guest
applied the host's death and then its own simulation killed the same element
again, logging one squaddie dying three times and leaving the roster at "host 4,
you 3", one unit SHORT of the host.

The `[DMGPROBE]` measurement stays (gated on SelfTestTrace). It records that the
two machines resolve the same squaddie's death with `applied=2` against
`applied=4` -- damage TIMING differs while the outcome converges, which is
exactly why relaying the outcome was the wrong intervention.

**Both relays withdrawn this cycle rested on the same wrong assumption:** that
the guest cannot reach a decision the host makes. In both cases the engine was
already replicating it -- container unloads through the skill path, deaths
through re-simulation.

### Withdrawn: the container unload relay

Built on a wrong assumption and deleted rather than repaired. The premise was
that a transport dropping its passengers is an AI decision the guest can never
reach, because the guest runs no AI. But the unload IS a skill --
`special.vehicle_eject_passengers_free` -- and skills already replicate through
the ordinary order path. The harness logs settle it:

```
host   Skill.Use(special.vehicle_eject_passengers_free ...) remote=False
guest  Skill.Use(special.vehicle_eject_passengers_free ...) remote=True
```

The guest had been replaying every eject correctly the whole time. The relay was
a second trigger for an event that already had one, and since a mounted squad is
a `[TransientActor]` with no coop id it could never resolve anyway: 20 relays
sent, 0 applied, 14 "target not found" warnings per battle.

Two findings from the attempt outlived the code. `Entity.ToActor()` **throws**
for a non-actor rather than returning null. And `MayBroadcastFor` answers true
for any AI unit on **both** machines -- correct for moves, wrong for anything
fired off local simulation, where it makes the guest relay straight back.

### Garrisons that start the battle inside a building

Reachable by nothing the mod had. Not in any faction's actor list, not under the
actors root, not an occupant of any *Actor* (their container is a Structure), and
they never fire `OnEnteredContainer` because they were never outside. Measured:
`allied.local_forces_basic_infantry` relaying as "NO identity -- at (0,0),
faction 0". A once-per-battle map scan now finds and identifies them.

### The deployment/arrival boundary was not a shared moment

It flipped on the first sweep that saw any actor -- i.e. whenever each machine
happened to look. A unit already present when the guest first looked was filed as
deployment and given a slot id, while the host saw the same unit arrive and gave
it an arrival id. Same unit, two schemes, two ids, every order missed. One
civilian (`CId=1375245641`) accounted for 15 unresolved orders that way. The
boundary is now `BattleRoster.BoardSettled`, which exists to be the same instant
on both machines.

### Objective states now come from the host

Objectives were never replicated; each machine scored its own board. A completed
mission read Primary **Completed** on the host and **Failed** on the guest, with
every secondary failed too. The campaign result comes from the host's save, so
the guest's screen was not merely different, it was wrong.

### Every hit on a vehicle landed twice

A pirate heavy truck took three 6-point rifle hits from the local-forces
garrison and went 140 -> 112 on **both** machines, where 122 was correct. Every
shot is simulated twice -- by the machine that fired it and by the partner
replaying it -- and the replay was meant to be muted. The mute only spanned
`Skill.Use()`, and projectile damage lands after that returns, so the replay's
damage went through, was relayed back, and the shooter applied the echo on top of
its own hit: 38 host hits relayed to the guest and 34 guest hits relayed back in
one mission, none muted. It is also why the late roster check found "pirate
outcasts: host 4, you 3". Two timing windows had been tried against this before
and both regressed, because a window cannot tell a late replayed hit from this
machine's own next hit on the same target.

Hits are now owned by **who fired them**, which the engine names in
`Actor.OnDamageReceived` -- and every element damage write happens inside that
call (138 of 138 in the probe logs). A player unit's hit belongs to the machine
that owns that unit; AI of every faction, structures, hazards and damage over time
belong to the host. The other machine discards its own copy and applies the
owner's relayed damage instead. Structures keep simulating on both, since they
have no relay.

### Investigated, not a bug: "auto-combat isn't fighting"

A unit sat out rounds 3 and 4 of a mission with "done for this round (ap left=0)".
Suppression, not the brain: a pirate chaingun team suppressed it twice in the
enemy phase, and a suppressed unit starts its next turn with no action points.
Across every saved log, 11 of 13 zero-AP turn-ends followed suppression of that
unit since its last turn; the other 2 had spent their AP shooting. What looks
like idling is the AI advancing into a chaingun's arc and getting pinned -- a
positioning weakness, not a turn or sync fault.

(A first diagnosis -- that the brain judged a unit before its turn started -- was
wrong and was withdrawn after a run disproved it: the `OnTurnStart` that followed
each zero-AP line was caused by the brain's own selection call after logging.)

Also not a bug: a unit ending with AP unspent in a **defence** mission is usually
holding the defence area -- the nearest enemy was out of weapon range and
advancing would have left the zone.

Open: the host logs suppression and morale breaks (Wavering, Fleeing) on a
guest-owned squad that the guest's own copy never raises; the guest only receives
the morale later through drift correction.

### Every hit on an enemy squad went to its first member

Squaddie ids are only unique in player squads -- every element of a pirate squad
reports id 0. Relayed damage found its target by id and took the first match, so
on the receiving machine one member of each enemy squad absorbed every hit while
the shooter's machine spread them across the squad; the two then lost different
members, which is where "pirate outcasts: host 4, you 3" came from. The HP
correction had the identical flaw (110 `HP drift ... sq0` corrections in one
verified run, each writing another member's HP onto the first). Both now address
an element by its permanent slot, which is unique within a unit.

### The guest could fail a mission the host was still playing

In a verified run the guest's primary objective ("too many hostile units inside
the zone for too long") went to Failed at the end of round 5 while the host's
stayed Ongoing -- replayed enemies stand in the zone a moment longer on the guest.
Objective changes were relayed from the host but the guest still graded itself
in parallel, so its engine then tried to leave the battle every turn and showed a
failure that never happened. The guest can no longer decide Completed or Failed
on its own; those come only from the host.

### The guest froze waiting on an enemy the host had already finished

Reported from manual testing as a hard freeze at round 5. The host finished two
AI factions back to back -- faction 3, then faction 6, where only a truck acted
and the rest were fleeing or suppressed. Both "the host left an AI faction"
messages reached the guest while it was still replaying faction 3, and the
message did not say WHICH faction had been left, so both settled faction 3. The
guest then moved on to faction 6 and waited for an enemy squad whose turn the
host would never play, while the host had already handed turn control to the
guest: each side waiting on the other.

The message now names the faction and round the host left. A guest that has not
got there yet remembers it and settles that faction the moment it arrives.

### The guest froze after one machine saw a squad flee and the other did not

A second manual-test freeze, same shape, different cause. Enemy fire broke a
guest-owned squad's morale on the host; the engine moves a fleeing unit out of the
player faction, so at the start of the next round the host's turn-order safety net
marked that squad "down and cannot act" -- without telling anyone. On the guest the
morale break had never happened, so the squad took its turn and ended it, the
guest's engine moved on to the garrison, and the host, which never ran that turn,
waited on the guest indefinitely.

A unit either machine retires this way is now reported to the partner as an
ordinary turn-done, so both skip it. (Open, and the underlying cause: morale and
suppression on a guest-owned squad are decided only on the host and reach the guest
late, through corrections.)

### A destroyed vehicle stayed standing on the guest for a while

A pirate flamethrower truck the host destroyed kept counting as alive on the guest
until a periodic sweep noticed it had no living elements -- long enough for the late
roster check to report "BATTLES HAVE DRIFTED APART: host 0, you 1". The engine marks
a unit dying inside its own damage path, which a relayed hit never enters. A unit
whose last element is killed by a relayed hit or death is now marked dead at once.

### A false "abandon this mission" over friendly garrisons

"SAME ARMY, DIFFERENT PLACEMENT" fired three times at the start of a mission that
then played cleanly. Enemies and terrain matched; the only difference was an allied
garrison squad inside a building on one machine and outside on the other. The
army check now compares hostile units only.

### Health bars lagged behind the partner's hits

Reported from manual testing: "health desync on machine gun fire". Every element
agreed -- the guest's resyncs each reported "0 HP/AP ... the last correction
changed nothing" -- yet the state hash kept differing. The hash, and the health
bar, read the unit's TOTAL hit points, which the engine recomputes only inside
its own damage path. Relayed damage and deaths wrote elements directly and never
recomputed it. Since hits are now owned by the shooter, the relay is the only
damage the other machine applies, so the partner's hits simply did not move the
bar. Relayed damage, relayed deaths and HP corrections now recompute the total,
and a correction that fixes only a stale total counts as a fix.

### Grenades and other gear did not redraw on the guest's planning screen

The equipment mirror placed the item (logged as "Mirrored place") but only asked
the planning screen to re-read its unit list, which does not redraw an open unit
window. The window and its equipment slots are now refreshed as well.

### A sync pause on the result screen

After a mission ended, both players sat on the result screen while the battle
still technically existed. Three minutes later the stall watchdog declared the
board stuck and opened a SYNC PAUSE with a hard resync -- over a finished battle,
on the screen people stop to read. The watchdog now stands down once either
machine's mission is over.

### Hidden-enemy markers follow the game's own fog of war

Reported: a marker appeared on an enemy neither player could see -- only the
garrison could. The detector published every enemy and guessed on the other side
with "within 14 tiles of a player unit", which is distance, not sight, and
ignores every friendly that is not a player unit. It now publishes only enemies
the engine reports as visible to the player (`VisibilityToPlayer`), so a marker
can never reveal what the fog hides, and the distance guess is gone.

A vehicle that drove 9 tiles was also flagged on both machines at once: each had
moved the truck and then received a partner report sent before the partner had
applied the same move. Reports are now only compared once they were received
after this machine's board last moved.

### The guest threw away the host's opening AI turns

Every "Order 'aidone' never resolved -- DESYNC" in five guest logs (53 of 53)
fired *outside* a battle. They looked like leftovers from a finished battle and
were not: the id in the first one was a pirate squad the guest went on to fight
for the entire mission. The host had launched first, its round-1 AI turns were
already arriving while the guest was still generating the map, and with no board
to resolve them against the 3-second grace expired and they were discarded.

Orders are now held, without ageing, until the board exists. When a battle has
genuinely ended, anything still queued for it is discarded instead -- together
with that battle's host wave budgets, which were never being cleared and could
have been reused by the next battle's reinforcement draw.

### Per-hit damage logging is off by default

The damage probe rode `SelfTestTrace`, which ships ON, so every player's log got
one line per hit on both machines. It now has its own `[Dev] TraceDamage`
switch, off by default.

### Release script refuses nested archives

`Build-Release.ps1` now opens the finished zip and fails if it contains any
archive, since Nexus quarantines the whole upload for one.

### Corrected: "the element damage relay has never fired" was wrong

An earlier note here claimed `[LOCALDMG]` logged zero lines and the `ehp`
channel was vestigial. That was a measurement taken with the trace gated off.
The relay is live and carries every hit -- which is exactly how it came to
double-count them (see "Every hit on a vehicle landed twice" above).

### The container relay's first live run, and what it broke

Shipping the unload relay without a live test cost a battle, and the logs traced
one bug through five symptoms.

`Entity.ToActor()` **throws** for a non-actor rather than returning null. Every
civilian leaving a building raised `Unable to cast object of type 'Structure' to
type 'Actor'` -- 93 per machine -- aborting the broadcast before anything sent.

Worse, structures were being relayed at all. A vehicle unloads because the host's
AI decided to, and the guest runs no AI, so it needs telling. A building
releasing occupants is driven by damage that is *already replicated*, so both
machines do it independently. Relaying it was pure interference, and because
`MayBroadcastFor` answers "true" for any AI unit on **both** machines, the guest
relayed its own local structure unloads back at the host. The host applied 12 of
them, spawning units its own simulation never created.

That inflation is visible in the reinforcement diagnostics:

```
round=1   host 935 -> 935     guest 935 -> 935
round=2   host 1770           guest 935
```

`concurrentStrength` feeds the wave draw, so an inflated host board drew larger
waves every round after. The host finished with 46 mid-battle arrivals to the
guest's 34, including a chaingun truck and a flamethrower truck the guest never
received at all -- reported as "some enemies are straight up invisible". They
were real, and shooting, and not on the guest's board.

The rest followed: 266 unresolvable orders, 602 HP-drift corrections and eleven
stop-the-world pauses on a board that looked normal to both players.

Fixed: actors only, host only, `IsActor()` before `ToActor()`, and the guest's
tile fallback now asks for the tile's ACTOR (a transport) rather than its Entity
(the structure standing on it), which is why it rescued none of the 36 misses.

### Pauses no longer fire for faults a pause cannot fix

An unresolvable order is an identity fault, and a hard resync addresses units *by
id* -- so when the ids disagree it has nothing to work with. It slipped past the
existing no-op guard because the corrections *did* fix something (a pile of HP
drift), leaving `_lastResyncFixedSomething` true and the escalation armed. Now
identity faults report once and keep playing.

### Red and orange banners, defined

The red banner existed for one case -- mismatched builds, where nothing works and
the message must stay up -- and got reused until colour was the only difference.
Stated in code now:

- **RED = the player must act.** Mismatched builds (sticky), different missions,
  different maps, genuinely different armies. Fix the install, or relaunch.
- **ORANGE = the mod is handling it.** Resyncs, round mismatches, stalled-board
  recovery, blocked guest actions, and drift being actively corrected.

The test is agency, not severity. Mid-battle drift and one-sided transport
unloads moved to orange under that rule; both are recoverable and both were
firing repeatedly during ordinary play.

### Builds were not reaching the game

`ContinueOnError="true"` on the deploy copy meant a locked DLL failed silently
while the build printed "Build succeeded". Four rounds of fixes sat in
`bin/Release` while play-testing ran against an older DLL, and a game restart
would not have picked them up -- only a rebuild with the game closed. The build
now prints `DEPLOYED:` or a warning naming the process holding the file.

### FIX DESYNC now says what it did

Reported as doing nothing. It was working every time -- the host logged three
forced pushes -- but it pushes *unit state*, and unit state cannot repair
terrain, so nothing visibly changed. It now logs every press, and when the maps
are known to differ it says so instead of pretending it can help.

### Tooling: the API index could go stale invisibly

`_Tools/api-index.txt` stamped the *live game's* `metadata-size` onto content
decompiled from a different build, so the staleness check passed forever. The
decompile was from 16 Jul; the game updated 20 Aug. The index confidently listed
`Mission.MapgenTemplates`, which the shipped build does not have. The header now
carries both dates and prints a `*** STALE ***` banner, and `CLAUDE.md` records
that interop -- not the decompile -- is authoritative.

---

## v3.13.0 - 26 Aug 2026 - what auto-combat is thinking, and a corner it could not leave

### The red banner can be dismissed

Reported: *"add ability to hide red banner, we have one for green and orange and
it stays forever which is annoying."* It was written for one case -- mismatched
game versions -- where staying forever is correct, because nothing will work
until you fix it. The roster and terrain checks then started using it for things
a player might reasonably decide to play through, and a permanent band across the
screen became a punishment.

Click it to dismiss that alert, or turn it off entirely (`UI/ShowAlertBanner`,
beside the green and orange toggles). A NEW alert with different text still shows;
dismissing suppresses the notification only, never the log.

### Auto-combat says what it is doing, on screen and in the log

Ported from the standalone mod, where it was asked for in these words: *"a section
under the panel that explains the AI's current thinking, like Angel is moving...
angel is attacking."* Then extended to the log, because *"just saying turns
finished isnt descriptive."*

```
[AUTO] Pike is attacking Alien_01_small_spiderling.
[AUTO] Darby is helping Tech.
[AUTO] Carda is using suppressive fire.
[AUTO] Tech is done for this round.
[AUTO] Waiting for your partner's squads...
```

That last one earns its place twice over in co-op: on the board, "waiting for your
partner" and "auto-combat is stuck" look identical. Now they are not.

### Squads that spawned in a corner stayed in the corner

Reported: *"squads are sitting in the bottom right corner... ai combat is excellent
when they spawn in the middle but not when they spawn at the bottom or a side."*

`PatrolWaypoint` picked a compass DIRECTION from wherever the unit stood and
walked that way. A squad in the bottom-right corner whose seeded direction pointed
further into the corner got a null tile at every reach and therefore never
patrolled at all -- it stood still. Near an edge only two or three of the eight
directions land on the map, so the unit flipped between them round after round:
the reported "rotating side to side".

Patrol now heads for absolute destinations -- quadrant centres and the middle --
skipping any it is already standing near, and falling back to the map centre when
everything is close. A cornered unit always has somewhere to go, because "away
from here" is now a property of the map rather than of the compass.

### The engine's own AI scorer is reachable

Asked: *"is there a way to analyze the enemy AI and steal their algorithms, since
they perform better."* There is, and better than reimplementing it.

```
[BRAIN] Engine AI scorer probe: 4/4 of your units carry an engine Agent,
        18 behaviour(s), 6 skill-behaviour(s) -> USABLE
```

`Actor.GetAgent()` exists on every actor, including player-controlled ones, and
the agent owns `SkillBehavior.GetTargetValue(...)` -- the function the enemy uses
to decide how good a skill is against a tile from a position. Nothing is
constructed to reach it: building engine objects from a mod earns an
`AccessViolationException`, which .NET Core cannot catch.

`Goal` turned out to be an enum -- Damage, Suppression, Stun -- so the question
can be asked *for a purpose*, which is exactly the pirates-versus-bugs
distinction.

**This release only probes and reports.** Nothing is wired into the decision path:
that is a behaviour change to auto-combat and it has not been shown to play better
yet. `ScoreTarget` returns a nullable and callers must read null as "no opinion",
never as "bad target" -- a zero would silently veto every shot the moment it
stopped resolving.

### Fixed: the roster check cried wolf over your own casualties

The first live catch was a false alarm, and the check's own fault:

```
[ROSTER] player_squad.darby: host 1, you 0
[ROSTER] You are not fighting the same army.
```

`darby` is a player squad. The manifest skips the player faction -- but the engine
moves a DOWNED unit out of that faction into an orphan one, so a squad that goes
down stops looking like a player unit and starts counting as an enemy. Squads go
down a moment apart on the two machines, so the counts disagreed and the check
announced a different army over one of your own casualties.

It now excludes player units by identity (which survives the faction move) rather
than by current faction, skips dying units entirely, and needs a difference to
persist 20 seconds rather than 6 before saying anything.

### Also

- Roster check now **re-publishes every 20s** instead of once at battle start, so
  reinforcement waves arriving mid-battle are covered. Latched so a clean board
  does not announce itself every 20 seconds.
- **Vehicle passengers are counted.** They have no tile of their own and were being
  skipped, so an enemy transport's crew vanished from the manifest and a squad
  riding on one machine while standing on the other compared as equal.
- Available operations and their enemy factions are listed when a campaign loads,
  so choosing what to test against is a matter of reading the log.

### Known, still open

- **Damage rolls can diverge**, producing HP corrections that thrash (114 of 145
  in one pirate mission were immediate reversals of each other). Root cause not
  established; the healer is reconciling two legitimate-but-different results.
- **Interrupted moves reconcile by teleport**, which reads as rubberbanding: the
  host's unit is interrupted mid-walk, the relay reports where it stopped, and the
  guest snaps its unit there. Positions end up correct; it is only ugly.
- **~20 ROUND RUNAWAY warnings per mission transition** on the guest, transient.
- **Morale drift** between machines, cause not established.

---

## v3.12.1 - 23 Aug 2026 - a weaker check beats no check

The roster check anchors to the moment before the first activation, because that
is the only instant both machines can describe without positions having moved. A
guest that joins late misses that window -- and the check then did nothing at all:

```
[ROSTER] Could not compare before the fighting started -- skipping this battle's check
```

Which meant it was reliable on the first battle of a session and gave up on the
later ones. The players who reported the original bug were on **mission idx=2**.
The one check written for their problem would have skipped their battle.

There is a weaker comparison that survives the fighting starting. Positions and
squad sizes drift honestly once units move and die; **which unit types are on the
board** does not. "The host has a flamethrower team and you do not" is true
whenever it is asked, and that is the failure that killed a player's squad.

So a missed window now falls back to comparing the multiset of unit templates. It
confirms a difference over two samples six seconds apart before saying anything --
a squad dying on one machine a moment before the other is a difference that
resolves itself, and reporting that would be the false alarm this whole class of
check exists to avoid.

Both paths verified against a live session by forcing the late path on the guest:

```
[ROSTER] Joined too late for a full comparison, but both machines have the same unit types -- no army mismatch
```

and, with one unit type removed from the incoming manifest:

```
[ROSTER] ========= ENEMY FORCES DIFFER (late check) =========
[ROSTER]   enemy.alien_stinger: host 0, you 2
```

It cannot catch "same units, different tiles" once the battle is moving. It can
catch "different armies", which is the one that matters.

---

## v3.12.0 - 23 Aug 2026 - missions rotate, and the deadlock the watchdog called "unhandled"

Reported: *"missions are restarting, not rotating. after mission complete, get back
to strategy layer, and select a new mission. and have the guest join that way,
things are desyncing and erroring because of that."* All correct, and the desync
turned out to be two separate faults with a third hiding behind them.

**Three distinct missions now run unattended in one session** -- idx=1, idx=3,
idx=5 -- with the guest joining every one and all six roster/terrain checks
matching.

### It was restarting because the only exit was a save reload

The harness left the results screen by reloading a save. That rebuilds the
strategy layer from disk, which puts the campaign back where the save was, so the
"next" mission was the same mission with the same seed. It also yanks the host
onto a fresh strategy layer while the guest is still finishing the old battle,
which is what the ROUND RUNAWAY spam was.

The route is now the one a player takes: **results close, strategy layer reopens,
a new mission is picked there, and the guest joins through the ordinary
mission-start handoff.** No save reload anywhere. The old `RestartForNextMission`
is deleted rather than left sitting there recommending itself.

Closing the results screen alone strands the game on an empty screen -- the old
note in the source was right about that symptom, but the conclusion drawn from it
(stop closing, reload instead) was wrong. The screen simply has to be asked for
afterwards, via `MissionSelectUIScreen.Open()`. Opening it by prefab name through
`UIManager.OpenScreen` also works, by way of a NullReferenceException inside the
game's `OnOpened`; a screen that opens through an exception is not something to
build a test loop on.

### The deadlock: two guards that contradicted each other

Seen live: **guest pinned on round 12 while the host reached round 19**, the
watchdog retrying 25 times across 21 minutes, every hard resync reporting
`0 position(s), 0 HP/AP, 0 combat-state` -- repairing nothing, because nothing was
broken. It ended with the watchdog declaring *"this is an unhandled deadlock
class, not drift"*.

One unit had nobody who could finish it. The guest may not move the partner's
units; the host had been seven rounds past it for a quarter of an hour and was
never sending that turn-end again.

The catch-up that exists to settle exactly this case was unreachable.
`AnyPlayerUnitOwesATurn` counted **every** unit in the player faction, the
partner's included, and returned before reaching the settle loop written
specifically for partner units. The two guards disagreed and the stricter one won.
It now only counts units the local player actually owns.

Result: 53 rounds in lockstep, gap never above one round, zero watchdog fires.

### The guest could not follow the host into the next mission

Two faults, one behind the other.

**A superseded load kept the old load's clock.** At a mission boundary two load
requests arrive back to back -- the post-battle strategy sync, then the next
battle's autosave. `_loadQueuedAt` was refreshed only when nothing was queued, so
the second load was judged against a clock that had already been running, tripped
the 25-second failsafe immediately, and the guest gave up entering the battle. A
new load is a new wait.

**Nobody dismissed the guest's own results screen.** When the host's mission ends
the guest follows it out with `Finish(Leave)` -- which opens the guest's *own*
results screen and leaves the tactical layer alive underneath it, exactly as it
does on the host. `UpdateLoadQueue` waits for that screen ("ExecLoad into it hangs
the game") and the guest does not drive campaign UI, so the wait never ended and
the next mission's load was never taken. The guest now closes it once the host has
demonstrably moved on, after a short grace period so a human still sees their
results.

### Harness: abandon a mission it cannot win

Auto-combat manoeuvres far better than it shoots -- one mission reached **round 54
with five kills and four enemies still standing**, both round-limit objectives
long failed. The turn cycle was flawless throughout, so nothing was broken; the
fight was simply not going to end, and one unwinnable map can eat an entire
unattended run. `Dev/SelfTestMaxRounds` (default 30) abandons it through the same
`Finish(Leave)` exit the guest already uses. 0 disables.

Rotation itself now happens on the operation directly after the transition, ahead
of any screen that reads it -- the previous attempt lived in the mission-select
step, which never ran, because the reloaded save reaches the prep screen inside
the harness's own settle delay.

### Known, still open

- **~20 ROUND RUNAWAY warnings per mission transition** on the guest. Transient,
  recovers on its own, host stays clean -- but the handoff is not silent yet.
- **Morale drift** between machines, cause still not established.
- **Coverage is still one alien operation.** Six missions now cycle within it;
  human and pirate factions and vehicles still have no local coverage.

---

## v3.11.0 - 23 Aug 2026 - the checks now run on every battle, and the harness finishes missions

v3.10.0's checks had a hole big enough to have missed the very report that
prompted them, and finding it took making the test harness play more than one
mission -- which turned out to be blocked by a chain of wrong conclusions.

### The checks only ever ran on the first battle of a session

Roster and terrain manifests were published from the guest's "ready" handshake.
That fires **once per session, when the partner joins** -- not once per battle. So
a pair playing a second mission got no check at all.

The players who reported the original bug were on **mission idx=2**. v3.10.0
would have said nothing to them.

Both checks now publish off their own tick, keyed on the TacticalManager: a new
one is a new battle. Confirmed across two consecutive missions in a single
session, each getting its own roster and terrain comparison.

### A manifest taken before the board finished spawning

The first per-battle run reported `Host generated 0 enemy position(s), this
machine generated 10` -- because the host published on the first frame of the
battle, with one unit on the board.

The guard had been "the payload is non-empty", which silently stopped meaning
anything the moment a battle id was prefixed to the payload: the string was never
empty again. Both sides now require the AI unit count to hold still for two
seconds before publishing or comparing.

### "MAPS DIFFER 34.5%" when the real problem was that nobody had joined

The first genuine catch reported a third of the map differing and two completely
disjoint enemy forces. Both statements were true and both were misleading: the
guest had never finished loading into the second battle, so a battle-2 host board
was being compared against a battle-1 guest board.

A player sent to investigate their terrain when their partner simply never joined
has been sent to the wrong place. The mission identity (index and seed) now
travels inside both manifests, and a mismatch reports **NOT IN THE SAME BATTLE**
with both mission ids instead of diffing two unrelated boards.

### The harness can finish a mission now

It never could. It pressed Next on the results screen, got nothing, and the
conclusion recorded in the source was that the button "requires the real
UIElements event pipeline, which only an actual mouse provides" -- so every run
reloaded a pinned save instead, and the campaign could never advance past the
first mission. Every test this project has ever run was one operation, one map,
one faction.

Three separate things were wrong, none of them the button:

1. **Auto-combat kept playing the finished battle.** The tactical layer stays
   alive under the results screen, so nothing noticed the mission had ended;
   squads went on moving and animating while the screen waited to be dismissed.
2. **The press came too early.** `BaseButton` carries `m_CreationFrameCount` -- the
   standard guard against a click falling through from the previous screen. The
   harness pressed on the first tick, inside that window, and burned its whole
   retry budget there.
3. **`OnClicked` was tried last.** The press/release pair looks more faithful to a
   real mouse and was tried first for that reason; it has never once moved a
   button in this UI. Next responded to `OnClicked` on the very next attempt.

With a two-second settle and one attempt a second, the results screen advances --
and it has two pages, mission summary then unit roster, which is why a single
successful press looked like nothing had happened.

Also fixed: `AllowLoad` treated a finished battle as a battle in progress, because
`TacticalManager` still exists while the results are on screen. Every reload the
harness attempted was refused with "Blocked host mid-battle load while connected".
That block is for genuinely mid-battle loads the guest could never follow; the
results screen is not one.

Result: two missions completed and chained unattended in a single session, each
with its own roster and terrain check.

### Two faults in the new checks themselves, found by running them

The first battle-2 catch reported twenty differing tiles, **all of them
"walkable"**, at exactly the tiles the enemies were standing on.
`IsValidMovementDestination()` is board state, not terrain -- a tile with a unit
on it is not a valid destination -- so the map check was reporting a roster
difference in a terrain costume. Walkability is out of the fingerprint; surface,
cover, line-of-sight, structures and features remain.

The same report listed guest squads with **x0 living members**: units already
killed. The manifest records where every unit *is*, so a host snapshot compared
against a guest snapshot taken later compares two different instants and calls
the difference a desync. Both sides now only snapshot **before the first
activation**, the one moment they can both describe without coordinating. If the
window is missed -- a guest that joins late -- the check says so and skips,
rather than inventing a difference:

```
[ROSTER] Could not compare before the fighting started -- skipping this battle's check
```

### Mission rotation

The harness now sets the operation's current mission directly after the reload,
ahead of any screen that reads it. The test operation turns out to offer **six**
missions, not one; runs now cycle them, and a single-mission operation says so
instead of silently replaying.

### Derived state was hiding worse than suppression

Following the suppression question from v3.10.0, the newly named drift lines
turned up morale, not suppression:

```
MORALE drift 'enemy.alien_01_small_spiderling': mine 51 -> host 0
MORALE drift 'enemy.alien_stinger': mine 0 -> host 20
```

On a run where roster and terrain both matched exactly. Morale drives panic and
rout. Cause still open.

### Known, still open

- **Morale drift** between machines, cause not established.
- **The guest can fail to load into a second battle** after the host reloads a
  save ("Sync load still waiting after 25s"). Seen once, recovered on later runs.
- **"ROUND RUNAWAY" during the second battle handoff** -- the guest logged it 39
  times while catching up to the host.s round. Pre-existing guard, doing its job,
  but the underlying handoff timing is not right yet.
- **Coverage is still one operation.** The harness now rotates the mission it
  picks, but a save whose operation offers a single mission has nothing to rotate
  to -- and it now says so rather than silently replaying. Human and pirate
  factions, and vehicles, still have no local coverage.

---

## v3.10.0 - 23 Aug 2026 - the map, the install, and what derived state was hiding

Follow-up to v3.9.0, from the same player report. Three more things that were
never checked, plus one that was checked so vaguely it could not be read.

### Map check (new, on by default)

The same player: *"the game update added and updated terrain and these new or
changed entities did not sync between our games either in the planning mission
screen or in battle."*

**The mod had never touched terrain.** Not one line. The guest relaunches the
mission from the host's mission seed and both machines are assumed to generate
identical ground -- the same bet that lost for the enemy roster, made for the
larger half of the board.

Terrain is not scenery. Cover decides whether a shot lands, line-of-sight decides
what each player can see, surface decides where a unit may walk. And the
divergence was invisible: the desync hash covers actor position, hitpoints and
player action points, so **a map made of different rock has always hashed as
identical**.

Now the host ships a per-tile fingerprint at battle start -- surface, cover mask,
line-of-sight blocking, structures, environment features, walkability -- and the
guest diffs it, reporting the count, the breakdown by category, and the exact
coordinates.

Verified by injecting five perturbed tiles into a live session:

```
[TERRAIN] 5 of 1764 tiles differ (0.3%).
[TERRAIN]   surface=5 cover=0 lineOfSight=5 structures=0 features=5 walkable=0
[TERRAIN]   (10,12): surface 2->1, line-of-sight, environment feature
```

A clean run reports `Map fingerprints MATCH (42x42)`.

### Installation report (new)

A stale install was suspected in the original report and could not be ruled out,
because nothing about the installation appeared in the log.

**BepInEx never rewrites a config value that already exists.** It adds newly
bound keys, but an existing key keeps its old value forever. So when a default
changes between versions, every existing player silently keeps the old behaviour
and their log looks identical to a clean install's. That is an excellent way to
ship a fix that does nothing.

Startup now prints one block: mod version, game build, which version wrote the
config, every setting that differs from the shipped default, the full path of the
loaded plugin, and any leftover MenaceCoop files sitting beside it.

```
[INSTALL] Config was written by MenaceCoop 3.7.1, this is 3.10.0.
[INSTALL] Existing settings are KEPT -- BepInEx never rewrites values that are already
[INSTALL] in the file, so any default changed since then is NOT in effect here.
[INSTALL] To adopt this version's defaults, delete: ...\com.menacecoop.core.cfg
[INSTALL] Leftover file from an older install (safe to delete): ...\MenaceCoop.dll.old
```

**It deletes nothing.** Removing files from somebody's game directory quietly is
not a thing a mod should do: a wrong guess about which file is stale destroys
another mod and there is no undo. Files are named with full paths so the player
removes the right one. (BepInEx already refuses to load two plugins sharing a
GUID, so a duplicate is a reporting problem, not a double-patch.)

Verified by planting a 3.7.1-stamped config and a leftover `.dll.old` on one
machine and confirming both were named.

### Derived state now says which unit and which field

A player asked why suppression behaved differently on two of their units, and the
logs could not answer it.

Suppression, morale and stance are **not in the state hash** -- it covers
position, hitpoints and player action points only. So a divergence in them is
invisible until a snapshot is sent for some unrelated reason, and even then it was
reported as `N combat-state value(s)`: a count, with no unit and no field. Position
drift has always logged a named `HP drift` line; these three never did.

They do now. The first run with the change immediately surfaced this, which no
previous log could have shown:

```
[DESYNC]   MORALE drift 'enemy.alien_01_small_spiderling': mine 51 -> host 0
[DESYNC]   MORALE drift 'enemy.alien_stinger': mine 0 -> host 20
```

Morale drives panic and rout, and it is drifting by up to 51 points between
machines **on a run where the enemy roster and the map both matched exactly**.
Suppression itself did not drift in that run. This is instrumentation, not a fix:
the underlying cause is still open, and it is now visible instead of hidden
behind a number.

### Known, still open

- Morale drift between machines (above). Cause not yet established.
- A player squad differed by one member mid-battle (`darby`: 8 living here, host
  sent 9) with matched rosters and matched terrain, so combat outcomes can still
  diverge on their own.
- Coverage is still narrow: testing runs one alien operation. Human/pirate
  factions and vehicles are exercised only by the reporting players, not here.

### Notes

- Built and tested against game **v0.7.14+20743**.
- New switches, all default on: `CheckTerrain`, and from v3.9.0
  `CheckEnemyRoster` and `PinMissionRng`.

---

## v3.9.0 - 23 Aug 2026 - the two players were fighting different armies

A player report against v3.8.4: *"a basic scavenger to the Client was a
flamethrower to the Host, resulting in a squad's death"*, plus enemies missing
entirely on one side and a mission ending with one death on the host and two on
the client. Three symptoms, one cause.

**The guest's enemies were not the host's enemies.** Not drifted -- different
units. From the two logs, at the same tiles in the same round:

| tile | host | client |
|---|---|---|
| (12,22) | `pirate_outcasts` x7 | `pirate_flamethrower_team` x3 |
| (28,18) | `pirate_flamethrower_team` x3 | `pirate_scavengers` x7 |

The host also had enemies at (10,25) and (7,25) that did not exist on the client,
and the client had two the host did not. Of six enemy squads, exactly **one** had
the same identity on both machines. Every "order never resolved" error in that
log is the host narrating a unit the client does not have.

**Why nothing caught it.** A battle cannot be saved in MENACE -- the save system
only captures the strategy layer -- so the guest cannot be handed the host's
board. It relaunches the mission locally from the host's mission seed and trusts
the game to generate the same force. Nothing ever checked that it did. The
roster healer that *looked* like it was checking matches squad members by
squaddie id and only corrects hitpoints; independently generated squads share no
ids, so it matched nothing and repaired nothing, logging the same three
`ROSTER DIFFERS` lines for the whole match while reporting `0 position(s),
0 HP/AP, 0 REFUSED`. It had been describing the problem accurately all along and
could never act on it.

### Enemy roster check (new, on by default)

At the first moment both boards exist, the host sends a manifest of every AI unit
(template, tile, member count) and the guest compares it. On a mismatch the log
names the exact tiles and units that disagree, the co-op panel says so, and a red
banner tells both players to abandon and relaunch the mission.

This **detects, it does not repair** -- rebuilding one machine's enemy force to
match the other's is not something the engine exposes. The point is that nobody
loses a squad to a unit that was never on their screen.

Verified by injecting a deliberate one-unit difference into a live session: the
guest reported the missing unit and its tile correctly, and a clean run
immediately after reported `Enemy manifests MATCH (10 unit(s))`.

### Strategy RNG pinned before launch (new, on by default)

The mission seed alone was never enough. The game draws from several independent
`PseudoRandom` streams -- `BaseMapGenerator.m_Random` for the map,
`TacticalManager.s_Random` for combat rolls (already synced), and
`Operation.m_Random` on the strategy layer. **Nothing synced the last one.**

The evidence it matters: on the diverged battle the two machines agreed on most
enemy *spawn positions* -- the map generated identically from the mission seed --
but disagreed on which squad stood at each. That is a shared map draw and an
unshared composition draw.

The streams drift because the two machines do different amounts of strategy-layer
work before launching: the guest opens mission prep twice (once mirroring the
host's planning screen, once for its own auto-launch) and loads the sync save up
to three times, where the host opens prep once. Both machines now re-seed the
operation RNG from the mission seed in the `LaunchMission` prefix, so whatever
each drew on the way there, they enter generation from the same point.

**This fix is unverified against the failure.** The local test pair generates
identical forces with the pin on *or* off, so it cannot reproduce the divergence
-- that reproduction only exists on the reporting players' machines. The roster
check above is what will confirm whether it worked.

### Notes

- Built and tested against game **v0.7.14+20743**. The report came from v0.7.11.
- Both new behaviours can be turned off: `CheckEnemyRoster`, `PinMissionRng`.

---

## v3.5.0 (dev) - 29 Jul 2026 - prevention over correction

**Three consecutive missions completed with every blocking metric at zero**, measured
not asserted. Per mission: both machines in lockstep every round, ~38 replicated
moves, both turn-end paths exercised, and:

| metric | v3.4.0 first run | v3.5.0 (runs 12-14) |
|---|---|---|
| Stalls / deadlocks | several classes | 0 |
| Sync pauses (stop-the-world) | 2 | 0 |
| Orders never resolved | 0 | 0 |
| Player position corrections | 25 | 0 |
| AI position corrections | 22 | 0 |
| Refused corrections | several | 0 |
| AI action-point corrections | 68 | 19 |

The goal this cycle was to stop RELYING on the desync fixer. Position corrections
went to zero: the healer now almost never touches the board.

### The authority model changed

"Host overrules everything" was wrong and caused desyncs of its own. Authority now
follows the ACTOR: **the machine that originated an action owns its outcome, and a
replay never overrules the original.** A unit stopped three tiles into a six-tile
move by overwatch is the owner's genuine result; the partner's re-enactment yields.

Host authority is retained exactly where a single decider is required: AI units,
round/faction sequencing, mission state, and anything on a unit that cannot be
identified. Hard restores and the FIX DESYNC button still override everything --
a last-resort hammer with exceptions is not a hammer.

### Deadlocks fixed (each a distinct class)

- **Revive filed units into the wrong faction.** `GetFaction(int)` takes an array
  POSITION; it was being passed a faction ID. A revived player unit landed in
  faction 2 and sat there forever, because nothing in the player phase finishes a
  unit that is no longer in the player faction. Root cause of the "stuck on faction
  2 waiting for player_squad.x" hangs on both host and guest.
- **Nothing rolled the round from the player faction.** The engine reported
  `roundDone=True finishedPct=1.00` with every faction empty and still would not
  advance; unsuppressing the AI pipeline did not help. Now escalates through the
  engine's own `CheckRound()` and, only while genuinely behind the host,
  `NextRound()`. Not `NextFaction` (law 1), not writing `m_Round` (law 2).
- **The round-roll retry budget locked itself out.** Keyed on round+faction, it was
  spent early in the round when there was nothing to roll, so when the round became
  rollable it had already given up. `roundDone` is now part of the key.
- **Unidentifiable stragglers held a faction forever.** Settled only when the host
  has left that faction and finishing it will not roll the round -- adopting host
  state, not inventing it.

### Desync sources removed at the source

- **Interrupted moves.** The mover now reports the tile it ACTUALLY stopped on and
  the partner adopts it. The previous landing check snapped to the REQUESTED
  destination, which would drag a legitimately interrupted unit forward -- it
  manufactured the desync it was meant to prevent.
- **Enemy fire during the player phase.** Player actions are held while an enemy
  shot resolves; AI orders are deliberately exempt (holding those deadlocks the
  cycle). Local hold and broadcast gate updated together, per law 7.
- **Game speed.** Only the click-to-fast-forward entry point was blocked before;
  `PathMover.SetTimeScale` is now clamped to 1x while connected (`LockGameSpeed`).
  Timing is not cosmetic -- it moves when a reaction shot resolves.
- **AI action points mid-phase.** The host runs the AI and the guest replays it, so
  mid-phase they are legitimately at different points in one sequence. Those
  "corrections" were pipeline lag, and pointless besides: the guest never makes AI
  decisions. Reconciled only once the player phase is active.
- Orders carry the sender's authoritative AP; landings carry final AP.

### Healer no longer escalates over non-events

- **Downed units.** A dying/stunned/contained unit declines every move, so the
  healer retried an impossible correction on every snapshot until the failures
  triggered a stop-the-world sync pause. "Cannot move yet" is no longer a failure.
- **Late damage packets.** An `ehp`/`edie` for an already-dead unit was treated as
  divergence and was the single biggest source of resync requests (6 of 8 in one
  mission). Dropped instead.
- **Swap-breaking parked in a loop** (MoveTo is async, law 8) and reported a
  deliberate two-step as a failure.

### UI

- Panel scrolls instead of growing off-screen, with a slim 6px scrollbar.
- Independent toggles for the green turn banner and orange warning banner. Hiding
  the orange one hides the NOTIFICATION only; corrections still run and still log.

### Known / unproven

- The revive faction fix is diagnosed and reasoned, but **no revive occurred in the
  three clean runs**, so it is not yet confirmed by observation.
- Steam P2P and real network latency remain **completely untested** -- everything
  here is two instances over TCP loopback.
- Menu/strategy-layer sync is deliberately out of scope this cycle.

---
## v3.4.0 (dev) - 29 Jul 2026 - the freeze, actually found

**A full mission now plays start to finish with both machines in lockstep.**
Verified unattended twice. Standing still: both sides to round 10, identical unit
counts every round. **With real movement: both sides to round 12 across 31
replicated unit moves (16 host, 15 guest) and ZERO player-unit position
corrections** -- player movement replicates exactly. Both runs: no unexplained
stalls, no unresolvable corrections, no unresolved orders, no failed landings.
The only drift left is transient AI action-point/position sampling during relayed
alien moves (7 corrections over 12 rounds), which the healer fixes.

Three real bugs, all found the same way -- by building a test harness first
instead of guessing:

- **Skip was never broadcast as a turn-end.** The local gate allowed
  `InExplicitEndTurn || InSkipTurn`; the broadcast gate tested `InExplicitEndTurn`
  alone. Any unit either player SKIPPED went done locally and stayed pending on
  the partner forever, so that side's faction never completed and both engines
  waited on each other. This is the recurring "ends turn -> alien turn -> freeze".
- **The host acted during hold-fire and those orders were dropped.**
  `ShouldBroadcastOrder` refused to send while the guest was still loading in, but
  `ActionBlockedByLock` did not refuse the ACTION -- so the host finished units the
  guest never heard about. `Actor.SkipTurn` had no local gate at all. Both fixed:
  an action we cannot tell our partner about no longer happens.
- **Nothing ended the players' phase.** Both engines sat with the player faction
  active, zero players left to act and ten aliens waiting. A unit finished via
  SkipTurn never runs the end-of-phase check that `TacticalState.EndTurn` performs.
  Now `TacticalManager.TryCheckRound` (the engine's own check -- NOT `NextFaction`)
  runs off a condition both machines evaluate identically, with a retry cap.

Also fixed:
- Turn-done application is idempotent, so a skip arriving as both a `skip` order
  and a `done` message no longer ping-pongs turn control between players.
- Mission end is relayed (`mend`): the guest stops waiting on AI units the host
  will never move again, instead of spinning until the post-mission sync.
- Position corrections break pure SWAP cycles by parking one unit on a free tile.
  Previously two units each standing on the other's tile stayed wrong all battle.
- The phase-end check stands down when the partner's mission is over.

New dev tooling (`Dev/SelfTestTrace`, `Dev/SelfTestAutoPlay`, both default off):
- `SelfTest.cs` -- one canonical state line per machine (`r= fac= player= ctrl=
  mine= theirs= ai=`), diffable host vs guest to the exact divergence frame, plus
  a 20s stall dump naming every unit that still owes a turn and the engine's own
  faction counters. Autopilot ends one owned unit's turn per second.
- `UiAutopilot.cs` -- drives the host title -> load save -> mission -> battle so a
  mission runs unattended and repeatably.

Build/test safety: the build scripts no longer close a running game. `Build-Release.ps1`
skips the deploy when the DLL is locked; `Launch-CoopTest.ps1` refuses to start
unless `-Force` is passed.

Known limitation: the mission-result screen's Next button responds only to a real
mouse click, so the autopilot run ends there -- a human clicks through. Remaining
noise is transient AI action-point drift during relayed alien moves, which the
healer corrects; no position or HP divergence persists.

---
## 3.2.1 â€” 2026-07-25 (dev release)
- **Fixed the guest running a whole round ahead** (host on round 1, guest on
  round 2, every unit's action points reset on one side and spent on the other).
  The 3.1.0 rule â€” follow the host by settling an AI faction whenever the host
  leaves one â€” overshot: it emptied that faction's remaining activations, so once
  the players finished, the engine rolled straight into the next round while the
  host was still working through its own alien activations.
  Settling now happens **only while a human still has a unit to move**. That is
  the only reason it exists â€” to hand the activation back to the players â€” and
  when nobody has a move left it buys nothing while costing the round. In that
  case the guest simply waits, which is free, and the round then rolls on the
  host first, as it should.

## 3.2.0 â€” 2026-07-25 (dev release) â€” TURN CONTROL PER ACTIVATION
- **Co-op turn handling now matches how the game actually works.** The engine
  does not run "all player units, then all enemies" â€” it alternates ONE player
  activation with ONE AI activation. The 3.0.0 phase model fought that by making
  each player finish EVERY unit before the other could play, a concept the
  engine has no notion of, and that mismatch is what produced the mutual waits
  and freezes.
  Control is now granted **per activation**: while the player faction is active
  exactly one human may issue orders, and when their unit finishes, control
  passes to the other player if they still have someone to move. The engine's
  own cycle is never touched â€” it hands out activations exactly as before, and
  we only decide who is holding the controller.
- The host is the authority on who holds control and tells the guest, so the two
  machines cannot disagree. Who acts first alternates each round.
- A player with nothing left to move is skipped automatically rather than
  holding the activation.
- This removes the phase-done bookkeeping, phase-end settling and auto-pass
  entirely â€” a large amount of state that existed only to prop up the mismatched
  model, and which generated bugs of its own.
- Banner now reads "YOUR TURN â€” PLAYER 1" or "PLAYER 2 IS ACTING â€” PLEASE WAIT".

## 3.1.0 â€” 2026-07-25 (dev release) â€” THE RECURRING FREEZE, STRUCTURALLY
- **Root cause of the freeze that kept coming back in different disguises.**
  The host's log shows its real cycle: enter an AI faction, run exactly ONE
  unit, leave again â€” alternating activations. The guest cannot mirror that on
  its own: with AI processing suppressed, its engine only leaves an AI faction
  once EVERY unit of that faction is done, and nothing on the guest ever makes
  that happen mid-round. So the guest sat on the alien faction while the host
  sat in the player phase waiting for the guest's squads to move. A mutual wait
  â€” which is why it always looked like "guest ends turn, aliens start, freeze".
  Every previous fix treated a symptom of this.
  **The host's faction advance is now the guest's cue to advance too**: when the
  host reports leaving an AI faction, the guest settles that faction so its
  engine follows the same cycle. Safe because on the guest AI units do not need
  turn-flags in order to ACT â€” their moves and attacks arrive as relayed orders
  and are applied regardless of turn state; the flags only gate the guest's own
  faction cycling. Rolling the round ahead of the host is still refused, which
  is what previously made enemies appear to skip turns.
- **Stalls are now diagnostic rather than corrective.** If the guest is still
  waiting after the host's cue, it names the exact units holding the cycle â€”
  template, faction and coop id â€” instead of repeating "stalled" forever.

## 3.0.7 â€” 2026-07-25 (dev release)
- **Fixed the freeze after the last unit of a phase.** The logs showed both
  machines waiting on each other: the guest finished its last squad, its engine
  left the player phase and sat waiting for enemy relays, while the HOST stayed
  in the player phase because it still believed one of the guest's squads had a
  turn owing â€” so its AI never ran, and the enemy relays the guest was waiting
  for never came.
  Cause: only turn-ends made through an explicit End Turn are broadcast, so any
  done-flag set another way never reached the partner. Ending a phase now
  settles that player's units on BOTH machines â€” locally when you end, and on
  your partner's machine when your "phase ended" message arrives â€” so the two
  engines always agree about who still owes a turn.
- **Fixed the recurring one-value drift, at its source.** The new diagnostics
  named it immediately: `AP drift 'player_squad.darby': mine 0 -> host 28`.
  Ending a unit's turn zeroes its action points on the machine that does it,
  but the partner only received the flag â€” so the two sides disagreed about
  that unit's AP from then on, and the resync dutifully "corrected" the right
  value back to the stale one. Turn-end messages now carry the unit's action
  points, and the receiver applies them.

## 3.0.6 â€” 2026-07-25 (dev release)
- **Fixed the round-1 freeze â€” a livelock introduced in 3.0.4.** The logs show
  it plainly: the guest reported a stalled faction, asked for a resync, and
  every single restore came back "0 positions, 0 HP/AP, 0 state, 0 refused" â€”
  the boards already matched perfectly. The stall was never a state problem.
  Repeated requests escalated into a SYNC PAUSE, and a sync pause deliberately
  freezes AI processing on BOTH machines â€” including the host's AI, which is the
  only thing that could have cleared the stall. So it looped: stall, pause,
  host AI frozen, still stalled.
  A stall can no longer escalate to a pause, and it now re-checks every couple
  of seconds instead of every frame.
- **An ordinary sync can now clear a stalled faction.** Turn-flags used to be
  applied only by a hard resync, which is why those restores corrected nothing.
  A routine snapshot may now settle AI units the host has already finished â€”
  marking done only, never re-opening â€” and only when the snapshot is from the
  round being played, so a stale one cannot settle units whose flags the engine
  has since reset.

## 3.0.5 â€” 2026-07-25 (dev release)
- **Stopped crying wolf mid-turn.** Board hashes are compared at every faction
  switch â€” which, with alternating activations, happens several times per round,
  not once per turn â€” and a single hitpoint of drift tripped the same
  "DESYNC DETECTED" banner as a genuinely broken board. Detection is now quiet:
  it logs, requests the correction, and lets the RESULT decide whether to speak.
  A trivial fix stays silent; a real one (units moved, a revival, or five or
  more values) still raises the banner as before.
- **Drift is now named instead of counted.** A recurring "1 HP/AP corrected"
  said nothing about its source, so corrections now log the unit, the field and
  both values â€” e.g. `AP drift 'player_squad.pike' at (11,24): mine 12 -> host
  10`. That turns the recurring small divergence into something we can trace to
  a mechanism rather than guess at.

## 3.0.4 â€” 2026-07-25 (dev release)
- **Fixed the round rolling over with the enemies never taking their turn.**
  The logs caught it exactly: the guest logged "Faction stalled â€” completing
  stragglers locally", then reached round 2 while the host was still in round 1.
  The stall failsafe was marking the ENEMY faction done on the guest, which robs
  those enemies of their activation and can roll the round on one machine alone.
  **The guest no longer fabricates AI turn-flags at all.** A suspected stall now
  asks the host for authoritative state instead of inventing it â€” the host's
  relays are the only legitimate source of AI turn-state, which is the same
  principle that fixed the 2.9.2 restore deadlock.
- **Resyncs can now clear a genuine stall.** A hard resync applies the host's
  own done-flags to AI units, but only in the safe direction: an enemy may be
  marked DONE (which is what unsticks a stalled faction) and never re-opened
  (which is what caused the earlier deadlock). Player units still follow the
  host exactly in both directions.
- Removed the blanket "settle every AI unit after a restore" net added in 2.9.2
  â€” it could finish enemies that still had a turn coming, producing this same
  symptom. Convergence now comes from the host's state rather than a local
  guess.

## 3.0.3 â€” 2026-07-25 (dev release)
- **Fixed units losing their turn ("two units never went, it said enemies go
  next").** The game has TWO end-turn commands: one finishes the SELECTED UNIT
  (the "F END TURN" you press after moving each squad) and one finishes your
  whole side. The phase handover was hooked to the per-unit one â€” so ending a
  single squad's turn handed the entire phase to your partner, and any of your
  units that had not acted yet never got the chance. Handover now happens only
  on "end turn all", which is the command that actually means "I am finished".
  Ending units one at a time still works: once every unit of yours is done, the
  phase passes on its own.
- **The auto-pass is now conservative**, because getting it wrong costs a player
  their whole turn. It waits four seconds, and it only passes when it can
  positively identify units of its own AND all of them are finished â€” if it
  recognises none of your units it assumes there is work left rather than
  guessing.
- **A phase ending with unmoved units now logs a warning naming them**, so a
  lost turn leaves evidence instead of being absorbed silently.

## 3.0.2 â€” 2026-07-25 (dev release)
- **Fixed phases resetting mid-round** ("PLAYER 1'S TURN â€” PLEASE WAIT" shown to
  Player 1, while the other machine said both players were done). The engine
  uses alternating activations, so its "is it the player's turn" flag flickers
  off and back on several times within ONE round â€” and the phase logic treated
  every flicker as a brand new player phase. That wiped both players' done-flags
  and handed the lead back to Player 1 mid-round, so the two machines ended up
  with contradictory pictures and the turn stalled.
  Phase bookkeeping is now keyed to the ROUND, which is the thing that actually
  changes once per cycle; the activation flag now only decides whether order
  gating is currently in force. Repeat End Turn presses can no longer pass the
  phase twice either.

## 3.0.1 â€” 2026-07-25 (dev release)
- **Half of all units silently had no identity â€” negative id hashes.** Unit ids
  are derived from a hash, and hash arithmetic overflows into NEGATIVE numbers
  roughly half the time. But every identity check in the mod reads a negative
  value as "no id": lookups return -1 for missing, order resolution only trusts
  ids >= 0, and snapshots count negatives as unidentified. So any unit whose
  hash landed negative had its orders, damage and DEATH packets fall back to
  tile addressing â€” which misses the moment anything moves.
  That is the mechanism behind "an enemy dies for the host but is still alive
  for the client", and behind the handful of units that kept reappearing in the
  logs as unidentified despite three previous fixes aimed at them. Ids are now
  forced positive everywhere they are derived (base, collision tie-break, and
  spawn capture).

## 3.0.0 â€” 2026-07-25 (dev release) â€” SEQUENTIAL PLAYER PHASES
- **One player acts at a time.** The player turn is now split in two: one player
  plays all of their units and presses End Turn, control passes to the other,
  and only when both have finished does the AI move. Who leads alternates by
  round, so neither player always goes first.
  This addresses the root generator behind most desyncs rather than another
  symptom: MENACE assumes a SINGLE controller for the player faction, so two
  players acting simultaneously made the two machines see the same actions in
  different orders â€” and order is what decides reaction fire, contested tiles
  and AP accounting.
- **Whose-turn banner across the top of the screen** â€” green "YOUR TURN â€”
  PLAYER 1" when it is yours, grey "PLAYER 2'S TURN â€” PLEASE WAIT" when it is
  not. Host is Player 1, guest is Player 2, fixed so both screens agree.
- Outside your phase you can watch, move the camera and use the co-op panel, but
  not issue orders. Both the input gate and the broadcast gate enforce it, so a
  blocked action can never leak to your partner as a ghost order.
- A player whose units are all finished passes automatically after a couple of
  seconds, so an empty phase never holds the round.
- The engine is never told about any of this â€” it still sees one player phase.
  We only decide who may issue orders into it, which keeps well clear of the
  turn-cycle internals that caused the freezes in the 1.4.2x line.
- Config: `SequentialPlayerPhases` (Coop) to restore free-for-all.

## 2.10.0 â€” 2026-07-25 (dev release)
- **The squad-takeover bug is found and fixed â€” and it was causing the freezes.**
  The detector added in 2.9.3 caught it immediately: the guest was issuing
  turn-ends for units it does not own (`player_squad.tech`, `player_squad.carda`),
  and a round mismatch followed ten seconds later. Cause: the engine's end-turn
  finishes EVERY unit of the player faction, so one player pressing End Turn was
  marking the other player's squads done â€” locally, and by broadcast on their
  machine too. Their units went inert, their phase completed early, and the turn
  cycles drifted apart into the deadlock.
  **Ending your turn now ends only YOUR units**, enforced in both directions:
  the local end is refused for a partner-owned unit ("Your End Turn only ends
  YOUR units"), and a turn-end for a partner-owned unit is never broadcast. The
  round advances when both players have finished, which is how ownership was
  meant to work from the start.

## 2.9.3 â€” 2026-07-25 (dev release)
- **Fixed the round drift and deadlock â€” the stall failsafe was causing it.**
  The logs showed "Faction stalled ... completing stragglers locally" firing
  immediately before each drift ("host 1, guest 2", then "host 2, guest 3").
  Completing a faction advances the cycle, and when every OTHER faction is
  already finished that advance IS the round rollover â€” so the failsafe meant to
  help the guest catch up was pushing it a round ahead instead. It now checks
  whether clearing the stall would roll the round (using the engine's own
  "actors left to act" count) and refuses unless the host is genuinely a round
  ahead.
- **Added a takeover detector.** The squad-takeover bug left no trace, because
  both machines log the same order line â€” the replay runs through the same hook.
  An order ORIGINATED by a side for a player unit it does not own now logs
  `[OWN] TAKEOVER:` with the unit and the offending side, so the next occurrence
  is provable rather than suspected.
- Noted for investigation: enemies still occasionally send orders with no stable
  id (`enemy.alien_stinger`, `enemy.alien_acid_spitter_bombardier`), which is the
  likely root of the remaining state mismatches â€” the detector is now reporting
  those mismatches correctly, which it never used to.

## 2.9.2 â€” 2026-07-25 (dev release)
- **Fixed the freeze after restoring: enemies were being rewound into
  activations that never come back.** A restore was rewinding AI units' turn
  flags to "not done" â€” but the host's AI already ran those activations this
  round and only relays a done flag when a unit actually acts. Nothing would
  ever move them again, and the guest's faction pump waits for every unit of a
  faction to be done, so the turn order stopped dead. That is the deadlock.
  Three changes: turn-done is now restored for PLAYER units only (their turn
  ends are explicit commands that replicate normally); a revived enemy comes
  back already settled for the current round and acts again next round, when the
  engine resets the flags itself; and after any restore, any AI unit of the
  active faction still reading "not done" is settled outright, so no restore can
  strand the turn order regardless of what moved.

## 2.9.1 â€” 2026-07-25 (dev release)
- **Round 1 auto-save timing.** Round 1 was already covered by the 2.9.0 logic
  (the round tracker starts unset, so the first battle tick counts as a new
  round) â€” but that capture could land while the guest was still loading in and
  the board still initialising. It now waits for the battle-entry hold to
  release, so round 1's save is taken with both players actually in the battle:
  the state a restore would have to put them back into.

## 2.9.0 â€” 2026-07-25 (dev release)
- **Automatic battle save at the start of every round.** The host now captures
  the board as each round begins, once the board has settled, and discards the
  previous round's save at the same moment â€” it could never be restored anyway.
  The result is that there is always exactly one save, it always belongs to the
  round you are currently playing, and RESTORE BATTLE STATE is therefore always
  valid instead of sometimes refusing. The button reads "round N start" so you
  can see what you would be rewinding to.
- Manual SAVE BATTLE STATE still takes precedence within a round, for the case
  where you want a point mid-round rather than at its start; it is cleared when
  the round turns over, for the same reason.

## 2.8.3 â€” 2026-07-25 (dev release)
- **Cross-round restores are now refused, with an explanation.** Restoring a
  round 1 save while playing round 2 broke instantly, and it always would: a
  snapshot holds UNIT state only. The engine's round and turn-order bookkeeping
  isn't in it and can't be rewound â€” the round counter is load-bearing and
  writing it crashes the turn-order panel (learned in 1.4.19). So a restore was
  handing round-1 action points and done-flags to a turn order that had already
  moved on, which is exactly the "turns and AP not being reset" symptom.
  Restores within the same round are sound â€” which is why same-turn restores
  worked perfectly â€” and cross-round ones now say so instead of breaking.
- **Fixed the false "REFUSED" reports from 2.8.2.** MoveTo is asynchronous: it
  ACCEPTS a move and the unit's tile updates when the movement completes, never
  within the call. The 2.8.2 check ran immediately and so declared working
  teleports refused (`MoveTo=True, ended at <original tile>` in your log â€”
  accepted, just not finished). Verification now runs a second and a half later
  and reports only units that genuinely never arrived.
- **Fixed the five permanently-unidentified enemies.** Id assignment had no
  per-actor guard, so a single actor that threw â€” a transient or dying unit â€”
  aborted the whole pass and left every unit AFTER it in the enumeration with no
  id at all. Each actor is now guarded individually, so one bad unit costs one
  id instead of all of them.

## 2.8.2 â€” 2026-07-25 (dev release)
- **Found why a restore could report success and change nothing on screen.**
  The teleport ignored MoveTo's result: a refused move â€” which happens when the
  destination is contested or has no valid path â€” was counted as a success
  anyway. So "7 positions restored" could mean "7 attempts, some of which the
  engine declined", which matches a restore that looked right in the log and
  wrong on the board. Restores now use the strongest movement flags available
  (force, teleport, move-through-actors, skip-pathfinding), CHECK the result,
  and verify the unit actually landed on the target tile. Refusals are counted,
  logged per unit with where the unit ended up instead, and reported on screen
  as "N unit(s) could NOT be moved â€” the board is only partly restored".
- **Units with no stable id are now given one.** Two identical units sharing a
  tile at capture time collided, and the loser was left with no id at all â€”
  which pushed it onto the restore's fuzzy type-and-position matching, where the
  wrong unit can be placed in the right spot. There is a deterministic
  tie-break available: enumeration runs through the engine's own per-faction
  actor lists, which are built identically on both machines, so the Nth
  duplicate is the same unit on both sides and that ordinal is mixed into the
  id. Any unit that still ends up without an id is now named in the log along
  with its faction and tile.

## 2.8.1 â€” 2026-07-25 (dev release)
- **Stopped the constant resync banner.** Routine background healing of a stray
  hitpoint was raising a banner every few seconds, which trains you to ignore
  the banner that actually matters. Background corrections are now logged
  silently; the banner appears only for an explicit restore, a unit actually
  moving, a revival, or a correction of five or more values.
- **Save/restore now announces itself.** Restoring shows "RESTORING saved 1:1 â€”
  rewinding both boards..." immediately, then reports what changed. Previously a
  restore that legitimately changed little looked like a button that did
  nothing â€” even though it was working (your last session restored 7 positions,
  5 HP/AP and 4 state values on both machines).
- **Per-unit restore detail in the log**, e.g. `moved 'player_squad.pike'
  (11,24) -> (9,22)`, so a restore can be verified afterwards rather than
  trusted from a count.
- **Snapshots now report units with no stable id**, which are the ones that fall
  back to type+position matching. Your last restore matched 3 units that way â€”
  worth chasing, since fuzzy matching is where a restore can put the wrong unit
  in the right place.

## 2.8.0 â€” 2026-07-25 (dev release)
- **Mid-battle save and restore â€” the thing the base game cannot do.** New
  host-only **SAVE BATTLE STATE** captures the live board (positions, HP, AP,
  ammo, turn state, morale, stance, containment, who is dead) and writes it
  beside your saves, and **RESTORE BATTLE STATE** rewinds both machines to it,
  bringing back anyone who has died since. Save before a risky push, restore
  when it goes wrong. A restore prefers YOUR save over the automatic checkpoint,
  so a background capture can never quietly replace the moment you chose.
- **Panel simplified.** The two similar-sounding sync buttons are gone. There is
  now a single BATTLE section, shown only while in a battle and connected, with
  buttons named for what they do: **FIX DESYNC** (host pushes its board, guest
  asks the host), plus the host's SAVE and RESTORE. Everything outside battle is
  hidden, so the panel stays short.
- Why this is a mod feature rather than the game's own save button: a `.save`
  file provably contains no tactical state â€” searching one finds 31 references
  to squad leaders and 10 to missions, but zero tiles, zero actors, zero
  hitpoints and zero action points. Enabling the game's save mid-mission would
  write a file with no board in it; reading the live board is the only way.

## 2.7.1 â€” 2026-07-25 (dev release)
- **Fixed desync DETECTION, which had been broken for a long time.** Your logs
  showed the smoking gun: one checkpoint captured in an entire session, and not
  a single hash mismatch reported despite obvious desync â€” so "restore last good
  state" always rewound to the battle's opening moment, and the detector behind
  every auto-resync was effectively blind.
  Two flaws, both in the old compare-by-key scheme: (1) the key was
  "round:faction", which RECURS many times per round under alternating
  activations, so the two sides' stored hashes routinely came from different
  moments; (2) both sides' hashes shared one eviction queue holding an entry per
  store, so when an older duplicate of a recurring key aged out it deleted the
  freshly written pair. Comparisons therefore almost never happened.
  Detection is now live and directional: **the host publishes its hash, and the
  guest verifies it against its own board at that moment** â€” only when both are
  at the same round and faction, nothing is animating, and no order is in
  flight. A mismatch reports and resyncs immediately; a match is acknowledged
  back to the host, which is now what triggers a checkpoint. Checkpoints should
  accumulate through a battle instead of freezing at turn one.

## 2.7.0 â€” 2026-07-25 (dev release)
- **Checkpoint restore is now a true rewind â€” including bringing units back.**
  "RESTORE LAST GOOD STATE" rewinds BOTH boards to the last verified-good
  checkpoint, and a unit that has died since is restored to life. The game has
  no revive API, but the state that *makes* a unit dead is all reachable: the
  actor's dying flag, each element's destroyed flag and hitpoints, and â€” easily
  missed â€” its membership in the faction's actor list, which death removes. Skip
  that last part and the unit stands there alive but unable to act and invisible
  to every later snapshot.
- **The host rewinds itself too.** A restore that only moved the guest would be
  "drag the guest to the host's current reality", which is exactly what a rewind
  must not be. The host now applies the checkpoint to its own board as well.
- **Resurrection happens only on an explicit restore**, never from routine
  background snapshots â€” otherwise a snapshot in flight at the moment of a kill
  could undo a legitimate death.
- The result banner reports units brought back alongside positions, HP/AP and
  state values.
- Honest status: this is the least-proven feature in the mod. Every step is
  individually guarded and failures are reported rather than silent, but revival
  side effects (corpse visuals, ragdolls, morale reactions to the original
  death) are unverified until tested.

## 2.6.0 â€” 2026-07-25 (dev release)
- **Verified-good checkpoints.** Both sides already hash the board at every
  faction switch; when those hashes MATCH the boards are provably identical,
  which is the only moment a snapshot is certainly worth keeping. The host now
  captures a checkpoint at each such moment and mirrors it to disk
  (`coop_battle_checkpoint.txt` beside the saves), so a battle state survives a
  crash or a quit â€” something the base game cannot do at all.
- **Resync controls are now host-driven.** The host's button pushes its
  authoritative state and forces it on the guest; the guest's button only ASKS
  the host to do so. That matches reality: the guest usually notices a desync
  first, but only the host can resolve it.
- **New host-only "RESTORE LAST GOOD STATE"**, which pushes the last
  verified-good checkpoint instead of the current board, labelled with the turn
  it came from.

## 2.5.1 â€” 2026-07-25 (dev release)
- **Auto-end-turn is now switched off at the source while connected.** The game
  has its own settings for it â€” "Auto end turns" and "End turn on selecting
  unit" â€” and co-op now forces both off for the duration of a session, restoring
  your own values when you disconnect. This is strictly better than the 1.4.27
  approach of intercepting the auto-end after the fact: the engine never STARTS
  an engine-timed turn end, so there is nothing to race and no reliance on
  "did the player click just now?" timing. The interception stays in place as a
  backstop. Controlled by the existing `ManualTurnEnd` config.

## 2.5.0 â€” 2026-07-25 (dev release)
- **The desync fixer now corrects what you can actually see.** It previously
  carried only position, HP, AP and death â€” so a board could be "fixed"
  successfully while still differing in whose turn was finished, who was pinned,
  who was panicking and who was crouched, which is why a resync could report
  success and visibly change nothing. Snapshots now also carry **turn-done,
  suppression, morale and stance**, and the resync banner reports how many state
  values it corrected alongside positions and HP/AP.
- **Turn-done is applied only by a HARD resync** (the FORCE RESYNC button and
  the auto-escalation behind it). It decides who may still act, so applying it
  from a routine background snapshot could silently end a unit's turn if that
  snapshot was built moments before the unit moved. The explicit "make my board
  match the host" path is the right place to override it.
- Note on the save-based reset idea: MENACE saves contain only the strategy
  layer â€” no tactical state â€” which is why mid-mission saving is unavailable in
  the base game and why the guest joins battles via the battle-START autosave.
  A save round-trip therefore cannot restore a mid-battle board; it could only
  restart the mission from its opening.

## 2.4.2 â€” 2026-07-25 (dev release)
- **Boarding and disembarking vehicles can no longer be swallowed.** Vehicles
  use the same containment system as buildings (the engine's container types are
  Building, Vehicle, Forest, Tower), and the board/exit decision arrives as a
  movement step carrying Enter or Leave â€” to the SAME destination tile the walk
  just used. The multi-tile suppression added in 1.4.5 (which relays only the
  first step of a move to stop position cascades) would therefore swallow the
  boarding step itself, leaving the partner's copy standing outside the vehicle.
  A step carrying Enter or Leave is now never treated as a per-tile
  continuation, while ordinary walking stays suppressed exactly as before.

## 2.4.1 â€” 2026-07-25 (dev release)
- **Fixed the multi-faction breakdown â€” civilians had no identity at all.** The
  logs named it precisely: every failure began with `Sending 'move' with NO
  identity -- unit 'civilian.worker', faction 3`. Actor enumeration was built
  from the actors-root object and the spawn registry, and civilians are parented
  outside both, so they never received a stable id. Every order, damage packet
  and done-flag for a civilian therefore fell back to tile addressing, missed as
  soon as anything moved, and cascaded into repeated sync pauses. Enumeration
  now comes primarily from **each faction's own actor list** (the engine's own
  bookkeeping), which covers every unit regardless of how the scene parents it.
- **Fixed the guest racing ahead in rounds** (host on 2, guest on 4). The stall
  failsafe added in 2.3.3 was firing constantly â€” because those identity-less
  civilians could never be marked done â€” and each firing advanced a faction, so
  the guest burned whole rounds. It now refuses to fire when the guest is
  already level with or ahead of the host's round, so it can only ever help the
  guest catch up, never overshoot.
- **Fixed a player's end-turn settling OTHER factions' units on the partner.**
  Ending your turn also settles civilians and allies in the engine, and those
  were being broadcast as your own turn-ends â€” marking host-authoritative units
  done on the other machine. Only units of your own active faction are relayed
  now.

## 2.4.0 â€” 2026-07-25 (dev release)
- **Units entering buildings now replicate.** Building entry is not part of a
  move: the game marks the actor with the container it will enter and the
  containment happens when the movement finishes. We were replaying only the
  move, so a unit that walked into a building on one machine stood on the tile
  outside on the other â€” and the desync fixer could never repair it, because a
  contained unit has no tile to teleport to. Move orders now carry the
  container's tile (buildings are static, so tile-addressing them is stable
  unlike units), and the receiver marks the actor before replaying the move, so
  both copies end up inside. Leaving a building clears the mark, and if the
  container can't be resolved the unit stops outside with a warning rather than
  desyncing silently.
- Applies to both plain moves and move-with-skill (the AI's entry path).
- Prepares for defence missions, where building combat is central.

## 2.3.3 â€” 2026-07-25 (dev release)
- **Fixed civilians freezing the turn cycle in multi-faction missions.** The
  host relays each AI unit's done-flag as its activation ends, and the guest's
  faction pump waits for EVERY unit of a faction to be done before letting the
  engine advance. But the relay skipped any unit with no tile â€” and in a rescue
  mission the settlers are exactly that (garrisoned inside structures), so their
  done-flags never arrived and the civilian faction stalled forever. Tile-less
  units now relay their done-flag like everyone else, resolved by unit identity
  instead of position.
- **Added a stall failsafe so one lost flag can never hang a round again.** If
  the host has already moved past the faction the guest is sitting on and a unit
  here still reads "not done" for 3 seconds, the stragglers are completed
  locally and the pump advances normally. This is a data fix, never a
  NextFaction call, so it stays clear of the freeze class retired in 1.5.2 â€” and
  it is safe by construction, since the host has already finished those units.

## 2.3.2 â€” 2026-07-25 (dev release)
- **No more false "only the host can change the campaign" warnings.** The
  banner was firing during ordinary screen transitions (main menu â†’ planning,
  planning â†’ combat deployment), because the engine legitimately writes
  campaign state while building those screens and the guest's data gates refuse
  it â€” the same silent behaviour every build since 2.1.0 has had, only now it
  was shouting about it. The warning is now raised only when the player
  actually clicked something; engine-driven blocks stay silent in the log where
  they belong.

## 2.3.1 â€” 2026-07-25 (dev release)
- **Fixed the guest never joining after the host loaded in.** The transaction
  gates added in 2.2.1 (ship upgrades, hiring, perks) were blocking the GUEST
  unconditionally â€” but the engine uses those same calls to REBUILD state:
  deserialising the host's save re-hires the roster and re-installs O.C.I.
  upgrades. Blocking those left the guest with a half-built campaign that never
  finished coming in. Those gates now fire only for a genuine user click
  (recent-input detection, the same mechanism behind manual turn ends), so
  engine-driven load and init paths pass untouched while a guest clicking "buy"
  is still refused.
- **Fixed mirrored screens flickering open/closed.** The market and hiring are
  opened *from inside* the armory, so both sit in the open-screens list at once;
  the host was reporting whichever was registered first rather than the one on
  top, which could alternate between polls and spray the guest with open/close
  pairs. The topmost screen is now reported.

## 2.3.0 â€” 2026-07-25 (dev release)
- **O.C.I. upgrades now relay live.** Installing or removing a ship upgrade on
  the host appears on the guest's O.C.I. screen immediately â€” slots, ship stats
  and assets all repaint â€” instead of waiting for the next sync point. Upgrades
  are identified by asset name, resolved through a cache built from every slot
  template's tech tree (the same approach that makes item sync work).
- **Transactions relay atomically.** A ship upgrade carries its own OCI cost, so
  the relay sends the transaction and SUPPRESSES any currency message generated
  inside it; the guest's own execution of the same call reproduces the identical
  components spent and slot filled. That holds whether the game deducts inside
  the call or around it, and it means the guest can never end up with an upgrade
  it didn't pay for (or a payment without the upgrade).

## 2.2.1 â€” 2026-07-25 (dev release)
- **Blocked guest actions now say so.** Any menu action the guest isn't allowed
  to take â€” clicking inside a screen the host is driving, opening a host-only
  screen, buying, hiring, installing an O.C.I. upgrade â€” raises the orange
  banner at the top of the screen explaining why ("CLICKING DISABLED â€” you are
  viewing the host's black market screen", "BLOCKED â€” only the host can install
  O.C.I. upgrades"). Throttled, so a frustrated click storm produces one clear
  message rather than twenty. Combat is untouched.
- **Closed a real desync hole the browsing question exposed.** O.C.I. upgrades,
  hiring, and perk removal were previously only *indirectly* blocked, via their
  currency change â€” which meant a refusal could leave the effect applied
  without the payment, i.e. a divergence. They are now gated at their ATOMIC
  entry points (`TryEquipUpgrade` even carries the OCI cost as a parameter), so
  cost and effect are refused together and no half-transaction is possible.
  This hole pre-dated the 2.2.0 mirroring â€” the guest could reach those screens
  freely in every earlier build.

## 2.2.0 â€” 2026-07-25 (dev release)
- **Squads and O.C.I. now mirror too.** The live strategy layer covers all four
  armory screens: Black Market, Hiring, **Squads** (ArmoryUIScreen) and
  **O.C.I.** (ShipUpgradesUIScreen). When the host opens one, the guest is
  brought into the same screen and back out again with it, exactly like the
  market.
- **â€¦without trapping the guest again.** Squads and O.C.I. are inspection
  screens, so the guest may still open them *for itself* whenever the host
  isn't in a mirrored screen, and those self-opened screens stay fully
  clickable. The input shield now engages only while genuinely mirroring the
  host (host in a mirrored screen AND guest showing it), rather than whenever
  such a screen happens to be open. Market and hiring remain host-only â€” they
  exist to spend money.
- **Intro video skipped.** The startup splash/logo goes straight to the title
  screen, using the game's own "continue" path. Config: `SkipIntro` (QoL
  section) to restore it. Separate patch class, so a future game update can
  only cost this convenience, never co-op.
- Note: the mirrored Squads / O.C.I. views follow the host's *screen*, not yet
  the host's selected squad or slot â€” selection mirroring is the next
  increment.

## 2.1.3 â€” 2026-07-18 (dev release)
- **Guest can browse Squads and O.C.I. again, and syncing is fast.** The logs
  showed the real culprit: the armory container fires a "closed" event on every
  tab rotation, so each menu visit queued a full sync â€” and each sync load
  dumped the guest to mission select (that "straight to mission select" jump)
  while stacking up to feel like slow syncing. Menu-close syncs are now REMOVED
  entirely: the live mirror carries the economy, the battle-entry save transfer
  guarantees correctness where it matters, and only true decision points
  (operation results, post-battle, operation change, campaign load) still sync.
  The 2.1.2 screen lockstep itself was working perfectly per the logs and is
  kept.
- **Buying is now physically impossible for the guest.** The definitive layer:
  while a mirrored screen is open on the guest, every game-UI click is
  swallowed at the input-dispatch level â€” the screen is a literal viewer (the
  co-op panel stays clickable). The data gates and disabled trade button remain
  as backup layers.

## 2.1.2 â€” 2026-07-18 (dev release)
- **Fixed the guest being trapped in a mirrored screen** (couldn't enter
  Squads or O.C.I.). The armory's tab switches don't fire a close event, so
  when the host moved from hiring to another tab the guest's mirror never
  closed and blocked its navigation. Screen lockstep is now driven by POLLING
  the host's actual open-screen state â€” tab switches, closes, everything is
  detected within a third of a second and the guest is freed/moved in step.
- **Guest can no longer buy from the black market.** The trade executes at the
  market-stack level, which bypassed the higher-level data gates â€” those paths
  are now gated too, and the guest's TRADE button is kept disabled outright
  (the screen is a pure viewer).
- Real-time economy/stock sync: confirmed working by testing â€” marked complete.

## 2.1.1 â€” 2026-07-18
- **No more view resets when the host leaves a menu.** Closing the black market
  or hiring no longer schedules a sync at all (the live mirror already carries
  those changes), and any OTHER pending auto-sync now waits until the host is
  back on a navigation screen (system map / mission select) â€” exactly where a
  reset is invisible. If a sync does reset the guest mid-mirror, the guest
  automatically reopens the host's current screen afterwards.
- **The host's cart is now shared.** While the black market is open, the items
  the host stages to buy or sell appear in the guest's own trade columns live â€”
  both players see what is ABOUT to be purchased before the trade confirms, not
  just the before/after.
- 4-player co-op: parked by decision until v2 is complete (analysis kept in
  the project notes).

## 2.1.0 â€” 2026-07-18
- **Live two-screen strategy layer â€” increment 1: shop together.** When the
  host opens the Black Market or Hiring screen, the guest's game opens the same
  screen in lockstep (and closes it when the host leaves). Every economy change
  the host makes â€” money/intel/OCI-component variables, items bought or sold,
  market stock â€” is relayed live and the guest's open screen refreshes
  immediately, so both players browse the market and watch purchases happen
  together.
- **The guest lock moves to the data layer.** Per the project's design law (the
  guest controls nothing outside its combat squads), guest-initiated strategy
  mutations â€” currency changes, item add/remove, market changes â€” are now
  refused at the source, and guest-local attempts to open the mirrored screens
  are blocked ("the host drives the strategy screens"). The host's relayed
  changes are the only way guest strategy state moves between syncs.
- Config: `LiveStrategyMirror` (Coop) to disable the whole feature.
- Known gaps for later increments: hiring actions themselves aren't itemized
  yet (roster changes arrive via the existing sync), and item-instance creation
  on the market side relies on matching templates â€” the periodic save-sync
  remains the safety net for anything the relays miss.

## 2.0.0 â€” 2026-07-18 (STABLE â€” the "AI & Combat" milestone)
- **Marked stable v2 by the user.** AI relay, combat replication, desync
  auto-healing, round cadence, and mission flow (including mission failure back
  to the planning screen) are considered complete. Verified this session: the
  pump-only faction design survived a full mission including a failed ending.
- **Host save-loading (save scumming) is back.** The host may load saves while
  connected on the strategy layer: the loaded state simply becomes the new
  truth and the guest is auto-synced to it (the campaign watch already handled
  this). Only MID-BATTLE loads are blocked â€” the save system captures no
  tactical state, so the guest could never follow. Guests remain unable to
  load (the host drives the campaign).
- Roadmap for v2.x: live two-screen mirroring of the strategy layer â€” OCI,
  squads, hiring, black market â€” instead of a one-shot sync afterwards.

## 1.5.2 â€” 2026-07-18
- **Mission-end guest freeze fixed â€” and the whole freeze class retired.** The
  mission-fail teardown turned out to be a THIRD special transition that hangs
  when reached through mod code calling NextFaction (after round rollovers and
  the all-done race). Rather than adding a third guard, mod code now NEVER
  calls NextFaction: the faction pump â€” the mechanism that has never frozen â€”
  is the guest's only advance driver. It is purely state-driven: whenever the
  guest sits on an AI faction whose units are all done (per the host's aidone
  relays), the engine's own pipeline is unsuppressed and advances itself â€”
  through mid-round switches, round rollovers, and mission endings alike â€” then
  re-suppresses the moment there is real work or a player phase. No advance
  messages to race, no special cases to guess.
- Noted for investigation (logs pending from the user): the guest can
  occasionally take control of the host's squads around turn-end boundaries.

## 1.5.1 â€” 2026-07-18
- **Fixed the late-mission freeze (squaddie-down timing).** A race the round
  detection missed: an advance the host sends MID-round can arrive just after
  the guest's board has become fully done (last AI done-flags applied) â€” a
  NextFaction call in that state performs the round rollover INTERNALLY, which
  is exactly the cold-call native hang. The squaddie death only shaped the
  timing. Every one of our NextFaction call sites now checks "is the whole
  board done?" first and, if so, hands the rollover to the engine via the pump
  window instead of calling â€” the second and last entrance to the rollover trap
  is closed.

## 1.5.0 â€” 2026-07-18 (STABLE)
- **Marked stable** after a run past round 3 with no freezes, working rollovers,
  matching squaddie deaths, and desyncs healing with no visible impact.
- **Fixed the false round-mismatch banner.** During a rollover the two round
  counters legitimately differ for a few seconds (the host rolls first, the
  guest's engine follows; the guest's info about the host can also be a few
  seconds stale) â€” the banner fired on that transient. It now only warns when a
  mismatch persists for 8+ seconds outside a rollover, i.e. a real divergence.
- Stable highlights since 1.4.x: identity-addressed orders, continuous +
  escalating auto-resync, hard resync with sync-pause ("stop the game and fix
  it"), host-relayed AI with native round rollovers on the guest, manual turn
  ends, reinforcement tracking, and versions shown in the panel.

## 1.4.28 â€” 2026-07-18
- **Round rollover now runs through the engine's own pipeline on the guest.**
  1.4.27 proved the AI turn-start was NOT the blocker (its skip worked twice
  mid-round; the rollover still hung). The real difference: on the host the
  rollover runs inside the engine's AI processing pipeline; our cold NextFaction
  call from outside that context hangs in native code. Now the host's faction
  advances carry its round number â€” when the guest sees the host's round is
  ahead, it does NOT call NextFaction: it opens a "pump window" where the
  engine's AI pipeline is unsuppressed (actual AI behavior stays dead â€” a
  faction's processing only runs while it has nothing left to do) so the ENGINE
  rolls the round natively, exactly as on the host. The window closes the
  instant the round catches up (20 s failsafe), and held advances then resume.
- Manual turn ends confirmed working (user-verified) â€” no changes.

## 1.4.27 â€” 2026-07-18
- **Round-rollover freeze: attacked at the true block point.** 1.4.26's
  "all-done" guard proved unreliable (the freeze recurred at round 2 with the
  guard silent). The actual blocker is the AI faction's own turn-start, which
  NextFaction invokes when an AI faction becomes active â€” on the guest that
  call can never be pumped and hangs forever. The guest now skips the AI
  faction's turn-start/processing/thinking entirely (its AI never acts anyway â€”
  the host relays everything), which makes EVERY faction advance safe,
  rollover included. The unreliable guard is removed so advances replay 1:1.
- **Manual turn ends now actually work.** The AP-exhaustion auto-end turned out
  to call TacticalState.EndTurn â€” the SAME method as the End Turn button â€” so
  1.4.26's filter missed it. The reliable difference is user input: the button
  and hotkey always fire within a fraction of a second of a click or key press;
  the auto-end fires from attack-animation completion. EndTurn is now gated on
  recent input ("Turn NOT auto-ended â€” press End Turn when the unit is
  finished").

## 1.4.26 â€” 2026-07-18
- **The guest freeze is dead â€” the exact spot was found.** Every guest freeze
  happened at the same call: our code performing the END-OF-ROUND faction
  advance (the rollover into an AI faction), whose turn-start blocks forever on
  the guest because its AI is suppressed (the host's runs, so the host never
  hangs). Mid-round advances (AI â†’ player) have always been safe. Now, when
  every unit of every faction is done â€” i.e. the next advance IS the rollover â€”
  the guest consumes the relayed advance without acting and lets its OWN engine
  roll the round through its natural pipeline, which starts AI factions safely.
  Applies to the reconciler too.
- **Manual turn ends (new, on by default).** While connected, player units no
  longer end their turn automatically on AP exhaustion or after an attack with
  no AP left â€” only the explicit End Turn or Skip commands (or the partner's
  relayed end) do. The engine's auto-ends fired at engine-dependent moments
  that could differ across machines â€” a standing desync source, now closed.
  Config: `ManualTurnEnd` (Coop section) to restore vanilla behavior.

## 1.4.25 â€” 2026-07-17
- **Round drift fixed at its real source.** Logs showed the guest racing to
  round 6 while the host sat on round 2. Cause: whenever the guest advanced past
  an AI faction it marked the ENTIRE faction done â€” but the engine is an
  alternating-activation system where the host consumes ONE activation per
  visit, so the guest burned whole rounds in seconds. Now the host relays each
  AI unit's done-flag ("aidone") as its activation ends, the guest mirrors it,
  and faction advances use a plain NextFaction â€” force-completing a faction is
  a logged last resort. Both engines consume activations at the same cadence,
  so rounds stay in step â€” with no counter writes and no cycle holds (the two
  approaches that crashed/froze).
- **SYNC PAUSE â€” desyncs now stop the game and visibly fix it.** On a detected
  desync (auto-escalation or the panel button), BOTH machines freeze â€” orders
  blocked, AI halted, full-screen "GAME PAUSED, FORCING EVERYONE BACK INTO
  POSITION" â€” the host pushes its authoritative state, units visibly snap to
  it, and both sides resume together. A 12 s failsafe means a lost packet can
  never leave the game stuck.
- **Round mismatch is loud.** If the guest's round ever differs from the
  host's, a banner says so immediately (objectives follow the host's round).
- Diagnostics: any order sent for a unit with NO identity now logs the unit's
  template and tile â€” naming the exact source of the next cascade, if any.

## 1.4.24 â€” 2026-07-17
- **Fixed the recurring guest freeze â€” root cause found and removed.** All
  three freezes since 1.4.21 shared one signature: the guest entering the
  engine's NextFaction (via an applied host advance or the reconciler) and
  hanging in NATIVE code â€” no exception, no return. Cause: 1.4.21's
  "host-driven faction cycle" held the guest engine's own NextFaction calls,
  which starved its internal activation bookkeeping; a later forced advance
  then spun forever in the engine's native scan loop. That design is reverted:
  the guest's engine cycles factions locally again (as in 1.4.20 and earlier,
  which never froze), the host relays only AI-faction exits, echo advances are
  dropped, and the phase-based stranded failsafe returns. All 1.4.22/1.4.23
  hardening (exception-consuming advances, per-subsystem guards, log-flood
  fixes) is kept.
- **Known cost:** the guest's round counter can drift one ahead again. A safe
  round-sync needs a sync point the engine tolerates; the previous two attempts
  (writing the counter, holding the cycle) both broke the game, so this ships
  stable-first.
- **Unity console messages now go to LogOutput.log** (`WriteUnityLog = true` in
  both installs' BepInEx.cfg) â€” engine-side errors were previously visible only
  in the console window and invisible to log analysis.

## 1.4.23 â€” 2026-07-17
- **Fixed the second guest freeze.** The log showed the guest entering the
  engine's NextFaction while applying a host faction-advance and never
  returning. Three defenses now cover both possible mechanisms: (1) a failing
  advance is CONSUMED instead of retried every frame (the invisible
  exception-spiral case) and the faction reconciler catches the guest up;
  (2) a pressure valve on the faction hold â€” if the engine calls NextFaction
  many times in one frame (an internal loop our hold can never satisfy), one
  advance is let through instead of freezing, and the reconciler corrects any
  slip; (3) every per-frame mod subsystem is individually exception-guarded so
  one failure can't kill the update loop frame after frame.
- **Cursor/move-line options now appear only in battle** â€” they cluttered the
  panel in the menu, where they do nothing.

## 1.4.22 â€” 2026-07-17
- **HOTFIX: fixed the freeze ("Not Responding") on both machines.** Two log
  floods, not a desync: (1) the scene-wide actor search added in 1.4.18 turns
  out to be rejected by Unity for this type â€” it logged an error on EVERY call
  and sat in per-frame paths, drowning both processes (it also never found
  anything, so removing it loses nothing); (2) the guest's "held local faction
  advance" message logged every frame through entire AI phases. The search is
  replaced by a spawn registry (spawned units are registered at their spawn
  event and stay reachable no matter where the scene parents them â€” this is
  also faster: identity lookups are now a dictionary hit instead of a scan),
  and the hold log is throttled.

## 1.4.21 â€” 2026-07-17
- **Rounds now stay in step â€” the host drives the entire faction cycle.** The
  guest's engine no longer advances factions on its own once settled in battle;
  every real faction change on the host (player phases included) is relayed and
  replayed 1:1. Identical cycles mean identical round counters, so
  round-limited primary/secondary objectives finally count the same on both
  machines â€” without touching the crash-prone round field. A failsafe steps the
  guest forward if it ever slips off the host's active faction.
- **Snapshots can now kill.** Dead units were excluded from snapshots, so a unit
  dead on the host but alive on the guest was never corrected ("the bug that
  died did not move"). Snapshots now carry a dead flag and the guest kills its
  lingering copy.
- **Garrisoned/embarked units are tracked.** Units without a tile (inside
  structures or transports) were silently skipped from both ids and snapshots â€”
  the source of the remaining no-identity desyncs. They now get ids from their
  world position and appear in snapshots (HP/AP fixable by identity).
- **Fixed the red log error** (crash while applying damage packets):
  Entity.GetElement throws on an out-of-range index rather than returning null;
  now bounds-checked. Mismatched element counts degrade gracefully instead of
  aborting the packet.

## 1.4.20 â€” 2026-07-17
- **Fixed the recurring "(3,19)" unidentifiable-unit desync.** Log analysis
  showed the damaged enemy had no stable id on EITHER machine: when several
  units share a tile at capture time (garrisons, transport passengers), the
  tile-based id collided and the unit was skipped â€” permanently invisible to the
  auto-fixer. Ids now include the unit TEMPLATE (deterministic hash of the asset
  name), which breaks those ties; remaining collisions are logged.
- **HARD RESYNC â€” the "resolve it no matter what" fixer.** A new "FORCE RESYNC
  (host state wins)" button in the co-op panel (works from either side; the
  guest asks the host to push). The host sends its full board state and the
  guest applies it aggressively: identity matching first, then â€” for units with
  no id at all â€” matching by unit type + faction + nearest position, then
  multi-pass teleports plus HP/AP correction. Also triggers automatically when
  soft resyncs fail 3 times within 10 s.
- Snapshots now carry each unit's template + faction to power that matching.

## 1.4.19 â€” 2026-07-17
- **HOTFIX: reverted 1.4.18's round-counter write â€” it crashed the guest's
  battle entry.** Setting the round mid-battle blew up the game's own turn-order
  panel (it indexes internal state off the round), throwing inside NextFaction
  and leaving the guest unable to load into battle. The round counter is
  read-only for us now; the "turns off by one" display drift will be fixed at
  its source (faction-advance cycling) in a later pass. Everything else from
  1.4.18 (reinforcement tracking, scene-wide enumeration, faster stranded
  recovery) is unchanged and was working.

## 1.4.18 â€” 2026-07-17
- **Mid-battle reinforcements are now fully tracked.** The turn-2 desync: an
  enemy that spawned mid-battle had NO identity (the order arrived with
  RId=-1 CId=-1) and was invisible to healing snapshots â€” its orders dropped and
  its position could never be auto-corrected. Two fixes: (a) spawned units are
  captured at the spawn event itself (both machines execute the spawn, so both
  derive the same id from the spawn tile before the unit can move); (b) all
  actor enumeration (ids, snapshots, hashes) now merges a scene-wide search, so
  units parented outside the actors root are seen everywhere.
- **Round counter stays in step.** The guest's engine was drifting one round
  ahead ("turns are off by one"); the host's snapshots now carry its round and
  the guest adopts it â€” round-limited objectives count identically.
- Stranded-guest self-heal reacts in 2.5 s (was 4 s); the board-busy check uses
  the engine's own moving-actors set (also covers spawned units).
- Known/observed: the host not "locking on" to an enemy acting inside its fog is
  the unshared fog-of-war issue (guest could see it, host couldn't) â€” the
  shared-vision feature on the roadmap addresses that.

## 1.4.17 â€” 2026-07-17
- **Fixed the round-long guest freeze / turn-order deadlock.** The engine uses
  alternating activations (player â†’ AI â†’ player within one round), and both
  machines advance past AI factions locally at round start â€” but the host also
  relays that advance, and the guest queued the echo forever (it's only consumed
  on an AI faction). The 1.4.15 input hold treated the stuck echo as "partner
  still acting" and froze the guest for the entire round; the host's engine then
  sat waiting for the guest's squads â€” deadlock, no desync. Echo advances that
  arrive while the guest is already in the player phase are now dropped, and
  pending advances no longer block input at all.
- **Stranded-guest self-heal.** If a real (non-echo) advance is ever lost, the
  host's periodic snapshots now carry its current phase; a guest stuck on an AI
  faction for 4+ s while the host reports the player phase steps itself forward
  one faction per second until it catches up.
- Answer to "does hand end-turn differ from exhausting AP?": yes â€” a manual end
  turn replicates each unit's done-flag; AP exhaustion relies on both engines
  auto-marking done independently. Both paths are now safe, but the freeze was
  the echo bug above, not the done-flags.

## 1.4.16 â€” 2026-07-17
- **Fixed "ghost moves" introduced in 1.4.15** (a move worked on the partner's
  screen but not on yours). The 1.4.15 input hold and the order broadcast are
  two separate hooks on the same click; the hold skipped the action locally but
  the broadcast still sent it, so the partner executed an order that never
  happened on the clicker's machine â€” a self-inflicted position desync that also
  suppressed its own healing (each ghost click counted as "order in flight",
  deferring snapshots) and raised no warning. The broadcast gate now refuses in
  exactly the situations the input hold refuses.

## 1.4.15 â€” 2026-07-17
- **Guest input is held during AI turns.** Log analysis of the turn-2 cascade
  found the fork's birth: the guest's AI processing is suppressed, so the game
  leaves the guest's input LIVE during AI factions' turns â€” and the guest moved a
  unit onto (9,22) an instant before the host's relayed enemy move into the same
  tile arrived. Each machine resolved the contest differently and the boards
  forked. The guest is now blocked from issuing orders until the player faction
  is active and every relayed order has fully applied ("the other side's actions
  are still playing out").
- **Healing snapshots no longer get discarded while firing.** Damage packets
  were updating the "guest recently acted" timestamp, so during sustained fire
  every incoming healing snapshot was thrown away as stale â€” the desync fixer
  effectively never ran while it was needed most. Only real orders (move/skill/
  skip/done) gate snapshots now.
- **Position fixes are multi-pass.** A drifted unit's correct tile is usually
  occupied by ANOTHER drifted unit (that's what a contested-tile fork looks
  like); teleports now run in passes so chains untangle instead of being
  skipped.
- **One desync burst = one warning.** A missed machine-gun volley is 6+ packets;
  resync requests are now throttled to one per 3 s instead of spamming the
  banner and the host.
- Sharper diagnostics: unresolved orders/damage now log the stable ids and
  whether the actor or the element failed to resolve.

## 1.4.14 â€” 2026-07-17
- **Orders are now addressed by unit identity, not by tile.** Log analysis of the
  machine-gun cascade showed the root cause of every recurring desync: orders
  (and damage packets) located their unit by "the actor on tile (x,y)", so the
  moment positions drifted by one tile, every subsequent order missed and the
  drift snowballed. Every order/damage/death message now carries the unit's
  stable id (roster slot for players, spawn id for enemies) and resolves by that
  first â€” tile is only a fallback. A drifted unit still receives its orders and
  its damage.
- **Desync healing is now immediate and continuous, not end-of-turn.** Previously
  the fixer only ran at faction switches, so a mid-turn desync sat uncorrected
  (the "banner appeared but nothing changed" case â€” the banner was the
  order-dropped warning; the fixer never fired). Now: (a) any dropped order or
  unresolvable damage packet triggers an instant authoritative snapshot from the
  host; (b) the host streams a healing snapshot every 5 s during battle.
- **Snapshots wait for a safe moment.** They are applied only when no unit is
  animating and none of the guest's own orders are still in flight, and a
  snapshot that predates the guest's latest order is discarded (a fresh one
  follows within seconds) â€” so reconciliation can no longer fight animations or
  yank a just-moved unit back.

## 1.4.13 â€” 2026-07-17
- **Desync fixer, phase 2b â€” enemies now self-heal too.** Previously only player
  units could have their position auto-corrected (matched by roster slot); AI /
  enemy units had no stable cross-machine identity, so a drifted enemy would miss
  its health delta and cascade the whole board. Each enemy now gets a stable id
  derived from its spawn tile (identical on both machines because deployment is
  deterministic), captured while the board is pristine and cached for the battle.
  The host's authoritative snapshot now teleports drifted enemies back onto the
  correct tile and reconciles their HP/AP, the same way it already did for
  players. This directly targets the "enemy health desync cascaded everything"
  case.

## 1.4.12 â€” 2026-07-17
- **Versions on screen.** The co-op panel now shows a persistent "Mod vX â”‚ Game
  vY" line so both players can confirm at a glance that they match. The test
  launcher also prints the mod version it's starting.

## 1.4.11 â€” 2026-07-17
- **Desync self-heal, phase 2: position (player units).** The reconciliation
  snapshot now carries each unit's stable roster id, so the guest can match a
  player unit to the host even after their positions have drifted â€” and snap it
  back to the host's tile (force-teleport) along with its AP/HP. When the desync
  detector spots a mismatch, the host now *immediately* force-sends a snapshot
  rather than waiting, so a drifted board self-corrects on the spot (with an
  "auto-corrected N unit position(s)" notice). Enemy/AI positions aren't
  reconciled yet (no stable id for them) â€” that's still to come.

## 1.4.10 â€” 2026-07-17
- **Action lock (serialised player actions).** Only one player can have an action
  in flight at a time during the shared turn. When you start a move or attack, your
  partner is briefly blocked from *starting* their own until yours (and its
  animation) finishes â€” with a status hint "your partner is taking their action".
  Previews/aiming stay free, and fast back-and-forth still works; you just can't
  both act in the exact same instant. This closes the biggest remaining desync
  source â€” two actions interleaving mid-flight. A host-wins tiebreak handles the
  rare simultaneous start, and the lock auto-releases (failsafe + on turn end).

## 1.4.9 â€” 2026-07-17
- **Desync self-heal, phase 1 (HP/AP).** The host now periodically ships an
  authoritative snapshot of every unit's action points and per-soldier HP, and
  the guest quietly snaps any drifted values back to match. This is a safety net
  for small HP/AP divergences (it logs how many it corrected). Unit *positions*
  are not reconciled yet â€” that's the harder phase 2 â€” so units still need to be
  on matching tiles for this to apply.

## 1.4.8 â€” 2026-07-17
- **Stop a slow move animation from being mistaken for a desync.** The 1.4.7 retry
  gave an unresolvable order a fixed 2 seconds â€” but a long move animates slowly
  (more so with co-op's speed-up disabled), so a shot could be dropped just before
  the moving shooter arrived. Dropping it skipped that shot's AP deduction on one
  side, which then cascaded. The retry now keys off actual unit movement: while
  ANY unit is still animating it keeps waiting (no time limit), and it only reports
  a real desync once the board is idle and the unit still isn't there.

## 1.4.7 â€” 2026-07-17
- **Fix desync from orders arriving before a move finishes animating.** A relayed
  move animates on the receiver; the moving unit isn't registered on its
  destination tile until it arrives, but the mod had already processed the next
  message (its turn-end / a follow-up order) that same frame, so it failed to
  resolve and flagged a (false) desync â€” and the turn-end was lost. Orders whose
  unit isn't resolvable yet are now retried for a short window; only if the unit
  truly never appears is a real desync reported.
- **Fix the guest slightly over-damaging enemies.** The receiver replays each
  shot (for the animation and AP) *and* applied the authoritative damage delta â€”
  double-counting, so enemy HP drifted low relative to the shooter's screen.
  Replayed shots are now visual/AP only; HP comes solely from the authoritative
  damage/death sync.

## 1.4.6 â€” 2026-07-17
- **Fix corpses vanishing on the guest** when the host killed an enemy. The 1.4.4
  health change Dies an element when its HP reaches 0; a normal kill also sends a
  separate death message, so the element was Die()'d twice on the guest and the
  second call stripped the corpse (the host, dying it once, kept it). The death
  message is now ignored if the element is already dead.
- **On-screen desync warning.** When the mod detects a likely desync â€” a unit the
  partner references isn't on the same tile here, or the periodic state-checksum
  mismatches â€” it now shows a brief orange banner at the top of the screen naming
  roughly what diverged (e.g. the tile), instead of only writing it to the log.

## 1.4.5 â€” 2026-07-17
- **Fix position desync from multi-tile moves (and its cascade).** A move across
  several tiles is executed by the game as repeated `MoveTo` calls â€” one per tile
  stepped, all with the same final destination. The mod was relaying every one of
  them; the receiver applies the first fully (jumping straight to the
  destination), so each following per-tile message could no longer resolve and
  the unit's position drifted apart. Because everything is addressed by tile,
  that single drift then cascaded â€” subsequent moves/attacks/turn-ends for that
  unit failed to resolve on the other side. Now only the first call of a move is
  relayed. Applies to both player and AI/enemy moves.

## 1.4.4 â€” 2026-07-17
- **Fix health desync when both players damage the same enemy.** Element-HP sync
  was broadcasting the *absolute* resulting HP from each shooter's local view.
  When two players hit the same element, those absolute values conflicted â€” each
  machine kept only the last one it received, so they ended at different HP (the
  "guest hit a target but damage didn't sync" bug). It now sends the *damage
  delta* and subtracts it locally, so both players' damage accumulates correctly
  regardless of order (and a combined-fire kill registers on both sides). This is
  why it only showed up once players started focus-firing the same target.

## 1.4.3 â€” 2026-07-17
- **Fix a turn-cycle deadlock in battles with multiple AI factions.** In such
  battles (e.g. player + aliens + wildlife) the game calls `NextFaction` many
  times within one AI faction's turn â€” once per AI unit â€” and the host was
  signalling the guest to "advance" on every one of those calls, ~15 times where
  ~4 were real. The guest over-advanced and the two turn cycles desynced into a
  freeze around round 2 (no one could take their turn). Now the host only signals
  on a *real* faction change, and the guest â€” whose AI is suppressed, so its AI
  units were never marked done â€” marks the faction's units done before advancing
  so `NextFaction` can actually leave the faction. Single-AI-faction battles were
  unaffected, which is why earlier tests passed.

## 1.4.2 â€” 2026-07-16
- **Full cursor / move-line visibility options.** The move line can now be toggled
  independently of the cursor, and each can be controlled in both directions. Four
  persisted toggles in the co-op panel under "CURSOR / MOVE LINE": *Send my cursor*,
  *Send my move line* (hide yours from your partner), *Show partner cursor*, *Show
  partner line* (hide theirs on your own screen). E.g. keep the battle cursor but
  turn the line off â€” either for yourself or for your partner.

## 1.4.1 â€” 2026-07-16
- **Cursor colours by role + a hide toggle.** The host's cursor and move line are
  now blue and the guest's stay orange, so each player sees their partner in the
  partner's colour. A new co-op-panel button ("My cursor: shown/hidden from
  partner") lets either player stop broadcasting their own cursor/move line if
  they find it distracting; the choice persists (config `Coop.ShareCursor`).

## 1.4.0 â€” 2026-07-16
- **Partner move indicator.** The in-battle tile cursor now upgrades to a move
  line when the partner has a unit selected: an orange line is drawn from that
  unit's tile to the tile they're hovering (mirroring the game's own move
  indicator, in the partner's orange colour). With no unit selected it stays the
  familiar hovered-tile bracket. Best-effort and self-hiding â€” falls back to the
  plain cursor if the selected unit can't be resolved. (Move-cost number not yet
  shown â€” would need pathfinding-cost replication.)

## 1.3.9 â€” 2026-07-16
- **Make the battle pause screens persistent.** (1) Host: the HOLD FIRE screen was
  being hidden after ~1.5s by the guest's `synced` ack (which fires when the
  battle-save load changes the guest's strategy state, well before it's actually
  in the battle). The blocker now stays up for exactly as long as the host is
  really holding â€” only the hold's own release (guest `ready` / failsafe) can
  clear it. (2) Guest: a single load screen now covers the *entire* multi-step
  battle entry (sync load â†’ mission prep â†’ launch â†’ battle scene) instead of
  flickering between per-phase pauses with gaps, so the guest can't accidentally
  click out mid-entry and desync. Both are failsafe-released.

## 1.3.8 â€” 2026-07-16
- **Close two pause-coverage gaps.** (1) Battle: the host's HOLD FIRE now engages
  the instant its battle begins, not after it finds and ships the autosave â€” that
  poll took a second or two during which the host was in the battle with no pause
  and could act before the guest even started loading. (2) Planning: the guest now
  shows an "ENTERING PLANNING WITH HOSTâ€¦" pause while it loads into the host's
  planning view, instead of sitting on the mission-select screen with no feedback.

## 1.3.7 â€” 2026-07-16
- **Enforce the sync pauses instead of just showing them.** New users were
  frustrated that the host got "too much leeway" on battle turn 1 â€” the guest
  signalled ready the instant its battle sim ticked over (before it was actually
  loaded in), and the host's HOLD FIRE was only a dimmed overlay it could act
  through. Now: (1) the guest sends `ready` only once it's genuinely settled in
  the battle (on the players' turn, past a short settle window); (2) the host is
  **hard-blocked** from issuing orders / moving / ending the turn until that ack
  arrives, with a failsafe release so a lost ack can't strand it; (3) sync pauses
  have a minimum on-screen time so they can't flash away before registering.

## 1.3.6 â€” 2026-07-16
- **Mirror host squad-size changes to the guest** (the other half of 1.3.5). The
  host's add/remove of a squaddie now applies to the guest's roster live, so the
  planning view's squad counts match instead of drifting. Units are addressed by
  hired-roster index, squaddies by their save-stable id. Pre-existing drift
  self-heals on the next full sync.

## 1.3.5 â€” 2026-07-16
- **Block guest squad-size edits.** A connected guest could add/remove squaddies
  (changing squad size), which diverged the two rosters and desynced the battle.
  Now blocked at the data layer (`BaseUnitLeader.TryAddSquaddie`/`TryRemoveSquaddie`),
  exempting mod-driven sync loads so the guest's roster still rebuilds from the
  host's save. Consistent with the existing loadout / deployment / launch locks.

## 1.3.4 â€” 2026-07-16
- **Draggable co-op panel** (QoL). The fixed top-right panel covered the game's
  F7 bug-report button. Grab the title bar to drag it anywhere. Poll-based drag
  (UI Toolkit pointer callbacks are unreliable under IL2CPP).

## 1.3.3 â€” 2026-07-16
- Guest spectator prep screen now also mirrors the **on-map unit markers** when
  the host adds/removes a deployed unit (previously only the data layer was
  mirrored, so the visual didn't update). Cosmetic; battle roster was always
  correct via the save load.

## 1.3.2 â€” 2026-07-16
- **Disable click-to-speed-up movement while connected.** The fast-forward
  cranks a global movement time-scale, which changed local animation/event
  timing and desynced reaction fire / event ordering. Works again in single-player.

## 1.3.1 â€” 2026-07-16
- **Authoritative health/death sync.** Damage was being re-rolled on each machine
  (RNG-based â†’ different results â†’ the target the host hit stayed at full HP on
  the guest). Now the side that deals the damage broadcasts the resulting
  per-element HP and deaths; the other side snaps to them. Symmetric across host,
  guest, and relayed AI shots. Damage-only gate (HP decreases) avoids setup floods.

## 1.3.0 â€” 2026-07-16
- **Host-authoritative AI.** The game's AI is inherently non-deterministic across
  machines (it thinks on a background Task with a wall-clock timeout, and its
  state machine is frame-time paced), so lockstep "run the same AI from the same
  seed" could never hold â€” enemies ended up in different positions. Now only the
  host runs the AI; it relays every AI order (move / move-with-skill / attack /
  skip) and drives the guest's faction advancement. The guest suppresses its own
  AI (`TacticalManager.ProcessAI`) and mirrors the host.

## 1.2.3 â€” 2026-07-16
- Block the guest from manually launching a mission (it must be pulled in by the
  host). The mod's own follow-the-host auto-launch is exempt via a flag.

## 1.2.2 â€” 2026-07-16
- **Guest loadout lock.** A connected guest can no longer change equipment (host
  authority); the guest still sees the host's loadout changes mirrored. Blocked
  at the UI-click level so save/battle container operations are untouched.

## 1.2.1 â€” 2026-07-16
- **Game-version mismatch warning.** Each side computes a build fingerprint
  (`Application.version` + il2cpp metadata size), exchanged in the hello
  handshake; a mismatch shows a persistent red banner (save format is
  version-specific, so cross-version play breaks).

## 1.2.0 â€” 2026-07-16
- **Equipment sync in planning** (the project's hardest feature). The host's
  weapon/gear swaps now mirror to the guest's planning view. Root fix: items are
  identified by template ID, not GUID â€” the guest resolves them by enumerating
  the owned-items pool and preferring an unused instance. Battle loadouts were
  always correct via the save load; this makes the planning view match too.

## 1.1.0 (dev) â€” 2026-07-15
- Experimental equipment mirroring; also extended auto-sync to every strategy
  sub-screen that changes persistent state (black market, hiring, armory,
  workshop, ship upgrades, operation result) so the guest never diverges.

## 1.0.0 â€” 2026-07-15 â€” first stable milestone
- Full feature set consolidated and hardened. TCP + Steam transports, players
  roster, title-screen branding, all sync pause screens, host-delegated squad
  assignment, tile cursor, desync detector, guest load-blocking, save-scum
  warning.

## 0.21.x â€” 2026-07-15
- **Battle tile cursor** (screen cursors are meaningless in battle â€” cameras
  differ; the marker now pins to the hovered tile, tracked per-frame to screen).
- **Desync detector**: both sides checksum actor (tile, HP, AP) at each faction
  switch and exchange hashes; mismatch surfaces a warning.
- **Guest load-blocking** while connected (host drives the campaign).
- Steam ID hover-to-reveal (clean screenshots).
- Fix guest infinite-load when the host loads an earlier save mid-guest-battle
  (12s grace then force-load).

## 0.20.x â€” 2026-07-15
- **Host-delegated squad assignment.** Panel section to assign each battle slot
  to host or guest (replaces the fixed alternating split); mirrors live.

## 0.19.x â€” 2026-07-15
- Title-screen "CO-OP" branding and Early-Access welcome-box hijack; full pause
  coverage (prep-entry wait, enforced hold-fire until the guest is in the battle).

## 0.18.x â€” 2026-07-15
- **Steam P2P transport** added alongside direct-IP TCP (uses the game's shipped
  Steamworks.NET). Mutual-send handshake (no AOT callbacks in IL2CPP). Panel
  gains a PLAYERS roster.

## 0.17.0 / 0.16.0 â€” 2026-07-15
- Guest-side sync pause screens; host input-blocker while the partner syncs;
  operation-change / new-planet auto-sync; guest "synced" acknowledgement.

## 0.15.x â€” 2026-07-15
- Post-battle auto-sync (removes the "remember to click Sync" dependency);
  campaign/continue/trust live mirroring; prep-exit detection fixes (poll the
  active screen â€” the prep screen's OnInactive override can't be safely patched).

## 0.14.x â€” 2026-07-15
- **Planning spectator mode.** The guest mirrors the host's mission-prep screen
  (live unit placement, roster membership), read-only. Host authority kept: the
  guest can't edit deployment or roster.

## 0.13.x â€” 2026-07-15
- Title-menu integration ("MENACE (co-op)" entry); panel restyled to match the
  game's dark theme and moved out of the way.

## 0.12.0 â€” 2026-07-14 â€” original vision complete
- **Unit ownership** (soft selection gating): each player controls half the
  squad; the round advances when both finish. Simulation identical on both
  machines (zero desync surface).

## 0.9.0â€“0.11.1 â€” 2026-07-14
- **Battle rendezvous**: when the host launches, the guest is auto-pulled into
  the same battle (battle-start autosave transfer + mission/seed replication).
- **Deployment replication**: the host's exact unit placement is stamped onto
  the guest's plan at spawn, so both battles start identical. Guest deployment
  dragging disabled.

## 0.7.0â€“0.8.0 â€” 2026-07-14
- **Session sync**: on join (and on demand), the host saves and transfers its
  campaign; the guest auto-loads it. **Ghost cursor sharing** both directions.

## 0.5.0â€“0.6.0 â€” 2026-07-14
- AP and desync issues resolved (per-order RNG seeds). **Save transfer** with
  hash verification. Menu-driven connections.

## 0.3.0â€“0.4.0 â€” 2026-07-14
- In-game panel built in **UI Toolkit** (the game's UI stack; uGUI/TMP/IMGUI are
  stripped or unused). Aim-preview skill calls filtered out of replication;
  per-order RNG seeding introduced.

## 0.2.0 â€” 2026-07-14
- Turn-done state replicated (actor-addressed) so "end turn" syncs regardless of
  which unit each player has selected.

## 0.1.0 â€” 2026-07-13
- First battle test passed: same save + same mission = identical procedural map;
  moves, attacks, and kills replicate across two instances.
- Core replication layer: `CoopNet` (length-prefixed JSON frames over TCP),
  `CoopRunner` (main-thread message pump), `Replication` (Harmony prefixes on
  `Skill.Use` / `Actor.MoveTo` / turn events, RNG seed sync).

## 0.0.1 â€” 2026-07-13
- Hello-world BepInEx IL2CPP plugin verified loading in-game.



## v3.5.1 (staged)

- Patrol now runs ONLY when no enemy is known. It previously triggered whenever
  the nearest enemy was merely far, marching units toward a fixed map quadrant
  that could be away from the fight -- long strides, no contact, zero attacks
  for a whole mission. Attacks went 0 -> 8 in the run after this change.
- Moves are gated on the engine's own MovementSystem.IsMovementPossible, both
  before broadcasting and when the harness picks a tile. The broadcasts are
  Harmony PREFIXES, so an unreachable destination was relayed before the local
  pathfinder refused it: 326 "Movement failed!" warnings in one battle and 41
  "Order 'move' never resolved -- DESYNC" on the partner, from the same cause.
  "Empty tile" was being treated as "reachable tile"; it is not.
- Combat-state corrections (suppression/morale/stance) give up after 3 attempts
  at the same value and log which field will not hold. These are recomputed by
  the engine, so a write that did not stick was re-applied every snapshot
  forever -- the guest logged "Resynced from host: 2 combat-state value(s)" on
  every tick, sat permanently in "syncing with host", and its harness never
  acted. Both processes healthy, board deadlocked.

KNOWN, NOT YET FIXED: host and guest can disagree about which unit is active
(host active=carda while guest active=pike, host mine=0/theirs=1 vs guest
mine=1/theirs=2). The convergence guard stops the resync loop that hid this,
but the underlying activation-cursor disagreement is the next thing to chase.

## v3.6.0 (staged)

Deadlock and desync work, all found by sweeping whole logs rather than metrics.

- END TURN ALL is no longer used while the partner still has units. ui.EndTurnAll()
  is the End Turn *button*: it finishes EVERY unit in the faction, the partner's
  included, so the host was ending the guest's units ("TAKEOVER: this side issued
  'skip' for a unit it does NOT own"). The guest never learned those turns ended,
  so the sides disagreed on how many units still owed a turn (host mine=0/theirs=1
  vs guest mine=1/theirs=2) and the board deadlocked. Falls back to per-unit Skip,
  which only touches owned units. TAKEOVER 2 -> 0.
- Player turn-done flags now converge from ordinary same-round snapshots, in the
  DONE direction only. Previously player done-flags synced ONLY on a hard resync,
  justified by "player turn ends replicate normally" -- but 'done'/'skip' orders
  do get lost, and nothing ever reconciled it. Round mismatch 2 -> 0.
- Moves are gated on MovementSystem.IsMovementPossible before broadcast and when
  the harness picks a tile. "Empty tile" is not "reachable tile".
  "Movement failed!" 326 -> 0; "Order 'move' never resolved" 41 -> 0.
- Combat-state corrections give up after 3 attempts at the same value and name
  the field. Morale is engine-recomputed, so it never held and the guest re-applied
  it every tick -- permanently "syncing with host", harness starved.
- Damage packets (ehp/edie) address squad members by GetSquaddieId(), not by index
  into GetElements(). That list holds only LIVING members, so it shrinks on
  casualties and indices shift: "cannot resolve el4 -- actor=found,3 els" was the
  visible half, and where the index still existed damage hit the WRONG squaddie.
  Refuses and resyncs rather than guessing.
- BattleWatchdog: if the board fingerprint (round, faction, positions, turn-done,
  AP) is unchanged for 45s, auto-recover -- host pushes authoritative state, guest
  asks for it. Every deadlock class we fixed looked different from the front but
  identical from outside: the board stopped changing while both processes stayed
  healthy. This catches the classes we have not found yet, and says so loudly
  after 3 failed attempts rather than retrying in silence.
- Owner labels: HOST/GUEST tag above every player unit in battle (Coop >
  ShowOwnerLabels), colour-matched to the Player 1 / Player 2 turn banner.
- Launch-CoopTest promotes staged builds AFTER killing the game, and verifies it.
  It previously promoted before the kill, so every promotion silently failed
  against the locked DLL while printing "Promoted" -- a whole run validated code
  that was never deployed.

## v3.6.1 (staged)

- The harness never plays before the partner has reported into the battle.
  HostHoldFire alone was not enough: it is failsafe-released after 12s so a lost
  ack can never freeze a HUMAN's squad, but loading into a battle regularly takes
  longer than that, so the harness resumed while the partner was still on the
  loading screen and opened a round of drift immediately. The two needs are now
  separate -- the failsafe still unblocks a human, while the harness waits on
  SessionSync.PartnerBattleConfirmed, set only when the partner actually reports
  in and cleared at every battle entry. Gate sits ABOVE the lockout escape so the
  escape cannot punch through it.
- A unit the harness cannot finish is now ENDED rather than abandoned. Units that
  fail to act are parked in _stuckThisRound and skipped by FindMyPendingUnit; once
  every remaining unit was on that list it returned null and AutoPlay returned
  silently while the unit was still not turn-done -- so the phase could never end.
  Seen live at round 3: guest holding control with one unit, taking no action for
  285s, host parked on ctrl=them/mine=0, every hard restore reporting 0
  corrections. It was never drift; the harness was refusing to play.
  Only ever finishes units this side OWNS.
- BattleWatchdog escalates instead of repeating itself: attempt 1 reconciles state,
  later attempts hand over control (TurnControl.ForceRecoverControl). A restore
  reporting zero corrections proves the boards are identical, so re-syncing state
  can never break that class of stall -- somebody has to be given the controller.
- AI stragglers holding a faction are settled even when they ARE addressable, once
  the host has demonstrably left that faction in this round. The old filter skipped
  any unit with a coop id on the theory a relay would finish it, but if the done
  relay was lost none ever arrives: seen as 1145 repetitions of "Still on faction 7
  waiting for N unit(s)" against fully-addressable units while the host played on.
- HOST/GUEST labels can be toggled from the co-op panel (Show HOST/GUEST labels).

## v3.6.2 (staged)

- Battle-entry confirmation is MUTUAL. The guest used to self-confirm on the
  theory that "the battle came from the host's save, so the host is already
  there" -- observed false live: the guest sat in the battle at round 1 while the
  host had not entered at all. The host now sends "ready" when its battle starts
  (it is demonstrably in the battle at that moment), and each side waits for the
  other's.
- The harness entry gate has a 90s failsafe (SessionSync.HarnessMayAct). A gate
  with no failsafe is just a slower deadlock; a lost "ready" must not disable the
  harness for the rest of the mission.
- BattleWatchdog ignores deliberate holds (entry gate, hold-fire, guest entering,
  sync pause). It fired at round 1 against the entry gate doing its job, and
  "recovering" there would resync on top of a partner still loading.

## Launch-CoopTest fixes (test harness, not the mod)

The guest was not auto-connecting. Three separate faults in the launcher, all of
which made the .cfg on disk look correct at every point the script inspected it:

1. Wait-ModReady waited for "panel created", which is written ~45s into startup
   AND was present in the previous run's log. A stale log matched instantly, so
   the script reset the roles to Off while both games were still booting -- they
   read Off 45s later and never connected. It now waits for the "loaded.
   AutoMode=" line, emitted immediately after Config.Bind, which PROVES the
   config was consumed before anything resets it. The adopted mode is returned
   and asserted, so a role that fails to stick is now a loud red warning instead
   of a silently wasted run.
2. Logs are DELETED rather than Clear-Content'd, so a leftover file cannot match.
3. The post-kill wait was a fixed 3s. A dying instance still flushes its own
   BepInEx config on the way out, landing after the script wrote "Mode = Host"
   and quietly restoring "Mode = Off". It now polls until no Menace process
   exists, then settles.

Config writes also use UTF-8 without a BOM ([System.IO.File]::WriteAllText);
Set-Content -Encoding utf8 on PS 5.1 emits a BOM. Not the root cause here, but a
BOM ahead of the first section header does make BepInEx fall back to defaults.

## v3.6.3 (staged)

- Element.Die() is now suppressed during a replayed skill/move, as SetHitpoints
  already was. Death was a SEPARATE, unguarded path: the engine's own kill logic
  ran during replay and killed squad members that were still alive on the
  shooter's machine. Squads then diverged permanently and worsened with every
  exchange -- one unit went 6 -> 4 -> 1 -> 0 living elements while the shooter
  still had 9, and damage packets arrived for squaddies that no longer existed.
  This had to be PREVENTED, not repaired: BOARD RESTORED reports "0 revived"
  because a hard restore does not resurrect dead squad members. Authoritative
  deaths ("ehp"/"edie") do not run under the flag, so they still apply.
- A damage/death packet naming a squaddie this side lacks now escalates to a
  structural repair rather than a soft resync, which could never fix it.
- An 'edie' for a unit that is already gone here is dropped quietly -- the
  outcome agrees, so resyncing over it was pure churn.
- Lockout escape 5s -> 15s and turn-control handover failsafe 12s -> 8s. The
  escape used to win that race every time, so the harness barged in while the
  partner still legitimately held control -- both sides acting at once, the exact
  ordering desync turn control exists to prevent.
- The host now ANSWERS the guest's "ready" instead of only announcing its own.
  Its announcement goes out at the host's battle start, before the guest has
  loaded; the guest recorded it and then cleared the flag when its own battle
  began, wiping the confirmation it already held and waiting out the full 90s
  failsafe every time. Replying delivers it after the guest has entered, which
  is the only point it can survive. Only the host replies, so no ping-pong.
- CatchUpToHostRound no longer bails on IsPlayerTurn() alone. It read that as
  "our units still owe turns", but the cursor can sit on the player phase with
  EVERY unit finished -- seen live with the guest pinned on round 1 reporting
  f1..f9 all left=0, roundDone=True, finishedPct=1 while the host was on round 3.
  Nothing was left to act, nothing could advance it, and the one mechanism meant
  to pull the guest forward refused to run; the harness just re-ended turns for
  units that were already done. It now blocks only on a unit that genuinely owes
  a turn, and when there is nothing left to settle it closes the phase instead of
  waiting.
- The phase-end retry budget is no longer spent by DECLINING to roll. The
  "never roll past the host" check sat below the attempt counter, so the first
  pass -- taken while the host was still on our round, where waiting is correct --
  burned a try. By the time the host moved ahead and rolling became legitimate,
  the budget was gone: "Round 1 looks spent but the host is on 1 -- waiting"
  followed by "will not leave the player phase at round 1 -- not retrying", with
  the guest pinned there while the host reached round 3. The check now runs
  before the counter, and the host's round is part of the retry state key, so the
  host advancing counts as a new situation deserving a fresh budget.
- Orders are never relayed for a unit this side does not own (MayBroadcastFor,
  applied to the 'skip' and 'done' broadcasts). Every place the MOD issues orders
  already filtered on ownership, but the ENGINE ends turns by itself and our
  hooks relayed those too -- so the host broadcast a 'skip' for a guest-owned
  unit and ended that player's turn for them. A local engine auto-skip is a local
  event, not a command: the owner sends the authoritative turn-end for their own
  unit. Authority follows the ACTOR.
- Support skills require an enemy within fighting range (14 tiles). Support ran
  before movement, so a unit that could not shoot -- because the enemy was far --
  spent its action self-buffing instead of closing, every round. 10 support
  skills to 2 attacks with almost no movement: a squad buffing itself across an
  empty map while never reaching the fight.

## MILESTONE (2026-08-01): first WON co-op mission

mission.alien_wildlife_clear_landing_zone, 25 rounds, host + guest:

- Primary objective **Completed** (logged on both machines), mission ended cleanly.
- Both sides finished on round 25 -- perfect round sync for the whole mission.
- ZERO deadlock recoveries (watchdog never fired), ZERO takeovers, ZERO
  unresolved orders, ZERO squad/element divergence, ZERO entry-gate failsafes,
  ZERO retry exhaustion. One "Movement failed!" and 8 sync pauses (self-healing).

This is the first mission that was WON rather than merely finished; the previous
best ended Primary=Failed with the squad overrun. What moved it:
patrol only when no enemy is known, moves gated on the pathfinder before
broadcast, support skills gated on an enemy within fighting range (units close
instead of buffing across an empty map), and the replayed-Element.Die()
suppression that stopped squads diverging.

STILL staged, not yet in this result: the 3-attacks-per-unit-per-round budget.
STILL untested: Steam P2P / real latency. Everything here is TCP loopback.

## v3.7.0 (deployed) -- mission-end authority, stance spam, one-mission mode

- **Only the host ends a mission.** The harness runs on BOTH machines, so the guest
  could decide the objectives were met a moment earlier and close the battle out
  from under the host. Blocked in two places: the harness's own finish path, and a
  prefix on TacticalManager.Finish so nothing guest-side can end a mission --
  including a player clicking Leave/Abort. AllPlayerUnitsDead and LoadingSavegame
  still pass: those are facts both machines reach independently, not commands.
- **The guest now FOLLOWS the host out of the battle** on "mend" instead of merely
  standing its cycle down. It was sitting in a battle the host had already left,
  leaving the two on different screens until the post-mission sync dragged it out.
  Both now reach the result screen together.
- **Stance toggles are no longer treated as support skills.** The picker took the
  first usable non-attack skill, which is a stance toggle, so units crouched and
  stood up instead of fighting: 18x active.stance.deploy and 13x active.stance.get_up
  against 14 carbine shots in one mission -- visible on screen as posturing rather
  than shooting, and each toggle burned an activation.
- **Up to 3 attacks per unit per round** (was 1). A successful attack marked the unit
  finished for the whole round, so a soldier with AP left simply stopped firing.
- **One-mission mode** (Dev/SelfTestLoopMissions, default OFF). Chaining into the next
  mission made every observation a moving target; replaying one small map is far
  easier to reason about.
- **Reaction fire during a relayed MOVE no longer rolls locally.** The skill and
  move-with-skill replays suppressed element damage, but the plain "move" case did
  not -- so a relayed walk past an enemy rolled its OWN overwatch/reaction fire on
  the receiving machine. Those rolls are per-machine, so the receiver killed squad
  members the originator still had ("cannot resolve el6 (sq7) -- actor=found,5 els")
  and squads diverged permanently, since a hard restore cannot revive a dead squad
  MEMBER. Reaction damage belongs to the machine that rolled it and arrives as
  "ehp"/"edie" like any other shot. This is the reaction-shot desync class the user
  predicted would bite.
- Turn-end logging now records the AP left unspent, to measure whether our units end
  their activation early (the "enemies act twice, we act once" observation).

## v3.7.1 -- the "it says desync but nothing looks wrong" class

The user reported desync warnings with NO visible in-game effect, followed by a
stall. Both traced to the same place: the checker was reporting differences that
cannot affect play, and the escalation from those false reports is what froze the
game.

- **The state hash no longer includes AI action points.** It hashed AP for EVERY
  actor, but AI AP drifts transiently on the guest while relayed orders are still
  landing, and that cannot affect play at all -- the guest never makes AI
  decisions, it replays the host's. So the hash cried desync over a meaningless
  value several times per round (it is compared at every faction switch). Each
  mismatch requests a correction and repeated "failures" escalate to a SYNC PAUSE,
  which freezes BOTH machines: a difference that changed nothing on screen produced
  a real stall. 16 sync pauses in the observed run. Position and HP are still
  hashed for everything; AP only for player units, where it is authoritative.
- **The host no longer publishes a hash while the board is still animating.** A unit
  mid-walk has not reached its tile, so the two boards legitimately differ for a
  moment. The guest already waited for a settled board; the host did not.
- **Stranded-unit settling now scans EVERY faction, not just the active one.** The
  guard existed but could only fire if the engine's cursor happened to be on the
  faction holding the stuck unit. Seen live: the guest's cursor on f1 while
  'player_squad.darby' was stranded in f2 -- downed on the guest, still alive in f1
  on the host. Each machine waited on a different faction; neither could finish, and
  the watchdog could not break it either, because no amount of state syncing un-downs
  a unit. The unit's OWN faction is now what is tested, not the active one.
- Root cause of that divergence is the reaction-fire fix in the previous entry: the
  guest rolled its own overwatch damage during a relayed move and downed a unit the
  host still had standing.
- MinApToMove 25 -> 8. Measured at turn end: units were ending their activation
  holding "ap left=20" (twice), 10, 5, 3 -- they could not afford a move and quit
  early while the enemy AI spent its whole activation. That is the "enemies move
  twice, our guys move once" the user saw. Affordability is the pathfinder's job
  (MoveIsPossible), not a guessed reserve.
- Exhaust() marked units against the in-contact cap (2) even when the applicable cap
  was the out-of-contact one (4), so "nowhere to go" did not stick.
- **Each side now re-asserts the turn-ends only IT can know about** (every 4s, own
  finished units, board settled). The host's snapshots carry player turn-done flags,
  so a lost turn-end from the HOST is eventually repaired on the guest -- but there
  was no channel the other way, so a lost GUEST turn-end left the host waiting on
  that unit forever. Seen live as host f1:left=1 / theirs=1 against guest f1:left=0:
  the guest had finished everything and the host was still waiting on one of the
  guest's units. Authority follows the ACTOR, so the repair is symmetric rather than
  host-special. Re-stating is idempotent and only touches FINISHED units, so it can
  never re-open or steal an activation.
- **Target selection no longer snipes the toughest unit on the map.** Toughness
  dominated the score (210*4 = 840 vs a distance penalty of ~21), so units locked
  onto the hardest target anywhere on the board and fired a 0-AP weapon at it from
  21 tiles -- three times a round, costing nothing, achieving nothing, and never
  closing the distance. Distance is now a real cost and a free weapon no longer
  outranks a real one.

## v3.7.2 -- friendly fire, and the ownership guard finally covers every order

- **Units no longer shoot their own downed squadmates.** LivingEnemies excluded only
  the player faction BY INDEX, but the engine moves a DOWNED player unit out of the
  player faction (into faction 2) -- so a casualty stopped looking like one of ours
  and became a valid target. The squad opened fire on its own downed squadmate,
  which is what the user saw. Ownership is the only reliable test; faction
  membership is not identity. This is the same lesson already recorded for the
  casualty scan, which had never been applied to the enemy scan.
- **MayBroadcastFor now guards 'skill' and 'move' too**, not just 'skip'/'done'. The
  host was relaying a skill for a guest-owned unit ("TAKEOVER: this side issued
  'skill' for a unit it does NOT own ('player_squad.pike')"). A partial ownership
  guard is barely better than none -- it just moves which order type leaks.
- **Control is never handed to a partner who is not in the battle yet.** The host can
  see the guest's units in its own roster from the moment the battle starts, so
  "they still have units to move" was true well before the guest had finished
  loading -- and control went to a player who could not possibly use it. Observed
  as host ctrl=them / mine=2 with no guest trace at all: the host sat holding two of
  its own units doing nothing while the enemy shot at it.
- The harness now NAMES the reason it is idle (after 10s holding units): cursor off
  the player phase, an actor still animating, or turn control with the partner.
  Every exit path used to return silently, so an idle side left no evidence of which
  condition stopped it -- the cause had to be guessed from missing log lines.
- **A damage packet for a unit already destroyed here is dropped, not escalated.** An
  actor with NO living elements is dead on this machine; more damage cannot change
  that and the sender's own death packet settles it. This was escalating to a hard
  repair and, from there, to a stop-the-world SYNC PAUSE -- observed live as
  "SYNC PAUSE started: squad mismatch at (10,12): sq0 is not on this side", i.e. the
  game freezing over a unit both machines agree is dead. The same reasoning was
  already applied to 'edie'; the 'ehp' path had not inherited it.

## v3.7.3 -- stop freezing over things a freeze cannot fix

- **A squad-roster mismatch no longer triggers a SYNC PAUSE.** It escalated to a hard
  resync, which stops the world on BOTH machines -- but a hard restore reports
  "0 revived" because it does not resurrect dead squad MEMBERS, only whole actors.
  So the game froze and the divergence survived: exactly the trap engine law 16
  names, a stop-the-world pause triggered by a condition the world has to resolve.
  Observed live as "SYNC PAUSE started: squad mismatch at (13,19)" with the board
  locked and nothing repaired. It now reports once per 15s and play continues --
  rosters differing costs a squaddie's worth of HP accounting; a freeze costs the
  session.
- **AI action points are no longer "corrected" by ordinary snapshots.** AI AP was
  already dropped from the state hash because it drifts while relayed orders land
  and cannot affect play (the guest never makes AI decisions). Repairing it here was
  the same value judged by the opposite standard, and it buried the corrections that
  matter under a stream of "AP drift 'enemy.alien_stinger': mine 4 -> host 100".
  A HARD restore still sets it -- that is the deliberate make-everything-identical
  hammer.
- **Ordinary snapshots no longer overwrite player-unit AP at all.** They already
  skipped AP for units owned by the RECEIVER, but still overwrote it for the
  PARTNER's units, racing the 'ap' follow-up the acting machine sends. The two sides
  then corrected the SAME unit in opposite directions and never converged:
  "'player_squad.tech': mine 0 -> host 100" followed by "mine 100 -> host 0".
  Authority follows the ACTOR -- the machine that spent the AP reports it and nobody
  else gets a vote. Hard restores still override everything.

## v3.7.4 -- nesting-safe replay suppression

- **ApplyingRemote and SuppressElementDamage are saved/restored, never blanket-cleared.**
  All three replay sites (skill, move, move-with-skill) set them true and reset them
  to FALSE in the finally. If a replay ever nests -- one handled message applying
  while another is still in flight -- the INNER finally cleared suppression while the
  OUTER replay was still running, and from that point the receiver applied its own
  damage rolls on top of the authoritative ehp. That double-damage is a standing
  candidate for the squad divergence that keeps recurring ("cannot resolve el8 (sq7)
  -- actor=found,7 els": the receiver had killed members the sender still had).
  Restoring the previous value makes nesting safe.

  NOTE: this is a REASONED candidate, not a proven fix -- the divergence had already
  survived the Element.Die() and reaction-fire suppression fixes, so it should not be
  called closed until several missions run clean.

## v3.7.5 -- the "squad divergence" was never a divergence

CORRECTION to v3.6.3 / v3.7.2 / v3.7.4, which each claimed to address a squad
roster divergence. The evidence says there wasn't one.

BOTH machines logged the IDENTICAL warning at the same moment -- same unit (RId=1),
same tile (12,19), same squaddies (sq7, sq8), and both reporting "actor=found,7 els".
If both sides hold 7 living elements then neither holds 9, so nothing had drifted
apart. Host 2 occurrences, guest 2 occurrences, mirror images.

The real mechanism is index semantics:
  - Element.GetElementIndex() is the PERMANENT squad slot (0..8).
  - Actor.GetElements() returns only the LIVING members (7 after two deaths).
So "el8" was never a valid index into that list on EITHER machine, and sq7/sq8 are
squaddies already dead on both sides. These were damage packets about the dead.

- Damage is no longer broadcast for an element that is not alive: a dead element
  still receives SetHitpoints during death processing, and the partner can never
  resolve it.
- A damage packet naming a squaddie that is not among the living is now DROPPED
  quietly as agreement, not escalated as a structural mismatch. The escalation is
  what produced the stop-the-world SYNC PAUSEs.

Lesson worth keeping: three fixes were aimed at a divergence that both logs, read
side by side, showed was not occurring. Checking whether BOTH machines report the
same thing distinguishes "they disagree" from "they agree and our checker is wrong",
and that check was not made until now.

## v3.7.6 -- the persistent hash gap: unmarked corpses

The residual mismatch was NOT drift. Measured across successive rounds:

  diff=185   diff=3   diff=512555   diff=512562   diff=1030330

512,558 is almost exactly ONE actor's contribution to the state hash, and the gap
GREW to two actors' worth. So one side was holding whole actors the other had
removed, accumulating one per death, and no snapshot could heal it -- the host has
no such actor to send a correction for.

Cause: actors die through the "ehp" path (the last element reaches 0 HP and Die()s),
which never marks the ACTOR as dying. ComputeStateHash skips dying actors, so a side
still holding an all-dead actor counted a whole extra unit forever.

- An actor with NO living elements is now marked dying. Deterministic from element
  state both machines already share, so it reads a fact rather than inventing one
  (law 4 forbids the latter, not the former).

Also note: "N revived" in the log is the RESYNC SUMMARY text ("0 revived"), not a
revive count. Grepping for "revived" produced false counts of 29 and 18 in this
session -- twice. Count "[AUTO] REVIVE" for actual revives.

### v3.7.6 VERIFIED

First kill of the run (enemies 10 -> 9 at round 6) fired the new settle twice --
once per machine, as expected since both mark the dead actor -- and NO actor-sized
hash gap followed. Only ordinary drift remained (diff=24, later 252).

  before fix: first kill -> diff=512555, second kill -> diff=1030330
  after fix:  first kill -> diff=24

Confirms the diagnosis: actors dying via the "ehp" path were never marked dying, so
the state hash counted them as live units indefinitely and the gap grew by one whole
actor per death.

## v3.7.7 -- focus fire

Targeting scored on toughness alone, so every unit independently picked "the
hardest thing I can reach" and four soldiers chipped four different enemies
instead of finishing one. Measured over a mission: 144 damage packets, 674 total
damage, average 4.7 per hit against 100-210 HP targets -- plenty of damage dealt,
almost no kills, enemies stuck at 9 for three rounds, and the mission then lost
with Primary=Failed.

Score now weights how badly a target is ALREADY hurt (missing HP * 6) above raw
toughness (* 2). A wounded target removes a shooter from the board sooner, which is
the only thing that actually reduces incoming fire.

Confirmed working in the same run: the guest now follows the host out of a finished
battle ("Host's mission ended; leaving the battle to follow them out") instead of
being stranded on a different screen.

## v3.7.8 -- each side re-asserts where its own units stand

The hash reported gaps of exactly 961 -- precisely the x-coordinate weight (31^2)
-- i.e. ONE unit ONE tile out, on a settled board, while every resync reported
"0 position(s)" corrected.

Cause: snapshots deliberately SKIP position for units owned by the RECEIVER
(authority follows the actor), so a partner holding a stale position for one of OUR
units is never corrected by them. The hash counts position; the repair layer had
decided not to. Third instance this session of the checker and the reconciler
judging the same value by different standards -- after AI action points and player
AP, both of which produced "desyncs" nobody was ever going to repair.

- The turn-end re-assert now also re-states 'land' (the existing "here is where this
  unit really is" message) for each owned unit. Idempotent, no new wire format, and
  it closes the one gap the ownership scoping leaves open.

Worth keeping in mind: hash-gap MAGNITUDE identifies the field.
  ~961 x N   -> position, N tiles in x
  ~31 x N    -> position, N tiles in y
  tens/hundreds -> hitpoints
  ~512,558   -> one whole actor counted on one side only

## v3.7.9 -- a snapshot must not finish a unit its owner still commands

REGRESSION FIX. Action balance had fallen from ~56/58 to H=33 G=9, with the guest
sitting at mine=0/theirs=0 for 147 consecutive trace samples and the board stalling
(watchdog fired 6 times). The guest was not being denied control -- its units were
being marked DONE out from under it.

Cause: MayBroadcastFor stopped the host RELAYING skip/done for a guest-owned unit,
but the host's SNAPSHOT still carried turn-done for every player unit, and the guest
applied it in the DONE direction. The host's own engine auto-skip therefore leaked
through the second channel and finished the guest's units for it. Blocking one
channel while leaving the other open is the same mistake as the AI-AP hash, one
layer down.

- ApplyCombatState now refuses to apply turn-done to a player unit the RECEIVER
  owns (hard restores still override). Authority follows the ACTOR: our own units'
  turn-ends are ours to declare.

Diagnostic note: the giveaway was that the guest's idle-reason logging never fired.
It only reports when units are PENDING, so "idle with no reason logged" meant the
units were already done -- which pointed at the turn-done flag rather than at turn
control, where I had first suspected the fault.

## v3.7.10 -- a FAILED mission also has to be ended

The watchdog reported an "unhandled deadlock class" it could not break, and it was
right that it could not: BOTH machines were byte identical -- round 14, faction 7,
f7:left=4, same pending list, same AP values. Nothing had diverged, so there was no
state to reconcile and no controller to hand over.

The board was frozen because the MISSION WAS ALREADY OVER (Primary=Failed) and
nothing closed it out. TryFinishCompletedMission accepted only
ObjectiveState.Completed, so a lost mission left the engine to stop activating units
while the battle scene stayed open, with 4 aliens pending forever.

- The mission now ends on a primary that is Completed OR Failed. A lost mission is
  decided just as definitively as a won one.

NOTE for the harness's combat work: that mission was NOT lost to casualties. Zero
revives, zero friendly units destroyed, six enemies killed, enemy count down 10 -> 4.
It failed an objective condition (most likely a turn limit) while winning the fight,
so the remaining gap is SPEED, not lethality or survivability.

METHOD NOTE: Select-String is case-insensitive BY DEFAULT, so -Pattern "REVIVE"
matches the "0 revived" resync summary. That produced three separate false revive
counts in one session (29, 18, 25 -- all actually zero) and one confident but wrong
causal story about heavy casualties. Derived counts need -CaseSensitive and an
anchored pattern before they are reported, let alone reasoned from.

## MILESTONE (2026-08-01, late): clean win on a build with the full fix set

Primary objective Completed on BOTH machines, mission ended cleanly via the new
Completed-or-Failed path, ~13 rounds.

  vs the earlier win:  25 rounds -> ~13, secondaries 2 -> 4, friendly losses 0
  sync: 0 pauses, 0 watchdog recoveries, 0 takeovers, 0 element errors
  combat: 0 revives needed, 0 units downed

Enemy count plateaued at 4 and it did not matter -- the primary is a zone clear,
not a kill-all, which is why SPEED and not lethality was the correct diagnosis of
the previous loss.

Carrying: focus fire, restored guest turn parity (the snapshot no longer finishes
units their owner still commands), dead-actor settling, position re-assert,
mission-ends-on-Failed, and the five freeze fixes.

## v3.7.11 -- REGRESSION REMOVED: the turn-end/position re-assert

The periodic re-assert added in v3.7.8/v3.7.9 was actively harmful and is disabled.

Symptoms it CAUSED:
  Order 'done' at (14,16) never resolved (board idle, RId=3) -- DESYNC
  Order 'land' at (14,16) never resolved (board idle, RId=3) -- DESYNC
  SYNC PAUSE started: repeated sync failures
  state hash gap growing to 16,162,493 (~30 actors)

Two distinct mistakes:

1. A restatement is a NO-OP by design, but the receiver files every relayed order
   and expects an observable change. So each re-assert was recorded as an order
   that never resolved -> resync requested -> repeated every 4s -> escalation to a
   stop-the-world pause. Exactly the freeze class this whole session was spent
   removing, reintroduced by the fix for it.

2. 'land' SETS a position. Re-sending it periodically yanked units back to whatever
   tile they occupied when the timer fired, dragging them backwards if they had
   moved since -- turning a one-tile (961) gap into a ~30-actor one.

RULE: restating a FACT is not the same as restating a COMMAND. "This unit is
finished" cannot be undone by repetition; "this unit is at (x,y)" can. Any future
restatement needs its own message type that the order tracker does not expect to
change anything.

NOTE: the problem it was written for (a partner holding a stale turn-done) had
never actually been observed to deadlock anything -- it was inferred from a trace
that turned out to be two machines simply on different rounds.

## v3.7.12 -- the lost partner turn-end, fixed safely this time

CORRECTION to the v3.7.11 note, which said the problem the re-assert targeted "had
never actually been observed to deadlock anything". It has now:

  guest r=5, mine=0 theirs=1     host r=8
  WATCHDOG Board unchanged for 45s -- recovering (attempt 1)   x3
  Catching up: host is on round 6, we are on 4 ... closing the phase

The guest sat waiting on a HOST-owned unit's turn-end that never arrived, three
rounds behind, with the watchdog firing repeatedly. So the re-assert was solving a
REAL problem -- its implementation was the fault, not its purpose.

Fixed without any periodic restatement: CatchUpToHostRound may now settle a
PARTNER-owned player unit, but only on the path that already requires the host to be
on a LATER round. If the host has moved past this round then every unit of it is
finished over there, so adopting that is reading the host's state rather than
fabricating it. One-shot, only while genuinely behind, and it never touches a unit
whose turn is ours to spend.

Contrast with what failed: the re-assert fired every 4s regardless of need, was
filed by the receiver as an order that never resolved, and (for 'land') repeated a
command whose repetition moves units.

## Release-candidate validation (same pinned mission, back to back)

Purpose: every serious bug in this session was INTERMITTENT, and single clean runs
repeatedly gave false confidence. One good mission proves very little.

RUN 1 -- WON. Primary objective Completed on both machines, ended cleanly at round 11.
  drift 0 | watchdog 0 | SYNC PAUSE 0 | never-resolved 0 | takeover 0
  element errors 0 | position-scale hash gaps 0 | partner-settle 0 (never needed)
  secondaries: 4 completed, 6 failed
  only small hitpoint drift, self-corrected

NOTE: partner-settle firing zero times means the v3.7.12 fix is UNEXERCISED, not
proven. The failure it guards against did not occur.

RUN 2 -- STALLED at round 23, NOT a win. This is why one run proves nothing.
  Found a genuine roster divergence (the new ROSTER DIFFERS diagnostic):
    "ROSTER DIFFERS 'player_squad.pike': 2 living element(s) here, host sent 1"
  Guest kept a squad member the host had killed. Confirmed by a PERSISTENT,
  uncorrected hash gap of exactly 100 across turns 9:7, 10:1, 10:7.

  That divergence then drove a CHAIN:
    unfixable difference -> resync requested -> "repeated sync failures"
    -> SYNC PAUSE (x2) -> watchdog pause -> stalled at round 23 with
    "Engine will not leave the player phase -- not retrying"

## v3.7.13 -- a repair that changed nothing must not escalate

ApplyHp compares only up to the SHORTER element list, so a roster-length difference
is structurally UNFIXABLE and every resync honestly reports "0 corrected".
Escalating on that applied a stop-the-world freeze to something no amount of syncing
can touch -- the same mistake as the AI-action-point hash, the dead-squaddie packets
and the squad-mismatch pause, now for the fourth time.

RequestResync now escalates only if the LAST correction actually changed something
(_lastResyncFixedSomething, set from the real fix counts). If it fixed nothing,
repeating it cannot fix it either: report once per 30s and keep playing.

NOTE: this stops the unfixable difference from FREEZING the game. It does not fix
the divergence itself -- see task #14 (a death applied on the host that never took
effect on the guest).

## v3.7.14 -- damage inside buildings was never broadcast (the release blocker)

ActorTileOf returned FALSE when an actor had no tile, and BOTH damage broadcasters
bail on that. Units inside CONTAINERS (buildings, vehicles) have no tile by design,
so every point of damage and EVERY DEATH to a contained unit was silently never
sent. The partner kept those squaddies alive permanently, unrepairable afterwards
because ApplyHp only compares up to the shorter element list.

That is the origin of the run-2 stall:
  ROSTER DIFFERS 'player_squad.pike': 2 living element(s) here, host sent 1
  -> permanent hash gap of exactly 100 -> repeated resyncs fixing nothing
  -> SYNC PAUSE x2 -> stalled at round 23

It also explains the intermittency: a clean mission is one where nobody fought from
inside a building.

Fix: a missing tile no longer silences the report. These messages already carry
stable identity (StampIds -> RId/CId, plus SqId); the tile is only a positional
fallback. Send (-1,-1) when there is none - ResolveOrderActor tries RId then CId
first, and GetTile(-1,-1) is null so the fallback cannot mis-match anyone.

Validation restarts from run 1 against this build, which is the first genuine
release candidate.

## PAUSED 2026-08-02 -- resume point

v3.7.14 is deployed to both installs and is the first genuine release candidate.

TO DO, in order:
  1. Validate across 3-5 consecutive missions (SelfTestLoopMissions stays OFF;
     relaunching replays the same pinned mission). One clean run proves nothing.
  2. Verify the container-damage fix specifically: ROSTER DIFFERS must stay at
     zero, especially in missions where units fight from inside buildings.
  3. Fix the position divergence: ReportLanding is gated on ShouldBroadcastOrder(),
     which suppresses the landing report when turn control is with the partner.
     A landing is a FACT, not a command. Root-caused, not yet applied.
  4. Measure the round drift: just read "persisted Ns" from the Round mismatch
     line. Seconds = normal lag. Tens of seconds = a real problem.
  5. Package a 3.x dev build for testers only once 1-4 are clean.

Non-blocking: splash-screen NRE from our own intro-skip patch (cosmetic, startup).
Out of scope: Steam P2P (the user is testing it).

## v3.7.15 -- a landing report is a fact, not a command

ReportLanding required ShouldBroadcastOrder(), which is false during a sync pause,
hold-fire, while the partner holds the action lock, while deferred orders are
pending, and WHEN TURN CONTROL IS WITH THE PARTNER. A walk that happened to FINISH
inside any of those windows -- most plausibly just as control passed -- never
reported where the unit came to rest, so the partner's copy kept whatever tile its
replay ended on. Nothing else corrects that: snapshots flow host->guest only and
skip receiver-owned units.

Symptom: hash gaps that are exact multiples of 961 (2883 = three tiles of x) on a
settled board, intermittent, never repaired.

Those gates exist to stop a side ISSUING NEW ACTIONS. Reporting where our own unit
stopped is not an action, and suppressing it destroys information no other channel
carries. Sync pause is still honoured -- during one the board is deliberately frozen.

Note the symmetry with v3.7.11: that regression treated a COMMAND ('land' sets a
position) as a fact safe to repeat. This bug treated a FACT as a command needing
permission. Same confusion, opposite direction.

v3.7.15 is now the release candidate. Validation set restarts against it.

## v3.7.16 -- damage that lands AFTER the replay call returns

ROOT CAUSE of the roster divergence, found by tagging the local-damage trace with
stable actor identity.

SuppressElementDamage was scoped to the synchronous skill.Use() call. A projectile
or area weapon resolves its damage ASYNCHRONOUSLY, after Use() returns and after the
finally block has cleared the flag:

  [SYNC] Applied remote skill active.fire_acid_spitter at (12,19)
  [LOCALDMG:G] darby sq7  -8 -> 0 (suppressed=False)
  [LOCALDMG:G] darby sq8  -8 -> 0 (suppressed=False)

So the guest rolled its OWN splash targets for a shot the host had already resolved.
Host and guest then killed DIFFERENT squad members of the same unit -- host
sq5/sq22/sq0 against guest sq8/sq23/sq6/sq24/sq21, ZERO overlap. That is the roster
divergence: both directions, both player and enemy units, intermittent because only
delayed-damage weapons trigger it.

Fix: suppression now has a 1.2s TAIL after a replayed action, so late-resolving
damage is still caught. Authoritative HP still arrives via ehp/edie, which do not
run under the flag.

TRADEOFF (stated, not hidden): the tail suppresses damage globally, so this
machine's own attack resolving inside the window would be wrongly suppressed --
the mirror-image bug. The action lock serialises actions and the harness paces
~1-2s apart, so 1.2s should not reach the next action. If the logs show our own
damage being suppressed, LOWER the number; do not abandon the tail.

METHOD NOTE: the trace only became usable once it carried rid/cid. Before that,
"template name + squaddie id" compared DIFFERENT actors (every alien_stinger is a
one-member squad, so all of them are sq0) and produced a confident wrong conclusion.
