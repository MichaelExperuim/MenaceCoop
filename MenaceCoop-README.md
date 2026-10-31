# MenaceCoop v5.3.0 (MelonLoader)

Co-op for 2 to 4 players in MENACE (requires game v0.7.17 or later). One player hosts,
the others join; the squads are split between you and you fight each mission together.

**Make sure everyone is in the lobby before starting a mission.** See Known limitations.

## What's new in v5.3.0
Requires MENACE v0.7.17. Everyone in the session must update.
- Fixed: a squad that broke and fled could hang the turn for everyone.
- Fixed: a host crash when a unit died with no attacker.
- Fixed: joining players' kills now count after the mission.
- Fixed: status effects, vehicle module damage and buildings destroyed with a squad
  inside now show the same for everyone.
- Fixed: the workshop now appears for joining players after the workshop event.
- Fixed: enemy positions, garrisons and arriving trucks drifting on joining players'
  screens.

## What's new in v5.2.0
Requires MENACE v0.7.17. Everyone in the session must update.
- Fixed: co-op on game v0.7.17.
- Fixed: a vehicle turned at the end of its own move kept its old facing on the other
  players' screens.
- Fixed: a player's game could end another player's unit's turn at the start of a
  round, so that unit's next move was refused on that machine.

## What's new in v5.1.0
Everyone in the session must update.
- Added (EXPERIMENTAL): Steam lobbies. **Host (Steam)** opens a lobby; friends join by invite or from the
  Steam friends list, and the connection is made automatically. Typing Steam IDs is no
  longer needed (it still works).
- Added: **INVITE FRIENDS (Steam)** button for the host, and a **JOIN** button in the panel
  for each friend who is hosting.
- Fixed: a player who left a Steam session and joined again while the host stayed up could
  be left half-connected.
- Changed: a message Steam refuses to send is now reported in the log instead of being
  dropped silently.

Steam lobbies are new and experimental. If a lobby does not connect you, use the Steam ID
fallback or direct IP (see Connecting), and please send both players'
`MelonLoader\Latest.log`.

## What's new in v5.0.1
Everyone in the session must update.
- Fixed: a mission-objective object (eggs, supplies, landing ships) destroyed by the host
  could disappear without the kill being counted, so the mission could not be completed.
- Fixed: when the host pressed PASS TURN TO PARTNER without ending a unit, a player who
  had just ended a unit's turn could be left on the hourglass.
- Changed: automatic desync repair no longer moves a player's own units back or brings
  dead units back. The full restore now only runs when a player presses FIX DESYNC.

## What's new in v5.0.0
- Added: up to 4 players (host plus up to three others). Squads are split evenly.
- Added: a player who drops or crashes mid-battle can rejoin. While they are away their
  units are played automatically (needs MenaceAutoCombat) and are handed back on return.
  Everyone is paused while they load back in and resumes together.
- Changed: the host decides the fog of war, so every player sees the same units.
- Fixed: mission loot is the host's on every result screen.
- Fixed: a fleeing enemy could escape on one machine only.
- Fixed: destroyed vehicles' wrecks and destroyed buildings were missing after a rejoin.
- Fixed: hosting with no one connected stopped enemies from moving.

v4.0.0 was the first MelonLoader release. v3.14.0 was the last BepInEx build; the two
cannot be installed together, and settings do not carry over from BepInEx.

---

## Install

1. Both players need **MelonLoader** (the MENACE Modkit / Menace Mod Manager installs it).
2. Put `MenaceCoop.dll` directly in the game's `Mods\` folder -- `Mods\MenaceCoop.dll`.
   Not in a subfolder: MelonLoader does not load a DLL one folder deep.
3. Both players must run the **same MenaceCoop version** and the same content mods. The
   mod compares both players' mod lists when you connect and tells you what differs.
4. Co-op does not skip the intro video. To skip it, install **MenaceIntroSkip 2.3.0 or
   newer** -- older versions stand down whenever co-op is installed, so with them the
   intro still plays.

Uninstall: delete `Mods\MenaceCoop.dll`. To reset settings, delete the `[MenaceCoop]`,
`[MenaceCoop_Network]`, `[MenaceCoop_UI]` and `[MenaceCoop_Dev]` sections from
`UserData\MelonPreferences.cfg` (that file is shared with your other mods -- do not
delete the whole file).

## Connecting

Use the **co-op panel** in game:

- **Over Steam:** the host presses **Host (Steam)**. That opens a Steam lobby (friends
  only). Players then get in any of these ways, and are connected automatically:
  - the host presses **INVITE FRIENDS (Steam)** (needs the Steam overlay), or invites from
    the Steam friends list, and the friend accepts -- this also starts the game for a
    friend who does not have it running;
  - the friend picks **Join Game** on the host in their Steam friends list;
  - the friend opens the co-op panel and presses **JOIN <host's name> (Steam)**, or
    **Join (Steam)** with **Partner ID** left empty.

  An invite is not followed while you are already connected to a session: press
  **Disconnect** first.
