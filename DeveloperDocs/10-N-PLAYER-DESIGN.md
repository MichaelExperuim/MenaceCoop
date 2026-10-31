# MenaceCoop — architecture plan for N players

Written by the **review chat**. Forward-looking design, not bug work. Proposals
only — the implementation chat owns the source.

**Objectives this serves** (user, 2026-09-27): MelonLoader (done — ships as v4.0.0), and
**more than two players**. Planned now, deliberately,
because every week of desync work bakes more two-player assumptions in.

---

## 1. The finding that decides everything

The two-player assumption is **not** spread thinly across 208 `CoopMode.Host/Guest`
branches as a style problem. It is encoded in the type system, as **booleans in
three places where identities are needed**:

| Primitive | Today | File |
|---|---|---|
| Who owns this unit? | `IsLocal(Actor) : bool` — a membership test against `_localUnits` | `Ownership.cs:41` |
| Who may act right now? | `private static bool _hostControls = true;` | `TurnPhases.cs:34` |
| Who sent this message? | **nothing — `CoopMsg` has no sender field at all** | `CoopNet.cs` |

The third is the important one. There is no sender id because there has never been
any ambiguity: a message came from "the other side", because there is exactly one
other side. `CoopNet.Send` likewise takes no destination — it means "tell the
partner".

**Why this is good news:** the shape of the migration is now clear and mechanical.
It is not "add a loop over players". It is **"introduce `PlayerId` as a first-class
type, then let the compiler find the 208 branches"**. Most of those branches are
`if (host) … else …` over what is really "is this mine", and they collapse once
ownership answers with an id instead of a bool.

**Why it is also urgent:** every new subsystem written against `IsLocal` and
`_hostControls` adds to that 208. The cost of this change grows monotonically with
every week of feature work.

---

## 2. The convergence: N players forces the fix you already need

This is the central argument of this document.

N players requires a **topology** decision. There are two:

- **Mesh** — every client connects to every other. N(N−1)/2 links, no natural
  ordering, every pair can disagree independently. For a simulation that already
  struggles to keep two copies equal, this is the wrong answer at any N.
- **Star** — the host is the hub; clients talk only to the host. N−1 links, and
  **the host becomes the sequencer**: every message in the session passes through
  one process, which assigns it a single total order that every client sees
  identically.

Star is obviously right here, and the consequence is the interesting part:

> **Host-as-sequencer is the same change as host authority.** The thing that makes
> N players tractable is the thing that fixes the two-player desync architecture.

So Proposal 4 of `DESIGN-REVIEW.md` — *host runs the enemy AI, clients stop
deciding* — stops being a separate large project competing for time. It is a
**prerequisite for N players that you were going to have to build anyway.** Do them
as one project.

The corollary is the sequencing rule for the whole roadmap:

> **Never add a player before authority is inverted.** Mirror-input lockstep with N
> independent simulations multiplies the divergence surface by N. Host-authoritative
> with N view-only clients adds *no* new divergence surface per player, because no
> client decides anything.

That is the difference between 4-player being a hard project and a mostly
mechanical one.

---

## 3. What the engine does and does not constrain

Checked, because it bounds the design:

- **MENACE has no multiplayer and no concept of a second player.** Every "player"
  here is a mod-side ownership set over the engine's single player faction. So
  **the engine imposes no limit on N.** N is bounded by your design and by
  divergence surface, not by the game.
- **Detection/fog belongs to the player faction**, not to a player. All co-op
  players therefore share one vision state — simpler, and no per-player fog to
  reconcile. (Keep it that way; per-player fog would be a large new divergence
  surface for little gain.)
- **The engine alternates one player activation with one AI activation**
  (`CLAUDE.md` engine law). Control order *among* players is entirely mod-decided,
  so N-way turn passing is a scheduling choice, not an engine fight.
- **Casualties leave `GetActors()` for `GetDeadActors()`**, and downed units move to
  an orphan faction — so identity must not be derived from faction or list index.
  Already solved (`Ownership.IsKnownPlayerUnit`, coop ids). That solution
  generalises to N unchanged, which is the single biggest piece of luck in this
  codebase.

---

## 4. The migration, in the order that keeps a working build at every step

The critical insight about sequencing:

> **Introduce the identity primitives while N is still 2.** At N=2 the change is a
> behaviour-preserving refactor — `PlayerId` with two values, where
> `IsLocal(a)` becomes `OwnerOf(a) == Me` and `IsMyTurn` becomes `_holder == Me`.
> It can be validated against the existing harness and the existing runs, because
> nothing about the game changes.

If instead you wait until you want four players, you change the primitives, the
topology, and the authority model simultaneously, with no working baseline to
compare against and no way to tell which of the three broke a run. That is the
failure mode to design around.

### Phase 0 — `PlayerId` at N=2 (behaviour-preserving)

**Revised after review by the implementation chat, who corrected five points.**
Their corrections are incorporated below and materially improve the phase; the
notes marked *(review)* are this chat's response to them.

