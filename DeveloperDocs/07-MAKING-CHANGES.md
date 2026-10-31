# Making a change

1. **Find the subsystem** in the code map and read its opening comment.
2. **Reproduce it on the rigs** (06-TESTING.md). If it involves loading, joining or
   handover, use `-LatencyMs`. If it is rare, add a scenario that forces it.
3. **Probe if you cannot explain it** from the code and logs.
4. **Fix the cause.** Check the change against [03-DESIGN-RULES.md](03-DESIGN-RULES.md) and [04-ENGINE-LAWS.md](04-ENGINE-LAWS.md).
5. **Prove it:** a run where the case happens and both logs show it behaving.
6. **Write the comment** (what broke, the evidence, why this shape), and a
   CHANGELOG line under the next version.

New Harmony patches: put them in a patch class with one concern, add the class to
`MelonEntry.cs`'s list, and check the startup `[PATCHES]` line says it applied.

New settings: `MelonEntry.cs`. Player-facing ones in `[MenaceCoop_*]` with a clear
description; test-only ones in `[MenaceCoop_Dev]`, defaulting off, and under
`COOP_DEVTOOLS` if they must never ship.

New messages: a short `T` string, handled in `Replication.Apply`. If a client sends
it and only the host should see it, add it to `Relay.HostOnly`.
