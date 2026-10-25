// THE ONE LINE THAT LETS THE AUTOCOMBAT BRAIN BIND TO CO-OP'S SERVICES.
//
// The files compiled in from _AutoCombatModDev sit in `namespace
// MenaceAutoCombat` and call co-op's services by bare name -- Ownership,
// CoopNet, CoopRunner, Replication, Thinking, Plugin. In their own mod those
// names resolve to the single-player stand-ins in SinglePlayer.cs, which is
// excluded from this build; here this global using resolves them to the real
// MenaceCoop types instead. That is the whole point: the brain then asks co-op's
// genuine ownership whether a unit is this player's, rather than being told yes.
//
// WHERE BOTH NAMESPACES DECLARE THE SAME NAME -- SkillPolicy, Hazards -- the
// enclosing namespace wins, so a MenaceAutoCombat file keeps using its own and
// a MenaceCoop file keeps using co-op's. Nothing is ambiguous and nothing had
// to be renamed.
global using MenaceCoop;