**Timing is fixed by the user:** MelonLoader ships first as v4.0.0; Phase 0 is the
first v5 work, after v4 ships and after the 2×2 experiment.

1. **`PlayerId` keyed on a stable per-install identity, not join order.** Identity
   must survive a disconnect *and* a session, because `GuestReload` and
   `BattleSave.cs` (`coop_battle.coopbattle`) restore ownership across both — a
   player who drops and returns must get their own units back, and join order would
   hand them to whoever reconnects first. Host maps identity → seat and persists
   that map in the co-op sidecar of the save.

   *(review)* Steam ID is the right key for the Steam path, but **the direct-IP /
   Tailscale path has no Steam ID**, and the mod documents that path as the
   supported fallback. So the key needs to be an identity the mod owns: a stable
   GUID generated per install and persisted in `MelonPreferences`, with the Steam ID
   used as the key when available and the GUID otherwise. Otherwise direct-IP
   players cannot rejoin and keep their units — a regression the Steam-only design
   would not reveal in testing.

2. **Classify every `IsLocal` call site; do not rewrite them mechanically.**
   `IsLocal` currently carries four different meanings at once:
   - *ownership* — is this unit mine to command;
   - *authority* — the owner-scoping rule in the correction path (never move a unit
     this machine commands on the host's say-so) and the hit-ownership rule (the
     shooter's machine decides);
   - *presentation* — the selection gate (`Actor.IsSelectableByPlayer` postfix),
     labels, cursors;
   - *provenance* — `DeployedByLocal` for minions.

   At N=2 "the shooter's machine decides" and "the host decides" coincide for
   roughly half the cases. **At N=3 they split apart**, and any call site that
   conflated them silently changes meaning. `OwnerOf(Actor)` is still the right
   primitive, but the classification is most of Phase 0's real cost.

   *(review)* **This classification IS the authority table** from
   `DESIGN-REVIEW.md` Proposal 1, which is already accepted and in progress. They
   are the same work product, approached from two directions: the table asks "who
   decides X", the classification asks "which sense of mine does this call site
   mean". Doing them as one artefact rather than two saves the duplicate pass, and
   the table gains the thing it was missing — a concrete call-site inventory to be
   complete against.

3. **`_hostControls` needs a deterministic next-holder rule, not just an id.** It
   rides on the engine's one-player-then-one-AI alternation *plus* a per-round
   "Player 1/2 acts first" alternation. At N players that becomes a rotation, and
   the deadlock watchdog's "does the holder have units, else who does" needs a
   next-holder rule every client computes identically — otherwise two clients
   disagree about who holds control.

   *(review)* There is a simpler answer than replicating seat order and requiring
   identical computation: **do not let clients compute the next holder at all.** The
   host is the sequencer; it assigns the holder and broadcasts it, exactly as `ctrl`
   already works today, with `passreq` as the client's request. Clients cannot
   disagree about something they never decide. Seat order then only has to exist on
   the host, which removes an entire class of N-player divergence rather than
   managing it. This is the same principle as Phase 1 applied to turn control, and
   it is worth adopting in Phase 0 because it *reduces* what Phase 0 has to build.

4. **Per-client session state — belongs in Phase 0.** The catch-up journal, the
   ready / hold-fire gate ("partner is loading"), `SyncPause`, and the hash/snapshot
   exchange all assume one peer. The host must track readiness and last-verified
   state **per client**, and a desync verdict must name **which** client diverged.
   At N=2 these are all one-element maps, which is precisely why this is cheap now
   and expensive later — and it is where asserting on `From` earns its keep.

   *(review)* This also raises the value of per-subsystem hashes
   (`DESIGN-REVIEW.md` Proposal 6): at N>2, "the board diverged" without naming the
   client *or* the subsystem is close to useless as a diagnostic.

5. **Owner semantics needing an id, for the grep:** hostages / VIPs, drone minions
   (`DeployedByLocal`), and the ship-support pool (`BUGS.md` #3a).

6. `CoopMsg` gains **`From`** (sender) and **`Scope`** (broadcast / to-host /
   to-one-client). At N=2 both are redundant — set and assert on them anyway, so the
   invariant is live and tested long before it is load-bearing.

7. `CoopNet.Send(msg)` → `Send(msg, to)` plus a `Broadcast` helper; `SendFile` takes
   a destination. Identical meaning at N=2.

**Exit test:** the whole existing run suite behaves identically, and
corrections/round (the new standing metric) is unchanged. If it is, Phase 0 was free
and the hard part of N players is already done.

### Phase 1 — authority inversion (`DESIGN-REVIEW.md` Proposal 4)

Host runs the enemy activations and relays them as scripted orders; clients'
agents suppressed. Latency already bounded and small (5–13 AI activations per
round in r63; ~0.4 s/round added at 50 ms RTT, against rounds taking tens of
seconds — see `DESIGN-REVIEW.md` §4). Still at N=2, still testable against the
existing harness.

**This is the phase that actually makes N players safe**, and it pays for itself at
N=2 by deleting the "both machines decided the enemy's action" bug class.

### Phase 2 — star topology and the host as sequencer

Transport goes from one link to N−1. `_client` → a client table; `_partnerSteamId`
→ per-client Steam ids. Every client-originated message is sent to the host, which
stamps it with a session-wide sequence number and fans it out. Clients apply strictly
in sequence order.

This is also where `Seq` (`DESIGN-REVIEW.md` Proposal 5b) stops being a diagnostics
nicety and becomes structural: with a hub, the sequence number *is* the total order.

### Phase 3 — N-way gameplay rules

Only now do the player-facing decisions arrive, and they are design questions
rather than plumbing:

- **Squad split.** "Half yours, half theirs" becomes N shares. Remainder handling
  and whether the host chooses or it is automatic.
- **Turn order among N.** Free-for-all (anyone with units may act) is closest to
  today's behaviour and least latency-bound; strict round-robin is more legible.
  Recommend free-for-all with the existing "holder has nothing left → pass"
  mechanism generalised, since that rule was already learned from players (never let
  a timer decide whose turn it is).
- **Per-player resources.** See §5 — this is where the roadmap and player demand
  meet.
- **Join/leave mid-session.** Today a rejoin runs a full guest reload with the host
  frozen. At N>2 that freezes *everyone* for one person's reload. Must become
  per-client and non-blocking before N>2 ships.

---

## 5. Two things N players makes easier, not harder

**Per-player ships / OCI / supply.** The most-liked feature request on the Nexus
page (HellFire0114, 2 Aug: *"we both own our own ships and OCIs and could command
our own forces rather than splitting"*) is currently awkward because there is one
pool and no identity to attach a second one to. With `PlayerId` as a first-class
type, a per-player pool is a dictionary keyed by owner rather than a special case.
`BUGS.md` #3a ("one ship-support pool") is the two-player shadow of this same
design gap.

**PvP — RAISED AND CLOSED. Out of scope (user, 2026-09-27):** it needs a significant
overhaul and is properly a separate project. Not to be raised again.

Recorded only so the reasoning is not rediscovered: the suggestion was to retire
MenacePvP as a separate tree and make it a mode of this one, on the grounds that
once ownership answers with a `PlayerId` and authority sits with the host,
"commander vs swarm" is an assignment of factions to player ids. The user's read is
that the overhaul cost outweighs that, which is a scope judgement, not a technical
disagreement.

**Nothing in this plan needs to change for it.** Phase 0 is motivated entirely by
N-player co-op; ownership-by-id happens to leave PvP unification possible later at no
additional cost, so there is no decision to make now and no work to do either way.

---

## 6. Risks to design for now, cheaply

| Risk | Why it bites at N>2 | Cheap mitigation now |
|---|---|---|
| **Save fan-out** | `SaveTransfer` base64-chunks the save over JSON to one partner. At N=4 that is 3× out of the host on join and after every battle | Make `SendFile` take a destination in Phase 0; measure actual save size before N>2 |
| **Reload freezes everyone** | `GuestReload` freezes the host while one guest rebuilds (~18 s) | Make it per-client in Phase 2; do not let it stay host-global |
| **Version/mod handshake** | `hello` compares one partner's version and mod list | Make the host the arbiter over a *set* of clients; refuse the odd one out rather than the whole session |
| **Panel and labels** | HOST/GUEST labels and "PLAYER 2 IS ACTING" are two-valued strings | Drive them from `PlayerId` in Phase 0, with colour per player |
| **Ship-support pool** | One pool, two claimants already (#3a) | Key it by owner in Phase 0 even while there is one player per side |

---

## 7. What not to do

- **Do not announce a player count publicly before Phase 1 lands.** The Nexus reply
  of 25 Sep already floats "possibly 4 but maybe more". If Phase 1 slips, that
  becomes the next "about to release a patch for all of those issues" — the
  credibility pattern flagged in `DESIGN-REVIEW.md` §6.2.
- **Do not do mesh networking.** At any N.
- **Do not build per-player fog.** Large new divergence surface, little gain.
- **Do not start Phase 3 before Phase 1.** N independent simulations is the one
  version of this that cannot be made to work.
- **Do not refactor `Replication.cs` into per-opcode handlers as a separate
  project.** It is worth doing (5,272 lines, 57-way switch) but it should fall out
  of Phase 2, when opcodes need routing and scope anyway. Doing it twice is waste.

---

## 8. Recommended immediate step

**Phase 0, and nothing else.** It is behaviour-preserving, verifiable with the
harness that already exists, and it is the only part of this plan whose cost rises
every week it is deferred. It does not commit you to N players, to PvP unification,
or to a timeline — it just stops the codebase from making N players harder.

**The window is now fixed by the user:** MelonLoader ships first as **v4.0.0**, and
Phase 0 is the first **v5** work, after v4 ships and after the 2×2. That ordering is
right for two independent reasons — the 2×2 needs today's code to interpret its
numbers, and v4's release notes describe a loader migration rather than a loader
migration plus a refactor.
