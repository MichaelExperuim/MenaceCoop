# Design rules

These are decisions with history behind them. Breaking one has always come back as
a bug.

1. **Wait on state, never on time.** Anything waiting for another machine waits for
   a signal from it: an ack, a named save finishing its load, a failure message, a
   disconnect, or a person pressing a button. A timer may only *report* a local
   failure; it never "goes ahead anyway". Real network latency outlasts every
   timeout; a 30 s "launch anyway" crashed guests at mission start on real
   connections (BUGS #100). Retries of an idempotent message are fine.
2. **Never let a timer decide whose turn it is.** A 90 s idle failsafe kept passing
   control from players who were thinking. Control passes when a player has nothing
   left to move.
3. **Prevent, don't repair.** A hard restore cannot revive a dead squad member, so
   anything that kills on one machine only must be stopped, not corrected later.
4. **The input gate and the broadcast gate must agree.** `LockBlockSkill` (does the
   action run here?) and `ShouldBroadcastOrder` (is it sent?) are separate patches on
   the same call. If one blocks and the other does not, an action happens on one
   machine only -- a ghost move, which is a desync.
5. **Per seat, never "the partner".** With 3-4 players, "the first client said X"
   is not "every client said X" (acks, ready, sync).
6. **Identity is never derived from position.** Unit ids come from the engine's
   ordering (deployment slot, arrival ordinal), because two machines that disagree
   about a tile would otherwise disagree about *which unit* every order is for.
7. **Every patch class logs whether it applied** (`PatchReport`), and one failing
   target must not take others down: one concern per patch class.
8. **Probe before you fix.** The decompile has no method bodies, so behaviour is
   established by logging it at runtime, never by inference (06-TESTING.md §4).
9. **Dev tools never ship.** Anything only for testing sits under `COOP_DEVTOOLS`
   and carries the `[DEVTOOLS]` marker in its strings; the release script refuses a
   DLL that contains it. Diagnostic probes that do ship are off by default
   (`ProbeBugs`).
10. **Comment the why, with the evidence.** The code's comments record what broke,
    the log line or run that showed it, and why the fix is shaped as it is. Keep
    doing that; it is how the next person avoids re-breaking it.