- **Over Steam, by ID (fallback):** the panel shows **Your Steam ID** (hover to reveal).
  The host types the other players' IDs into **Partner ID** (comma separated) and presses
  **Host (Steam)**; each other player types the host's ID and presses **Join (Steam)**.
- **Direct IP / VPN (e.g. Tailscale):** both players need the same **Password**
  (required). The **host** types a password of their choice into **Password** -- or
  presses **NEW CODE** for a random one (pressing **Host (direct IP)** with the field
  empty also makes one) -- then presses **COPY** and pastes it to the partner (or
  **SHOW** and reads it out). The **guest** enters the **host's** password -- case,
  spaces and dashes do not matter. The host presses **Host (direct IP)**; the guest enters the host's address in
  **Host IP** (and **Port**, default 27555, which must be reachable on the host) and
  presses **Join (direct IP)**. The password is never sent over the network, and the
  connection is encrypted. Anyone without it is refused. If you type your own
  instead, do not use a password you use anywhere else.
- **Disconnect** ends the session.

The same values are saved in `UserData\MelonPreferences.cfg`, section
`[MenaceCoop_Network]` (`Mode`, `PartnerSteamId`, `HostAddress`, `Port`,
`SessionPassword`). The password is stored there in plain text, on your own machine.

The host runs the campaign. The guest is brought into the same battle from the host's
save automatically.

---

## Features

### Playing together
- **Squads are split between you.** You command your squads, your partner commands
  theirs; neither can move or end the turn of the other's units.
- **Choosing who commands which squad (host).** During mission planning, open the
  co-op panel and scroll to **SQUAD ASSIGNMENT** at the bottom. Each deployed squad is
  listed with a button in its commander's colour; **click it to hand that squad to the
  next player**. Keep clicking to cycle through the players. Everyone else sees the
  choice live. A player left with no squad is shown as SPECTATOR. The split is fixed
  when the battle starts.
- **Only the host plans the mission.** Squad sizes, loadouts and deployment are set on
  the host's planning screen; everyone else sees those changes live but cannot make
  them. Tell the host what you want in your squads.
- **HOST / GUEST labels** above units show whose is whose (toggle in the co-op panel).
- **Your partner's cursor** shows on your screen; their planned move line can be shown
  too. Each can be turned off in the `[MenaceCoop_UI]` section.

### Taking turns
- One player acts at a time; when you have nothing left to move, your partner gets the
  turn. A banner shows whose turn it is.
- While the enemy is acting, nobody can give orders: the guest sees "Hold on -- the
  enemy is acting" until it finishes.
- **PASS TURN TO PARTNER** hands over whenever you like.
- No timer decides whose turn it is -- take as long as you need.

### Auto combat
- Needs the separate **MenaceAutoCombat** mod (both players). With it installed,
  **"Auto combat (your squads)"** in the co-op panel turns it on and off; it only ever
  commands your own units and steps back the moment you touch anything.
- Without it, that button is greyed out and tells you the mod is needed.

### Staying in sync
- Positions, hitpoints and turn state are checked continuously and corrected from the
  host without interrupting play.
- **FIX DESYNC** reloads the guest into the host's current battle (about 20 seconds).
- **Rejoining:** a guest who disconnects mid-battle can reconnect and is brought back in.
- **SAVE BATTLE STATE / RESUME SAVED BATTLE** (host) saves a battle in progress and
  resumes it later with the guest.
- Ship support (strikes, supply drops, turrets, airdropped troops) is shared from one
  pool and appears for both players.
- Skybot drones: deploying and commanding them is synchronised for both players.

---

## Known limitations

- **Make sure everyone is in the lobby before starting a mission.** Joining a battle
  already in progress, or rejoining one, can desync once enemies have moved into
  buildings. Not fixed yet.
- After joining or rejoining mid-battle, **dead enemies' bodies may not show** on the
  joining player's screen (cosmetic -- they are dead everywhere).
- A **FIX DESYNC or rejoin while delayed ship support is still inbound** (turrets,
  airdrops): the rebuilt battle does not include it. Rare.
- Small corrections to an enemy's facing, stance or morale are logged a few times per
  round. They are not visible in play.
- Most testing has been two copies of the game on one machine. Play over real network
  latency has had less coverage; reports are welcome.

## Reporting a problem

Attach **`MelonLoader\Latest.log` from BOTH players' games** -- the two logs side by
side are the most useful thing you can send. Note which skill or ship ability was used
if the problem followed one.
