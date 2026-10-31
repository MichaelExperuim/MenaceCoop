# Update resilience -- to implement (user request, 2026-10-16)

Every MENACE update (0.7.11, 0.7.15, 0.7.17) broke our mods harder than it had to:
one renamed engine member took whole groups of hooks down with it. Implement the
three items below, then delete this file.

1. **Small patch classes, one feature each.** A Harmony patch class whose target
   is missing fails as a unit, taking its unrelated neighbours with it (0.7.15: a
   mission-launch rename disabled co-op's battle replication hooks). Split big
   patch classes so one rename can only disable the feature it belongs to.

2. **Resolve fragile targets by name at runtime, and fail soft.** Anything
   compiler-generated (`_X_d__NN` coroutines, `_b__N_M` lambdas, DisplayClass)
   is renumbered on every build -- find it by prefix with `[HarmonyTargetMethod]`
   instead of naming it. Exoskeleton and Helicopter already do this for the
   `StrategyState.ProcessSaveState` coroutine; it is why they survived 0.7.17.
   A target that cannot be found: log it and skip that feature, never throw out
   of mod init.

3. **Startup self-check.** After patching, verify every intended patch attached
   (co-op's `_Tools/Shared/PatchReport.cs` is the model). If a required one did
   not, disable the dependent feature and tell the player in-game
   ("<feature> disabled: game version not supported yet") instead of running
   half-patched.

## When the game updates

Before relinking any clone, keep the old index, then run:

```bash
dotnet _Tools/InteropDump/bin/Release/net6.0/InteropDump.dll 'C:\Games\steamapps\common\Menace\MelonLoader\Il2CppAssemblies' _Tools/interop-index-<new>.txt
python -I _Tools/Api-Impact.py _Tools/interop-index-<old>.txt _Tools/interop-index-<new>.txt <new>
```

Read `_Tools/api-impact-<new>.txt` for your mod's section first (a lead, not
proof -- confirm with a compile), and `_Tools/interop-diff-<new>.txt` for the full
list. The current pair is 0.7.15 -> 0.7.17. A clean compile still does not catch
behaviour changes: launch the mod and read its log before calling it safe.
